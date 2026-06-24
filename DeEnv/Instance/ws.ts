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

// Where /ws and /js live (set by the SSR page): the asset AUTHORITY (host:port — a kernel-level
// shared port, decoupled from the per-instance app addressing) and the instance's mount BASE
// (`/apps/<name>`, or "/" root-mounted). The WS URL is `<proto>//<assetAuthority><base>/ws`. Empty
// authority → a same-origin, base-relative URL (a reverse-proxied / domain-root deployment).
declare const initAssetAuthority: string;
declare const initBase: string;
// The instance display name (the kernel registry `app` label), injected by the SSR page. The generic-UI
// breadcrumb/title uses it (humanized) as the ROOT label on a client-side render — see ui.ts rootLabel.
declare const initAppName: string;

// The mount base as a URL PREFIX: "" when root-mounted ("/"), else the base verbatim ("/apps/todo").
// Prepended to the asset paths (/ws, /js) and — in ui.ts/init.ts — to the app's root-relative links.
function basePrefix(): string {
    return initBase === "/" ? "" : initBase;
}

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
// a later rollback stays correct. Returns false when no journal entry matches (the error
// is correlated to a non-journaled send, e.g. a host action) so the caller can still
// surface it without a journal replay.
function rollbackJournal(msgId: number, error: string): boolean {
    const idx = journal.findIndex(e => e.msgId === msgId);
    if (idx < 0) return false;
    for (let i = journal.length - 1; i >= idx; i--) journal[i].undo();
    const [failed] = journal.splice(idx, 1);
    failed.onReject?.();
    for (let i = idx; i < journal.length; i++) journal[i].redo();
    uiStatic.lastError = error;
    console.error("Mutation rejected by the server:", error);
    renderUi();
    return true;
}

// ── connection ────────────────────────────────────────────────────────────────────

// Set adds awaiting their real id: tempId (the negative client id) → the array it
// went into, so the reply can re-key item + object from negative to real.
const pendingAdds = new Map<number, number>();

let wsRetryDelay = 1000;

// ── full-readiness signal (`data-ready`) — INTERIM ───────────────────────────────────
//
// `data-hydrated` (init.ts) marks the FIRST CLIENT RENDER done, but that fires BEFORE the
// WebSocket has opened — so a mutation made between hydration and the socket settling rides
// the connecting-window outbox (flushed only on open) instead of an established, claimed
// connection. Under a slow/contended connect that edit can be delayed past a caller's window
// (or lost to an early disconnect+resync), i.e. an edit made "before the page is fully loaded"
// goes missing. `data-ready` is the stricter signal: it is set ONLY once the page has FULLY
// settled — hydration done AND the socket open AND the session-claim `hello` acknowledged AND
// any connect-time refetch applied — so an interaction gated on it always acts on an
// established, server-acknowledged connection.
//
// INTERIM: this WAITS for readiness rather than making a not-ready mutation survive. The proper
// fix is offline-resilient mutations (a durable outbox that delivers across a not-ready/dropped
// connection regardless of timing — see the offline-support direction); until then, gating on
// readiness is the minimal correct guard. Remove this marker (and the test waits on it) when
// mutations become connection-state-independent.
let helloAcked = false;

function setReady(ready: boolean): void {
    if (ready) document.documentElement.setAttribute("data-ready", "1");
    else document.documentElement.removeAttribute("data-ready");
}

// Ready = the connection is established (hello acknowledged) AND no connect-time settle is still
// outstanding (a refetch kicked off on open has returned). Called after the hello reply and after
// each refetch reply, so whichever completes last flips the marker on. (Hydration is implied: a WS
// reply can only arrive after init() ran connectWs + the first render.)
function markReadyIfSettled(): void {
    setReady(helloAcked && !refetchInFlight);
}

