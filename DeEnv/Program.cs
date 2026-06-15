using DeEnv.Kernel;
using DeEnv.Storage;

// ── Entry point: the kernel host ─────────────────────────────────────────────────
//
// The program IS the kernel host. It reads the instance registry (kernel.json) and hosts every
// instance in it at once — each on its own port pair with its own sovereign data — then blocks
// until shutdown. There are NO run modes and no --app switch: kernel.json is the single source of
// truth for what runs (one entry = a single app; many entries = multi-instance). A single instance
// is just a one-entry registry.
//
// The M4 schema tools (designing a schema, publishing it via SchemaBridge) are no longer CLI
// modes. SchemaBridge stays — still unit-tested by Bridge.feature — to be exposed to Code as a
// devops action (the host-side "publish" primitive), which is where instance management belongs
// ("C# is the kernel — app logic belongs in the app"). See DECISIONS.

var baseDir = AppContext.BaseDirectory;

IReadOnlyList<InstanceSpec> specs;
try
{
    var registry = RegistryReader.Read(Path.Combine(baseDir, "kernel.json"));
    specs = KernelHost.SpecsFor(registry, baseDir);
}
catch (KernelConfigException ex)
{
    // No usable registry (missing / malformed / empty) — the kernel can't assemble the system.
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
    return;
}

var kernel = new KernelHost(baseDir, Path.Combine(baseDir, "kernel.json"));
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

foreach (var instance in kernel.Instances)
    Console.WriteLine($"Hosting {instance.Spec.App} on app:{instance.AppPort} infra:{instance.InfraPort}.");

// Block until Ctrl+C / process exit, then stop every host cleanly.
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
try { await Task.Delay(Timeout.Infinite, shutdown.Token); }
catch (TaskCanceledException) { }
await kernel.DisposeAsync();
