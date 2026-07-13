// The component workbench's live-instance driver (M12 W1a/W1b — docs/plans/component-workbench.md). The
// designer's per-configuration preview card (app.deenv, the U1 `.use-preview` container) is mounted with
// a REAL running instance of the previewed component: the SAME client runtime, in a sandbox, rather than
// a second engine — "preview = live" (the design's own mandate). Global script, sibling to ui.ts/
// codeExec.ts; runs entirely client-side (SSR never mounts an instance — see mountWorkbenchInstances doc).
//
// W1b (events + Reset): W1a mounted content INERT (noWiring) because the isolation bracket only wrapped
// RENDER — a click fires from the DOM long after that bracket restored the page's real globals. W1b adds
// the DISPATCH-time bracket (runInstanceHandler) every instance event routes through, a matching wiring
// strategy (instanceWiring, mirroring wireEvents' own click/two-way-binding coverage), and Reset (a
// framework-owned control bar the driver renders inside the container — see ensureInstanceContent).
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
    // W1b: the sandbox's OWN id-minting counter, persisted across this instance's WHOLE lifetime (setup +
    // every later render/dispatch), not rebuilt per pass. A handler-triggered re-render reuses the SAME
    // `cache` (so component state / reactive-props memo entries survive) — reusing a FRESH {value:0}
    // counter alongside that reused cache would let a later render re-mint an id (e.g. -1) already alive
    // in state a prior render built, corrupting anything keying off object identity (foreach reconciliation
    // keys, objectArgKey). A remount/Reset mints a fresh counter (paired with a fresh cache), matching
    // "fresh mount" semantics exactly.
    lastId: { value: number };
    // W1c: this instance's OWN deep-copied seed graph — minted ONCE at mount/Reset (deepCopySeed), never
    // re-copied on a later render pass. A component's setup runs ONCE (memoized under its slot in `cache`
    // above) and captures whatever `db` reference the FIRST render bound — so every LATER render/handler
    // dispatch must keep passing that SAME reference (never a fresh copy) for a handler's db write
    // (`db.notes.add(...)`) to be visible to the setup-scope's own later reads AND to seedSandboxCache's
    // extent walk below. null only when the eval context shipped no seed db at all (an unusual/degraded
    // context — the render then simply has no `db` var, matching the pre-W1c behavior).
    db: ExecValue | null;
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
        const copy: ExecObject = { type: "object", props: {}, id: fakeIdCounter++, sourcePath: value.sourcePath, scalarEntry: value.scalarEntry, ownerRef: value.ownerRef, dictProp: value.dictProp, key: value.key };
        seen.set(value, copy);
        for (const key of Object.keys(value.props)) copy.props[key] = deepCopySeed(value.props[key], seen);
        return copy;
    }
    if (value.type === "array") {
        const existing = seen.get(value);
        if (existing != null) return existing as ExecArray;
        const copy: ExecArray = { type: "array", kind: value.kind, items: [], id: fakeIdCounter++, elementTypeName: value.elementTypeName, sourcePath: value.sourcePath, ownerRef: value.ownerRef, dictProp: value.dictProp };
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

// The ORIGINAL per-call helper (W1a): deep-copy straight from the eval context's shipped seed. The real
// driver no longer calls this per render (see WorkbenchInstance.db) — kept for renderWorkbenchInstance's
// no-instanceDb fallback (the direct-call unit tests below).
function deepCopyDbFromCtx(ctx: ExecObject): ExecValue | null {
    const seedDb = ctx.props["db"];
    return seedDb != null ? deepCopySeed(seedDb, new Map()) : null;
}

// ── W1c cache seeding (docs/plans/component-workbench.md "The v1 fidelity boundary" — the fast-follow)
// ────────────────────────────────────────────────────────────────────────────────────────────────────────

const CAPABILITY_VERBS = ["create", "edit", "delete"];
const NO_DEPS: CacheDeps = { props: [], members: [], vars: [] };

// Populate a FRESH-or-reused instance's private cache with the entries every store-backed builtin
// (sys.schema/sys.new/sys.extent/sys.canWrite/sys.canRead) revives from (codeExec.ts execSchema/execNew/
// execExtent/execCanWrite/execCanRead — all `memoize("<kind>:<key>", …)` lookups that THROW "Value not
// available" on a miss, exactly what a fresh private cache always was before this). Called on EVERY render
// pass (mount/remount/Reset/handler-triggered repaint) from runInstanceRenderPass — schema/canWrite/canRead
// are cheap reference copies off `ctx.types` (a handful of declared types), so re-seeding them every pass
// costs nothing and needs no separate "already seeded" bookkeeping; extent (seedExtentCache below) MUST be
// re-derived every pass to stay mutation-consistent, so folding both into one call keeps exactly one
// seeding path, not two with diverging cadences.
//
// canWrite/canRead ship UNCONDITIONALLY true: the sandbox has no session/access floor to evaluate — the
// preview runs as "the operator previewing their own design" (the same trust level the whole designer
// already assumes for this design's data). This is safe regardless of what it reports: wsHooks stays null
// through every dispatch (withSandboxGlobals), so no real write can EVER escape the sandbox no matter what
// a component's write-affordance check believes — canWrite/canRead only gate what the UI OFFERS to click,
// never what a real floor would ALLOW, and there is no real floor here to under- or over-approximate.
function seedSandboxCache(instance: WorkbenchInstance, ctx: ExecObject): void {
    const typesV = ctx.props["types"];
    if (typesV != null && typesV.type === "object") {
        for (const key of Object.keys(typesV.props)) {
            instance.cache.set("schema:" + key, { result: typesV.props[key], deps: NO_DEPS, stale: false });
            if (key.includes("/")) continue; // "Type/prop" dict-prop descriptor keys carry no capability entries
            for (const verb of CAPABILITY_VERBS)
                instance.cache.set("canWrite:" + key + ":" + verb, { result: { type: "bool", value: true }, deps: NO_DEPS, stale: false });
            instance.cache.set("canRead:" + key, { result: { type: "bool", value: true }, deps: NO_DEPS, stale: false });
        }
    }
    seedExtentCache(instance, typesV);
}

// One type's extent-in-progress: its member objects (deduped by id — the SAME object reached two ways
// counts once) AND the ids of every set/dict array that contributed a member — the latter is what makes
// the seeded entry (below) genuinely REACTIVE, not just fresh-at-seed-time.
interface ExtentBucket { members: Map<number, ExecObject>; arrayIds: number[]; }

// extent: derived FRESH every render pass from the instance's OWN (mutating) db copy — "the seed graph's
// collections ARE the extents" (the design doc's per-instance choice, preferred over shipping a server-
// computed extent for this exact reason): a handler's `db.<set>.add(...)` writes straight into
// `instance.db` (the SAME object every render/dispatch reuses — see WorkbenchInstance.db), so walking it
// here on the NEXT pass sees the addition. A live page instead marks `extent:*` stale on a mutation and
// waits for a wire REFETCH to recompute it server-side (ws.ts invalidateExtents) — the sandbox has no wire/
// store round trip to lean on, so re-deriving directly here is the correct substitute.
//
// CORRECTNESS NOTE (not just freshness): re-seeding the entry alone is not enough — a COMPONENT that reads
// `sys.extent("Note")` gets its OWN render memoized (executeComponentValue's `memoize(slotKey, …)`), and a
// memo HIT never re-invokes the component body at all, so a fresh `extent:Note` VALUE sitting unread in the
// cache would never reach the screen. The fix is the SAME dependency mechanism the rest of the interpreter
// already uses everywhere else: `deps.members` on the seeded entry lists every set/dict array that fed it
// (db.notes' own array id, etc.) — memoize's HIT path merges a cache entry's OWN `deps` into the ENCLOSING
// computation (mergeDeps, codeExec.ts), so the component's render inherits that same member dependency the
// FIRST time it reads the extent; `db.notes.add(...)` (addToCollection) then calls `invalidateMember(arr.id)`,
// which marks BOTH the extent entry AND the component's own memoized render stale — exactly the ordinary
// live-page path, just without a wire round trip. Without this, extent reads would be permanently stuck at
// their FIRST-render value no matter how many times a handler mutated the underlying set.
//
// Every declared type gets an entry even with zero members found (an empty extent is a real, meaningful
// empty list — never left absent, which would VNA-throw instead of showing "no rows").
function seedExtentCache(instance: WorkbenchInstance, typesV: ExecValue | undefined): void {
    if (typesV == null || typesV.type !== "object" || instance.db == null) return;
    const byType = new Map<string, ExtentBucket>();
    collectExtentMembers(instance.db, byType, new Set());
    for (const key of Object.keys(typesV.props)) {
        if (key.includes("/")) continue; // a "Type/prop" descriptor key, not a type name
        const bucket = byType.get(key);
        const items: ExecArrayItem[] = bucket != null ? Array.from(bucket.members.values()).map(o => ({ key: o.id, value: o })) : [];
        const deps: CacheDeps = { props: [], members: bucket?.arrayIds ?? [], vars: [] };
        // A fresh transient id every pass (mirrors DbBridge.LoadExtent's own `--context.LastId.Value` mint)
        // — instance.lastId is the SAME id-minting counter renderWorkbenchInstance's sandboxContext uses,
        // so an extent array's id can never collide with anything else this instance ever mints.
        instance.cache.set("extent:" + key, { result: { type: "array", kind: "list", items, id: --instance.lastId.value }, deps, stale: false });
    }
}

// Recursive walk of an instance's db graph, bucketing every set/dict member (AND every contributing
// array's own id — see ExtentBucket) by the containing array's `elementTypeName`. `seen` guards a cyclic/
// shared graph, mirroring deepCopySeed's own seen-map. Object members only (a scalar dict's entries are
// wrapper objects keyed by a SCALAR elementTypeName like "text"/"int", which never matches a declared
// object type name, so they naturally never populate any bucket a caller asks about).
function collectExtentMembers(value: ExecValue, byType: Map<string, ExtentBucket>, seen: Set<ExecObject | ExecArray>): void {
    if (value.type === "object") {
        if (seen.has(value)) return;
        seen.add(value);
        for (const key of Object.keys(value.props)) collectExtentMembers(value.props[key], byType, seen);
        return;
    }
    if (value.type === "array") {
        if (seen.has(value)) return;
        seen.add(value);
        if ((value.kind === "set" || value.kind === "dict") && value.elementTypeName) {
            let bucket = byType.get(value.elementTypeName);
            if (bucket == null) byType.set(value.elementTypeName, bucket = { members: new Map(), arrayIds: [] });
            bucket.arrayIds.push(value.id);
            for (const item of value.items) if (item.value.type === "object") bucket.members.set(item.value.id, item.value);
        }
        for (const item of value.items) collectExtentMembers(item.value, byType, seen);
    }
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

// The INERT wiring strategy: nothing is clickable. W1a used this for ALL mounted content (a click
// handler captured during the sandboxed render closes over the SANDBOX's scope, but by the time a user
// could click it the render bracket has long since restored the PAGE's real wsHooks — wiring it live
// there would let a click slip a real host action / wire mutation past the sandbox). W1b adds
// instanceWiring (below) — the real, DISPATCH-bracketed strategy — for a successfully-rendered instance's
// OWN content; this stays reserved for the one case that must NEVER be interactive: an `.instance-error`
// card (a broken render has no live scope worth wiring into, mount OR handler-triggered alike).
function noWiring(): void { /* content renders, nothing is clickable */ }

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
// by the caller's bracket) and return either its rendered children or the real error text.
//
// M12 W1a review (arch fix 1) — a `nothing` result is AMBIGUOUS, not automatically a swallowed VNA: it is
// ALSO runBody's own honest "no value" fallback (`executeBlock(...) ?? {type:"nothing"}`, codeExec.ts) for
// a function body that falls off the end without hitting a `return` — reachable under perfectly valid Code
// (a bare `if` with no `else`, condition false; more generally any body whose taken path never returns).
// ANSWERING THE REVIEWER'S OPEN QUESTION: yes, a top-level view CAN legitimately be `nothing` — confirmed
// both by reading runBody/executeBlock and by this fix's own pin (a conditionally-empty component mounts
// with NO error). The live page treats this exactly like an empty array: isRenderable/flatten drop it,
// rendering nothing — so labeling EVERY `nothing` "Value not available" was a real preview≠live divergence
// (a spurious error card where the page shows blank). The two cases are disambiguated with the ONE signal
// that is actually load-bearing: `needsServerData`. Its ONLY setter is memoize's VNA-swallow catch
// (codeExec.ts:452) — a legitimate no-value return never touches it. The caller (runInstanceRenderPass)
// resets it to false immediately before calling this function, so reading it HERE — still inside the
// caller's try, before its finally restores the saved value — samples exactly THIS render's own
// contribution: true only if some store-backed builtin actually missed somewhere in this render (top-level
// or nested), never for an honest empty view. Any OTHER thrown error (a different message — "Variable not
// found", FG's depth guard, a genuine bug) propagates normally, uncaught by memoize, straight to this
// try/catch — its message rides through verbatim, unaffected by this disambiguation.
// `lastId` (W1b): the sandbox's id-minting counter for THIS render — defaults to a fresh {value:0} (the
// original W1a behavior, and what the unit tests below still exercise directly), but the driver always
// passes the INSTANCE's own persisted counter (WorkbenchInstance.lastId) so a handler-triggered re-render
// mints ids that stay unique against everything the instance's setup/prior renders already built (see the
// WorkbenchInstance.lastId doc comment).
// `instanceDb` (W1c): the instance's OWN, ALREADY-deep-copied seed graph (WorkbenchInstance.db) — passed
// explicitly by the driver so the SAME db reference is reused across every render/handler-dispatch of this
// instance (a component's setup captures whatever `db` its FIRST render bound; a fresh copy on a LATER
// render would silently orphan any handler write already made against the first copy). `undefined` (the
// unit tests below, which call this directly with no 5th argument) falls back to the ORIGINAL per-call
// deep-copy-from-ctx behavior — fine for a single-render test, never used by the real driver.
function renderWorkbenchInstance(fn: ExecObject, use: ExecObject, ctx: ExecObject, lastId: { value: number } = { value: 0 }, instanceDb?: ExecValue | null): { tags: ExecTagChild[]; errorMessage: string | null } {
    const fnName = propText(fn, "name");
    const evalScope: ExecScope = { items: {}, parent: null };
    const db = instanceDb !== undefined ? instanceDb : deepCopyDbFromCtx(ctx);
    if (db != null) evalScope.items["db"] = { value: db, isReadOnly: true };
    // Library components (SetTable/ObjectForm/Field/RefSelect/…) bound FIRST so a same-named design fn
    // (bindCtxFns, next) shadows one — mirrors a real app's own scope nesting inside the library scope.
    bindFnMap(ctx.props["lib"], evalScope);
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
    const sandboxContext: ExecContext = { lastId, ambient: null };

    try {
        const view = executeComponentValue(tag, component, evalScope, sandboxContext);
        if (view.type === "nothing") {
            // Disambiguate: a real VNA swallow armed needsServerData (reset false by the caller just
            // before this call) — an honest empty view never touches it. See the doc comment above.
            return needsServerData ? { tags: [], errorMessage: "Value not available" } : { tags: [], errorMessage: null };
        }
        return { tags: view.type === "array" ? view.items.map(i => i.value) : [view], errorMessage: null };
    } catch (e) {
        return { tags: [], errorMessage: e instanceof Error ? e.message : String(e) };
    }
}

// Locate a use row's fn/use/ctx and its ctx generation key — shared by mount, Reset, and a
// handler-triggered re-render (all three need to know "what would this instance render against RIGHT
// NOW"). A plain lookup, no side effects; the caller decides what to do with the result.
function locateInstanceInputs(useId: number): { fn: ExecObject; use: ExecObject; ctx: ExecObject; ctxKey: string } | null {
    const located = findUseRow(useId);
    if (located == null) return null; // the row isn't in the live graph yet (mid-edit) or was just removed
    const found = findEvalContextEntry(located.design.id);
    if (found == null || found.entry.result.type !== "object") return null; // ctx not ready this pass — retry next render
    const ctx = found.entry.result;
    if (ctxError(ctx) != null) return null; // the design itself is invalid (BuildEvalContext's degrade arm) — nothing sane to preview
    return { fn: located.fn, use: located.use, ctx, ctxKey: found.key };
}

// The Reset control + the reconciled-content wrapper, built ONCE per container (idempotent — the driver
// owns this subtree from here on, per applyNode's opaque-container skip, so the SSR/U1-static pre-mount
// body is cleared exactly once, on the instance's very first mount pass, and never rebuilt on a later
// one). Returns the inner content element every render (mount, remount, Reset, or a handler-triggered
// repaint) reconciles the previewed component's OWN rendered tags into — kept structurally SEPARATE from
// the toolbar so updateChildren's positional tag-name matching can never confuse the Reset button with
// the component's own root element (Counter's root literally IS a `<button>`).
function ensureInstanceContent(container: HTMLElement, useId: number): HTMLElement {
    const existing = container.querySelector(":scope > .workbench-instance-content") as HTMLElement | null;
    if (existing != null) return existing;
    container.textContent = ""; // replace the static pre-mount body (the U1 sys.renderTree walk / SSR) — this runs synchronously inside the SAME commitRender that painted it (see mountWorkbenchInstances), so it is never actually seen on screen
    const toolbar = document.createElement("div");
    toolbar.className = "workbench-instance-toolbar";
    const resetBtn = document.createElement("button");
    resetBtn.type = "button";
    resetBtn.className = "workbench-instance-reset";
    resetBtn.textContent = "Reset";
    // Framework chrome, not app.deenv markup (app docs have no host-DOM/comment syntax to author this in
    // anyway) — a plain DOM handler, entirely outside the reconciler/event-wiring machinery below.
    resetBtn.onclick = () => resetWorkbenchInstance(useId, container);
    toolbar.appendChild(resetBtn);
    container.appendChild(toolbar);
    const content = document.createElement("div");
    content.className = "workbench-instance-content";
    // Swallow in-app anchor navigation (arch review fold): a previewed component rendering a bare
    // `<a href>` (no onClick — instanceWiring only stops propagation for a WIRED handler, ui.ts:702) would
    // otherwise bubble all the way to the PAGE's own document-level delegated listener
    // (interceptNavigation, ui.ts:227, wired once in init.ts) and navigate the operator's whole designer
    // page away from a click inside a preview card. preventDefault ALSO satisfies
    // interceptNavigation's own bail-out check (its first line reads `e.defaultPrevented`), so either guard
    // alone would suffice — both together make the containment airtight without relying on that coupling.
    // Attached ONCE, on the content root itself (this function's own idempotent-build guard above), so it
    // catches every anchor the component ever renders here, including ones added by a later remount.
    content.addEventListener("click", (e: MouseEvent) => {
        const anchor = (e.target as Element | null)?.closest?.("a");
        if (anchor instanceof HTMLAnchorElement) { e.preventDefault(); e.stopPropagation(); }
    });
    container.appendChild(content);
    return content;
}

// THE shared isolation bracket (evalCtxExpr's idiom, extended from "one expression" to "one real
// component render/dispatch"): save every module global a sandboxed run touches, install the instance's
// own, restore UNCONDITIONALLY (finally) so a driver render OR a dispatched handler can never leak into —
// or inherit stale posture from — the enclosing page render. wsHooks nulled: no host action/save/login can
// escape the sandbox (the session-safety pin — sendLogin/sendLogout are not id-gated, this bracket is the
// only thing stopping them). memoCache is the INSTANCE's own persistent Map, never a fresh one here — a
// fresh Map belongs only to a real mount/remount/Reset, minted by the caller BEFORE this runs.
//
// `memoBypass` is the ONE load-bearing difference between the two callers (runInstanceRenderPass: false —
// a render must be cacheable, exactly like the page's own render; runInstanceHandler: true — a handler is
// side-effecting, exactly like the page's own runWithMemoBypass) — an explicit PARAMETER rather than two
// hand-copied brackets, so restore-set parity is structural (one save/restore list, used by both callers),
// not something a future edit to one bracket could silently drift from the other.
function withSandboxGlobals<T>(instance: WorkbenchInstance, useId: number, opts: { memoBypass: boolean }, body: () => T): T {
    const savedCache = memoCache;
    const savedSlotPath = slotPath.slice();
    const savedNeedsServerData = needsServerData;
    const savedCallDepth = callDepth;
    const savedWsHooks = wsHooks;
    const savedMemoBypass = memoBypass;
    memoCache = instance.cache;
    slotPath.length = 0;
    slotPath.push("workbench:" + useId); // the private cache's own slot prefix — never read outside this Map
    needsServerData = false;
    callDepth = 0;
    wsHooks = null;
    memoBypass = opts.memoBypass;
    depStack.push({ props: [], members: [], vars: [] }); // a throwaway frame: recordProp/recordMember calls during the run land here, discarded below — never the enclosing page's frame
    try {
        return body();
    } finally {
        depStack.pop();
        memoCache = savedCache;
        slotPath.length = 0;
        slotPath.push(...savedSlotPath);
        needsServerData = savedNeedsServerData; // a store-backed miss inside the sandbox must NEVER arm the PAGE's own refetch (the design doc's PERMANENT-chatter warning)
        callDepth = savedCallDepth;
        wsHooks = savedWsHooks;
        memoBypass = savedMemoBypass;
    }
}

// Run ONE render pass for a mounted instance and reconcile the result into its container's content
// wrapper. Shared by every path that (re)paints an instance — mountOneWorkbenchInstance and
// resetWorkbenchInstance (a FRESH `instance` — new cache, new lastId) and rerenderWorkbenchInstance (the
// SAME `instance` reused, after a handler dispatch) — so the isolation bracket is defined in exactly ONE
// place (withSandboxGlobals above; W1b's dispatch bracket in runInstanceHandler is the SAME helper, not a
// parallel one).
function runInstanceRenderPass(useId: number, fn: ExecObject, use: ExecObject, ctx: ExecObject, instance: WorkbenchInstance, container: HTMLElement): void {
    // W1c: re-seed schema:/extent:/canWrite:/canRead: before every render (mount, remount, Reset, OR a
    // handler-triggered repaint) — see seedSandboxCache's own doc comment for why every pass, not just mount.
    seedSandboxCache(instance, ctx);
    const result = withSandboxGlobals(instance, useId, { memoBypass: false },
        () => renderWorkbenchInstance(fn, use, ctx, instance.lastId, instance.db));

    const content = ensureInstanceContent(container, useId);
    const children: ExecTagChild[] = result.errorMessage != null ? [instanceErrorTag(result.errorMessage)] : result.tags;
    // Compose the REAL reconciler over the mounted content — the same updateChildren/applyNode the page
    // uses. An error card is never wired live (noWiring — nothing sane to click into); a real view is
    // wired through instanceWiring (W1b), the dispatch-bracketed strategy below. This RECONCILES against
    // whatever is currently in `content` (empty on a first mount/remount, or this instance's own prior
    // live content on a handler-triggered repaint) rather than blindly clearing it — the reused-node
    // machinery (data-key, refreshAttributes) is what makes an <input>'s uncommitted keystroke/focus
    // survive its own repaint.
    updateChildren(content, children, result.errorMessage != null ? noWiring : instanceWiring(useId, container));
}

// Mount (or, on a real change, remount) ONE workbench container. Idempotent: an already-current instance
// (same args signature, same ctx generation) is left completely alone — its live DOM subtree is the
// driver's own, untouched by this pass, matching the opaque-container contract applyNode's skip enforces.
function mountOneWorkbenchInstance(useId: number, container: HTMLElement): void {
    const inputs = locateInstanceInputs(useId);
    if (inputs == null) return;
    const { fn, use, ctx, ctxKey } = inputs;

    const argsSignature = useArgsSignature(use, ctx);
    const existing = workbenchInstances.get(useId);
    if (existing != null && existing.argsSignature === argsSignature && existing.ctxKey === ctxKey) return; // unchanged — leave mounted

    // A fresh sandbox: a fresh private cache (component-state persistence, scoped entirely to this
    // instance — never reused across a remount, since a remount IS a fresh mount, per the design's
    // Reset-shaped "REMOUNTS that instance"), a fresh id-minting counter (WorkbenchInstance.lastId), and a
    // fresh deep-copied db graph (WorkbenchInstance.db — see newWorkbenchInstance).
    const instance = newWorkbenchInstance(argsSignature, ctxKey, ctx);
    workbenchInstances.set(useId, instance);
    runInstanceRenderPass(useId, fn, use, ctx, instance, container);
}

// A brand-new sandbox: fresh cache, fresh id counter, and ONE deep copy of the eval context's seed db —
// minted here (not per render — see WorkbenchInstance.db) so a handler's write and seedExtentCache's later
// walk both see, and mutate, the SAME graph as the mounted component's own setup.
function newWorkbenchInstance(argsSignature: string, ctxKey: string, ctx: ExecObject): WorkbenchInstance {
    return { argsSignature, ctxKey, cache: new Map(), lastId: { value: 0 }, db: deepCopyDbFromCtx(ctx) };
}

// Reset (component-workbench.md's user-picked semantics): DISPOSE the whole sandbox — the private
// memoCache entry (component state) AND, since the setup closure it lived in is what held the ONLY
// reference to the deep-copied db graph, the db copy too — and remount fresh from the shipped context.
// "As first rendered", predictably: identical to a first mount, because it IS one (a brand-new `instance`,
// exactly like mountOneWorkbenchInstance mints for a real remount).
function resetWorkbenchInstance(useId: number, container: HTMLElement): void {
    workbenchInstances.delete(useId);
    const inputs = locateInstanceInputs(useId);
    if (inputs == null) return; // the row is gone — nothing to remount; the next mount pass will clean up the container
    const { fn, use, ctx, ctxKey } = inputs;
    const instance = newWorkbenchInstance(useArgsSignature(use, ctx), ctxKey, ctx);
    workbenchInstances.set(useId, instance);
    runInstanceRenderPass(useId, fn, use, ctx, instance, container);
}

// Re-render an ALREADY-MOUNTED instance in place, reusing its EXISTING private cache (and lastId) — the
// driver's OWN re-render, called after a dispatched handler completes successfully (runInstanceHandler),
// never the page's global renderUi. Reusing the same cache/lastId (as opposed to mountOneWorkbenchInstance's
// fresh ones) is what makes a handler's state/db writes STICK across the repaint: the component's setup
// already ran and is cache-resident (memoize hits it without re-invoking), so only its VIEW recomputes,
// over the SAME captured scope the handler just mutated.
function rerenderWorkbenchInstance(useId: number, container: HTMLElement): void {
    const instance = workbenchInstances.get(useId);
    if (instance == null) return; // torn down mid-dispatch (Reset raced the click, or the use row was removed) — nothing to repaint
    const inputs = locateInstanceInputs(useId);
    if (inputs == null) return;
    runInstanceRenderPass(useId, inputs.fn, inputs.use, inputs.ctx, instance, container);
}

// The context a dispatched handler (or a select's onChange fn) runs under: the INSTANCE's own persisted
// id-minting counter (see WorkbenchInstance.lastId), no ambient (the v1 fidelity boundary — path/
// currentUser/ambient reads are real "Variable not found" errors until per-use ambients land, same as a
// render's own sandboxContext).
function instanceHandlerContext(useId: number): ExecContext {
    const instance = workbenchInstances.get(useId);
    return { lastId: instance?.lastId ?? { value: 0 }, ambient: null };
}

// THE dispatch bracket (M12 W1b — component-workbench.md's "grill's core fix"). Every DOM event a mounted
// instance's content fires runs through here, never straight off the DOM: the MOUNT/render bracket
// (runInstanceRenderPass) only wraps RENDER — it has long since restored the page's real globals by the
// time a user could click anything — so dispatch needs its OWN install. withSandboxGlobals reuses the
// INSTANCE's persistent cache + lastId (not fresh ones: a handler's writes must land in the SAME state
// graph the mounted view already reads, and a fresh lastId would risk re-minting an id already alive in
// that graph — see the WorkbenchInstance.lastId doc comment), with memoBypass forced TRUE (unlike the
// render bracket's false) — a handler is side-effecting, exactly like the page's own runWithMemoBypass.
//
// wsHooks nulled is what makes this safe for sys.login/sys.logout specifically: NEITHER is id-gated
// (codeExec.ts execLogin/execLogout call sendLogin/sendLogout unconditionally) — a login-form component
// mounted in a card would otherwise REALLY re-bind the page's session on a click. The fake-positive
// sandbox db ids (the design's user fork) give staging/reactive-props FIDELITY; they provide NO isolation
// on their own. This bracket is the isolation, and it is why it is non-optional on every dispatch, not
// just the ones a component happens to look side-effecting.
//
// The sandbox's own thin transaction wrapper (component-workbench.md: instance handlers do NOT reuse
// runHandlerTransaction, which is page-entangled five ways — the wire journal it rolls back through,
// uiStatic.cache stale-flag snapshots, the global renderUi, and an action-miss path that would ship an
// irreproducible `workbench:<useId>/…` slot to the server and block the GC sweep awaiting a reply). There
// is no journal to roll back here anyway: wsHooks null means propValueChange/setRef/arrayAdd/etc. never
// even reach a journal-pushing hook (they no-op at `wsHooks?.…`), so nothing was ever staged to undo. v1's
// STATED divergence (design doc, not silently accepted): a throw — a genuine bug or a VNA alike, "action:
// undefined" honesty either way — renders the REAL error into the card and leaves whatever partial writes
// already landed in place. Reset is the recovery, not an automatic rollback.
function runInstanceHandler(useId: number, container: HTMLElement, body: () => void): void {
    const instance = workbenchInstances.get(useId);
    if (instance == null) return; // torn down mid-dispatch (Reset, a removed use row, navigation away) — nothing to run

    let errorMessage: string | null = null;
    withSandboxGlobals(instance, useId, { memoBypass: true }, () => {
        try { body(); } catch (e) { errorMessage = e instanceof Error ? e.message : String(e); }
    });

    if (errorMessage != null) {
        // Render the real error directly (never through a normal re-render, which — over honestly partial
        // state — could itself throw a DIFFERENT, more confusing error, or silently succeed and hide that
        // something went wrong this click). noWiring: an error card is never interactive.
        const content = ensureInstanceContent(container, useId);
        updateChildren(content, [instanceErrorTag(errorMessage)], noWiring);
        return;
    }
    rerenderWorkbenchInstance(useId, container);
}

// The W1b wiring strategy for a successfully-rendered instance's content: mirrors wireEvents' OWN coverage
// (ui.ts wireEvents — two-way input/textarea/select bindings + onClick) but dispatches every event through
// runInstanceHandler (the sandbox bracket above) instead of the page's runHandlerTransaction/renderUi.
// Re-render on completion is the driver's OWN (rerenderWorkbenchInstance, via runInstanceHandler) — never
// the global renderUi, which would re-render the whole PAGE from the page's own (unrelated) state.
function instanceWiring(useId: number, container: HTMLElement): EventWireStrategy {
    return (el, tag) => {
        const checked = tag.attributes["checked"];
        const value = tag.attributes["value"];
        // Two-way binding for <input> and <textarea>, exactly like wireEvents.
        if ((tag.name === "input" || tag.name === "textarea") && (checked?.setValue || value?.setValue)) {
            (el as HTMLInputElement).oninput = () => {
                const input = el as HTMLInputElement;
                runInstanceHandler(useId, container, () => {
                    if (checked?.setValue) checked.setValue({ type: "bool", value: input.checked });
                    else if (value?.setValue) value.setValue(coerceInputValue(input.value, value.value));
                });
            };
        } else {
            (el as HTMLInputElement).oninput = null;
        }

        // Two-way binding for <select>, plus its optional onChange fn (RefSelect's applyPick shape) —
        // both run through the SAME bracket, like wireEvents' onClick/onChange do through
        // runHandlerTransaction on the page.
        const onChange = tag.attributes["onChange"]?.value;
        if (tag.name === "select" && (value?.setValue || (onChange != null && onChange.type === "fn"))) {
            const fn = onChange != null && onChange.type === "fn" ? onChange : null;
            (el as HTMLSelectElement).onchange = () => {
                const select = el as HTMLSelectElement;
                runInstanceHandler(useId, container, () => {
                    if (value?.setValue) value.setValue(coerceInputValue(select.value, value.value));
                    if (fn != null) callFunction(fn, instanceHandlerContext(useId), []);
                });
            };
        } else if (tag.name === "select") {
            (el as HTMLSelectElement).onchange = null;
        }

        const onClick = tag.attributes["onClick"]?.value;
        if (onClick != null && onClick.type === "fn") {
            const fn = onClick;
            el.onclick = (e: MouseEvent) => {
                e.stopPropagation();
                runInstanceHandler(useId, container, () => { callFunction(fn, instanceHandlerContext(useId), []); });
            };
        } else {
            el.onclick = null;
        }
    };
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