function connectWs(): void {
    const proto = location.protocol === "https:" ? "wss:" : "ws:";
    // Asset endpoints (/ws, /js) live on a separate shared port from the app's URL space, addressed by
    // path under the instance's mount — so the app URL space stays clean AND the same authority serves
    // every instance. <assetAuthority><base>/ws; an empty authority means a same-origin, base-relative
    // URL (a reverse-proxied deployment, where nginx maps it back to the kernel's asset port).
    const wsUrl = initAssetAuthority
        ? `${proto}//${initAssetAuthority}${basePrefix()}/ws`
        : `${proto}//${location.host}${basePrefix()}/ws`;
    codeWs = new WebSocket(wsUrl);
    codeWs.onopen = () => {
        wsRetryDelay = 1000;
        // Claim the warm session minted at SSR before anything else; if this arrives
        // past the claim window the session is gone and refetches do a full re-render.
        codeWs!.send(JSON.stringify({ op: "hello", clientId: uiStatic.clientId }));
        for (const m of codeWsOutbox.splice(0)) codeWs!.send(m);
        maybeRefetch(); // resync after a reconnect (no-op on the first open)
    };
    codeWs.onclose = () => {
        // The connection died: outcomes of in-flight mutations are unknown, so drop
        // the journal (the optimistic state stands) and resync authoritatively once
        // reconnected. Reconnects back off exponentially, capped at 10s.
        journal.length = 0;
        pendingAdds.clear();
        refetchInFlight = false;
        needsServerData = true;
        // No longer ready: a dropped connection cannot carry a mutation. The marker re-arms
        // when the reconnect's hello is re-acknowledged (INTERIM — see the data-ready note).
        helloAcked = false;
        setReady(false);
        setTimeout(connectWs, wsRetryDelay);
        wsRetryDelay = Math.min(wsRetryDelay * 2, 10000);
    };
    codeWs.onmessage = ev => onWsMessage(JSON.parse(ev.data));

    setWsHooks({
        propChange: (obj, prop, value, before) => {
            // A negative id that is NOT a pending set add is a transient DRAFT (a `{ … }` never added to a
            // set): it has no server object, so skip it — its fields ship with its own eventual arrayAdd,
            // and sending now would be a "no such object" reject. A pending add (negative id IN pendingAdds)
            // IS sent: the server resolves the transient id through the add's remap, so a field edited
            // before the round-trip returns still saves.
            if (obj.id <= 0 && !pendingAdds.has(obj.id)) return;
            const msgId = nextWsMsgId++;
            journal.push({
                msgId,
                undo: () => { obj.props[prop] = before; invalidateProp(obj.id, prop); },
                redo: () => { before = obj.props[prop]; obj.props[prop] = value; invalidateProp(obj.id, prop); },
            });
            wsSend({ op: "objectPropChange", id: msgId, clientId: uiStatic.clientId,
                objectId: obj.id, prop, value: scalarOf(value) });
        },
        pathWrite: (obj, prop, path, value, before) => {
            // A dictionary entry's field has no extent id, so it persists by PATH: the `write`
            // op sets the leaf at the entry's path (an object entry's field at path/prop, a
            // scalar entry's value at path). DeserializeLeaf reads a BARE value.
            const msgId = nextWsMsgId++;
            journal.push({
                msgId,
                undo: () => { obj.props[prop] = before; invalidateProp(obj.id, prop); },
                redo: () => { before = obj.props[prop]; obj.props[prop] = value; invalidateProp(obj.id, prop); },
            });
            wsSend({ op: "write", id: msgId, clientId: uiStatic.clientId, path, value: bareScalar(value) });
        },
        setRef: (obj, prop, value, before) => {
            const msgId = nextWsMsgId++;
            journal.push({
                msgId,
                undo: () => { obj.props[prop] = before; invalidateProp(obj.id, prop); },
                redo: () => { before = obj.props[prop]; obj.props[prop] = value; invalidateProp(obj.id, prop); },
            });
            const base = { op: "setReferenceField", id: msgId, clientId: uiStatic.clientId, objectId: obj.id, prop };
            if (value.type === "object" && value.id > 0)
                wsSend({ ...base, refId: value.id });                 // point at an existing extent object
            else if (value.type === "object")
                wsSend({ ...base, value: objectOf(value) });          // mint a new object + point
            else
                wsSend({ ...base, clear: true });                     // unset
            // A pick/create can change a type's extent and the referenced object's data the
            // client never had: stale extents and refetch for the authoritative state.
            invalidateExtents();
            needsServerData = true;
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
        // A dictionary entry persists through the PATH-addressed addEntry/removeEntry ops
        // (the dict carries its sourcePath). addEntry's CreateEntry rejects a duplicate key
        // → the reply is an error → the journal rolls the optimistic row back automatically.
        entryAdd: (arr, item, key, value) => {
            const msgId = nextWsMsgId++;
            journal.push({
                msgId,
                undo: () => { const i = arr.items.indexOf(item); if (i >= 0) arr.items.splice(i, 1); invalidateMember(arr.id); },
                redo: () => { if (!arr.items.includes(item)) arr.items.push(item); invalidateMember(arr.id); },
            });
            // addEntry's DeserializeValue/DeserializeLeaf reads BARE values at the top level
            // (an object entry's fields as { name: rawScalar }, a scalar entry as the raw
            // scalar) — not the tagged/`props` shape the id-addressed ops use.
            const wireValue = value.type === "object" ? bareFieldsOf(value) : bareScalar(value);
            wsSend({ op: "addEntry", id: msgId, clientId: uiStatic.clientId,
                path: arr.sourcePath, key, value: wireValue });
        },
        entryRemove: (arr, item, key, index) => {
            const msgId = nextWsMsgId++;
            journal.push({
                msgId,
                undo: () => { arr.items.splice(Math.min(index, arr.items.length), 0, item); invalidateMember(arr.id); },
                redo: () => { const i = arr.items.indexOf(item); if (i >= 0) arr.items.splice(i, 1); invalidateMember(arr.id); },
            });
            wsSend({ op: "removeEntry", id: msgId, clientId: uiStatic.clientId,
                path: arr.sourcePath, key });
        },
        // A SERVER-ONLY host action (sys.publish): the server alone runs the effect, so this stages
        // NOTHING locally and pushes NO journal entry (there is no optimistic state to roll back). It
        // allocates a correlation id only to route the reply; an error reply surfaces as lastError
        // (no journal replay — see onWsMessage). Args ship as wire scalars (the server reads arg 0
        // as the target id).
        hostAction: (action, args) => {
            const msgId = nextWsMsgId++;
            wsSend({ op: "hostAction", id: msgId, clientId: uiStatic.clientId,
                action, args: args.map(scalarOf) });
        },
        // The session→principal bind (sys.login, M-auth login UI). Sends the plaintext credentials over
        // the WS (the server reads `name`/`password` as bare strings — the login op's shape, not the
        // tagged { type, value } the data ops use). It stages NOTHING and pushes NO journal entry (the
        // bind lives on the connection, not the data model); the REPLY (handled in onWsMessage) refetches
        // on success so the page re-renders as the bound principal. No correlation id is needed — the
        // reply is recognized by its `op` ("login"), and a failed login is a normal negative reply.
        login: (name, password) => {
            wsSend({ op: "login", clientId: uiStatic.clientId,
                name: bareScalar(name), password: bareScalar(password) });
        },
    });
}

function onWsMessage(msg: { op?: string; id?: number; tempId?: number; newId?: number; ok?: boolean;
                            collections?: { [prop: string]: { id: number; elementTypeName?: string } };
                            state?: ServerDtState; error?: string }): void {
    // Correlated accept/reject first: an error rolls the journal back, an ok commits.
    if (typeof msg.id === "number") {
        if (msg.error != null) {
            // A non-journaled send (a host action stages nothing): no journal entry matches, so
            // surface the error and re-render WITHOUT a journal replay.
            if (!rollbackJournal(msg.id, msg.error)) {
                uiStatic.lastError = msg.error;
                console.error("Host action rejected by the server:", msg.error);
                renderUi();
            }
            return;
        }
        commitJournal(msg.id);
    }

    // An uncorrelated error is a failed refetch (it carries no id) — clear the
    // in-flight guard so a later mutation can retry, instead of wedging forever.
    if (msg.error != null && typeof msg.id !== "number") {
        refetchInFlight = false;
        markReadyIfSettled(); // a failed connect-time refetch still settles readiness
        console.error("Server error:", msg.error);
        return;
    }

    if (msg.op === "hello") {
        // The session-claim is acknowledged: the connection is established. Readiness flips on
        // once any connect-time refetch has also returned. (INTERIM — see the data-ready note.)
        helloAcked = true;
        markReadyIfSettled();
    } else if (msg.op === "login") {
        // The session→principal bind reply (M-auth login UI). On success the WS session is now bound
        // to a principal, so refetch: the re-render runs AS that principal, the access floor admits the
        // newly-readable data, and `currentUser` is overwritten by mergeState — the page flips from the
        // login gate to the bound view at the SAME URL (login is a state, not a route). A failed login
        // (ok:false) is a normal negative reply — leave the gate up (a richer error surface is a
        // follow-up; the locked scope is the success→refetch flip).
        //
        // resetViewState() — NOT a bare needsServerData — because a login flip is a WHOLESALE render-tree
        // rebuild at the same URL (the gate's root <LoginForm> → the resolved root <ObjectForm>/<SetTable>/…),
        // exactly the "two different components at the SAME slot" hazard a navigation has. A component
        // memoizes by render-tree SLOT, and BOTH the gate's `return <LoginForm>` and the post-login
        // `return <ObjectForm>` are returned in VALUE position from the synthesized `fn render()`, so both
        // key on the same (empty/root) slot path "comp:". The refetch reply PRESERVES `comp:` entries
        // (component state), so without dropping them the stale LoginForm view would sit under the root slot
        // and renderUi would hand it back for the ObjectForm call — the page would never leave the gate.
        // resetViewState drops the `comp:` slot-cache (the SAME helper a navigation uses, ui.ts) and forces
        // the refetch, so the data view re-runs fresh at the root slot and the DOM swaps. (A navigation
        // doesn't hit this because it already calls resetViewState; login is the other same-slot-swap edge.)
        if (msg.ok) { resetViewState(); maybeRefetch(); }
    } else if (msg.op === "arrayAdd" && typeof msg.tempId === "number" && typeof msg.newId === "number") {
        const arrayId = pendingAdds.get(msg.tempId);
        if (arrayId != null) { pendingAdds.delete(msg.tempId); remapAddedId(arrayId, msg.tempId, msg.newId, msg.collections); }
    } else if (msg.op === "refetch" && msg.state != null) {
        refetchInFlight = false;
        mergeState(msg.state); // shipped entries arrive fresh (stale: false)
        // After a fresh data merge, drop the client-local RECOMPUTE entries so the next render
        // recomputes them over the merged data instead of reusing an outdated tree:
        //   • any STILL-stale entry — a mutation invalidated its deps but the server could not
        //     refresh it, so it must recompute; and
        //   • a `fn:`-keyed entry whose RESULT is a tag/fn — a per-route PAGE FUNCTION's rendered tree
        //     (e.g. the designer's `designEditorPage`/`designEditor`). The server NEVER ships tag/fn
        //     results (ClientState skips ExecTag/ExecFunction — it delegates their recompute to the
        //     client), so such an entry is always a client-local recompute. Critically, an OPTIMISTIC
        //     render that ran before this refetch may have computed one against not-yet-shipped data: a
        //     deep read threw "Value not available", which memoize SWALLOWS to an empty result whose deps
        //     never recorded the missing field — so the merge's prop-invalidation can't mark it stale, and
        //     it would survive as a stale EMPTY tree. (This works BECAUSE the direct-VNA path in codeExec.ts
        //     memoize deliberately does NOT cache the bare swallowed empty — only an enclosing `fn:` page,
        //     which doesn't itself throw, persists, and always as a tag/fn RESULT; see the LOAD-BEARING
        //     INVARIANT note there. So matching tag/fn results here catches exactly the poisoned pages.)
        //     Dropping it (a plain fn carries no component state,
        //     unlike `comp:`, so re-running is a pure recompute) forces a correct re-render over the
        //     complete data. Scoped to tag/fn RESULTS so a `fn:` returning a SCALAR computed from PRIVATE
        //     data — which the server DOES ship precisely so the client need not recompute it — is kept,
        //     not dropped (dropping it would re-read the unshipped input and loop). This is what makes a
        //     fully-CUSTOM render (the designer) navigate client-side correctly; the generic UI's views
        //     are `comp:` (their state is preserved) and were already fresh.
        //   • an `incomplete` entry — its compute SWALLOWED a "Value not available" below it (a speculative
        //     render over un-shipped data), so it (e.g. a `comp:`-view that spliced a just-created member's
        //     RefEditor reading an un-shipped sys.extent) holds an empty child but recorded NO dep on the
        //     missing data — the merge can't stale it. Unlike the broad `fn:` rule this is PRECISE: it drops
        //     ONLY views built over partial data, so healthy `comp:` state (the operator designer's delete
        //     flow) is untouched. The dropped view recomputes over the now-complete merged data next render.
        for (const [key, e] of uiStatic.cache)
            if (e.stale || e.incomplete || (key.startsWith("fn:") && (e.result.type === "tag" || e.result.type === "fn")))
                uiStatic.cache.delete(key);
        markReadyIfSettled(); // the connect-time settle (if any) is now applied
        renderUi();
        // A navigation that fired DURING this refetch found refetchInFlight set and self-serialized
        // (maybeRefetch no-op'd), leaving needsServerData set for the NEW path. The reply we just merged
        // was for the OLD path, so the current path may still be incomplete and HELD — re-fire now that
        // the in-flight slot is free, so the latest path actually gets its own fetch instead of being
        // stranded on a held/partial view. No-op in the common case (needsServerData already cleared);
        // it self-terminates once the current path renders complete (each fetch ships more, never loops).
        maybeRefetch();
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
    if (refetchInFlight || (!hasStaleEntry() && !needsServerData)) return;
    needsServerData = false;
    refetchInFlight = true;
    // lastId: the server mints its re-render transients BELOW everything this client
    // already holds, so shipped negative ids never collide with local drafts.
    wsSend({ op: "refetch", clientId: uiStatic.clientId, path: location.pathname,
        vars: sessionVars(), lastId: uiStatic.lastId.value });
}

function hasStaleEntry(): boolean {
    for (const e of uiStatic.cache.values()) if (e.stale) return true;
    return false;
}

// The client-held session vars the server re-renders with: scalars (path, transient
// inputs, …) by value, persisted objects (the selection) as id-refs the server resolves
// against the warm session graph. Transient objects and computed collections stay local.
function sessionVars(): { [name: string]: object } {
    const out: { [name: string]: object } = {};
    for (const [name, item] of Object.entries(uiStatic.state.scope.items)) {
        if (item.isReadOnly) continue; // db / functions are not session state
        const v = item.value;
        if (v.type === "int" || v.type === "bool" || v.type === "text") out[name] = scalarOf(v);
        else if (v.type === "object" && v.id > 0) out[name] = { type: "object", id: v.id };
    }
    return out;
}

// Re-key a just-persisted set member from its transient negative id to the real extent
// id: the array item's key, the member object's id, and the id maps. A positive id now
// means future prop writes on it persist. The store also minted the object's collection
// props with their own intrinsic ids — re-key the transient arrays to them, so adds
// into them persist too. A re-render refreshes the row's data-key.
function remapAddedId(arrayId: number, tempId: number, realId: number,
                      collections?: { [prop: string]: { id: number; elementTypeName?: string } }): void {
    const arr = uiStatic.state.arrays[arrayId];
    const item = arr?.items.find(i => i.key === tempId);
    if (item) {
        item.key = realId;
        if (item.value.type === "object") {
            item.value.id = realId;
            uiStatic.state.objects[realId] = item.value;
            for (const [prop, coll] of Object.entries(collections ?? {})) {
                const propArr = item.value.props[prop];
                if (propArr?.type === "array") {
                    propArr.id = coll.id;
                    propArr.kind = "set";
                    propArr.elementTypeName = coll.elementTypeName;
                    uiStatic.state.arrays[coll.id] = propArr;
                }
            }
        }
    }
    uiStatic.state.localToServerIds[tempId] = realId;
    uiStatic.state.serverToLocalIds[realId] = tempId;
    // Ack the remap so the server can drop its transient→real entry for tempId: from here on we address
    // this object by its real id and never send the transient one again (every op referencing it that we
    // sent BEFORE this point was already ordered ahead of this ack). Uncorrelated (no journal entry).
    wsSend({ op: "ackRemap", clientId: uiStatic.clientId, tempId });
    // The re-keyed member changes what dependents render (row data-keys), so cached
    // computations over this array must rebuild.
    invalidateMember(arrayId);
    renderUi();
}

// Is the session ready to service a client-side navigation's refetch over the warm session? Not merely
// socket-open: the connection must also be CLAIMED — the session-claim `hello` ACKNOWLEDGED (helloAcked,
// the same readiness signal `data-ready` is built from). Gating SPA nav only on socket-open would let a
// navigation fire a refetch into the UNCLAIMED connecting-window (open but before `hello` is acked),
// where the warm session is not yet bound to this client and the target view could be left
// unserviceable. When not claimed, client-side nav (forward click AND Back/Forward) falls back to a full
// browser navigation so the user is never stranded on a changed URL with stale/NotFound content.
//
// NB this does NOT also require `!refetchInFlight`: a refetch already in flight does not make the
// session unable to service a navigation. `maybeRefetch` self-serializes (a second refetch no-ops while
// one is pending, and the latest `path` wins on the next render), so a nav fired during a refetch is
// fine. Folding `refetchInFlight` in here WOULD break the common flow — a forward client-nav kicks off a
// refetch (the always-refetch floor), so an immediate Back (or a rapid second click) would see the
// session "not ready" and force a full reload mid-settle. claimed-and-open is the right serviceability
// line; the connect-time settle that `data-ready` additionally waits for is a MUTATION concern, not a
// nav one. A normal mutation still rides the outbox (wsSend) as before — this is only the SPA-nav gate.
function wsReady(): boolean {
    return codeWs != null && codeWs.readyState === WebSocket.OPEN && helloAcked;
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

// A new object's scalar props for the server to persist: { props: { name: leaf } }, each
// leaf tagged — the shape the id-addressed ops (arrayAdd / setReferenceField) read.
function objectOf(value: ExecValue): object {
    const props: { [name: string]: object } = {};
    if (value.type === "object")
        for (const [name, v] of Object.entries(value.props))
            if (v.type === "int" || v.type === "bool" || v.type === "text") props[name] = scalarOf(v);
    return { props };
}

// An object's scalar fields as bare values { name: rawScalar } — the shape addEntry's
// DeserializeValue/DeserializeLeaf reads (each field a raw JSON scalar, not tagged).
function bareFieldsOf(value: ExecValue): { [name: string]: string | number | boolean } {
    const fields: { [name: string]: string | number | boolean } = {};
    if (value.type === "object")
        for (const [name, v] of Object.entries(value.props))
            if (v.type === "int" || v.type === "bool" || v.type === "text") fields[name] = v.value;
    return fields;
}

// A scalar ExecValue's raw JS value (a scalar dict entry's bare value for addEntry).
function bareScalar(value: ExecValue): string | number | boolean {
    if (value.type === "int" || value.type === "bool" || value.type === "text") return value.value;
    return "";
}
