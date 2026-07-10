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
    // The asset port's own base URL (where /ws, /js, /session, and now /assets live) — root-mounted
    // here (no /apps/<name> prefix; see the mount-base comment below), so a test hitting the blob pool
    // edges directly (Assets.feature) targets "<AssetBaseUrl>/assets" / "<AssetBaseUrl>/assets/<name>".
    public string AssetBaseUrl { get; private set; } = "";
    public IInstanceStore? Store { get; private set; }
    // The SAME TokenAuth (and instanceId, always 0 for this single-instance test host) InstanceApp.Build
    // wired into WsHandler/AssetsHandler — exposed so a step can mint/verify a ticket the exact same way
    // the running server does (Assets.feature's upload-ticket scenarios), without a second secret.
    public TokenAuth? Auth { get; private set; }
    public const int InstanceId = 0;

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
        var blobPool = new FileBlobPool(AppPaths.BlobsDirForDataPath(dataFilePath));
        Auth = TokenAuth.ForDataHome(Path.GetDirectoryName(dataFilePath)!);
        var (appApp, assetApp) = InstanceApp.Build(Store, description, mountBase: "/", assetPort: assetPort,
            instanceId: InstanceId, auth: Auth, blobPool: blobPool);

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
        AssetBaseUrl = $"http://localhost:{assetPort}";
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
