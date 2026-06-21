// Client DOM runtime: turns the render fn's ExecTag tree into DOM and keeps it in
// sync. Ported from the app14 prototype and extended with identity-keyed
// reconciliation (foreach rows carry the member object's id as data-key, so a row's
// element — and any focused input inside it — moves with the object across
// orderBy/insert/remove instead of being rebuilt in place). Global script; shares
// scope with codeExec.ts (interpreter) and init.ts (uiStatic). DOM-only; the server
// half is SsrRenderer.

function renderUi(): void {
    const context: ExecContext = { lastId: uiStatic.lastId };
    resetSlotPath(); // a fresh render tree starts at the root slot (defensive; push/pop is balanced)
    let result: ExecValue;
    try {
        result = callFunction(uiStatic.renderFn, context, []);
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
    // Mount on the #app container the SSR shell emits (present on every code page,
    // including a full-takeover render); fall back to body if it is somehow absent.
    updateChildren(document.getElementById("app") ?? document.body, [result]);
    syncPath();
    syncBreadcrumbs();
    syncScopeText("title", v => { document.title = v; });
    refreshErrorBanner();
    maybeRefetch(); // anything stale or missing → re-ask the server
}

// Keep the generic-UI breadcrumb chrome in step with the current `path` after a CLIENT-SIDE render.
// Breadcrumbs are SSR'd by C# (SsrRenderer.Breadcrumbs) OUTSIDE the #app reconciler root, so a
// client-side navigation (which only re-renders #app) would otherwise leave them stale. This rebuilds
// the trail from the `path` var, segment for segment, exactly as the server does — the same
// location-mirrors-URL invariant. No-op when the nav is absent (a full-custom render has no
// breadcrumbs) or when path is unavailable, so it only touches the chrome that exists. Hrefs are
// mount-prefixed (mountUrl), like the server's and like app-emitted links, so a breadcrumb click is
// itself a valid in-app navigation.
function syncBreadcrumbs(): void {
    const nav = document.querySelector("nav.breadcrumbs");
    if (nav == null) return;
    const item = uiStatic.state.scope.items["path"];
    if (item == null || item.value.type !== "text") return;

    // Build the desired trail: "Db" → "/", then one link per path segment at its cumulative path.
    const segs = item.value.value.split("/").filter(s => s !== "");
    const desired: { href: string; text: string }[] = [{ href: "/", text: "Db" }];
    let url = "";
    for (const seg of segs) { url += "/" + seg; desired.push({ href: url, text: seg }); }

    // Idempotent: skip the rebuild when the rendered trail already matches (renderUi runs on every
    // keystroke, so only touch the DOM when the path actually changed).
    const current = Array.from(nav.querySelectorAll("a")).map(a => a.textContent || "").join(" ");
    if (current === desired.map(d => d.text).join(" ")) return;

    nav.textContent = "";
    desired.forEach((d, i) => {
        if (i > 0) nav.appendChild(document.createTextNode(" / "));
        const a = document.createElement("a");
        a.setAttribute("href", mountUrl(d.href));
        a.textContent = d.text;
        nav.appendChild(a);
    });
}

// Routing writes real history entries: a code-driven `path` change pushes, so the
// browser's back/forward buttons work; popstate (init.ts) writes the var back.
//
// The app's `path` var is ROOT-RELATIVE (mount-unaware); the BROWSER URL carries the mount, so the
// pushState target is `base + path` (mountUrl). Compared against the live location.pathname (also
// mounted), so an unchanged path does not re-push. Identity when root-mounted.
function syncPath(): void {
    const item = uiStatic.state.scope.items["path"];
    if (item == null || item.value.type !== "text") return;
    const mounted = mountUrl(item.value.value);
    if (mounted !== location.pathname) history.pushState(null, "", mounted);
}

// ── client-side (SPA) navigation ──────────────────────────────────────────────────
//
// An in-app link click navigates CLIENT-SIDE instead of a full page reload: intercept the click,
// update the URL via the History API, and re-render the target view over the warm session. The server
// still SSRs every URL on a direct GET (deep-link/refresh unaffected); this only short-circuits the
// in-app click. Wired from init.ts as a single delegated listener (alongside popstate).

// Whether this page is the self-hosted GENERIC UI (set by the SSR bootstrap, beside initData/initUi/
// initBase). The first-class signal that the generic router is in play — the same _isGeneric C# uses
// to emit breadcrumbs. SPA interception is gated on it directly, not on sniffing the breadcrumb chrome.
declare const initGeneric: boolean;

// Handle a click that may be an in-app link. Only SAME-ORIGIN, in-mount, plain left-clicks on an
// anchor with no new-tab/download/external intent are taken over; everything else is left to the
// browser (the classic SPA footguns — modified clicks open tabs, off-origin/external/hash links must
// behave natively). If the link qualifies but the session cannot service it (the socket is not open,
// so a refetch for unshipped data could not run), it ALSO falls back to a full browser navigation
// rather than stranding the user on a changed URL with stale content.
function interceptNavigation(e: MouseEvent): void {
    // SCOPE: client-side nav is for the self-hosted GENERIC UI, which is refetch-complete by
    // construction (the synthesized router ships every schema descriptor and resolves any URL over the
    // shipped graph). A fully-CUSTOM `fn render()` (e.g. the operator designer) may read a deeply-nested
    // graph the refetch does not fully reconstruct cross-page, so it stays on full-page navigation (the
    // designer deliberately relies on a fresh SSR per route). initGeneric is the first-class signal
    // (injected by the SSR bootstrap, the same _isGeneric C# gates the breadcrumb chrome on); non-generic
    // → let the browser navigate (the safe default).
    if (!initGeneric) return;
    // A modified click (new tab / new window / download-gesture) or a non-primary button must keep
    // its native behavior. defaultPrevented: another handler already consumed it.
    if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;

    // The nearest enclosing anchor with an href (a click can land on a child of the link).
    const anchor = (e.target as Element | null)?.closest?.("a");
    if (!(anchor instanceof HTMLAnchorElement)) return;
    const href = anchor.getAttribute("href");
    if (href == null || href === "") return;

    // Explicit "let the browser handle it" signals: a new browsing context, a download, or an
    // externally-marked link.
    if (anchor.target && anchor.target !== "_self") return;
    if (anchor.hasAttribute("download")) return;
    if (/(^|\s)external(\s|$)/i.test(anchor.getAttribute("rel") ?? "")) return;

    // Resolve against the current document so a relative href is handled too; bail on a different
    // origin (the browser must do a real cross-origin navigation).
    const url = new URL(anchor.href, location.href);
    if (url.origin !== location.origin) return;

    // A pure in-page hash (same path + search, only the fragment differs) is native anchor behavior —
    // never a view navigation.
    if (url.pathname === location.pathname && url.search === location.search && url.hash !== "") return;

    // A same-origin link OUTSIDE this instance's mount (e.g. /apps/other from /apps/todo) is a real
    // navigation to a different app — let the browser load it. stripBase returns the path unchanged
    // when it does not carry our base, which would route it to this app's NotFound; guard on that.
    const base = basePrefix();
    if (base !== "" && url.pathname !== base && !url.pathname.startsWith(base + "/")) return;

    // The link is in-app. Take it over — unless the session can't service it, in which case fall back
    // to a full browser navigation (no preventDefault) so the user never lands on a stale view.
    if (!wsReady()) return;
    e.preventDefault();
    // The browser URL keeps the FULL target (mounted pathname + query + fragment) so it stays
    // shareable; the app's `path` var gets just the base-stripped pathname — exactly the value init.ts
    // and the popstate handler derive from location.pathname (search/hash are not part of a node path).
    navigateClientSide(url.pathname + url.search + url.hash, stripBase(url.pathname));
}

// Navigate to a target client-side: push a history entry for `pushUrl` (so the URL bar reflects the
// view and stays shareable), write the root-relative `pathVar` into the `path` var, invalidate `path`
// so its dependents recompute, and render. This is the SAME var-write + invalidate + render the
// popstate handler runs for Back/Forward, plus the pushState (popstate is the browser writing history
// for us; here we write it ourselves).
//
// FLASH GUARD: the target's object is often NOT in the client graph yet — the first paint ships only
// the starting view's data, and following a REFERENCE ships only the target's label, never the object.
// sys.resolve returns target:null for such an un-shipped (but valid) node — byte-identical to a
// genuinely-missing one — so an optimistic paint would render the router's NotFound branch, then the
// refetch reply would re-render the real view: a "Not found" FLASH on a perfectly good navigation.
// So: optimistic-paint immediately ONLY when the target already resolves to a renderable view locally
// (the instant feel for shipped data, e.g. a set-table row); otherwise HOLD the current view, fire the
// refetch, and let its reply paint the target once. NotFound is reserved for a genuinely-gone node —
// if the refetch returns and the target is STILL unresolvable, the render then paints NotFound (one
// clean paint). No router/interpreter change: this only consults the existing client sys.resolve.
function navigateClientSide(pushUrl: string, pathVar: string): void {
    const item = uiStatic.state.scope.items["path"];
    if (item == null) return;
    // pushState BEFORE the render: refetch sends location.pathname, so the URL must already be the
    // target when renderUi (→ maybeRefetch) runs.
    history.pushState(null, "", pushUrl);
    item.value = { type: "text", value: pathVar };
    invalidateVar("path");
    resetViewState();
    // Optimistic paint only when the target view is renderable from the data already on the client;
    // when it is not (a valid route whose object was not shipped) skip the paint — holding the current
    // view — and just fire the refetch (resetViewState set needsServerData), whose reply re-renders the
    // now-shipped target. Reserving NotFound for the post-refetch render kills the navigation flash.
    if (targetRenderableLocally(pathVar)) renderUi();
    else maybeRefetch();
}

// Is the target path already renderable from the client's shipped data — i.e. would the generic router
// paint a real view (not NotFound) for it right now? Resolves the path with the SAME client sys.resolve
// the router uses (a throwaway context, so it never disturbs uiStatic.lastId; depStack is empty here so
// it records no dependencies), then checks the kind-appropriate binding the router branches require:
// object/leaf need the target object present; set/ref/dict are owner-bound (need the PARENT present);
// notFound (a route invalid per the shipped descriptors) is NOT locally renderable — wait for the
// server's authoritative answer before painting NotFound. Any unexpected failure → treat as not
// locally renderable (fall back to the refetch-then-render path, which is always correct).
function targetRenderableLocally(pathVar: string): boolean {
    try {
        const probeCtx: ExecContext = { lastId: { value: uiStatic.lastId.value } };
        const call: CodeCall = { type: "call", fn: { type: "symbol", name: "resolve" },
            params: [{ type: "text", value: pathVar }] };
        const r = execResolve(call, uiStatic.renderFn.scope, probeCtx);
        if (r.type !== "object") return false;
        const kind = r.props["kind"]?.type === "text" ? r.props["kind"].value : "";
        const present = (v: ExecValue | undefined) => v != null && v.type === "object";
        switch (kind) {
            case "object": case "leaf": return present(r.props["target"]);
            case "set": case "ref": case "dict": return present(r.props["parent"]);
            default: return false; // notFound (or unknown) — let the refetch confirm before painting
        }
    } catch {
        return false;
    }
}

// Reset the client view state for a NAVIGATION (the same effect a full reload had, minus the reload):
//   1. Drop the component slot-cache. A component memoizes by its render-tree SLOT (position), NOT its
//      function identity — so two different components at the SAME slot (e.g. the root <SetTable> on
//      /notes vs the root <ObjectForm> on /notes/2) share a slot key, and the cache would hand the
//      target view the PREVIOUS view's component. A navigation rebuilds the render tree wholesale, so
//      the old slot assignments are meaningless; clearing them lets each component re-run (and resets
//      per-view component state, like a fresh load). Operates on uiStatic.cache directly (the same Map
//      memoize uses) — no interpreter change.
//   2. Force a refetch. The first paint shipped only the STARTING view's data, so the target's object
//      may be absent from the client `db` graph — and sys.resolve returns target:null for a missing
//      node (no "Value not available" throw), which would render NotFound instead of refetching.
//      needsServerData makes renderUi's trailing maybeRefetch re-ask the server (it renders over a
//      fresh store load), as the full-reload navigation this replaces did. The optimistic render paints
//      immediately from whatever IS shipped; the refetch reply re-renders with the complete data.
function resetViewState(): void {
    for (const key of Array.from(uiStatic.cache.keys()))
        if (key.startsWith("comp:")) uiStatic.cache.delete(key);
    needsServerData = true;
}

// Prefix a ROOT-RELATIVE url with the mount base (the client twin of SsrRenderer.MountUrl). Identity
// when root-mounted ("/") or the url is not root-relative (absolute/protocol-relative/fragment). Used
// for pushState targets and for app-emitted href/src in the reconciler (refreshAttributes).
function mountUrl(url: string): string {
    const b = basePrefix();
    if (b === "" || !url.startsWith("/") || url.startsWith("//")) return url;
    return url === "/" ? b : b + url;
}

// Strip the mount base off a FULL browser path to recover the app's root-relative `path` var (the
// inverse of mountUrl; the client twin of SsrRenderer.StripBase). "/apps/todo/notes/2" with base
// "/apps/todo" → "/notes/2"; "/apps/todo" → "/". Identity when root-mounted, or when the path does
// not carry the base (a domain-root deployment whose location is already root-relative).
function stripBase(fullPath: string): string {
    const b = basePrefix();
    if (b === "") return fullPath;
    if (fullPath === b) return "/";
    if (fullPath.startsWith(b + "/")) { const rest = fullPath.slice(b.length); return rest === "" ? "/" : rest; }
    return fullPath;
}

// The latest server-rejected mutation, surfaced as a dismissable banner. Lives on
// document.body, OUTSIDE the #app reconciler root, so a render never disturbs it.
function refreshErrorBanner(): void {
    const existing = document.getElementById("__error");
    if (uiStatic.lastError == null) { existing?.remove(); return; }
    const banner = existing ?? document.createElement("div");
    if (existing == null) {
        banner.id = "__error";
        banner.style.cssText =
            "position:fixed;top:0;left:0;right:0;z-index:9999;padding:0.5rem 1rem;" +
            "background:#b00020;color:#fff;font:14px system-ui,sans-serif;cursor:pointer;";
        banner.onclick = () => { uiStatic.lastError = null; refreshErrorBanner(); };
    }
    banner.textContent = `Change rejected: ${uiStatic.lastError} — click to dismiss`;
    document.body.appendChild(banner);
}

// Invoke a function — the render fn / a view / an event handler — by running its
// body directly over its params bound to `args` (a view's routed object or path;
// handlers pass none). The fn is a bare CodeFunction without a "type" discriminator,
// so it must not be routed back through executeValue.
function callFunction(fn: ExecFunction, context: ExecContext, args: ExecValue[] = []): ExecValue {
    const callScope: ExecScope = { parent: fn.scope, items: {} };
    for (let i = 0; i < args.length && i < fn.fn.params.length; i++)
        callScope.items[fn.fn.params[i].name] = { value: args[i], isReadOnly: true };
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

    // Index existing children: keyed elements by their data-key, the rest by tag name. A foreach row is
    // keyed by its member object's id; when a just-added member's transient negative id is remapped to its
    // real (positive) id, the desired child for that row now carries the POSITIVE key while the live node
    // still carries the negative one. Without bridging them the reconciler would treat the remapped row as a
    // new node and rebuild its subtree — destroying focus and any in-progress (uncommitted) input edit. So a
    // node keyed by a remapped negative id is ALSO indexed under its real id, keeping the SAME element across
    // the remap (the very identity-stability the data-key reconciliation exists to provide).
    const keyed: { [key: string]: ChildNode[] } = {};
    const unkeyed: { [name: string]: ChildNode[] } = {};
    for (const node of Array.from(parent.childNodes)) {
        const k = node.nodeType === 1 ? (node as Element).getAttribute("data-key") : null;
        if (k != null) {
            // A node keyed by a just-remapped transient (negative) id is indexed under its REAL id, the key
            // the desired child now carries — so the same element is reused across the remap (post-remap the
            // desired children always carry positive ids, so the negative key is never looked up).
            const remapped = uiStatic.state.localToServerIds[Number(k)];
            (keyed[remapped != null ? String(remapped) : k] ??= []).push(node);
        }
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
        // A <select>'s bound value selects the matching <option>, set AFTER its options exist
        // (updateChildren above builds them) — the client half of <select> binding, symmetric to
        // the SSR `selected` marking. .value selects by the option's value attribute.
        syncSelectValue(el, child);
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
        if ((tag.name === "input" || tag.name === "textarea") && name === "value") {
            const text = raw == null ? "" : String(raw);
            // Only assign when the value actually differs: the re-render that follows every
            // keystroke would otherwise reset .value unconditionally and the browser jumps the
            // caret to the end — unusable while typing (most visible in a multi-line textarea).
            if ((el as HTMLInputElement).value !== text) (el as HTMLInputElement).value = text;
            // A textarea's value is its content, not a `value` attribute (the property is
            // authoritative; `value` is non-standard there), so only an <input> mirrors it.
            if (tag.name === "input") { el.setAttribute("value", text); want.add("value"); }
            continue;
        }
        // A <select>'s `value` is not a real attribute (it drives option-selected); it is applied to
        // the .value property in syncSelectValue, AFTER the options exist — so skip it here.
        if (tag.name === "select" && name === "value") continue;
        if (raw == null || raw === false) { el.removeAttribute(name); continue; }
        // A navigational URL attribute (href/src) whose value is root-relative is mount-prefixed — the
        // client twin of SsrRenderer's edge prefixing, so the hydrated link matches the SSR one (the app
        // wrote `/notes/2`, both edges emit `/apps/todo/notes/2`). Identity when root-mounted.
        const out = raw === true ? "" : (name === "href" || name === "src") ? mountUrl(String(raw)) : String(raw);
        el.setAttribute(name, out);
        want.add(name);
    }
    for (const attr of Array.from(el.attributes))
        if (attr.name !== "data-key" && !want.has(attr.name)) el.removeAttribute(attr.name);
}

// A text input's value is always a string; coerce it back to the bound value's type so binding an
// input to a non-text var preserves the type (e.g. a port var stays an int, so sys.create receives
// an int — not the string "9100"). Unparseable int input falls back to 0.
function coerceInputValue(raw: string, current: ExecValue | undefined): ExecValue {
    if (current?.type === "int") {
        const n = parseInt(raw, 10);
        return { type: "int", value: isNaN(n) ? 0 : n };
    }
    return { type: "text", value: raw };
}

// A <select>'s bound value reflected onto its .value property (which selects the matching <option>),
// the client twin of the SSR `selected` marking. Set after the options are reconciled (applyNode
// calls this post-updateChildren). The no-op guard mirrors the input/textarea one — only assign when
// it differs, so an open dropdown / repeated render is never disturbed.
function syncSelectValue(el: HTMLElement, tag: ExecTag): void {
    if (tag.name !== "select") return;
    const value = tag.attributes["value"];
    if (value == null) return;
    const v = value.value;
    if (v.type === "fn" || v.type === "sysFn" || v.type === "object" || v.type === "array") return;
    const text = v.type === "null" ? "" : String((v as ExecInt | ExecBool | ExecText).value);
    if ((el as HTMLSelectElement).value !== text) (el as HTMLSelectElement).value = text;
}

// Two-way binding + click handlers. A bound value/checked attribute whose result
// carries a setValue closure writes back to the model and re-renders.
function wireEvents(el: HTMLElement, tag: ExecTag): void {
    const checked = tag.attributes["checked"];
    const value = tag.attributes["value"];
    // Two-way binding for <input> and <textarea> (checked is input-only; a textarea binds
    // only its value — el.value is its text either way).
    if ((tag.name === "input" || tag.name === "textarea") && (checked?.setValue || value?.setValue)) {
        (el as HTMLInputElement).oninput = () => {
            const input = el as HTMLInputElement;
            if (checked?.setValue) checked.setValue({ type: "bool", value: input.checked });
            else if (value?.setValue) value.setValue(coerceInputValue(input.value, value.value));
            renderUi();
        };
    } else {
        (el as HTMLInputElement).oninput = null;
    }

    // Two-way binding for <select>: a change picks a new option, so write the chosen value back
    // (coerced to the bound var's type, like an input — option values are always DOM strings) and
    // re-render. onchange (not oninput) is the select's commit event.
    if (tag.name === "select" && value?.setValue) {
        (el as HTMLSelectElement).onchange = () => {
            value.setValue(coerceInputValue((el as HTMLSelectElement).value, value.value));
            renderUi();
        };
    } else if (tag.name === "select") {
        (el as HTMLSelectElement).onchange = null;
    }

    const onClick = tag.attributes["onClick"]?.value;
    if (onClick != null && onClick.type === "fn") {
        const fn = onClick;
        // Handlers may be side-effecting (assignments, factory calls): bypass the memo
        // cache while one runs, so a repeated call never skips its effects.
        // A handled click is CONSUMED — stop it bubbling first, so a nested control (e.g. a
        // Remove button inside a whole-row navigable link) fires its OWN handler and does not
        // also trigger an ancestor's onClick (the row-link navigation). Native anchors without an
        // onClick handler still navigate as usual (this only guards code-wired handlers).
        el.onclick = (e: MouseEvent) => {
            e.stopPropagation();
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
