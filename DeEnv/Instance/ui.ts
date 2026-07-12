// Client DOM runtime: turns the render fn's ExecTag tree into DOM and keeps it in
// sync. Ported from the app14 prototype and extended with identity-keyed
// reconciliation (foreach rows carry the member object's id as data-key, so a row's
// element — and any focused input inside it — moves with the object across
// orderBy/insert/remove instead of being rebuilt in place). Global script; shares
// scope with codeExec.ts (interpreter) and init.ts (uiStatic). DOM-only; the server
// half is SsrRenderer.

function renderUi(): void {
    const result = buildRenderTree();
    // null = the render fn read unshipped data OUTSIDE any computation boundary (the top-level VNA
    // throw): keep the current DOM and let the refetch (kicked off by buildRenderTree) re-render with
    // the data present.
    if (result == null) return;
    commitRender(result);
    maybeRefetch(); // anything stale or missing → re-ask the server
}

// Run the render fn to its ExecValue tree (DOM-free — the tree is committed separately by
// commitRender). Returns null when a read OUTSIDE any computation boundary hit "Value not available":
// that's unrecoverable locally, so it sets needsServerData + fires the refetch and the caller keeps the
// current DOM. (A VNA INSIDE a memoize is swallowed there to an empty result and ALSO sets
// needsServerData — see codeExec.ts; the tree still builds, just incomplete. renderUiSpeculative reads
// needsServerData to tell a complete tree from such an incomplete one.)
//
// SPECULATIVE = best-effort: an OPTIMISTIC pre-refetch render (renderUiSpeculative) runs over data the
// client may not hold yet, where a read of un-shipped data does NOT always surface as a clean VNA. The
// memoize swallow turns an un-shipped sys.extent/sys.schema MISS into an empty `nothing` (not a throw),
// and using that `nothing` in a position that wants a real value — e.g. `foreach c in sys.extent(...)`
// (the generic RefEditor's candidate loop) — then throws a NON-VNA error ("foreach target is not a
// collection."). So in speculative mode ANY throw is treated exactly like a top-level VNA: the data is
// incomplete, hold the current view and let the trailing refetch paint the target over complete data
// (which a reload proves works). This is the floor that guarantees a client-side navigation NEVER strands
// the user on a changed URL with a frozen view, whatever the optimistic render hit. The COMMITTING caller
// (renderUi, default speculative=false) still rethrows a non-VNA error — over its complete data that is a
// genuine bug and must surface, not be silently hidden.
function buildRenderTree(speculative: boolean = false): ExecValue | null {
    const context: ExecContext = { lastId: uiStatic.lastId, ambient: rootAmbient() };
    resetSlotPath(); // a fresh render tree starts at the root slot (defensive; push/pop is balanced)
    callDepth = 0; // same defensive reset for the call-depth guard (M12 FG) — push/pop is balanced too
    conflictSurfacedThisRender.clear(); // this render re-decides which conflicts a resolver "door" surfaces
    try {
        return callFunction(uiStatic.renderFn, context, []);
    } catch (e) {
        if (speculative || (e instanceof Error && e.message === "Value not available")) {
            needsServerData = true;
            maybeRefetch();
            return null;
        }
        throw e;
    }
}

// Commit a rendered tree to the DOM and resync the out-of-#app chrome. Shared by the committing
// renderUi and the speculative commit (renderUiSpeculative) so both paint identically.
function commitRender(result: ExecValue): void {
    // Mount on the #app container the SSR shell emits (present on every code page,
    // including a full-takeover render); fall back to body if it is somehow absent.
    updateChildren(document.getElementById("app") ?? document.body, [result]);
    syncPath();
    syncBreadcrumbs();
    syncScopeText("title", v => { document.title = v; });
    refreshErrorBanner();
    consumeScrollReset(); // a forward nav whose target just painted scrolls to the top
    focusNewCreateForm(); // a just-opened create form scrolls into view and takes focus
    mountWorkbenchInstances(); // M12 W1a (workbench.ts) — mount/remount/dispose the component-workbench's live instances
    applySelectionChrome(); // M12 S4a — re-derive canvas is-selected chrome from the current selection every commit
    checkRevealSelected(); // M12 S5b — arm a scroll-to-row on a REMOTE selection change (a palette insert)
    consumeSelectionScroll(); // M12 S4a — scroll the selected editor row into view, armed only on a real change
}

// Speculatively render the target and commit it ONLY if it rendered COMPLETELY from already-local data.
// This is the SECOND flash gate (targetRenderableLocally is the first): targetRenderableLocally only asks
// "is the route's target object present", which is true for the designer's thin design-row leaf — yet the
// deep `/designs/<id>` editor then reads the design's UNSHIPPED types/code, throws VNA, and memoize
// swallows it to an EMPTY tree, so the operator saw a blank editor for one frame before the refetch filled
// it. This renders the target into a throwaway tree first (in SPECULATIVE mode — see buildRenderTree) and
// checks whether building it needed server data: if the build returned null (it threw — a top-level VNA OR,
// over incomplete data, a non-VNA error like iterating a swallowed-empty sys.extent) OR set needsServerData
// (a swallowed VNA), the tree is incomplete, so DISCARD it, hold the current #app, and let the trailing
// refetch paint the complete tree once. If it did not, the tree is complete (every generic case with its
// props shipped, e.g. a set-row nav) → commit it for the instant paint. Returns whether it committed (so
// the caller knows the view changed). Render-mode-agnostic: the test is "is the data complete", never
// "which UI mode". Crucially, building speculatively NEVER throws out of here, so navigateClientSide always
// reaches its floor maybeRefetch — the user is never stranded on a changed URL with a frozen view.
function renderUiSpeculative(): boolean {
    // Isolate THIS render's data-completeness: needsServerData may already be true (resetViewState set it
    // as the always-refetch floor), so save it, clear it, and read back whether building the tree set it.
    const before = needsServerData;
    needsServerData = false;
    const result = buildRenderTree(true); // best-effort: a throw over incomplete data → null (incomplete)
    const incomplete = result == null || needsServerData;
    // Restore the floor (a committed instant paint still refetches for authoritative state, exactly as the
    // pre-speculative instant path did); an incomplete build already needs the server anyway.
    needsServerData = before || incomplete;
    if (incomplete) return false; // hold the current view — the refetch reply paints the target
    commitRender(result!);
    return true;
}

