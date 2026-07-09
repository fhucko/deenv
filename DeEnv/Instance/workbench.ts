// The component workbench's live-instance driver (M12 W1a — docs/plans/component-workbench.md). The
// designer's per-configuration preview card (app.deenv, the U1 `.use-preview` container) is mounted with
// a REAL running instance of the previewed component: the SAME client runtime, in a sandbox, rather than
// a second engine — "preview = live" (the design's own mandate). Global script, sibling to ui.ts/
// codeExec.ts; runs entirely client-side (SSR never mounts an instance — see mountWorkbenchInstances doc).
//
// GROUNDED STRICTLY in the existing exported primitives the design's trace names: executeComponentValue
// (the real component-invocation path, M11), the evalCtxExpr/bindCtxFns/lookupCtxAst idiom (the isolated-
// scope + wsHooks-null pattern, M12 CANVAS-EVAL-1 — extended here from "one expression" to "one real
// component render"), and updateChildren (the reconciler, M1). No second interpreter, no reimplemented
// setup/view split — invoking a real stateful component this way IS composition, per the design doc.

// ── the opaque container: applyNode's skip (called from ui.ts's applyNode) ─────────────────────────────

// Does this rendered tag carry the reserved workbench mount marker? Presence-only — the reconciler never
// reads the marker's VALUE (that is the driver's job, once mounted); it only needs to know "some app code
// asked to own this element's children from here on". A general, app-code-facing convention (like
// data-key), not a designer-specific special case — any app could emit `instancemount` on a
// container to opt it out of ordinary child reconciliation.
function isWorkbenchMountContainer(tag: ExecTag): boolean {
    return tag.attributes["instancemount"] != null;
}

// ── the mount hook: end of commitRender (ui.ts) ─────────────────────────────────────────────────────────

// One mounted instance's bookkeeping — enough to decide, on the NEXT commitRender pass, whether this
// container's instance is still current (idempotent — left alone) or must be torn down and rebuilt
// (a REMOUNT: a fresh private cache + a fresh db copy, exactly like a first mount, never a partial patch).
interface WorkbenchInstance {
    argsSignature: string; // the use's current (name, value) args — an edit remounts (the page-side signature; see the design doc's grill fix)
    ctxKey: string; // the evalContext cache key this instance was built from — a Refresh mints a new one, so this doubles as the "ctx generation"
    cache: Map<string, ClientCacheEntry>; // this instance's PRIVATE memo cache — invisible to the page's uiStatic.cache, slotState shipping, and GC sweep by construction (not installed there)
}

// Keyed by the use row's object id (the SAME id the container's instancemount attribute carries).
const workbenchInstances = new Map<number, WorkbenchInstance>();

// Out-of-range positive ids for a sandbox's deep-copied seed graph (the design's user fork: FAKE-POSITIVE,
// "preview = live"). The staging gate (`obj.id > 0`) and the reactive-props rebind (objectArgKey, positive
// ids only) both key off the SIGN of an object's id — fake-POSITIVE ids keep both semantics live exactly
// as a real instance would; safety comes from wsHooks being nulled during the render (below), not from the
// id's sign. A single monotonic counter, shared by every mounted instance on the page — collisions across
// SEPARATE sandboxes could never matter functionally (each sandbox's scope/cache/db is its own reference
// graph), but one counter is simplest and is never wrong.
let fakeIdCounter = 1_000_000_000;

