using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;
using GenHTTP.Api.Protocol;
using GenHTTP.Modules.IO;

namespace DeEnv.Http;

// The APP host's handler: server-rendered HTML for every node path. No reserved paths —
// the app port is a clean data URL space. Asset endpoints (/ws, /js) live on the asset
// host (see BundleHandler + InstanceApp), addressed by path under the same mount.
//
// MOUNT-AWARENESS: the instance is base-UNAWARE internally (its `path` var + emitted links are
// root-relative). The base is applied at THIS edge. The kernel front-router strips the mount
// segments before this handler runs, so `request.Target.GetRemaining()` is already the instance's
// root-relative path. The effective base for emitted links/assets is the `X-Forwarded-Prefix`
// request header when present (so nginx can serve the app at a domain root with prefix ""), else the
// `mountBase` the kernel built this handler with ("/apps/<name>", or "/" when root-mounted).
public sealed class ContentHandler : IHandler
{
    private readonly SsrRenderer _renderer;
    private readonly string _mountBase;
    private readonly int _assetPort;

    public ContentHandler(IInstanceStore store, InstanceDescription description, ClientSessionStore sessions,
        string mountBase, int assetPort, LiveRegistry registry)
    {
        _renderer = new SsrRenderer(store, description, sessions, registry);
        _mountBase = mountBase;
        _assetPort = assetPort;
    }

    public ValueTask PrepareAsync() => ValueTask.CompletedTask;

    public ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        var remaining = request.Target.GetRemaining();
        var path = remaining.IsRoot ? "/" : "/" + remaining.ToString().Trim('/');

        var (html, status) = _renderer.Render(path, EffectiveBase(request), AssetAuthority(request));
        var builder = request.Respond().Content(html).Type(ContentType.TextHtml);
        // Only override the default 200 when user code set a status (e.g. NotFound → 404).
        if (status != 200) builder = builder.Status(status, ((ResponseStatus)status).ToString());
        IResponse response = builder.Build();

        return new ValueTask<IResponse?>(response);
    }

    // The mount prefix to apply at the edges: the `X-Forwarded-Prefix` header (so a reverse proxy can
    // re-mount the instance — `""` to serve it at a domain root), else the kernel-assigned mountBase.
    // An empty header value is honored ("" → root); an absent header falls back to the mount.
    private string EffectiveBase(IRequest request) =>
        request.Headers.TryGetValue("X-Forwarded-Prefix", out var prefix)
            ? (prefix.Length == 0 ? "/" : prefix)
            : _mountBase;

    // Where the client loads /js + opens its WebSocket: the request host with the kernel-level asset
    // PORT (a shared port, decoupled from the per-instance app addressing). Empty when no asset port
    // (a render with no client bundle) — the client then falls back to a same-origin, base-relative
    // asset URL. The host is taken from the request (stripping any :port it carries) so the browser
    // reaches the asset host on the same name it used for the app.
    private string AssetAuthority(IRequest request)
    {
        if (_assetPort == 0) return "";
        var host = request.Host;
        if (string.IsNullOrEmpty(host)) host = "localhost";
        var colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon];
        return host + ":" + _assetPort;
    }
}

// Minimal builder so the handler can be added to a Layout via the IHandlerBuilder overload.
public sealed class ContentHandlerBuilder : IHandlerBuilder
{
    private readonly IInstanceStore _store;
    private readonly InstanceDescription _description;
    private readonly ClientSessionStore _sessions;
    private readonly string _mountBase;
    private readonly int _assetPort;
    private readonly LiveRegistry _registry;

    public ContentHandlerBuilder(IInstanceStore store, InstanceDescription description, ClientSessionStore sessions,
        string mountBase, int assetPort, LiveRegistry registry)
    {
        _store = store;
        _description = description;
        _sessions = sessions;
        _mountBase = mountBase;
        _assetPort = assetPort;
        _registry = registry;
    }

    public IHandler Build() => new ContentHandler(_store, _description, _sessions, _mountBase, _assetPort, _registry);
}

// The ASSET host's bundle handler: serves the self-hosted UI client at /js. (The
// WebSocket is added separately at /ws — see InstanceApp.)
public sealed class BundleHandler : IHandler
{
    public ValueTask PrepareAsync() => ValueTask.CompletedTask;

    public ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        IResponse response = request.Respond()
            .Content(ClientScript.UiJs)
            .Type(ContentType.ApplicationJavaScript)
            .Build();
        return new ValueTask<IResponse?>(response);
    }
}

public sealed class BundleHandlerBuilder : IHandlerBuilder
{
    public IHandler Build() => new BundleHandler();
}
