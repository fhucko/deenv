using DeEnv.Kernel;

// ── Entry point: the kernel host ─────────────────────────────────────────────────
//
// The program IS the kernel host. It reads the instance registry (kernel.json) and hosts every
// instance in it at once — all behind ONE shared app port + ONE shared asset port, each instance
// addressed by PATH (`/apps/<name>`) with its own sovereign data — then blocks until shutdown. There
// are NO run modes and no --app switch: kernel.json is the single source of truth for what runs (the
// two kernel-level ports + the per-instance entries). A single instance is just a one-entry registry.
//
// The M4 schema tools (designing a schema, publishing it via SchemaBridge) are no longer CLI
// modes. SchemaBridge stays — still unit-tested by Bridge.feature — to be exposed to Code as a
// devops action (the host-side "publish" primitive), which is where instance management belongs
// ("C# is the kernel — app logic belongs in the app"). See DECISIONS.

// The kernel's home: where it reads kernel.json and resolves instances/<id>/ (app docs + data).
// Defaults to the executable's directory (local/dev). A deployment can point it at a persistent
// data location via DEENV_HOME (e.g. /var/lib/deenv under systemd), separate from the binaries, so
// an update replaces the binaries without touching data. Backward compatible: unset → old behavior.
var baseDir = Environment.GetEnvironmentVariable("DEENV_HOME") is { Length: > 0 } home
    ? home
    : AppContext.BaseDirectory;

IReadOnlyList<InstanceSpec> specs;
Registry registry;
try
{
    registry = RegistryReader.Read(Path.Combine(baseDir, "kernel.json"));
    specs = KernelHost.SpecsFor(registry, baseDir);
}
catch (KernelConfigException ex)
{
    // No usable registry (missing / malformed / empty) — the kernel can't assemble the system.
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
    return;
}

// Two deployment knobs for running behind a reverse proxy (nginx terminating TLS), read from env so the
// committed kernel.json stays environment-agnostic (same as DEENV_HOME). Both default to today's
// behavior, so local/dev is untouched:
//   DEENV_BIND=loopback         → bind the app + asset hosts to 127.0.0.1 only (the proxy owns the
//                                 public interface; the raw ports never leave the box), else all
//                                 interfaces.
//   DEENV_PUBLIC_ASSET_PORT=<n> → the asset port the page tells the browser to use for /js + the
//                                 WebSocket, while the asset host still BINDS registry.AssetPort on the
//                                 box. `0` → SAME-ORIGIN: advertise an EMPTY authority so the browser
//                                 uses the app origin and the proxy routes /ws + /js to the asset host
//                                 (one origin → one auth gate covers the WebSocket too). `>0` → that
//                                 explicit public TLS asset port. Unset → advertise the bind port
//                                 (local/dev — same origin port as before).
var bindLoopback = string.Equals(
    Environment.GetEnvironmentVariable("DEENV_BIND"), "loopback", StringComparison.OrdinalIgnoreCase);
int? advertisedAssetPort =
    int.TryParse(Environment.GetEnvironmentVariable("DEENV_PUBLIC_ASSET_PORT"), out var pub) && pub >= 0
        ? pub
        : null;

// The two shared kernel ports come from the registry header (addressing is by path, not per-instance).
var kernel = new KernelHost(
    baseDir, Path.Combine(baseDir, "kernel.json"), registry.AppPort, registry.AssetPort,
    bindLoopback, advertisedAssetPort);
// Per-instance failures (a stale doc, a tripped storage guard) are handled INSIDE StartAsync — the
// bad instance is skipped loudly, the rest serve. Anything that escapes here is kernel-level (a host
// failing to bind); StartAsync has already stopped the hosts, so letting it crash the process is right.
await kernel.StartAsync(specs);

var iface = bindLoopback ? "127.0.0.1" : "all interfaces";
var advert = advertisedAssetPort switch
{
    null => "",
    0 => ", assets same-origin",
    int a => $", asset advertised as :{a}",
};
Console.WriteLine($"Kernel listening on {iface} — app:{kernel.AppPort} asset:{kernel.AssetPort}{advert}.");
foreach (var instance in kernel.Instances)
    Console.WriteLine($"  Hosting {instance.Spec.App} at /apps/{instance.Spec.App}.");
foreach (var failed in kernel.FailedInstances)
    Console.WriteLine($"  FAILED {failed.Spec.App} — /apps/{failed.Spec.App} answers 503 (see the boot error above).");
if (kernel.DesignSyncError is { } syncError)
    Console.WriteLine($"  Design library NOT reconciled this boot — {syncError}");

// Block until Ctrl+C / process exit, then stop every host cleanly.
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
try { await Task.Delay(Timeout.Infinite, shutdown.Token); }
catch (TaskCanceledException) { }
await kernel.DisposeAsync();
