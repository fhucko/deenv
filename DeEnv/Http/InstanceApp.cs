using System.Text;
using DeEnv.Code;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;
using GenHTTP.Modules.Layouting;
using GenHTTP.Modules.Websockets;
using GenHTTP.Modules.Websockets.Protocol;

namespace DeEnv.Http;

// One row of the kernel's instance registry as surfaced to image Code (the read-only `instances`
// global): the instance id, its display NAME, and its mount PATH (`/apps/<name>`). A pure projection —
// no file paths, no store — so the kernel hands the renderer DATA, not a kernel reference (the
// locality-free seam). `Id` is the kernel-minted instance id (the sole key to its files, and the
// address a host action like `sys.publish(id)` targets); `App` is the display name (which also
// determines the mount); `Path` is `/apps/<name>` — what the operator sees + links to, now that
// addressing is by path, not per-instance ports.
//
// There is no `Created`/boot distinction: storage is fully id-based, and clone/delete/publish all
// work on ANY instance by its id, so the surface renders those actions on every row uniformly.
//
// `DesignId` is the explicit reference to which IDE design this instance currently runs (null = none),
// carried from the registry so the operator IDE can pre-select that design in the /instances/<id>
// dropdown and show its label in the instances list.
//
// PRIVACY: keep this projection free of anything sensitive. Registry rows render as transient
// objects, and ClientState ships a transient's props in FULL to every client that renders the list —
// there is no per-prop gating here. The id, the name, and the mount path are all non-sensitive
// handles (not file paths).
public sealed record InstanceInfo(int Id, string App, string Path, int? DesignId = null);

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

// Builds the GenHTTP handler trees for an instance, split into an app tree and an asset tree so the
// app URL space stays clean and reserved-path-free. Shared by the kernel host (which mounts these
// under apps/<name> on the kernel's two shared ports) and the in-process test host so routing is
// identical.
//
//   App tree   → SSR HTML for every node path, nothing reserved (ContentHandler).
//   Asset tree → /ws (WebSocket: all data ops) + /js (the client bundle).
//
// Both trees share one session store (the SSR path mints sessions, the WS path recomputes over them
// — see ClientSession). `mountBase` is where the instance is mounted ("/" root-mounted, "/apps/<name>"
// path-mounted) — applied at the SSR edge so emitted links/assets are mount-correct while the app
// stays base-unaware (a request can override it via X-Forwarded-Prefix). `assetPort` is the ADVERTISED
// asset port the page builds its /js + WebSocket URL against (the kernel's bind port locally, or a
// proxy's public TLS asset port — the host comes from the request, so the same authority serves all
// instances).
public static class InstanceApp
{
    public static (IHandlerBuilder App, IHandlerBuilder Asset) Build(
        IInstanceStore store, InstanceDescription description, string mountBase, int assetPort,
        LiveRegistry? registry = null, IHostActions? hostActions = null, string appName = "",
        int instanceId = 0, TokenAuth? auth = null,
        Func<ExecObject, int, ExecContext, IExecValue>? publishPreview = null, IBlobPool? blobPool = null)
    {
        var sessions = new ClientSessionStore();
        auth ??= TokenAuth.Ephemeral();
        var ws = new WsHandler(store, description, sessions, registry ?? new LiveRegistry(),
            hostActions ?? new NoHostActions(), mountBase, publishPreview: publishPreview);

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
            .Add(new ContentHandlerBuilder(store, description, sessions, mountBase, assetPort, registry ?? new LiveRegistry(), appName, instanceId, auth, publishPreview));

        var asset = Layout.Create()
            .Add("ws", websocket)
            .Add("js", new BundleHandlerBuilder())
            .Add("session", new SessionHandler(store, description, instanceId, auth))
            // The blob pool's upload+serve edges (docs/plans/assets-design.md) — additive, a sibling
            // of ws/js/session, so the app tree stays reserved-path-free. `store`/`description`/
            // `instanceId`/`auth` are the SAME values ContentHandler/SessionHandler use, so the ambient
            // session cookie a login mints there verifies here too (assets slice 2b, §2).
            .Add("assets", new AssetsHandlerBuilder(blobPool ?? new NoBlobPool(), store, description, instanceId, auth));

        return (app, asset);
    }
}
