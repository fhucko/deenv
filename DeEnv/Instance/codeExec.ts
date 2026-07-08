// The client-side twin of the C# Code interpreter (DeEnv/Code/CodeExecutor.cs),
// ported from the app14 prototype and adapted to the camelCase wire format. This is
// the evaluation core only — DOM rendering (ui.ts) and the WebSocket/data-transfer
// protocol (Stage 4) layer on top. The two interpreters are kept in lockstep by the
// shared conformance suite (DeEnv/Code/conformance.json), run here via runConformance.
//
// Authored as a global script (no import/export) so it can be injected and called
// directly. Op codes are the camelCase CodeInfixOpType values (CodeAst.cs).

// ── AST (mirrors DeEnv/Code/CodeAst.cs, "type"-discriminated, camelCase) ──────────

type CodeStatement = CodeAssignment | CodeBlock | CodeVarDec | CodeFunction | CodeReturn | CodeCall | CodeIf | CodeAmbient;

type CodeValue = CodeInt | CodeText | CodeBool | CodeNull | CodeSymbol | CodeObject | CodeArray |
    CodeFunction | CodeTag | CodeInfixOp | CodeCall | CodeAssignment | CodeNot | CodeTernary;

type CodeTagChild = CodeValue | CodeTagIf | CodeTagForEach;

interface CodeSymbol { type: "symbol"; name: string; }
interface CodeInt { type: "int"; value: number; }
interface CodeText { type: "text"; value: string; }
interface CodeBool { type: "bool"; value: boolean; }
interface CodeNull { type: "null"; }
interface CodeArray { type: "array"; items: CodeValue[]; }
interface CodeObject { type: "object"; props: CodeObjectProp[]; }
interface CodeObjectProp { name: string; value: CodeValue; }
interface CodeInfixOp { type: "infixOp"; op: string; left: CodeValue; right: CodeValue; }
interface CodeNot { type: "not"; operand: CodeValue; }
interface CodeTernary { type: "ternary"; condition: CodeValue; then: CodeValue; else: CodeValue; }
interface CodeAssignment { type: "assign"; target: CodeValue; value: CodeValue; }
interface CodeBlock { type: "block"; statements: CodeStatement[]; }
interface CodeVarDec { type: "varDec"; name: string; value: CodeValue | null; }
interface CodeAmbient { type: "ambient"; name: string; value: CodeValue; }
interface CodeFunction { type: "fn"; name: string | null; params: CodeFunctionParam[]; body: CodeBlock; id?: number; }
interface CodeFunctionParam { name: string; }
interface CodeReturn { type: "return"; value: CodeValue; }
interface CodeCall { type: "call"; fn: CodeValue; params: CodeValue[]; }
interface CodeIf { type: "if"; condition: CodeValue; body: CodeStatement; elseBody: CodeStatement | null; }
interface CodeTag { type: "tag"; name: string; attributes: CodeTagAttribute[]; children: CodeTagChild[]; }
interface CodeTagAttribute { name: string; value: CodeValue; }
interface CodeTagIf { type: "if"; condition: CodeValue; body: CodeTagChild[]; elseBody: CodeTagChild[] | null; }
interface CodeTagForEach { type: "foreach"; item: CodeSymbol; collection: CodeValue; body: CodeTagChild[]; }

// ── runtime values (mirrors DeEnv/Code/ExecValues.cs) ─────────────────────────────

type ExecValue = ExecFunction | ExecSysFunction | ExecTag | ExecArray | ExecObject |
    ExecInt | ExecBool | ExecText | ExecNull | ExecNothing | ExecCtx | ExecCtxMethod;
type ExecTagChild = ExecValue;
type ExecResult = { value: ExecValue; setValue?: (value: ExecValue) => void; };

interface ExecInt { type: "int"; value: number; }
interface ExecBool { type: "bool"; value: boolean; }
interface ExecText { type: "text"; value: string; }
interface ExecNull { type: "null"; }
interface ExecNothing { type: "nothing"; }
interface ExecObject { type: "object"; props: { [name: string]: ExecValue }; id: number; sourcePath?: string; scalarEntry?: boolean; }
// The Code array — one collection shape for every kind, identical on server, wire, and
// client. A positive id ⇒ persisted (a db set/dict); a negative id ⇒ transient (a list
// literal or where/orderBy result). ElementTypeName is the member type (set/dict only).
interface ExecArray { type: "array"; kind: "set" | "dict" | "list"; items: ExecArrayItem[]; id: number; elementTypeName?: string; sourcePath?: string; }
interface ExecArrayItem { key: number; value: ExecValue; }
// `handlerSlot` (client data layer, slice 4): the render-slot path active when an onClick handler closure was
// built (stamped by executeTag), so a click can report the handler's (slot, fn-id) to the server's action-miss
// harvest — the slot path is reset per render, so it must be captured on the closure at build time. Set only on
// onClick handler closures; absent on every other fn.
interface ExecFunction { type: "fn"; fn: CodeFunction; scope: ExecScope; capturedAmbient?: AmbientFrame | null; handlerSlot?: string; }
interface ExecSysFunction { type: "sysFn"; fn(args: ExecValue[]): ExecValue; }
// A data context: staged field writes over a parent, read-through. live = the root (writes go
// live); a sub-context (ctx.new) stages until commit. Staged keyed by object reference.
// `status`/`pending` are the form-Save lifecycle (rendered by the generic ObjectForm as inline
// feedback). status is "idle" | "saving" | "saved" (a reject clears back to "idle" — the failure
// surfaces via the global error banner, not inline); pending counts THIS ctx's in-flight
// commit sends. CLIENT-only — the C# twin renders once, so ctx.status there is always "idle" and
// these fields don't exist server-side (the property read returns the literal "idle"). `id` is a
// unique handle so a `ctx.status` READ records a reactive var dep (`ctxStatus:<id>`) and a status
// WRITE invalidates it — that is what makes the rendered indicator re-render on the WS ack (the
// memoized component view is otherwise served from cache, since ctx is not a tracked object).
// `conflicts` (M13 slice 6): the same-field collisions from the LAST rejected commit of this ctx — CLIENT-
// only (a conflict lands on a WS reply; the C# twin renders once and returns the EMPTY list). Each entry is
// a `{ field: text }` ExecObject the generic form's coarse banner iterates; empty = no conflict (the common
// case). Read via `ctx.conflicts` (records a `ctxConflicts:<id>` var dep so the banner re-renders when a
// reply populates it), written by setCtxConflicts from ws.ts on a conflict reply / a resolution.
interface ExecCtx { type: "ctx"; id: number; staged: Map<ExecObject, Map<string, ExecValue>>; creates: StagedCreate[]; parent: ExecCtx | null; live: boolean; status: string; pending: number; conflicts: ExecValue[]; }
// A staged create (atomic-commit Step B): a transient (id<0) draft `set.add`/`setRef`'d under a staging
// ctx. The draft is held BY REFERENCE — its fields are read at commit time, never snapshotted, so a later
// `draft.x = …` (an edit landing on the draft's own live props, since id<0 bypasses staging) is included.
// `join` is where the draft attaches; at the outermost commit the create mints and the join applies, all
// in one atomic `commit` op. Beside `staged` (the edits to EXISTING objects).
interface StagedCreate { draft: ExecObject; join: CreateJoin; }
// Where a staged create attaches: into a set (set.add) or onto a single reference field (setRef). The
// element/target TYPE is resolved SERVER-SIDE from the join (the set's element type, or the prop's declared
// type) — the wire carries no client-asserted type, so the floor can't be widened by a crafted type.
type CreateJoin = { kind: "set"; set: ExecArray } | { kind: "ref"; parent: ExecObject; prop: string };
// Unique per-ctx id (form-Save feedback) — namespaced into the var-dep key, so it never collides with
// an object id or a real var name.
let nextCtxId = 1;
function ctxStatusDep(ctxId: number): string { return "ctxStatus:" + ctxId; }
// Set a ctx's Save status and invalidate the reactive var dep so any rendered ctx.status indicator
// re-renders. The single write path for the lifecycle — called from the commit branch (codeExec) and
// the WS ack/reject (ws.ts), so every transition both updates the value and triggers the re-render.
function setCtxStatus(ctx: ExecCtx, status: string): void {
    ctx.status = status;
    invalidateVar(ctxStatusDep(ctx.id));
}
// The reactive var dep a `ctx.conflicts` READ records (M13 slice 6) — namespaced by ctx id like the status
// dep, so a conflict reply that sets/clears conflicts re-renders the memoized component view that reads it.
function ctxConflictsDep(ctxId: number): string { return "ctxConflicts:" + ctxId; }
// Set a ctx's same-field conflicts and invalidate the var dep so the coarse banner (which reads
// `ctx.conflicts`) re-renders. The single write path — called from ws.ts on a conflict reply (populate) and
// on a keep-mine/take-theirs resolution (clear to []). Mirrors setCtxStatus exactly.
function setCtxConflicts(ctx: ExecCtx, conflicts: ExecValue[]): void {
    ctx.conflicts = conflicts;
    invalidateVar(ctxConflictsDep(ctx.id));
}
interface ExecCtxMethod { type: "ctxMethod"; ctx: ExecCtx; method: string; }
interface ExecTag { type: "tag"; name: string; attributes: { [name: string]: ExecResult }; children: ExecTagChild[]; key?: number; }

// isTop marks a persistent top-level scope (the framework system scope, or the app scope)
// whose writable vars are reactive — read in a computation they are deps, assigned they
// invalidate the memo cache. Transient local scopes (fn calls, blocks, foreach) leave it unset.
interface ExecScope { items: { [name: string]: ExecScopeItem }; parent: ExecScope | null; isTop?: boolean; }
interface ExecScopeItem { value: ExecValue; isReadOnly: boolean; }
// `seed` (client data layer, slice 1a) — twin of C# ExecContext.Seed: a map from a component's
// render-slot (`comp:<slotpath>`) to its setup-scope locals (`state`), applied right after that
// component's setup runs so a render reproduces the client's exact component view-state. Undefined =
// today's behavior (the setup defaults stand). The SEED-CONSUMPTION half; the client SHIP of state is
// a later slice. (Slot paths are twin-identical, so the same key lands on the same component.)
interface ExecContext { lastId: LastId; ambient?: AmbientFrame | null; seed?: { [slotKey: string]: { [varName: string]: ExecValue } } | null; }
interface AmbientFrame { name: string; value: ExecValue; parent: AmbientFrame | null; }
interface LastId { value: number; }

// ── statements ────────────────────────────────────────────────────────────────────

// A statement yields null when it does NOT return (mirroring the C# executor) —
// `nothing` is a real returned value (a void call's result), so `return voidFn()`
// must still exit the enclosing block.
function executeStatement(statement: CodeStatement, scope: ExecScope, context: ExecContext): ExecValue | null {
    switch (statement.type) {
        case "assign": executeAssignment(statement, scope, context); return null;
        case "block": return executeBlock(statement, scope, context);
        case "varDec": executeVarDec(statement, scope, context); return null;
        case "fn": executeFunction(statement, scope, context); return null;
        case "return": return executeValue(statement.value, scope, context).value;
        case "call": executeCall(statement, scope, context); return null;
        case "if": return executeIf(statement, scope, context);
        case "ambient": context.ambient = { name: statement.name, value: executeValue(statement.value, scope, context).value, parent: context.ambient ?? null }; return null;
        default: throw new Error("NotImplementedException");
    }
}

function executeIf(codeIf: CodeIf, scope: ExecScope, context: ExecContext): ExecValue | null {
    const condition = executeValue(codeIf.condition, scope, context).value;
    if (condition.type !== "bool") throw new Error("Result of if condition is not boolean.");
    const code = condition.value ? codeIf.body : codeIf.elseBody;
    return code == null ? null : executeStatement(code, scope, context);
}

function executeFunction(fun: CodeFunction, scope: ExecScope, context: ExecContext): ExecFunction {
    const fn: ExecFunction = { type: "fn", fn: fun, scope, capturedAmbient: context.ambient ?? null };
    if (fun.name != null) scope.items[fun.name] = { value: fn, isReadOnly: true };
    return fn;
}

function executeVarDec(varDec: CodeVarDec, scope: ExecScope, context: ExecContext): void {
    if (scope.items[varDec.name]) throw new Error(`Variable ${varDec.name} already exists`);
    const value: ExecValue = varDec.value == null ? { type: "null" } : executeValue(varDec.value, scope, context).value;
    scope.items[varDec.name] = { value, isReadOnly: false };
}

function executeBlock(block: CodeBlock, scope: ExecScope, context: ExecContext): ExecValue | null {
    const innerScope: ExecScope = { parent: scope, items: {} };
    const savedAmbient = context.ambient;   // ambient provides in this block pop on exit
    try {
        for (const statement of block.statements) {
            const value = executeStatement(statement, innerScope, context);
            if (value != null) return value;
        }
        return null;
    } finally { context.ambient = savedAmbient; }
}

function executeAssignment(assignment: CodeAssignment, scope: ExecScope, context: ExecContext): ExecValue {
    const value = executeValue(assignment.value, scope, context).value;
    const target = assignment.target;
    if (target.type === "symbol") {
        const itemScope = findScope(target, scope);
        const item = itemScope.items[target.name];
        if (item.isReadOnly) throw new Error(`Symbol ${target.name} is read only`);
        item.value = value;
        // Assigning a top-scope UI-state var invalidates every cached computation that read it.
        if (itemScope.isTop) invalidateVar(target.name);
        return value;
    }
    // An object-field lvalue (`obj.member = …`): set in place, the same write path two-way
    // binding uses — invalidate readers, and persist when the object is server-backed.
    if (target.type === "infixOp" && target.op === "objectProp" && target.right.type === "symbol") {
        const obj = executeValue(target.left, scope, context).value;
        if (obj.type !== "object") throw new Error("Cannot assign a field on a non-object.");
        const prop = target.right.name;
        // In a staging context the write stages — the live object is untouched until commit.
        // Gated to persisted (positive-id) objects: a transient draft (sys.new, id<0) writes live,
        // so a create-form's draft is not entangled in the surrounding edit transaction. (id>0 is
        // today's proxy for "real identity in the live store" — a just-added object still awaiting its
        // negative→real remap is also id<0, but nothing routes one into a staging form. Revisit if so.)
        const staging = obj.id > 0 ? nearestStagingCtx(context) : null;
        if (staging != null) {
            let fields = staging.staged.get(obj);
            if (fields == null) staging.staged.set(obj, fields = new Map());
            fields.set(prop, value);
            invalidateProp(obj.id, prop);
            return value;
        }
        const before = obj.props[prop];
        obj.props[prop] = value;
        invalidateProp(obj.id, prop);
        if (obj.id > 0) propValueChange(obj, prop, value, before);
        return value;
    }
    throw new Error("Invalid assignment target.");
}

// ── values ──────────────────────────────────────────────────────────────────────

function executeValue(value: CodeValue, scope: ExecScope, context: ExecContext): ExecResult {
    switch (value.type) {
        case "int": return { value: { type: "int", value: value.value } };
        case "text": return { value: { type: "text", value: value.value } };
        case "bool": return { value: { type: "bool", value: value.value } };
        case "null": return { value: { type: "null" } };
        case "fn": return { value: executeFunction(value, scope, context) };
        // A tag in VALUE position whose name resolves to a function is a component (a root/returned
        // component) — run it slot-keyed and yield its view value; otherwise it's an HTML element.
        case "tag": {
            const component = tryResolveComponent(value.name, scope);
            return { value: component ? executeComponentValue(value, component, scope, context) : executeTag(value, scope, context) };
        }
        case "infixOp": return executeInfixOp(value, scope, context);
        case "not": return executeNot(value, scope, context);
        // Ternary: evaluate the condition, then ONLY the chosen branch (short-circuit). Delegating
        // to executeValue on the branch preserves its setValue. Twin of CodeExecutor's CodeTernary arm.
        case "ternary": {
            const cond = executeValue(value.condition, scope, context).value;
            const branch = (cond.type === "bool" ? cond.value : false) ? value.then : value.else;
            return executeValue(branch, scope, context);
        }
        // sys.field(obj, name) is a bindable lvalue (two-way binding needs its setValue), so it
        // is resolved here rather than through executeCall (which drops setValue). Its callee is
        // a `sys.field` member access, so recognize the sys-rooted callee (not a bare symbol).
        case "call":
            if (sysBuiltinName(value.fn) === "field") return fieldResult(value, scope, context);
            return { value: executeCall(value, scope, context) };
        case "symbol": return executeSymbol(value, scope, context);
        case "object": return { value: executeObject(value, scope, context) };
        case "array": return { value: executeArray(value, scope, context) };
        case "assign": return { value: executeAssignment(value, scope, context) };
        default: throw new Error("NotImplementedException");
    }
}

