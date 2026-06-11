// WebSocket transport for the code-owned UI (Stage 4b/5). Wires the codeExec mutation
// hooks (setWsHooks) to the server so a two-way write reaches storage. Global script,
// concatenated after codeExec/dt and before ui/init (see ClientScript.UiJs).
//
// Optimistic mutations are PROVISIONAL (Stage 5). Each one is applied locally first,
// journaled with what its undo needs (the 3-state model: server-data is the current
// state with the journal undone, client-after is the current state, and each entry
// holds its own client-before), and sent with a correlation id. The server's reply is
// authoritative: ok → the entry commits (drops); error → reverse-replay the journal
// back past the failed entry, drop it, and re-apply the rest.

let codeWs: WebSocket | null = null;
const codeWsOutbox: string[] = [];

// ── the change journal (pending optimistic mutations, in send order) ──────────────

interface JournalEntry {
    msgId: number;
    undo(): void;     // restore the captured before-state
    redo(): void;     // re-apply after a rollback of an earlier entry (recaptures before)
    onReject?(): void; // extra cleanup when this entry itself is the rejected one
}
const journal: JournalEntry[] = [];
let nextWsMsgId = 1;

// The reply for msgId was ok: the mutation is server-committed, the entry retires.
function commitJournal(msgId: number): void {
    const idx = journal.findIndex(e => e.msgId === msgId);
    if (idx >= 0) journal.splice(idx, 1);
}

// The reply for msgId was an error: the server refused the mutation. Undo every pending
// entry from the newest back to the failed one (restoring each captured before-value),
// drop the failed entry, then re-apply the survivors in order — they are still pending
// on the server and may yet be accepted. Their redo recaptures fresh before-values, so
// a later rollback stays correct.
function rollbackJournal(msgId: number, error: string): void {
    const idx = journal.findIndex(e => e.msgId === msgId);
    if (idx < 0) return;
    for (let i = journal.length - 1; i >= idx; i--) journal[i].undo();
    const [failed] = journal.splice(idx, 1);
    failed.onReject?.();
    for (let i = idx; i < journal.length; i++) journal[i].redo();
    uiStatic.lastError = error;
    console.error("Mutation rejected by the server:", error);
    renderUi();
}

// ── connection ────────────────────────────────────────────────────────────────────

// Set adds awaiting their real id: tempId (the negative client id) → the array it
// went into, so the reply can re-key item + object from negative to real.
const pendingAdds = new Map<number, number>();

function connectWs(): void {
    const proto = location.protocol === "https:" ? "wss:" : "ws:";
    codeWs = new WebSocket(`${proto}//${location.host}/ws`);
    codeWs.onopen = () => {
        // Claim the warm session minted at SSR before anything else; if this arrives
        // past the claim window the session is gone and refetches do a full re-render.
        codeWs!.send(JSON.stringify({ op: "hello", clientId: uiStatic.clientId }));
        for (const m of codeWsOutbox.splice(0)) codeWs!.send(m);
    };
    codeWs.onmessage = ev => onWsMessage(JSON.parse(ev.data));

    setWsHooks({
        propChange: (obj, prop, value, before) => {
            const msgId = nextWsMsgId++;
            journal.push({
                msgId,
                undo: () => { obj.props[prop] = before; invalidateProp(obj.id, prop); },
                redo: () => { before = obj.props[prop]; obj.props[prop] = value; invalidateProp(obj.id, prop); },
            });
            wsSend({ op: "objectPropChange", id: msgId, clientId: uiStatic.clientId,
                objectId: obj.id, prop, value: scalarOf(value) });
        },
        arrayAdd: (arr, item, typeName) => {
            const msgId = nextWsMsgId++;
            pendingAdds.set(item.key, arr.id);
            journal.push({
                msgId,
                undo: () => { const i = arr.items.indexOf(item); if (i >= 0) arr.items.splice(i, 1); invalidateMember(arr.id); },
                redo: () => { arr.items.push(item); invalidateMember(arr.id); },
                onReject: () => pendingAdds.delete(item.key),
            });
            wsSend({ op: "arrayAdd", id: msgId, clientId: uiStatic.clientId,
                setId: arr.id, tempId: item.key, typeName, value: objectOf(item.value) });
        },
        arrayRemove: (arr, item, index) => {
            const msgId = nextWsMsgId++;
            journal.push({
                msgId,
                undo: () => { arr.items.splice(Math.min(index, arr.items.length), 0, item); invalidateMember(arr.id); },
                redo: () => { const i = arr.items.indexOf(item); if (i >= 0) arr.items.splice(i, 1); invalidateMember(arr.id); },
            });
            wsSend({ op: "arrayRemove", id: msgId, clientId: uiStatic.clientId,
                setId: arr.id, objectId: item.key });
        },
    });
}

function onWsMessage(msg: { op?: string; id?: number; tempId?: number; newId?: number;
                            state?: ServerDtState; error?: string }): void {
    // Correlated accept/reject first: an error rolls the journal back, an ok commits.
    if (typeof msg.id === "number") {
        if (msg.error != null) { rollbackJournal(msg.id, msg.error); return; }
        commitJournal(msg.id);
    }

    if (msg.op === "arrayAdd" && typeof msg.tempId === "number" && typeof msg.newId === "number") {
        const arrayId = pendingAdds.get(msg.tempId);
        if (arrayId != null) { pendingAdds.delete(msg.tempId); remapAddedId(arrayId, msg.tempId, msg.newId); }
    } else if (msg.op === "refetch" && msg.state != null) {
        refetchInFlight = false;
        mergeState(msg.state);
        for (const e of uiStatic.cache.values()) e.stale = false; // server truth: nothing stale now
        renderUi();
    }
}

// A mutation can leave a cache entry stale that the client cannot recompute — its
// dependency was never shipped (private), or its function is server-only. When the
// render finishes with such an entry still stale, re-ask the server (by clientId), which
// recomputes over the warm graph it kept for this client and returns authoritative state.
// (Entries whose deps are all present were already recomputed locally with no round-trip
// — the first-paint invariant.)
let refetchInFlight = false;

function maybeRefetch(): void {
    if (refetchInFlight || !hasStaleEntry()) return;
    refetchInFlight = true;
    wsSend({ op: "refetch", clientId: uiStatic.clientId, path: location.pathname, vars: sessionVars() });
}

function hasStaleEntry(): boolean {
    for (const e of uiStatic.cache.values()) if (e.stale) return true;
    return false;
}

// The client-held scalar session vars (path, transient inputs, …) the server re-renders
// with. Computed collections are not the client's to push, so only scalars go.
function sessionVars(): { [name: string]: object } {
    const out: { [name: string]: object } = {};
    for (const [name, item] of Object.entries(uiStatic.state.scope.items)) {
        const v = item.value;
        if (v.type === "int" || v.type === "bool" || v.type === "text") out[name] = scalarOf(v);
    }
    return out;
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
