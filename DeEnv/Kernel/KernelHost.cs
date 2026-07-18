using System.Collections.Concurrent;
using System.Net;
using DeEnv.Code;
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

    // The design-host's LIVE store — resolved on demand from the hosted set (mirror-clobber fix — one store
    // instance per data file). Every mirror write (create/delete/rename/clone/setDesign keeping db.instances
    // in lockstep) and the boot design-library sync go through THIS one live store, the SAME instance
    // KernelHostActions now uses (via HostActionsFor's resolveStore) and the live WS session serves from — so
    // a commit through a host action and a mirror write can never be two different in-memory `_db`s racing
    // one file. Was a field cached ONCE at boot: that copy went stale the moment any post-boot commit landed
    // through a (then-fresh) host-action store, and its next mirror `Save()` rewrote the snapshot from the
    // stale `_db` — silently deleting the commit AND colliding WAL seqs. Re-resolving live each call (a
    // cheap dictionary lookup) removes the staleness by construction, and stays correct across a design-host
    // restart (which swaps in a re-opened store). Null when no design-host is present (rare in practice).
    private IInstanceStore? DesignHostStore =>
        _instances.Values.FirstOrDefault(i => IsDesignHost(i.Spec))?.Store;

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
            // The caller's ONE live store, resolved at ACTION time (mirror-clobber fix — one store instance
            // per data file). Was `new JsonFileInstanceStore(spec.SchemaPath, spec.DataPath)` per call inside
            // KernelHostActions; now every action shares the SAME live store the kernel hosts this instance
            // on — so a commit and the db.instances mirror write (below) can no longer be two different
            // in-memory `_db`s racing one file. Lazy because the HostedInstance is registered AFTER this
            // seam is built (boot/create/clone all call HostActionsFor before Register); by action time it is
            // live. The store instance is re-resolved each action, so a rename/restart that swaps the hosted
            // instance's store hands back the CURRENT one. Fresh-store fallback only if the instance somehow
            // isn't registered at call time (defensive — never on the real dispatch path).
            () => _instances.GetValueOrDefault(spec.Id)?.Store
                  ?? new JsonFileInstanceStore(spec.DataPath, InstanceDescriptionLoader.LoadFile(spec.SchemaPath)),
            callerId: spec.Id,
            id => _instances.GetValueOrDefault(id)?.Spec,
            // create projects the caller's schema into a NEW instance via the kernel's own create
            // mechanism, fed the kernel's boot baseDir/registryPath. The new instance is addressed by
            // its NAME (the mount path derives from it) — no ports, since addressing is by path now.
            createInstance: (appDb, name, designId) =>
                CreateAsync(appDb, name, baseDir, registryPath, designId),
            // delete + clone resolve the id → the live hosted instance, then run the kernel's own
            // DeleteAsync/CloneAsync (fed the same boot baseDir/registryPath as create).
            deleteInstance: id => DeleteAsyncById(id, registryPath),
            cloneInstance: (sourceId, atSeq) => CloneAsyncById(sourceId, atSeq, baseDir, registryPath),
            // setDesign records the chosen design id on the target's registry entry (and refreshes the
            // live view so the dropdown re-selects it on the next render).
            recordDesign: (targetId, designId) => SetDesignAsyncById(targetId, designId, registryPath),
            // restart re-reads the updated schema+data from disk and hot-swaps the hosted instance.
            restartInstance: id => RestartAsync(id),
            renameInstance: (id, name) => RenameAsync(id, name, registryPath),
            // M13 slice 4 — the versioning stamp: read the target's current PublishedCommitId (null =
            // unstamped) and persist a NEW one after a successful versioned publish.
            readPublishedCommitId: id => ReadPublishedCommitId(id, registryPath),
            stampPublishedCommit: (id, commitId) => StampPublishedCommitAsync(id, commitId, registryPath),
            // M13 Track-B B3 addendum — the preview→apply consistency guard's target-version half: the
            // TARGET's own live store (by id), the SAME live-hosted-set lookup PublishPreviewFor already
            // reads (never a second store over the file — the mirror-clobber invariant).
            resolveTargetStore: id => _instances.GetValueOrDefault(id)?.Store);

    // The kernel-wired dry-run publish-preview delegate for one instance (M13 Track-B B3) — the read-side
    // sibling of HostActionsFor's `publish` action. Threaded into this instance's render executor (via
    // SsrRenderer), so `sys.publishPreview(design, targetRuntimeId)` computes what a publish onto that target
    // WOULD do — the structured PublishReport — and ships it via the memo cache (a server-backed READ, like
    // sys.diffCommits: NOT a host action). It is READ-ONLY: it computes with `dryRun:true`, so
    // PublishReportComputer.Compute → ApplyPublishBoundary(dryRun:true) touches the target's data file only to
    // READ it (the boundary apply's own dryRun flag skips every disk write) — never a second writer on the
    // target's single-store file.
    //
    // Wired ONLY for the design host (like the real host-action seam) — an ordinary app never previews a
    // publish, and a real delegate exists only where the design library lives. The closure resolves the
    // DESIGNER's own store for the design's commits (the design is a row there — the same store B2 read for
    // diffCommits), the TARGET's spec for its DataPath, and the target's stamp from kernel.json, then calls the
    // shared PublishReportComputer and renders the report as a Code value (a Constant, distinct-negative-id
    // tree, so ClientState ships the whole structure to the client). Non-design-host instances get null →
    // sys.publishPreview simply isn't reachable there.
    private Func<ExecObject, int, ExecContext, IExecValue>? PublishPreviewFor(InstanceSpec spec) =>
        !IsDesignHost(spec) ? null : (design, targetId, context) =>
        {
            // The design host can NEVER preview a publish onto itself (the same single-writer guard the real
            // publish holds — see KernelHostActions' callerId note). The editor's Publish section already
            // filters the designer's own instance out of the target list, so this is defensive.
            if (targetId == spec.Id)
                throw new InvalidOperationException(
                    "The design host cannot preview a publish onto itself — publish deploys a design onto an app instance.");

            var store = _instances.GetValueOrDefault(spec.Id)?.Store
                ?? throw new InvalidOperationException("The design host is not live.");
            var targetInstance = _instances.GetValueOrDefault(targetId)
                ?? throw new InvalidOperationException($"No instance with id {targetId} to preview a publish to.");
            var target = targetInstance.Spec;

            // The design row in the designer's own store (keyed by the ExecObject's intrinsic id — the same
            // db.designs[id] resolution KernelHostActions.ResolveDesign does). A stale/foreign id resolves to
            // no Design and is rejected before any read of the target.
            if (store.ReadById(design.Id) is not (var typeName, var designFields) || typeName != "Design")
                throw new InvalidOperationException($"No design with id {design.Id} in the designer's `designs` set.");

            var stampedCommitId = ReadPublishedCommitId(targetId, registryPath);
            var plan = PublishReportComputer.Compute(
                store, design.Id, designFields, target.DataPath, stampedCommitId, dryRun: true);

            // The preview→apply consistency GUARD token's second half (M13 Track-B B3 addendum): the
            // TARGET's own live store version, read here (not inside PublishReportComputer, which is
            // store-agnostic w.r.t. the target's LIVE version — it only ever touches target.DataPath as a
            // file). Shipped on the report as `targetVersion`; the Apply button passes it straight back to
            // `sys.publish`, which rejects if the target's version has moved since (any write to the target,
            // including its own re-stamp, bumps this — no separate stamp check needed).
            return PublishReportCode.Build(plan.Report, targetInstance.Store.CurrentVersion, context);
        };

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
    private async Task CloneAsyncById(int sourceId, int? atSeq, string baseDir, string registryPath)
    {
        var source = _instances.GetValueOrDefault(sourceId)
            ?? throw new InvalidOperationException($"No instance with id {sourceId} to clone.");
        await CloneAsync(source, baseDir, registryPath, atSeq);
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

    // The instance's CURRENT versioning stamp (M13 slice 4 — RegistryEntry.PublishedCommitId): null =
    // unstamped (a pre-versioning instance, or one whose entry no longer exists — defensive). Read fresh
    // from kernel.json (not the in-memory spec, which carries no stamp) — the same read-the-file pattern
    // every registry-consulting host action already uses.
    public static int? ReadPublishedCommitId(int instanceId, string registryPath) =>
        RegistryReader.Read(registryPath).Instances
            .FirstOrDefault(e => e.Id == instanceId)?.PublishedCommitId;

    // Persist the instance's NEW versioning stamp after a successful versioned publish — a plain
    // read-modify-write of kernel.json (single-operator, like every other registry mutation here). No
    // in-memory spec field to update (PublishedCommitId is registry-only bookkeeping, read fresh by
    // ReadPublishedCommitId whenever a publish needs it — unlike DesignId, nothing else consults it).
    public static Task StampPublishedCommitAsync(int instanceId, int commitId, string registryPath)
    {
        var stored = RegistryReader.Read(registryPath);
        RegistryWriter.Write(registryPath, new Registry(
            [.. stored.Instances.Select(e => e.Id == instanceId ? e with { PublishedCommitId = commitId } : e)]));
        return Task.CompletedTask;
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
                try { Register(HostedInstance.Start(spec, AdvertisedAssetPort, _registry, HostActionsFor(spec), PublishPreviewFor(spec))); }
                catch (Exception ex)
                {
                    _failed[spec.Id] = new FailedInstance(spec, ex);
                    Console.Error.WriteLine(
                        $"KERNEL BOOT FAILURE: instance {spec.Id} '{spec.App}' ({spec.SchemaPath}) failed to " +
                        $"load and is NOT served — /apps/{spec.App} answers 503. Fix its files (or publish a " +
                        $"corrected document) and restart the kernel. {ex}");
                }
            }
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

    // The intrinsic id of the db.instances set on the given (live) design-host store, through the root path.
    // Null when the set isn't there yet. Takes the store so one mirror op resolves DesignHostStore ONCE and
    // every read/write below shares that exact live instance.
    private static int? InstancesSetId(IInstanceStore store) =>
        store.ReadNode(NodePath.Root.Field("instances")) is SetValue sv ? sv.Id : null;

    // Find the Instance object whose runtimeId matches the given kernel id, on the given (live) store.
    private static int? FindInstanceObjectId(IInstanceStore store, int runtimeId)
    {
        foreach (var (id, obj) in store.ReadExtent("Instance"))
            if (obj.Fields.TryGetValue("runtimeId", out var v) && v is IntValue iv && iv.Value == runtimeId)
                return id;
        return null;
    }

    // INSERT a new Instance row into db.instances (after a create or clone kernel action).
    private void MirrorInstanceInsert(int runtimeId, string name, int? designId)
    {
        if (DesignHostStore is not { } store) return;
        var fields = new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name),
            ["runtimeId"] = new IntValue(runtimeId),
        };
        var objId = store.CreateObject("Instance", new ObjectValue(fields));
        // Add to the set BEFORE writing the design reference: WriteReference triggers GC, and a
        // freshly minted object that has not yet been linked to a set would be collected as unreachable.
        if (InstancesSetId(store) is { } setId)
            store.AddToSet(setId, objId);
        // BuildFields ignores provided reference fields (single-object props start unset), so set the
        // design reference as a separate write after the object is safely reachable via the set.
        if (designId.HasValue)
            store.WriteReference(objId, "design", designId, "Design");
    }

    // REMOVE the Instance row matching runtimeId from db.instances (after a delete kernel action).
    private void MirrorInstanceDelete(int runtimeId)
    {
        if (DesignHostStore is not { } store) return;
        if (FindInstanceObjectId(store, runtimeId) is not { } objId) return;
        if (InstancesSetId(store) is { } setId)
            store.RemoveFromSet(setId, objId);
    }

    // UPDATE the name on the Instance row matching runtimeId (after a rename kernel action).
    private void MirrorInstanceRename(int runtimeId, string newName)
    {
        if (DesignHostStore is not { } store) return;
        if (FindInstanceObjectId(store, runtimeId) is not { } objId) return;
        store.WriteField(objId, "name", new TextValue(newName));
    }

    // UPDATE the design ref on the Instance row matching runtimeId (after setDesign kernel action).
    private void MirrorInstanceSetDesign(int runtimeId, int? designId)
    {
        if (DesignHostStore is not { } store) return;
        if (FindInstanceObjectId(store, runtimeId) is not { } objId) return;
        store.WriteReference(objId, "design", designId, "Design");
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
        (string App, int RuntimeId, int? DesignId)[] InstanceTuples(IReadOnlyDictionary<int, int> designIdRemap) =>
            specs
                .OrderBy(s => s.Id)
                .Select(s => (s.App, RuntimeId: s.Id,
                    DesignId: s.DesignId is { } d && designIdRemap.TryGetValue(d, out var actual) ? actual : s.DesignId))
                .ToArray();

        var description = InstanceDescriptionLoader.LoadFile(designHost.SchemaPath);
        var fresh = !File.Exists(designHost.DataPath) || new FileInfo(designHost.DataPath).Length == 0;

        IInstanceStore store;
        if (fresh)
        {
            // The one-time full seed for a brand-new install — nothing exists yet, so InitialData/Reset
            // costs nothing (invariant 6). Every registered design is adopted at its kernel.json designId
            // (the JSON-pool seed CAN pin an arbitrary id — see DesignerSeed.Build's doc), so no remap is
            // needed here. Still falls through to EnsureMainBranches below (invariant 5) —
            // DesignerSeed.Build seeds Design/MetaType/MetaProp/Instance rows only, no Branch rows, so a
            // freshly-seeded design needs one too.
            store = new JsonFileInstanceStore(designHost.DataPath, description with { InitialData = DesignerSeed.Build(fileBacked, InstanceTuples(EmptyRemap)) });
        }
        else
        {
            // An EXISTING store: open it (no Reset — this is the log-preserving path) and adopt only the
            // designs it has never seen before.
            store = new JsonFileInstanceStore(designHost.DataPath, description);
            var seenDesignIds = store.ReadExtent("Design").Keys.ToHashSet();

            // oldDesignId → the id the store ACTUALLY minted for the adopted design (only when they differ).
            // Instance.design references must point at the minted id, not the kernel.json one — so the
            // instances reconcile below reads its tuples' DesignId THROUGH this remap. (A crash between the
            // AdoptInto store write and RewriteDesignIdInRegistry below re-adopts the file as a DUPLICATE on
            // the next boot — the known crash-durability class, deferred deliberately, same as every other
            // "store write then registry write" pair in the kernel.)
            var designIdRemap = new Dictionary<int, int>();

            foreach (var (_, designId, appText) in fileBacked)
            {
                if (!seenDesignIds.Add(designId)) continue; // already adopted this boot (or before) — never touched again

                var adoptedId = DesignerSeed.AdoptInto(store, appText);
                if (adoptedId != designId)
                {
                    // The store minted a different id than kernel.json named (CreateObject cannot pin an
                    // arbitrary id on a live, already-open store — see AdoptInto's doc). Rewrite the
                    // registry entry AND remap this designId so the Instance.design reference resolves to
                    // the id it actually got minted, not the stale kernel.json one (which no longer names
                    // any Design — a dangling reference).
                    RewriteDesignIdInRegistry(registryPath, designId, adoptedId);
                    designIdRemap[designId] = adoptedId;
                }
            }

            ReconcileInstancesSet(store, InstanceTuples(designIdRemap));
        }

        EnsureMainBranches(store, specs, registryPath);
    }

    private static readonly IReadOnlyDictionary<int, int> EmptyRemap = new Dictionary<int, int>();

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

    // Ensure every Design (in `db.designs`) has a `main` Branch WITH a head — invariant 5 (M13 slice 3),
    // extended by slice 4's ADOPTION BASELINE COMMIT: create-if-missing, idempotent (a Design that already
    // has a `main` Branch — WITH or WITHOUT a head — is left untouched; a branch's head, once set, is never
    // overwritten here). A design that gets a FRESH branch also gets a BASELINE Commit (message "Adopted",
    // no parent) — the diff anchor slice 4's publish needs (a target can only be diffed against a
    // PREVIOUSLY-COMMITTED state; without a baseline, every adopted design's first publish would have
    // nothing to diff from). `workingCopy` points at the Design itself (this slice's lean shape — see
    // docs/plans/versioning-slices.md slice 3: "at this slice the working copy IS the design row").
    //
    // Stamping (slice 4's second half): an instance whose OWN app.deenv canonically equals the baseline's
    // printed text (parse+reprint BOTH sides — never a raw-byte compare, since whitespace/section-order
    // artifacts from a hand-edited file must not defeat the match) is stamped to that baseline commit in
    // the registry — so its FIRST real publish after this boot is already identity-diffed, not a fallback.
    // An instance whose text does NOT match (a design edited since that instance last ran, or one with no
    // matching instance at all) is left unstamped; its first publish uses the one-time name-match fallback.
    private static void EnsureMainBranches(IInstanceStore store, IReadOnlyList<InstanceSpec> specs, string registryPath)
    {
        if (store.ReadNode(NodePath.Root.Field("branches")) is not SetValue branchesSet) return; // pre-slice-3 meta (a test fixture) — nothing to ensure
        var commitsSetId = (store.ReadNode(NodePath.Root.Field("commits")) as SetValue)?.Id;
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

            var branchId = store.CreateObject("Branch", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["name"] = new TextValue("main"),
            }));
            store.AddToSet(branchesSet.Id, branchId);
            store.WriteReference(branchId, "workingCopy", designId, "Design");

            // No `db.commits` set at all (a pre-slice-3 test meta) → no baseline to mint; head stays unset,
            // matching the original pre-baseline behavior for such a fixture.
            if (commitsSetId is not { } setId) continue;

            var design = store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString()));
            if (design is null) continue; // defensive — designs came from ReadExtent moments ago
            DesignSnapshot snapshot;
            try { snapshot = SchemaBridge.Snapshot(design, store); }
            catch (SchemaValidationException) { continue; } // an invalid design mints no baseline (nothing to commit)

            // ONE atomic changeset — the Commit row + its design ref + the db.commits link + every idMap
            // entry + the branch-head advance — mirroring sys.commitDesign's own atomicity (KernelHostActions
            // .CommitDesign) exactly, so an adoption baseline is never observable half-written either.
            const int commitTemp = -1;
            var creates = new List<CommitCreate>
            {
                new(commitTemp, "Commit", new ObjectValue(new Dictionary<string, NodeValue>
                {
                    ["message"] = new TextValue("Adopted"),
                    ["at"]      = new DateTimeValue(DateTimeOffset.UtcNow),
                    ["logSeq"]  = new IntValue(store.CurrentVersion),
                    ["text"]    = new TextValue(snapshot.Text),
                    ["migration"] = new TextValue(""),
                })),
            };
            var mutations = new List<CommitMutation>
            {
                new RefSetMutation(commitTemp, "design", designId, "Design"),
                new SetAddMutation(setId, commitTemp),
            };
            foreach (var (path, id) in snapshot.IdMap)
                mutations.Add(new DictAddMutation(commitTemp, "idMap", new TextValue(path), new IntValue(id)));
            mutations.Add(new RefSetMutation(branchId, "head", commitTemp, "Commit"));
            // `parent` stays unset — a baseline commit is the root of its design's history.

            var result = store.CommitBatch(creates, mutations);
            var commitId = result.Creates.Single().RealId;

            StampMatchingInstance(snapshot.Text, commitId, specs, registryPath);
        }
    }

    // Stamp any hosted instance whose app.deenv CANONICALLY equals the baseline's printed text to the
    // baseline commit — comparing CANONICAL forms (parse THEN reprint both sides, via the SAME
    // AppPrint/InstanceDescriptionLoader pipeline every publish already goes through), never raw bytes, so
    // a hand-edited file with different whitespace/section order still matches. An unreadable/unparseable
    // instance file is skipped (never stamped) — a broken file cannot be canonicalized, and boot-time
    // per-instance failure handling (StartAsync's own try/catch) is where that gets reported loudly.
    private static void StampMatchingInstance(
        string baselineText, int commitId, IReadOnlyList<InstanceSpec> specs, string registryPath)
    {
        string? baselineCanonical = null;
        foreach (var spec in specs)
        {
            if (!File.Exists(spec.SchemaPath)) continue;
            string canonical;
            try { canonical = AppPrint.Print(InstanceDescriptionLoader.LoadFile(spec.SchemaPath)); }
            catch { continue; } // unreadable instance file — never stamped, reported (loudly) elsewhere at boot
            baselineCanonical ??= AppPrint.Print(InstanceDescriptionLoader.Load(baselineText));
            if (canonical != baselineCanonical) continue;

            var stored = RegistryReader.Read(registryPath);
            RegistryWriter.Write(registryPath, new Registry(
                [.. stored.Instances.Select(e => e.Id == spec.Id ? e with { PublishedCommitId = commitId } : e)]));
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
        string appDb, string name, string baseDir, string registryPath, int? designId = null)
    {
        // Mint a unique id (max over the live set AND on-disk id-dirs + 1). Its id-dir is
        // instances/<id>/, so it survives a restart and never reuses a directory — even a ghost one.
        var id = NextInstanceId(baseDir);
        var schemaPath = AppPaths.SchemaPathForId(baseDir, id);
        Directory.CreateDirectory(AppPaths.IdDirFor(baseDir, id));
        File.WriteAllText(schemaPath, appDb);

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
        var created = HostedInstance.Start(spec, AdvertisedAssetPort, _registry, HostActionsFor(spec), PublishPreviewFor(spec));
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
    //
    // `atSeq` (M13 slice 7, OPTIONAL) — omitted (null) is BYTE-IDENTICAL to the pre-slice-7 clone (a plain
    // file copy of the source's CURRENT head, below). When given, the clone gets the source's data as it
    // stood at that log seq instead — "the app as of Tuesday" (design doc §0) — under the SCHEMA that was
    // in force at that moment (era resolution, see ResolveEraDb), one fresh fork, no history carried (its
    // own log starts genesis-less — the FIRST real mutation freezes its OWN genesis from the materialized
    // state, exactly like any new instance under slice-1's rules; the design's "fork forever" line).
    public async Task<HostedInstance> CloneAsync(
        HostedInstance source, string baseDir, string registryPath, int? atSeq = null)
    {
        var id = NextInstanceId(baseDir);
        var schemaPath = AppPaths.SchemaPathForId(baseDir, id);
        var dataPath = AppPaths.DataPathForId(baseDir, id);

        int? eraCommitId = null;
        if (atSeq is { } seq)
        {
            // Materialize the source's OWN store at `seq` (validates seq bounds — throws loudly, nothing
            // written yet, on an invalid one) and resolve which schema was in force then. Deliberately
            // BEFORE the id-dir is created (review fix — "nothing was cloned" must be LITERALLY true on any
            // throw here, not just "the registry was untouched": no orphan instances/<id>/ directory left
            // behind either). Every step through Validate can throw; nothing below creates a file or
            // directory until all of them have succeeded.
            //
            // Reads the source's LIVE hosted store (source.Store — mirror-clobber fix: one store instance per
            // data file), NOT a second `new JsonFileInstanceStore` over the same live file. EACH read
            // (MaterializeAtSeq, the boundary scan) is individually locked under the store's `_sync`; the
            // SEQUENCE of reads is not one lock, so a write landing between them could shift the head —
            // benign single-operator (nothing races a clone), part of the deferred concurrency class, and
            // no worse than the fresh-store read it replaced. Do not read this as cross-call atomicity.
            var sourceDesc = InstanceDescriptionLoader.LoadFile(source.Spec.SchemaPath);
            var sourceStore = (JsonFileInstanceStore)source.Store;
            var materialized = sourceStore.MaterializeAtSeq(seq); // throws on an out-of-range/pre-genesis seq

            var (eraDesignText, eraDesc, resolvedCommitId) = ResolveEraDb(sourceStore, sourceDesc, source.Spec.SchemaPath, seq);
            eraCommitId = resolvedCommitId;

            // Validate the materialized store against the ERA schema BEFORE writing anything — a mismatch
            // (e.g. genesis-era data whose schema predates every publish boundary) must fail loudly rather
            // than write an unloadable clone. Same guard HostedInstance.Start's ctor would hit on load —
            // run it explicitly here so the failure names the remedy before any file exists.
            StoredDataValidator.Validate(materialized, eraDesc, source.Spec.DataPath);

            // Every check above passed — now, and only now, does anything touch the filesystem.
            Directory.CreateDirectory(AppPaths.IdDirFor(baseDir, id));
            File.WriteAllText(schemaPath, eraDesignText);
            JsonFileInstanceStore.SaveRaw(dataPath, materialized);
            // Deliberately NOT copying the source's log/genesis — a time-travel clone is a FRESH fork with
            // its own history (design doc §6): its first mutation freezes ITS OWN genesis from this
            // materialized state, exactly like any brand-new instance (JsonFileInstanceStore.EnsureGenesis).
        }
        else
        {
            // Copy the source's files: the app doc always, and the data file when it exists (a brand-new
            // source may not have persisted yet). This copied data is the clone's whole point. Nothing
            // above this point can throw on the atSeq-less path, so creating the dir right before copying
            // (rather than up front) changes nothing observable here — kept in the same place for both
            // branches' symmetry.
            Directory.CreateDirectory(AppPaths.IdDirFor(baseDir, id));
            File.Copy(source.Spec.SchemaPath, schemaPath);
            if (File.Exists(source.Spec.DataPath))
                File.Copy(source.Spec.DataPath, dataPath);
        }

        // Copy the source's whole blob pool (docs/plans/assets-design.md — clone = whole-pool copy, an
        // EXPLICIT step, NOT free composition: neither branch above copies sibling dirs on its own).
        // Runs for BOTH branches identically — even the atSeq materialization can reference OLD hashes
        // still sitting in the source's append-only pool, so the fork needs the whole thing, not just
        // what the current head references. Missing-dir guarded: a pre-pool instance (or one that never
        // received an upload) has no blobs/ dir at all.
        CopyBlobsDir(baseDir, source.Spec.Id, id);

        // A unique mount name (the source's name + a free "-copy[-N]" suffix) — two instances cannot
        // share `/apps/<name>`. The id keeps storage distinct regardless; this keeps the URL distinct.
        var name = UniqueName(source.Spec.App + "-copy");
        var spec = new InstanceSpec(id, name, schemaPath, dataPath, null);

        EnsureNoCollision(
            spec,
            new HashSet<string>(KnownSpecs().Select(s => s.DataPath), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(KnownSpecs().Select(s => s.App), StringComparer.Ordinal));

        var created = HostedInstance.Start(spec, AdvertisedAssetPort, _registry, HostActionsFor(spec), PublishPreviewFor(spec));
        Register(created);

        RegistryWriter.Write(registryPath, new Registry(
            [.. RegistryReader.Read(registryPath).Instances, new RegistryEntry(id, name, null)]));

        MirrorInstanceInsert(id, name, null);

        // Stamp the clone's versioning entry (M13 slice 4's PublishedCommitId) to the era commit it was
        // materialized from — it genuinely IS that design version, so a future publish onto the clone
        // diffs correctly (identity-safe) instead of falling back to the one-time name-match path. A
        // never-published source (no boundary at or before atSeq) leaves the clone unstamped, exactly like
        // today's ordinary (atSeq-less) clone — it was never stamped either.
        if (eraCommitId is { } commitId)
            await StampPublishedCommitAsync(id, commitId, registryPath);

        return created;
    }

    // Copy the WHOLE blob pool from one instance id's dir to another's (docs/plans/assets-design.md —
    // clone's explicit whole-pool-copy step). A no-op when the source has no blobs/ dir yet (pre-pool
    // instance, or one that never received an upload). Flat, non-recursive (the pool itself is flat).
    private static void CopyBlobsDir(string baseDir, int sourceId, int destId)
    {
        var srcBlobs = AppPaths.BlobsDirForId(baseDir, sourceId);
        if (!Directory.Exists(srcBlobs)) return;
        var destBlobs = AppPaths.BlobsDirForId(baseDir, destId);
        Directory.CreateDirectory(destBlobs);
        foreach (var file in Directory.GetFiles(srcBlobs))
            File.Copy(file, Path.Combine(destBlobs, Path.GetFileName(file)), overwrite: true);
    }

    // Era-schema resolution (M13 slice 7): which app document was in force at `atSeq`. The latest publish
    // BOUNDARY marker in the source's OWN log at or before atSeq names the design commit that was live then
    // (BoundaryMarker.CommitId, resolved against the DESIGN HOST's store) — its cached `text` field IS that
    // era's canonical app document (M13 slice 2's per-commit cache).
    //
    // Reads the design host's Commit rows through the LIVE design-host store (DesignHostStore) — the SAME
    // single store KernelHostActions.CommitDesign/Publish/MergeBranch now write through (mirror-clobber fix:
    // one store instance per data file). Before that fix, this read a FRESH `new JsonFileInstanceStore` per
    // call specifically because those actions used their OWN fresh stores and the boot-cached field went
    // stale the moment any post-boot commit landed — that whole staleness class is now gone (the live store
    // always reflects every committed row by construction), so a plain read off the shared live store is
    // correct AND current, and the extra fresh open per clone is deleted.
    //
    // NO boundary at or before atSeq splits into TWO genuinely different cases (collapsing them into one
    // "use current app.deenv" fallback is correct for the first but WRONG for the second — a clone of a
    // seq that predates the instance's ONE-AND-ONLY publish, taken AFTER that publish already landed, would
    // otherwise get the POST-publish schema for a PRE-publish seq):
    //   (a) the instance has NEVER been published, ever (EarliestBoundary() is also null) — it has run the
    //       SAME schema its whole life, so "current app.deenv" trivially IS the era doc (covers
    //       pre-first-publish history and a never-published instance alike).
    //   (b) a LATER publish exists (EarliestBoundary() finds one), just not at-or-before atSeq — the era doc
    //       is the schema that publish DIFFED FROM, read DIRECTLY off that boundary's OWN `BaseCommitId`
    //       (the pre-publish stamped commit KernelHostActions.Publish recorded at boundary-write time — see
    //       BoundaryMarker's own doc). REVIEW FIX (blocks-landing): this is an EXACT lookup, never a
    //       `CommitId.parent` DAG walk — publish diffs the stamped base against the head across an
    //       ARBITRARY commit distance (Publish's own comment on the versioned path), so with a deployed
    //       chain C1 (stamped) ← C2 ← C3 (published), the boundary names C3 and `C3.parent` is C2, NOT the
    //       C1 the instance actually ran — a parent-walk would silently serve C2's schema for a clone that
    //       predates the ENTIRE publish. `BaseCommitId` sidesteps the DAG question entirely: it names C1
    //       directly, because that is literally what the diff's base argument was.
    //
    // Returns (docText, parsedDesc, resolvedCommitId-or-null) — the commit id feeds the clone's
    // PublishedCommitId stamp (case (b)'s base commit genuinely IS that era's design version, so it stamps
    // exactly like case (a)'s direct hit does; only the truly-never-published (a) leaves it null).
    //
    // UNRESOLVABLE (review fix, fail-soon → applied here): a boundary that NAMES a commit which will not
    // resolve (a missing/wrong-type row, empty cached text, or — case (b) specifically — a null
    // `BaseCommitId`, meaning the boundary predates this field and cannot be exactly resolved) is a
    // DIFFERENT state from "no boundary exists at all" (case (a), legitimate) — it must fail LOUDLY, never
    // silently fall through to "current app.deenv" (which would serve a schema the caller never asked for,
    // indistinguishable from a correct resolution). Compaction (design doc §6, not yet built): once
    // promoted per-commit checkpoints exist, a pre-horizon boundary whose commit has been compacted away
    // becomes resolvable again through the checkpoint instead of the live Commit row — this unresolvable
    // path is the recorded residual until then, not a permanent ceiling.
    private (string Text, InstanceDescription Desc, int? CommitId) ResolveEraDb(
        JsonFileInstanceStore sourceStore, InstanceDescription currentDesc, string currentSchemaPath, int atSeq)
    {
        var designHostStore = DesignHostStore;

        if (sourceStore.LatestBoundaryAtOrBefore(atSeq) is { } boundary)
        {
            var text = TryReadCommitText(designHostStore, boundary.CommitId)
                ?? throw new InvalidOperationException(
                    $"Cannot resolve the era schema at seq {atSeq}: publish boundary commit {boundary.CommitId} " +
                    "no longer resolves (missing, wrong type, or empty cached text) — nothing was cloned.");
            return (text, InstanceDescriptionLoader.Load(text), boundary.CommitId);
        }

        if (sourceStore.EarliestBoundary() is { } earliest)
        {
            var baseCommitId = earliest.BaseCommitId
                ?? throw new InvalidOperationException(
                    $"Cannot resolve the era schema at seq {atSeq}: the earliest publish boundary (commit " +
                    $"{earliest.CommitId}) carries no recorded base commit (it predates M13 slice 7) — the " +
                    "pre-publish schema cannot be exactly determined — nothing was cloned.");
            var baseText = TryReadCommitText(designHostStore, baseCommitId)
                ?? throw new InvalidOperationException(
                    $"Cannot resolve the era schema at seq {atSeq}: the publish boundary's base commit " +
                    $"{baseCommitId} no longer resolves (missing, wrong type, or empty cached text) — " +
                    "nothing was cloned.");
            return (baseText, InstanceDescriptionLoader.Load(baseText), baseCommitId);
        }

        // Case (a): no publish anywhere in the log — the current app document IS the era doc.
        return (File.ReadAllText(currentSchemaPath), currentDesc, null);
    }

    // A Commit row's cached `text`, read off the given design-host store by its own intrinsic id. Null
    // when the store is absent, the id does not resolve to a Commit row, or its cached text is empty —
    // every one of these is a genuine "cannot resolve" the caller must fail loudly on (see ResolveEraDb),
    // never silently substitute for. No longer returns a `parent` id (M13 slice 7 review fix — era
    // resolution reads BoundaryMarker.BaseCommitId directly now, never walks the design DAG).
    private static string? TryReadCommitText(IInstanceStore? designHostStore, int commitId)
    {
        if (designHostStore is null) return null;
        if (designHostStore.ReadById(commitId) is not (var typeName, var fields) || typeName != "Commit") return null;
        return fields.Fields.GetValueOrDefault("text") is TextValue { Text.Length: > 0 } textValue
            ? textValue.Text : null;
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
        var renamed = HostedInstance.Start(instance.Spec with { App = name }, AdvertisedAssetPort, _registry, HostActionsFor(instance.Spec with { App = name }), PublishPreviewFor(instance.Spec with { App = name }));
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
            var restarted = HostedInstance.Start(existing.Spec, AdvertisedAssetPort, _registry, HostActionsFor(existing.Spec), PublishPreviewFor(existing.Spec));
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