function executeArray(codeArray: CodeArray, scope: ExecScope, context: ExecContext): ExecArray {
    const items: ExecArrayItem[] = codeArray.items.map(p => ({ key: --context.lastId.value, value: executeValue(p, scope, context).value }));
    return { type: "array", kind: "list", items, id: --context.lastId.value };
}

function executeObject(codeObject: CodeObject, scope: ExecScope, context: ExecContext): ExecObject {
    const props: { [name: string]: ExecValue } = {};
    for (const prop of codeObject.props) props[prop.name] = executeValue(prop.value, scope, context).value;
    return { type: "object", props, id: --context.lastId.value };
}

function executeSymbol(codeSymbol: CodeSymbol, scope: ExecScope, context: ExecContext): ExecResult {
    const itemScope = tryFindScope(codeSymbol, scope);
    if (itemScope == null) {
        for (let f = context.ambient ?? null; f != null; f = f.parent)
            if (f.name === codeSymbol.name) return { value: f.value };   // dynamic-scope fallback
        throw new Error(`Variable ${codeSymbol.name} not found`);
    }
    const item = itemScope.items[codeSymbol.name];
    // A writable top-scope var read inside a computation is a dependency: assigning
    // the var must invalidate the cached result. (Read-only items — db, functions —
    // can never be reassigned, so they are not deps.)
    if (itemScope.isTop && !item.isReadOnly) recordVar(codeSymbol.name);
    return {
        value: item.value,
        setValue: p => {
            item.value = p;
            if (itemScope.isTop) invalidateVar(codeSymbol.name);
        },
    };
}

const collectionMethods = ["add", "remove", "setEntry", "where", "orderBy", "any", "single"];

// ── memoization cache (Stage 4) ────────────────────────────────────────────────────
// Mirrors the server (DeEnv/Code/MemoCache.cs). Computation boundaries (user-fn calls,
// where/orderBy) memoize by the same (function, args) key. The client reuses a fresh
// result, recomputes a stale one when its deps are present, and invalidates by
// dependency on each mutation. memoCache is null when codeExec runs standalone
// (conformance) → no caching, just compute.

interface CacheDeps { props: { obj: number; prop: string }[]; members: number[]; vars: string[]; }
// argsKey: the identity of the args a component slot was last invoked with — a re-invocation at the SAME
// slot with a CHANGED arg (e.g. a create-form draft replaced by a fresh sys.new) re-binds/recomputes it
// (reactive props). incomplete: this entry's compute swallowed a "Value not available" below it (a
// speculative render over un-shipped data) — it is dropped on the next refetch so it recomputes over the
// now-complete data. Both are client-only (absent on server-shipped entries, where they default falsy).
interface ClientCacheEntry { result: ExecValue; deps: CacheDeps; stale: boolean; argsKey?: string; incomplete?: boolean; }

let memoCache: Map<string, ClientCacheEntry> | null = null;
const depStack: CacheDeps[] = [];

// The render-tree slot path (twin of ExecContext.SlotPath) — executeTagChildren pushes each
// child's static AST index so a tag-invoked component keys its run-once setup on its render-tree
// position, not its argument identities. Module-level like depStack; balanced push/pop returns it
// to empty between renders, and the render entry (renderUi) resets it defensively.
const slotPath: string[] = [];
function resetSlotPath(): void { slotPath.length = 0; }

// Objects minted by sys.new (create-form drafts) — the bindable identities a component's view pins to.
// objectArgKey tracks a swap of one of these (or a stable positive-id db object), but NOT a re-minted
// descriptor (sys.schema ships a fresh negative-id object each refetch though its meaning is constant) —
// tracking those would re-bind every descriptor-taking component on every refetch (churn). A WeakSet keeps
// this off the object's serialized shape, so the twin/conformance (which compares sys.new's output) is
// untouched. Membership survives a refetch (the draft object is preserved by mergeState).
const draftObjects = new WeakSet<ExecObject>();

// True while an event handler runs: handlers may be side-effecting (assignments,
// factory calls), so their calls must never hit or fill the cache.
let memoBypass = false;
function runWithMemoBypass(f: () => void): void {
    memoBypass = true;
    try { f(); } finally { memoBypass = false; }
}

// A computation read data the server never shipped (and has no cached result):
// the next maybeRefetch round-trips for authoritative state.
let needsServerData = false;

// Monotonic count of swallowed VNAs ("Value not available" reads over un-shipped data). memoize samples it
// before/after each compute: a delta means the result (or a child it spliced) was built over INCOMPLETE
// data, so the entry is flagged and dropped on the next refetch (see ClientCacheEntry.incomplete + ws.ts).
// NEVER reset this (unlike slotPath, which IS reset per render): only DELTAS are read, so zeroing it
// mid-render would corrupt any in-flight before/after sample straddling the reset and mis-flag entries.
let vnaSwallows = 0;

function setMemoCache(cache: Map<string, ClientCacheEntry> | null): void { memoCache = cache; }

function recordProp(objId: number, prop: string): void {
    if (depStack.length > 0) depStack[depStack.length - 1].props.push({ obj: objId, prop });
}
function recordMember(arrId: number): void {
    if (depStack.length > 0) depStack[depStack.length - 1].members.push(arrId);
}
// Which ctxs had their `conflicts` READ during the current render — i.e. a resolver "door" (the generic
// <ConflictBar>, or a custom render's own resolver) surfaced the conflict. Cleared at the start of every
// render (buildRenderTree); marked at the `ctx.conflicts` read below. handleConflictReply consults it to
// decide the no-silent-clobber fallback banner: it fires ONLY for a ctx whose conflict NO render surfaced
// (an app that ignores ctx.conflicts) — where the "reload" advice is the only door. Global-script scope.
const conflictSurfacedThisRender = new Set<number>();
function recordVar(name: string): void {
    if (depStack.length > 0) depStack[depStack.length - 1].vars.push(name);
}
function mergeDeps(into: CacheDeps, from: CacheDeps): void {
    into.props.push(...from.props);
    into.members.push(...from.members);
    into.vars.push(...from.vars);
}

function argKey(v: ExecValue): string {
    switch (v.type) {
        case "object": return "o" + v.id;
        case "array": return "a" + v.id;
        case "int": return "i" + v.value;
        case "bool": return "b" + (v.value ? 1 : 0);
        case "text": return "t" + v.value.length + ":" + v.value;
        case "null": return "n";
        default: return "?";
    }
}
function memoKey(callee: string, args: ExecValue[]): string {
    return args.length === 0 ? callee : callee + "|" + args.map(argKey).join(",");
}

// The captured-environment part of a where/orderBy memo key — twin of CodeExecutor.cs's
// ClosureKey. The lambda's free vars vary per call (e.g. a foreach loop var it closes
// over) yet the lambda AST id is the SAME node every iteration, so (collection id, lambda
// id) alone collides — every call returns the first's result. Fold in the lambda's
// captured NON-top scope values: walk its scope chain up while !isTop (the transient
// frames — fn calls, blocks, foreach items — holding the closed-over locals), keying each
// bound item by argKey; stop at the first top scope (globals are stable, the collection id
// already covers the data). Names are sorted so both interpreters enumerate a scope
// identically. Over-keying on all captured locals (a superset of the actual free vars)
// only costs an extra recompute; it is never stale.
function closureKey(lambda: ExecFunction): string {
    let key = "";
    for (let s: ExecScope | null = lambda.scope; s != null && !s.isTop; s = s.parent)
        for (const name of Object.keys(s.items).sort())
            key += ":" + name + "=" + argKey(s.items[name].value);
    return key;
}

function memoize(key: string, context: ExecContext, compute: () => ExecValue): ExecValue {
    if (memoCache == null || memoBypass) return compute();
    const existing = memoCache.get(key);
    if (existing && !existing.stale) {
        if (depStack.length > 0) mergeDeps(depStack[depStack.length - 1], existing.deps);
        // Splicing an INCOMPLETE child taints the enclosing computation: count the swallow against the
        // current compute so its entry is flagged incomplete too — propagating through HITS (not just the
        // same render) so a parent that re-reads a cached-empty child is dropped on refetch, not left stale.
        if (existing.incomplete) vnaSwallows++;
        return existing.result;
    }
    const deps: CacheDeps = { props: [], members: [], vars: [] };
    const lastIdBefore = context.lastId.value;
    const vnaBefore = vnaSwallows;
    depStack.push(deps);
    try {
        const result = compute();
        // An identity-creating computation — its result is a transient OBJECT minted
        // inside it (a `getNewX()` factory) — is not pure: caching it would hand every
        // caller the same mutable instance. (A derived array stays cacheable.)
        if (!(result.type === "object" && result.id < 0 && result.id < lastIdBefore))
            // incomplete: a VNA was swallowed under this compute (directly, or via a spliced incomplete
            // child) — the result is built over partial data, so the refetch cleanup must drop it.
            memoCache.set(key, { result, deps, stale: false, incomplete: vnaSwallows > vnaBefore });
        return result;
    } catch (e) {
        if (e instanceof Error && e.message === "Value not available") {
            // A dependency the server never shipped: ask the server either way, and
            // show the stale result (if any) until the refetch lands.
            //
            // LOAD-BEARING INVARIANT (ws.ts refetch cleanup): a swallowed VNA with NO prior cached
            // result is deliberately NOT cached here — we return the empty `nothing` WITHOUT
            // memoCache.set, so this poisoned key never persists. A `fn:` PAGE function (e.g. the
            // designer's designEditorPage) whose body merely READS a swallowed-empty inner result does
            // NOT itself throw, so IT does get cached — with a tag/fn RESULT. ws.ts's refetch cleanup
            // relies on that: it drops every persisted `fn:` entry whose result is a tag/fn so the
            // post-refetch render recomputes the page over the now-complete data. The contract is that a
            // persisted poisoned `fn:` entry always surfaces as such a tag/fn result (never a bare cached
            // empty), which holds precisely because the direct-VNA path below skips the cache. (See ws.ts
            // onWsMessage's refetch branch.) The fn:-result heuristic only catches PAGE functions; counting
            // the swallow here additionally flags every enclosing memoize (incl. a poisoned `comp:`-view) as
            // incomplete, so the refetch cleanup drops those too — precisely, without dropping healthy state.
            //
            // Count the swallow as incompleteness ONLY when there is NO prior result: that is the genuinely
            // MISSING case (the compute spliced an empty `nothing`), which the refetch must recompute. A
            // STALE-but-present result (existing != null — e.g. a RefEditor reading an extent that setRef
            // coarsely staled, whose membership is unchanged) is returned as-is and handled by the NORMAL
            // stale-invalidation path; flagging it incomplete would needlessly drop+re-render a view that has
            // usable data, re-reading possibly-stale merged data and racing the optimistic display.
            if (existing == null) vnaSwallows++;
            needsServerData = true;
            return existing != null ? existing.result : { type: "nothing" };
        }
        throw e;
    } finally {
        depStack.pop();
        if (depStack.length > 0) mergeDeps(depStack[depStack.length - 1], deps);
    }
}

function invalidateProp(objId: number, prop: string): void {
    if (memoCache == null) return;
    for (const e of memoCache.values())
        if (e.deps.props.some(d => d.obj === objId && d.prop === prop)) e.stale = true;
}
function invalidateMember(arrId: number): void {
    if (memoCache == null) return;
    for (const e of memoCache.values())
        if (e.deps.members.includes(arrId)) e.stale = true;
}
function invalidateVar(name: string): void {
    if (memoCache == null) return;
    for (const e of memoCache.values())
        if (e.deps.vars.includes(name)) e.stale = true;
}
// Coarsely stale every extent computation: a mint or reference change can alter any
// type's candidate list, and the client can't recompute it (no store) — staling forces a
// refetch that re-renders with the fresh extents. (Precise per-type deps can come later.)
function invalidateExtents(): void {
    if (memoCache == null) return;
    for (const [key, e] of memoCache) if (key.startsWith("extent:")) e.stale = true;
}

function executeInfixOp(codeInfixOp: CodeInfixOp, scope: ExecScope, context: ExecContext): ExecResult {
    if (codeInfixOp.op !== "objectProp") return { value: executeInfixOpBasic(codeInfixOp, scope, context) };

    const left = executeValue(codeInfixOp.left, scope, context).value;
    const right = codeInfixOp.right;
    if (right.type !== "symbol") throw new Error("Object-prop access expects a symbol on the right.");

    if (left.type === "array" && collectionMethods.includes(right.name))
        return { value: collectionSysFunction(left, right.name, context) };

    if (left.type === "ctx") {
        // dirty counts staged EDITS and staged CREATES (atomic-commit Step B): a form with only a staged
        // create still has unsaved work, so the Save chrome must light up. Twin of CodeExecutor's ExecCtx read.
        if (right.name === "dirty") return { value: { type: "bool", value: left.staged.size > 0 || left.creates.length > 0 } };
        // ctx.status: the form-Save lifecycle ("idle" | "saving" | "saved"), a readable
        // property like dirty. CLIENT-only state — on the C# twin (which renders once) it is always "idle".
        // Record a reactive var dep so the enclosing (memoized) component view re-renders when a status
        // WRITE invalidates it — otherwise the cached view would never reflect the WS ack.
        if (right.name === "status") { recordVar(ctxStatusDep(left.id)); return { value: { type: "text", value: left.status } }; }
        // ctx.conflicts: the same-field collisions from the last rejected commit (M13 slice 6) as a LIST the
        // banner iterates (`ctx.conflicts.any(...)` / `foreach c in ctx.conflicts`). CLIENT-only — empty on
        // the C# twin. Record the conflicts var dep so a conflict reply (setCtxConflicts) re-renders the
        // memoized form. A fresh list wrapper each read so the reconciler treats it as its own value.
        if (right.name === "conflicts") {
            recordVar(ctxConflictsDep(left.id));
            conflictSurfacedThisRender.add(left.id); // this render surfaced the conflict → the app owns it (no fallback banner)
            return { value: { type: "array", kind: "list", items: left.conflicts.map((v, i) => ({ key: i, value: v })), id: --context.lastId.value } };
        }
        return { value: { type: "ctxMethod", ctx: left, method: right.name } };
    }

    // Property access on null/nothing FAILS CLOSED: it yields null, never a throw — the twin of
    // CodeExecutor.ExecuteInfixOp. The M-auth obligation: a currentUser-dependent access condition
    // must DENY (not error) for an anonymous request, so `currentUser.role` with `currentUser == null`
    // reads `null.role` → null and `null == "Admin"` is false. Null propagates through a chain. (A
    // missing field on a REAL object stays a "Value not available" below — the refetch path, unchanged.)
    if (left.type === "null" || left.type === "nothing") return { value: { type: "null" } };

    if (left.type !== "object") throw new Error(`Cannot read '${right.name}' on a non-object.`);
    const value = nearestStagedValue(left, right.name, context) ?? left.props[right.name];
    if (value == null) throw new Error("Value not available");
    recordProp(left.id, right.name);
    return {
        value,
        setValue: p => {
            const before = left.props[right.name];
            left.props[right.name] = p;
            invalidateProp(left.id, right.name);
            persistFieldEdit(left, right.name, p, before);
        },
    };
}

