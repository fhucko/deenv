using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;
using GenHTTP.Api.Protocol;
using GenHTTP.Modules.IO;

namespace DeEnv.Http;

// GenHTTP handler for everything that isn't the WebSocket:
//   /ui-js           → the self-hosted UI client bundle
//   anything else     → server-rendered HTML for that node path
public sealed class ContentHandler : IHandler
{
    private readonly SsrRenderer _renderer;

    public ContentHandler(IInstanceStore store, InstanceDescription description, ClientSessionStore sessions)
    {
        _renderer = new SsrRenderer(store, description, sessions);
    }

    public ValueTask PrepareAsync() => ValueTask.CompletedTask;

    public ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        var remaining = request.Target.GetRemaining();
        var path = remaining.IsRoot ? "/" : "/" + remaining.ToString().Trim('/');

        IResponse response = path switch
        {
            "/ui-js" => request.Respond()
                     .Content(ClientScript.UiJs)
                     .Type(ContentType.ApplicationJavaScript)
                     .Build(),
            _ => request.Respond()
                     .Content(_renderer.Render(path))
                     .Type(ContentType.TextHtml)
                     .Build(),
        };

        return new ValueTask<IResponse?>(response);
    }
}

// Minimal builder so the handler can be added to a Layout via the IHandlerBuilder overload.
public sealed class ContentHandlerBuilder : IHandlerBuilder
{
    private readonly IInstanceStore _store;
    private readonly InstanceDescription _description;
    private readonly ClientSessionStore _sessions;

    public ContentHandlerBuilder(IInstanceStore store, InstanceDescription description, ClientSessionStore sessions)
    {
        _store = store;
        _description = description;
        _sessions = sessions;
    }

    public IHandler Build() => new ContentHandler(_store, _description, _sessions);
}
