using System.Text;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;
using GenHTTP.Modules.Layouting;
using GenHTTP.Modules.Websockets;
using GenHTTP.Modules.Websockets.Protocol;

namespace DeEnv.Http;

// One row of the kernel's instance registry as surfaced to image Code (the read-only `instances`
// global): the instance id, its display NAME, and its two ports. A pure projection — no file paths,
// no store — so the kernel hands the renderer DATA, not a kernel reference (the locality-free seam).
// `Id` is the kernel-minted instance id (the sole key to its files, and the address a host action
// like `sys.publish(id)` targets); `App` is a display name label only (used for nothing functional);
// `Port` is the app/serving port; `AssetsPort` is the infra port (/ws + /js).
//
// There is no `Created`/boot distinction: storage is fully id-based, and clone/delete/publish all
// work on ANY instance by its id, so the surface renders those actions on every row uniformly.
//
// PRIVACY: keep this projection free of anything sensitive. Registry rows render as transient
// objects, and ClientState ships a transient's props in FULL to every client that renders the
// list — there is no per-prop gating here. So the "expose the contended external binding (ports),
// hide internal identity (storage)" line (DECISIONS "`create` direction") must be drawn AT this
// projection, never relied on at the render. The id is a non-sensitive handle (not a file path).
public sealed record InstanceInfo(int Id, string App, int Port, int AssetsPort);

// A live cell holding the kernel's current instance registry. The WRITER is the kernel (it swaps
// `.Current` whenever the hosted set changes); the READERS are every hosted instance's renderer (they
// read `.Current` at render time, so each render reflects the current instances). It is DATA, not a
// pull-`Func`: modeling ambient framework data as a var-shaped cell — rather than a delegate — is what
// keeps a future live-update path open (a cell can later carry change-notification to PUSH a re-render;
// a function is a pull-only dead-end). The `volatile` reference makes the single-writer / many-reader
// handoff safe for the single-operator model (an atomic reference swap; no lock).
public sealed class LiveRegistry
{
    private volatile IReadOnlyList<InstanceInfo> _current = [];
    public IReadOnlyList<InstanceInfo> Current { get => _current; set => _current = value; }
}

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
        LiveRegistry? registry = null, IHostActions? hostActions = null)
    {
        var sessions = new ClientSessionStore();
        var ws = new WsHandler(store, description, sessions, registry ?? new LiveRegistry(),
            hostActions ?? new NoHostActions());

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
            .Add(new ContentHandlerBuilder(store, description, sessions, infraPort, registry ?? new LiveRegistry()));

        var infra = Layout.Create()
            .Add("ws", websocket)
            .Add("js", new BundleHandlerBuilder());

        return (app, infra);
    }
}