// Persist a bound field edit: a positive id is server-backed (id-addressed objectPropChange);
// a dictionary entry has no extent id but carries its SourcePath, so it persists via the
// PATH-addressed `write` op (a scalar entry writes at its path, an object entry at path/prop).
function persistFieldEdit(obj: ExecObject, name: string, value: ExecValue, before: ExecValue): void {
    if (obj.id > 0) { propValueChange(obj, name, value, before); return; }   // server-backed extent object
    if (obj.sourcePath != null) {                                            // a dictionary entry (no extent id)
        const path = obj.scalarEntry ? obj.sourcePath : obj.sourcePath + "/" + name;
        pathWriteChange(obj, name, path, value, before);
        return;
    }
    // obj.id <= 0 with no sourcePath: a just-added SET MEMBER edited before its arrayAdd round-trip remapped
    // its transient negative id to a real one (under load the round-trip is slow, so the edit lands first).
    // Persist by the transient id anyway — the server resolves it through the add's transient-id remap. A
    // purely transient DRAFT (an object never added to a set) is filtered out in the ws propChange hook:
    // it has no server object yet, and its fields ship with its own eventual arrayAdd.
    propValueChange(obj, name, value, before);
}

// field(obj, name): dynamic by-name prop access — the reflective twin of `obj.member`,
// for the self-hosted generic UI (the prop name comes from schema data at runtime). Same
// dependency recording and write-back/persist as the static objectProp branch above.
function fieldResult(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecResult {
    if (codeCall.params.length !== 2) throw new Error("field(obj, name) takes two arguments.");
    const obj = executeValue(codeCall.params[0], scope, context).value;
    const nameV = executeValue(codeCall.params[1], scope, context).value;
    if (obj.type !== "object") throw new Error("field() expects an object.");
    if (nameV.type !== "text") throw new Error("field() expects a text field name.");
    const name = nameV.value;
    // Capture the staging context at render (ctx is active here); the deferred setValue stages into it.
    // Gated to persisted objects (id>0): a transient draft writes live, isolating create-form drafts.
    const staging = obj.id > 0 ? nearestStagingCtx(context) : null;
    const value = nearestStagedValue(obj, name, context) ?? obj.props[name];
    if (value == null) throw new Error("Value not available");
    recordProp(obj.id, name);
    return {
        value,
        // In a staging context the edit stages (the live object is untouched until commit); otherwise
        // it persists immediately (autosave / the live root).
        setValue: p => {
            if (staging != null) {
                let fields = staging.staged.get(obj);
                if (fields == null) staging.staged.set(obj, fields = new Map());
                fields.set(name, p);
                invalidateProp(obj.id, name);
                return;
            }
            const before = obj.props[name];
            obj.props[name] = p;
            invalidateProp(obj.id, name);
            persistFieldEdit(obj, name, p, before);
        },
    };
}

// humanize(text): a prop name → a human label ("companyName" → "Company name").
// Twin of DeEnv.Code.TextUtil.Humanize; pinned by the conformance suite.
function humanizeText(name: string): string {
    if (!name) return name;
    const isUpper = (c: string) => c >= "A" && c <= "Z";
    const isLower = (c: string) => c >= "a" && c <= "z";
    const isDigit = (c: string) => c >= "0" && c <= "9";
    let out = "";
    for (let i = 0; i < name.length; i++) {
        const c = name[i];
        if (c === "_" || c === "-") { out += " "; continue; }
        if (isUpper(c) && i > 0 && (isLower(name[i - 1]) || isDigit(name[i - 1]))) out += " ";
        out += c.toLowerCase();
    }
    out = out.trim();
    return out.length === 0 ? out : out[0].toUpperCase() + out.slice(1);
}

function execHumanize(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    if (codeCall.params.length !== 1) throw new Error("humanize(text) takes one argument.");
    const v = executeValue(codeCall.params[0], scope, context).value;
    if (v.type !== "text") throw new Error("humanize() expects a text value.");
    return { type: "text", value: humanizeText(v.value) };
}

// nest(base, seg): a URL path-join ("/notes" + a member → "/notes/3") — Code has no string
// concatenation. `seg` is a text (a prop name, or — for a future dictionary route — a text/int
// key) or an object (→ its intrinsic id). A trailing "/" on base is trimmed so nest("/", "x")
// == "/x". One primitive covers prop names, set members, and dict keys — no further URL builtin.
function execNest(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    if (codeCall.params.length !== 2) throw new Error("nest(base, seg) takes two arguments.");
    const baseV = executeValue(codeCall.params[0], scope, context).value;
    if (baseV.type !== "text") throw new Error("nest() expects a text base path.");
    const seg = executeValue(codeCall.params[1], scope, context).value;
    const segStr = seg.type === "object" ? String(seg.id)
        : seg.type === "int" ? String(seg.value)
        : seg.type === "text" ? seg.value
        : (() => { throw new Error("nest() expects a text or object segment."); })();
    return { type: "text", value: baseV.value.replace(/\/+$/, "") + "/" + segStr };
}

// segment(path, n): the n-th "/"-delimited segment of `path` as text ("" if out of range) —
// the URL-DESTRUCTURING twin of nest. RAW split on "/" indexed by `n` (the leading slash
// yields an empty first segment). The framework does the string work; Code gains no general
// string ops.
function execSegment(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    if (codeCall.params.length !== 2) throw new Error("segment(path, n) takes two arguments.");
    const pathV = executeValue(codeCall.params[0], scope, context).value;
    if (pathV.type !== "text") throw new Error("segment() expects a text path.");
    const nV = executeValue(codeCall.params[1], scope, context).value;
    if (nV.type !== "int") throw new Error("segment() expects an int index.");
    const n = nV.value;
    const parts = pathV.value.split("/");
    return { type: "text", value: n >= 0 && n < parts.length ? parts[n] : "" };
}

// toInt(text): parse `text` to an int; 0 on empty or non-numeric. Strict (an optional leading
// "-" then digits) so this twins the C# int.TryParse exactly — "5x" is non-numeric → 0 on both
// (parseInt would leniently yield 5 and drift from the server).
function execToInt(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const v = executeValue(codeCall.params[0], scope, context).value;
    if (v.type !== "text") throw new Error("toInt() expects a text value.");
    return { type: "int", value: /^-?\d+$/.test(v.value) ? Number(v.value) : 0 };
}

// id(obj): an object's intrinsic int identity — the read companion to nest (which stringifies
// obj.id for a link). Pure: records no prop dependency and never writes back (unlike field).
function execId(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const obj = executeValue(codeCall.params[0], scope, context).value;
    if (obj.type !== "object") throw new Error("id() expects an object.");
    return { type: "int", value: obj.id };
}

// new(desc): a FRESH object of a type, built REFLECTIVELY from its descriptor — built to the SAME
// COMPLETE shape DbBridge.LoadObject gives a stored object, so a freshly-minted member is never missing
// a key (twin of CodeExecutor.DefaultExec / DefaultProp). EVERY declared prop is
// initialized: a scalar → its baseType default; a single object (reference) → null; a set → an empty
// Set array; a dictionary → an empty Dict array (the element type carried so a later add persists the
// right member type). The constructor for the self-hosted UI's drafts: a create-new form's blank state
// (the SetTable does `set.add(sys.new(desc))`, so the draft becomes a real member and MUST be complete —
// else the generic table's reference/set columns read an absent prop and throw, freezing the form), and
// the seed of ObjectForm's edit draft. A fresh
// object every call (no aliasing). Privacy-trivial: reads NO source object — only the already-shipped
// descriptor — and emits constant defaults/empties, so the client's setup re-run mints the same shape.
// Two descriptor shapes (the two the UI passes): a TYPE descriptor → one field per `props` entry (every
// cardinality); a dictionary PROP descriptor → a `value` for a scalar dict (defaulted by `element`) or
// one field per `valueProps` entry (an entry is a scalar-only object, so valueProps are all scalar).
function defaultValue(baseType: string): ExecValue {
    if (baseType === "bool") return { type: "bool", value: false };
    if (baseType === "int") return { type: "int", value: 0 };
    return { type: "text", value: "" };   // text/date/datetime/decimal/enum are text-shaped, default ""
}
function descProps(desc: ExecObject, field: string): ExecObject[] {
    const arr = desc.props[field];
    return arr != null && arr.type === "array"
        ? arr.items.map(i => i.value).filter((v): v is ExecObject => v.type === "object")
        : [];
}
function propName(p: ExecObject): string { const n = p.props["name"]; return n != null && n.type === "text" ? n.value : ""; }
function propBaseType(p: ExecObject): string { const b = p.props["baseType"]; return b != null && b.type === "text" ? b.value : ""; }
function propElement(p: ExecObject): string { const e = p.props["element"]; return e != null && e.type === "text" ? e.value : ""; }
// A prop's COMPLETE default value — mirrors DbBridge.LoadObject's per-cardinality shape so sys.new and a
// stored load agree: scalar → default leaf; object → null (an unset reference); set/dict → an empty
// array of the right kind, carrying its element type (id 0 = a draft-local empty collection, exactly as
// the store yields for an absent set/dict — add/remove only sends to the server once id > 0).
function defaultProp(p: ExecObject): ExecValue {
    const bt = propBaseType(p);
    if (bt === "object") return { type: "null" };
    if (bt === "set") return { type: "array", kind: "set", items: [], id: 0, elementTypeName: propElement(p) };
    if (bt === "dictionary") return { type: "array", kind: "dict", items: [], id: 0, elementTypeName: propElement(p) };
    return defaultValue(bt);
}
function execNew(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    if (codeCall.params.length !== 1) throw new Error("new(desc) takes one argument.");
    const desc = executeValue(codeCall.params[0], scope, context).value;
    if (desc.type !== "object") throw new Error("new() expects a descriptor object.");
    const props: { [name: string]: ExecValue } = {};
    const bt = desc.props["baseType"];
    if (bt != null && bt.type === "text" && bt.value === "dictionary") {
        const isScalar = desc.props["isScalar"];
        const element = desc.props["element"];
        if (isScalar != null && isScalar.type === "bool" && isScalar.value)
            props["value"] = defaultValue(element != null && element.type === "text" ? element.value : "");
        else
            for (const vp of descProps(desc, "valueProps")) props[propName(vp)] = defaultValue(propBaseType(vp));
    } else {
        for (const p of descProps(desc, "props")) props[propName(p)] = defaultProp(p);
    }
    const obj: ExecObject = { type: "object", props, id: --context.lastId.value };
    draftObjects.add(obj); // a sys.new draft is a bindable identity a component pins to — track its swaps (objectArgKey)
    return obj;
}

// resolve(pathText): the URL→view-kind dispatch (the client twin of C# CodeExecutor.ExecuteResolve;
// it replaced the deleted SsrRenderer.ResolveView). Resolves a URL to its view-KIND plus the bound
// object(s):
//   { kind, target, parent, prop, typeName, parentType }
// kind ∈ object | set | ref | dict | leaf | notFound. The CLIENT has no schema/store (unlike the
// server, which reuses the TypeResolver), so it ports the SAME cardinality-walk over the SHIPPED
// `sys.schema` descriptors + its own `db` graph: at each segment the descriptor decides cardinality
// (a prop's baseType: object | set | dictionary | scalar) and the graph binds the object. Both twins
// MUST produce the identical result — SSR (server) and hydrate (client) resolve the SAME URL, so a
// divergence would flash/reroute the page; proven by the SelfHostedUi resolve-probe scenarios.
//
// A descriptor read mirrors execSchema: a cache miss throws "Value not available" (→ refetch), but
// the server prewarms EVERY descriptor, so the walk finds each type/prop shape it needs.
function resolveDescriptor(lookup: string, context: ExecContext): ExecObject {
    const d = memoize("schema:" + lookup, context, () => { throw new Error("Value not available"); });
    if (d.type !== "object") throw new Error("Value not available");
    return d;
}

// The prop descriptors of a TYPE descriptor (its `props` array), by name.
function typeProp(typeDesc: ExecObject, name: string): ExecObject | null {
    return descProps(typeDesc, "props").find(p => propName(p) === name) ?? null;
}
function propText(p: ExecObject, key: string): string {
    const v = p.props[key];
    return v != null && v.type === "text" ? v.value : "";
}
function propBool(p: ExecObject, key: string): boolean {
    const v = p.props[key];
    return v != null && v.type === "bool" && v.value;
}

// Bind a URL segment within the db graph (twin of the C# FindTarget step): a set member by its
// identity id, a dict entry by its __key, a field by name. Records the read so a re-render's deps
// match the server. Returns null when the node is missing.
function bindSegment(current: ExecValue, segment: string): ExecValue | null {
    if (current.type === "array" && current.kind === "dict") {
        recordMember(current.id);
        const item = current.items.find(i =>
            i.value.type === "object" && i.value.props["__key"]?.type === "text" && i.value.props["__key"].value === segment);
        return item ? item.value : null;
    }
    if (current.type === "array" && /^-?\d+$/.test(segment)) {
        recordMember(current.id);
        const item = current.items.find(i => i.key === Number(segment));
        return item ? item.value : null;
    }
    if (current.type === "object" && current.props[segment] != null) {
        recordProp(current.id, segment);
        return current.props[segment];
    }
    return null;
}

function resolveResult(context: ExecContext, kind: string,
    opts: { target?: ExecValue; parent?: ExecValue; prop?: string; typeName?: string; parentType?: string } = {}): ExecObject {
    return {
        type: "object", id: --context.lastId.value,
        props: {
            kind: { type: "text", value: kind },
            target: opts.target ?? { type: "null" },
            parent: opts.parent ?? { type: "null" },
            prop: { type: "text", value: opts.prop ?? "" },
            typeName: { type: "text", value: opts.typeName ?? "" },
            parentType: { type: "text", value: opts.parentType ?? "" },
        },
    };
}

function execResolve(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    if (codeCall.params.length !== 1) throw new Error("resolve(path) takes one argument.");
    const pathV = executeValue(codeCall.params[0], scope, context).value;
    if (pathV.type !== "text") throw new Error("resolve() expects a text path.");
    const db = findScope({ type: "symbol", name: "db" }, scope).items["db"].value;
    if (db.type !== "object") throw new Error("resolve() requires a db root object.");
    const segments = pathV.value.split("/").filter(s => s.length > 0);

    // One combined pass — the type-walk (over descriptors) AND the graph-bind (over db) in lockstep,
    // tracking the owning object/prop for an owner-bound route and whether any step entered a dict
    // (the scalar-dict-entry → leaf distinction). The root type is the well-known "Db".
    let typeName = "Db";              // current declared type name
    let isObject = true;              // current type is an object (vs a scalar leaf)
    let cardinality = "single";       // single | set | dict
    let isReference = false;          // the last field was a single object reference
    let traversedDict = false;
    let current: ExecValue = db;      // the currently-bound graph node
    let ownerType = "";               // the type owning the last field (for an owner-bound route)
    let prop = "";                    // the last field name
    let bound = true;                 // the graph walk is still binding (a deleted member → notFound)

    for (const segment of segments) {
        if (cardinality === "set" || cardinality === "dict") {
            // The segment addresses a MEMBER: descend into the element. A set element is always an
            // object; a dict element may be scalar (its descriptor's isScalar) — then the member is a leaf.
            if (cardinality === "dict") traversedDict = true;
            // element type was recorded when we entered the collection (typeName/isObject already set).
            cardinality = "single";
            isReference = false;
            prop = "";
        } else if (isObject) {
            const typeDesc = resolveDescriptor(typeName, context);
            const pd = typeProp(typeDesc, segment);
            if (pd == null) return resolveResult(context, "notFound");
            const baseType = propBaseType(pd);
            ownerType = typeName;
            prop = segment;
            if (baseType === "set") {
                cardinality = "set"; isReference = false;
                typeName = propText(pd, "element"); isObject = true; // set elements are objects
            } else if (baseType === "dictionary") {
                cardinality = "dict"; isReference = false;
                // The dict's element kind is known now (isScalar); the type for an object element.
                if (propBool(pd, "isScalar")) { isObject = false; typeName = propText(pd, "element"); }
                else { isObject = true; typeName = propText(pd, "element"); }
            } else if (baseType === "object") {
                cardinality = "single"; isReference = true;
                typeName = propText(pd, "target"); isObject = true;
            } else {
                cardinality = "single"; isReference = false;
                typeName = baseType; isObject = false; // a scalar field
            }
        } else {
            return resolveResult(context, "notFound"); // can't navigate into a leaf
        }

        // Bind the segment in the graph (parallel to the type-walk). A missing node → notFound below.
        if (bound) {
            const next = bindSegment(current, segment);
            if (next == null) bound = false; else current = next;
        }
    }

    // Dispatch — the faithful port of SsrRenderer.ResolveView.
    if (cardinality === "set" && isObject)
        return ownerBound(context, db, segments, "set", { typeName });
    if (cardinality === "dict")
        return ownerBound(context, db, segments, "dict", { parentType: ownerType });
    if (cardinality === "single" && !isObject && traversedDict)
        return resolveResult(context, "leaf", { target: bound && current.type === "object" ? current : undefined });
    if (!(cardinality === "single" && isObject))
        return resolveResult(context, "notFound");
    if (isReference)
        return ownerBound(context, db, segments, "ref", { typeName });
    return resolveResult(context, "object",
        { target: bound && current.type === "object" ? current : undefined, typeName });
}

// An owner-bound route (set / ref / dict): the parent is the object owning the final prop (the path
// minus its last segment); the prop is that last segment. Re-binds the parent from db (a small,
// cheap walk that records the same leaves the server's parent FindTarget does).
function ownerBound(context: ExecContext, db: ExecObject, segments: string[], kind: string,
    opts: { typeName?: string; parentType?: string }): ExecObject {
    const prop = segments[segments.length - 1];
    let parent: ExecValue = db;
    let bound = true;
    for (let i = 0; i < segments.length - 1; i++) {
        const next = bindSegment(parent, segments[i]);
        if (next == null) { bound = false; break; }
        parent = next;
    }
    return resolveResult(context, kind, {
        parent: bound && parent.type === "object" ? parent : undefined,
        prop, typeName: opts.typeName, parentType: opts.parentType,
    });
}

// extent(typeName): the reference picker's candidates — all objects of a type. Memoized
// like where/orderBy; the server shipped the displayed list and the client reuses it. No
// store on the client, so a cache miss/stale throws "Value not available", which the
// memoize wrapper turns into a refetch (the same hidden-dependency path).
//
// A true MISS (the extent was never shipped — e.g. navigating to a ref/object page whose start view
// shipped no candidate list) makes memoize SWALLOW its VNA and return an empty `nothing` (not a throw)
// — and `nothing` is NOT an empty array, so a consumer that iterates it (`foreach c in sys.extent(...)`)
// or reads a member would otherwise throw a misleading NON-VNA error. Re-throw the VNA so the miss
// surfaces as exactly that — a "Value not available" the nearest memoize boundary swallows cleanly and
// that triggers the refetch — instead of a `nothing` leaking into a value position. A legitimately EMPTY
// extent ships as an empty ARRAY (kind set, zero items), never `nothing`, so this only fires on a miss.
function execExtent(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const v = executeValue(codeCall.params[0], scope, context).value;
    if (v.type !== "text") throw new Error("extent() expects a text type name.");
    const r = memoize("extent:" + v.value, context, () => { throw new Error("Value not available"); });
    if (r.type === "nothing") throw new Error("Value not available"); // a miss (never shipped) — not an empty extent
    return r;
}

// schema(typeName): a type's descriptor — { name, labelProp, props } — the reflective shape
// the self-hosted generic UI walks. The twin of execExtent: the descriptor is SERVER-RESOLVED
// (computed from the schema, which never crosses the wire), shipped as a cached value, and the
// client reuses it. No store/schema on the client, so a cache miss throws "Value not available",
// which the memoize wrapper turns into a refetch (the same server-resolved-dependency path).
function execSchema(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const v = executeValue(codeCall.params[0], scope, context).value;
    if (v.type !== "text") throw new Error("schema() expects a text type name.");
    // Two-arg form: a specific PROP's descriptor (a dict prop at its root route), keyed "Type/prop".
    let lookup = v.value;
    if (codeCall.params.length === 2) {
        const p = executeValue(codeCall.params[1], scope, context).value;
        if (p.type !== "text") throw new Error("schema() expects a text prop name.");
        lookup += "/" + p.value;
    }
    // As in execExtent: a MISS makes memoize return an empty `nothing`. A descriptor is always read as an
    // object (`.props`/`.labelProp`/`.values`), so a leaked `nothing` would throw a misleading non-VNA
    // "Cannot read on a non-object." Re-throw the VNA so a not-yet-shipped descriptor surfaces cleanly
    // (swallowed at the nearest memoize boundary → refetch), never as a `nothing` in a value position.
    const r = memoize("schema:" + lookup, context, () => { throw new Error("Value not available"); });
    if (r.type === "nothing") throw new Error("Value not available"); // a miss (never shipped)
    return r;
}

// canWrite(typeName, verb): the bound principal's WRITE capability for a type + verb — server-resolved
// (the floor never crosses the wire) and shipped as a cached bool, exactly like extent/schema. A cache
// miss throws "Value not available" → refetch. The self-hosted UI reads it to hide write affordances
// (Save/New/Remove) a read-only principal cannot use (the floor still gates every real write).
function execCanWrite(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const t = executeValue(codeCall.params[0], scope, context).value;
    if (t.type !== "text") throw new Error("canWrite() expects a text type name.");
    const v = executeValue(codeCall.params[1], scope, context).value;
    if (v.type !== "text") throw new Error("canWrite() expects a text verb.");
    const r = memoize("canWrite:" + t.value + ":" + v.value, context, () => { throw new Error("Value not available"); });
    if (r.type === "nothing") throw new Error("Value not available"); // a miss (never shipped)
    return r;
}

// canRead(typeName): may the principal read ANY member of the type — server-resolved (the floor never
// crosses the wire) and shipped as a cached bool, like canWrite. The self-hosted UI reads it to hide a
// collection/route whose element type the principal cannot read. A miss → "Value not available" → refetch.
function execCanRead(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const t = executeValue(codeCall.params[0], scope, context).value;
    if (t.type !== "text") throw new Error("canRead() expects a text type name.");
    const r = memoize("canRead:" + t.value, context, () => { throw new Error("Value not available"); });
    if (r.type === "nothing") throw new Error("Value not available"); // a miss (never shipped)
    return r;
}

// diffCommits(from, to): the rename-aware structural diff between two design commits (M13 Track-B B2) —
// the twin of execSchema/execCanRead. The server COMPUTES the diff (DesignDiffer, in the Designer layer)
// and ships it via the memo cache; the client never computes — it only REUSES the shipped result under the
// same key, keyed by the two commits' intrinsic ids (from.id:to.id) so both twins address the same entry.
// A cache miss throws "Value not available", which the memoize wrapper turns into a refetch (the same
// server-resolved-dependency path). No store/DesignDiffer on the client — there is nothing to compute here.
function execDiffCommits(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const from = executeValue(codeCall.params[0], scope, context).value;
    if (from.type !== "object") throw new Error("diffCommits() expects a commit object as its first argument.");
    const to = executeValue(codeCall.params[1], scope, context).value;
    if (to.type !== "object") throw new Error("diffCommits() expects a commit object as its second argument.");
    // As in execSchema: a MISS makes memoize return an empty `nothing`; the report is always read as an
    // object (`.isEmpty`/`.renames`/…), so re-throw the VNA rather than let a `nothing` leak into a value
    // position (which would throw a misleading non-VNA error). The nearest memoize boundary swallows it → refetch.
    const r = memoize("diffCommits:" + from.id + ":" + to.id, context, () => { throw new Error("Value not available"); });
    if (r.type === "nothing") throw new Error("Value not available"); // a miss (never shipped)
    return r;
}

// publishPreview(design, targetId): the dry-run PublishReport a publish onto that target would produce
// (M13 Track-B B3) — the twin of execDiffCommits/execSchema. The server COMPUTES the report (the
// kernel-wired preview delegate, reaching the target's data file cross-instance) and ships it via the memo
// cache; the client never computes — it only REUSES the shipped result under the same key, keyed by the
// design's + target's ids (design.id:targetId) so both twins address the same entry. A cache miss throws
// "Value not available", which the memoize wrapper turns into a refetch (the same server-resolved-dependency
// path). No store/kernel on the client — there is nothing to compute here.
function execPublishPreview(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const design = executeValue(codeCall.params[0], scope, context).value;
    if (design.type !== "object") throw new Error("publishPreview() expects a design object as its first argument.");
    const targetId = executeValue(codeCall.params[1], scope, context).value;
    if (targetId.type !== "int") throw new Error("publishPreview() expects an integer target id as its second argument.");
    // As in execDiffCommits: a MISS makes memoize return an empty `nothing`; the report is always read as an
    // object (`.isEmpty`/`.removes`/…), so re-throw the VNA rather than let a `nothing` leak into a value
    // position. The nearest memoize boundary swallows it → refetch.
    const r = memoize("publishPreview:" + design.id + ":" + targetId.value, context, () => { throw new Error("Value not available"); });
    if (r.type === "nothing") throw new Error("Value not available"); // a miss (never shipped)
    return r;
}

// mergePreview(source, target): the MergeReport a mergeBranch(source, target) would produce — conflicts (each
// with base/source/target) + the always-surfaced access changes + any drift/no-op signal (M13 Track-B B4) —
// the twin of execPublishPreview/execDiffCommits. The server COMPUTES the report (the SELF-BUILT preview
// delegate in SsrRenderer, over the designer's own store — both branches are Design rows there) and ships it
// via the memo cache; the client never computes — it only REUSES the shipped result under the same key, keyed
// by the two designs' ids (source.id:target.id) so both twins address the same entry. A cache miss throws
// "Value not available", which the memoize wrapper turns into a refetch. No store/DesignMerger on the client.
function execMergePreview(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const source = executeValue(codeCall.params[0], scope, context).value;
    if (source.type !== "object") throw new Error("mergePreview() expects a design object as its first argument.");
    const target = executeValue(codeCall.params[1], scope, context).value;
    if (target.type !== "object") throw new Error("mergePreview() expects a design object as its second argument.");
    // As in execPublishPreview: a MISS makes memoize return an empty `nothing`; the report is always read as an
    // object (`.merged`/`.conflicts`/…), so re-throw the VNA rather than let a `nothing` leak into a value
    // position. The nearest memoize boundary swallows it → refetch.
    const r = memoize("mergePreview:" + source.id + ":" + target.id, context, () => { throw new Error("Value not available"); });
    if (r.type === "nothing") throw new Error("Value not available"); // a miss (never shipped)
    return r;
}

// renderTree(node[, ctx]): the CLIENT-COMPUTABLE canvas (M12 S4 foundation) — the twin of
// CodeExecutor.ExecuteRenderTree. Turns a MetaNode row (tag/expr/order scalars + attrs/children sets, the
// S1a structured-render schema) into a live tag tree built from the rows. UNLIKE the server-backed reads
// (execPublishPreview et al., revived from shipped data), this is computed by BOTH twins from row data the
// client already holds — no memoize, no refetch. Every node-field / set read goes through the SAME
// dep-recording paths ordinary reads use (recordProp, recordMember), so an ordinary tree-editor edit
// (rename a tag, edit an attr, add/remove a node) invalidates the enclosing render and the canvas
// re-renders in the same interaction with no round-trip. The optional SECOND arg is reserved for the
// eval-context slice (ignored today) so the signature is extensible without reshaping.
function execRenderTree(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    if (codeCall.params.length !== 1 && codeCall.params.length !== 2)
        throw new Error("renderTree(node[, ctx]) takes one or two arguments.");
    const node = executeValue(codeCall.params[0], scope, context).value;
    if (node.type !== "object") throw new Error("renderTree() expects a node object as its first argument.");
    return renderTreeNode(node, context);
}

// Walk one MetaNode row → its rendered node. ELEMENT (tag non-empty): a `tag` element carrying
// data-node=<id> (the provenance spine for click-to-select — on EVERY emitted element), literal attrs
// (ordered by `order`; non-literal + event `on*` attrs skipped — the canvas is display-inert), and its
// recursively-rendered children (ordered by `order`). LEAF (tag empty, expr non-empty): a literal expr →
// its unquoted value as a text child; otherwise an EXPRESSION CHIP (span.expr-chip) holding the raw source.
// INVALID (tag AND expr empty): span.expr-chip.is-empty "(empty)" — a visible marker, never silent nothing.
function renderTreeNode(node: ExecObject, context: ExecContext): ExecValue {
    const id = node.id;
    const tag = readNodeText(node, "tag", context);
    const expr = readNodeText(node, "expr", context);
    if (tag.length > 0) {
        const attributes: { [name: string]: ExecResult } = { "data-node": { value: { type: "text", value: String(id) } } };
        for (const attr of orderedMembers(node, "attrs", context)) {
            const name = readNodeText(attr, "name", context);
            const value = readNodeText(attr, "value", context);
            // Event attrs are always inert; "data-node" is the RESERVED provenance attr this walk itself
            // stamps (above) — a user attr of that name is skipped so it can never clobber the id S4's
            // click-to-select depends on.
            if (name.length === 0 || isEventAttr(name) || name === "data-node") continue;
            const lit = literalValue(value);
            if (lit != null) attributes[name] = { value: lit };        // non-literal → skipped
        }
        const children: ExecTagChild[] = orderedMembers(node, "children", context).map(c => renderTreeNode(c, context));
        return { type: "tag", name: tag, attributes, children };
    }
    if (expr.length > 0)
        return isLiteral(expr) ? { type: "text", value: literalDisplay(expr) } : chip("expr-chip", expr, id);
    return chip("expr-chip is-empty", "(empty)", id);
}

// A span.expr-chip (or its .is-empty variant) with the node's provenance id and one text child.
function chip(cls: string, text: string, id: number): ExecTag {
    return {
        type: "tag", name: "span",
        attributes: { "class": { value: { type: "text", value: cls } }, "data-node": { value: { type: "text", value: String(id) } } },
        children: [{ type: "text", value: text }],
    };
}

// Read a MetaNode/MetaAttr field through the ordinary dep-recording prop path (recordProp), so an edit to it
// stales the canvas. A staging overlay wins (a ctx draft edit reflects live). An absent prop throws "Value
// not available" — the standard refetch path (the server ships every field renderTree read, so a real render
// never misses); the twin of the objectProp read.
function readNodeProp(node: ExecObject, name: string, context: ExecContext): ExecValue {
    const staged = nearestStagedValue(node, name, context);
    if (staged != null) { recordProp(node.id, name); return staged; }
    const value = node.props[name];
    if (value == null) throw new Error("Value not available");
    recordProp(node.id, name);
    return value;
}

function readNodeText(node: ExecObject, name: string, context: ExecContext): string {
    const v = readNodeProp(node, name, context);
    return v.type === "text" ? v.value : "";
}

// The members of a node's attrs/children SET, ordered by each member's `order` — observed through the same
// reads a `node.children.orderBy(order)` foreach makes (a prop dep on the set, a membership dep, an order
// read per member), so an add/remove/reorder re-renders the canvas. Non-object / absent → empty.
function orderedMembers(node: ExecObject, setProp: string, context: ExecContext): ExecObject[] {
    const setV = readNodeProp(node, setProp, context);
    if (setV.type !== "array") return [];
    recordMember(setV.id);
    const objs: { o: ExecObject; order: number }[] = [];
    for (const item of setV.items)
        if (item.value.type === "object") {
            const ord = readNodeProp(item.value, "order", context);
            objs.push({ o: item.value, order: ord.type === "int" ? ord.value : 0 });
        }
    return objs.sort((a, b) => a.order - b.order).map(p => p.o);
}

// ── render-tree literal rules (twin-identical with CodeExecutor.cs; pinned by the conformance case) ──────
// A leaf/attr value source is a LITERAL when it is ONE complete quoted string, an int, or a bool. Anything
// else (`a + b`, a bare symbol, `"a" + b`) is non-literal → a chip (leaf) or a skip (attr). A manual
// char-scan (not a RegExp) so both interpreters agree byte-for-byte.
function isLiteral(s: string): boolean { return isStringLiteral(s) || isIntLiteral(s) || isBoolLiteral(s); }

// The DISPLAY text of a literal LEAF: a string literal's unescaped content; an int/bool's raw source.
function literalDisplay(s: string): string { return isStringLiteral(s) ? unquoteString(s) : s; }

// The typed VALUE of a literal ATTR — string → text, int → int, bool → bool; null for a non-literal (skipped).
// Review fix 1: an int-SHAPED source outside Int32 range (deenv's `int` is 32-bit) is treated as NON-LITERAL
// here — an explicit range check (JS numbers don't overflow like C#'s int.Parse, so classification must be
// asserted explicitly) so this twin agrees with CodeExecutor.cs's TryParse guard. The LEAF path
// (literalDisplay) never parses — it shows the raw digits verbatim regardless of magnitude — so only the
// ATTR path needed this guard.
function literalValue(s: string): ExecValue | null {
    if (isStringLiteral(s)) return { type: "text", value: unquoteString(s) };
    if (isIntLiteral(s)) {
        const n = parseInt(s, 10);
        return (n >= -2147483648 && n <= 2147483647) ? { type: "int", value: n } : null;
    }
    if (isBoolLiteral(s)) return { type: "bool", value: s === "true" };
    return null;
}

function isEventAttr(name: string): boolean {
    return name.length >= 2 && (name[0] === "o" || name[0] === "O") && (name[1] === "n" || name[1] === "N");
}

function isBoolLiteral(s: string): boolean { return s === "true" || s === "false"; }

function isIntLiteral(s: string): boolean {
    if (s.length === 0) return false;
    let i = 0;
    if (s[0] === "-") { if (s.length === 1) return false; i = 1; }
    for (; i < s.length; i++) { const c = s.charCodeAt(i); if (c < 48 || c > 57) return false; }
    return true;
}

// Matches ^"([^"\\]|\\.)*"$ — a single complete quoted string: an unescaped `"` may appear ONLY as the final
// char, and a `\` escapes the next char. So `"a" + b` (a quote inside) is NOT a literal.
function isStringLiteral(s: string): boolean {
    if (s.length < 2 || s[0] !== '"') return false;
    let i = 1;
    while (i < s.length) {
        const c = s[i];
        if (c === '"') return i === s.length - 1;                     // a bare quote is valid only as the close
        if (c === "\\") { if (i + 1 >= s.length) return false; i += 2; continue; }  // no trailing backslash
        i++;
    }
    return false;                                                     // never closed
}

// Strip the outer quotes of a confirmed string literal and unescape \" and \\ (other \x kept verbatim).
function unquoteString(s: string): string {
    const inner = s.substring(1, s.length - 1);
    let out = "";
    let i = 0;
    while (i < inner.length) {
        if (inner[i] === "\\" && i + 1 < inner.length && (inner[i + 1] === '"' || inner[i + 1] === "\\")) {
            out += inner[i + 1]; i += 2; continue;
        }
        out += inner[i]; i++;
    }
    return out;
}

// setRef(obj, prop, value): set/clear an object REFERENCE prop and persist it. value is an
// existing candidate (id>0 → refId), a fresh draft (id<0 → its scalar props), or null
// (clear). Stages in memory (UI reflects it), then sends the id-addressed WS op.
function execSetRef(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const obj = executeValue(codeCall.params[0], scope, context).value;
    const propV = executeValue(codeCall.params[1], scope, context).value;
    const value = executeValue(codeCall.params[2], scope, context).value;
    if (obj.type !== "object") throw new Error("setRef() expects an object.");
    if (propV.type !== "text") throw new Error("setRef() expects a text prop name.");
    const prop = propV.value;
    const before = obj.props[prop];
    obj.props[prop] = value;                  // optimistic — local, unchanged whether the persist stages or fires
    invalidateProp(obj.id, prop);
    // Staging branch (atomic-commit Step B): pointing a reference at a TRANSIENT (id<0) draft under a staging
    // ctx STAGES the create + its link, so the whole changeset persists all-or-none on the outermost commit
    // (mirrors addToCollection). An EXISTING (id>0) pick / a clear stays LIVE — the same id discriminator.
    if (value.type === "object" && value.id < 0) {
        const staging = nearestStagingCtx(context);
        if (staging != null) { staging.creates.push({ draft: value, join: { kind: "ref", parent: obj, prop } }); return { type: "nothing" }; }
    }
    if (obj.id > 0) referenceChange(obj, prop, value, before);
    return { type: "nothing" };
}

// The schema object crosses the wire as its ID — the server reads the object's subtree from the
// caller's store and projects it (no object-graph serialization). The designer passes `db`, the
// root object (id 1). A non-object schema has no id → id 0, which the server rejects.
function schemaIdArg(schema: ExecValue): ExecValue {
    return { type: "int", value: schema.type === "object" ? schema.id : 0 };
}

// Host-action success callback (docs/plans/host-action-success-signal.md) — every kernel host-action
// builtin accepts ONE optional TRAILING fn arg, invoked when the action's ok reply arrives (never on
// error). The universal rule: a CodeFunction in LAST position = the callback, everywhere. Shared here
// so the nine call sites (execPublish/execCreate/execRename/execCloneInstance/execDelete/
// execSetDesign/execCommitDesign/execCreateBranch/execMergeBranch) don't each hand-roll the
// detect-and-split. `params` is the call's REMAINING (unevaluated) param nodes after the builtin's own
// fixed leading args have already been consumed by the caller. Evaluates ALL params LEFT-TO-RIGHT
// (review fix: an earlier version evaluated the last param FIRST, making trailing-arg evaluation
// right-to-left and risking a double-evaluation if a caller's expr had side effects) into one array,
// THEN checks whether the last evaluated value is a fn — if so it is popped off as `callback` instead
// of shipping in `rest`, so it never reaches sendHostAction's wire args (a fn silently scalarOf's to
// null otherwise, shipping a bogus arg).
function splitTrailingCallback(
    params: CodeValue[], scope: ExecScope, context: ExecContext
): { rest: ExecValue[]; callback?: ExecFunction } {
    const evaluated = params.map(p => executeValue(p, scope, context).value);
    if (evaluated.length > 0 && evaluated[evaluated.length - 1].type === "fn")
        return { rest: evaluated.slice(0, -1), callback: evaluated[evaluated.length - 1] as ExecFunction };
    return { rest: evaluated };
}

// sys.publish(schema, targetId, expectedHeadCommit?, expectedTargetVersion?, callback?): a SERVER-ONLY
// host action — the M4 schema export runs server-side, projecting the passed SCHEMA object onto an
// EXISTING target instance. The client stages NOTHING in the data model (no obj.props mutation, no
// invalidateProp — mirrors execSetRef minus the local mutation); it only fires the hostAction
// send-hook (schema as its id + the target id [+ the optional guard token] [+ the optional trailing
// callback]), which ws.ts sends as the `hostAction` WS op. The server is authoritative: it alone runs
// the effect, and an error reply surfaces as a user-visible lastError. Returns nothing; SSR/refetch
// no-ops it.
//
// The guard pair (M13 Track-B B3 addendum — the preview→apply consistency guard) is OPTIONAL and
// BOTH-OR-NEITHER: the design editor's Apply button passes back the exact token
// `sys.publishPreview` handed it (`report.targetCommit`, `report.targetVersion`), so the server can
// reject a stale apply (the design or target moved since the operator's approved preview) rather than
// silently applying a DIFFERENT plan than the one shown. Every other 2-arg call site (existing tests,
// any future unguarded caller) is unaffected — sent as-is with no trailing args. The success callback
// (docs/plans/host-action-success-signal.md) is a further OPTIONAL trailing fn: 3 args = 2 + callback
// (arg 2 MUST be a fn — a non-fn 3rd arg stays invalid, preserving the guard pair's both-or-neither
// shape), 5 args = the guarded 4 + callback (arg 3, the guard token, is never a fn).
function execPublish(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const schema = executeValue(codeCall.params[0], scope, context).value;
    const targetId = executeValue(codeCall.params[1], scope, context).value;
    const args = [schemaIdArg(schema), targetId];
    const { rest, callback } = splitTrailingCallback(codeCall.params.slice(2), scope, context);
    args.push(...rest);
    sendHostAction("publish", args, callback);
    return { type: "nothing" };
}

// sys.create(schema, name, callback?): a SERVER-ONLY host action — project the passed SCHEMA object
// into a NEW kernel instance with the given display NAME (the sibling of publish: publish replaces an
// existing instance, create spawns a new one). NO ports: addressing is by PATH now, so the new
// instance is served at `/apps/<name>` (the kernel derives the mount from the name). Like execPublish
// it stages NOTHING and only fires the hostAction send-hook. Returns nothing; SSR/refetch no-ops it.
function execCreate(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const schema = executeValue(codeCall.params[0], scope, context).value;
    const { rest, callback } = splitTrailingCallback(codeCall.params.slice(1), scope, context);
    sendHostAction("create", [schemaIdArg(schema), ...rest], callback);
    return { type: "nothing" };
}

// sys.rename(id, name, callback?): a SERVER-ONLY host action — update the display label of an existing
// kernel instance. The id is a bare int (NOT a schema object). Stages NOTHING; only fires the
// hostAction send-hook. Returns nothing; SSR/refetch no-ops it.
function execRename(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const id = executeValue(codeCall.params[0], scope, context).value;
    const { rest, callback } = splitTrailingCallback(codeCall.params.slice(1), scope, context);
    sendHostAction("rename", [id, ...rest], callback);
    return { type: "nothing" };
}

// sys.cloneInstance(sourceId, callback?): a SERVER-ONLY host action — copy an existing instance's app
// document AND data into a NEW instance (the data-carrying sibling of create: create projects a fresh
// design, clone copies a live one). The source is named by its instance id (a bare int, NOT a schema
// object — no schemaIdArg). NO ports: the clone is served at a unique `/apps/<name>` the kernel derives
// from the source's name. Stages NOTHING; only fires the hostAction send-hook. Returns nothing;
// SSR/refetch no-ops it.
function execCloneInstance(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const sourceId = executeValue(codeCall.params[0], scope, context).value;
    const { rest, callback } = splitTrailingCallback(codeCall.params.slice(1), scope, context);
    sendHostAction("cloneInstance", [sourceId, ...rest], callback);
    return { type: "nothing" };
}

// sys.delete(targetId, callback?): a SERVER-ONLY host action — remove an existing kernel instance,
// named by its instance id (a bare int, NOT a schema object). Stages NOTHING; only fires the
// hostAction send-hook (the target id). Returns nothing; SSR/refetch no-ops it.
function execDelete(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const targetId = executeValue(codeCall.params[0], scope, context).value;
    const { rest, callback } = splitTrailingCallback(codeCall.params.slice(1), scope, context);
    sendHostAction("delete", [targetId, ...rest], callback);
    return { type: "nothing" };
}

// sys.setDesign(schema, targetId, callback?): a SERVER-ONLY host action — the IDE's "Apply". Record (on
// the target's registry entry) that it now runs the passed design AND deploy it (publish + the registry
// write that makes the reference explicit). Like execPublish it stages NOTHING and only fires the
// hostAction send-hook (the design as its id + the target id). Returns nothing; SSR/refetch no-ops it.
function execSetDesign(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const schema = executeValue(codeCall.params[0], scope, context).value;
    const targetId = executeValue(codeCall.params[1], scope, context).value;
    const { rest, callback } = splitTrailingCallback(codeCall.params.slice(2), scope, context);
    sendHostAction("setDesign", [schemaIdArg(schema), targetId, ...rest], callback);
    return { type: "nothing" };
}

// sys.commitDesign(design, message, migration, callback?): a SERVER-ONLY host action — snapshot the
// design's CURRENT working copy into an immutable Commit chained onto its owning branch (M13 slice 3).
// The design crosses the wire as its id (schemaIdArg, like publish/setDesign); the message is a plain
// text arg (NOT wrapped — it is not a schema object). Migration is plain text stored on the Commit for
// publish-time execution. Stages NOTHING in the data model; only fires the hostAction send-hook.
// Returns nothing; SSR/refetch no-ops it (CodeExecutor's `commitDesign` host-action case). The optional
// trailing callback (docs/plans/host-action-success-signal.md) is the commit bar's clear-on-success —
// its first consumer.
function execCommitDesign(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const design = executeValue(codeCall.params[0], scope, context).value;
    const message = executeValue(codeCall.params[1], scope, context).value;
    const migration = executeValue(codeCall.params[2], scope, context).value;
    const { rest, callback } = splitTrailingCallback(codeCall.params.slice(3), scope, context);
    sendHostAction("commitDesign", [schemaIdArg(design), message, migration, ...rest], callback);
    return { type: "nothing" };
}

function execRevertCommit(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const design = executeValue(codeCall.params[0], scope, context).value;
    const commit = executeValue(codeCall.params[1], scope, context).value;
    const { rest, callback } = splitTrailingCallback(codeCall.params.slice(2), scope, context);
    sendHostAction("revertCommit", [schemaIdArg(design), schemaIdArg(commit), ...rest], callback);
    return { type: "nothing" };
}

// sys.importRender(design, callback?): a SERVER-ONLY host action (M12 X2a) — convert the design's text
// `ui` render (a custom `fn render()`) INTO structured MetaNode rows (Design.render), clearing the `ui`
// text so the design then projects its `ui` FROM the structured tree. The design crosses the wire as its
// id (schemaIdArg, like setDesign/commitDesign); server-side SchemaBridge.ImportRender does the atomic
// mint. Stages NOTHING in the data model; only fires the hostAction send-hook. Returns nothing;
// SSR/refetch no-ops it (CodeExecutor's `importRender` host-action case). The ack's refetch surfaces the
// now-structured render.
function execImportRender(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const design = executeValue(codeCall.params[0], scope, context).value;
    const { rest, callback } = splitTrailingCallback(codeCall.params.slice(1), scope, context);
    sendHostAction("importRender", [schemaIdArg(design), ...rest], callback);
    return { type: "nothing" };
}

// sys.createBranch(design, name, callback?): a SERVER-ONLY host action (M13 slice 5, wired in Track-B
// B4) — clone the design's working-copy subgraph (Design + its MetaTypes + MetaProps) into a NEW
// Branch named `name`, linked into db.branches (never db.designs). The design crosses as its id
// (schemaIdArg, like commitDesign); the name is a plain text arg. Stages NOTHING; only fires the
// hostAction send-hook. Returns nothing; SSR/refetch no-ops it (CodeExecutor's `createBranch` host-
// action case). The ack's refetch surfaces the new branch.
function execCreateBranch(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const design = executeValue(codeCall.params[0], scope, context).value;
    const name = executeValue(codeCall.params[1], scope, context).value;
    const { rest, callback } = splitTrailingCallback(codeCall.params.slice(2), scope, context);
    sendHostAction("createBranch", [schemaIdArg(design), name, ...rest], callback);
    return { type: "nothing" };
}

// sys.mergeBranch(source, target, resolutions?, callback?): a SERVER-ONLY host action (M13 slice 5,
// wired in Track-B B4) — a lineage-keyed three-way structural merge of `source`'s branch into
// `target`'s. Both designs cross as their ids (schemaIdArg). `resolutions` (OPTIONAL) is the operator's
// per-conflict picks — a Code array of { id, take:"source"|"target" } objects; it crosses NATIVELY
// because the hostAction send-hook's scalarOf now serializes arrays + objects-of-scalars recursively
// (ws.ts), which the server's existing ArgResolutionsOptional already parses. Omitted (a clean/preview-
// only merge) sends no third arg. Stages NOTHING; only fires the send-hook. Returns nothing; SSR/refetch
// no-ops it (CodeExecutor's `mergeBranch` case). A clean merge lands a two-parent commit; a conflict/
// drift merge writes nothing and its rejection surfaces via the global error banner. The trailing
// callback is type-disambiguated from `resolutions` by splitTrailingCallback (array vs fn) — the 3rd
// arg may be EITHER, and a 4th arg (resolutions + callback together) is the callback alone in last
// position.
function execMergeBranch(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const source = executeValue(codeCall.params[0], scope, context).value;
    const target = executeValue(codeCall.params[1], scope, context).value;
    const { rest, callback } = splitTrailingCallback(codeCall.params.slice(2), scope, context);
    sendHostAction("mergeBranch", [schemaIdArg(source), schemaIdArg(target), ...rest], callback);
    return { type: "nothing" };
}

// sys.login(name, password): a CLIENT-only host effect (M-auth login UI) — the session→principal bind.
// Like execPublish it stages NOTHING in the data model (no obj.props mutation, no invalidateProp), but it
// fires the dedicated `login` hook (NOT hostAction): login needs its REPLY to drive a refetch so the page
// re-renders as the bound principal (currentUser flips). Returns nothing; the SSR/refetch renderer no-ops
// it (CodeExecutor's `login` host-effect case). Args are the plaintext name + password (wire scalars).
function execLogin(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const name = executeValue(codeCall.params[0], scope, context).value;
    const password = executeValue(codeCall.params[1], scope, context).value;
    sendLogin(name, password);
    return { type: "nothing" };
}

// sys.logout(): the MIRROR of execLogin (M-auth login UI 1e-2) — clear the session→principal bind. Takes
// no args; stages NOTHING in the data model; fires the dedicated `logout` hook (NOT hostAction) so its
// REPLY can drive a refetch (the page swaps the root view back to the anonymous gate at the SAME URL —
// logout is a state change, not a route). Returns nothing; the SSR/refetch renderer no-ops it.
function execLogout(_codeCall: CodeCall, _scope: ExecScope, _context: ExecContext): ExecValue {
    sendLogout();
    return { type: "nothing" };
}

function collectionSysFunction(arr: ExecArray, method: string, context: ExecContext): ExecSysFunction {
    switch (method) {
        case "add": return { type: "sysFn", fn: args => { addToCollection(arr, args[0], context); return { type: "nothing" }; } };
        case "remove": return { type: "sysFn", fn: args => { removeFromCollection(arr, args[0]); return { type: "nothing" }; } };
        case "setEntry": return { type: "sysFn", fn: args => { setDictEntry(arr, args[0], args[1]); return { type: "nothing" }; } };
        case "where": return { type: "sysFn", fn: args => {
            const lambda = asLambda(args[0]);
            return memoize(`where:a${arr.id}:fn${lambda.fn.id}${closureKey(lambda)}`, context, () => whereCollection(arr, lambda, context));
        } };
        case "orderBy": return { type: "sysFn", fn: args => {
            const lambda = asLambda(args[0]);
            return memoize(`orderBy:a${arr.id}:fn${lambda.fn.id}${closureKey(lambda)}`, context, () => orderByCollection(arr, lambda, context));
        } };
        case "any": return { type: "sysFn", fn: args => {
            const lambda = asLambda(args[0]);
            recordMember(arr.id);
            for (const item of arr.items) {
                const r = invokeLambda(lambda, item.value, context);
                if (r.type === "bool" && r.value) return { type: "bool", value: true };
            }
            return { type: "bool", value: false };
        } };
        // single(predicate): the first member matching the predicate, or NULL when none match (no throw on
        // no-match — a "(choose…)" pick that matches nothing must clear a ref). Twin of CodeExecutor's single.
        case "single": return { type: "sysFn", fn: args => {
            const lambda = asLambda(args[0]);
            recordMember(arr.id);
            for (const item of arr.items) {
                const r = invokeLambda(lambda, item.value, context);
                if (r.type === "bool" && r.value) return item.value;
            }
            return { type: "null" };
        } };
        default: throw new Error(`Unknown collection method '${method}'.`);
    }
}

function asLambda(value: ExecValue): ExecFunction {
    if (value.type !== "fn") throw new Error("Expected a lambda argument.");
    return value;
}

function invokeLambda(fn: ExecFunction, arg: ExecValue, context: ExecContext): ExecValue {
    const callScope: ExecScope = { parent: fn.scope, items: {} };
    if (fn.fn.params.length > 0) callScope.items[fn.fn.params[0].name] = { value: arg, isReadOnly: true };
    return runBody(fn, callScope, context);
}

// Run a closure body, restoring its captured ambient first (null = a top-level fn → flows down to
// the live ambient), then restoring the call site's ambient afterward.
function runBody(fn: ExecFunction, callScope: ExecScope, context: ExecContext): ExecValue {
    const saved = context.ambient;
    if (fn.capturedAmbient != null) context.ambient = fn.capturedAmbient;
    try { return executeBlock(fn.fn.body, callScope, context) ?? { type: "nothing" }; }
    finally { context.ambient = saved; }
}

// The live root data context provided by the framework as ambient `ctx` (writes persist; a form
// opens a staging child via ctx.new()). Fresh per render — the root is a stateless live sentinel.
function rootAmbient(): AmbientFrame {
    return { name: "ctx", value: { type: "ctx", id: nextCtxId++, staged: new Map(), creates: [], parent: null, live: true, status: "idle", pending: 0, conflicts: [] }, parent: null };
}

function addToCollection(arr: ExecArray, value: ExecValue, context: ExecContext): void {
    // A set member is keyed by its object identity; other kinds get a transient key.
    const key = arr.kind === "set" && value.type === "object" ? value.id : --context.lastId.value;
    const item: ExecArrayItem = { key, value };
    arr.items.push(item);                 // optimistic row — local, unchanged whether the persist stages or fires
    invalidateMember(arr.id);
    // Staging branch (atomic-commit Step B): a TRANSIENT (id<0) draft added to a SET under a staging ctx
    // STAGES — it joins the ctx's creates instead of firing the live arrayAdd, so the whole changeset
    // persists all-or-none on the outermost commit. An EXISTING (id>0) member stays LIVE (the same id>0
    // discriminator the object-prop staging gate uses): poking an existing collection is immediate, building
    // new graph is transactional. The row already pushed above either way; only the persistence defers.
    if (arr.kind === "set" && value.type === "object" && value.id < 0) {
        const staging = nearestStagingCtx(context);
        if (staging != null) { staging.creates.push({ draft: value, join: { kind: "set", set: arr } }); return; }
    }
    if (arr.id > 0) sendArrayItemAdd(arr, item);
}

function removeFromCollection(arr: ExecArray, value: ExecValue): void {
    const index = arr.items.findIndex(i => i.value === value
        || (i.value.type === "object" && value.type === "object" && i.value.id === value.id));
    if (index < 0) return;
    const item = arr.items.splice(index, 1)[0];
    invalidateMember(arr.id);
    if (arr.id > 0 && arr.kind === "dict") sendEntryRemove(arr, item, dictEntryKey(item.value), index);
    else if (arr.id > 0) sendArrayItemRemove(arr, item, index);
}

// setEntry(key, value): create/replace a dictionary entry. The entry surfaces as an object
// carrying its key in `__key` (object dict: the value's scalar fields; scalar dict: a
// `{ __key, value }` wrapper). The entry's id is keyHash(dict,key) — deterministic, so the
// optimistic row and the server's refetched row share an id and dedup on merge.
function setDictEntry(arr: ExecArray, key: ExecValue, value: ExecValue): void {
    const keyText = scalarText(key);
    const id = keyHash(arr.id, keyText);
    const props: { [name: string]: ExecValue } = {};
    if (value.type === "object") {
        for (const [n, v] of Object.entries(value.props))
            if (v.type === "int" || v.type === "text" || v.type === "bool") props[n] = v;
    } else {
        props["value"] = value;
    }
    props["__key"] = { type: "text", value: keyText };
    const entry: ExecObject = { type: "object", id, props };
    const existing = arr.items.findIndex(i => i.key === id);
    let item: ExecArrayItem;
    if (existing >= 0) { item = arr.items[existing]; item.value = entry; }
    else { item = { key: id, value: entry }; arr.items.push(item); }
    invalidateMember(arr.id);
    if (arr.id > 0) sendEntryAdd(arr, item, keyText, value);
}

// A dictionary entry's key string (the reserved `__key` field).
function dictEntryKey(value: ExecValue): string {
    if (value.type === "object" && value.props["__key"]?.type === "text") return value.props["__key"].value;
    return "";
}

// A scalar ExecValue's string form (a dictionary key). Mirrors DbBridge.KeyText.
function scalarText(value: ExecValue): string {
    if (value.type === "text") return value.value;
    if (value.type === "int") return String(value.value);
    if (value.type === "bool") return value.value ? "true" : "false";
    return "";
}

// FNV-1a over (dict id, key) → a stable negative id for a dict entry. Mirrors
// DbBridge.KeyHash so the optimistic row and the server's row key alike.
function keyHash(dictId: number, keyText: string): number {
    let h = 2166136261 >>> 0;
    const s = `${dictId}/${keyText}`;
    for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; }
    return -((h & 0x3FFFFFFF)) - 1;
}

