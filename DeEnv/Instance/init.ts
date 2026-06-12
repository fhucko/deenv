// Client bootstrap for a code-owned (`ui`) instance. The SSR page sets window.initUi
// (the ui/common AST) and window.initData (the first-paint db + session state), then
// loads this bundle (codeExec.ts + dt.ts + ui.ts + init.ts). init() rebuilds the top
// scope exactly as the server's SsrRenderer did, then renders. Global script.

declare const initUi: { ui: ClientUi; common?: ClientCommon; view?: ViewInfo };
declare const initData: ServerDtState;
declare const initClientId: string;

interface ClientUi {
    vars?: unknown[];
    functions?: CodeFunction[];
    render: CodeFunction | null; // optional: an app may define only views
    views?: ClientView[];
}
interface ClientView { type?: string | null; path?: string | null; fn: CodeFunction; }
interface ClientCommon { functions?: CodeFunction[]; }
// The server's resolved rendering-function decision for THIS page.
interface ViewInfo { kind: "render" | "type" | "path"; index?: number; objectId?: number; }

const uiStatic: {
    renderFn: ExecFunction;
    renderArgs: ExecValue[]; // a type view's routed object / a path view's path
    lastId: LastId;
    state: AppState;
    cache: Map<string, ClientCacheEntry>;
    clientId: string;
    lastError: string | null; // the latest server-rejected mutation's reason (Stage 5)
} = {
    renderFn: null as unknown as ExecFunction,
    renderArgs: [],
    lastId: { value: 0 },
    state: { objects: {}, arrays: {}, scope: { items: {}, parent: null }, localToServerIds: {}, serverToLocalIds: {} },
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
    for (const fn of initUi.common?.functions ?? []) executeFunction(fn, scope);
    for (const fn of initUi.ui.functions ?? []) executeFunction(fn, scope);

    // The render function for this page is the server's resolved view (render, a
    // type view, or a path view). Built as a direct closure — never registered by
    // name. A type view's parameter starts from the shipped object (or a stub whose
    // unshipped reads trigger the refetch machinery).
    const view = initUi.view;
    const fn = view != null && view.kind !== "render" && view.index != null
        ? initUi.ui.views![view.index].fn
        : initUi.ui.render!;
    uiStatic.renderFn = { type: "fn", fn, scope };
    if (view?.kind === "type" && view.objectId != null) {
        const objects = uiStatic.state.objects;
        uiStatic.renderArgs = [objects[view.objectId]
            ?? (objects[view.objectId] = { type: "object", id: view.objectId, props: {} })];
    } else if (view?.kind === "path" && fn.params.length > 0) {
        uiStatic.renderArgs = [{ type: "text", value: location.pathname }];
    }

    // Routing: the live URL overrides the server's first-paint `path`.
    if (scope.items["path"] != null)
        scope.items["path"] = { value: { type: "text", value: location.pathname }, isReadOnly: false };

    // Browser back/forward: write the location back into the path var and re-render.
    window.addEventListener("popstate", () => {
        const item = uiStatic.state.scope.items["path"];
        if (item != null) {
            item.value = { type: "text", value: location.pathname };
            invalidateVar("path");
            renderUi();
        }
    });

    renderUi();
}

init();
