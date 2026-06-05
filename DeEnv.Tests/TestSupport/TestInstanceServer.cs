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
    private IServerHost? _host;

    public string BaseUrl { get; private set; } = "";
    public IInstanceStore? Store { get; private set; }

    public async Task StartAsync(InstanceDescription description, string dataFilePath)
    {
        Store = new JsonFileInstanceStore(dataFilePath, description);

        var port = GetFreePort();
        var app = InstanceApp.Build(Store, description);

        _host = Host.Create()
                    .Handler(app)
                    // Plain HTTP for tests: no HTTPS endpoint, so don't upgrade/redirect.
                    .Defaults(secureUpgrade: false, strictTransport: false)
                    .Port((ushort)port);

        await _host.StartAsync();
        BaseUrl = $"http://localhost:{port}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
            await _host.StopAsync();
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
