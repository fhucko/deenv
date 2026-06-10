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
interface ServerDtObject { isInDb: boolean; props: { [name: string]: DtValue }; }
interface ServerDtArray { isInDb: boolean; items: { id: number; value: DtValue }[]; }

interface ServerDtState {
    objects: { [id: number]: ServerDtObject };
    arrays: { [id: number]: ServerDtArray };
    scope: { [key: string]: DtScopeValue };
}

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
                return objects[value.id] ?? (objects[value.id] = { type: "object", id: value.id, props: {}, isInDb: false });
            case "array":
                return arrays[value.id] ?? (arrays[value.id] = { type: "array", id: value.id, items: [], isInDb: false });
        }
    }

    for (const [idText, dtObj] of Object.entries(dtState.objects)) {
        const id = Number(idText);
        const obj = objects[id] ?? (objects[id] = { type: "object", id, props: {}, isInDb: dtObj.isInDb });
        obj.isInDb = dtObj.isInDb;
        for (const [name, value] of Object.entries(dtObj.props)) obj.props[name] = fromDtValue(value);
    }

    for (const [idText, dtArr] of Object.entries(dtState.arrays)) {
        const id = Number(idText);
        const arr = arrays[id] ?? (arrays[id] = { type: "array", id, items: [], isInDb: dtArr.isInDb });
        arr.isInDb = dtArr.isInDb;
        for (const item of dtArr.items)
            if (!arr.items.some(p => p.id === item.id)) arr.items.push({ id: item.id, value: fromDtValue(item.value) });
    }

    for (const [key, value] of Object.entries(dtState.scope))
        scope.items[key] = { isReadOnly: value.isReadOnly, value: fromDtValue(value.value) };
}
