// Client bootstrap for a code-owned (`ui`) instance. The SSR page sets window.initUi
// (the ui/common AST) and window.initData (the first-paint db + session state), then
// loads this bundle (codeExec.ts + dt.ts + ui.ts + init.ts). init() rebuilds the top
// scope exactly as the server's SsrRenderer did, then renders. Global script.

declare const initUi: { ui: ClientUi; common?: ClientCommon };
declare const initData: ServerDtState;
declare const initClientId: string;
declare const initIsGeneric: boolean;

interface ClientUi {
    vars?: unknown[];
    functions?: CodeFunction[];
    render: CodeFunction; // every page is `fn render()` — the app's own, or the framework-synthesized generic router
}
interface ClientCommon { functions?: CodeFunction[]; }

const uiStatic: {
    renderFn: ExecFunction;
    lastId: LastId;
    state: AppState;
    cache: Map<string, ClientCacheEntry>;
    clientId: string;
    lastError: string | null; // the latest server-rejected mutation's reason (Stage 5)
} = {
    renderFn: null as unknown as ExecFunction,
    lastId: { value: 0 },
    // The client mirrors the server's framework values (db, path) + app vars in one flat
    // top scope; resolution is by name, so the server's system/app split is observationally
    // the same here. isTop marks it reactive (matching the server's IsTop check).
    state: { objects: {}, arrays: {}, scope: { items: {}, parent: null, isTop: true }, localToServerIds: {}, serverToLocalIds: {} },
    cache: new Map(),
    clientId: "",
    lastError: null,
};

function init(): void {
    // db + session vars arrive as data; the memoized computations arrive in the cache;
    // functions are re-defined from the AST so they close over this same top scope.
    setMemoCache(uiStatic.cache);
    uiStatic.clientId = typeof initClientId === "string" ? initClientId : "";
    connectWs();
    mergeState(initData);
    const scope = uiStatic.state.scope;
    const initCtx: ExecContext = { lastId: { value: 0 } };   // top-level fns capture no ambient
    for (const fn of initUi.common?.functions ?? []) executeFunction(fn, scope, initCtx);
    for (const fn of initUi.ui.functions ?? []) executeFunction(fn, scope, initCtx);

    // Every page renders through a single `fn render()` (the app's own, or the framework's
    // synthesized generic router), taking no arguments — routing is internal: the generic render
    // calls sys.resolve(path) and the client RE-resolves the SAME URL over the shipped descriptors +
    // db leaves (SSR/hydrate agree, proven by the resolve-probe scenarios); a custom render reads
    // path itself. Built as a direct closure over the rebuilt top scope, never registered by name.
    uiStatic.renderFn = { type: "fn", fn: initUi.ui.render, scope };

    // Routing: `path` is framework-provided (the live URL), overriding the server's first-paint value.
    // The browser URL carries the mount (`/apps/<name>/…`); the app's `path` var is ROOT-RELATIVE
    // (mount-unaware), so strip the base — exactly the SSR first paint, which gave Code the stripped
    // path. (Identity when root-mounted.)
    scope.items["path"] = { value: { type: "text", value: stripBase(location.pathname) }, isReadOnly: false };

    // `isGeneric`: mirrors SsrRenderer's system-scope flag, fixed for the app's lifetime (an app is
    // either fully custom or fully generic, never both) — gates GenericUi's collection-prop links,
    // which only resolve under the framework's own router.
    scope.items["isGeneric"] = { value: { type: "bool", value: initIsGeneric }, isReadOnly: true };

    // Browser back/forward: write the (base-stripped) location back into the path var and re-render
    // over the warm session — the same machinery a forward click uses, just driven by the browser's
    // history pop instead of a pushState. resetViewState() drops the component slot-cache (the visited
    // view's components must run fresh — the slot keys collide across different-kind views) and forces a
    // refetch (the visited path's data may not be in the client graph; a deep read of an un-shipped node
    // throws "Value not available", caught + held by the speculative render below).
    //
    // Mirror the forward-click guards so Back/Forward is never less safe than a click:
    //   • If the session is not fully ready (wsReady false — socket still connecting/dropped, or the
    //     hello not yet acked), a refetch could not be serviced, so an optimistic render would strand
    //     the user on the popped URL with stale/incomplete content. Force a real browser reload of the
    //     popped location instead — the server SSRs it (the deep-link path), which is always correct.
    //   • Otherwise paint optimistically ONLY when the target resolves to a renderable view locally AND
    //     renderUiSpeculative finds it builds COMPLETELY from local data; when the route resolves to a
    //     not-yet-confirmed NotFound (un-shipped node) OR the build is incomplete (a present-but-thin
    //     object), HOLD the current view and let the refetch reply paint it — the same TWO flash gates
    //     navigateClientSide uses, so a Back to an un-shipped view never flashes a NotFound/partial frame
    //     either. NOTE: no scroll reset here — the browser restores the prior scroll position on a pop.
    window.addEventListener("popstate", () => {
        const item = uiStatic.state.scope.items["path"];
        if (item == null) return;
        if (!wsReady()) { location.reload(); return; }
        const pathVar = stripBase(location.pathname);
        item.value = { type: "text", value: pathVar };
        invalidateVar("path");
        resetViewState();
        if (targetRenderableLocally(pathVar)) renderUiSpeculative(); // instant iff renderable AND complete
        maybeRefetch();
    });

    // In-app links navigate CLIENT-SIDE (no full reload): one delegated listener intercepts qualifying
    // anchor clicks (same-origin, in-mount, plain left-click) and re-renders the target over the warm
    // session via the History API. Delegated on document so it also covers links OUTSIDE the #app
    // reconciler root (e.g. the breadcrumb trail). Non-qualifying clicks (external/new-tab/download/
    // hash/modified) and a not-ready session fall through to the browser. See ui.ts interceptNavigation.
    document.addEventListener("click", interceptNavigation);

    renderUi();

    // Hydration is complete: the first client render has run, so the reconciled DOM and its event
    // handlers are in place. Expose a deterministic marker so a test can wait for "the page is
    // interactive" instead of guessing (the Load event waits for unrelated subresources; window.initUi
    // is set by an inline script BEFORE this bundle even loads). Harmless in production — an attribute.
    document.documentElement.setAttribute("data-hydrated", "1");
}

// The bundle is injected dynamically (from the infra port), so it is not `defer`red —
// wait for the DOM (#app) before the first render.
if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", () => init());
else init();
