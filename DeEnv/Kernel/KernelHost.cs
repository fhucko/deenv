using System.Collections.Concurrent;
using System.Net;
using DeEnv.Designer;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;
using GenHTTP.Api.Infrastructure;
using GenHTTP.Engine.Internal;
using GenHTTP.Modules.Practices;

namespace DeEnv.Kernel;

// The kernel supervisor: in one process, hosts every instance in the registry at once — all behind
// ONE shared app port + ONE shared asset port, each instance addressed by PATH (`/apps/<name>`) with
// its own sovereign store. It owns the multi-instance hosting MECHANISM only (host N instances, bind
// the two kernel ports, route by path, hold the registry); the management EXPERIENCE (create/list/
// delete) is image Code, not C# here — the kernel-vs-image line from DECISIONS ("Multi-instance
// management — the kernel host").
//
// ADDRESSING IS BY PATH, not per-instance ports: two GenHTTP hosts (app + asset) bind the two
// kernel-level ports once, and a PathRouter at the front of each dispatches `apps/<name>/…` to that
// instance's handler (resolved by NAME over the LIVE set, so create/rename/delete re-route with no
// rebind). This dissolves the per-instance-port + two-public-TLS-port deploy complexity (gate #2).
//
// Storage is FULLY ID-BASED: every instance lives under instances/<id>/ and is resolved purely by its
// id; the registry `app` field is the display NAME label that ALSO determines the mount path. There is
// no boot-vs-created distinction — clone/delete/publish all work on ANY instance by its id. (Deleting
// an instance removes its instances/<id>/ dir; the committed app SOURCES are git-tracked, the accepted
// safety net.)
//
// It deliberately does NOT own process lifetime (blocking on Ctrl+C) — the composition root does — so
// it stays a plain start/stop unit that tests can drive synchronously. Constructed with the kernel's
// boot context (the base directory it resolves instances under, the registry file it persists to, and
// the two shared ports) so a Code-triggered create (sys.create) can self-service CreateAsync with the
// same layout + registry as a boot instance.
public sealed class KernelHost(
    string baseDir, string registryPath, int appPort, int assetPort,
    bool bindLoopback = false, int? advertisedAssetPort = null) : IAsyncDisposable
{
    // Keyed by instance id (its unique address + id-dir name). A dictionary, not a list, so every
    // operation addresses an instance by its STABLE id; ConcurrentDictionary is thread-safe per
    // operation, so a fire-and-forget restart (which can outlive its WS message) needs no external lock.
    private readonly ConcurrentDictionary<int, HostedInstance> _instances = new();
    public IReadOnlyList<HostedInstance> Instances => _instances.Values.OrderBy(i => i.Spec.Id).ToList();

    // Instances that FAILED to load at boot, kept aside: never routed as live (not in the registry
    // projection), but their mounts answer an explicit 503 through the front routers — the loud-guards
    // principle applied per instance, so one broken instance is visibly down without taking the kernel
    // or its siblings with it (the 2026-07-02 outage).
    private readonly ConcurrentDictionary<int, FailedInstance> _failed = new();
    public IReadOnlyList<FailedInstance> FailedInstances => _failed.Values.OrderBy(f => f.Spec.Id).ToList();

    private IHandler? FailedHandlerFor(string name) =>
        _failed.Values.FirstOrDefault(f => string.Equals(f.Spec.App, name, StringComparison.Ordinal))?.Handler;

    // Every spec the kernel KNOWS — live AND boot-failed. A failed instance still claims its mount
    // (the 503 stand-in) and its id-dir, so runtime create/clone/rename collision checks must see it:
    // shadowing a broken instance's name would silently serve a different app where the operator
    // expects the failure. Freeing the name is operator-level (fix or remove the instance, restart).
    private IEnumerable<InstanceSpec> KnownSpecs() =>
        _instances.Values.Select(i => i.Spec).Concat(_failed.Values.Select(f => f.Spec));

    // Why the design-host boot sync failed this boot (null = reconciled fine). Kept as kernel-level
    // status for the startup banner — the persistent signal for the one sync failure that fails no
    // instance (a merge bug / transient IO), where the designer serves a STALE design library.
    public string? DesignSyncError { get; private set; }

    public int AppPort => appPort;
    public int AssetPort => assetPort;

    // The asset port the page ADVERTISES to the browser (for /js + the WebSocket). Defaults to the bind
    // port; a reverse-proxied deployment overrides it (DEENV_PUBLIC_ASSET_PORT) so the client dials the
    // proxy's public TLS asset port while this host still binds `assetPort` on the box.
    private int AdvertisedAssetPort => advertisedAssetPort ?? assetPort;

    // The two shared GenHTTP hosts (the app host on appPort, the asset host on assetPort), bound once
    // at StartAsync with a PathRouter front handler each. Null until started.
    private IServerHost? _appHost;
    private IServerHost? _assetHost;

    // The current registry as a live DATA cell (LiveRegistry), shared BY REFERENCE with every hosted
    // instance's renderer. RefreshRegistry swaps `.Current` whenever the hosted set changes, so a
    // render on ANY instance reads the LIVE list — no frozen per-instance snapshot.
    private readonly LiveRegistry _registry = new();

    // The design-host's store: cached at boot so mutating host actions (create/delete/rename/clone/
    // setDesign) can mirror their kernel-registry writes into db.instances in one paired call without
    // re-scanning or re-opening a store. Null when no design-host is present (rare in practice).
    private IInstanceStore? _designHostStore;

    // Track a newly-started instance, then re-project the registry so every instance's next render
    // sees it. The PathRouter resolves by name over `_instances`, so a tracked instance is routable
    // immediately (no host rebind).
    private void Register(HostedInstance instance)
    {
        _instances[instance.Spec.Id] = instance;
        RefreshRegistry();
    }

    // Re-project the live hosted set as registry rows. The instance NAME comes from spec.App (the
    // display label + mount segment); its `path` is `/apps/<name>` (what the operator sees), since
    // there are no per-instance ports anymore.
    private void RefreshRegistry() =>
        _registry.Current = _instances.Values
            .OrderBy(i => i.Spec.Id)
            .Select(i => new InstanceInfo(i.Spec.Id, i.Spec.App, HostedInstance.MountBaseFor(i.Spec.App), i.Spec.DesignId))
            .ToList();

    // Resolve a mount name → the live instance's app/asset handler, for the front routers. By name over
    // the LIVE set each request, so a created/renamed instance routes and a deleted one stops routing.
    private HostedInstance? ByName(string name) =>
        _instances.Values.FirstOrDefault(i => string.Equals(i.Spec.App, name, StringComparison.Ordinal));

    private IReadOnlyList<string> Names() => _instances.Values.Select(i => i.Spec.App).ToList();

    // The host-action seam for one instance. Host actions are OPERATOR devops (create/delete/clone/
    // publish/rename another instance) and run with kernel authority OUTSIDE the per-instance access
    // floor — so an instance gets a REAL KernelHostActions only when its Code actually CALLS a host-action
    // builtin (HostActionScan.UsesHostActions — wiring by AST USE, not by schema SHAPE). Every other
    // instance (an ordinary app like `devlog`, whose WS may be public, and which calls no host action)
    // gets NoHostActions: a `hostAction` frame on its socket is rejected, never executed. This is the
    // WIRING half; the AUTHORITY half is the app's own `sys` access rule, re-checked in
    // WsHandler.HandleHostAction before every dispatch (AccessFloor.CanHostAction) — so even a wired
    // instance denies host actions unless its `sys` rule grants the caller. A miss here fails closed (an
    // unwired seam errors); a false hit is gated by that rule. Parsing failures fail closed too (no seam).
    private IHostActions HostActionsFor(InstanceSpec spec) =>
        !UsesHostActions(spec)
            ? new NoHostActions("this instance does not use host actions")
            : new KernelHostActions(
            spec.SchemaPath, spec.DataPath,
            id => _instances.GetValueOrDefault(id)?.Spec,
            // create projects the caller's schema into a NEW instance via the kernel's own create
            // mechanism, fed the kernel's boot baseDir/registryPath. The new instance is addressed by
            // its NAME (the mount path derives from it) — no ports, since addressing is by path now.
            createInstance: (appDoc, name, designId) =>
                CreateAsync(appDoc, name, baseDir, registryPath, designId),
            // delete + clone resolve the id → the live hosted instance, then run the kernel's own
            // DeleteAsync/CloneAsync (fed the same boot baseDir/registryPath as create).
            deleteInstance: id => DeleteAsyncById(id, registryPath),
            cloneInstance: sourceId => CloneAsyncById(sourceId, baseDir, registryPath),
            // setDesign records the chosen design id on the target's registry entry (and refreshes the
            // live view so the dropdown re-selects it on the next render).
            recordDesign: (targetId, designId) => SetDesignAsyncById(targetId, designId, registryPath),
            // restart re-reads the updated schema+data from disk and hot-swaps the hosted instance.
            restartInstance: id => RestartAsync(id),
            renameInstance: (id, name) => RenameAsync(id, name, registryPath));

    // Resolve an instance id → the live hosted instance and delete it. An id matching no instance is a
    // clear reject before any work. Every instance is deletable now.
    private async Task DeleteAsyncById(int id, string registryPath)
    {
        var instance = _instances.GetValueOrDefault(id)
            ?? throw new InvalidOperationException($"No instance with id {id} to delete.");
        await DeleteAsync(instance, registryPath);
    }

    // Resolve a source instance id → the live hosted instance and clone it. An unknown id is a clear
    // reject. Any instance is a valid clone source — clone only READS its files (see CloneAsync).
    private async Task CloneAsyncById(int sourceId, string baseDir, string registryPath)
    {
        var source = _instances.GetValueOrDefault(sourceId)
            ?? throw new InvalidOperationException($"No instance with id {sourceId} to clone.");
        await CloneAsync(source, baseDir, registryPath);
    }

    // Resolve a target instance id → the live hosted instance and record which design it now runs (the
    // registry-write half of the IDE's Apply; KernelHostActions runs the publish projection).
    private Task SetDesignAsyncById(int targetId, int designId, string registryPath)
    {
        var target = _instances.GetValueOrDefault(targetId)
            ?? throw new InvalidOperationException($"No instance with id {targetId} to set a design on.");
        SetDesign(target, designId, registryPath);
        return Task.CompletedTask;
    }

    // Record the design an instance runs: update its in-memory spec, re-project the live view, and
    // persist the new DesignId to kernel.json. The instance is NOT rebuilt — DesignId is registry
    // metadata; the IDE's Apply pairs this with a publish that does the actual document/data deploy.
    public void SetDesign(HostedInstance instance, int designId, string registryPath)
    {
        instance.SetDesignId(designId);
        RefreshRegistry();

        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances.Select(e =>
                e.Id == instance.Spec.Id ? e with { DesignId = designId } : e)]));

        MirrorInstanceSetDesign(instance.Spec.Id, designId);
    }

    // Resolve each registry entry to a hosting spec: the schema/data paths are derived PURELY from the
    // entry's id (instances/<id>/app.app + app-data.json), never from the `app` name (carried through
    // as the display label + mount segment). Resolution lives here, off the registry shape.
    public static IReadOnlyList<InstanceSpec> SpecsFor(Registry registry, string baseDir)
    {
        var specs = registry.Instances
            .Select(e => new InstanceSpec(
                e.Id,
                e.App,
                AppPaths.SchemaPathForId(baseDir, e.Id),
                AppPaths.DataPathForId(baseDir, e.Id),
                e.DesignId))
            .ToList();
        EnsureNoCollisions(specs);
        return specs;
    }

    // Fail loudly on a registry that would alias resources across instances. Two instances resolving
    // to the SAME data file would silently break data sovereignty; two instances with the SAME mount
    // NAME would collide on `/apps/<name>` (the second would be unreachable). Both are caught upfront.
    private static void EnsureNoCollisions(IReadOnlyList<InstanceSpec> specs)
    {
        var dataPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in specs)
            EnsureNoCollision(s, dataPaths, names);
    }

    // Check one candidate spec against the data files + mount names already claimed (the accumulators
    // are mutated to include it). Shared by boot and CreateAsync's check against the LIVE set.
    private static void EnsureNoCollision(InstanceSpec spec, HashSet<string> dataPaths, HashSet<string> names)
    {
        if (!dataPaths.Add(spec.DataPath))
            throw new KernelConfigException(
                $"Two instances resolve to the same data file '{spec.DataPath}' — each instance needs " +
                "its own store. Give them distinct ids.");

        if (!names.Add(spec.App))
            throw new KernelConfigException(
                $"Two instances share the mount name '{spec.App}' — each instance is served at " +
                "/apps/<name>, so names must be unique. Give them distinct names.");
    }

    // Start the kernel: bind the two shared hosts (app + asset) with a PathRouter front handler each,
    // then load every instance and register it (the routers resolve them by name). Instance loads are
    // INDIVIDUALLY guarded — a bad instance is skipped loudly, never aborting the kernel (the
    // 2026-07-02 outage: one stale designer doc took every hosted app down). Only a kernel-level
    // failure (a host failing to bind) stops the boot, with the hosts stopped so no port leaks.
    public async Task StartAsync(IReadOnlyList<InstanceSpec> specs)
    {
        // BOOT SYNC of the operator IDE's design library, BEFORE any store is opened (the design-host
        // is reconciled with the current app files). The one sanctioned bootstrap-only peer-file read.
        // Non-fatal: a sync that cannot seed/merge (a stale designer doc/data after a binary update)
        // is logged loudly and skipped — the designer then fails or serves a stale library
        // PER-INSTANCE below, while every other app boots.
        try { SyncDesignHost(specs, registryPath); }
        catch (Exception ex)
        {
            DesignSyncError = ex.Message;
            Console.Error.WriteLine(
                $"KERNEL BOOT: design-host sync failed — the design library was NOT reconciled this boot. {ex}");
        }

        // The two shared hosts: one front PathRouter each, resolving by name over the live `_instances`.
        // The app router dispatches to each instance's APP handler (SSR); the asset router to its ASSET
        // handler (/ws + /js). Built before the instances load — an early request just 404s until the
        // instance is registered (loads are fast and synchronous below).
        var appRouter = new PathRouter(n => ByName(n)?.AppHandler ?? FailedHandlerFor(n), Names);
        var assetRouter = new PathRouter(n => ByName(n)?.AssetHandler ?? FailedHandlerFor(n), Names);

        _assetHost = WithBinding(Host.Create()
            .Handler(assetRouter)
            .Defaults(secureUpgrade: false, strictTransport: false), assetPort);

        _appHost = WithBinding(Host.Create()
            .Handler(appRouter)
            // Plain HTTP: no HTTPS endpoint, so don't upgrade/redirect.
            .Defaults(secureUpgrade: false, strictTransport: false), appPort);

        try
        {
            await _assetHost.StartAsync();
            await _appHost.StartAsync();

            // Load every instance (open its store, build its handlers) and register it. Loads are
            // independent and INDIVIDUALLY guarded: an instance that cannot load (unparseable doc,
            // stale data tripping the storage guard) is skipped LOUDLY — the full error to stderr with
            // the remedy, its mount answering 503 via the failed set — while every other instance
            // serves. Each instance shares the LIVE registry provider and gets its own host-action
            // seam (a lazy closure over the hosted set).
            foreach (var spec in specs)
            {
                try { Register(HostedInstance.Start(spec, AdvertisedAssetPort, _registry, HostActionsFor(spec))); }
                catch (Exception ex)
                {
                    _failed[spec.Id] = new FailedInstance(spec, ex);
                    Console.Error.WriteLine(
                        $"KERNEL BOOT FAILURE: instance {spec.Id} '{spec.App}' ({spec.SchemaPath}) failed to " +
                        $"load and is NOT served — /apps/{spec.App} answers 503. Fix its files (or publish a " +
                        $"corrected document) and restart the kernel. {ex}");
                }
            }

            // Cache the design-host's store now that all instances are live, so mutating host actions
            // can mirror writes into db.instances without re-scanning.
            _designHostStore = _instances.Values.FirstOrDefault(i => IsDesignHost(i.Spec))?.Store;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    // Bind a built host to its port: loopback-only when DEENV_BIND=loopback (a reverse proxy then owns
    // the public interface and the raw app/asset ports never leave the box), else all interfaces — the
    // local/dev default. Backward compatible: unset → bind every interface as before.
    private IServerHost WithBinding(IServerHost host, int port) =>
        bindLoopback
            ? host.Bind(IPAddress.Loopback, (ushort)port, dualStack: false)
            : host.Port((ushort)port);

    // ── db.instances mirror — keep the design-host's db.instances in lockstep with kernel actions ──

    // The intrinsic id of the db.instances set — read once per call through the root path.
    // Null when the design-host store is absent or the set isn't there yet.
    private int? InstancesSetId() =>
        _designHostStore?.ReadNode(NodePath.Root.Field("instances")) is SetValue sv ? sv.Id : null;

    // Find the Instance object whose runtimeId matches the given kernel id.
    private int? FindInstanceObjectId(int runtimeId)
    {
        if (_designHostStore is null) return null;
        foreach (var (id, obj) in _designHostStore.ReadExtent("Instance"))
            if (obj.Fields.TryGetValue("runtimeId", out var v) && v is IntValue iv && iv.Value == runtimeId)
                return id;
        return null;
    }

    // INSERT a new Instance row into db.instances (after a create or clone kernel action).
    private void MirrorInstanceInsert(int runtimeId, string name, int? designId)
    {
        if (_designHostStore is null) return;
        var fields = new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name),
            ["runtimeId"] = new IntValue(runtimeId),
        };
        var objId = _designHostStore.CreateObject("Instance", new ObjectValue(fields));
        // Add to the set BEFORE writing the design reference: WriteReference triggers GC, and a
        // freshly minted object that has not yet been linked to a set would be collected as unreachable.
        if (InstancesSetId() is { } setId)
            _designHostStore.AddToSet(setId, objId);
        // BuildFields ignores provided reference fields (single-object props start unset), so set the
        // design reference as a separate write after the object is safely reachable via the set.
        if (designId.HasValue)
            _designHostStore.WriteReference(objId, "design", designId, "Design");
    }

    // REMOVE the Instance row matching runtimeId from db.instances (after a delete kernel action).
    private void MirrorInstanceDelete(int runtimeId)
    {
        if (_designHostStore is null) return;
        if (FindInstanceObjectId(runtimeId) is not { } objId) return;
        if (InstancesSetId() is { } setId)
            _designHostStore.RemoveFromSet(setId, objId);
    }

    // UPDATE the name on the Instance row matching runtimeId (after a rename kernel action).
    private void MirrorInstanceRename(int runtimeId, string newName)
    {
        if (_designHostStore is null) return;
        if (FindInstanceObjectId(runtimeId) is not { } objId) return;
        _designHostStore.WriteField(objId, "name", new TextValue(newName));
    }

    // UPDATE the design ref on the Instance row matching runtimeId (after setDesign kernel action).
    private void MirrorInstanceSetDesign(int runtimeId, int? designId)
    {
        if (_designHostStore is null) return;
        if (FindInstanceObjectId(runtimeId) is not { } objId) return;
        _designHostStore.WriteReference(objId, "design", designId, "Design");
    }

    // ── boot sync of the design library (the design-host's `db.designs`) ──────────────
    //
    // M13 slice 3 — THE AUTHORITY INVERSION. Design-data + its commit history are now the source of
    // truth; an app's `.deenv` FILE is a PUBLISH ARTIFACT (written BY publish), not authoritative input.
    // So this is no longer a boot-time re-seed:
    //   • A FRESH store (no data file at all — a brand-new install) still gets a full `Build` seed via
    //     InitialData — this IS the one-time adoption for everything present, and there is no prior log
    //     to lose (invariant 6).
    //   • An EXISTING store is NEVER reseeded/Reset again. A file-backed design NOT YET a Design row is
    //     ADOPTED — exactly ONCE — via ordinary LIVE store writes (DesignerSeed.AdoptInto), never
    //     touching InitialData/Reset (which would re-freeze genesis and TRUNCATE the log — the very
    //     boot-wipe this inversion ends). Every design already present stays untouched.
    // `db.instances` keeps mirroring the kernel registry every boot regardless (ReconcileInstancesSet) —
    // it is runtime state, not versioned design data, so it is split OUT of the design-adoption path and
    // reconciled via the SAME live-write primitives the runtime host actions already mirror through.
    // Every Design (freshly adopted or pre-existing) gets a `main` Branch if it lacks one (invariant 5,
    // EnsureMainBranches) — idempotent, never recreated once present.
    private void SyncDesignHost(IReadOnlyList<InstanceSpec> specs, string registryPath)
    {
        var designHost = specs.FirstOrDefault(IsDesignHost);
        if (designHost is null) return;

        var fileBacked = specs
            .Where(s => s.DesignId is not null)
            .OrderBy(s => s.Id)
            .Select(s => (s.App, DesignId: s.DesignId!.Value, AppText: File.ReadAllText(s.SchemaPath)))
            .ToList();

        // Build the instance tuples: one per hosted spec (every instance, with or without a design).
        // `App` is the display name; `Id` is the kernel runtime id; `DesignId` is the design reference
        // (0 = not-yet-hosted, carried as null here so the reference is omitted from the seed).
        var instanceTuples = specs
            .OrderBy(s => s.Id)
            .Select(s => (s.App, RuntimeId: s.Id, DesignId: s.DesignId))
            .ToList();

        var description = InstanceDescriptionLoader.LoadFile(designHost.SchemaPath);
        var fresh = !File.Exists(designHost.DataPath) || new FileInfo(designHost.DataPath).Length == 0;

        IInstanceStore store;
        if (fresh)
        {
            // The one-time full seed for a brand-new install — nothing exists yet, so InitialData/Reset
            // costs nothing (invariant 6). Every registered design is adopted at its kernel.json designId
            // (the JSON-pool seed CAN pin an arbitrary id — see DesignerSeed.Build's doc). Still falls
            // through to EnsureMainBranches below (invariant 5) — DesignerSeed.Build seeds Design/MetaType/
            // MetaProp/Instance rows only, no Branch rows, so a freshly-seeded design needs one too.
            store = new JsonFileInstanceStore(designHost.DataPath, description with { InitialData = DesignerSeed.Build(fileBacked, instanceTuples) });
        }
        else
        {
            // An EXISTING store: open it (no Reset — this is the log-preserving path) and adopt only the
            // designs it has never seen before.
            store = new JsonFileInstanceStore(designHost.DataPath, description);
            var seenDesignIds = store.ReadExtent("Design").Keys.ToHashSet();

            foreach (var (_, designId, appText) in fileBacked)
            {
                if (!seenDesignIds.Add(designId)) continue; // already adopted this boot (or before) — never touched again

                var adoptedId = DesignerSeed.AdoptInto(store, appText);
                if (adoptedId != designId)
                    // The store minted a different id than kernel.json named (CreateObject cannot pin an
                    // arbitrary id on a live, already-open store — see AdoptInto's doc). Rewrite the
                    // registry entry so future resolution (KernelHostActions.ResolveDesign, the IDE's
                    // dropdown) finds the design at the id it actually got minted.
                    RewriteDesignIdInRegistry(registryPath, designId, adoptedId);
            }

            ReconcileInstancesSet(store, instanceTuples);
        }

        EnsureMainBranches(store);
    }

    // Rewrite the registry entry(ies) whose designId is `oldId` to `newId` — the adoption-mismatch
    // follow-up (SyncDesignHost). Single-operator, so a plain read-modify-write like every other
    // registry mutation (RegistryWriter's own doc); at most one entry should ever match (each hosted
    // instance names its OWN designId), but every match is corrected defensively.
    private static void RewriteDesignIdInRegistry(string registryPath, int oldId, int newId)
    {
        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances.Select(e => e.DesignId == oldId ? e with { DesignId = newId } : e)]));
    }

    // Reconcile `db.instances` to EXACTLY match the given (App, RuntimeId, DesignId) tuples — the
    // runtime-state mirror that runs every boot regardless of design adoption (invariant 4, split OUT of
    // the design-data path). Insert a missing Instance row, update a changed name/design reference on an
    // existing one, and remove a row whose runtimeId no longer names a hosted instance — all via the SAME
    // live-write primitives (CreateObject/AddToSet/WriteField/WriteReference/RemoveFromSet) the runtime
    // mirror methods (MirrorInstanceInsert et al.) use for a single change; this is their boot-time,
    // whole-set counterpart.
    private static void ReconcileInstancesSet(IInstanceStore store, IReadOnlyList<(string App, int RuntimeId, int? DesignId)> instances)
    {
        if (store.ReadNode(NodePath.Root.Field("instances")) is not SetValue instancesSet) return;
        var byRuntimeId = store.ReadExtent("Instance")
            .Where(e => e.Value.Fields.GetValueOrDefault("runtimeId") is IntValue)
            .ToDictionary(e => ((IntValue)e.Value.Fields["runtimeId"]).Value, e => e.Key);

        var wanted = instances.Select(i => i.RuntimeId).ToHashSet();

        // Remove any Instance row whose runtimeId is no longer among the live specs (an instance the
        // kernel no longer hosts — e.g. deleted between boots by hand-editing the registry).
        foreach (var (runtimeId, objId) in byRuntimeId)
            if (!wanted.Contains(runtimeId))
                store.RemoveFromSet(instancesSet.Id, objId);

        foreach (var (name, runtimeId, designId) in instances)
        {
            if (byRuntimeId.TryGetValue(runtimeId, out var objId))
            {
                // Existing row: update its name/design reference if they drifted (a rename/setDesign that
                // happened between boots via a hand-edited registry, or the first sync ever to see a
                // preexisting hosted instance land its design reference). ReadById's Fields is an
                // ObjectValue whose OWN .Fields dictionary holds the public NodeValue model (a single
                // reference reads back as ReferenceValue), never the store's internal representation.
                if (store.ReadById(objId) is { } hit)
                {
                    if (hit.Fields.Fields.GetValueOrDefault("name") is not TextValue { Text: var n } || n != name)
                        store.WriteField(objId, "name", new TextValue(name));

                    var currentDesignId = hit.Fields.Fields.GetValueOrDefault("design") is ReferenceValue { TargetId: { } t } ? t : (int?)null;
                    if (currentDesignId != designId)
                        store.WriteReference(objId, "design", designId, "Design");
                }
                continue;
            }

            // Missing row: insert it (mirrors MirrorInstanceInsert — link into the set BEFORE the
            // reference write, since WriteReference triggers GC and an unlinked fresh object would be
            // collected as unreachable).
            var fields = new Dictionary<string, NodeValue>
            {
                ["name"] = new TextValue(name),
                ["runtimeId"] = new IntValue(runtimeId),
            };
            var newId = store.CreateObject("Instance", new ObjectValue(fields));
            store.AddToSet(instancesSet.Id, newId);
            if (designId.HasValue)
                store.WriteReference(newId, "design", designId, "Design");
        }
    }

    // Ensure every Design (in `db.designs`) has a `main` Branch (invariant 5, M13 slice 3) — create-if-
    // missing, idempotent: a Design that already has ANY Branch named "main" is left untouched (never
    // recreated/reset). `head` starts EMPTY (null) — no adoption commit mints in this slice (a design
    // freshly adopted from a file has no commit history yet; slice 4 decides whether adoption itself
    // should mint a baseline commit). `workingCopy` points at the Design itself (this slice's lean shape —
    // see docs/plans/versioning-slices.md slice 3: "at this slice the working copy IS the design row").
    private static void EnsureMainBranches(IInstanceStore store)
    {
        if (store.ReadNode(NodePath.Root.Field("branches")) is not SetValue branchesSet) return; // pre-slice-3 meta (a test fixture) — nothing to ensure
        var designs = store.ReadExtent("Design");
        var existingBranches = store.ReadExtent("Branch");

        var designIdsWithMainBranch = existingBranches.Values
            .Where(b => b.Fields.GetValueOrDefault("name") is TextValue { Text: "main" })
            .Select(b => b.Fields.GetValueOrDefault("workingCopy") is ReferenceValue { TargetId: { } t } ? t : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        foreach (var designId in designs.Keys)
        {
            if (designIdsWithMainBranch.Contains(designId)) continue;

            var fields = new Dictionary<string, NodeValue>
            {
                ["name"] = new TextValue("main"),
            };
            var branchId = store.CreateObject("Branch", new ObjectValue(fields));
            store.AddToSet(branchesSet.Id, branchId);
            store.WriteReference(branchId, "workingCopy", designId, "Design");
            // `head` stays unset (empty) — no commit exists yet for a freshly-adopted or pre-slice-3 design.
        }
    }

    // Whether an instance's Code CALLS a host-action builtin (sys.create/delete/…): the WIRING signal for
    // HostActionsFor. Parses the instance's app document and scans ALL its Code (HostActionScan) — wiring
    // by actual USE, not by schema shape. Fails CLOSED on a parse error (no seam) — a broken app cannot
    // acquire kernel authority. This is a DIFFERENT question from IsDesignHost (below), which asks where
    // db.instances lives (a data question); an app can use host actions without the design-host shape.
    private static bool UsesHostActions(InstanceSpec spec)
    {
        InstanceDescription desc;
        try { desc = InstanceDescriptionLoader.LoadFile(spec.SchemaPath); }
        catch { return false; }
        return Code.HostActionScan.UsesHostActions(desc);
    }

    // Whether an instance is the design-host: its schema declares the meta-schema — a `Db` whose
    // `designs` prop is a `set of Design`, where `Design` is a declared object type. This answers a DATA
    // question — "where does db.instances live" (the _designHostStore selection + the boot design-library
    // sync) — NOT authority: host-action AUTHORITY is the `sys` access rule, and WIRING is UsesHostActions
    // (AST use). Kept because those two data uses still need to find the single design-host store.
    private static bool IsDesignHost(InstanceSpec spec)
    {
        InstanceDescription desc;
        try { desc = InstanceDescriptionLoader.LoadFile(spec.SchemaPath); }
        catch { return false; }

        return desc.Db()?.Props?.Any(p =>
                   p.Name == "designs"
                   && p.Cardinality == Cardinality.Set
                   && desc.IsObjectType(p.Type)
                   && p.Type == "Design")
               == true;
    }

    // Create one new instance in a RUNNING kernel and persist it to the registry. The kernel's hosting
    // MECHANISM only — the create COMMAND/UI is image Code over the registry-as-data. The app document
    // is supplied as content; storage is keyed by a kernel-minted id, never a user-chosen file name. The
    // instance is addressed by its `name` (the mount path `/apps/<name>` derives from it) — NO ports,
    // since every instance is served under the kernel's shared app + asset ports. `designId` is the id
    // of the IDE design this instance was spawned from (null when none), recorded on the new entry.
    public Task<HostedInstance> CreateAsync(
        string appDoc, string name, string baseDir, string registryPath, int? designId = null)
    {
        // Mint a unique id (max over the live set AND on-disk id-dirs + 1). Its id-dir is
        // instances/<id>/, so it survives a restart and never reuses a directory — even a ghost one.
        var id = NextInstanceId(baseDir);
        var schemaPath = AppPaths.SchemaPathForId(baseDir, id);
        Directory.CreateDirectory(AppPaths.IdDirFor(baseDir, id));
        File.WriteAllText(schemaPath, appDoc);

        var spec = new InstanceSpec(id, name, schemaPath, AppPaths.DataPathForId(baseDir, id), designId);

        // Collision-check the new spec against every KNOWN data file + mount name — live and
        // boot-failed (same named errors as boot), so a created instance can never alias a running
        // one's store or mount path, nor shadow a failed instance's 503 mount.
        EnsureNoCollision(
            spec,
            new HashSet<string>(KnownSpecs().Select(s => s.DataPath), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(KnownSpecs().Select(s => s.App), StringComparer.Ordinal));

        // Load it (build its handlers) and register — the front routers resolve it by name immediately,
        // and it shows up on EVERY instance's next render (the shared LIVE registry). No host bind.
        var created = HostedInstance.Start(spec, AdvertisedAssetPort, _registry, HostActionsFor(spec));
        Register(created);

        // Persist: append the created entry and rewrite kernel.json, so the instance reappears on the
        // next boot via SpecsFor. (The ghost caveat — a create whose registry WRITE throws after the
        // load — is the deferred concurrent-write milestone, acceptable single-operator.)
        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances, new RegistryEntry(id, name, designId)]));

        MirrorInstanceInsert(id, name, designId);

        return Task.FromResult(created);
    }

    // Clone one instance in a RUNNING kernel: copy its app document AND its data into a NEW instance,
    // then persist it. The data-carrying sibling of CreateAsync. Any source is fine to clone: we only
    // READ its files. The clone keeps the source's display name with a "-copy" suffix so the mount name
    // stays unique (two instances cannot share `/apps/<name>`); the operator can rename it after.
    public Task<HostedInstance> CloneAsync(HostedInstance source, string baseDir, string registryPath)
    {
        var id = NextInstanceId(baseDir);
        var schemaPath = AppPaths.SchemaPathForId(baseDir, id);
        var dataPath = AppPaths.DataPathForId(baseDir, id);
        Directory.CreateDirectory(AppPaths.IdDirFor(baseDir, id));

        // Copy the source's files: the app doc always, and the data file when it exists (a brand-new
        // source may not have persisted yet). This copied data is the clone's whole point.
        File.Copy(source.Spec.SchemaPath, schemaPath);
        if (File.Exists(source.Spec.DataPath))
            File.Copy(source.Spec.DataPath, dataPath);

        // A unique mount name (the source's name + a free "-copy[-N]" suffix) — two instances cannot
        // share `/apps/<name>`. The id keeps storage distinct regardless; this keeps the URL distinct.
        var name = UniqueName(source.Spec.App + "-copy");
        var spec = new InstanceSpec(id, name, schemaPath, dataPath, null);

        EnsureNoCollision(
            spec,
            new HashSet<string>(KnownSpecs().Select(s => s.DataPath), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(KnownSpecs().Select(s => s.App), StringComparer.Ordinal));

        var created = HostedInstance.Start(spec, AdvertisedAssetPort, _registry, HostActionsFor(spec));
        Register(created);

        RegistryWriter.Write(registryPath, new Registry(
            [.. RegistryReader.Read(registryPath).Instances, new RegistryEntry(id, name, null)]));

        MirrorInstanceInsert(id, name, null);

        return Task.FromResult(created);
    }

    // A mount name not currently in use: `desired`, else `desired-2`, `desired-3`, … The mount path
    // `/apps/<name>` must be unique across every KNOWN mount (live + boot-failed), so a clone of an
    // already-cloned instance still gets a distinct URL.
    private string UniqueName(string desired)
    {
        var taken = new HashSet<string>(KnownSpecs().Select(s => s.App), StringComparer.Ordinal);
        if (!taken.Contains(desired)) return desired;
        for (var n = 2; ; n++)
            if (!taken.Contains($"{desired}-{n}")) return $"{desired}-{n}";
    }

    // Delete one instance from a RUNNING kernel and forget it: drop it from the live set (the front
    // routers stop resolving its name), remove its registry entry, and delete its id-dir (app doc +
    // co-located store). No host to stop — addressing is by path now, so removing it from the routed
    // set is the "stop serving" action. Works on ANY instance by its id (the committed app SOURCES are
    // git-tracked, the accepted safety net for the data this drops).
    public Task DeleteAsync(HostedInstance instance, string registryPath)
    {
        // Drop it from the routed set first (the LiveRegistry whole-snapshot swap keeps a concurrent
        // render thread safe), then rewrite the registry and collect its store dir.
        _instances.TryRemove(instance.Spec.Id, out _);
        RefreshRegistry();

        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances.Where(e => e.Id != instance.Spec.Id)]));

        MirrorInstanceDelete(instance.Spec.Id);

        var idDir = AppPaths.IdDirFor(BaseDirOf(instance), instance.Spec.Id);
        if (Directory.Exists(idDir))
            Directory.Delete(idDir, recursive: true);

        return Task.CompletedTask;
    }

    // Rename an instance's display label in a RUNNING kernel — which, since addressing is by path, is a
    // RE-MOUNT (the path becomes `/apps/<new>`). Rebuild the instance's handlers with the new mount base
    // (cheap — no port bind), swap it in by id, re-project, and rewrite kernel.json. Reject a name that
    // collides with another live instance's mount name (the routers resolve by name, so a duplicate
    // would shadow). The hosts are NOT touched — only this instance's handlers are rebuilt.
    public Task RenameAsync(int id, string name, string registryPath)
    {
        var instance = _instances.GetValueOrDefault(id)
            ?? throw new InvalidOperationException($"No instance with id {id} to rename.");

        if (KnownSpecs().Any(s => s.Id != id && string.Equals(s.App, name, StringComparison.Ordinal)))
            throw new KernelConfigException(
                $"Another instance is already mounted at the name '{name}' — each instance is served at " +
                "/apps/<name>, so names must be unique.");

        // Rebuild with the new mount base so emitted links/assets use `/apps/<name>`. Same id-dir + store.
        var renamed = HostedInstance.Start(instance.Spec with { App = name }, AdvertisedAssetPort, _registry, HostActionsFor(instance.Spec with { App = name }));
        if (!_instances.TryUpdate(id, renamed, instance))
            throw new InvalidOperationException($"Instance {id} changed during the rename.");
        RefreshRegistry();

        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances.Select(e => e.Id == id ? e with { App = name } : e)]));

        MirrorInstanceRename(id, name);
        return Task.CompletedTask;
    }

    // Restart one instance in a RUNNING kernel: re-read its now-updated schema and data from disk
    // (written by a preceding publish/setDesign) and rebuild its handlers, swapping them in by id. The
    // front routers resolve by name over `_instances`, so the swap is picked up on the next request with
    // no host rebind. Called fire-and-forget after every publish/setDesign so the live instance reflects
    // the deployed version. Self-restart (the designer publishing to itself) is fine — there is no host
    // to stop, just a handler swap.
    public Task RestartAsync(int id)
    {
        try
        {
            if (!_instances.TryGetValue(id, out var existing)) return Task.CompletedTask;
            var restarted = HostedInstance.Start(existing.Spec, AdvertisedAssetPort, _registry, HostActionsFor(existing.Spec));
            if (_instances.TryUpdate(id, restarted, existing))
                RefreshRegistry();
            // If it was deleted / re-swapped during the rebuild, just drop the rebuilt handlers (GC).
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Background restart of instance {id} failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    // The base directory an instance was hosted from, recovered from its schema path
    // (<baseDir>/instances/<id>/app.app → two levels up from the schema dir).
    private static string BaseDirOf(HostedInstance instance)
    {
        var schemaDir = Path.GetDirectoryName(instance.Spec.SchemaPath)!; // <baseDir>/instances/<id>
        return Path.GetDirectoryName(Path.GetDirectoryName(schemaDir)!)!; // <baseDir>
    }

    // The next unique instance id: one past the max of BOTH the live hosted set AND any on-disk
    // instances/<n>/ directory (so a "ghost" id-dir is never re-minted). Deterministic + restart-stable.
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
        // Stop the two shared hosts (frees the kernel ports). The instances hold no hosts of their own,
        // so dropping them from the dictionary is enough; clear it so a lingering restart's TryUpdate
        // finds no key and drops its rebuilt handlers.
        _instances.Clear();
        _failed.Clear();
        DesignSyncError = null;
        RefreshRegistry();
        if (_appHost is not null) await _appHost.StopAsync();
        if (_assetHost is not null) await _assetHost.StopAsync();
        _appHost = null;
        _assetHost = null;
    }
}
