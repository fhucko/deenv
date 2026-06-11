// WebSocket transport for the code-owned UI (Stage 4b). Wires the codeExec mutation
// hooks (setWsHooks) to the server so a two-way write reaches storage. Global script,
// concatenated after codeExec/dt and before ui/init (see ClientScript.UiJs).
//
// objectPropChange / arrayAdd / arrayRemove persist a mutation; the client already
// applied it optimistically, so these are fire-and-forget — except arrayAdd, whose
// reply echoes the real extent id so the client re-keys its transient (negative-id)
// copy. Surgical deltas/refetch are a later slice.

let codeWs: WebSocket | null = null;
const codeWsOutbox: string[] = [];

// Set adds awaiting their real id: tempId (the negative client id) → the array it
// went into, so the reply can re-key item + object from negative to real.
const pendingAdds = new Map<number, number>();

function connectWs(): void {
    const proto = location.protocol === "https:" ? "wss:" : "ws:";
    codeWs = new WebSocket(`${proto}//${location.host}/ws`);
    codeWs.onopen = () => { for (const m of codeWsOutbox.splice(0)) codeWs!.send(m); };
    codeWs.onmessage = ev => onWsMessage(JSON.parse(ev.data));

    setWsHooks({
        propChange: (objectId, prop, value) =>
            wsSend({ op: "objectPropChange", objectId, prop, value: scalarOf(value) }),
        arrayAdd: (arrayId, tempKey, typeName, value) => {
            pendingAdds.set(tempKey, arrayId);
            wsSend({ op: "arrayAdd", setId: arrayId, tempId: tempKey, typeName, value: objectOf(value) });
        },
        arrayRemove: (arrayId, objectId) =>
            wsSend({ op: "arrayRemove", setId: arrayId, objectId }),
    });
}

function onWsMessage(msg: { op?: string; tempId?: number; id?: number }): void {
    if (msg.op === "arrayAdd" && typeof msg.tempId === "number" && typeof msg.id === "number") {
        const arrayId = pendingAdds.get(msg.tempId);
        if (arrayId != null) { pendingAdds.delete(msg.tempId); remapAddedId(arrayId, msg.tempId, msg.id); }
    }
}

// Re-key a just-persisted set member from its transient negative id to the real extent
// id: the array item's key, the member object's id, and the id maps. A positive id now
// means future prop writes on it persist. A re-render refreshes the row's data-key.
function remapAddedId(arrayId: number, tempId: number, realId: number): void {
    const arr = uiStatic.state.arrays[arrayId];
    const item = arr?.items.find(i => i.key === tempId);
    if (item) {
        item.key = realId;
        if (item.value.type === "object") item.value.id = realId;
    }
    uiStatic.state.localToServerIds[tempId] = realId;
    uiStatic.state.serverToLocalIds[realId] = tempId;
    renderUi();
}

// Send now if the socket is open; otherwise queue and flush on open.
function wsSend(msg: object): void {
    const text = JSON.stringify(msg);
    if (codeWs && codeWs.readyState === WebSocket.OPEN) codeWs.send(text);
    else codeWsOutbox.push(text);
}

// A scalar ExecValue as the wire { type, value } the server expects.
function scalarOf(value: ExecValue): object {
    switch (value.type) {
        case "int": case "bool": case "text": return { type: value.type, value: value.value };
        default: return { type: "null" };
    }
}

// A new object's scalar props for the server to persist: { props: { name: leaf } }.
function objectOf(value: ExecValue): object {
    const props: { [name: string]: object } = {};
    if (value.type === "object")
        for (const [name, v] of Object.entries(value.props))
            if (v.type === "int" || v.type === "bool" || v.type === "text") props[name] = scalarOf(v);
    return { props };
}
