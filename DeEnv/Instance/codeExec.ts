// The client-side twin of the C# Code interpreter (DeEnv/Code/CodeExecutor.cs),
// ported from the app14 prototype and adapted to the camelCase wire format. This is
// the evaluation core only — DOM rendering (ui.ts) and the WebSocket/data-transfer
// protocol (Stage 4) layer on top. The two interpreters are kept in lockstep by the
// shared conformance suite (DeEnv/Code/conformance.json), run here via runConformance.
//
// Authored as a global script (no import/export) so it can be injected and called
// directly. Op codes are the camelCase CodeInfixOpType values (see enums.ts).

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
interface CodeAssignment { type: "assign"; target: CodeSymbol; value: CodeValue; }
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
interface ExecObject { type: "object"; props: { [name: string]: ExecValue }; id: number; isInDb: boolean; }
interface ExecArray { type: "array"; items: ExecArrayItem[]; id: number; isInDb: boolean; }
interface ExecArrayItem { id: number; value: ExecValue; }
interface ExecFunction { type: "fn"; fn: CodeFunction; scope: ExecScope; }
interface ExecSysFunction { type: "sysFn"; fn(args: ExecValue[]): ExecValue; }
interface ExecTag { type: "tag"; name: string; attributes: { [name: string]: ExecResult }; children: ExecTagChild[]; key?: number; }

interface ExecScope { items: { [name: string]: ExecScopeItem }; parent: ExecScope | null; }
interface ExecScopeItem { value: ExecValue; isReadOnly: boolean; }
interface ExecContext { lastId: LastId; }
interface LastId { value: number; }

// ── statements ────────────────────────────────────────────────────────────────────

function executeStatement(statement: CodeStatement, scope: ExecScope, context: ExecContext): ExecValue {
    switch (statement.type) {
        case "assign": executeAssignment(statement, scope, context); return { type: "nothing" };
        case "block": return executeBlock(statement, scope, context);
        case "varDec": executeVarDec(statement, scope, context); return { type: "nothing" };
        case "fn": executeFunction(statement, scope); return { type: "nothing" };
        case "return": return executeValue(statement.value, scope, context).value;
        case "call": executeCall(statement, scope, context); return { type: "nothing" };
        case "if": return executeIf(statement, scope, context);
        default: throw new Error("NotImplementedException");
    }
}