function whereCollection(arr: ExecArray, predicate: ExecFunction, context: ExecContext): ExecArray {
    recordMember(arr.id);
    const items = arr.items.filter(item => {
        const r = invokeLambda(predicate, item.value, context);
        return r.type === "bool" && r.value;
    });
    return { type: "array", kind: "list", items, id: --context.lastId.value };
}

function orderByCollection(arr: ExecArray, keySelector: ExecFunction, context: ExecContext): ExecArray {
    recordMember(arr.id);
    const items = arr.items
        .map(item => ({ item, key: invokeLambda(keySelector, item.value, context) }))
        .sort((a, b) => compareExec(a.key, b.key))
        .map(p => p.item);
    return { type: "array", kind: "list", items, id: --context.lastId.value };
}

function compareExec(x: ExecValue, y: ExecValue): number {
    if (x.type === "int" && y.type === "int") return x.value - y.value;
    if (x.type === "text" && y.type === "text") return x.value < y.value ? -1 : x.value > y.value ? 1 : 0;
    if (x.type === "bool" && y.type === "bool") return (x.value ? 1 : 0) - (y.value ? 1 : 0);
    throw new Error("orderBy key is not a comparable scalar.");
}

function getCompareValue(value: ExecValue): any {
    switch (value.type) {
        case "int": case "bool": case "text": return value.value;
        case "object": case "array": return value;
        // A function compares by reference identity (mirrors C#'s ExecFunction.Value => this,
        // compared via object.Equals): return the function value itself, so `fn == null` is
        // false and `fn != null` / `fn == sameFn` work. Without this the `default` threw.
        case "fn": return value;
        case "null": return null;
        default: throw new Error("NotImplementedException");
    }
}

