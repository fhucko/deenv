using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;
using GenHTTP.Api.Protocol;
using GenHTTP.Modules.IO;
using System.Text;
using System.Text.Json;

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
    private readonly IInstanceStore _store;
    private readonly InstanceDescription _description;
    private readonly TokenAuth _auth;
    private readonly int _instanceId;
    private readonly string _mountBase;
    private readonly int _assetPort;

    public ContentHandler(IInstanceStore store, InstanceDescription description, ClientSessionStore sessions,
        string mountBase, int assetPort, LiveRegistry registry, string appName, int instanceId, TokenAuth auth)
    {
        _store = store;
        _description = description;
        _renderer = new SsrRenderer(store, description, sessions, registry, appName);
        _auth = auth;
        _instanceId = instanceId;
        _mountBase = mountBase;
        _assetPort = assetPort;
    }

    public ValueTask PrepareAsync() => ValueTask.CompletedTask;

    public ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        var remaining = request.Target.GetRemaining();
        var path = remaining.IsRoot ? "/" : "/" + remaining.ToString().Trim('/');

        var principal = PrincipalFromCookie(request);
        var (html, status) = _renderer.Render(path, EffectiveBase(request), AssetAuthority(request), principalUserId: principal);
        var builder = request.Respond().Content(html).Type(ContentType.TextHtml);
        // Only override the default 200 when user code set a status (e.g. NotFound → 404).
        if (status != 200) builder = builder.Status(status, ((ResponseStatus)status).ToString());
        IResponse response = builder.Build();

        return new ValueTask<IResponse?>(response);
    }

    private int? PrincipalFromCookie(IRequest request)
    {
        var name = _auth.CookieName(_instanceId);
        foreach (var cookie in request.Cookies)
            if (cookie.Key == name)
                return _auth.Verify(cookie.Value.Value, _instanceId, _store, _description, DateTimeOffset.UtcNow);
        return null;
    }

    // The mount prefix to apply at the edges: the `X-Forwarded-Prefix` header (so a reverse proxy can
    // re-mount the instance — `""` to serve it at a domain root), else the kernel-assigned mountBase.
    // An empty header value is honored ("" → root); an absent header falls back to the mount.
    private string EffectiveBase(IRequest request) =>
        request.Headers.TryGetValue("X-Forwarded-Prefix", out var prefix)
            ? (prefix.Length == 0 ? "/" : prefix)
            : _mountBase;

    // Where the client loads /js + opens its WebSocket: the request host with the ADVERTISED asset port
    // (the kernel's bind port locally, or a reverse proxy's public TLS asset port via
    // DEENV_PUBLIC_ASSET_PORT — decoupled from the per-instance app addressing). Empty when no asset port
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
    private readonly string _appName;
    private readonly int _instanceId;
    private readonly TokenAuth _auth;

    public ContentHandlerBuilder(IInstanceStore store, InstanceDescription description, ClientSessionStore sessions,
        string mountBase, int assetPort, LiveRegistry registry, string appName, int instanceId, TokenAuth auth)
    {
        _store = store;
        _description = description;
        _sessions = sessions;
        _mountBase = mountBase;
        _assetPort = assetPort;
        _registry = registry;
        _appName = appName;
        _instanceId = instanceId;
        _auth = auth;
    }

    public IHandler Build() => new ContentHandler(_store, _description, _sessions, _mountBase, _assetPort, _registry, _appName, _instanceId, _auth);
}

public sealed class SessionHandler(IInstanceStore store, InstanceDescription description, int instanceId, TokenAuth auth) : IHandler
{
    private sealed record LoginRequest(string Name, string Password);

    public ValueTask PrepareAsync() => ValueTask.CompletedTask;

    public async ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        if (request.Method.KnownMethod == RequestMethod.Options)
            return Cors(request, request.Respond().Status(ResponseStatus.NoContent)).Build();

        var clear = request.Method.KnownMethod == RequestMethod.Delete;
        var userId = clear ? null : await LoginUserId(request);
        var ok = clear || userId is not null;
        var builder = request.Respond()
            .Content(JsonSerializer.Serialize(new { ok }))
            .Type(ContentType.ApplicationJson);
        if (ok)
            builder = builder.Header("Set-Cookie", clear ? ExpiredCookieHeader(request) : CookieHeader(request, userId));
        return Cors(request, builder).Build();
    }

    private async Task<int?> LoginUserId(IRequest request)
    {
        if (request.Content is null) return null;
        using var reader = new StreamReader(request.Content, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        LoginRequest? login;
        try
        {
            login = JsonSerializer.Deserialize<LoginRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
        if (login is null) return null;
        if (Code.UserConvention.PasswordFieldName(description) is not { } passwordField) return null;
        foreach (var (id, fields) in store.ReadExtent(Code.UserConvention.TypeName))
            if (fields.Fields.GetValueOrDefault(Code.UserConvention.NameField) is TextValue { Text: var n } && n == login.Name
                && fields.Fields.GetValueOrDefault(passwordField) is TextValue { Text: var hash }
                && hash.Length > 0
                && Code.AuthCrypto.Verify(login.Password, hash))
                return id;
        return null;
    }

    private string CookieHeader(IRequest request, int? userId)
    {
        if (userId is null) return ExpiredCookieHeader(request);
        var hash = "";
        if (Code.UserConvention.PasswordFieldName(description) is { } passwordField
            && store.ReadById(userId.Value) is { Fields: var fields }
            && fields.Fields.GetValueOrDefault(passwordField) is TextValue { Text: var h })
            hash = h;
        var token = auth.Mint(instanceId, userId.Value, hash, DateTimeOffset.UtcNow);
        return $"{auth.CookieName(instanceId)}={token}; Path=/; Max-Age={TokenAuth.DefaultMaxAgeSeconds}; HttpOnly; SameSite=Lax{SecureAttr(request)}";
    }

    private string ExpiredCookieHeader(IRequest request) =>
        $"{auth.CookieName(instanceId)}=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax{SecureAttr(request)}";

    private static string SecureAttr(IRequest request) =>
        request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) && proto == "https" ? "; Secure" : "";

    private static IResponseBuilder Cors(IRequest request, IResponseBuilder response) =>
        request.Headers.TryGetValue("Origin", out var origin)
        && request.Host is { } host
        && SameHostOrigin(origin, host)
            ? response
                .Header("Access-Control-Allow-Origin", origin)
                .Header("Access-Control-Allow-Credentials", "true")
                .Header("Access-Control-Allow-Methods", "POST, DELETE, OPTIONS")
                .Header("Access-Control-Allow-Headers", "Content-Type")
            : response;

    private static bool SameHostOrigin(string origin, string host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, host.Split(':')[0], StringComparison.OrdinalIgnoreCase);
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
