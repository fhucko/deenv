// Client data-transfer state and the merge of a server payload into local AppState.
// Ported from the app14 prototype (dt.ts/ws.ts) and trimmed to first-paint state;
// the WebSocket round-trip and id remapping land in Stage 4. Global script — shares
// scope with codeExec.ts (the interpreter) and init.ts (uiStatic). Mirrors the C#
// emitter DeEnv/Code/ClientState.cs.

type DtValue = DtSimpleValue | DtObjectRef | DtArrayRef;
interface DtSimpleValue { type: "simple"; value: ExecInt | ExecBool | ExecText | ExecNull; }
interface DtObjectRef { type: "object"; id: number; }
interface DtArrayRef { type: "array"; id: number; }

interface DtScopeValue { isReadOnly: boolean; value: DtValue; }
interface ServerDtObject { props: { [name: string]: DtValue }; sourcePath?: string; scalarEntry?: boolean; ownerRef?: number; dictProp?: string; key?: string; }
interface ServerDtArray { kind: "set" | "dict" | "list"; elementTypeName?: string; sourcePath?: string; ownerRef?: number; dictProp?: string; items: { key: number; value: DtValue }[]; }

interface ServerDtState {
    leaves: { objects: { [id: number]: ServerDtObject }; arrays: { [id: number]: ServerDtArray } };
    scope: { [key: string]: DtScopeValue };
    cache: ServerCacheEntry[];
    // The store's version as of THIS render (optimistic-concurrency anti-clobber — DECISIONS.md "App
    // versioning — the full design (M13 clump)"). A refetch reply carries it (SsrRenderer.RenderState);
    // ws.ts advances clientKnownVersion from it. Not present on the SSR-embedded initData (that path
    // ships it separately as window.initStoreVersion — see ws.ts) — optional so both shapes typecheck
    // against this one interface.
    storeVersion?: number;
}

interface ServerCacheEntry { key: string; result: DtValue; deps: CacheDeps; }

interface AppState {
    objects: { [id: number]: ExecObject };
    arrays: { [id: number]: ExecArray };
    scope: ExecScope;
    localToServerIds: { [localId: number]: number };
    serverToLocalIds: { [serverId: number]: number };
}

const lastMergedScopeScalars: { [key: string]: ExecInt | ExecBool | ExecText | ExecNull | undefined } = {};

function isScalarValue(value: ExecValue | undefined): value is ExecInt | ExecBool | ExecText | ExecNull {
    return value?.type === "int" || value?.type === "bool" || value?.type === "text" || value?.type === "null";
}

function sameScalar(a: ExecInt | ExecBool | ExecText | ExecNull, b: ExecInt | ExecBool | ExecText | ExecNull): boolean {
    return a.type === b.type && (a.type === "null" || b.type === "null" || a.value === b.value);
}