// Deep-copy an eval context's seed graph, re-minting every object/array id from `fakeIdCounter` — so each
// mounted instance gets its OWN mutable graph (instance A's writes never bleed into instance B's, or into
// the shipped seed the STATIC preview elsewhere on the page still reads). `seen` preserves shared/cyclic
// structure within ONE copy (a graph reachable two ways stays reachable two ways, at the SAME copy).
// Scalars (int/text/bool/null) and any non-data value pass through unchanged — a seed graph is plain data.
function deepCopySeed(value: ExecValue, seen: Map<ExecObject | ExecArray, ExecObject | ExecArray>): ExecValue {
    if (value.type === "object") {
        const existing = seen.get(value);
        if (existing != null) return existing as ExecObject;
        const copy: ExecObject = { type: "object", props: {}, id: fakeIdCounter++, sourcePath: value.sourcePath, scalarEntry: value.scalarEntry };
        seen.set(value, copy);
        for (const key of Object.keys(value.props)) copy.props[key] = deepCopySeed(value.props[key], seen);
        return copy;
    }
    if (value.type === "array") {
        const existing = seen.get(value);
        if (existing != null) return existing as ExecArray;
        const copy: ExecArray = { type: "array", kind: value.kind, items: [], id: fakeIdCounter++, elementTypeName: value.elementTypeName, sourcePath: value.sourcePath };
        seen.set(value, copy);
        copy.items = value.items.map(item => {
            const v = deepCopySeed(item.value, seen);
            // A set/dict member's reconciliation key mirrors its (now re-minted) object id, the same
            // invariant executeTagForEach reads at render time; a list/scalar item keeps its original key
            // (a plain positional index — no re-mint needed, never read as an identity elsewhere).
            return { key: v.type === "object" ? v.id : item.key, value: v };
        });
        return copy;
    }
    return value;
}

// Does ctx.exprs already carry a parsed AST for this source text? A cheap presence check (unlike
// lookupCtxAst, this never reports a miss to wsHooks/recordProp — it is read-only bookkeeping for the
// signature below, not a real lookup attempt).
function ctxHasAst(ctx: ExecObject, text: string): boolean {
    const exprs = ctx.props["exprs"];
    if (exprs == null || exprs.type !== "object") return false;
    const entry = exprs.props[text];
    return entry != null && entry.type === "object" && entry.props["ast"]?.type === "text";
}

// The MetaUse's current args as an ORDER-sorted (name, value-source, AST-availability) signature — the
// page-side remount trigger (design doc grill fix: deps recorded during a driver render land in the
// PRIVATE cache, which a page-side arg edit never invalidates, so the mount hook must independently detect
// the edit each pass). The AST-availability flag matters on its OWN, not just the source text: a
// just-typed arg (or a freshly-created configuration) MISSES against ctx.exprs on its first mount attempt
// (the auto-live parse-op's round trip hasn't landed yet) — that miss is INSIDE the wsHooks-null bracket,
// so it deliberately never queues a request (see renderWorkbenchInstance's lookupCtxAst calls); the request
// that DOES land comes from the STILL-COMPUTED static preview elsewhere on the page (U1's own
// sys.renderTree call, un-bracketed, whose OWN evalCtxExpr miss reports it normally). Once that reply
// merges the AST in, the source TEXT hasn't changed — so without this flag the signature would stay
// identical and the driver would stay stuck showing whatever it bound (null, or a real error) on the
// missed first attempt forever. Length-prefixed fields (the argKey `"t" + v.value.length + ":" + v.value`
// idiom) so no separator choice can produce a false negative across a real edit.
function useArgsSignature(use: ExecObject, ctx: ExecObject): string {
    const argsV = use.props["args"];
    if (argsV == null || argsV.type !== "array") return "";
    const rows = argsV.items.map(i => i.value).filter((v): v is ExecObject => v.type === "object");
    rows.sort((a, b) => propInt(a, "order") - propInt(b, "order"));
    return rows.map(a => {
        const name = propText(a, "name"), val = propText(a, "value");
        return "n" + name.length + ":" + name + "v" + val.length + ":" + val + "a" + (ctxHasAst(ctx, val) ? "1" : "0");
    }).join("|");
}

