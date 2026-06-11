// WebSocket transport for the code-owned UI (Stage 4b). Wires the codeExec mutation
// hooks (setWsHooks) to the server so a two-way write reaches storage. Global script,
// concatenated after codeExec/dt and before ui/init (see ClientScript.UiJs).
//
// Slice 1: objectPropChange persistence — fire-and-forget. The client already applied
// the change optimistically; the server persists it so a reload reflects it. Set
// add/remove (with negative→real id remapping) and surgical deltas are later slices,
// so their hooks stay local no-ops for now.

let codeWs: WebSocket | null = null;
const codeWsOutbox: string[] = [];

function connectWs(): void {
    const proto = location.protocol === "https:" ? "wss:" : "ws:";
    codeWs = new WebSocket(`${proto}//${location.host}/ws`);
    codeWs.onopen = () => { for (const m of codeWsOutbox.splice(0)) codeWs!.send(m); };

    setWsHooks({
        propChange: (objectId, prop, value) =>
            wsSend({ op: "objectPropChange", objectId, prop, value: scalarOf(value) }),
        arrayAdd: () => { /* Slice 2: add + id remap */ },
        arrayRemove: () => { /* Slice 2 */ },
    });
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
