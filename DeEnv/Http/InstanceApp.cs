using System.Text;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;
using GenHTTP.Modules.Layouting;
using GenHTTP.Modules.Websockets;
using GenHTTP.Modules.Websockets.Protocol;

namespace DeEnv.Http;

// Builds the GenHTTP handler tree for an instance. Shared by the real host
// (Program.cs) and the in-process test host so routing is identical.
//
//   /ws            → WebSocket: all data ops (read + write), request/response
//   everything else → ContentHandler: /js (embedded client script) + SSR HTML
public static class InstanceApp
{
    public static IHandlerBuilder Build(IInstanceStore store, InstanceDescription description)
    {
        var ws = new WsHandler(store, description);

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

        return Layout.Create()
            .Add("ws", websocket)
            .Add(new ContentHandlerBuilder(store, description));
    }
}