// Locate a MetaUse row by its object id, straight off the page's OWN live `db` graph (this instance's
// design + fn + use rows are ordinary designer data, not the workbench's own sandboxed data — the sandbox
// only wraps the PREVIEWED component's OWN evaluation, never the designer's own render). A plain structural
// walk (direct .props reads, no recordProp) — this runs AFTER commitRender, outside any computation
// boundary, so there is nothing to record a dependency into; the container disappearing from the DOM
// (handled by the caller) is what re-triggers this walk, not a memo invalidation.
function findUseRow(useId: number): { design: ExecObject; fn: ExecObject; use: ExecObject } | null {
    const dbItem = uiStatic.state.scope.items["db"];
    if (dbItem == null || dbItem.value.type !== "object") return null;
    const designsV = dbItem.value.props["designs"];
    if (designsV == null || designsV.type !== "array") return null;
    for (const d of designsV.items) {
        if (d.value.type !== "object") continue;
        const fnsV = d.value.props["fns"];
        if (fnsV == null || fnsV.type !== "array") continue;
        for (const f of fnsV.items) {
            if (f.value.type !== "object") continue;
            const usesV = f.value.props["uses"];
            if (usesV == null || usesV.type !== "array") continue;
            for (const u of usesV.items)
                if (u.value.type === "object" && u.value.id === useId) return { design: d.value, fn: f.value, use: u.value };
        }
    }
    return null;
}

// Find the design's currently-shipped evalContext cache entry, straight off `uiStatic.cache` (the PAGE's
// memo cache — NOT the workbench's own private one). execEvalContext's own cleanup invariant ("bounding the
// cache to one seed graph per design") guarantees at most one `evalContext:<designId>[:*]` entry exists at a
// time, so a prefix scan finds exactly the one the page's own static preview (app.deenv's `sys.evalContext`
// call, still rendered every pass as the container's SSR/pre-mount body) just computed or reused THIS SAME
// render — the workbench never triggers its own evalContext compute or VNA. Returns null when the design
// has no ctx yet (never rendered) or it is stale (a Refresh just invalidated the old one, the new compute
// pending) — the caller leaves the existing content alone and retries next render.
function findEvalContextEntry(designId: number): { key: string; entry: ClientCacheEntry } | null {
    const prefix = "evalContext:" + designId;
    for (const [k, v] of uiStatic.cache)
        if ((k === prefix || k.startsWith(prefix + ":")) && !v.stale) return { key: k, entry: v };
    return null;
}

// Build the mounted instance's DOM — a PARALLEL, INERT twin of updateChildren/applyNode (reusing their
// own createNode/refreshAttributes/syncSelectValue/flatten/isRenderable primitives), deliberately never
// calling wireEvents. W1a is inert DOM by design (scope boundary): a click handler captured during the
// sandboxed render closes over the SANDBOX's scope (its fake-positive db, its private cache), but by the
// time a user could click it the isolation bracket has long since restored the PAGE's real wsHooks —
// wiring it live here would let a click slip a real host action / wire mutation past the sandbox (the
// design doc's grill finding: "the isolation bracket must wrap HANDLER DISPATCH, not just the mount").
// W1b adds that dispatch-time bracket and wires handlers through it; until then, content renders, nothing
// is clickable. Always a fresh build (never a reconciling patch) — correct because a mount/remount always
// starts this container's content from nothing (see mountOneWorkbenchInstance: idempotent when unchanged,
// a full fresh build otherwise, never a partial patch).
function mountInertChildren(parent: Node, execChildren: ExecTagChild[]): void {
    parent.textContent = "";
    for (const child of flatten(execChildren).filter(isRenderable)) {
        const node = createNode(child);
        applyInertNode(node, child);
        parent.appendChild(node);
    }
}
function applyInertNode(node: ChildNode, child: ExecValue): void {
    if (child.type === "tag") {
        const el = node as HTMLElement;
        refreshAttributes(el, child); // a `fn`-typed attribute (onClick, …) is already skipped here — never becomes a DOM attribute
        mountInertChildren(el, child.children);
        syncSelectValue(el, child);
    } else if (child.type === "text") {
        node.textContent = child.value;
    } else if (child.type === "int" || child.type === "bool") {
        node.textContent = String(child.value);
    }
}