// The generic-UI breadcrumb/title ROOT label: the instance display name (window.initAppName),
// humanized (e.g. "devlog" → "Devlog"), so the root reads as the app's identity rather than the
// internal root-type name. Falls back to "Db" when no name was injected — byte-identical to the
// server's SsrRenderer.RootLabel.
function rootLabel(): string {
    const n = typeof initAppName === "string" ? initAppName : "";
    return n.length > 0 ? humanizeText(n) : "Db";
}

// The breadcrumb/title LABEL for the final segment of a cumulative `urlPath` — the client twin of
// CodeExecutor.SegmentLabel: a MEMBER route (a set member / object-dict entry — kind=object on a
// non-root path) shows the bound object's labelProp value; a SCALAR-DICT entry (kind=leaf — the
// user's own literal key) shows the RAW segment verbatim (never humanized — "ORD-001" stays as is);
// anything else (a prop-name route segment) → null, so the caller humanizes the raw segment. Resolves
// over the SAME client sys.resolve the router uses (a throwaway context, so it never disturbs
// uiStatic.lastId and records no deps), reading the labelProp off the shipped `sys.schema` descriptor
// — and (for an ancestor object on a deep route) off the labelProp leaf the server now records and
// ships. Returns null (humanize the raw segment) on any miss/throw, so a thin/un-shipped node never
// blanks the trail.
function segmentLabel(urlPath: string): string | null {
    try {
        const probeCtx: ExecContext = { lastId: { value: uiStatic.lastId.value } };
        const call: CodeCall = { type: "call", fn: { type: "symbol", name: "resolve" },
            params: [{ type: "text", value: urlPath }] };
        const r = execResolve(call, uiStatic.renderFn.scope, probeCtx);
        if (r.type !== "object") return null;
        const kind = r.props["kind"];
        if (kind?.type !== "text") return null;
        // A scalar-dict entry: the segment IS the user's literal key — show it verbatim, never humanized.
        if (kind.value === "leaf") {
            const segs = urlPath.split("/").filter(s => s !== "");
            return segs.length > 0 ? segs[segs.length - 1] : null;
        }
        if (kind.value !== "object") return null;
        const target = r.props["target"];
        const typeName = r.props["typeName"];
        if (target?.type !== "object" || typeName?.type !== "text") return null;
        const desc = resolveDescriptor(typeName.value, probeCtx);
        const labelProp = propText(desc, "labelProp");
        if (labelProp === "") return null;
        const v = target.props[labelProp];
        return v != null && v.type === "text" && v.value.length > 0 ? v.value : null;
    } catch {
        return null;
    }
}

