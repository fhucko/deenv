using System.Net;
using System.Net.Sockets;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Infrastructure;
using GenHTTP.Engine.Internal;
using GenHTTP.Modules.Practices;

namespace DeEnv.Tests.TestSupport;

/// <summary>
/// Starts an in-process GenHTTP server (pure-C# engine) for Playwright tests,
/// using the exact same handler tree as production via InstanceApp.Build.
/// </summary>
public sealed class TestInstanceServer : IAsyncDisposable
{
    private IServerHost? _appHost;
    private IServerHost? _infraHost;

    public string BaseUrl { get; private set; } = "";
    public IInstanceStore? Store { get; private set; }

    public async Task StartAsync(InstanceDescription description, string dataFilePath)
    {
        Store = new JsonFileInstanceStore(dataFilePath, description);

        // Two ports, exactly like production: the app port (SSR, clean URL space) and the
        // infra port (/ws + /js). The page is served from the app port; its bundle + WS
        // target the infra port (injected as window.initInfraPort).
        var appPort = GetFreePort();
        var infraPort = GetFreePort();
        var (appApp, infraApp) = InstanceApp.Build(Store, description, infraPort);

        _infraHost = Host.Create()
                    .Handler(infraApp)
                    .Defaults(secureUpgrade: false, strictTransport: false)
                    .Port((ushort)infraPort);

        _appHost = Host.Create()
                    .Handler(appApp)
                    // Plain HTTP for tests: no HTTPS endpoint, so don't upgrade/redirect.
                    .Defaults(secureUpgrade: false, strictTransport: false)
                    .Port((ushort)appPort);

        await _infraHost.StartAsync();
        await _appHost.StartAsync();
        BaseUrl = $"http://localhost:{appPort}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_appHost != null) await _appHost.StopAsync();
        if (_infraHost != null) await _infraHost.StopAsync();
    }

    // Grab a free TCP port by binding to :0, reading the assigned port, then releasing it.
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