// A `.instance-error` node — the v1 fidelity boundary made HONEST, not silent (design doc): a component
// whose render throws (a store-backed builtin's "Value not available" against this fresh, unseeded private
// cache; an ambient read; FG's runaway guard) shows its REAL error text in the card, the same tier
// eval-degrade-banner styling already uses elsewhere in the designer.
function instanceErrorTag(message: string): ExecTag {
    return {
        type: "tag", name: "div",
        attributes: { "class": { value: { type: "text", value: "instance-error" } } },
        children: [{ type: "text", value: message }],
    };
}

// Run the REAL component invocation over a sandbox scope (already installed as the module's live globals
// by the caller's bracket) and return either its rendered children or the real error text. A `nothing`
// result (executeComponentValue's own VNA-swallow, since a real — non-null — private memoCache is
// installed) always means a store-backed builtin missed against this fresh cache: `nothing` is never a
// user-producible value, so it is an unambiguous signal, and "Value not available" is the EXACT message
// every store-backed builtin throws for this condition — reproducing it here is not fabricating an error,
// it is naming the one the interpreter always produces. Any OTHER thrown error (a different message —
// "Variable not found", FG's depth guard, a genuine bug) propagates normally, uncaught by memoize, straight
// to this try/catch — its message rides through verbatim.
function renderWorkbenchInstance(fn: ExecObject, use: ExecObject, ctx: ExecObject): { tags: ExecTagChild[]; errorMessage: string | null } {
    const fnName = propText(fn, "name");
    const evalScope: ExecScope = { items: {}, parent: null };
    const seedDb = ctx.props["db"];
    if (seedDb != null) evalScope.items["db"] = { value: deepCopySeed(seedDb, new Map()), isReadOnly: true };
    bindCtxFns(ctx, evalScope);
    const component = evalScope.items[fnName]?.value;
    if (component == null || component.type !== "fn") return { tags: [], errorMessage: "Component not found." };

    // The use's args become the synthesized invocation's attributes — real ASTs (from ctx.exprs, the SAME
    // content-addressed source the static preview reads), so executeComponentValue's own attribute-eval
    // step (real BindParams, no separate call needed here) binds them exactly as a live call site would. A
    // source with no shipped AST yet (auto-live parse-op territory) is left OUT of the attributes array —
    // matching runtime truth: an absent attr binds the param to null, never a guessed value.
    const argsV = use.props["args"];
    const argRows = argsV != null && argsV.type === "array"
        ? argsV.items.map(i => i.value).filter((v): v is ExecObject => v.type === "object").sort((a, b) => propInt(a, "order") - propInt(b, "order"))
        : [];
    const attributes: CodeTagAttribute[] = [];
    for (const a of argRows) {
        const name = propText(a, "name");
        if (name.length === 0) continue;
        const ast = lookupCtxAst(ctx, propText(a, "value"));
        if (ast != null) attributes.push({ name, value: ast });
    }
    const tag: CodeTag = { type: "tag", name: fnName, attributes, children: [] };
    const sandboxContext: ExecContext = { lastId: { value: 0 }, ambient: null };

    try {
        const view = executeComponentValue(tag, component, evalScope, sandboxContext);
        if (view.type === "nothing") return { tags: [], errorMessage: "Value not available" };
        return { tags: view.type === "array" ? view.items.map(i => i.value) : [view], errorMessage: null };
    } catch (e) {
        return { tags: [], errorMessage: e instanceof Error ? e.message : String(e) };
    }
}