function executeNot(codeNot: CodeNot, scope: ExecScope, context: ExecContext): ExecResult {
    const operand = executeValue(codeNot.operand, scope, context).value;
    if (operand.type !== "bool") throw new Error("Expected a bool.");
    return { value: { type: "bool", value: !operand.value } };
}

function executeInfixOpBasic(codeInfixOp: CodeInfixOp, scope: ExecScope, context: ExecContext): ExecValue {
    const left = executeValue(codeInfixOp.left, scope, context).value;
    const right = executeValue(codeInfixOp.right, scope, context).value;
    const asInt = (v: ExecValue) => { if (v.type !== "int") throw new Error("Expected an int."); return v.value; };
    const asBool = (v: ExecValue) => { if (v.type !== "bool") throw new Error("Expected a bool."); return v.value; };
    switch (codeInfixOp.op) {
        // `+` is overloaded: a string operand makes it concatenation (both sides stringified),
        // otherwise integer addition. Twin of CodeExecutor's Add arm + AsText.
        case "add": {
            if (left.type === "text" || right.type === "text") {
                const asText = (v: ExecValue): string => {
                    if (v.type === "text") return v.value;
                    if (v.type === "int" || v.type === "bool") return String(v.value);
                    throw new Error("Cannot convert value to text.");
                };
                return { type: "text", value: asText(left) + asText(right) };
            }
            return { type: "int", value: asInt(left) + asInt(right) };
        }
        case "subtract": return { type: "int", value: asInt(left) - asInt(right) };
        case "multiply": return { type: "int", value: asInt(left) * asInt(right) };
        case "divide": return { type: "int", value: Math.trunc(asInt(left) / asInt(right)) };
        case "modulo": return { type: "int", value: asInt(left) % asInt(right) };
        case "equals": return { type: "bool", value: getCompareValue(left) === getCompareValue(right) };
        case "notEquals": return { type: "bool", value: getCompareValue(left) !== getCompareValue(right) };
        case "lessThan": return { type: "bool", value: asInt(left) < asInt(right) };
        case "lessThanOrEqual": return { type: "bool", value: asInt(left) <= asInt(right) };
        case "moreThan": return { type: "bool", value: asInt(left) > asInt(right) };
        case "moreThanOrEqual": return { type: "bool", value: asInt(left) >= asInt(right) };
        case "and": return { type: "bool", value: asBool(left) && asBool(right) };
        case "or": return { type: "bool", value: asBool(left) || asBool(right) };
        default: throw new Error("NotImplementedException");
    }
}

