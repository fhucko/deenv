using System.Collections.Concurrent;
using DeEnv.Http;
using DeEnv.Storage;

namespace DeEnv.Kernel;

// The kernel supervisor: in one process, hosts every instance in the registry at once — each on
// its own port pair with its own sovereign store — and exposes them. It owns the multi-instance
// hosting MECHANISM only (host N instances, bind ports, hold the registry); the management
// EXPERIENCE (create/list/switch/delete) is image Code in a later slice, not C# here — that is the
// kernel-vs-image line from DECISIONS ("Multi-instance management — the kernel host").
//
// Storage is FULLY ID-BASED: every instance lives under instances/<id>/ and is resolved purely by
// its id; the registry `app` field is a display NAME label, used for nothing functional. There is no
// boot-vs-created distinction — clone/delete/publish all work on ANY instance by its id. (Deleting an
// instance removes its instances/<id>/ dir; the committed app SOURCES are git-tracked, the accepted
// safety net.)
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
    // Keyed by instance id (its unique address + id-dir name). A dictionary, not a list, so every
    // operation addresses an instance by its STABLE id — never a positional index, which a fire-and-forget
    // restart (KernelHostActions) could race into "Index was out of range". ConcurrentDictionary is
    // thread-safe per operation, so that restart — which can outlive its WS message and overlap a later
    // create/delete or shutdown — needs no external lock; its hot-swap is an atomic TryUpdate (below).
    private readonly ConcurrentDictionary<int, HostedInstance> _instances = new();
    public IReadOnlyList<HostedInstance> Instances => _instances.Values.OrderBy(i => i.Spec.Id).ToList();

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
        _instances[instance.Spec.Id] = instance;
        RefreshRegistry();
    }

    // Re-project the live hosted set as registry rows. The instance NAME comes from spec.App (the
    // display label) — NOT the schema file name, which is "app" for every instance now that storage
    // is id-based.
    private void RefreshRegistry() =>
        _registry.Current = _instances.Values
            .OrderBy(i => i.Spec.Id)
            .Select(i => new InstanceInfo(IdOf(i.Spec), i.Spec.App, i.AppPort, i.InfraPort, i.Spec.DesignId))
            .ToList();

    // The host-action seam for one instance: it acts as the designer (its own schema+data are the
    // meta-schema for a publish) and resolves a publish target id against the LIVE hosted set — a
    // closure over `_instances`, so an instance created after this one is still a reachable target.
    // ANY instance is a publish target (resolution is purely by id — there is no boot-vs-created
    // distinction): an unknown id resolves to null → the existing reject. (The id is unique per
    // instance, so this targets exactly one app.)
    // ASSUMPTION: `sys.publish` is only meaningful from the DESIGNER. Every instance gets this seam
    // with ITS OWN schema as the meta-schema, so a publish authored in a non-designer app (todo/crm)
    // would try to read that app's data as a meta-schema — SchemaBridge rejects a non-meta Db, so it
    // is a clean error, not a bad write. Today only the designer authors the call; pinning the publish
    // SOURCE to the designer instance specifically (rather than any caller) is a deferred model choice.
    private IHostActions HostActionsFor(InstanceSpec spec) =>
        new KernelHostActions(
            spec.SchemaPath, spec.DataPath,
            id => _instances.GetValueOrDefault(id)?.Spec,
            // create projects the caller's schema into a NEW instance via the kernel's own create
            // mechanism, fed the kernel's boot baseDir/registryPath so a Code-triggered create lands
            // in the same id-layout + registry as a boot one. The design's id is recorded on the new
            // entry (so the new instance's design dropdown pre-selects it).
            createInstance: (appDoc, name, appPort, infraPort, designId) =>
                CreateAsync(appDoc, name, appPort, infraPort, baseDir, registryPath, designId),
            // delete + clone resolve the id → the live hosted instance, then run the kernel's own
            // DeleteAsync/CloneAsync (fed the same boot baseDir/registryPath as create, so a
            // Code-triggered clone lands in the same id-layout + registry).
            deleteInstance: id => DeleteAsyncById(id, registryPath),
            cloneInstance: (sourceId, appPort, infraPort) =>
                CloneAsyncById(sourceId, appPort, infraPort, baseDir, registryPath),
            // setDesign records the chosen design id on the target's registry entry (and refreshes the
            // live view so the dropdown re-selects it on the next render). The projection itself is run
            // by KernelHostActions after this records the reference — the registry write is the "remember
            // which design" half, the publish projection the "deploy it" half.
            recordDesign: (targetId, designId) => SetDesignAsyncById(targetId, designId, registryPath),
            // restart re-reads the updated schema+data from disk and hot-swaps the hosted instance;
            // called fire-and-forget after every publish/setDesign.
            restartInstance: id => RestartAsync(id),
            renameInstance: (id, name) => RenameAsync(id, name, registryPath));

    // Resolve an instance id → the live hosted instance (by its unique Spec.Id) and delete it. An id
    // matching no instance is a clear reject before any work. Every instance is deletable now.
    private async Task DeleteAsyncById(int id, string registryPath)
    {
        var instance = _instances.GetValueOrDefault(id)
            ?? throw new InvalidOperationException($"No instance with id {id} to delete.");
        await DeleteAsync(instance, registryPath);
    }

    // Resolve a source instance id → the live hosted instance (by its unique Spec.Id) and clone it
    // onto the given ports. An unknown id is a clear reject. Any instance is a valid clone source —
    // clone only READS its files (see CloneAsync). (Ids are unique, so cloning a specific instance is
    // unambiguous.)
    private async Task CloneAsyncById(int sourceId, int appPort, int infraPort, string baseDir, string registryPath)
    {
        var source = _instances.GetValueOrDefault(sourceId)
            ?? throw new InvalidOperationException($"No instance with id {sourceId} to clone.");
        await CloneAsync(source, appPort, infraPort, baseDir, registryPath);
    }

    // Resolve a target instance id → the live hosted instance and record which design it now runs (the
    // registry-write half of the IDE's Apply; KernelHostActions runs the publish projection). An unknown
    // id is a clear reject before any write. Async only for delegate-signature symmetry (the work is
    // synchronous — a registry rewrite, no port bind).
    private Task SetDesignAsyncById(int targetId, int designId, string registryPath)
    {
        var target = _instances.GetValueOrDefault(targetId)
            ?? throw new InvalidOperationException($"No instance with id {targetId} to set a design on.");
        SetDesign(target, designId, registryPath);
        return Task.CompletedTask;
    }

    // Record the design an instance runs: update its in-memory spec, re-project the live view (so the
    // design dropdown pre-selects the new choice on the next render), and persist the new DesignId to
    // kernel.json (so the reference survives a restart). The instance is NOT restarted — DesignId is
    // registry metadata, not a hosting parameter; the IDE's Apply pairs this with a publish that does the
    // actual document/data deploy. The kernel MECHANISM only; Apply (the operator command) is image Code.
    public void SetDesign(HostedInstance instance, int designId, string registryPath)
    {
        instance.SetDesignId(designId);
        RefreshRegistry();

        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances.Select(e =>
                e.Id == instance.Spec.Id ? e with { DesignId = designId } : e)]));
    }

    // Resolve each registry entry to a hosting spec: the schema/data paths are derived PURELY from the
    // entry's id (AppPaths.SchemaPathForId/DataPathForId — instances/<id>/app.app + app-data.json),
    // never from the `app` name (which is carried through as a display label only). Distinct ids get
    // distinct id-dirs, so distinct instances get distinct stores. Resolution lives here, off the
    // registry shape, so the registry stays minimal and locality-free.
    public static IReadOnlyList<InstanceSpec> SpecsFor(Registry registry, string baseDir)
    {
        var specs = registry.Instances
            .Select(e => new InstanceSpec(
                e.Id,
                e.App,
                AppPaths.SchemaPathForId(baseDir, e.Id),
                AppPaths.DataPathForId(baseDir, e.Id),
                e.AppPort,
                e.InfraPort,
                e.DesignId))
            .ToList();
        EnsureNoCollisions(specs);
        return specs;
    }

    // Fail loudly on a registry that would alias resources across instances. Two instances
    // resolving to the SAME data file would silently break data sovereignty — the slice's headline
    // guarantee — so it must be caught here, not discovered later (matches the storage guard's
    // "never silently run over the wrong data", DECISIONS "Stored data must match the running app").
    // A shared port is rejected too: GenHTTP would fail the second bind anyway, but an upfront,
    // named error beats a mid-startup bind exception. (Since storage is id-based, two entries with
    // the SAME id are what would alias a store — distinct ids never collide.)
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
                "its own store. Give them distinct ids.");

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
        // Start every instance CONCURRENTLY. Each binds its own (collision-checked) ports and opens
        // its own store, so they have no startup-time dependency. GenHTTP's host start blocks its
        // thread synchronously (seconds each), so plain async concurrency would NOT overlap them —
        // Task.Run puts each instance's startup on its own thread, turning boot from sum-of-instances
        // into max-of-instances. Every instance shares the LIVE registry provider and gets its own
        // host-action seam (a lazy closure over the hosted set).
        var starts = specs
            .Select(spec => Task.Run(() => HostedInstance.StartAsync(spec, _registry, HostActionsFor(spec))))
            .ToList();
        try
        {
            await Task.WhenAll(starts);
        }
        catch
        {
            // One failed mid-startup (e.g. a stale data file trips the storage guard). Stop every
            // instance that DID start so the process never leaks half-bound ports, then rethrow.
            foreach (var start in starts)
                if (start.IsCompletedSuccessfully)
                    await start.Result.DisposeAsync();
            await DisposeAsync();
            throw;
        }

        // All up — register them, refreshing the shared LiveRegistry with the full set in one go.
        foreach (var start in starts)
            Register(start.Result);
    }

    // Create one new instance in a RUNNING kernel and persist it to the registry. The kernel's
    // hosting MECHANISM only — the create COMMAND/UI (operator-facing) is image Code over the
    // registry-as-data, a later slice (the kernel-vs-image line, DECISIONS "Multi-instance
    // management"). The app document is supplied as content; the operator sets the port pair (ports
    // are a genuinely contended external resource); storage is keyed by a kernel-minted id, never a
    // user-chosen file name (DECISIONS "`create` direction — storage by id…"). A created instance
    // `name` is the display label for the new instance (the registry `app` field). `designId` is the
    // id of the IDE design this instance was spawned from (null when none) — recorded on the new entry
    // so its design dropdown pre-selects it and the instances list resolves its design, exactly like a
    // seeded instance; the IDE's create form threads the picked design's id (mirrors how setDesign
    // writes DesignId on an existing entry).
    public async Task<HostedInstance> CreateAsync(
        string appDoc, string name, int appPort, int assetsPort, string baseDir, string registryPath, int? designId = null)
    {
        // Mint a unique id (max over the live set AND on-disk id-dirs + 1). Its id-dir is
        // instances/<id>/, so it survives a restart and never reuses a directory — even a ghost one.
        var id = NextInstanceId(baseDir);
        var schemaPath = AppPaths.SchemaPathForId(baseDir, id);
        Directory.CreateDirectory(AppPaths.IdDirFor(baseDir, id));
        File.WriteAllText(schemaPath, appDoc);

        var spec = new InstanceSpec(
            id, name, schemaPath, AppPaths.DataPathForId(baseDir, id), appPort, assetsPort, designId);

        // Collision-check the new spec against the LIVE set's data files + ports (same named errors
        // as boot), so a created instance can never alias a running one's store or port.
        EnsureNoCollision(
            spec,
            new HashSet<string>(_instances.Values.Select(i => i.Spec.DataPath), StringComparer.OrdinalIgnoreCase),
            new HashSet<int>(_instances.Values.SelectMany(i => new[] { i.AppPort, i.InfraPort })));

        // Start it (every instance shares the LIVE registry provider, so this create shows up on
        // EVERY instance's next render — no stale list), then track + persist.
        var created = await HostedInstance.StartAsync(spec, _registry, HostActionsFor(spec));
        Register(created);

        // Persist: append the created entry and rewrite kernel.json, so the instance reappears on the
        // next boot via SpecsFor. Persist AFTER a successful start, so a failed start never leaves an
        // orphan entry. (If the WRITE itself throws after the start, the instance is live now but gone
        // on the next boot — a "ghost"; acceptable single-operator, the operator sees the exception.
        // True create-atomicity is the deferred concurrent-write milestone.)
        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances, new RegistryEntry(id, name, appPort, assetsPort, designId)]));

        return created;
    }

    // Clone one instance in a RUNNING kernel: copy its app document AND its data into a NEW instance
    // on the given ports, then persist it. The data-carrying sibling of CreateAsync — where create
    // PROJECTS a fresh design (write a new doc, empty store), clone COPIES a live one byte-for-byte
    // (the same app doc, the same data), so the new instance is a true, independent copy with its own
    // sovereign store. Any source is fine to clone: we only READ its files, never touch them. The
    // clone keeps the source's display name. Single-operator copy: cloning a source that is being
    // written concurrently is the deferred concurrency case (the same ghost/atomicity caveat as
    // create), not handled here.
    public async Task<HostedInstance> CloneAsync(
        HostedInstance source, int appPort, int infraPort, string baseDir, string registryPath)
    {
        // Mint a fresh unique id + id-dir paths, exactly like CreateAsync.
        var id = NextInstanceId(baseDir);
        var schemaPath = AppPaths.SchemaPathForId(baseDir, id);
        var dataPath = AppPaths.DataPathForId(baseDir, id);
        Directory.CreateDirectory(AppPaths.IdDirFor(baseDir, id));

        // Copy the source's files instead of writing a projected document: the app doc always, and
        // the data file when it exists (a brand-new source may not have persisted yet). This copied
        // data is the clone's whole point — an independent store seeded from the source's current
        // state, NOT an empty one.
        File.Copy(source.Spec.SchemaPath, schemaPath);
        if (File.Exists(source.Spec.DataPath))
            File.Copy(source.Spec.DataPath, dataPath);

        var spec = new InstanceSpec(id, source.Spec.App, schemaPath, dataPath, appPort, infraPort);

        // Collision-check the new spec against the LIVE set's data files + ports (same named errors
        // as boot/create), so the clone can never alias a running instance's store or port.
        EnsureNoCollision(
            spec,
            new HashSet<string>(_instances.Values.Select(i => i.Spec.DataPath), StringComparer.OrdinalIgnoreCase),
            new HashSet<int>(_instances.Values.SelectMany(i => new[] { i.AppPort, i.InfraPort })));

        // Start it (shares the LIVE registry, so it shows up on every instance's next render), then
        // track + persist AFTER a successful start (a failed start leaves no orphan entry — the same
        // ghost caveat as create if the WRITE itself throws).
        var created = await HostedInstance.StartAsync(spec, _registry, HostActionsFor(spec));
        Register(created);

        RegistryWriter.Write(registryPath, new Registry(
            [.. RegistryReader.Read(registryPath).Instances,
                new RegistryEntry(id, source.Spec.App, appPort, infraPort)]));

        return created;
    }

    // Delete one instance from a RUNNING kernel and forget it: stop its hosts, drop it from the live
    // set, remove its registry entry, and delete its id-dir (app doc + co-located store). The kernel's
    // hosting MECHANISM only — the delete COMMAND/UI is image Code over the registry-as-data, a later
    // slice (the kernel-vs-image line). Works on ANY instance by its id: the committed app SOURCES are
    // git-tracked, which is the accepted safety net for the data this drops (DECISIONS / user sign-off).
    public async Task DeleteAsync(HostedInstance instance, string registryPath)
    {
        // Stop-then-remove ordering: stop both hosts first (frees the ports), then drop it from the
        // live set and re-project. The LiveRegistry whole-snapshot swap keeps a concurrent render
        // thread safe (it reads the old immutable list until the new one is published).
        await instance.DisposeAsync();
        _instances.TryRemove(instance.Spec.Id, out _);
        RefreshRegistry();

        // Rewrite kernel.json without the matching entry (by its unique id), so it stays gone across a
        // restart. (A crash between the stop and this write leaves a "ghost" entry — the instance is
        // down now but would re-host on the next boot; acceptable single-operator, the symmetric twin
        // of create's ghost. True delete-atomicity is the deferred concurrent-write milestone, not a
        // WAL here.)
        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances.Where(e => e.Id != instance.Spec.Id)]));

        // Collect the store: delete the whole id-dir on the filesystem (app doc + its co-located
        // data file). Store-dir removal is an OS/kernel concern about id→location, so IInstanceStore
        // stays untouched (it speaks the model's terms — paths/nodes — not "drop my backing dir").
        var idDir = AppPaths.IdDirFor(BaseDirOf(instance), instance.Spec.Id);
        if (Directory.Exists(idDir))
            Directory.Delete(idDir, recursive: true);
    }

    // Re-bind one instance to a new port pair in a RUNNING kernel and persist it. Mirrors create:
    // the operator supplies the new ports (a port is a genuinely contended external resource, so it
    // stays operator-set — no auto-allocation). It rewrites only ports and touches no data. The
    // kernel MECHANISM only; the switch COMMAND/UI is image Code.
    public async Task SwitchAsync(HostedInstance instance, int newAppPort, int newInfraPort, string registryPath)
    {
        var newSpec = instance.Spec with { AppPort = newAppPort, InfraPort = newInfraPort };

        // Guard FIRST, before stopping anything: a rejected switch must stop nothing. Check the new
        // ports (and unchanged data file) against the LIVE set EXCLUDING this instance — so re-binding
        // doesn't false-positive against its own current ports — reusing create/boot's named errors.
        EnsureNoCollision(
            newSpec,
            new HashSet<string>(
                _instances.Values.Where(i => i != instance).Select(i => i.Spec.DataPath),
                StringComparer.OrdinalIgnoreCase),
            new HashSet<int>(
                _instances.Values.Where(i => i != instance).SelectMany(i => new[] { i.AppPort, i.InfraPort })));

        // Stop the old binding, start the new one (ports are the only delta), swap it in, re-project.
        await instance.DisposeAsync();
        var restarted = await HostedInstance.StartAsync(newSpec, _registry, HostActionsFor(newSpec));
        // Atomic id-keyed swap (no positional index): only replace if this instance is still the live one.
        if (!_instances.TryUpdate(instance.Spec.Id, restarted, instance))
        {
            await restarted.DisposeAsync();
            throw new InvalidOperationException($"Instance {instance.Spec.Id} changed during the switch.");
        }
        RefreshRegistry();

        // Persist: rewrite kernel.json mapping the matching entry (by its unique id) to its new ports,
        // so the binding survives a restart. (App + id are unchanged — switch re-points ports, not the
        // app document; repointing to a DIFFERENT app is a deferred slice.)
        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances.Select(e =>
                e.Id == instance.Spec.Id ? e with { AppPort = newAppPort, InfraPort = newInfraPort } : e)]));
    }

    // Restart one instance in a RUNNING kernel: stop its current hosts, re-read its now-updated schema
    // and data from disk (written by a preceding publish/setDesign), and start fresh hosts on the same
    // ports. Called fire-and-forget after every publish/setDesign so the live instance immediately
    // reflects the deployed version. Self-restart (the designer publishing to itself) is supported: the
    // "ok" is sent before this fires, so the WS handler is already done when the hosts stop.
    // Rename an instance's display label in a RUNNING kernel. Updates the live spec and rewrites
    // kernel.json so the new label persists. No hosts are stopped — renaming is registry metadata only.
    public Task RenameAsync(int id, string name, string registryPath)
    {
        var instance = _instances.GetValueOrDefault(id)
            ?? throw new InvalidOperationException($"No instance with id {id} to rename.");
        instance.SetApp(name);
        RefreshRegistry();
        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances.Select(e => e.Id == id ? e with { App = name } : e)]));
        return Task.CompletedTask;
    }

    public async Task RestartAsync(int id)
    {
        // Called FIRE-AND-FORGET after publish/setDesign, so it can run after its WS message returned and
        // overlap a later delete or the kernel's shutdown. Address the instance by ID throughout (never a
        // positional index — the source of the prior "Index was out of range"); the hot-swap is an atomic
        // TryUpdate that replaces only while THIS instance is still the live one. If it was deleted /
        // re-swapped meanwhile, unwind the freshly-started host rather than resurrecting a gone instance.
        // Swallow+log rather than surface an UNOBSERVED exception — the deploy already succeeded, the
        // restart is a best-effort hot-swap (the id-dir may have been cleaned up mid-restart).
        try
        {
            if (!_instances.TryGetValue(id, out var existing)) return;
            await existing.DisposeAsync();
            var restarted = await HostedInstance.StartAsync(existing.Spec, _registry, HostActionsFor(existing.Spec));
            if (_instances.TryUpdate(id, restarted, existing))
                RefreshRegistry();
            else
                await restarted.DisposeAsync(); // deleted or re-swapped during restart — unwind, don't leak
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Background restart of instance {id} failed: {ex.Message}");
        }
    }

    // The base directory an instance was hosted from, recovered from its schema path. Every instance
    // lives at <baseDir>/instances/<id>/app.app, so baseDir is two levels up from the schema dir.
    // Derived from the spec so callers needn't thread baseDir.
    private static string BaseDirOf(HostedInstance instance)
    {
        var schemaDir = Path.GetDirectoryName(instance.Spec.SchemaPath)!; // <baseDir>/instances/<id>
        return Path.GetDirectoryName(Path.GetDirectoryName(schemaDir)!)!; // <baseDir>
    }

    // The instance's unique id — its address for clone/delete/publish and `sys.instances` rows.
    // Carried on the spec (assigned in the registry / minted by create). It is also the instance's
    // id-dir name (instances/<Id>/) — the sole key to its files now that storage is id-based.
    private static int IdOf(InstanceSpec spec) => spec.Id;

    // The next instance id: max id over the LIVE hosted set + 1 (1 when empty). A single shared id
    // pool, so every hosted instance has a unique address. Deterministic; an id-dir is instances/<id>/.
    // The next unique instance id: one past the max of BOTH the live hosted set AND any on-disk
    // instances/<n>/ directory. Unioning the on-disk dirs means a "ghost" id-dir — left by a create
    // whose registry write failed (see CreateAsync), so it is not in the live set — is never
    // re-minted, so a new instance can never silently adopt an orphaned store's stale data.
    // Deterministic + restart-stable.
    private int NextInstanceId(string baseDir)
    {
        var maxLive = _instances.Keys.DefaultIfEmpty(0).Max();
        var dir = AppPaths.InstancesDir(baseDir);
        var maxDir = Directory.Exists(dir)
            ? Directory.EnumerateDirectories(dir)
                .Select(d => int.TryParse(Path.GetFileName(d), out var n) ? n : 0)
                .DefaultIfEmpty(0).Max()
            : 0;
        return Math.Max(maxLive, maxDir) + 1;
    }

    public async ValueTask DisposeAsync()
    {
        // Drain by id (TryRemove returns whatever is CURRENTLY under each key, so a fire-and-forget restart
        // that swapped an instance in is still captured + disposed). After draining, a lingering restart's
        // TryUpdate finds no key and unwinds its own host, so nothing leaks. Dispose OUTSIDE the dictionary.
        var instances = new List<HostedInstance>();
        foreach (var id in _instances.Keys.ToList())
            if (_instances.TryRemove(id, out var instance))
                instances.Add(instance);
        RefreshRegistry();
        foreach (var instance in instances)
            await instance.DisposeAsync();
    }
}
