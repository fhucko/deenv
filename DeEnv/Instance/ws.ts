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
    // The objects/arrays this entry's undo/redo closures capture — exposed for the reachability GC
    // (sweepUnreachable, dt.ts) to MARK as roots, since a closure's captured vars are opaque to a graph
    // walk. A pending mutation must keep its referenced data alive for rollback — crucially an
    // arrayRemove/entryRemove's DETACHED item, which is no longer in any array and so is reachable ONLY
    // through this entry. The walk descends each root (an object's props, an array's items), so listing the
    // top object(s)/array(s) suffices; the removed item is listed by its value explicitly.
    roots?: ExecValue[];
    // The ObjectForm ctx whose ctx.commit() produced this entry (form-Save feedback), set only for sends
    // fired inside a commit bracket (beginCommit/endCommit). The ack drains it to "saved" once this ctx's
    // last pending entry retires; a reject clears it back to "idle". Undefined for a LIVE collection edit
    // (set/dict add/remove, ref, autosave per-keystroke) — those fire outside any commit, so they never
    // touch a form's lifecycle.
    ctx?: ExecCtx;
}
// The ctx whose commit is currently flushing — set by beginCommit, cleared by endCommit. A commit's
// staged-walk fires its WS sends SYNCHRONOUSLY inside that bracket, so recordMutation reads this to know
// which form ctx an entry belongs to. Per-ctx (not a global counter) because a live collection edit can
// interleave with a form's pending commit and must NOT decrement the form's count.
let committingCtx: ExecCtx | null = null;
const journal: JournalEntry[] = [];
let nextWsMsgId = 1;

// THE mutation chokepoint (client data layer, slice 1c — the generation guard). Every journaled DATA
// MUTATION (objectPropChange / write / setReferenceField / arrayAdd / arrayRemove / addEntry / removeEntry)
// records its entry through here, so pushing a journal entry IS the mutation signature: it bumps `stateGen`,
// invalidating any in-flight refetch (whose reply was computed over the PRE-mutation store and would
// otherwise clobber the optimistic value — see `stateGen`/`inFlightGen`). Non-mutation sends that push NO
// journal entry (hello / refetch / ackRemap / hostAction / login / logout) do NOT bump on this
// path; session ops (login/logout) keep their own explicit bump.
function recordMutation(entry: JournalEntry): void {
    stateGen++;
    // Form-Save feedback: if this send fired inside a ctx.commit bracket, tag it with that ctx and count
    // it as in-flight, so the ack can drive the form's "Saving… → Saved" lifecycle (a reject clears to idle).
    if (committingCtx != null) { entry.ctx = committingCtx; committingCtx.pending++; }
    journal.push(entry);
}

// ── handler transaction (client data layer, slice 3 — atomic, commit-on-success handlers) ──────────
//
// A click handler runs as a TRANSACTION: its writes apply optimistically in place (so an intra-handler
// read sees an earlier write), but the WS SENDS they trigger are BUFFERED — flushed only when the
// handler COMPLETES cleanly. If the handler throws (a runtime error, or — slice 4 — a missing-data VNA),
// the staged effects are rolled back atomically: the journal entries it pushed are reverse-undone and
// dropped, `stateGen` is restored, and the buffered sends are discarded (NOTHING reached the server).
//
// WHY here, not a `ctx` overlay: the data-context `ctx` (an ambient overlay) does NOT redirect a
// handler's writes — a closure resolves `ctx` from its BIRTHPLACE ambient (data-context-refactor slice
// 3a), so wrapping the call site in a fresh `ctx.new()` is overridden by the captured ambient — AND `ctx`
// only stages object-prop writes (positive-id), not set/ref/dict/entry ops (which `recordMutation` +
// `wsSend` directly). Bracketing at the SEND + JOURNAL boundary catches EVERY effect kind uniformly, below
// the ambient resolution, with no interpreter/twin change. This is the "discardable overlay" the spec's
// atomicity model calls for, realized at the transport seam (client-only orchestration around the fn).
//
// Single-level: handlers are invoked only from the DOM (onClick), never re-entrantly mid-handler, so a
// nested transaction cannot arise. A defensive guard makes a nested begin a no-op (it just participates in
// the outer transaction) rather than corrupting the marks.
let txDepth = 0;
let txJournalMark = 0;   // journal length when the (outermost) transaction began
let txStartGen = 0;      // stateGen when it began — restored on abort (the undone mutations didn't happen)
let txSendBuffer: string[] | null = null;
// OUT-OF-JOURNAL client state a handler can flip that the journal `undo` does NOT reverse — captured at
// BEGIN, restored verbatim on the non-VNA abort so the rolled-back state leaves ZERO trace and triggers NO
// refetch. Two things leak today, both from the `setRef` hook (it stages the prop in the journal but also,
// OUTSIDE the entry, sets `needsServerData = true` + `invalidateExtents()` which coarse-stales every
// `extent:` memo entry): (1) `needsServerData`, and (2) the cache entries' `stale` flags. Restoring
// `stale` per-key also subsumes the staling the journal `undo` itself does (invalidateProp/invalidateMember
// inside undo), so the abort is a complete restore-to-begin. SOUND because handlers run under memoBypass —
// no NEW cache entries appear mid-handler and none are deleted, so the begin-time keys are exactly the
// keys at abort, and a key→stale snapshot maps cleanly back.
let txStartNeedsServerData = false;
let txStaleSnapshot: Map<string, boolean> | null = null;