// Keep the generic-UI breadcrumb chrome AND the tab title in step with the current `path` after a
// CLIENT-SIDE render. Breadcrumbs are SSR'd by C# (SsrRenderer.Breadcrumbs) OUTSIDE the #app
// reconciler root, and the generic title is set in the SSR <head>, so a client-side navigation (which
// only re-renders #app) would otherwise leave both stale. This rebuilds the LABELED trail from the
// `path` var — root label + one label per segment (a member's labelProp value, else the humanized
// segment) — byte-identically to the server, the same location-mirrors-URL invariant. No-op when the
// nav is absent (a full-custom render has no breadcrumbs) or when path is unavailable. Hrefs are
// mount-prefixed (mountUrl), like the server's and like app-emitted links, so a breadcrumb click is
// itself a valid in-app navigation.
function syncBreadcrumbs(): void {
    const nav = document.querySelector("nav.breadcrumbs");
    if (nav == null) return;
    const item = uiStatic.state.scope.items["path"];
    if (item == null || item.value.type !== "text") return;

    // Build the desired LABELED trail: the root label at "/", then one link per path segment at its
    // cumulative path, its text resolved to the segment's label (object label or humanized prop).
    const segs = item.value.value.split("/").filter(s => s !== "");
    const desired: { href: string; text: string }[] = [{ href: "/", text: rootLabel() }];
    let url = "";
    for (const seg of segs) {
        url += "/" + seg;
        desired.push({ href: url, text: segmentLabel(url) ?? humanizeText(seg) });
    }

    // The generic tab title mirrors the labeled trail (the server set the same string in the SSR
    // <head>; SPA nav re-renders only #app, so update it here for the generic case). A custom render
    // (no breadcrumb nav) keeps its own `title` scope var via commitRender's syncScopeText.
    document.title = desired.map(d => d.text).join(" / ");

    // Idempotent: skip the rebuild when the rendered trail already matches (renderUi runs on every
    // keystroke, so only touch the DOM when the path/labels actually changed).
    const current = Array.from(nav.querySelectorAll("a")).map(a => a.textContent || "").join(" ");
    if (current === desired.map(d => d.text).join(" ")) return;

    nav.textContent = "";
    desired.forEach((d, i) => {
        if (i > 0) nav.appendChild(document.createTextNode(" / "));
        const a = document.createElement("a");
        // Root-relative by construction (built as "/" + segments), so scheme-guard-exempt — the twin
        // of SsrRenderer.Breadcrumbs; not a missed refreshAttributes sink.
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
//
// UNIFORM across UI modes — generic AND fully-custom (the operator designer). Both re-render over the
// warm session the same way: the refetch fires on the user's OWN navigation over a FRESH store load,
// so a custom render reading a deeply-nested cross-page graph (the designer's `/designs` → `/designs/<id>`
// type/prop editor) is reconstructed server-side and shipped via the same memo cache the first paint
// uses. The flash guard (renderUiSpeculative — paint instantly only when the target builds COMPLETELY
// from local data, else hold for the refetch) and the `wsReady()` not-ready fallback below are
// render-agnostic — they consult the live URL, the warm session, and the actual render over the shipped
// data — so the same safety applies to every page. There is no per-mode gate.

// Handle a click that may be an in-app link. Only SAME-ORIGIN, in-mount, plain left-clicks on an
// anchor with no new-tab/download/external intent are taken over; everything else is left to the
// browser (the classic SPA footguns — modified clicks open tabs, off-origin/external/hash links must
// behave natively). If the link qualifies but the session cannot service it (the socket is not open,
// so a refetch for unshipped data could not run), it ALSO falls back to a full browser navigation
// rather than stranding the user on a changed URL with stale content.
function interceptNavigation(e: MouseEvent): void {
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
// FLASH GUARD: the target view often can't render fully from the data on the client yet — the first
// paint ships only the starting view's data, following a REFERENCE ships only the target's label (not
// the object), and the designs LIST ships each design's label but NOT its types/code. An optimistic
// paint over such an incomplete graph either renders NotFound (an un-shipped node resolves to
// target:null) or paints a present-but-thin object as an EMPTY/partial view (the designer's deep editor
// reads design.types, throws VNA, memoize swallows it) — then the refetch re-renders the real view: a
// FLASH on a perfectly good navigation. So renderUiSpeculative renders the target into a throwaway tree
// and commits it ONLY if it built COMPLETELY from local data; otherwise it holds the current view and
// lets the trailing refetch paint the target once. This is data-completeness-driven, not mode- or
// kind-driven — so a generic set-row nav (props shipped) stays INSTANT while the designer's thin-target
// nav HOLDS. No router/interpreter change: it only runs the existing render fn.
//
// SCROLL: a forward nav resets window scroll to the top once the target paints (full reloads did this;
// SPA nav must do it explicitly). Armed here, consumed by commitRender whenever the target actually
// paints — the speculative commit (complete) or the trailing refetch's renderUi (incomplete/held). NOT
// armed on popstate (the browser restores scroll for Back/Forward) nor on keystroke re-renders.
function navigateClientSide(pushUrl: string, pathVar: string): void {
    const item = uiStatic.state.scope.items["path"];
    if (item == null) return;
    // pushState BEFORE the render: refetch sends location.pathname, so the URL must already be the
    // target when renderUi (→ maybeRefetch) runs.
    history.pushState(null, "", pushUrl);
    item.value = { type: "text", value: pathVar };
    invalidateVar("path");
    resetViewState();
    armScrollReset(); // forward nav → scroll to top when the target view paints
    // Optimistic-paint only when the route resolves to a renderable view locally — an un-shipped (but
    // valid) node resolves to target:null, byte-identical to a genuinely-missing one, and would otherwise
    // paint the router's NotFound branch (which builds "completely", so the completeness check alone
    // wouldn't catch it). When it resolves renderable, renderUiSpeculative additionally HOLDS if the
    // target's render is incomplete (a present-but-thin object — the designer's deep editor). Either
    // hold → the refetch reply paints the target once. NotFound stays reserved for a genuinely-gone node.
    if (targetRenderableLocally(pathVar)) renderUiSpeculative();
    maybeRefetch();        // floor refetch for authoritative state (held targets paint from its reply)
}

// Is the target path renderable from the client's shipped data WITHOUT painting an unconfirmed NotFound —
// i.e. would the generic router bind a real view (not NotFound) for it right now? Resolves the path with
// the SAME client sys.resolve the router uses (a throwaway context, so it never disturbs uiStatic.lastId;
// depStack is empty here so it records no dependencies), then checks the kind-appropriate binding the
// router branches require: object/leaf need the target object present; set/ref/dict are owner-bound (need
// the PARENT present); notFound (a route invalid per the SHIPPED descriptors — which may just mean the
// node was not shipped) is NOT locally renderable — wait for the server's authoritative answer before
// painting NotFound. This is the FIRST gate (don't paint an unconfirmed NotFound); renderUiSpeculative is
// the SECOND (don't paint an incomplete view). Any unexpected failure → treat as not locally renderable
// (fall back to the refetch-then-render path, which is always correct).
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

// ── scroll reset on forward navigation ──────────────────────────────────────────────
//
// A full page reload reset the window scroll; an SPA navigation does not, so a deep nav from a scrolled
// list would otherwise land mid-page. This arms a one-shot reset on a FORWARD navigation (a link click
// — navigateClientSide), consumed when the target view paints (commitRender): the speculative commit
// when the target is fully local, else the refetch reply's renderUi when it was held. Deliberately NOT
// armed for popstate (Back/Forward — the browser restores the prior scroll position there) nor for the
// keystroke re-renders renderUi runs on every edit (scrolling then would yank the page mid-typing).
let pendingScrollReset = false;
function armScrollReset(): void { pendingScrollReset = true; }
function consumeScrollReset(): void {
    if (!pendingScrollReset) return;
    pendingScrollReset = false;
    window.scrollTo(0, 0);
}

// ── M12 S4a — canvas click → selection ────────────────────────────────────────────────
//
// sys.renderTree stamps every canvas element with `data-node={rowId}` (CANVAS-1's provenance spine); a
// container opts a whole subtree INTO click-to-select by carrying `selecttarget="<uiVarName>"`, naming
// the top-scope ui var the resolved row id is written into. This is GENERAL framework infrastructure —
// any renderTree consumer can opt in — not designer-specific; an app that never emits the marker (every
// app but the designer, today) makes this listener a pure no-op on every click. A workbench card's live
// instance is a structurally separate container that never carries the marker (docs/plans/
// m12-remaining.md §1's v1 boundary), so it's excluded with no special-casing here.
//
// Delegated on document (the interceptNavigation precedent — one listener, works for content added by
// any future render). Resolves the nearest data-node ancestor of the click target, WITHIN the marked
// container only (the walk stops at the container, never past it, so a data-node elsewhere on the page
// can't leak in); a click that lands inside the container but off any data-node element (empty canvas
// padding) resolves to 0 — deselect.
//
// Swallow in-app anchor navigation inside a selection surface — the W1b anchor-containment pattern
// (workbench.ts:429-431) verbatim: a canvas element that happens to be a literal `<a href>` (or wraps
// one) would otherwise bubble to the PAGE's own document-level interceptNavigation listener and
// navigate the operator's whole designer away from a click meant to SELECT, not follow the link — S4a
// just made a canvas click first-class, so this containment is now load-bearing here too, exactly as it
// already is for a workbench preview card. preventDefault alone would suffice (interceptNavigation's own
// first line bails on `e.defaultPrevented` — but only if THIS listener runs first; see the registration
// order note in init.ts) — stopPropagation is kept for the verbatim parity and belt-and-braces. The
// selection resolution below still runs regardless (this is the SAME handler, not a second listener) —
// the click still SELECTS the anchor's own row, it just never navigates.
function resolveCanvasSelection(e: MouseEvent): void {
    const start = e.target as Element | null;
    if (start == null) return;
    const container = start.closest("[selecttarget]");
    if (container == null) return;
    const anchor = start.closest("a");
    if (anchor instanceof HTMLAnchorElement && container.contains(anchor)) { e.preventDefault(); e.stopPropagation(); }
    const varName = container.getAttribute("selecttarget");
    if (varName == null || varName === "") return;

    let nodeId = "";
    for (let el: Element | null = start; el != null; el = el.parentElement) {
        if (el.hasAttribute("data-node")) { nodeId = el.getAttribute("data-node") ?? ""; break; }
        if (el === container) break;
    }
    writeSelectedNode(varName, nodeId === "" ? 0 : (parseInt(nodeId, 10) || 0));
}

// Write a resolved selection id into the named top-scope var and repaint. This write happens OUTSIDE
// any Code execution (a raw DOM click, not a handler call — there's no assignment node to run it
// through), so it does directly what executeAssignment does for a top-scope symbol (codeExec.ts
// executeAssignment ~191: item.value = …; itemScope.isTop → invalidateVar), then renders. No-ops when
// the id is unchanged — a re-click, or a call reached from an unrelated repaint — so the scroll pass
// only ever arms on a genuine change (an unrelated re-render must not steal or drop the selection, nor
// re-trigger a scroll).
//
// TWO ASSUMPTIONS the 0-as-"nothing-selected" sentinel relies on (ui-arch review, both confirmed against
// the store, not just asserted):
//  (1) sys.id() never returns 0 for a real object. Every id comes from one store-wide monotonic counter
//      that starts at 1 (JsonFileInstanceStore.cs BuildInitialDb: "the root is id 1 ... starts at 1, so
//      they mint 2, 3, …"; BuildSeededDoc seeds doc.NextId from the authored ids' own max), and every
//      write path that could introduce a literal id rejects a non-positive one outright
//      (InstanceDescriptionLoader.cs:137-139 "initialData id ... is not a positive integer";
//      JsonFileInstanceStore.cs:2103 "A literal object id must be positive"). 0 is unreachable as a real
//      row id, so it can never collide with an actual selection.
//  (2) ids never restart per design. The whole designer instance is ONE store with ONE global NextId
//      counter shared by every extent (Design, MetaNode, MetaFn, … all mint from it) — there is no
//      per-type or per-design counter — so two different designs' MetaNode rows can never share an id.
//      A stale `selectedNode` left over from a design the operator has since navigated away from
//      therefore matches nothing on the design now showing (applySelectionChrome's querySelectorAll
//      simply finds no [data-node] equal to it) rather than spuriously highlighting an unrelated row.
function writeSelectedNode(varName: string, id: number): void {
    const item = uiStatic.state.scope.items[varName];
    if (item == null || item.isReadOnly) return;
    const current = item.value.type === "int" ? item.value.value : null;
    if (current === id) return;
    item.value = { type: "int", value: id };
    invalidateVar(varName);
    armSelectionScroll();
    renderUi();
}

// Canvas highlight chrome — a commitRender-end post-pass (the mountWorkbenchInstances precedent), run on
// EVERY commit, not just a selection change: for every `[selecttarget]` container, re-derive which of
// its `[data-node]` descendants should carry `is-selected` from the named var's CURRENT value, never
// from stale DOM state. This is what makes selection SURVIVE an unrelated structural re-render (elements
// get reconciled/replaced; the class is reapplied fresh every time) and what gives a loop selection its
// N:1 group outline (every instance sharing the template row's data-node matches together). The
// tree-editor side highlights itself — ordinary reactive deenv code (renderNodeEditor's nodeClass) — so
// this pass only ever touches canvas-side chrome.
function applySelectionChrome(): void {
    document.querySelectorAll("[selecttarget]").forEach(container => {
        const varName = container.getAttribute("selecttarget");
        const item = varName != null ? uiStatic.state.scope.items[varName] : undefined;
        const selected = item != null && item.value.type === "int" ? item.value.value : 0;
        container.querySelectorAll("[data-node]").forEach(el => {
            el.classList.toggle("is-selected", selected !== 0 && el.getAttribute("data-node") === String(selected));
        });
    });
}

// ── M12 S4b — Escape deselects ──────────────────────────────────────────────────────
//
// General framework infrastructure like resolveCanvasSelection/applySelectionChrome above (not
// designer-specific): clears EVERY `[selecttarget]` container's named var, so a page without the
// marker (every app but the designer, today) makes this listener a pure no-op, and a future
// multi-canvas page clears all of them together. Goes through writeSelectedNode (the same
// no-op-when-unchanged, arm-scroll-then-render path a canvas click uses) so an Escape with nothing
// selected does nothing, and a genuine deselect repaints exactly like clicking empty canvas space
// does today.
//
// M12 S4b review fold: a row's own fields are ordinary text-entry controls now sitting inside a
// selectable row, and Escape is a common "cancel/blur this edit" gesture WHILE typing in one (a
// browser-native expectation for text inputs) — clearing the selection out from under that keystroke
// would be a surprising, unrelated side effect. Skip while focus is in a text-entry element; Escape
// still deselects everywhere else (empty space, a button, the page body).
function handleEscapeDeselect(e: KeyboardEvent): void {
    if (e.key !== "Escape") return;
    const target = e.target as Element | null;
    const tag = target?.tagName;
    if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;
    document.querySelectorAll("[selecttarget]").forEach(container => {
        const varName = container.getAttribute("selecttarget");
        if (varName != null && varName !== "") writeSelectedNode(varName, 0);
    });
}

// Scroll-to-row: the armScrollReset/consumeScrollReset idiom above, for a selection CHANGE instead of a
// forward nav. Armed only by writeSelectedNode's actual-change branch; consumed at the next commitRender
// by finding whichever tree-editor row now carries `is-selected` (scoped to the two places
// renderNodeEditor runs — the main render tree and a component's own body) and scrolling it into view.
// "nearest" avoids yanking the page when the row is already visible.
let pendingSelectionScroll = false;
function armSelectionScroll(): void { pendingSelectionScroll = true; }
function consumeSelectionScroll(): void {
    if (!pendingSelectionScroll) return;
    pendingSelectionScroll = false;
    const el = document.querySelector(".render-tree .is-selected, .fn-body .is-selected");
    el?.scrollIntoView({ block: "nearest" });
}

// M12 S5b review fold #5 — reveal-scroll for a REMOTE selection change (a palette insert): S4b's row-
// click deliberately does NOT scroll (the operator is already at the row they clicked — see
// writeSelectedNode/that rule's own comment), but a palette click is the case that rule named as
// excluded: the new row can land anywhere in a long tree with zero visible confirmation otherwise.
// General framework chrome, not designer-specific (the precedent every other selection/Escape pass here
// sets): the app bumps its own `revealSelected` int ui var on each remote-selecting action; a transition
// arms the SAME scroll-to-row pass S4a/S4b already consume at the next commit. A page without that var
// reads a steady 0 here and this is a permanent no-op.
let lastRevealSelected = 0;
function checkRevealSelected(): void {
    const item = uiStatic.state.scope.items["revealSelected"];
    const v = item != null && item.value.type === "int" ? item.value.value : 0;
    if (v !== lastRevealSelected) { lastRevealSelected = v; armSelectionScroll(); }
}

// ── focus a newly-opened create form ────────────────────────────────────────────────
//
// The generic SetTable/DictTable keep the read-only table visible and reveal the create form BELOW it
// (rather than swapping the table out), so on a long list — or a small screen — the form can open below
// the fold and a "New" click looks like nothing happened. When a create form NEWLY appears (the count of
// .create-form under #app rises), bring it into view and focus its first field so the operator can type
// immediately. Gated on a count INCREASE, never the keystroke re-renders that follow (those keep the
// count flat) — focusing on every render would yank focus mid-typing, the same hazard consumeScrollReset
// avoids. ponytail: focuses the last create form in document order on an increase — exact for the common
// single-form-open case (every generic collection page, incl. devlog); a page with two forms open at once
// could focus the wrong one, harmless (the operator just clicks the field they want).
let openCreateForms = 0;
function focusNewCreateForm(): void {
    const forms = document.querySelectorAll<HTMLElement>("#app .create-form");
    if (forms.length > openCreateForms) {
        const form = forms[forms.length - 1];
        form.scrollIntoView({ block: "nearest" });
        form.querySelector<HTMLInputElement>("input, textarea, select")?.focus({ preventScroll: true });
    }
    openCreateForms = forms.length;
}

// Reset the client view state for a NAVIGATION (the same effect a full reload had, minus the reload):
//   1. Drop the component slot-cache. A component memoizes by its render-tree SLOT (position), NOT its
//      function identity — so two different components at the SAME slot (e.g. the root <SetTable> on
//      /notes vs the root <ObjectForm> on /notes/2) share a slot key, and the cache would hand the
//      target view the PREVIOUS view's component. A navigation rebuilds the render tree wholesale, so
//      the old slot assignments are meaningless; clearing them lets each component re-run (and resets
//      per-view component state, like a fresh load). Operates on uiStatic.cache directly (the same Map
//      memoize uses) — no interpreter change. (Plain-function `fn:` results — a custom render's per-route
//      page functions — are dropped on the REFETCH reply instead, after the fresh data lands; see ws.ts.)
//      A discarded SPECULATIVE render (the target was incomplete) leaves the target view's just-built
//      `comp:` entries in the cache — harmless: the held current view never re-renders, and the refetch
//      reply's renderUi re-resolves them by slot for the target.
//   2. Force a refetch. The first paint shipped only the STARTING view's data, so the target's object
//      (or the designer's design types/code) may be absent from the client `db` graph. needsServerData
//      makes the trailing maybeRefetch re-ask the server (it renders over a fresh store load), as the
//      full-reload navigation this replaces did — so the floor refetch fires after BOTH a complete
//      instant paint (for authoritative state) and a held incomplete target (whose reply paints it).
function resetViewState(): void {
    for (const key of Array.from(uiStatic.cache.keys()))
        if (key.startsWith("comp:")) uiStatic.cache.delete(key);
    // Reachability GC (client data layer, the LAST slice): a navigation is the point a whole view's data
    // goes out of scope, so collect the now-unreachable objects/arrays the prior views accumulated in
    // uiStatic.state (mergeState only grows it). Run AFTER dropping the `comp:` entries above, so the old
    // view's component-held data is no longer cache-reachable and is actually collected; what is still live
    // (the db graph via scope, surviving cache results, the pending journal) is marked and kept. Safe because
    // the round-trip re-pulls anything the target view needs (the forced refetch below + slices 1a–4). See
    // sweepUnreachable (dt.ts) for the root set. Nav is the right cadence — never the per-keystroke renderUi.
    sweepUnreachable();
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

// An inline event-handler attribute name (onclick, onmouseover, onload, …) — the client twin of
// SsrRenderer.IsEventAttribute. Only ever legitimate as a `fn` value (refreshAttributes already skips
// those above), so a scalar value under this name is dropped by the caller regardless of type.
function isEventAttribute(name: string): boolean {
    return name.length >= 3 && name.slice(0, 2).toLowerCase() === "on";
}

// A URL whose scheme lets an attacker run script from a clicked/loaded link — the client twin of
// SsrRenderer.HasDangerousScheme. Browsers strip embedded TAB/CR/LF anywhere in a URL before
// scheme-sniffing (the "java\tscript:" bypass) and ignore leading ASCII whitespace/control
// characters, so both are stripped/trimmed here before the case-insensitive scheme match.
const DANGEROUS_SCHEMES = ["javascript:", "data:", "vbscript:"];

function hasDangerousScheme(url: string): boolean {
    const stripped = url.replace(/[\t\r\n]/g, "");
    let start = 0;
    while (start < stripped.length && stripped.charCodeAt(start) <= 0x20) start++;
    const trimmed = stripped.slice(start).toLowerCase();
    return DANGEROUS_SCHEMES.some(scheme => trimmed.startsWith(scheme));
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
            "background:#b00020;color:#fff;font:14px system-ui,sans-serif;cursor:pointer;" +
            "white-space:pre-wrap;";
        banner.onclick = () => { uiStatic.lastError = null; refreshErrorBanner(); };
    }
    banner.textContent = `Change rejected: ${uiStatic.lastError} — click to dismiss`;
    document.body.appendChild(banner);
}

// Invoke a function — the render fn / a view / an event handler — by running its
// body directly over its params bound to `args` (a view's routed object or path;
// handlers pass none). The fn is a bare CodeFunction without a "type" discriminator,
// so it must not be routed back through executeValue.
//
// Call-depth zero-point note (M12 FG, arch review — noted, not restructured): unlike the C# SSR twin
// (CodeExecutor.InvokeFunction, which bypasses RunBody so render() itself is depth 0), this DOES route
// through runBody, so render() itself is depth 1 here. Immaterial at the 256 threshold; see
// InvokeFunction's matching note for why the zero-points are left asymmetric rather than restructured.
function callFunction(fn: ExecFunction, context: ExecContext, args: ExecValue[] = []): ExecValue {
    const callScope: ExecScope = { parent: fn.scope, items: {} };
    for (let i = 0; i < args.length && i < fn.fn.params.length; i++)
        callScope.items[fn.fn.params[i].name] = { value: args[i], isReadOnly: true };
    return runBody(fn, callScope, context);
}

function syncScopeText(name: string, apply: (v: string) => void): void {
    const item = uiStatic.state.scope.items[name];
    if (item != null && item.value.type === "text") apply(item.value.value);
}

// The event-wiring step applyNode takes for each reconciled tag — a seam (M12 W1a review, arch fix 2):
// the page's own reconciliation always wants the real `wireEvents` (the default), but a caller composing
// the SAME reconciler over an isolated tree — the component workbench's mounted instance (W1a: no wiring
// at all; W1b: dispatch through the sandbox bracket instead) — swaps in its own strategy instead of
// forking updateChildren/applyNode's attribute/child/text logic to get a different wiring policy.
type EventWireStrategy = (el: HTMLElement, tag: ExecTag) => void;

// Reconcile `parent`'s children against the rendered exec children, reusing nodes
// (keyed by data-key when present, else positionally by tag name) and reordering them
// to match — so a reused node keeps its focus, selection, and uncommitted input state.
function updateChildren(parent: Node, execChildren: ExecTagChild[], wire: EventWireStrategy = wireEvents): void {
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
        applyNode(node, child, wire);
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

function applyNode(node: ChildNode, child: ExecValue, wire: EventWireStrategy = wireEvents): void {
    if (child.type === "tag") {
        const el = node as HTMLElement;
        if (child.key != null) el.setAttribute("data-key", String(child.key));
        refreshAttributes(el, child);
        // The component workbench's live-instance container (M12 W1a, workbench.ts): a tag carrying the
        // reserved `instancemount` marker is OPAQUE to the PAGE's own top-down reconciliation pass from
        // here on — the mount hook (end of commitRender) owns its children, not this walk. Attributes
        // still refresh (e.g. the marker's own value tracking a use row across a server-ack id remap);
        // child reconciliation and event wiring are skipped so a page render can never clobber the
        // driver's live DOM. The container's pre-mount body (the U1 static preview this same render just
        // computed into child.children) simply stays un-reconciled until the mount hook replaces it —
        // never applied, never a clobber window. This is orthogonal to (and composes fine with) the `wire`
        // parameter below: the driver's OWN call to reconcile ITS content INTO the container passes the
        // container as `parent`, never as a `child` here, so this skip never fires for it.
        if (isWorkbenchMountContainer(child)) return;
        updateChildren(el, child.children, wire);
        // A <select>'s bound value selects the matching <option>, set AFTER its options exist
        // (updateChildren above builds them) — the client half of <select> binding, symmetric to
        // the SSR `selected` marking. .value selects by the option's value attribute.
        syncSelectValue(el, child);
        wire(el, child);
    } else if (child.type === "text") {
        node.textContent = child.value;
    } else if (child.type === "int" || child.type === "bool") {
        node.textContent = String(child.value);
    }
}

// Scalar attributes become DOM attributes; checkbox/value get special handling so the
// live input reflects the model. data-key is managed by applyNode, never wiped here.
//
// Two XSS guards live here, the client twin of SsrRenderer.AppendCodeAttribute (the SSR edge; that
// scenario file is the spec, this mirrors it):
//  - An `on*` event-attribute name with a SCALAR value is dropped — a real handler is always a `fn`
//    (the `v.type === "fn"` skip above already excludes it), so a scalar there can only be an
//    injection (`<div onclick={db.evil}>`).
//  - href/src is scheme-checked AFTER mountUrl (same reasoning as the SSR edge: a mount-prefixed
//    root-relative path never carries a scheme, so only a raw absolute value can trip it).
// An `<input type="file">` tag — ImageInput (GenericUi.cs), the ONE upload primitive. Its "value"
// binding (`value={sys.field(obj, prop)}`) exists so the CLEAR path and the field-write plumbing
// work exactly like any other bound scalar, but a file input's `.value` PROPERTY cannot be assigned a
// non-empty string from script (the browser throws) — so both refreshAttributes and wireEvents must
// special-case it rather than fall into the generic input/textarea two-way-bind path below.
function isFileInputTag(tag: ExecTag): boolean {
    if (tag.name !== "input") return false;
    const t = tag.attributes["type"]?.value;
    return t != null && t.type === "text" && t.value === "file";
}

// A <details> element's `open` is native, browser-toggled UI state (clicking its <summary> flips it)
// that the app almost never binds — no deenv app in this codebase authors `<details open={...}>`. Left
// to the generic diff below, EVERY reconcile of that (reused, unkeyed-but-positionally-matched) node would
// strip it straight back off, since the desired tag never declares it — collapsing the disclosure shut on
// the very next unrelated re-render (an autosave ack, a sibling field's edit) while the operator is mid-use.
// That is a real UX bug, not just a test race: type into an Advanced textarea, an autosave lands, the
// disclosure slams shut under you. Treat `open` like `data-key` — owned by the DOM, never touched here —
// UNLESS the app explicitly declares it, in which case the normal per-attribute loop below sets/clears it
// like any other bound attribute (a future controlled `<details open={expr}>` still works).
function preservesOpenAttr(el: HTMLElement, tag: ExecTag): boolean {
    return el.tagName === "DETAILS" && !("open" in tag.attributes);
}

function refreshAttributes(el: HTMLElement, tag: ExecTag): void {
    const want = new Set<string>();
    const isFileInput = isFileInputTag(tag);
    const preserveOpen = preservesOpenAttr(el, tag);
    for (const [name, result] of Object.entries(tag.attributes)) {
        const v = result.value;
        if (v.type === "fn" || v.type === "sysFn" || v.type === "object" || v.type === "array") continue;
        if (isEventAttribute(name)) { el.removeAttribute(name); continue; }
        const raw = v.type === "null" ? null : (v as ExecInt | ExecBool | ExecText).value;

        if (tag.name === "input" && name === "checked") {
            (el as HTMLInputElement).checked = !!raw;
            if (raw) { el.setAttribute("checked", ""); want.add("checked"); } else el.removeAttribute("checked");
            continue;
        }
        if ((tag.name === "input" || tag.name === "textarea") && name === "value" && !isFileInput) {
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
        // A file input's bound "value" (the pool name, once uploaded, or "" after Clear): the
        // .value PROPERTY cannot be assigned a non-empty string (the browser throws), but an EMPTY
        // assignment IS legal — and is exactly what's needed to clear the native "chosen file"
        // display (review fix: without this, Clear wrote "" to the pool name but the input still
        // showed the old filename, since nothing ever told the DOM control itself to forget it).
        if (isFileInput && name === "value") {
            const text = raw == null ? "" : String(raw);
            const input = el as HTMLInputElement;
            if (text === "" && input.files && input.files.length > 0) input.value = "";
            el.setAttribute("value", text);
            want.add("value");
            continue;
        }
        // A <select>'s `value` is not a real attribute (it drives option-selected); it is applied to
        // the .value property in syncSelectValue, AFTER the options exist — so skip it here.
        if (tag.name === "select" && name === "value") continue;
        if (raw == null || raw === false) { el.removeAttribute(name); continue; }
        // A navigational URL attribute (href/src) whose value is root-relative is mount-prefixed — the
        // client twin of SsrRenderer's edge prefixing, so the hydrated link matches the SSR one (the app
        // wrote `/notes/2`, both edges emit `/apps/todo/notes/2`). Identity when root-mounted.
        const isUrlAttr = name === "href" || name === "src";
        const out = raw === true ? "" : isUrlAttr ? mountUrl(String(raw)) : String(raw);
        if (isUrlAttr && hasDangerousScheme(out)) { el.removeAttribute(name); continue; }
        el.setAttribute(name, out);
        want.add(name);
    }
    for (const attr of Array.from(el.attributes))
        if (attr.name !== "data-key" && !(preserveOpen && attr.name === "open") && !want.has(attr.name))
            el.removeAttribute(attr.name);
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

    // ImageInput (GenericUi.cs) — the ONE upload primitive: a file input's "value" binding cannot use
    // the generic oninput/coerceInputValue path (input.value is a fake path string, and file inputs
    // fire "input" too — that would stomp the field with garbage on every pick). Instead: on "change",
    // upload the picked file (ws.ts's uploadBlob) and write the pool's returned name back through the
    // SAME setValue closure a normal bound input uses (sys.field's persist path — staging/history/wire
    // identical to any other scalar edit), once the async upload resolves.
    if (isFileInputTag(tag)) {
        (el as HTMLInputElement).oninput = null;
        (el as HTMLInputElement).onchange = () => {
            const input = el as HTMLInputElement;
            const file = input.files && input.files[0];
            if (!file || !value?.setValue) return;
            uploadBlob(file).then(name => {
                if (name) value.setValue!({ type: "text", value: name });
                renderUi();
            });
        };
    }
    // Two-way binding for <input> and <textarea> (checked is input-only; a textarea binds
    // only its value — el.value is its text either way).
    else if ((tag.name === "input" || tag.name === "textarea") && (checked?.setValue || value?.setValue)) {
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
    // re-render. onchange (not oninput) is the select's commit event. A select MAY also carry an
    // `onChange` fn attribute (RefSelect's applyPick): after the scalar bind writes, that handler runs
    // through the SAME handler machinery as onClick (transaction / memo-bypass / action-miss), so a pick
    // that calls sys.setRef stages atomically and refetches on a VNA. Detection is purely "select has an
    // onChange fn" — no ref-type sniffing; the select still binds a plain scalar.
    const onChange = tag.attributes["onChange"]?.value;
    if (tag.name === "select" && (value?.setValue || (onChange != null && onChange.type === "fn"))) {
        const fn = onChange != null && onChange.type === "fn" ? onChange : null;
        (el as HTMLSelectElement).onchange = () => {
            if (value?.setValue) value.setValue(coerceInputValue((el as HTMLSelectElement).value, value.value));
            if (fn != null) {
                const body = () => runWithMemoBypass(() => callFunction(fn, { lastId: uiStatic.lastId, ambient: rootAmbient() }));
                const action = fn.handlerSlot != null
                    ? { fnId: fn.fn.id, slot: fn.handlerSlot, reinvoke: body }
                    : undefined;
                runHandlerTransaction(body, action);
            }
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
            // Run the handler as a commit-on-success transaction (client data layer, slice 3): its writes
            // apply optimistically but their WS sends BUFFER until it completes cleanly — so a handler is
            // atomic and nothing is sent mid-run. A genuine-bug throw rolls every write back and sends
            // nothing (runHandlerTransaction re-renders + re-throws). A missing-data (VNA) throw is the
            // ACTION-MISS (slice 4): the transaction aborts atomically, RECORDS this handler (its fn-id +
            // render-slot — captured below) as a pending action, and FETCHES; on the reply the server-
            // harvested data merges and the handler RE-RUNS over it (the `body` thunk below). On success the
            // trailing renderUi paints once as before.
            const body = () => runWithMemoBypass(() => callFunction(fn, { lastId: uiStatic.lastId, ambient: rootAmbient() }));
            // The action identity for the miss path: the handler's twin-stable fn-id + the render-slot
            // stamped on its closure (codeExec.ts executeTag). undefined when not stamped (defensive — a
            // handler built outside a render); then a VNA falls back to today's passthrough.
            const action = fn.handlerSlot != null
                ? { fnId: fn.fn.id, slot: fn.handlerSlot, reinvoke: body }
                : undefined;
            runHandlerTransaction(body, action);
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
