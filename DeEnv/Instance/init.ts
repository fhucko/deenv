// Client bootstrap for a code-owned (`ui`) instance. The SSR page sets window.initUi
// (the ui/common AST) and window.initData (the first-paint db + session state), then
// loads this bundle (codeExec.ts + dt.ts + ui.ts + init.ts). init() rebuilds the top
// scope exactly as the server's SsrRenderer did, then renders. Global script.

declare const initUi: { ui: ClientUi; common?: ClientCommon };
declare const initData: ServerDtState;

interface ClientUi { vars?: unknown[]; functions?: CodeFunction[]; render: CodeFunction; }
interface ClientCommon { functions?: CodeFunction[]; }

const uiStatic: {
    renderFn: ExecFunction;
    lastId: LastId;
    state: AppState;
} = {
    renderFn: null as unknown as ExecFunction,
    lastId: { value: 0 },
    state: { objects: {}, arrays: {}, scope: { items: {}, parent: null }, localToServerIds: {}, serverToLocalIds: {} },
};

function init(): void {
    // db + session vars arrive as data; functions are re-defined from the AST so they
    // close over this same top scope (mutual recursion, like the server).
    mergeState(initData);
    const scope = uiStatic.state.scope;
    for (const fn of initUi.common?.functions ?? []) executeFunction(fn, scope);
    for (const fn of initUi.ui.functions ?? []) executeFunction(fn, scope);
    uiStatic.renderFn = executeFunction(initUi.ui.render, scope);

    // Routing: the live URL overrides the server's first-paint `path`.
    if (scope.items["path"] != null)
        scope.items["path"] = { value: { type: "text", value: location.pathname }, isReadOnly: false };

    renderUi();
}

init();
