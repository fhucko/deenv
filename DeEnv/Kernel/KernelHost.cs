using DeEnv.Http;
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

    // The current registry as a live DATA cell (LiveRegistry), shared BY REFERENCE with every hosted
    // instance's renderer. RefreshRegistry swaps `.Current` (an immutable snapshot) whenever the hosted
    // set changes, so a render on ANY instance reads the LIVE list — no frozen per-instance snapshot, no
    // stale data after a create. (Live VIEW only — a render reads the current list; PUSHING an update to
    // an already-open browser page is the deferred real-time milestone, a different thing — but the cell
    // is the var-shaped seam that future path will hang notification on.)
    private readonly LiveRegistry _registry = new();

    // Track a newly-started instance, then re-project the registry so every instance's next render
    // sees it.
    private void Register(HostedInstance instance)
    {
        _instances.Add(instance);
        RefreshRegistry();
    }

    private void RefreshRegistry() =>
        _registry.Current = _instances
            .Select(i => new InstanceInfo(Path.GetFileName(i.Spec.SchemaPath), i.AppPort, i.InfraPort))
            .ToList();

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
        var ports = new HashSet<int>();
        foreach (var s in specs)
            EnsureNoCollision(s, dataPaths, ports);
    }

    // Check one candidate spec against the data files + ports already claimed (the accumulators are
    // mutated to include it). Shared by the boot-time all-specs check and CreateAsync's check of a
    // new instance against the LIVE set, so both report the same named errors.
    private static void EnsureNoCollision(InstanceSpec spec, HashSet<string> dataPaths, HashSet<int> ports)
    {
        if (!dataPaths.Add(spec.DataPath))
            throw new KernelConfigException(
                $"Two instances resolve to the same data file '{spec.DataPath}' — each instance needs " +
                "its own store. Give them different app documents.");

        foreach (var port in new[] { spec.AppPort, spec.InfraPort })
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
            // Every instance shares the LIVE registry provider (CurrentRegistry), so each render
            // reads the current hosted set; Register refreshes it as instances come up.
            foreach (var spec in specs)
                Register(await HostedInstance.StartAsync(spec, _registry));
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    // Create one new instance in a RUNNING kernel and persist it to the registry. The kernel's
    // hosting MECHANISM only — the create COMMAND/UI (operator-facing) is image Code over the
    // registry-as-data, a later slice (the kernel-vs-image line, DECISIONS "Multi-instance
    // management"). The app document is supplied as content; the operator sets the port pair (ports
    // are a genuinely contended external resource); storage is keyed by a kernel-minted id, never a
    // user-chosen file name (DECISIONS "`create` direction — storage by id…").
    public async Task<HostedInstance> CreateAsync(
        string appDoc, int appPort, int assetsPort, string baseDir, string registryPath)
    {
        // Mint a deterministic id = max existing numeric id-dir + 1 (1 when none) so it survives a
        // restart and never reuses a directory.
        var id = NextInstanceId(baseDir);
        var appRelative = AppPaths.CreatedAppRelative(id);
        var schemaPath = AppPaths.SchemaPath(appRelative, baseDir);
        Directory.CreateDirectory(Path.GetDirectoryName(schemaPath)!);
        File.WriteAllText(schemaPath, appDoc);

        var spec = new InstanceSpec(
            schemaPath, AppPaths.DataPath(appRelative, baseDir), appPort, assetsPort);

        // Collision-check the new spec against the LIVE set's data files + ports (same named errors
        // as boot), so a created instance can never alias a running one's store or port.
        EnsureNoCollision(
            spec,
            new HashSet<string>(_instances.Select(i => i.Spec.DataPath), StringComparer.OrdinalIgnoreCase),
            new HashSet<int>(_instances.SelectMany(i => new[] { i.AppPort, i.InfraPort })));

        // Start it (every instance shares the LIVE registry provider, so this create shows up on
        // EVERY instance's next render — boot and created alike, no stale list), then track + persist.
        var created = await HostedInstance.StartAsync(spec, _registry);
        Register(created);

        // Persist: append the created entry (forward-slash relative app path) and rewrite kernel.json,
        // so the instance reappears on the next boot via SpecsFor. Persist AFTER a successful start, so
        // a failed start never leaves an orphan entry. (If the WRITE itself throws after the start, the
        // instance is live now but gone on the next boot — a "ghost"; acceptable single-operator, the
        // operator sees the exception. True create-atomicity is the deferred concurrent-write milestone.)
        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances, new RegistryEntry(appRelative, appPort, assetsPort)]));

        return created;
    }

    // The next created-instance id: max numeric subdirectory name under <baseDir>/instances/, + 1
    // (1 when the dir is absent or empty). Deterministic and restart-stable.
    private static int NextInstanceId(string baseDir)
    {
        var dir = AppPaths.InstancesDir(baseDir);
        if (!Directory.Exists(dir)) return 1;
        var max = Directory.EnumerateDirectories(dir)
            .Select(Path.GetFileName)
            .Select(name => int.TryParse(name, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return max + 1;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _instances)
            await instance.DisposeAsync();
        _instances.Clear();
        RefreshRegistry();
    }
}