function executeIf(codeIf: CodeIf, scope: ExecScope, context: ExecContext): ExecValue {
    const condition = executeValue(codeIf.condition, scope, context).value;
    if (condition.type !== "bool") throw new Error("Result of if condition is not boolean.");
    const code = condition.value ? codeIf.body : codeIf.elseBody;
    return code == null ? { type: "nothing" } : executeStatement(code, scope, context);
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

function executeBlock(block: CodeBlock, scope: ExecScope, context: ExecContext): ExecValue {
    const innerScope: ExecScope = { parent: scope, items: {} };
    for (const statement of block.statements) {
        const value = executeStatement(statement, innerScope, context);
        if (value.type !== "nothing") return value;
    }
    return { type: "nothing" };
}

function executeAssignment(assignment: CodeAssignment, scope: ExecScope, context: ExecContext): ExecValue {
    const itemScope = findScope(assignment.target, scope);
    const item = itemScope.items[assignment.target.name];
    if (item.isReadOnly) throw new Error(`Symbol ${assignment.target.name} is read only`);
    item.value = executeValue(assignment.value, scope, context).value;
    return item.value;
}

// ── values ──────────────────────────────────────────────────────────────────────

function executeValue(value: CodeValue, scope: ExecScope, context: ExecContext): ExecResult {
    switch (value.type) {
        case "int": return { value: { type: "int", value: value.value } };
        case "text": return { value: { type: "text", value: value.value } };
        case "bool": return { value: { type: "bool", value: value.value } };
        case "null": return { value: { type: "null" } };
        case "fn": return { value: executeFunction(value, scope) };
        case "call": return { value: executeCall(value, scope, context) };
        case "tag": return { value: executeTag(value, scope, context) };
        case "infixOp": return executeInfixOp(value, scope, context);
        case "symbol": return executeSymbol(value, scope);
        case "object": return { value: executeObject(value, scope, context) };
        case "array": return { value: executeArray(value, scope, context) };
        case "assign": return { value: executeAssignment(value, scope, context) };
        default: throw new Error("NotImplementedException");
    }
}

function executeArray(codeArray: CodeArray, scope: ExecScope, context: ExecContext): ExecArray {
    const items: ExecArrayItem[] = codeArray.items.map(p => ({ id: --context.lastId.value, value: executeValue(p, scope, context).value }));
    return { type: "array", items, id: --context.lastId.value, isInDb: false };
}

function executeObject(codeObject: CodeObject, scope: ExecScope, context: ExecContext): ExecObject {
    const props: { [name: string]: ExecValue } = {};
    for (const prop of codeObject.props) props[prop.name] = executeValue(prop.value, scope, context).value;
    return { type: "object", props, id: --context.lastId.value, isInDb: false };
}

function executeSymbol(codeSymbol: CodeSymbol, scope: ExecScope): ExecResult {
    const itemScope = findScope(codeSymbol, scope);
    return {
        value: itemScope.items[codeSymbol.name].value,
        setValue: p => { itemScope.items[codeSymbol.name].value = p; },
    };
}

const collectionMethods = ["add", "remove", "where", "orderBy"];

// ── memoization cache (Stage 4) ────────────────────────────────────────────────────
// Mirrors the server (DeEnv/Code/MemoCache.cs). Computation boundaries (user-fn calls,
// where/orderBy) memoize by the same (function, args) key. The client reuses a fresh
// result, recomputes a stale one when its deps are present, and invalidates by
// dependency on each mutation. memoCache is null when codeExec runs standalone
// (conformance) → no caching, just compute.

interface CacheDeps { props: { obj: number; prop: string }[]; members: number[]; }
interface ClientCacheEntry { result: ExecValue; deps: CacheDeps; stale: boolean; }

let memoCache: Map<string, ClientCacheEntry> | null = null;
const depStack: CacheDeps[] = [];

function setMemoCache(cache: Map<string, ClientCacheEntry>): void { memoCache = cache; }

function recordProp(objId: number, prop: string): void {
    if (depStack.length > 0) depStack[depStack.length - 1].props.push({ obj: objId, prop });
}
function recordMember(arrId: number): void {
    if (depStack.length > 0) depStack[depStack.length - 1].members.push(arrId);
}
function mergeDeps(into: CacheDeps, from: CacheDeps): void {
    into.props.push(...from.props);
    into.members.push(...from.members);
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

function memoize(key: string, compute: () => ExecValue): ExecValue {
    if (memoCache == null) return compute();
    const existing = memoCache.get(key);
    if (existing && !existing.stale) {
        if (depStack.length > 0) mergeDeps(depStack[depStack.length - 1], existing.deps);
        return existing.result;
    }
    const deps: CacheDeps = { props: [], members: [] };
    depStack.push(deps);
    try {
        const result = compute();
        memoCache.set(key, { result, deps, stale: false });
        return result;
    } catch (e) {
        // A hidden dependency: can't recompute on the client. Reuse the stale result.
        // (Stage 4b refetches from the server here.) Re-throw anything else.
        if (existing != null && e instanceof Error && e.message === "Value not available") return existing.result;
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
            left.props[right.name] = p;
            invalidateProp(left.id, right.name);
            if (left.isInDb) { propValueChange(left.id, right.name, p); setIsDb(p); }
        },
    };
}

function collectionSysFunction(arr: ExecArray, method: string, context: ExecContext): ExecSysFunction {
    switch (method) {
        case "add": return { type: "sysFn", fn: args => { addToCollection(arr, args[0], context); return { type: "nothing" }; } };
        case "remove": return { type: "sysFn", fn: args => { removeFromCollection(arr, args[0]); return { type: "nothing" }; } };
        case "where": return { type: "sysFn", fn: args => {
            const lambda = asLambda(args[0]);
            return memoize(`where:a${arr.id}:fn${lambda.fn.id}`, () => whereCollection(arr, lambda, context));
        } };
        case "orderBy": return { type: "sysFn", fn: args => {
            const lambda = asLambda(args[0]);
            return memoize(`orderBy:a${arr.id}:fn${lambda.fn.id}`, () => orderByCollection(arr, lambda, context));
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
    return executeBlock(fn.fn.body, callScope, context);
}

function addToCollection(arr: ExecArray, value: ExecValue, context: ExecContext): void {
    const item: ExecArrayItem = { id: --context.lastId.value, value };
    arr.items.push(item);
    invalidateMember(arr.id);
    if (arr.isInDb) { sendArrayItemAdd(arr.id, item.id, value); setIsDb(item.value); }
}

function removeFromCollection(arr: ExecArray, value: ExecValue): void {
    const index = arr.items.findIndex(i => i.value === value
        || (i.value.type === "object" && value.type === "object" && i.value.id === value.id));
    if (index < 0) return;
    const item = arr.items.splice(index, 1)[0];
    invalidateMember(arr.id);
    if (arr.isInDb) sendArrayItemRemove(arr.id, item.id);
}

function whereCollection(arr: ExecArray, predicate: ExecFunction, context: ExecContext): ExecArray {
    recordMember(arr.id);
    const items = arr.items.filter(item => {
        const r = invokeLambda(predicate, item.value, context);
        return r.type === "bool" && r.value;
    });
    return { type: "array", items, id: --context.lastId.value, isInDb: false };
}

function orderByCollection(arr: ExecArray, keySelector: ExecFunction, context: ExecContext): ExecArray {
    recordMember(arr.id);
    const items = arr.items
        .map(item => ({ item, key: invokeLambda(keySelector, item.value, context) }))
        .sort((a, b) => compareExec(a.key, b.key))
        .map(p => p.item);
    return { type: "array", items, id: --context.lastId.value, isInDb: false };
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

function executeCall(codeCall: CodeCall, scope: ExecScope, context: ExecContext): ExecValue {
    const fn = executeValue(codeCall.fn, scope, context).value;
    if (fn.type === "sysFn") return fn.fn(codeCall.params.map(p => executeValue(p, scope, context).value));
    if (fn.type !== "fn") throw new Error("Target of a call is not a function.");

    // Args evaluate in the caller's context (their deps are the caller's); the body is
    // memoized by (function id, arg identities), capturing its own deps.
    const argVals = codeCall.params.map(p => executeValue(p, scope, context).value);
    const closure = fn;
    return memoize(memoKey("fn:" + closure.fn.id, argVals), () => {
        const callScope: ExecScope = { parent: closure.scope, items: {} };
        for (let i = 0; i < argVals.length; i++)
            callScope.items[closure.fn.params[i].name] = { value: argVals[i], isReadOnly: true };
        return executeBlock(closure.fn.body, callScope, context);
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
        case "tag": return [executeTag(child, scope, context)];
        case "foreach": return executeTagForEach(child, scope, context);
        case "if": return executeTagIf(child as CodeTagIf, scope, context);
        default: return [executeValue(child as CodeValue, scope, context).value];
    }
}

function executeTagChildren(body: CodeTagChild[], scope: ExecScope, context: ExecContext): ExecTagChild[] {
    const children: ExecTagChild[] = [];
    const innerScope: ExecScope = { parent: scope, items: {} };
    for (const child of body) children.push(...executeTagChild(child, innerScope, context));
    return children;
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
    const children: ExecTagChild[] = [];
    for (const item of array.items) {
        // Identity key for DOM reconciliation: the member object's intrinsic id, so a
        // row's element (and its input focus/state) moves with the object on reorder.
        const key = item.value.type === "object" ? item.value.id : item.id;
        const itemScope: ExecScope = { parent: scope, items: {} };
        itemScope.items[codeTagForEach.item.name] = { value: item.value, isReadOnly: true };
        const produced = executeTagChildren(codeTagForEach.body, itemScope, context);
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

function setIsDb(value: ExecValue): void {
    if (value.type === "object") {
        if (value.isInDb) return;
        value.isInDb = true;
        for (const prop of Object.values(value.props)) setIsDb(prop);
    } else if (value.type === "array") {
        if (value.isInDb) return;
        value.isInDb = true;
        for (const item of value.items) setIsDb(item.value);
    }
}

// WebSocket mutation sends — wired in Stage 4; no-ops here so local two-way binding
// and transient construction work without a server round-trip.
function propValueChange(_objectId: number, _propName: string, _value: ExecValue): void { }
function sendArrayItemAdd(_arrayId: number, _itemId: number, _value: ExecValue): void { }
function sendArrayItemRemove(_arrayId: number, _itemId: number): void { }

// ── conformance entry point ───────────────────────────────────────────────────────

// Evaluates a Code AST expression (the exact JSON shape from instance.schema.json /
// conformance.json) and returns the scalar result as { kind, value }. The C# side
// runs the same cases in ConformanceTests; any drift fails on one side or the other.
function runConformance(exprJson: string): string {
    const expr = JSON.parse(exprJson) as CodeValue;
    const result = executeValue(expr, { items: {}, parent: null }, { lastId: { value: 0 } }).value;
    switch (result.type) {
        case "int": return JSON.stringify({ kind: "int", value: result.value });
        case "text": return JSON.stringify({ kind: "text", value: result.value });
        case "bool": return JSON.stringify({ kind: "bool", value: result.value });
        default: throw new Error(`Non-scalar conformance result '${result.type}'.`);
    }
}

(globalThis as any).runConformance = runConformance;
