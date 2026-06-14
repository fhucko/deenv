using System.Text;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;
using GenHTTP.Modules.Layouting;
using GenHTTP.Modules.Websockets;
using GenHTTP.Modules.Websockets.Protocol;

namespace DeEnv.Http;

// One row of the kernel's instance registry as surfaced to image Code (the read-only `instances`
// global): the app document name and its two ports. A pure projection — no file paths, no store —
// so the kernel hands the renderer DATA, not a kernel reference (the locality-free seam). `Port`
// is the app/serving port; `AssetsPort` is the infra port (/ws + /js).
//
// PRIVACY: keep this projection free of anything sensitive. Registry rows render as transient
// objects, and ClientState ships a transient's props in FULL to every client that renders the
// list — there is no per-prop gating here. So the "expose the contended external binding (ports),
// hide internal identity (storage)" line (DECISIONS "`create` direction") must be drawn AT this
// projection, never relied on at the render.
public sealed record InstanceInfo(string App, int Port, int AssetsPort);

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
        IInstanceStore store, InstanceDescription description, int infraPort,
        IReadOnlyList<InstanceInfo>? registry = null)
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
            .Add(new ContentHandlerBuilder(store, description, sessions, infraPort, registry ?? []));

        var infra = Layout.Create()
            .Add("ws", websocket)
            .Add("js", new BundleHandlerBuilder());

        return (app, infra);
    }
}
