using System.Text;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;
using GenHTTP.Modules.Layouting;
using GenHTTP.Modules.Websockets;
using GenHTTP.Modules.Websockets.Protocol;

namespace DeEnv.Http;

// Builds the GenHTTP handler trees for an instance, split across two ports so the app
// port owns a clean, reserved-path-free data URL space. Shared by the real host
// (Program.cs) and the in-process test host so routing is identical.
//
//   App host   → SSR HTML for every node path, nothing reserved (ContentHandler).
//   Infra host → /ws (WebSocket: all data ops) + /js (the client bundle).
//
// Both trees share one session store (the SSR path mints sessions, the WS path recomputes
// over them — see ClientSession). The app host is told the infra port so the page can load
// /js and open its WebSocket against it.
public static class InstanceApp
{
    public static (IHandlerBuilder App, IHandlerBuilder Infra) Build(
        IInstanceStore store, InstanceDescription description, int infraPort)
    {
        var sessions = new ClientSessionStore();
        var ws = new WsHandler(store, description, sessions);

        // Native GenHTTP websocket (no Fleck). We read/write raw UTF-8 frames so the
        // JSON payload goes on the wire verbatim — no extra serialization wrapping.
        var websocket = Websocket.Functional()
            .OnMessage(async (_, frame) =>
            {
                var message = Encoding.UTF8.GetString(frame.Data.Span);
                var response = ws.ProcessMessage(message);
                var bytes = Encoding.UTF8.GetBytes(response);
                await frame.Connection.WriteAsync(bytes, FrameType.Text, true, CancellationToken.None);
            });

        var app = Layout.Create()
            .Add(new ContentHandlerBuilder(store, description, sessions, infraPort));

        var infra = Layout.Create()
            .Add("ws", websocket)
            .Add("js", new BundleHandlerBuilder());

        return (app, infra);
    }
}
