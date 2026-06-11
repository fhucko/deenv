// Client DOM runtime: turns the render fn's ExecTag tree into DOM and keeps it in
// sync. Ported from the app14 prototype and extended with identity-keyed
// reconciliation (foreach rows carry the member object's id as data-key, so a row's
// element — and any focused input inside it — moves with the object across
// orderBy/insert/remove instead of being rebuilt in place). Global script; shares
// scope with codeExec.ts (interpreter) and init.ts (uiStatic). DOM-only; the server
// half is SsrRenderer.

function renderUi(): void {
    const context: ExecContext = { lastId: uiStatic.lastId };
    let result: ExecValue;
    try {
        result = callFunction(uiStatic.renderFn, context);
    } catch (e) {
        // Unshipped data read outside any computation boundary: keep the current DOM
        // and ask the server; the refetch reply re-renders with the data present.
        if (e instanceof Error && e.message === "Value not available") {
            needsServerData = true;
            maybeRefetch();
            return;
        }
        throw e;
    }
    updateChildren(document.body, [result]);
    syncPath();
    syncScopeText("title", v => { document.title = v; });
    refreshErrorBanner();
    maybeRefetch(); // anything stale or missing → re-ask the server
}

// Routing writes real history entries: a code-driven `path` change pushes, so the
// browser's back/forward buttons work; popstate (init.ts) writes the var back.
function syncPath(): void {
    const item = uiStatic.state.scope.items["path"];
    if (item == null || item.value.type !== "text") return;
    if (item.value.value !== location.pathname) history.pushState(null, "", item.value.value);
}

// The latest server-rejected mutation, surfaced as a dismissable banner. Re-appended
// after each render (the body reconciler drops children it didn't produce); keyed so
// the reconciler never repurposes the element for app content.
function refreshErrorBanner(): void {
    const existing = document.querySelector<HTMLElement>("[data-key='__error']");
    if (uiStatic.lastError == null) { existing?.remove(); return; }
    const banner = existing ?? document.createElement("div");
    if (existing == null) {
        banner.setAttribute("data-key", "__error");
        banner.style.cssText =
            "position:fixed;top:0;left:0;right:0;z-index:9999;padding:0.5rem 1rem;" +
            "background:#b00020;color:#fff;font:14px system-ui,sans-serif;cursor:pointer;";
        banner.onclick = () => { uiStatic.lastError = null; refreshErrorBanner(); };
    }
    banner.textContent = `Change rejected: ${uiStatic.lastError} — click to dismiss`;
    document.body.appendChild(banner);
}

// Invoke a (no-arg) function — the render fn or an event handler — by running its
// body directly. (The render fn is shipped as a bare CodeFunction without a "type"
// discriminator, so it must not be routed back through executeValue.)
function callFunction(fn: ExecFunction, context: ExecContext): ExecValue {
    const callScope: ExecScope = { parent: fn.scope, items: {} };
    return executeBlock(fn.fn.body, callScope, context) ?? { type: "nothing" };
}

function syncScopeText(name: string, apply: (v: string) => void): void {
    const item = uiStatic.state.scope.items[name];
    if (item != null && item.value.type === "text") apply(item.value.value);
}

// Reconcile `parent`'s children against the rendered exec children, reusing nodes
// (keyed by data-key when present, else positionally by tag name) and reordering them
// to match — so a reused node keeps its focus, selection, and uncommitted input state.
function updateChildren(parent: Node, execChildren: ExecTagChild[]): void {
    const desired = flatten(execChildren).filter(isRenderable);

    // Index existing children: keyed elements by their data-key, the rest by tag name.
    const keyed: { [key: string]: ChildNode[] } = {};
    const unkeyed: { [name: string]: ChildNode[] } = {};
    for (const node of Array.from(parent.childNodes)) {
        const k = node.nodeType === 1 ? (node as Element).getAttribute("data-key") : null;
        if (k != null) (keyed[k] ??= []).push(node);
        else (unkeyed[node.nodeName] ??= []).push(node);
    }

    const ordered: ChildNode[] = [];
    for (const child of desired) {
        const nodeName = child.type === "tag" ? child.name.toUpperCase() : "#text";
        const key = child.type === "tag" && child.key != null ? String(child.key) : null;
        let node = key != null ? keyed[key]?.shift() ?? null : unkeyed[nodeName]?.shift() ?? null;
        if (node == null || node.nodeName !== nodeName) node = createNode(child);
        applyNode(node, child);
        ordered.push(node);
    }

    // Place reused/created nodes in order; drop any that were not reused.
    for (let i = 0; i < ordered.length; i++) {
        if (parent.childNodes[i] !== ordered[i]) parent.insertBefore(ordered[i], parent.childNodes[i] ?? null);
    }
    while (parent.childNodes.length > ordered.length) parent.lastChild!.remove();
}