// The `sys` builtin a callee names, or null. Builtins are namespaced under `sys`: a callee of
// the form `sys.<name>` (a member access on the bare `sys` symbol) dispatches the builtin.
// Mirrors CodeExecutor.IsSysBuiltin — `sys` is a real object value but carries no builtin
// props, so this never reads `sys.field` as an object-prop access.
function sysBuiltinName(fn: CodeValue): string | null {
    if (fn.type === "infixOp" && fn.op === "objectProp"
        && fn.left.type === "symbol" && fn.left.name === "sys"
        && fn.right.type === "symbol")
        return fn.right.name;
    return null;
}

function executeCall(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    // Built-ins (sys.field / sys.humanize / …). `sys.field` is also intercepted in executeValue
    // for its setValue; in statement position the value form is enough.
    switch (sysBuiltinName(codeCall.fn)) {
        case "field": return fieldResult(codeCall, scope, context).value;
        case "humanize": return execHumanize(codeCall, scope, context);
        case "extent": return execExtent(codeCall, scope, context);
        case "schema": return execSchema(codeCall, scope, context);
        case "canWrite": return execCanWrite(codeCall, scope, context);
        case "canRead": return execCanRead(codeCall, scope, context);
        case "diffCommits": return execDiffCommits(codeCall, scope, context);
        case "publishPreview": return execPublishPreview(codeCall, scope, context);
        case "renderTree": return execRenderTree(codeCall, scope, context);
        case "mergePreview": return execMergePreview(codeCall, scope, context);
        case "setRef": return execSetRef(codeCall, scope, context);
        case "publish": return execPublish(codeCall, scope, context);
        case "create": return execCreate(codeCall, scope, context);
        case "cloneInstance": return execCloneInstance(codeCall, scope, context);
        case "delete": return execDelete(codeCall, scope, context);
        case "rename": return execRename(codeCall, scope, context);
        case "setDesign": return execSetDesign(codeCall, scope, context);
        case "commitDesign": return execCommitDesign(codeCall, scope, context);
        case "revertCommit": return execRevertCommit(codeCall, scope, context);
        case "importRender": return execImportRender(codeCall, scope, context);
        case "createBranch": return execCreateBranch(codeCall, scope, context);
        case "mergeBranch": return execMergeBranch(codeCall, scope, context);
        case "login": return execLogin(codeCall, scope, context);
        case "logout": return execLogout(codeCall, scope, context);
        case "nest": return execNest(codeCall, scope, context);
        case "segment": return execSegment(codeCall, scope, context);
        case "toInt": return execToInt(codeCall, scope, context);
        case "id": return execId(codeCall, scope, context);
        case "new": return execNew(codeCall, scope, context);
        case "resolve": return execResolve(codeCall, scope, context);
    }
    const fn = executeValue(codeCall.fn, scope, context).value;
    if (fn.type === "sysFn") return fn.fn(codeCall.params.map(p => executeValue(p, scope, context).value));
    if (fn.type === "ctxMethod") return callCtxMethod(fn, codeCall.params.map(p => executeValue(p, scope, context).value));
    if (fn.type !== "fn") throw new Error("Target of a call is not a function.");

    // Args evaluate in the caller's context (their deps are the caller's); the body is
    // memoized by (function id, RENDER SLOT, arg identities), capturing its own deps. The live `slotPath`
    // segment is what keeps a RENDER-PROP call distinct per call site: a `body()` invoked at two child
    // slots of one component — or once per `foreach` row (a `row<id>` slot segment) — shares the SAME
    // lambda AST id and (often) the same args, so id+args alone collide and the read-cache would replay
    // the FIRST call's render (and its <item> side effects) for the second, dropping the sibling/row's own
    // content. This holds even when the body returns a bare SCALAR (no tag built inside its bracket), which
    // a result/side-effect heuristic cannot see. Folding the slot disambiguates every shape uniformly; a
    // same-site re-render still hits (same slot + args). The C# twin folds context.SlotPath identically
    // (CallFunction) — same render structure ⇒ same slot ⇒ same key on both sides, so a `fn:` result the
    // server SHIPPED (private inputs the client can't recompute) is still found by key on the client.
    const argVals = codeCall.params.map(p => executeValue(p, scope, context).value);
    const closure = fn;
    return memoize(memoKey("fn:" + closure.fn.id + "@" + slotPath.join("/"), argVals), context, () => {
        const callScope: ExecScope = { parent: closure.scope, items: {} };
        for (let i = 0; i < argVals.length && i < closure.fn.params.length; i++)
            callScope.items[closure.fn.params[i].name] = { value: argVals[i], isReadOnly: true };
        return runBody(closure, callScope, context);
    });
}

