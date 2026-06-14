using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;
using GenHTTP.Api.Protocol;
using GenHTTP.Modules.IO;

namespace DeEnv.Http;

// The APP host's handler: server-rendered HTML for every node path. No reserved paths —
// the app port is a clean data URL space. Infra endpoints (/ws, /js) live on the infra
// host (see BundleHandler + InstanceApp).
public sealed class ContentHandler : IHandler
{
    private readonly SsrRenderer _renderer;

    public ContentHandler(IInstanceStore store, InstanceDescription description, ClientSessionStore sessions, int infraPort, Func<IReadOnlyList<InstanceInfo>> registry)
    {
        _renderer = new SsrRenderer(store, description, sessions, infraPort, registry);
    }

    public ValueTask PrepareAsync() => ValueTask.CompletedTask;

    public ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        var remaining = request.Target.GetRemaining();
        var path = remaining.IsRoot ? "/" : "/" + remaining.ToString().Trim('/');

        var (html, status) = _renderer.Render(path);
        var builder = request.Respond().Content(html).Type(ContentType.TextHtml);
        // Only override the default 200 when user code set a status (e.g. NotFound → 404).
        if (status != 200) builder = builder.Status(status, ((ResponseStatus)status).ToString());
        IResponse response = builder.Build();

        return new ValueTask<IResponse?>(response);
    }
}

// Minimal builder so the handler can be added to a Layout via the IHandlerBuilder overload.
public sealed class ContentHandlerBuilder : IHandlerBuilder
{
    private readonly IInstanceStore _store;
    private readonly InstanceDescription _description;
    private readonly ClientSessionStore _sessions;
    private readonly int _infraPort;
    private readonly Func<IReadOnlyList<InstanceInfo>> _registry;

    public ContentHandlerBuilder(IInstanceStore store, InstanceDescription description, ClientSessionStore sessions, int infraPort, Func<IReadOnlyList<InstanceInfo>> registry)
    {
        _store = store;
        _description = description;
        _sessions = sessions;
        _infraPort = infraPort;
        _registry = registry;
    }

    public IHandler Build() => new ContentHandler(_store, _description, _sessions, _infraPort, _registry);
}

// The INFRA host's bundle handler: serves the self-hosted UI client at /js. (The
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