function createNode(child: ExecValue): ChildNode {
    return child.type === "tag" ? document.createElement(child.name) : document.createTextNode("");
}

function applyNode(node: ChildNode, child: ExecValue): void {
    if (child.type === "tag") {
        const el = node as HTMLElement;
        if (child.key != null) el.setAttribute("data-key", String(child.key));
        refreshAttributes(el, child);
        updateChildren(el, child.children);
        wireEvents(el, child);
    } else if (child.type === "text") {
        node.textContent = child.value;
    } else if (child.type === "int" || child.type === "bool") {
        node.textContent = String(child.value);
    }
}

// Scalar attributes become DOM attributes; checkbox/value get special handling so the
// live input reflects the model. data-key is managed by applyNode, never wiped here.
function refreshAttributes(el: HTMLElement, tag: ExecTag): void {
    const want = new Set<string>();
    for (const [name, result] of Object.entries(tag.attributes)) {
        const v = result.value;
        if (v.type === "fn" || v.type === "sysFn" || v.type === "object" || v.type === "array") continue;
        const raw = v.type === "null" ? null : (v as ExecInt | ExecBool | ExecText).value;

        if (tag.name === "input" && name === "checked") {
            (el as HTMLInputElement).checked = !!raw;
            if (raw) { el.setAttribute("checked", ""); want.add("checked"); } else el.removeAttribute("checked");
            continue;
        }
        if (tag.name === "input" && name === "value") {
            const text = raw == null ? "" : String(raw);
            (el as HTMLInputElement).value = text;
            el.setAttribute("value", text); want.add("value");
            continue;
        }
        if (raw == null || raw === false) { el.removeAttribute(name); continue; }
        el.setAttribute(name, raw === true ? "" : String(raw));
        want.add(name);
    }
    for (const attr of Array.from(el.attributes))
        if (attr.name !== "data-key" && !want.has(attr.name)) el.removeAttribute(attr.name);
}

// Two-way binding + click handlers. A bound value/checked attribute whose result
// carries a setValue closure writes back to the model and re-renders.
function wireEvents(el: HTMLElement, tag: ExecTag): void {
    const checked = tag.attributes["checked"];
    const value = tag.attributes["value"];
    if (tag.name === "input" && (checked?.setValue || value?.setValue)) {
        (el as HTMLInputElement).oninput = () => {
            const input = el as HTMLInputElement;
            if (checked?.setValue) checked.setValue({ type: "bool", value: input.checked });
            else if (value?.setValue) value.setValue({ type: "text", value: input.value });
            renderUi();
        };
    } else {
        (el as HTMLInputElement).oninput = null;
    }

    const onClick = tag.attributes["onClick"]?.value;
    if (onClick != null && onClick.type === "fn") {
        const fn = onClick;
        // Handlers may be side-effecting (assignments, factory calls): bypass the memo
        // cache while one runs, so a repeated call never skips its effects.
        el.onclick = () => {
            runWithMemoBypass(() => callFunction(fn, { lastId: uiStatic.lastId }));
            renderUi();
        };
    } else {
        el.onclick = null;
    }
}

function isRenderable(v: ExecValue): boolean {
    return v.type === "tag" || v.type === "text" || v.type === "int" || v.type === "bool";
}

function flatten(items: ExecTagChild[]): ExecValue[] {
    const result: ExecValue[] = [];
    for (const item of items) {
        if (item.type === "array") result.push(...flatten(item.items.map(p => p.value)));
        else result.push(item);
    }
    return result;
}