// Merge a server state payload into uiStatic.state, resolving object/array refs to
// shared instances by id (so identity is preserved across the graph).
function mergeState(dtState: ServerDtState): void {
    const { objects, arrays, scope } = uiStatic.state;

    function fromDtValue(value: DtValue): ExecValue {
        switch (value.type) {
            case "simple":
                return value.value;
            case "object":
                return objects[value.id] ?? (objects[value.id] = { type: "object", id: value.id, props: {} });
            case "array":
                return arrays[value.id] ?? (arrays[value.id] = { type: "array", id: value.id, kind: "list", items: [] });
        }
    }

    for (const [idText, dtObj] of Object.entries(dtState.leaves.objects)) {
        const id = Number(idText);
        const obj = objects[id] ?? (objects[id] = { type: "object", id, props: {} });
        for (const [name, value] of Object.entries(dtObj.props)) obj.props[name] = fromDtValue(value);
        // A dict entry carries its path so a bound field edit persists path-addressed.
        if (dtObj.sourcePath != null) { obj.sourcePath = dtObj.sourcePath; obj.scalarEntry = dtObj.scalarEntry; }
        if (dtObj.ownerRef != null) obj.ownerRef = dtObj.ownerRef;
        if (dtObj.dictProp != null) obj.dictProp = dtObj.dictProp;
        if (dtObj.key != null) obj.key = dtObj.key;
    }

    for (const [idText, dtArr] of Object.entries(dtState.leaves.arrays)) {
        const id = Number(idText);
        const arr = arrays[id] ?? (arrays[id] = { type: "array", id, kind: dtArr.kind, items: [], elementTypeName: dtArr.elementTypeName });
        arr.kind = dtArr.kind;
        arr.elementTypeName = dtArr.elementTypeName;
        arr.sourcePath = dtArr.sourcePath;
        if (dtArr.ownerRef != null) arr.ownerRef = dtArr.ownerRef;
        if (dtArr.dictProp != null) arr.dictProp = dtArr.dictProp;
        for (const item of dtArr.items)
            if (!arr.items.some(p => p.key === item.key)) arr.items.push({ key: item.key, value: fromDtValue(item.value) });
    }

    // Keep client-minted (transient) ids below every shipped id, so a new object/array
    // can never reuse a server id (intrinsic positive, or server-derived negative).
    let minId = uiStatic.lastId.value;
    for (const id of Object.keys(objects)) minId = Math.min(minId, Number(id));
    for (const id of Object.keys(arrays)) minId = Math.min(minId, Number(id));
    uiStatic.lastId.value = minId;

    for (const [key, value] of Object.entries(dtState.scope)) {
        // `path` is CLIENT-OWNED navigation state — set by navClient/popstate/init from the LIVE URL. A
        // refetch ships the path it rendered FOR, but a (possibly stale) reply can land AFTER the user has
        // navigated on; overwriting would REVERT path to the refetch's value and re-render the OLD view
        // over the new URL — the back/forward nav race (a stale `/` reply clobbering a fresh `/notes/2`).
        // The client's live path is authoritative; the render reads it, so it always paints the real URL.
        if (key === "path") continue;
        // A var currently holding a client-minted draft (a transient object being
        // edited in a form) is client-held state: a server re-render recomputes its
        // initializer to a fresh empty draft, which must not clobber the user's input.
        const existing = scope.items[key];
        if (existing && existing.value.type === "object" && existing.value.id < 0) continue;
        const incoming = fromDtValue(value.value);
        const last = lastMergedScopeScalars[key];
        if (!value.isReadOnly && existing != null && isScalarValue(existing.value) && isScalarValue(incoming)
            && last != null && !sameScalar(existing.value, last) && !sameScalar(existing.value, incoming)) continue;
        scope.items[key] = { isReadOnly: value.isReadOnly, value: incoming };
        if (isScalarValue(incoming)) lastMergedScopeScalars[key] = incoming;
        else delete lastMergedScopeScalars[key];
    }

    // The memoized computation results + dependency refs, for reuse and invalidation.
    for (const entry of dtState.cache ?? [])
        uiStatic.cache.set(entry.key, { result: fromDtValue(entry.result), deps: entry.deps, stale: false });
}

