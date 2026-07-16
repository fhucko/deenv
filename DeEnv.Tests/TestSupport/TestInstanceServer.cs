using System.Net;
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
    // This single-instance test host always runs instanceId 0 — a step needs this to address the
    // per-instance session cookie by name (TokenAuth.CookiePrefix + InstanceId), e.g. the
    // garbage-cookie upload-auth scenario (Assets.feature).
    public const int InstanceId = 0;

    public async Task StartAsync(InstanceDescription description, string dataFilePath)
    {
        Store = new JsonFileInstanceStore(dataFilePath, description);

        // Two ports, like production: the app port (SSR, clean URL space) and the asset port (/ws +
        // /js). A single in-process instance is ROOT-MOUNTED (mount base "/"), so the base seam is in
        // its identity form (links + path are root-relative, the asset URL has no prefix) — exactly
        // the behavior-preserving case. The page targets the asset port (injected as the asset
        // authority host:port). The kernel-hosted path-mounted case is exercised by Kernel.feature.
        //
        // Bind under PortAllocator's residual-TOCTOU retry: Next() verifies bindable then releases,
        // and a sibling can grab the port before GenHTTP binds (Access login UI under parallel load
        // surfaces this as BindingException → later "user menu" steps skipped). Kernel host already
        // uses StartWithBindRetryAsync; this path must match.
        await PortAllocator.StartWithBindRetryAsync(async () =>
        {
            await StopHostsAsync();
            var appPort = GetFreePort();
            var assetPort = GetFreePort();
            var blobPool = new FileBlobPool(AppPaths.BlobsDirForDataPath(dataFilePath));
            var auth = TokenAuth.ForDataHome(Path.GetDirectoryName(dataFilePath)!);
            var (appApp, assetApp) = InstanceApp.Build(Store, description, mountBase: "/", assetPort: assetPort,
                instanceId: InstanceId, auth: auth, blobPool: blobPool);

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

            try
            {
                await _infraHost.StartAsync();
                await _appHost.StartAsync();
            }
            catch
            {
                // Partial start (infra up, app bind lost the race) must release before retry.
                await StopHostsAsync();
                throw;
            }

            BaseUrl = $"http://localhost:{appPort}";
            AssetBaseUrl = $"http://localhost:{assetPort}";
        });
    }

    public async ValueTask DisposeAsync() => await StopHostsAsync();

    private async Task StopHostsAsync()
    {
        if (_appHost != null)
        {
            try { await _appHost.StopAsync(); } catch { /* best-effort on bind-retry cleanup */ }
            _appHost = null;
        }
        if (_infraHost != null)
        {
            try { await _infraHost.StopAsync(); } catch { /* best-effort on bind-retry cleanup */ }
            _infraHost = null;
        }
    }

    // A genuinely free TCP port, never handed out twice this run (see PortAllocator) — so two parallel
    // in-process servers can't be dealt the same port pair.
    private static int GetFreePort() => PortAllocator.Next();
}
