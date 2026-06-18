// The client-side twin of the C# Code interpreter (DeEnv/Code/CodeExecutor.cs),
// ported from the app14 prototype and adapted to the camelCase wire format. This is
// the evaluation core only — DOM rendering (ui.ts) and the WebSocket/data-transfer
// protocol (Stage 4) layer on top. The two interpreters are kept in lockstep by the
// shared conformance suite (DeEnv/Code/conformance.json), run here via runConformance.
//
// Authored as a global script (no import/export) so it can be injected and called
// directly. Op codes are the camelCase CodeInfixOpType values (CodeAst.cs).

// ── AST (mirrors DeEnv/Code/CodeAst.cs, "type"-discriminated, camelCase) ──────────

type CodeStatement = CodeAssignment | CodeBlock | CodeVarDec | CodeFunction | CodeReturn | CodeCall | CodeIf;

type CodeValue = CodeInt | CodeText | CodeBool | CodeNull | CodeSymbol | CodeObject | CodeArray |
    CodeFunction | CodeTag | CodeInfixOp | CodeCall | CodeAssignment;

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
interface CodeAssignment { type: "assign"; target: CodeValue; value: CodeValue; }
interface CodeBlock { type: "block"; statements: CodeStatement[]; }
interface CodeVarDec { type: "varDec"; name: string; value: CodeValue | null; }
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
    ExecInt | ExecBool | ExecText | ExecNull | ExecNothing;
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
interface ExecFunction { type: "fn"; fn: CodeFunction; scope: ExecScope; }
interface ExecSysFunction { type: "sysFn"; fn(args: ExecValue[]): ExecValue; }
interface ExecTag { type: "tag"; name: string; attributes: { [name: string]: ExecResult }; children: ExecTagChild[]; key?: number; }

// isTop marks a persistent top-level scope (the framework system scope, or the app scope)
// whose writable vars are reactive — read in a computation they are deps, assigned they
// invalidate the memo cache. Transient local scopes (fn calls, blocks, foreach) leave it unset.
interface ExecScope { items: { [name: string]: ExecScopeItem }; parent: ExecScope | null; isTop?: boolean; }
interface ExecScopeItem { value: ExecValue; isReadOnly: boolean; }
interface ExecContext { lastId: LastId; }
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
        case "fn": executeFunction(statement, scope); return null;
        case "return": return executeValue(statement.value, scope, context).value;
        case "call": executeCall(statement, scope, context); return null;
        case "if": return executeIf(statement, scope, context);
        default: throw new Error("NotImplementedException");
    }
}

function executeIf(codeIf: CodeIf, scope: ExecScope, context: ExecContext): ExecValue | null {
    const condition = executeValue(codeIf.condition, scope, context).value;
    if (condition.type !== "bool") throw new Error("Result of if condition is not boolean.");
    const code = condition.value ? codeIf.body : codeIf.elseBody;
    return code == null ? null : executeStatement(code, scope, context);
}