// ── tags (DOM-free; ui.ts renders the resulting ExecTag tree) ─────────────────────

function executeTag(codeTag: CodeTag, scope: ExecScope, context: ExecContext): ExecTag {
    const attributes: { [name: string]: ExecResult } = {};
    for (const attr of codeTag.attributes) attributes[attr.name] = executeValue(attr.value, scope, context);
    // Stamp this element's onClick handler closure with the LIVE slot path (client data layer, slice 4):
    // executeTagChildren pushed this element's static index (and any enclosing foreach row identity) before
    // rendering it, so slotPath is the handler's render-slot here — capture it on the closure so a later click
    // can report (slot, fn-id) to the server's action-miss harvest (the slot path is reset each render, so it
    // cannot be recovered at click time otherwise). The server derives the SAME key during its reproduced
    // render (CodeExecutor.ExecuteTag), so the two match. Twin of the C# indexing.
    const onClick = attributes["onClick"]?.value;
    if (onClick != null && onClick.type === "fn") onClick.handlerSlot = slotPath.join("/");
    // A <select>'s onChange handler (RefSelect.applyPick) runs through the same action-miss machinery, so
    // it needs the SAME slot stamp — the create form is client-toggled, so a pick may be the FIRST access of
    // the candidate collection and must be able to report (slot, fn-id) and refetch.
    const onChange = attributes["onChange"]?.value;
    if (onChange != null && onChange.type === "fn") onChange.handlerSlot = slotPath.join("/");
    return { type: "tag", name: codeTag.name, attributes, children: executeTagChildren(codeTag.children, scope, context) };
}

// The address of an onClick handler closure: its render-slot path joined with its lambda's twin-stable
// fn-id (slot alone is not unique — every foreach row's button shares the lambda AST). Twin of
// CodeExecutor.HandlerKey; both must derive identically so the server's reproduced index matches the
// handler the client reports.
function handlerKey(slot: string, fnId: number): string { return slot + "#fn" + fnId; }

function executeTagChild(child: CodeTagChild, scope: ExecScope, context: ExecContext): ExecTagChild[] {
    switch (child.type) {
        case "tag": {
            // A tag whose name resolves to a function in scope is a COMPONENT (run-once setup,
            // slot-keyed identity); any other tag name is an HTML element.
            const component = tryResolveComponent(child.name, scope);
            return component ? executeComponent(child, component, scope, context)
                             : [executeTag(child, scope, context)];
        }
        case "foreach": return executeTagForEach(child, scope, context);
        case "if": return executeTagIf(child as CodeTagIf, scope, context);
        default: return [executeValue(child as CodeValue, scope, context).value];
    }
}

function executeTagChildren(body: CodeTagChild[], scope: ExecScope, context: ExecContext): ExecTagChild[] {
    const children: ExecTagChild[] = [];
    const innerScope: ExecScope = { parent: scope, items: {} };
    // Push each child's STATIC AST index onto the slot path while it renders (twin of the C#
    // ExecuteTagChildren) — a component child keys on its render-tree position. Balanced push/pop.
    for (let i = 0; i < body.length; i++) {
        slotPath.push(String(i));
        try { children.push(...executeTagChild(body[i], innerScope, context)); }
        finally { slotPath.pop(); }
    }
    return children;
}

// ── components (Milestone 11; twin of CodeExecutor.cs's component path) ────────────

// A tag is a component iff its name resolves to a function in the scope chain (pure
// name-resolution): <div> is an element because `div` is unbound; <noteForm> is a component
// because `noteForm` is a function. Non-throwing — a name not in scope, or bound to a
// non-function, is an HTML element. Stops at the first binding (shadowing).
function tryResolveComponent(name: string, scope: ExecScope): ExecFunction | null {
    for (let s: ExecScope | null = scope; s != null; s = s.parent)
        if (s.items[name]) return s.items[name].value.type === "fn" ? s.items[name].value as ExecFunction : null;
    return null;
}

// Render a component tag (<noteForm desc={...}>): runs ONCE PER RENDER-TREE SLOT (keyed on the
// slot path, not its argument identities) so its local state survives re-renders even when an
// argument is a fresh object each render. The body returns its reactive view (a render closure);
// we invoke that — itself slot-keyed, so it recomputes on its own deps and never collides with
// another slot — to produce the tags, which splice into the parent's children.
// A component in tag-child position: render it and splice its view into the parent's children.
function executeComponent(tag: CodeTag, component: ExecFunction, scope: ExecScope, context: ExecContext): ExecTagChild[] {
    return spliceView(executeComponentValue(tag, component, scope, context));
}

// Run a component (slot-keyed setup + the auto-invoked reactive view) and return its VIEW VALUE —
// the form used in VALUE / return position (a root/returned component). The tag-child form splices
// this value into the parent's children instead.
function executeComponentValue(tag: CodeTag, component: ExecFunction, scope: ExecScope, context: ExecContext): ExecValue {
    // Attributes evaluate in the CALLER's context, in tag order, like ordinary call arguments — so a
    // rebuilt-literal descriptor is fresh each render.
    const attrs: { [name: string]: ExecValue } = {};
    for (const attr of tag.attributes) attrs[attr.name] = executeValue(attr.value, scope, context).value;
    // The slot path is the chain of static child indices PLUS each enclosing `foreach`'s per-row
    // identity segment (executeTagForEach), so a component inside a list gets a distinct,
    // identity-stable key per row — its state moves with the member across reorder/remove. A
    // value/root component keys on the (empty/root) path — one component per render, so unique.
    let slotKey = "comp:" + slotPath.join("/");
    // `key={...}` is a RESERVED directive (not a param): its value folds into the slot key, so
    // changing it gives the component a NEW identity (caller-controlled "reset when X changes").
    if ("key" in attrs) slotKey += "#" + argKey(attrs["key"]);
    const args = component.fn.params.map(p => (p.name !== "key" && p.name in attrs) ? attrs[p.name] : { type: "null" } as ExecValue);
    // Reactive props: a slot-stable component re-invoked with a CHANGED OBJECT argument must reflect it. The
    // setup still runs ONCE (its `var state` persists across the re-render) — but its cached output was bound
    // to the OLD args. When an object arg's identity differs from the last invocation at this slot, refresh:
    //   • a STATEFUL component (its setup returned a render CLOSURE) keeps its state — re-bind the params in
    //     the captured scope and recompute only its view; and
    //   • a STATELESS one (returned the tag directly — no closure ⇒ no persistent state) is recomputed over
    //     the new args.
    // ONLY object args are tracked (objectArgKey): an object is the bindable identity a view pins to (its
    // sys.field reads), so a swap to a different object (e.g. a create-form draft replaced by a fresh
    // sys.new) must re-bind. A rebuilt-literal ARRAY (e.g. columns={["label"]}), scalar, or function arg gets
    // a fresh id every render but is semantically stable — its CONTENT changes already propagate through
    // deps; tracking it would recompute the component EVERY render (churn that breaks reconciliation). A
    // server-shipped entry has no argsKey yet: skip it on the hydration render (re-running could re-read
    // private data the server shipped the RESULT for) — that render uses the server's args, so it just
    // records the matching key; real swaps come from later interactions and are detected then.
    const argsKey = objectArgKey(args);
    const prior = memoCache?.get(slotKey);
    if (prior != null && !prior.stale && prior.argsKey != null && prior.argsKey !== argsKey) {
        if (prior.result.type === "fn") {
            if (rebindComponentArgs(component, prior.result, args)) {
                const viewEntry = memoCache!.get(slotKey + ":view");
                if (viewEntry != null) viewEntry.stale = true;
            }
        } else {
            prior.stale = true; // stateless: recompute the tag over the new args
        }
    }
    let view = memoize(slotKey, context, () => invokeFn(component, args, context));
    const entry = memoCache?.get(slotKey);
    if (entry != null) entry.argsKey = argsKey;
    if (view.type === "fn") {
        const renderClosure = view;
        // Seed (client data layer, slice 1a): if a state seed targets THIS slot, overwrite the setup's
        // locals BEFORE invoking the view — so a render reproduces the client's exact component state.
        // The render closure captures the setup's local scope (where its `var state …` lives), so the
        // seed writes straight into renderClosure.scope. Twin of CodeExecutor.ApplySeed.
        applySeed(slotKey, renderClosure.scope, context);
        view = memoize(slotKey + ":view", context, () => invokeFn(renderClosure, [], context));
    }
    return view;
}

// Overwrite the seeded locals for `slotKey` in the component setup's scope (client data layer, slice
// 1a). Each (varName → value) replaces that var's value wholesale (whole-object overwrite, v1) — but
// only when the var actually exists in the setup scope, so a stale/foreign seed can never inject a new
// local. Twin of CodeExecutor.ApplySeed; both must overwrite identically.
function applySeed(slotKey: string, setupScope: ExecScope, context: ExecContext): void {
    const vars = context.seed?.[slotKey];
    if (vars == null) return;
    for (const name in vars)
        if (name in setupScope.items) setupScope.items[name].value = vars[name];
}

// The identity signature of a component's BINDABLE object arguments (param-index : object id), the key the
// slot's reactive-prop check diffs across renders. Tracked iff the arg is a stable positive-id db object OR
// a sys.new draft (draftObjects) — the genuine bindable identities a view pins to. A re-minted DESCRIPTOR
// (sys.schema yields a fresh negative-id object each refetch, same meaning), a rebuilt-literal array/object,
// a scalar, or a function is EXCLUDED: its identity churns or is irrelevant, and its content changes already
// propagate through deps — tracking it would re-bind the component every render. Empty when a component
// takes no bindable object args (then it never re-binds on an arg change, by design).
function objectArgKey(args: ExecValue[]): string {
    let key = "";
    for (let i = 0; i < args.length; i++) {
        const a = args[i];
        if (a.type === "object" && (a.id > 0 || draftObjects.has(a))) key += i + ":" + a.id + ";";
    }
    return key;
}

// Refresh a slot-stable component's params to the CURRENT args, in the call scope its render closure
// captured (the transient frame between the closure's body scope and the persistent top scope — walk up
// while !isTop, like closureKey). Returns whether any param value actually changed, so the caller only
// stales the view when it must. Read-only param bindings are updated in place: the read-only guard is for
// user `=` statements; a re-bind is the framework refreshing the prop, not user code.
function rebindComponentArgs(component: ExecFunction, closure: ExecFunction, args: ExecValue[]): boolean {
    let changed = false;
    const params = component.fn.params;
    for (let i = 0; i < params.length && i < args.length; i++) {
        if (params[i].name === "key") continue;
        for (let s: ExecScope | null = closure.scope; s != null && !s.isTop; s = s.parent) {
            const item = s.items[params[i].name];
            if (item != null) {
                if (argKey(item.value) !== argKey(args[i])) { item.value = args[i]; changed = true; }
                break;
            }
        }
    }
    return changed;
}

// Invoke a function value with already-evaluated args (twin of C# InvokeFunction) — a child
// scope of the closure's captured scope, params bound positionally, the body run directly
// (no memoization; executeComponent wraps the slot-keyed memo around this).
function invokeFn(fn: ExecFunction, args: ExecValue[], context: ExecContext): ExecValue {
    const callScope: ExecScope = { parent: fn.scope, items: {} };
    for (let i = 0; i < args.length && i < fn.fn.params.length; i++)
        callScope.items[fn.fn.params[i].name] = { value: args[i], isReadOnly: true };
    return runBody(fn, callScope, context);
}

// A component's view splices into the parent's children: an array (a fragment) splices flat, a
// single tag/value becomes one child.
function spliceView(view: ExecValue): ExecTagChild[] {
    return view.type === "array" ? view.items.map(i => i.value) : [view];
}

function executeTagIf(codeTagIf: CodeTagIf, scope: ExecScope, context: ExecContext): ExecTagChild[] {
    const condition = executeValue(codeTagIf.condition, scope, context).value;
    if (condition.type !== "bool") throw new Error("Result of if condition is not boolean.");
    const code = condition.value ? codeTagIf.body : codeTagIf.elseBody;
    return code == null ? [] : executeTagChildren(code, scope, context);
}

function executeTagForEach(codeTagForEach: CodeTagForEach, scope: ExecScope, context: ExecContext): ExecTagChild[] {
    const array = executeValue(codeTagForEach.collection, scope, context).value;
    if (array.type !== "array") throw new Error("foreach target is not a collection.");
    // Inside a computation (a memoized page fn), iterating observes membership:
    // an add/remove to the collection must invalidate the cached result.
    recordMember(array.id);
    const children: ExecTagChild[] = [];
    for (const item of array.items) {
        // Identity key for DOM reconciliation: the member object's intrinsic id, so a
        // row's element (and its input focus/state) moves with the object on reorder.
        const key = item.value.type === "object" ? item.value.id : item.key;
        const itemScope: ExecScope = { parent: scope, items: {} };
        itemScope.items[codeTagForEach.item.name] = { value: item.value, isReadOnly: true };
        // The SAME identity keys the component slot path (twin of ExecuteTagForEach), so a
        // component inside this row gets a distinct, identity-stable slot — its state moves with the
        // member across reorder/remove, not with the row position.
        slotPath.push("row" + key);
        let produced: ExecTagChild[];
        try { produced = executeTagChildren(codeTagForEach.body, itemScope, context); }
        finally { slotPath.pop(); }
        for (const c of produced) if (c.type === "tag" && c.key == null) c.key = key;
        children.push(...produced);
    }
    return children;
}

function findScope(symbol: CodeSymbol, scope: ExecScope): ExecScope {
    if (scope.items[symbol.name]) return scope;
    if (scope.parent !== null) return findScope(symbol, scope.parent);
    throw new Error(`Variable ${symbol.name} not found`);
}

