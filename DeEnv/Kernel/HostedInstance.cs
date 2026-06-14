using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Infrastructure;
using GenHTTP.Engine.Internal;
using GenHTTP.Modules.Practices;

namespace DeEnv.Kernel;

// A fully-resolved instruction to host one instance: where its app document and data file live,
// and the two ports to bind (app = clean SSR URL space; infra = /ws + /js). `KernelHost.SpecsFor`
// resolves each registry entry to one of these (app name → schema path + derived data path).
// Resolving paths into the spec keeps locality in one place and out of the registry shape.
public sealed record InstanceSpec(string SchemaPath, string DataPath, int AppPort, int InfraPort);

// One instance running under the kernel: its sovereign store and the two GenHTTP hosts. This is
// the single-instance "build + start both hosts" unit, extracted from Program.cs's old hosting
// tail so the kernel can run many at once. The store is exposed so data sovereignty is observable
// (each hosted instance owns its own).
public sealed class HostedInstance : IAsyncDisposable
{
    public InstanceSpec Spec { get; }
    public IInstanceStore Store { get; }
    public int AppPort => Spec.AppPort;
    public int InfraPort => Spec.InfraPort;

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
    public static async Task<HostedInstance> StartAsync(InstanceSpec spec, Func<IReadOnlyList<InstanceInfo>> registry)
    {
        var description = InstanceDescriptionLoader.LoadFile(spec.SchemaPath);
        var store = new JsonFileInstanceStore(spec.DataPath, description);
        var (appApp, infraApp) = InstanceApp.Build(store, description, spec.InfraPort, registry);

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