// Run `body` as a commit-on-success handler transaction. Three outcomes:
//
//  • CLEAN COMPLETION — flush the buffered sends in handler order (their msg ids were allocated in that
//    order, so FIFO ordering on the wire is preserved). Net effect identical to the pre-slice-3
//    per-statement send; the handler is now ATOMIC (nothing was sent mid-run).
//
//  • a NON-VNA throw (a genuine runtime bug — a bad call, an invalid op) — ABORT: reverse-replay + drop the
//    journal entries this handler pushed (so its optimistic local writes are undone), restore stateGen, and
//    discard the buffered sends. Zero partial trace, nothing sent. Re-render the rolled-back state, then
//    re-throw so the bug still surfaces. This is the new atomicity guarantee.
//
//  • a VALUE-NOT-AVAILABLE throw (the handler read data the client does not hold — the action-miss case) —
//    for slice 3, PRESERVE today's behavior: flush whatever was buffered BEFORE the throw (today these
//    sends fired per-statement, so flushing the buffer reproduces exactly the same sent-set), keep the
//    local writes + journal, re-render, and re-throw. Slice 4 replaces this branch with catch → record →
//    fetch → re-invoke-over-the-now-present-data (the proper atomic action-miss round-trip); until then the
//    VNA path is left exactly as it is today, so the existing handler-driven suite is unchanged. (A handler
//    that hits a spurious sys.schema-under-memoBypass VNA after its real writes is precisely this case.)
function runHandlerTransaction(body: () => void, action?: PendingAction): void {
    if (txDepth > 0) { body(); return; }   // already in a transaction: just participate (defensive)
    txDepth = 1;
    txJournalMark = journal.length;
    txStartGen = stateGen;
    txSendBuffer = [];
    // Capture the out-of-journal client state (see the note above): the refetch flag and a snapshot of
    // every cache entry's stale flag, so an abort can restore the EXACT pre-handler state.
    txStartNeedsServerData = needsServerData;
    txStaleSnapshot = new Map();
    for (const [key, e] of uiStatic.cache) txStaleSnapshot.set(key, e.stale);
    try {
        body();
        flushHandlerTx();   // success: send everything the handler buffered, in order
    } catch (e) {
        if (e instanceof Error && e.message === "Value not available") {
            // A VALUE-NOT-AVAILABLE throw is one of two very different things, told apart by whether the
            // handler had already BUFFERED a send (done real work) before it threw:
            //
            //  • NO buffered send yet → a genuine ACTION-MISS (client data layer, slice 4): the handler could
            //    not even start because it READ data the client does not hold (the canonical "read unloaded
            //    data to compute, then write" — the read comes first). ABORT atomically (it made no committed
            //    write — there is nothing partial), RECORD this handler as a pending action, and FETCH: the
            //    server reproduces the exact render, invokes this handler READ-ONLY, harvests the data it reads
            //    and ships it; on the reply (onWsMessage) the data merges and the handler RE-RUNS over it and
            //    completes. Do NOT re-throw — the action is in flight, not failed. (Requires an `action`: a
            //    handler built outside a render carries no slot to address it by; then fall through.)
            //
            //  • a send ALREADY buffered → the handler DID its real work (e.g. a `.add` that staged an
            //    arrayAdd) and the VNA is INCIDENTAL — a trailing read over un-shipped data (the spurious
            //    sys.schema/sys.extent-under-memoBypass case the slice-3 note names). Keep TODAY's behavior:
            //    flush the pre-throw sends (the real work persists) + re-throw. Re-running this on the server
            //    read-only would mis-harvest (it reads client-only draft state the server cannot reproduce —
            //    "Unknown field" — and would re-do the mutation), so it must NOT take the action-miss path.
            const didWork = (txSendBuffer?.length ?? 0) > 0;
            if (action != null && !didWork) {
                abortHandlerTx();
                pendingAction = action;
                needsServerData = true; // the fetch must carry the action even if nothing is stale
                renderUi();             // paint the rolled-back state; the eventual reply re-runs + repaints
                maybeRefetch();
                return;
            }
            flushHandlerTx();   // the handler did real work (or carries no action): flush pre-throw sends + re-throw
            throw e;
        }
        // Genuine bug — atomic rollback, then re-throw so the bug still surfaces.
        abortHandlerTx();
        renderUi();
        throw e;
    }
}