// Mount (or, on a real change, remount) ONE workbench container. Idempotent: an already-current instance
// (same args signature, same ctx generation) is left completely alone — its live DOM subtree is the
// driver's own, untouched by this pass, matching the opaque-container contract applyNode's skip enforces.
function mountOneWorkbenchInstance(useId: number, container: HTMLElement): void {
    const located = findUseRow(useId);
    if (located == null) return; // the row isn't in the live graph yet (mid-edit) or was just removed — nothing to mount
    const { design, fn, use } = located;
    const found = findEvalContextEntry(design.id);
    if (found == null || found.entry.result.type !== "object") return; // ctx not ready this pass — retry next render
    const ctx = found.entry.result;
    if (ctxError(ctx) != null) return; // the design itself is invalid (BuildEvalContext's degrade arm) — nothing sane to preview

    const argsSignature = useArgsSignature(use, ctx);
    const existing = workbenchInstances.get(useId);
    if (existing != null && existing.argsSignature === argsSignature && existing.ctxKey === found.key) return; // unchanged — leave mounted

    // The isolation bracket (evalCtxExpr's idiom, extended from "one expression" to "one real component
    // render"): save every module global a render touches, install the sandbox's own, restore
    // UNCONDITIONALLY (finally) so a driver render can never leak into — or inherit stale posture from —
    // the enclosing page render. wsHooks nulled: no host action/save/login can escape the sandbox (W1a has
    // no handlers yet, but the synthesized tag's ATTRIBUTE evaluation and the component's own render body
    // already run real Code, so the same safety net applies from this slice on). A private, fresh Map (not
    // reused across a remount — a remount IS a fresh mount, per the design's Reset-shaped "REMOUNTS that
    // instance") gives the real component-state persistence executeComponentValue's own memoize provides,
    // scoped entirely to this instance.
    const savedCache = memoCache;
    const savedSlotPath = slotPath.slice();
    const savedNeedsServerData = needsServerData;
    const savedCallDepth = callDepth;
    const savedWsHooks = wsHooks;
    const cache = new Map<string, ClientCacheEntry>();
    memoCache = cache;
    slotPath.length = 0;
    slotPath.push("workbench:" + useId); // the private cache's own slot prefix — never read outside this Map
    needsServerData = false;
    callDepth = 0;
    wsHooks = null;
    depStack.push({ props: [], members: [], vars: [] }); // a throwaway frame: recordProp/recordMember calls during the render land here, discarded below — never the enclosing page's frame
    let result: { tags: ExecTagChild[]; errorMessage: string | null };
    try {
        result = renderWorkbenchInstance(fn, use, ctx);
    } finally {
        depStack.pop();
        memoCache = savedCache;
        slotPath.length = 0;
        slotPath.push(...savedSlotPath);
        needsServerData = savedNeedsServerData; // a store-backed miss inside the sandbox must NEVER arm the PAGE's own refetch (the design doc's PERMANENT-chatter warning)
        callDepth = savedCallDepth;
        wsHooks = savedWsHooks;
    }

    workbenchInstances.set(useId, { argsSignature, ctxKey: found.key, cache });
    const children: ExecTagChild[] = result.errorMessage != null ? [instanceErrorTag(result.errorMessage)] : result.tags;
    mountInertChildren(container, children);
}

// The mount hook, called once at the END of commitRender (ui.ts — the syncBreadcrumbs precedent: the
// single DOM-commit chokepoint for both render paths, module state balanced-empty there). Synchronous,
// idempotent, re-entered on every page render: scans the DOM for every live workbench container, mounts or
// remounts each as needed, and disposes any instance whose container left the DOM (a removed use row, or a
// navigation away from the design editor) — dropping its private cache so the page GC (which never sees a
// private Map) doesn't have to. SSR never runs this (server-only rendering has no DOM); the container's
// pre-mount body is the U1 static `sys.renderTree` preview app.deenv already computes, left untouched by
// applyNode's reconciler skip until this pass replaces it.
function mountWorkbenchInstances(): void {
    const present = new Set<number>();
    document.querySelectorAll<HTMLElement>("[instancemount]").forEach(el => {
        const key = Number(el.getAttribute("instancemount"));
        if (!Number.isFinite(key)) return;
        present.add(key);
        mountOneWorkbenchInstance(key, el);
    });
    for (const key of Array.from(workbenchInstances.keys()))
        if (!present.has(key)) workbenchInstances.delete(key);
}