function tryFindScope(symbol: CodeSymbol, scope: ExecScope): ExecScope | null {
    for (let s: ExecScope | null = scope; s != null; s = s.parent)
        if (s.items[symbol.name]) return s;
    return null;
}

// ── data context (the ambient `ctx` overlay) ─────────────────────────────────
// ponytail: resolves ambient `ctx` per prop access (linear walk); cache if it matters.
function nearestCtx(context: ExecContext): ExecCtx | null {
    for (let f = context.ambient ?? null; f != null; f = f.parent)
        if (f.name === "ctx") return f.value.type === "ctx" ? f.value : null;
    return null;
}
function nearestStagingCtx(context: ExecContext): ExecCtx | null {
    const c = nearestCtx(context);
    return c != null && !c.live ? c : null;
}
function nearestStagedValue(obj: ExecObject, prop: string, context: ExecContext): ExecValue | undefined {
    for (let c = nearestCtx(context); c != null; c = c.parent) {
        const v = c.staged.get(obj)?.get(prop);
        if (v != null) return v;
    }
    return undefined;
}
function callCtxMethod(m: ExecCtxMethod, args: ExecValue[]): ExecValue {
    switch (m.method) {
        // ctx.new(autosave): autosave=true → the live parent (writes persist); else a staging child.
        case "new": return args.length > 0 && args[0].type === "bool" && args[0].value ? m.ctx : { type: "ctx", id: nextCtxId++, staged: new Map(), creates: [], parent: m.ctx, live: false, status: "idle", pending: 0, conflicts: [] };
        case "discard":
            for (const [obj, fields] of m.ctx.staged)
                for (const prop of fields.keys()) invalidateProp(obj.id, prop);   // re-render the reverted fields
            m.ctx.staged.clear();
            setCtxStatus(m.ctx, "idle");   // a discard saved nothing — clear any lingering "Saved" indicator
            return { type: "nothing" };
        case "commit":
            // Begin the Save lifecycle: enter "saving" and tag the commit's WS sends with THIS ctx so the
            // ack can drive it to "saved" (sendBeginCommit makes recordMutation stamp entry.ctx = m.ctx +
            // bump m.ctx.pending for each send). The staged-walk's sends fire SYNCHRONOUSLY inside the
            // bracket (buffered by the handler transaction), so the module-level committing-ctx is reliably
            // the right ctx for exactly those sends. pending is NOT reset here — it accrues per-send and
            // retires per-ack, so a rapid second commit before the first's acks return stays correct; the
            // finally guarantees the bracket closes even if the walk throws (no leaked committing-ctx).
            setCtxStatus(m.ctx, "saving");
            sendBeginCommit(m.ctx);
            const parent = m.ctx.parent;
            const nested = parent != null && !parent.live; // NON-final: transfer up into the parent ctx
            try {
                // Edits: flush into the parent ctx (nested) or to the live object (final). Uniform with how
                // edits already flow; atomic-commit Step B adds the SAME defer-vs-persist rule for creates below.
                for (const [obj, fields] of m.ctx.staged)
                    for (const [prop, val] of fields) {
                        if (nested) {
                            let pf = parent!.staged.get(obj);
                            if (pf == null) parent!.staged.set(obj, pf = new Map());
                            pf.set(prop, val);
                        } else {
                            const before = obj.props[prop];
                            obj.props[prop] = val;
                            invalidateProp(obj.id, prop);
                            if (obj.id > 0) propValueChange(obj, prop, val, before);
                        }
                    }
                // Creates (atomic-commit Step B): a nested commit TRANSFERS each create up into the parent
                // (it joins the parent's creates, exactly as edits join the parent's staged) — so the outermost
                // form's Save persists the whole changeset. The FINAL commit hands each create to the WS layer,
                // which folds it into the ONE `commit` op's creates+relations (sendEndCommit sends it).
                for (const create of m.ctx.creates) {
                    if (nested) parent!.creates.push(create);
                    else sendCommitCreate(create.draft, create.join);
                }
            } finally {
                sendEndCommit();
            }
            m.ctx.creates = [];
            // Nothing in flight (no sends this commit AND no prior pending — e.g. an empty commit or a
            // flush into a staging parent) → no ack is coming, so don't get stuck on "saving": settle to
            // "idle". (pending is not zeroed above, so this stays non-zero while a prior batch is in flight.)
            if (m.ctx.pending === 0) setCtxStatus(m.ctx, "idle");
            m.ctx.staged.clear();
            return { type: "nothing" };
        // Conflict resolution (M13 slice 6) — the coarse banner's two buttons. CLIENT-only effects handed to
        // the WS layer (ws.ts owns the failed-commit re-send / the theirs-values), which is where the conflict
        // state was recorded on the reply. keep-mine = force re-commit at the fresh base; take-theirs = drop
        // mine, refresh to theirs. Both clear ctx.conflicts (the banner disappears) — the resolution is the
        // exit from conflict mode. The C# twin no-ops these (server renders once, never witnesses a conflict).
        case "keepMine": sendKeepMine(m.ctx); return { type: "nothing" };
        case "takeTheirs": sendTakeTheirs(m.ctx); return { type: "nothing" };
        // Per-field resolution (M13 Track-B B5) — the fine <ConflictBar>'s per-field buttons:
        // ctx.resolveField(object, field, take). take=true → take theirs for this field; false → keep mine.
        // CLIENT-only, handed to ws.ts (which owns the failed-commit edits + theirs values). Args: an int
        // object id, a text field name, a bool take. The C# twin no-ops (parity — server never conflicts).
        case "resolveField": {
            const object = args[0]?.type === "int" ? args[0].value : 0;
            const field = args[1]?.type === "text" ? args[1].value : "";
            const take = args[2]?.type === "bool" && args[2].value;
            sendResolveField(m.ctx, object, field, take);
            return { type: "nothing" };
        }
        default: throw new Error(`Unknown context method '${m.method}'.`);
    }
}

// WebSocket mutation sends — wired by ws.ts via setWsHooks (Stage 4b). Until then they
// no-op, so local two-way binding and transient construction work without a server.
// Each hook carries what a rollback needs (Stage 5): the live target reference, the
// overwritten before-value, the removed item and its index.
interface WsHooks {
    propChange(obj: ExecObject, prop: string, value: ExecValue, before: ExecValue): void;
    // A path-addressed leaf write for a dictionary entry's field (no extent id).
    pathWrite(obj: ExecObject, prop: string, path: string, value: ExecValue, before: ExecValue): void;
    setRef(obj: ExecObject, prop: string, value: ExecValue, before: ExecValue): void;
    arrayAdd(arr: ExecArray, item: ExecArrayItem, typeName: string | undefined): void;
    arrayRemove(arr: ExecArray, item: ExecArrayItem, index: number): void;
    // Dictionary entries persist through the PATH-addressed add/removeEntry ops (arr.sourcePath).
    entryAdd(arr: ExecArray, item: ExecArrayItem, key: string, value: ExecValue): void;
    entryRemove(arr: ExecArray, item: ExecArrayItem, key: string, index: number): void;
    // A SERVER-ONLY host action (sys.publish): the client fires the action, the server alone runs
    // the effect. Stages nothing in the data model (no optimistic mutation to roll back). `callback`
    // (docs/plans/host-action-success-signal.md — the optional trailing fn arg every host-action
    // builtin now accepts) is registered under this send's msgId and invoked ONLY when its ok reply
    // arrives; it never runs on error (the banner path is unchanged).
    hostAction(action: string, args: ExecValue[], callback?: ExecFunction): void;
    // The session→principal bind (sys.login, M-auth login UI): send credentials over the WS; its REPLY
    // (unlike a host action) drives a refetch so the page re-renders as the bound principal. Distinct
    // from hostAction because the reply must trigger the refetch, not just surface an error.
    login(name: ExecValue, password: ExecValue): void;
    // The MIRROR of login (sys.logout, M-auth login UI 1e-2): clear the principal over the WS. Takes no
    // credentials; like login its REPLY (not a host action) drives a refetch, so the page swaps the root
    // view back to the anonymous gate at the same URL.
    logout(): void;
    // Form-Save feedback: bracket a ctx.commit's staged-walk so the WS sends it triggers are TAGGED with
    // the committing ctx (recordMutation stamps entry.ctx + bumps ctx.pending). The ack/reject then drives
    // that ctx's status to "saved" (a reject clears it back to "idle"). CLIENT-only — the C# twin has no
    // wsHooks and renders once.
    beginCommit(ctx: ExecCtx): void;
    endCommit(): void;
    // A staged CREATE in a final commit (atomic-commit Step B): the transient draft + its join (a set, or a
    // single-reference field). Called for each create BETWEEN beginCommit and endCommit, so the WS layer folds
    // it into the ONE `commit` op's creates+relations. The server resolves the create's TYPE from the join
    // (set element type / ref prop type) — the wire carries no client-asserted type. CLIENT-only.
    commitCreate(draft: ExecObject, join: CreateJoin): void;
    // Conflict resolution (M13 slice 6) — the coarse banner's buttons. keepMine re-sends the ctx's LAST
    // rejected commit's edits at the now-fresh base (a deliberate force overwrite). takeTheirs drops mine and
    // refreshes the conflicted fields to the server's values (from the reply's payload). Both clear the ctx's
    // conflicts. CLIENT-only — the failed-commit edits + theirs values live in ws.ts (recorded on the reply).
    keepMine(ctx: ExecCtx): void;
    takeTheirs(ctx: ExecCtx): void;
    // Per-field resolution (M13 Track-B B5 — the fine <ConflictBar>'s per-field buttons). Resolve ONE
    // (object, field): take=true writes the server's value into that field + drops the item; take=false just
    // drops the item (mine stays). When the last item is dropped, ws.ts re-commits at the fresh base. This
    // progressively SHRINKS ctx.conflicts, avoiding a client-only picks collection (a refetch can't round-trip
    // component-state arrays — B4's constraint). CLIENT-only — the failed-commit edits + theirs live in ws.ts.
    resolveField(ctx: ExecCtx, object: number, field: string, take: boolean): void;
}
let wsHooks: WsHooks | null = null;
function setWsHooks(hooks: WsHooks): void { wsHooks = hooks; }

function propValueChange(obj: ExecObject, propName: string, value: ExecValue, before: ExecValue): void {
    wsHooks?.propChange(obj, propName, value, before);
}
function pathWriteChange(obj: ExecObject, propName: string, path: string, value: ExecValue, before: ExecValue): void {
    wsHooks?.pathWrite(obj, propName, path, value, before);
}
function referenceChange(obj: ExecObject, prop: string, value: ExecValue, before: ExecValue): void {
    wsHooks?.setRef(obj, prop, value, before);
}
function sendArrayItemAdd(arr: ExecArray, item: ExecArrayItem): void {
    wsHooks?.arrayAdd(arr, item, arr.elementTypeName);
}
function sendArrayItemRemove(arr: ExecArray, item: ExecArrayItem, index: number): void {
    wsHooks?.arrayRemove(arr, item, index);
}
function sendEntryAdd(arr: ExecArray, item: ExecArrayItem, key: string, value: ExecValue): void {
    wsHooks?.entryAdd(arr, item, key, value);
}
function sendEntryRemove(arr: ExecArray, item: ExecArrayItem, key: string, index: number): void {
    wsHooks?.entryRemove(arr, item, key, index);
}
function sendHostAction(action: string, args: ExecValue[], callback?: ExecFunction): void {
    wsHooks?.hostAction(action, args, callback);
}
function sendLogin(name: ExecValue, password: ExecValue): void {
    wsHooks?.login(name, password);
}
function sendLogout(): void {
    wsHooks?.logout();
}
function sendBeginCommit(ctx: ExecCtx): void {
    wsHooks?.beginCommit(ctx);
}
function sendCommitCreate(draft: ExecObject, join: CreateJoin): void {
    wsHooks?.commitCreate(draft, join);
}
function sendEndCommit(): void {
    wsHooks?.endCommit();
}
function sendKeepMine(ctx: ExecCtx): void {
    wsHooks?.keepMine(ctx);
}
function sendTakeTheirs(ctx: ExecCtx): void {
    wsHooks?.takeTheirs(ctx);
}
function sendResolveField(ctx: ExecCtx, object: number, field: string, take: boolean): void {
    wsHooks?.resolveField(ctx, object, field, take);
}

// ── conformance entry point ───────────────────────────────────────────────────────

// Runs a conformance case (the exact JSON shape from conformance.json) and returns the scalar
// result as { kind, value }. A case is either a single `expr` (evaluated once) or the lifecycle
// protocol — optional `setup` statements run once into a retained scope, then `renders`
// value-exprs evaluated in order against that same scope+context, returning the LAST result. The
// C# side runs the identical protocol in ConformanceTests; any drift fails on one side or the other.
function runConformance(caseJson: string): string {
    // `seed` (client data layer, slice 1a) is an OPTIONAL case-level map { slotKey → { varName →
    // value-expr } } that reproduces a component's client view-state: its value-exprs are evaluated
    // once (in the retained scope/context, like setup) and installed as context.seed for the renders,
    // exercising the same seeding path the server uses. Absent = today's protocol, unchanged.
    const c = JSON.parse(caseJson) as
        { expr?: CodeValue; setup?: CodeStatement[]; renders?: CodeValue[]; seed?: { [slotKey: string]: { [varName: string]: CodeValue } } };
    // Memoize like the live client (and like the C# runner, whose Memo is always on): a
    // where/orderBy memo-key collision — and the component slot identity — only surface when the
    // cache is active, so the conformance suite must exercise the cached path to prove both twins agree.
    setMemoCache(new Map());
    resetSlotPath();
    const scope: ExecScope = { items: {}, parent: null };
    // A persisted (positive-id) object the overlay cases stage onto (staging is gated to id>0).
    scope.items["o"] = { value: { type: "object", id: 100, props: { f: { type: "int", value: 1 } } }, isReadOnly: false };
    const context: ExecContext = { lastId: { value: 0 }, ambient: rootAmbient() };
    let result: ExecValue = { type: "nothing" };
    if (c.renders) {
        for (const stmt of c.setup ?? []) executeStatement(stmt, scope, context);
        if (c.seed) {
            const seed: { [slotKey: string]: { [varName: string]: ExecValue } } = {};
            for (const slotKey in c.seed) {
                seed[slotKey] = {};
                for (const varName in c.seed[slotKey])
                    seed[slotKey][varName] = executeValue(c.seed[slotKey][varName], scope, context).value;
            }
            context.seed = seed;
        }
        for (const render of c.renders) result = executeValue(render, scope, context).value;
    } else {
        result = executeValue(c.expr!, scope, context).value;
    }
    setMemoCache(null);
    switch (result.type) {
        case "int": return JSON.stringify({ kind: "int", value: result.value });
        case "text": return JSON.stringify({ kind: "text", value: result.value });
        case "bool": return JSON.stringify({ kind: "bool", value: result.value });
        case "array": return JSON.stringify({ kind: "intList", value: result.items.map(i => (i.value as ExecInt).value) });
        case "tag": return JSON.stringify({ kind: "tag", value: serializeTree(result) });
        case "nothing": return JSON.stringify({ kind: "nothing", value: null });
        case "null": return JSON.stringify({ kind: "null", value: null });
        default: throw new Error(`Non-scalar conformance result '${result.type}'.`);
    }
}

// Canonical string form of a rendered tag tree (conformance only): `<name attr="v"…>children…</name>`,
// attributes sorted ordinally, text children inline. Twin of ConformanceTests.SerializeTree — both must
// produce the same string for the SAME tree, so the tag conformance case proves the twins build an
// identical sys.renderTree canvas.
function serializeTree(node: ExecValue): string {
    if (node.type === "tag") {
        let s = "<" + node.name;
        for (const k of Object.keys(node.attributes).sort()) s += " " + k + "=\"" + scalarText(node.attributes[k].value) + "\"";
        s += ">";
        for (const c of node.children) s += serializeTree(c);
        return s + "</" + node.name + ">";
    }
    return scalarText(node);
}

(globalThis as any).runConformance = runConformance;
