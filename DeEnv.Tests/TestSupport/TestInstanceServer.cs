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

        // Two ports, like production: the app port (SSR, clean URL space) and the asset port (/ws +
        // /js). A single in-process instance is ROOT-MOUNTED (mount base "/"), so the base seam is in
        // its identity form (links + path are root-relative, the asset URL has no prefix) — exactly
        // the behavior-preserving case. The page targets the asset port (injected as the asset
        // authority host:port). The kernel-hosted path-mounted case is exercised by Kernel.feature.
        var appPort = GetFreePort();
        var assetPort = GetFreePort();
        var (appApp, assetApp) = InstanceApp.Build(Store, description, mountBase: "/", assetPort: assetPort);

        // Bind loopback-only (127.0.0.1), not all interfaces: tests are driven by Playwright over
        // localhost, and an all-interfaces listener trips the Windows Defender Firewall prompt — which
        // re-fires for every new executable path (e.g. running from a fresh git worktree). Loopback
        // listeners are firewall-exempt. (Production keeps all-interfaces unless DEENV_BIND=loopback.)
        _infraHost = Host.Create()
                    .Handler(assetApp)
                    .Defaults(secureUpgrade: false, strictTransport: false)
                    .Bind(IPAddress.Loopback, (ushort)assetPort, dualStack: false);

        _appHost = Host.Create()
                    .Handler(appApp)
                    // Plain HTTP for tests: no HTTPS endpoint, so don't upgrade/redirect.
                    .Defaults(secureUpgrade: false, strictTransport: false)
                    .Bind(IPAddress.Loopback, (ushort)appPort, dualStack: false);

        await _infraHost.StartAsync();
        await _appHost.StartAsync();
        BaseUrl = $"http://localhost:{appPort}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_appHost != null) await _appHost.StopAsync();
        if (_infraHost != null) await _infraHost.StopAsync();
    }

    // A genuinely free TCP port, never handed out twice this run (see PortAllocator) — so two parallel
    // in-process servers can't be dealt the same port pair.
    private static int GetFreePort() => PortAllocator.Next();
}