// Atomically roll back the current handler transaction (client data layer, slice 3): undo the journal slice
// this handler pushed (reverse order), then restore the OUT-OF-JOURNAL state the undo does not cover —
// `needsServerData` and every cache entry's stale flag — back to their begin values. This reverts a
// `setRef`'s coarse extent-staling + needsServerData (and the staling the undo itself just did) so the abort
// leaves ZERO trace: a trailing renderUi finds nothing stale and nothing needed → maybeRefetch sends NOTHING.
// (Restore AFTER the undo: the undo re-stales prop/member entries, which this snapshot then unwinds.) Discards
// the buffered sends and leaves the transaction. Shared by the genuine-bug abort and the action-miss abort.
function abortHandlerTx(): void {
    for (let i = journal.length - 1; i >= txJournalMark; i--) { journal[i].undo(); journal[i].onReject?.(); }
    journal.length = txJournalMark;
    stateGen = txStartGen;
    needsServerData = txStartNeedsServerData;
    if (txStaleSnapshot != null)
        for (const [key, wasStale] of txStaleSnapshot) { const e = uiStatic.cache.get(key); if (e) e.stale = wasStale; }
    txStaleSnapshot = null;
    txSendBuffer = null;
    txDepth = 0;
}

// Flush + clear the current handler transaction's buffered sends (in handler order), then leave the
// transaction. Shared by the success and the VNA-passthrough paths.
function flushHandlerTx(): void {
    const buffered = txSendBuffer ?? [];
    txSendBuffer = null;
    txStaleSnapshot = null;   // not an abort: keep the staling the handler/undo did, just drop the snapshot
    txDepth = 0;
    for (const text of buffered) wsSendText(text);
}

// ── action-miss pending action (client data layer, slice 4) ────────────────────────────────────────────
//
// A click handler that read un-shipped data is ABANDONED (atomic rollback) and recorded here: its (slot,
// fn-id) — which the refetch ships so the server invokes the SAME handler read-only to harvest its data — plus
// a `reinvoke` thunk that re-runs the handler. On the refetch reply (onWsMessage), once the harvested data has
// merged, the handler re-runs over it in a FRESH transaction (so the re-run is atomic too) and completes. The
// abandoned attempt left no trace (the abort), so the action runs exactly once FOR REAL. Single-slot: a second
// miss while one is pending overwrites it (the latest interaction wins — the older one is superseded), the
// same single-in-flight discipline the refetch itself uses.
interface PendingAction { fnId: number; slot: string; reinvoke: () => void; }
let pendingAction: PendingAction | null = null;

