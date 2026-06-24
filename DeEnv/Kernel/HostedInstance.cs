using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;

namespace DeEnv.Kernel;

// A fully-resolved instruction to host one instance: its stable unique id, its display name, and where
// its app document and data file live. `Id` is the instance's address for clone/delete/publish and the
// sole key to its files; `App` is a display NAME label that ALSO determines its mount path (`/apps/<App>`)
// now that addressing is by path, not port — the schema/data paths are still derived PURELY from the id
// (AppPaths.SchemaPathForId/DataPathForId), never from the name. `DesignId` is the explicit reference to
// the IDE design this instance runs (null = none), carried through to `sys.instances` so the design
// dropdown can pre-select it. The two per-instance ports are GONE: every instance is served under the
// kernel's single app port + single asset port, addressed by `/apps/<name>` (KernelHost's front routers).
public sealed record InstanceSpec(
    int Id, string App, string SchemaPath, string DataPath, int? DesignId = null);

// One instance running under the kernel: its sovereign store and its BUILT handler trees (the app SSR
// tree + the asset /ws+/js tree), which the kernel's shared front routers dispatch to by mount name. It
// no longer binds any port of its own — the kernel owns the two shared ports and routes `/apps/<name>`
// to these handlers — so this is now a "load + build the handlers" unit, not a "bind two hosts" one. The
// store is exposed so data sovereignty is observable (each hosted instance owns its own).
public sealed class HostedInstance
{
    public InstanceSpec Spec { get; private set; }
    public IInstanceStore Store { get; }

    // The built handler trees the kernel's front routers delegate to: the app tree (SSR) and the asset
    // tree (/ws + /js). Built with the instance's mount base so emitted links/assets are mount-correct.
    public IHandler AppHandler { get; }
    public IHandler AssetHandler { get; }

    // Update which IDE design this running instance references (the IDE's Apply records it). DesignId is
    // registry metadata only, so this is a plain spec swap, no rebuild.
    internal void SetDesignId(int designId) => Spec = Spec with { DesignId = designId };

    private HostedInstance(InstanceSpec spec, IInstanceStore store, IHandler appHandler, IHandler assetHandler)
    {
        Spec = spec;
        Store = store;
        AppHandler = appHandler;
        AssetHandler = assetHandler;
    }

    // Load the description, open the sovereign store (the startup guard runs in its constructor), and
    // build the app+asset handler trees with this instance's mount base (`/apps/<name>`) and the
    // kernel's shared asset port (for the client's /js + WebSocket URL). No host is started here — the
    // kernel mounts these handlers under its two shared hosts. `hostActions` is this instance's
    // host-action seam (sys.publish, …), supplied by the kernel.
    public static HostedInstance Start(
        InstanceSpec spec, int assetPort, LiveRegistry registry, IHostActions? hostActions = null)
    {
        var description = InstanceDescriptionLoader.LoadFile(spec.SchemaPath);
        var store = new JsonFileInstanceStore(spec.DataPath, description);
        var mountBase = MountBaseFor(spec.App);
        var (appApp, assetApp) = InstanceApp.Build(store, description, mountBase, assetPort, registry, hostActions, spec.App);
        return new HostedInstance(spec, store, appApp.Build(), assetApp.Build());
    }

    // The mount base for a display name: `/apps/<name>`. The instance is served at this path under the
    // kernel's shared app + asset ports; the base is applied only at the SSR/client edges so the
    // instance stays mount-unaware. Centralized so the router and the build agree on the segment.
    public static string MountBaseFor(string name) => "/apps/" + name;
}
