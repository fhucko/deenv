using DeEnv.Kernel;
using DeEnv.Storage;

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

// The two shared kernel ports come from the registry header (addressing is by path, not per-instance).
var kernel = new KernelHost(baseDir, Path.Combine(baseDir, "kernel.json"), registry.AppPort, registry.AssetPort);
try
{
    await kernel.StartAsync(specs);
}
catch (StoredDataException ex)
{
    // The startup guard tripped: an instance's data file belongs to a different/older app. Refuse
    // to serve (mutations would silently never persist) — the message names the file and the remedy.
    Console.Error.WriteLine(ex.Message);
    await kernel.DisposeAsync();
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine($"Kernel listening — app:{kernel.AppPort} asset:{kernel.AssetPort}.");
foreach (var instance in kernel.Instances)
    Console.WriteLine($"  Hosting {instance.Spec.App} at /apps/{instance.Spec.App}.");

// Block until Ctrl+C / process exit, then stop every host cleanly.
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
try { await Task.Delay(Timeout.Infinite, shutdown.Token); }
catch (TaskCanceledException) { }
await kernel.DisposeAsync();