// The reply for msgId was ok: the mutation is server-committed, the entry retires.
function commitJournal(msgId: number): void {
    const idx = journal.findIndex(e => e.msgId === msgId);
    if (idx < 0) return;
    const [done] = journal.splice(idx, 1);
    // Form-Save feedback: as this commit's last in-flight send retires, settle the form to "Saved" and
    // re-render so the inline indicator updates (the ONE new wire — the ack path did not re-render before).
    // setCtxStatus invalidates the ctx-status var dep so the memoized component view actually recomputes.
    if (done.ctx != null && --done.ctx.pending === 0 && done.ctx.status === "saving") {
        setCtxStatus(done.ctx, "saved");
        renderUi();
    }
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
    // Form-Save feedback: a rejected commit send fails the whole form — clear its ctx back to "idle" and
    // zero its pending count (the other entries from this commit are reverse-undone above; their later
    // acks must not then flip it to "saved"). The failure itself surfaces via the global error banner
    // (uiStatic.lastError below), so the inline indicator just returns to neutral — one failure surface,
    // not two. setCtxStatus invalidates the ctx-status var dep; renderUi() below repaints.
    if (failed.ctx != null) { setCtxStatus(failed.ctx, "idle"); failed.ctx.pending = 0; }
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
            recordMutation({
                msgId,
                undo: () => { obj.props[prop] = before; invalidateProp(obj.id, prop); },
                redo: () => { before = obj.props[prop]; obj.props[prop] = value; invalidateProp(obj.id, prop); },
                roots: [obj],
            });
            wsSend({ op: "objectPropChange", id: msgId, clientId: uiStatic.clientId,
                objectId: obj.id, prop, value: scalarOf(value) });
        },
        pathWrite: (obj, prop, path, value, before) => {
            // A dictionary entry's field has no extent id, so it persists by PATH: the `write`
            // op sets the leaf at the entry's path (an object entry's field at path/prop, a
            // scalar entry's value at path). DeserializeLeaf reads a BARE value.
            const msgId = nextWsMsgId++;
            recordMutation({
                msgId,
                undo: () => { obj.props[prop] = before; invalidateProp(obj.id, prop); },
                redo: () => { before = obj.props[prop]; obj.props[prop] = value; invalidateProp(obj.id, prop); },
                roots: [obj],
            });
            wsSend({ op: "write", id: msgId, clientId: uiStatic.clientId, path, value: bareScalar(value) });
        },
        setRef: (obj, prop, value, before) => {
            const msgId = nextWsMsgId++;
            recordMutation({
                msgId,
                undo: () => { obj.props[prop] = before; invalidateProp(obj.id, prop); },
                redo: () => { before = obj.props[prop]; obj.props[prop] = value; invalidateProp(obj.id, prop); },
                // obj, plus the referenced objects on both sides of the swap (a clear's `before` holds the
                // previously-pointed object that undo restores; a pick's `value` the newly-pointed one).
                roots: [obj, value, before],
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
            recordMutation({
                msgId,
                undo: () => { const i = arr.items.indexOf(item); if (i >= 0) arr.items.splice(i, 1); invalidateMember(arr.id); },
                redo: () => { arr.items.push(item); invalidateMember(arr.id); },
                onReject: () => pendingAdds.delete(item.key),
                roots: [arr, item.value],
            });
            wsSend({ op: "arrayAdd", id: msgId, clientId: uiStatic.clientId,
                setId: arr.id, tempId: item.key, typeName, value: objectOf(item.value) });
        },
        arrayRemove: (arr, item, index) => {
            const msgId = nextWsMsgId++;
            recordMutation({
                msgId,
                undo: () => { arr.items.splice(Math.min(index, arr.items.length), 0, item); invalidateMember(arr.id); },
                redo: () => { const i = arr.items.indexOf(item); if (i >= 0) arr.items.splice(i, 1); invalidateMember(arr.id); },
                // item.value is DETACHED (already spliced out of arr.items) — reachable ONLY through here, so
                // marking it is what keeps a removed-then-rolled-back member from being false-swept.
                roots: [arr, item.value],
            });
            wsSend({ op: "arrayRemove", id: msgId, clientId: uiStatic.clientId,
                setId: arr.id, objectId: item.key });
        },
        // A dictionary entry persists through the PATH-addressed addEntry/removeEntry ops
        // (the dict carries its sourcePath). addEntry's CreateEntry rejects a duplicate key
        // → the reply is an error → the journal rolls the optimistic row back automatically.
        entryAdd: (arr, item, key, value) => {
            const msgId = nextWsMsgId++;
            recordMutation({
                msgId,
                undo: () => { const i = arr.items.indexOf(item); if (i >= 0) arr.items.splice(i, 1); invalidateMember(arr.id); },
                redo: () => { if (!arr.items.includes(item)) arr.items.push(item); invalidateMember(arr.id); },
                roots: [arr, item.value],
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
            recordMutation({
                msgId,
                undo: () => { arr.items.splice(Math.min(index, arr.items.length), 0, item); invalidateMember(arr.id); },
                redo: () => { const i = arr.items.indexOf(item); if (i >= 0) arr.items.splice(i, 1); invalidateMember(arr.id); },
                roots: [arr, item.value], // item.value is detached on removal — see arrayRemove
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
        // The MIRROR of login (sys.logout, M-auth login UI 1e-2). Clears the session's principal back to
        // anonymous over the WS — no credentials, just the clientId (the server's `logout` op is idempotent
        // and always replies ok). Like login it stages NOTHING and pushes NO journal entry; the REPLY
        // (handled in onWsMessage) refetches so the page swaps the root view back to the anonymous gate at
        // the SAME URL. No correlation id — the reply is recognized by its `op` ("logout").
        logout: () => {
            wsSend({ op: "logout", clientId: uiStatic.clientId });
        },
        // Form-Save feedback: mark/clear the ctx whose commit is flushing, so recordMutation tags the
        // commit's sends with it (and bumps its pending count). Sends nothing — pure client bookkeeping.
        beginCommit: ctx => { committingCtx = ctx; },
        endCommit: () => { committingCtx = null; },
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
        if (msg.ok) { stateGen++; resetViewState(); maybeRefetch(); }
    } else if (msg.op === "logout") {
        // The MIRROR of the login reply (M-auth login UI 1e-2). The WS session is now anonymous again, so
        // refetch: the re-render runs with NO principal, the access floor denies the now-unreadable data,
        // `currentUser` flips back to null (mergeState), and the synthesized render's gate re-shows the
        // <LoginForm> — the page swaps from the bound view back to the gate at the SAME URL (logout is a
        // state, not a route). The server's `logout` op is idempotent and ALWAYS replies ok, so there is no
        // ok-gate (unlike login, where a wrong password is a normal negative reply).
        //
        // resetViewState() — the SAME root-slot-swap fix login uses — because logout is the inverse wholesale
        // render-tree rebuild at the same URL: the resolved root view (<UserMenu> + <ObjectForm>/…) → the
        // gate's root <LoginForm>, two different components returned in VALUE position from the synthesized
        // `fn render()` that key on the same root slot path. Without dropping the `comp:` slot-cache the stale
        // logged-in view would sit under the root slot and renderUi would hand it back for the LoginForm call —
        // the page would never return to the gate. resetViewState drops `comp:` (ui.ts) + forces the refetch.
        stateGen++; resetViewState(); maybeRefetch();
    } else if (msg.op === "arrayAdd" && typeof msg.tempId === "number" && typeof msg.newId === "number") {
        const arrayId = pendingAdds.get(msg.tempId);
        if (arrayId != null) { pendingAdds.delete(msg.tempId); remapAddedId(arrayId, msg.tempId, msg.newId, msg.collections); }
    } else if (msg.op === "refetch" && msg.state != null) {
        refetchInFlight = false;
        if (inFlightGen !== stateGen) {
            // The client state was SUPERSEDED while this refetch was in flight (client data layer, I5):
            //   • a login/logout changed the session (a dead session's render — e.g. a logout's anonymous
            //     state arriving after a login — must not overwrite the just-logged-in view); OR
            //   • a DATA MUTATION landed (slice 1c) — this reply was computed over the PRE-mutation store, so
            //     merging it would REVERT the just-made optimistic value (the optimistic-clobber hazard).
            // Either way the reply is STALE: DROP it (do NOT mergeState) and re-fetch under the CURRENT state.
            // The supersession already set needsServerData (resetViewState on a session change; the mutation
            // hooks that set it on a ref pick/create), but set it again defensively so maybeRefetch is
            // guaranteed to re-fire. The WS is FIFO-ordered, so a re-fetch fired now is processed AFTER the
            // mutation that bumped the gen and its reply reflects it — the merge never clobbers the optimistic
            // edit. Self-terminating: the re-fetch is stamped with the current gen, so it is discarded again
            // only if ANOTHER change lands meanwhile (no loop once the state settles).
            needsServerData = true;
            maybeRefetch();
            return;
        }
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
        // ACTION-MISS re-invoke (client data layer, slice 4): the server harvested the data the abandoned
        // handler reads and it has now merged, so RE-RUN the handler over the now-present data — in a FRESH
        // transaction (atomic, commit-on-success, exactly like the original click), so the writes it abandoned
        // now actually apply + persist. Clear the pending action FIRST (a re-run that misses AGAIN would
        // re-arm it via the same VNA path — but the server reproduced exactly what it reads, so it completes).
        // runHandlerTransaction renders the rolled-back state on its own abort; the trailing renderUi below
        // paints the committed result. Runs BEFORE that renderUi so the handler's optimistic writes are in the
        // tree it paints.
        if (pendingAction != null) {
            const action = pendingAction;
            pendingAction = null;
            runHandlerTransaction(action.reinvoke);
        }
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

// A refetch's reply is computed over the CLIENT STATE the server reproduced from the intent at the moment
// it ran. If that state is superseded while the refetch is in flight, the reply is STALE — merging it would
// overwrite the current state with a render of a state that no longer exists. `stateGen` is a generation
// counter bumped on every change that would make an in-flight reply wrong (client data layer, I5 —
// "apply-iff-current"); `inFlightGen` stamps the refetch in flight, so its reply can be recognised as stale
// and discarded. Two kinds of change bump the generation:
//   • a SESSION change — login/logout (a different principal; a dead session's render, e.g. a logout's
//     anonymous state arriving after a login, must not overwrite the just-logged-in view); and
//   • a DATA MUTATION — an optimistic edit/add/remove (recordMutation below): the in-flight reply was
//     computed over the PRE-mutation store, so merging it would revert the just-made optimistic value (the
//     optimistic-clobber hazard). Bumping the gen discards that reply; the WS is FIFO-ordered, so the
//     re-fetch is processed AFTER the mutation and reflects it — the merge never clobbers an optimistic edit.
// A pure view-state toggle / the slotState ship do NOT bump: their stale reply is harmless same-principal
// ADDITIVE data, absorbed by the additive merge + the trailing re-fetch (nav rides mergeState's path-skip).
let stateGen = 0;
let inFlightGen = 0;

function maybeRefetch(): void {
    if (refetchInFlight || (!hasStaleEntry() && !needsServerData)) return;
    needsServerData = false;
    refetchInFlight = true;
    inFlightGen = stateGen; // the client-state generation this refetch is computed under (checked on its reply, I5)
    // lastId: the server mints its re-render transients BELOW everything this client
    // already holds, so shipped negative ids never collide with local drafts.
    // slotState: the live per-component view-state (client data layer, slice 1b), so the server
    // reproduces the EXACT client render — including a popup the client toggled open — and ships the
    // data that state demands (which a URL-only refetch never reaches). Client-only orchestration,
    // exactly like `vars`/sessionVars (no twin, no conformance).
    // handlerFn/handlerSlot: the ACTION-MISS intent (client data layer, slice 4) — present when a click
    // handler abandoned on un-shipped data; the server invokes that handler read-only in the reproduced
    // render and harvests its data, which the reply merges before the handler re-runs (onWsMessage).
    const refetch: { [k: string]: unknown } = { op: "refetch", clientId: uiStatic.clientId,
        path: location.pathname, vars: sessionVars(), slotState: slotState(), lastId: uiStatic.lastId.value };
    if (pendingAction != null) { refetch.handlerFn = pendingAction.fnId; refetch.handlerSlot = pendingAction.slot; }
    wsSend(refetch);
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

// The live per-component view-state the server re-renders with (client data layer, slice 1b): a map from
// a MOUNTED component's render-slot key (`comp:<slotpath>`) to its setup-scope locals (its `var state …`).
// The server seeds these after the matching component's setup runs (CodeExecutor.ApplySeed), reproducing
// the client's EXACT component view-state — so a popup the client toggled open renders on the server too,
// and the data that state demands is harvested + shipped (the URL-keyed refetch alone never reaches it).
//
// Source: the memo cache's `comp:<slot>` entries whose result is a render CLOSURE (a stateful component —
// its setup returned `fn render()`). The closure captures the setup's body scope, where its `var state`
// lives (the SAME scope ApplySeed writes into). Read-only setup locals (the component's bound PARAMS) are
// skipped — the server re-binds those from the tag attributes; only the writable `var` state ships. Bounded
// to the LIVE cache's mounted slots (what is on screen), so "whole state" stays bounded.
//
// Ship-rule — the SAME identity axis as sessionVars (does the server already have this object? = id sign):
//   • scalar (int/bool/text) → by value;
//   • in-store object (positive id) → an id-ref the server resolves against its canonical load;
//   • TRANSIENT object (negative id) → by VALUE — its scalar fields ({ type:"object", props:{…} }), which
//     the server reconstructs as a throwaway transient and discards after harvesting. This is the COMMON
//     case for component state: a `var state = { open: false }` is a transient object (a component-local
//     object is non-top, so a `state.open = …` toggle re-renders via invalidateProp — a SCALAR `var` in a
//     component scope is NOT reactive and so is never the toggle), so the round-trip MUST carry it. It is
//     pure VIEW-STATE (a popup-open flag), not a draft that FEEDS a query — the harvested data depends on
//     WHICH branch runs, never on the object's field VALUES — so a crafted value can't widen the floor-
//     gated read (I3). (Draft-objects-that-DRIVE-a-query stay deferred; they are a different concern.)
function slotState(): { [slotKey: string]: { [name: string]: object } } {
    const out: { [slotKey: string]: { [name: string]: object } } = {};
    for (const [key, entry] of uiStatic.cache) {
        if (!key.startsWith("comp:") || key.endsWith(":view")) continue; // the setup entry, not its :view
        if (entry.stale || entry.result.type !== "fn") continue;          // a render closure = a stateful component
        const locals: { [name: string]: object } = {};
        for (const [name, item] of Object.entries(entry.result.scope.items)) {
            if (item.isReadOnly) continue; // a bound param — the server re-binds it; only `var` state ships
            const v = item.value;
            if (v.type === "int" || v.type === "bool" || v.type === "text") locals[name] = scalarOf(v);
            else if (v.type === "object" && v.id > 0) locals[name] = { type: "object", id: v.id };
            else if (v.type === "object") locals[name] = { type: "object", props: scalarPropsOf(v) }; // transient → by value
        }
        if (Object.keys(locals).length > 0) out[key] = locals;
    }
    return out;
}

// A transient object's SCALAR props as tagged wire values ({ open: { type:"bool", value:true } }) — the
// by-value shape slotState ships a component's `var state` object in, reconstructed server-side as a
// throwaway ExecObject (WsHandler.ExecValueFromWire per field). Scalars only: a nested object/collection in
// component view-state is not part of the toggle footprint v1 reproduces (and would re-introduce identity).
function scalarPropsOf(value: ExecObject): { [name: string]: object } {
    const props: { [name: string]: object } = {};
    for (const [name, v] of Object.entries(value.props))
        if (v.type === "int" || v.type === "bool" || v.type === "text") props[name] = scalarOf(v);
    return props;
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

// Send now if the socket is open; otherwise queue and flush on open. DURING a handler transaction
// (slice 3) the send is BUFFERED instead — held until the handler completes cleanly (then flushed in
// order) or discarded if it throws — so nothing reaches the server mid-handler.
function wsSend(msg: object): void {
    const text = JSON.stringify(msg);
    if (txSendBuffer != null) { txSendBuffer.push(text); return; }
    wsSendText(text);
}

// Put an already-serialized frame on the wire (or the connect-time outbox). The transaction flush sends
// its buffered frames through here, bypassing the buffer (which it has already cleared).
function wsSendText(text: string): void {
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
