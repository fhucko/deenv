using DeEnv.Storage;

namespace DeEnv.Kernel;

// The kernel supervisor: in one process, hosts every instance in the registry at once — each on
// its own port pair with its own sovereign store — and exposes them. It owns the multi-instance
// hosting MECHANISM only (host N instances, bind ports, hold the registry); the management
// EXPERIENCE (create/list/switch/delete) is image Code in a later slice, not C# here — that is the
// kernel-vs-image line from DECISIONS ("Multi-instance management — the kernel host").
//
// It deliberately does NOT own process lifetime (blocking on Ctrl+C) — the composition root does —
// so it stays a plain start/stop unit that tests can drive synchronously.
public sealed class KernelHost : IAsyncDisposable
{
    private readonly List<HostedInstance> _instances = [];
    public IReadOnlyList<HostedInstance> Instances => _instances;

    // Resolve each registry entry to a hosting spec: the app name becomes the schema path, and the
    // data path is DERIVED from the app stem (AppPaths) — never stored in the registry, so distinct
    // apps get distinct stores. Resolution lives here, off the registry shape, so the registry stays
    // minimal and locality-free.
    public static IReadOnlyList<InstanceSpec> SpecsFor(Registry registry, string baseDir)
    {
        var specs = registry.Instances
            .Select(e => new InstanceSpec(
                AppPaths.SchemaPath(e.App, baseDir),
                AppPaths.DataPath(e.App, baseDir),
                e.AppPort,
                e.InfraPort))
            .ToList();
        EnsureNoCollisions(specs);
        return specs;
    }

    // Fail loudly on a registry that would alias resources across instances. Two instances
    // resolving to the SAME data file would silently break data sovereignty — the slice's headline
    // guarantee — so it must be caught here, not discovered later (matches the storage guard's
    // "never silently run over the wrong data", DECISIONS "Stored data must match the running app").
    // A shared port is rejected too: GenHTTP would fail the second bind anyway, but an upfront,
    // named error beats a mid-startup bind exception. (Two instances of the SAME app needing
    // SEPARATE stores is the deferred test-instance/branch case — the registry grows an explicit
    // data-file field THEN; until then, distinct apps are how you get distinct stores.)
    private static void EnsureNoCollisions(IReadOnlyList<InstanceSpec> specs)
    {
        var dataPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in specs)
            if (!dataPaths.Add(s.DataPath))
                throw new KernelConfigException(
                    $"Two instances resolve to the same data file '{s.DataPath}' — each instance needs " +
                    "its own store. Give them different app documents.");

        var ports = new HashSet<int>();
        foreach (var s in specs)
            foreach (var port in new[] { s.AppPort, s.InfraPort })
                if (!ports.Add(port))
                    throw new KernelConfigException(
                        $"Port {port} is claimed by more than one instance — every app and infra port " +
                        "must be unique.");
    }

    // Start every instance. If one fails mid-startup (e.g. a stale data file trips the storage
    // guard), the already-started instances are stopped so the process never leaks half-bound ports.
    public async Task StartAsync(IReadOnlyList<InstanceSpec> specs)
    {
        try
        {
            foreach (var spec in specs)
                _instances.Add(await HostedInstance.StartAsync(spec));
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _instances)
            await instance.DisposeAsync();
        _instances.Clear();
    }
}
