using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Infrastructure;
using GenHTTP.Engine.Internal;
using GenHTTP.Modules.Practices;

namespace DeEnv.Kernel;

// A fully-resolved instruction to host one instance: its stable unique id, its display name, where
// its app document and data file live, and the two ports to bind (app = clean SSR URL space; infra =
// /ws + /js). `KernelHost.SpecsFor` resolves each registry entry to one of these — the schema/data
// paths are derived PURELY from the id (AppPaths.SchemaPathForId/DataPathForId), never from the name.
// `Id` is the instance's address for clone/delete/publish and the sole key to its files; `App` is a
// display NAME label only (surfaced to `sys.instances`), used for nothing functional — the file path
// no longer reveals it, so it is carried here for rendering. `DesignId` is the explicit reference to
// the IDE design this instance runs (null = none), carried from the registry through to `sys.instances`
// so the design dropdown can pre-select it; the IDE's Apply (sys.setDesign) updates it. Resolving paths
// into the spec keeps locality in one place and out of the registry shape.
public sealed record InstanceSpec(
    int Id, string App, string SchemaPath, string DataPath, int AppPort, int InfraPort, int? DesignId = null);

// One instance running under the kernel: its sovereign store and the two GenHTTP hosts. This is
// the single-instance "build + start both hosts" unit, extracted from Program.cs's old hosting
// tail so the kernel can run many at once. The store is exposed so data sovereignty is observable
// (each hosted instance owns its own).
public sealed class HostedInstance : IAsyncDisposable
{
    public InstanceSpec Spec { get; private set; }
    public IInstanceStore Store { get; }
    public int AppPort => Spec.AppPort;
    public int InfraPort => Spec.InfraPort;

    // Update which IDE design this running instance references (the IDE's Apply records it). DesignId is
    // registry metadata only — the hosting (ports, store) is unchanged — so this is a plain spec swap, no
    // restart. The kernel persists the new value to kernel.json + refreshes the live view (KernelHost.SetDesign).
    internal void SetDesignId(int designId) => Spec = Spec with { DesignId = designId };
    internal void SetApp(string name) => Spec = Spec with { App = name };

    private readonly IServerHost _appHost;
    private readonly IServerHost _infraHost;

    private HostedInstance(InstanceSpec spec, IInstanceStore store, IServerHost appHost, IServerHost infraHost)
    {
        Spec = spec;
        Store = store;
        _appHost = appHost;
        _infraHost = infraHost;
    }

    // Load the description, open the sovereign store (the startup guard runs in its constructor),
    // build the app+infra handler trees, and start both hosts. Mirrors the production hosting block.
    // `hostActions` is this instance's host-action seam (sys.publish, …), supplied by the kernel
    // (it carries the instance's own meta/data paths + the live target resolver).
    public static async Task<HostedInstance> StartAsync(
        InstanceSpec spec, LiveRegistry registry, IHostActions? hostActions = null)
    {
        var description = InstanceDescriptionLoader.LoadFile(spec.SchemaPath);
        var store = new JsonFileInstanceStore(spec.DataPath, description);
        var (appApp, infraApp) = InstanceApp.Build(store, description, spec.InfraPort, registry, hostActions);

        var infraHost = Host.Create()
            .Handler(infraApp)
            .Defaults(secureUpgrade: false, strictTransport: false)
            .Port((ushort)spec.InfraPort);

        var appHost = Host.Create()
            .Handler(appApp)
            // Plain HTTP: no HTTPS endpoint, so don't upgrade/redirect.
            .Defaults(secureUpgrade: false, strictTransport: false)
            .Port((ushort)spec.AppPort);

        await infraHost.StartAsync();
        await appHost.StartAsync();

        return new HostedInstance(spec, store, appHost, infraHost);
    }

    public async ValueTask DisposeAsync()
    {
        await _appHost.StopAsync();
        await _infraHost.StopAsync();
    }
}