function executeFunction(fun: CodeFunction, scope: ExecScope): ExecFunction {
    const fn: ExecFunction = { type: "fn", fn: fun, scope };
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
    for (const statement of block.statements) {
        const value = executeStatement(statement, innerScope, context);
        if (value != null) return value;
    }
    return null;
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
        case "fn": return { value: executeFunction(value, scope) };
        // A tag in VALUE position whose name resolves to a function is a component (a root/returned
        // component) — run it slot-keyed and yield its view value; otherwise it's an HTML element.
        case "tag": {
            const component = tryResolveComponent(value.name, scope);
            return { value: component ? executeComponentValue(value, component, scope, context) : executeTag(value, scope, context) };
        }
        case "infixOp": return executeInfixOp(value, scope, context);
        // sys.field(obj, name) is a bindable lvalue (two-way binding needs its setValue), so it
        // is resolved here rather than through executeCall (which drops setValue). Its callee is
        // a `sys.field` member access, so recognize the sys-rooted callee (not a bare symbol).
        case "call":
            if (sysBuiltinName(value.fn) === "field") return fieldResult(value, scope, context);
            return { value: executeCall(value, scope, context) };
        case "symbol": return executeSymbol(value, scope);
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

function executeSymbol(codeSymbol: CodeSymbol, scope: ExecScope): ExecResult {
    const itemScope = findScope(codeSymbol, scope);
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

const collectionMethods = ["add", "remove", "setEntry", "where", "orderBy", "any"];

// ── memoization cache (Stage 4) ────────────────────────────────────────────────────
// Mirrors the server (DeEnv/Code/MemoCache.cs). Computation boundaries (user-fn calls,
// where/orderBy) memoize by the same (function, args) key. The client reuses a fresh
// result, recomputes a stale one when its deps are present, and invalidates by
// dependency on each mutation. memoCache is null when codeExec runs standalone
// (conformance) → no caching, just compute.

interface CacheDeps { props: { obj: number; prop: string }[]; members: number[]; vars: string[]; }
interface ClientCacheEntry { result: ExecValue; deps: CacheDeps; stale: boolean; }

let memoCache: Map<string, ClientCacheEntry> | null = null;
const depStack: CacheDeps[] = [];

// The render-tree slot path (twin of ExecContext.SlotPath) — executeTagChildren pushes each
// child's static AST index so a tag-invoked component keys its run-once setup on its render-tree
// position, not its argument identities. Module-level like depStack; balanced push/pop returns it
// to empty between renders, and the render entry (renderUi) resets it defensively.
const slotPath: string[] = [];
function resetSlotPath(): void { slotPath.length = 0; }

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

function setMemoCache(cache: Map<string, ClientCacheEntry> | null): void { memoCache = cache; }

function recordProp(objId: number, prop: string): void {
    if (depStack.length > 0) depStack[depStack.length - 1].props.push({ obj: objId, prop });
}
function recordMember(arrId: number): void {
    if (depStack.length > 0) depStack[depStack.length - 1].members.push(arrId);
}
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
        return existing.result;
    }
    const deps: CacheDeps = { props: [], members: [], vars: [] };
    const lastIdBefore = context.lastId.value;
    depStack.push(deps);
    try {
        const result = compute();
        // An identity-creating computation — its result is a transient OBJECT minted
        // inside it (a `getNewX()` factory) — is not pure: caching it would hand every
        // caller the same mutable instance. (A derived array stays cacheable.)
        if (!(result.type === "object" && result.id < 0 && result.id < lastIdBefore))
            memoCache.set(key, { result, deps, stale: false });
        return result;
    } catch (e) {
        if (e instanceof Error && e.message === "Value not available") {
            // A dependency the server never shipped: ask the server either way, and
            // show the stale result (if any) until the refetch lands.
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

    if (left.type !== "object") throw new Error(`Cannot read '${right.name}' on a non-object.`);
    const value = left.props[right.name];
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
    const value = obj.props[name];
    if (value == null) throw new Error("Value not available");
    recordProp(obj.id, name);
    return {
        value,
        // Autosave: a bound edit persists immediately (like static `obj.member`), so the
        // generic form needs no Save button — consistent with the reference picker and
        // the reactive code pages.
        setValue: p => {
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

// clone(obj): a fresh object with the source's SCALAR props copied (a new draft from a
// type's blank template — a generic component's create-new state).
function execClone(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const obj = executeValue(codeCall.params[0], scope, context).value;
    if (obj.type !== "object") throw new Error("clone() expects an object.");
    const props: { [name: string]: ExecValue } = {};
    for (const [name, v] of Object.entries(obj.props))
        if (v.type === "int" || v.type === "text" || v.type === "bool" || v.type === "null") props[name] = v;
    return { type: "object", props, id: --context.lastId.value };
}

// extent(typeName): the reference picker's candidates — all objects of a type. Memoized
// like where/orderBy; the server shipped the displayed list and the client reuses it. No
// store on the client, so a cache miss/stale throws "Value not available", which the
// memoize wrapper turns into a refetch (the same hidden-dependency path).
function execExtent(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const v = executeValue(codeCall.params[0], scope, context).value;
    if (v.type !== "text") throw new Error("extent() expects a text type name.");
    return memoize("extent:" + v.value, context, () => { throw new Error("Value not available"); });
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
    obj.props[prop] = value;
    invalidateProp(obj.id, prop);
    if (obj.id > 0) referenceChange(obj, prop, value, before);
    return { type: "nothing" };
}

// The schema object crosses the wire as its ID — the server reads the object's subtree from the
// caller's store and projects it (no object-graph serialization). The designer passes `db`, the
// root object (id 1). A non-object schema has no id → id 0, which the server rejects.
function schemaIdArg(schema: ExecValue): ExecValue {
    return { type: "int", value: schema.type === "object" ? schema.id : 0 };
}

// sys.publish(schema, targetId): a SERVER-ONLY host action — the M4 schema export runs server-side,
// projecting the passed SCHEMA object onto an EXISTING target instance. The client stages NOTHING
// in the data model (no obj.props mutation, no invalidateProp — mirrors execSetRef minus the local
// mutation); it only fires the hostAction send-hook (schema as its id + the target id), which ws.ts
// sends as the `hostAction` WS op. The server is authoritative: it alone runs the effect, and an
// error reply surfaces as a user-visible lastError. Returns nothing; SSR/refetch no-ops it.
function execPublish(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const schema = executeValue(codeCall.params[0], scope, context).value;
    const targetId = executeValue(codeCall.params[1], scope, context).value;
    sendHostAction("publish", [schemaIdArg(schema), targetId]);
    return { type: "nothing" };
}

// sys.create(schema, name, appPort, infraPort): a SERVER-ONLY host action — project the passed SCHEMA
// object into a NEW kernel instance with the given display label on the given ports (the sibling of
// publish: publish replaces an existing instance, create spawns a new one). Like execPublish it stages
// NOTHING and only fires the hostAction send-hook. Returns nothing; SSR/refetch no-ops it.
function execCreate(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const schema = executeValue(codeCall.params[0], scope, context).value;
    const name = executeValue(codeCall.params[1], scope, context).value;
    const appPort = executeValue(codeCall.params[2], scope, context).value;
    const infraPort = executeValue(codeCall.params[3], scope, context).value;
    sendHostAction("create", [schemaIdArg(schema), name, appPort, infraPort]);
    return { type: "nothing" };
}

// sys.rename(id, name): a SERVER-ONLY host action — update the display label of an existing kernel
// instance. The id is a bare int (NOT a schema object). Stages NOTHING; only fires the hostAction
// send-hook. Returns nothing; SSR/refetch no-ops it.
function execRename(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const id = executeValue(codeCall.params[0], scope, context).value;
    const name = executeValue(codeCall.params[1], scope, context).value;
    sendHostAction("rename", [id, name]);
    return { type: "nothing" };
}

// sys.cloneInstance(sourceId, appPort, infraPort): a SERVER-ONLY host action — copy an existing
// instance's app document AND data into a NEW instance on the given ports (the data-carrying sibling
// of create: create projects a fresh design, clone copies a live one). The source is named by its
// instance id (a bare int, NOT a schema object — no schemaIdArg). Stages NOTHING; only fires the
// hostAction send-hook (the source id + the two ports). Returns nothing; SSR/refetch no-ops it.
function execCloneInstance(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const sourceId = executeValue(codeCall.params[0], scope, context).value;
    const appPort = executeValue(codeCall.params[1], scope, context).value;
    const infraPort = executeValue(codeCall.params[2], scope, context).value;
    sendHostAction("cloneInstance", [sourceId, appPort, infraPort]);
    return { type: "nothing" };
}

// sys.delete(targetId): a SERVER-ONLY host action — remove an existing kernel instance, named by
// its instance id (a bare int, NOT a schema object). Stages NOTHING; only fires the hostAction
// send-hook (the target id). Returns nothing; SSR/refetch no-ops it.
function execDelete(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const targetId = executeValue(codeCall.params[0], scope, context).value;
    sendHostAction("delete", [targetId]);
    return { type: "nothing" };
}

// sys.setDesign(schema, targetId): a SERVER-ONLY host action — the IDE's "Apply". Record (on the
// target's registry entry) that it now runs the passed design AND deploy it (publish + the registry
// write that makes the reference explicit). Like execPublish it stages NOTHING and only fires the
// hostAction send-hook (the design as its id + the target id). Returns nothing; SSR/refetch no-ops it.
function execSetDesign(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const schema = executeValue(codeCall.params[0], scope, context).value;
    const targetId = executeValue(codeCall.params[1], scope, context).value;
    sendHostAction("setDesign", [schemaIdArg(schema), targetId]);
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
    return executeBlock(fn.fn.body, callScope, context) ?? { type: "nothing" };
}

function addToCollection(arr: ExecArray, value: ExecValue, context: ExecContext): void {
    // A set member is keyed by its object identity; other kinds get a transient key.
    const key = arr.kind === "set" && value.type === "object" ? value.id : --context.lastId.value;
    const item: ExecArrayItem = { key, value };
    arr.items.push(item);
    invalidateMember(arr.id);
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
        case "null": return null;
        default: throw new Error("NotImplementedException");
    }
}

function executeInfixOpBasic(codeInfixOp: CodeInfixOp, scope: ExecScope, context: ExecContext): ExecValue {
    const left = executeValue(codeInfixOp.left, scope, context).value;
    const right = executeValue(codeInfixOp.right, scope, context).value;
    const asInt = (v: ExecValue) => { if (v.type !== "int") throw new Error("Expected an int."); return v.value; };
    const asBool = (v: ExecValue) => { if (v.type !== "bool") throw new Error("Expected a bool."); return v.value; };
    switch (codeInfixOp.op) {
        case "add": return { type: "int", value: asInt(left) + asInt(right) };
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
        case "setRef": return execSetRef(codeCall, scope, context);
        case "publish": return execPublish(codeCall, scope, context);
        case "create": return execCreate(codeCall, scope, context);
        case "cloneInstance": return execCloneInstance(codeCall, scope, context);
        case "delete": return execDelete(codeCall, scope, context);
        case "rename": return execRename(codeCall, scope, context);
        case "setDesign": return execSetDesign(codeCall, scope, context);
        case "nest": return execNest(codeCall, scope, context);
        case "segment": return execSegment(codeCall, scope, context);
        case "toInt": return execToInt(codeCall, scope, context);
        case "id": return execId(codeCall, scope, context);
        case "clone": return execClone(codeCall, scope, context);
    }
    const fn = executeValue(codeCall.fn, scope, context).value;
    if (fn.type === "sysFn") return fn.fn(codeCall.params.map(p => executeValue(p, scope, context).value));
    if (fn.type !== "fn") throw new Error("Target of a call is not a function.");

    // Args evaluate in the caller's context (their deps are the caller's); the body is
    // memoized by (function id, arg identities), capturing its own deps.
    const argVals = codeCall.params.map(p => executeValue(p, scope, context).value);
    const closure = fn;
    return memoize(memoKey("fn:" + closure.fn.id, argVals), context, () => {
        const callScope: ExecScope = { parent: closure.scope, items: {} };
        for (let i = 0; i < argVals.length && i < closure.fn.params.length; i++)
            callScope.items[closure.fn.params[i].name] = { value: argVals[i], isReadOnly: true };
        return executeBlock(closure.fn.body, callScope, context) ?? { type: "nothing" };
    });
}

// ── tags (DOM-free; ui.ts renders the resulting ExecTag tree) ─────────────────────

function executeTag(codeTag: CodeTag, scope: ExecScope, context: ExecContext): ExecTag {
    const attributes: { [name: string]: ExecResult } = {};
    for (const attr of codeTag.attributes) attributes[attr.name] = executeValue(attr.value, scope, context);
    return { type: "tag", name: codeTag.name, attributes, children: executeTagChildren(codeTag.children, scope, context) };
}

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
    let view = memoize(slotKey, context, () => invokeFn(component, args, context));
    if (view.type === "fn") {
        const renderClosure = view;
        view = memoize(slotKey + ":view", context, () => invokeFn(renderClosure, [], context));
    }
    return view;
}

// Invoke a function value with already-evaluated args (twin of C# InvokeFunction) — a child
// scope of the closure's captured scope, params bound positionally, the body run directly
// (no memoization; executeComponent wraps the slot-keyed memo around this).
function invokeFn(fn: ExecFunction, args: ExecValue[], context: ExecContext): ExecValue {
    const callScope: ExecScope = { parent: fn.scope, items: {} };
    for (let i = 0; i < args.length && i < fn.fn.params.length; i++)
        callScope.items[fn.fn.params[i].name] = { value: args[i], isReadOnly: true };
    return executeBlock(fn.fn.body, callScope, context) ?? { type: "nothing" };
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
    // the effect. Stages nothing in the data model (no optimistic mutation to roll back).
    hostAction(action: string, args: ExecValue[]): void;
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
function sendHostAction(action: string, args: ExecValue[]): void {
    wsHooks?.hostAction(action, args);
}

// ── conformance entry point ───────────────────────────────────────────────────────

// Runs a conformance case (the exact JSON shape from conformance.json) and returns the scalar
// result as { kind, value }. A case is either a single `expr` (evaluated once) or the lifecycle
// protocol — optional `setup` statements run once into a retained scope, then `renders`
// value-exprs evaluated in order against that same scope+context, returning the LAST result. The
// C# side runs the identical protocol in ConformanceTests; any drift fails on one side or the other.
function runConformance(caseJson: string): string {
    const c = JSON.parse(caseJson) as { expr?: CodeValue; setup?: CodeStatement[]; renders?: CodeValue[] };
    // Memoize like the live client (and like the C# runner, whose Memo is always on): a
    // where/orderBy memo-key collision — and the component slot identity — only surface when the
    // cache is active, so the conformance suite must exercise the cached path to prove both twins agree.
    setMemoCache(new Map());
    resetSlotPath();
    const scope: ExecScope = { items: {}, parent: null };
    const context: ExecContext = { lastId: { value: 0 } };
    let result: ExecValue = { type: "nothing" };
    if (c.renders) {
        for (const stmt of c.setup ?? []) executeStatement(stmt, scope, context);
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
        case "nothing": return JSON.stringify({ kind: "nothing", value: null });
        default: throw new Error(`Non-scalar conformance result '${result.type}'.`);
    }
}

(globalThis as any).runConformance = runConformance;
