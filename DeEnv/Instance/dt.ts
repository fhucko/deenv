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
interface ServerDtObject { props: { [name: string]: DtValue }; }
interface ServerDtArray { kind: "set" | "dict" | "list"; elementTypeName?: string; sourcePath?: string; items: { key: number; value: DtValue }[]; }

interface ServerDtState {
    leaves: { objects: { [id: number]: ServerDtObject }; arrays: { [id: number]: ServerDtArray } };
    scope: { [key: string]: DtScopeValue };
    cache: ServerCacheEntry[];
}

interface ServerCacheEntry { key: string; result: DtValue; deps: CacheDeps; }

interface AppState {
    objects: { [id: number]: ExecObject };
    arrays: { [id: number]: ExecArray };
    scope: ExecScope;
    localToServerIds: { [localId: number]: number };
    serverToLocalIds: { [serverId: number]: number };
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
    }

    for (const [idText, dtArr] of Object.entries(dtState.leaves.arrays)) {
        const id = Number(idText);
        const arr = arrays[id] ?? (arrays[id] = { type: "array", id, kind: dtArr.kind, items: [], elementTypeName: dtArr.elementTypeName });
        arr.kind = dtArr.kind;
        arr.elementTypeName = dtArr.elementTypeName;
        arr.sourcePath = dtArr.sourcePath;
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
        // A var currently holding a client-minted draft (a transient object being
        // edited in a form) is client-held state: a server re-render recomputes its
        // initializer to a fresh empty draft, which must not clobber the user's input.
        const existing = scope.items[key];
        if (existing && existing.value.type === "object" && existing.value.id < 0) continue;
        scope.items[key] = { isReadOnly: value.isReadOnly, value: fromDtValue(value.value) };
    }

    // The memoized computation results + dependency refs, for reuse and invalidation.
    for (const entry of dtState.cache ?? [])
        uiStatic.cache.set(entry.key, { result: fromDtValue(entry.result), deps: entry.deps, stale: false });
}
