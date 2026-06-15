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
// Constructed with the kernel's boot context — the base directory it resolves instances under and
// the registry file it persists to — so a Code-triggered create (sys.create) can self-service the
// kernel's own CreateAsync with the same id-layout + registry as a boot instance. CreateAsync /
// DeleteAsync / SwitchAsync deliberately KEEP baseDir/registryPath as explicit parameters (so they
// stay directly test-drivable against a temp dir + registry); these ctor fields exist ONLY to feed
// the self-service create delegate (HostActionsFor) the boot values — production passes the same
// dir + registry to both, so the two sources never diverge in practice.
public sealed class KernelHost(string baseDir, string registryPath) : IAsyncDisposable
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
            .Select(i => new InstanceInfo(IdOf(i.Spec), Path.GetFileName(i.Spec.SchemaPath), i.AppPort, i.InfraPort))
            .ToList();

    // The host-action seam for one instance: it acts as the designer (its own schema+data are the
    // meta-schema for a publish) and resolves a publish target id against the LIVE hosted set — a
    // closure over `_instances`, so an instance created after this one is still a reachable target.
    // Only created (id-dir) instances are targets; a boot instance has id 0 and matches nothing.
    // ASSUMPTION: `sys.publish` is only meaningful from the DESIGNER. Every instance gets this seam
    // with ITS OWN schema as the meta-schema, so a publish authored in a non-designer app (todo/crm)
    // would try to read that app's data as a meta-schema — SchemaBridge rejects a non-meta Db, so it
    // is a clean error, not a bad write. Today only the designer authors the call; pinning the publish
    // SOURCE to the designer instance specifically (rather than any caller) is a deferred model choice.
    private IHostActions HostActionsFor(InstanceSpec spec) =>
        new KernelHostActions(
            spec.SchemaPath, spec.DataPath,
            id => id == 0 ? null : _instances.FirstOrDefault(i => IdOf(i.Spec) == id)?.Spec,
            // create projects the caller's schema into a NEW instance via the kernel's own create
            // mechanism, fed the kernel's boot baseDir/registryPath so a Code-triggered create lands
            // in the same id-layout + registry as a boot one.
            createInstance: (appDoc, appPort, infraPort) =>
                CreateAsync(appDoc, appPort, infraPort, baseDir, registryPath));

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
            // reads the current hosted set; Register refreshes it as instances come up. Each gets
            // its own host-action seam (its own meta/data paths + the live target resolver).
            foreach (var spec in specs)
                Register(await HostedInstance.StartAsync(spec, _registry, HostActionsFor(spec)));
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
        var created = await HostedInstance.StartAsync(spec, _registry, HostActionsFor(spec));
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

    // Delete one CREATED instance from a RUNNING kernel and forget it: stop its hosts, drop it from
    // the live set, remove its registry entry, and delete its id-dir (app doc + co-located store).
    // The kernel's hosting MECHANISM only — the delete COMMAND/UI is image Code over the
    // registry-as-data, a later slice (the kernel-vs-image line). Restricted to created (id-dir)
    // instances: it must NEVER delete a boot/stem-derived instance's hand-authored data file — the
    // startup guard's "data is never dropped silently" principle (DECISIONS "Stored data must match
    // the running app"). `switch` works on any instance because it touches no data; `delete` does.
    public async Task DeleteAsync(HostedInstance instance, string registryPath)
    {
        // Refuse to delete a boot instance BEFORE stopping anything — its app doc + data are
        // hand-authored, never dropped automatically (CreatedIdOf throws for a non-created instance).
        var id = CreatedIdOf(instance);
        var baseDir = BaseDirOf(instance);

        // Stop-then-remove ordering: stop both hosts first (frees the ports), then drop it from the
        // live set and re-project. The LiveRegistry whole-snapshot swap keeps a concurrent render
        // thread safe (it reads the old immutable list until the new one is published).
        await instance.DisposeAsync();
        _instances.Remove(instance);
        RefreshRegistry();

        // Rewrite kernel.json without the matching entry, so it stays gone across a restart. (A crash
        // between the stop and this write leaves a "ghost" entry — the instance is down now but would
        // re-host on the next boot; acceptable single-operator, the symmetric twin of create's ghost.
        // True delete-atomicity is the deferred concurrent-write milestone, not a WAL here.)
        var appRel = RelativeApp(instance, baseDir);
        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances.Where(e => !PathsEqual(e.App, appRel))]));

        // Collect the store: delete the whole id-dir on the filesystem (app doc + its co-located
        // data file). Store-dir removal is an OS/kernel concern about id→location, so IInstanceStore
        // stays untouched (it speaks the model's terms — paths/nodes — not "drop my backing dir").
        var idDir = AppPaths.IdDirFor(baseDir, id);
        if (Directory.Exists(idDir))
            Directory.Delete(idDir, recursive: true);
    }

    // Re-bind one instance to a new port pair in a RUNNING kernel and persist it. Mirrors create:
    // the operator supplies the new ports (a port is a genuinely contended external resource, so it
    // stays operator-set — no auto-allocation). Works on ANY instance (boot or created): it rewrites
    // only ports and touches no data. The kernel MECHANISM only; the switch COMMAND/UI is image Code.
    public async Task SwitchAsync(HostedInstance instance, int newAppPort, int newInfraPort, string registryPath)
    {
        var baseDir = BaseDirOf(instance);
        var newSpec = instance.Spec with { AppPort = newAppPort, InfraPort = newInfraPort };

        // Guard FIRST, before stopping anything: a rejected switch must stop nothing. Check the new
        // ports (and unchanged data file) against the LIVE set EXCLUDING this instance — so re-binding
        // doesn't false-positive against its own current ports — reusing create/boot's named errors.
        EnsureNoCollision(
            newSpec,
            new HashSet<string>(
                _instances.Where(i => i != instance).Select(i => i.Spec.DataPath),
                StringComparer.OrdinalIgnoreCase),
            new HashSet<int>(
                _instances.Where(i => i != instance).SelectMany(i => new[] { i.AppPort, i.InfraPort })));

        // Stop the old binding, start the new one (ports are the only delta), swap it in, re-project.
        await instance.DisposeAsync();
        var restarted = await HostedInstance.StartAsync(newSpec, _registry, HostActionsFor(newSpec));
        _instances[_instances.IndexOf(instance)] = restarted;
        RefreshRegistry();

        // Persist: rewrite kernel.json mapping the matching entry to its new ports, so the binding
        // survives a restart. (App path is unchanged — switch re-points ports, not the app document;
        // repointing to a DIFFERENT app is a deferred slice.)
        var appRel = RelativeApp(instance, baseDir);
        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances.Select(e =>
                PathsEqual(e.App, appRel) ? new RegistryEntry(e.App, newAppPort, newInfraPort) : e)]));
    }

    // The base directory an instance was hosted from, recovered from its schema path. A created
    // instance's schema dir is <baseDir>/instances/<id>, so baseDir is two levels up; a boot
    // instance's schema dir IS <baseDir>. Derived from the spec so callers needn't thread baseDir.
    private static string BaseDirOf(HostedInstance instance)
    {
        var schemaDir = Path.GetDirectoryName(instance.Spec.SchemaPath)!;
        return IsCreated(instance)
            ? Path.GetDirectoryName(Path.GetDirectoryName(schemaDir)!)!
            : schemaDir;
    }

    // A created instance lives under <baseDir>/instances/<id>/ with a numeric id-dir name; a boot
    // instance does not. This is the gate `delete` uses to refuse dropping hand-authored boot data,
    // and the gate `publish` uses to decide which instances are addressable targets. Spec-based so
    // both the live hosted set and a candidate spec can be tested by the same rule.
    private static bool IsCreated(InstanceSpec spec)
    {
        var schemaDir = Path.GetDirectoryName(spec.SchemaPath)!;
        var parent = Path.GetDirectoryName(schemaDir);
        return parent is not null
            && string.Equals(Path.GetFileName(parent), "instances", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(Path.GetFileName(schemaDir), out _);
    }

    private static bool IsCreated(HostedInstance instance) => IsCreated(instance.Spec);

    // The created id parsed from the instance's id-dir name. Throws if the instance is not a created
    // one — `delete` is restricted to created instances (never drop a boot instance's authored data).
    private static int CreatedIdOf(HostedInstance instance)
    {
        if (!IsCreated(instance))
            throw new KernelConfigException(
                "Only an instance created by the kernel can be deleted — a boot instance's app " +
                "document and data are hand-authored and are never dropped automatically.");
        return int.Parse(Path.GetFileName(Path.GetDirectoryName(instance.Spec.SchemaPath)!));
    }

    // The instance's id: its created id-dir number, or 0 for a boot/stem-derived instance (which
    // has no id yet — boot instances aren't publish targets; uniform ids for boot instances stay
    // deferred). Non-throwing, used to project `sys.instances` rows and to resolve a publish target.
    private static int IdOf(InstanceSpec spec) =>
        IsCreated(spec) ? int.Parse(Path.GetFileName(Path.GetDirectoryName(spec.SchemaPath)!)) : 0;

    // The instance's app-relative path as it appears in kernel.json's RegistryEntry.App: the schema
    // path made relative to baseDir, forward-slashed (created entries are stored forward-slashed; boot
    // entries are a bare file name). Used to find the instance's own registry entry to rewrite/remove.
    private static string RelativeApp(HostedInstance instance, string baseDir) =>
        Path.GetRelativePath(baseDir, instance.Spec.SchemaPath).Replace('\\', '/');

    // Compare two registry app paths for the same instance, separator- and case-insensitively, so a
    // match holds regardless of how the path was written (hand-edited boot entry vs. minted created
    // entry) or the host OS's directory separator.
    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

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