// Client reachability GC (client data layer, the LAST slice) — the DUAL of the server store GC. mergeState
// GROWS uiStatic.state.objects/arrays on every refetch (1b merges by id; old views' rows + transient
// extents/where-results/descriptors linger) and nothing ever shrinks it. This mark-and-sweep drops the
// entries no live root can reach. Called on a NAVIGATION / view-state change (resetViewState, ui.ts) — the
// point where a whole view's data goes out of scope — NOT on the per-keystroke renderUi (too frequent).
//
// SAFE ONLY BECAUSE the round-trip can re-pull anything (slices 1a–4): if a later view needs swept data, a
// refetch reproduces the exact render and re-ships it (mergeState re-creates it by id). So a conservative,
// imperfect sweep is fine — but a FALSE-sweep (dropping something still reachable) is NOT: it would split an
// object's identity (the index loses id X, a re-ship/graph-walk mints a fresh shell X' while a live closure
// still holds the old X) and corrupt the live view or a pending rollback. So the rule is CONSERVATIVE: mark
// from EVERY root, keep more, sweep less.
//
// Sweeping only removes the id→object INDEX entries; it never mutates the objects or severs any closure's
// captured reference (a DOM handler's `() => f(c)` still holds the same `c`). The only failure mode is the
// identity split above, which the roots below close:
//   • SCOPE (uiStatic.state.scope) — every top-scope var: `db` (the entry into the whole live graph — its
//     props → arrays → items → objects), `currentUser`, the selection, and any live drafts.
//   • THE MEMO CACHE (uiStatic.cache) — each entry's `result` references the objects/arrays it returned (a
//     where/orderBy array, an extent list, a render closure). A render closure (`fn`) additionally holds its
//     view's data in its CAPTURED SCOPE (its `var state` — the same scope slotState() ships), so the walk
//     descends a fn's scope too. The LIVE RENDER reach folds in here: the render reads through scope + the
//     cache, so marking both covers what is on screen (the spec's allowed simplification).
//   • THE PENDING JOURNAL — an un-acked optimistic mutation must keep its referenced objects alive for a
//     rollback, INCLUDING an arrayRemove/entryRemove's DETACHED item (no longer in any array, reachable ONLY
//     through the journal entry — the one reference a graph walk cannot otherwise see). Closures are opaque,
//     so each entry exposes its pinned values as `roots` (ws.ts); we mark those.
// A `pendingAction` in flight (an action-miss between abandon and re-invoke) holds its handler's captured
// objects inside an opaque `reinvoke` closure that the walk cannot see; rather than risk it, the sweep is
// SKIPPED while one is pending (a rare, brief, transient state — the next nav sweeps once it has cleared).
function sweepUnreachable(): void {
    // Conservative: never sweep mid-action-miss (an opaque pending `reinvoke` closure may pin objects).
    if (typeof pendingAction !== "undefined" && pendingAction != null) return;

    const { objects, arrays } = uiStatic.state;
    const markedObjects = new Set<number>();
    const markedArrays = new Set<number>();
    const visitedScopes = new Set<ExecScope>(); // a scope is walked once (fns share scopes; the top scope
                                                // is reached from countless closures — re-walking explodes)
    const stack: ExecValue[] = [];

    function push(v: ExecValue | undefined): void { if (v != null) stack.push(v); }
    function pushScope(scope: ExecScope | null): void {
        for (let s = scope; s != null && !visitedScopes.has(s); s = s.parent) {
            visitedScopes.add(s);
            for (const it of Object.values(s.items)) push(it.value);
        }
    }

    // Roots: every top-scope var (→ the whole db graph), every cache result, and the journal's pinned values.
    for (const item of Object.values(uiStatic.state.scope.items)) push(item.value);
    for (const entry of uiStatic.cache.values()) push(entry.result);
    if (typeof journal !== "undefined")
        for (const e of journal) for (const r of e.roots ?? []) push(r);

    // Mark: walk object → prop values, array → item values, fn → captured-scope values, tag → attrs +
    // children. markedObjects/markedArrays dedupe by id (cyclic graph safe) — a value is expanded only the
    // first time its id is seen; visitedScopes dedupes scope walks (a fn has no id, and closures share the
    // top scope, so without this a fn would be re-expanded endlessly until the stack array overflows).
    while (stack.length > 0) {
        const v = stack.pop()!;
        switch (v.type) {
            case "object":
                if (markedObjects.has(v.id)) break;
                markedObjects.add(v.id);
                for (const p of Object.values(v.props)) push(p);
                break;
            case "array":
                if (markedArrays.has(v.id)) break;
                markedArrays.add(v.id);
                for (const it of v.items) push(it.value);
                break;
            case "fn":
                // A render closure pins its view's data in its captured scope chain (where `var state` lives).
                pushScope(v.scope);
                break;
            case "tag":
                // A `fn:` page result can be a tag tree holding object/array refs in attribute values + children.
                for (const a of Object.values(v.attributes)) push(a.value);
                for (const c of v.children) push(c);
                break;
        }
    }

    // Sweep: drop every index entry whose id was not marked. The objects themselves are untouched — only the
    // id→object lookup shrinks, so a still-live closure reference keeps working and a future re-ship re-indexes.
    for (const id of Object.keys(objects)) if (!markedObjects.has(Number(id))) delete objects[Number(id)];
    for (const id of Object.keys(arrays)) if (!markedArrays.has(Number(id))) delete arrays[Number(id)];
}
