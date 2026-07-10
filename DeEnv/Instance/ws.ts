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
// The store's version as of THIS first paint (optimistic-concurrency anti-clobber — DECISIONS.md "App
// versioning — the full design (M13 clump)"). Seeds clientKnownVersion below.
declare const initStoreVersion: number;

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
// The edits accumulated during a commit bracket (beginCommit..endCommit). The propChange hook buffers
// here instead of sending individual objectPropChange messages; endCommit flushes ONE `commit` message.
// null = no commit bracket is open (live edits fire objectPropChange directly, unchanged).
// Each entry carries the wire payload AND the closure data (obj, before) needed for the journal undo/redo.
interface CommitEdit { obj: ExecObject; prop: string; value: ExecValue; before: ExecValue; wire: object }
let commitEdits: CommitEdit[] | null = null;
// The CREATES accumulated during a commit bracket (atomic-commit Step B). The commitCreate hook buffers each
// staged create here; endCommit folds them into the ONE `commit` message's creates+relations. null = no commit
// bracket open (parallel to commitEdits). Each carries the draft + join so endCommit can build the wire
// create/relation AND the journal undo (drop the created object) + the ack re-key (negId→realId).
interface CommitCreate { draft: ExecObject; join: CreateJoin }
let commitCreates: CommitCreate[] | null = null;
const journal: JournalEntry[] = [];
let nextWsMsgId = 1;
// Host-action success callbacks (docs/plans/host-action-success-signal.md), keyed by the SENT op's
// msgId (the reply is stamped with the same id via WithId — no wire change). Registered by the
// hostAction hook when a call carries a trailing fn; invoked ONCE on that id's ok reply, then deleted
// (also deleted on that id's error reply — never invoked). A navigate-away before the reply just
// leaves the entry unreachable (same class as any other stale in-flight handler) — harmless.
const hostActionCallbacks = new Map<number, ExecFunction>();

// ── auto-live parse-op (M12) ────────────────────────────────────────────────────────────────────────
//
// The canvas evaluates a NEWLY EDITED expression WITHOUT the "Refresh values" click, via an on-demand
// PARSE round-trip that involves NO refetch — so it can never race the tree editor's optimistic mutations
// by construction (unlike S3a's removed auto-live attempt, which raced by keying the whole evalContext on
// the edited subgraph). codeExec.ts's evalCtxExpr calls wsHooks.parseMiss for every canvas expression text
// it can't find in the shipped `ctx.exprs` map; this section batches those misses, debounces one
// `parseExprs` request, and merges a successful parse DIRECTLY into the SAME `ctx` object the walk already
// holds — mutating its `exprs` props in place, never re-keying evalContext's own memo entry, never
// touching needsServerData/maybeRefetch. CLIENT-ONLY liveness machinery, like reactive props: no twin, no
// conformance case (codeExec.ts's own doc on WsHooks.parseMiss explains why).
//
// State is keyed PER evalContext object (a WeakMap on `ctx`'s identity), not one global set: a real
// "Refresh values" click mints a BRAND NEW ctx (a new `evalContext:<designId>:<refreshKey>` memo key — the
// empty-deps law, canvas-eval.md), so its WeakMap entry starts genuinely empty — the "failed" set below
// drops its history on Refresh with NO explicit clear, and a reply for an OLD ctx simply mutates an object
// nothing reads anymore (the stale-reply guard, "for free" via object identity — nothing to compare).
interface ParseMissState {
    pending: Set<string>;   // seen since the last flush, not yet sent
    inFlight: Set<string>;  // sent, awaiting parseExprsResult
    failed: Set<string>;    // requested, server had nothing (unparseable, or cap-truncated) — not re-asked
    timer: ReturnType<typeof setTimeout> | null;
}
const parseMissStateByCtx = new WeakMap<ExecObject, ParseMissState>();
const parseExprsDebounceMs = 300;
// Synthetic exprs-map entry ids for locally-merged parse results — never sent to the server, never looked
// up by id (evalCtxExpr/BuildEvalContext's exprs consumers always read by TEXT, never id — see codeExec.ts),
// so uniqueness only needs to avoid colliding with the SAME evalContext's own (small, near-zero) negative
// ids; a far-away counter makes that collision practically impossible without needing a registry.
let nextParseEntryId = -1_000_000_000;

interface ParseExprsRequest { ctx: ExecObject; state: ParseMissState; texts: string[]; }
const parseExprsRequests = new Map<number, ParseExprsRequest>();

// ── upload ticket (assets slice 2) ──────────────────────────────────────────────────────────────────
//
// Self-correlating request map, same shape as parseExprsRequests: a msgId → the Promise's resolve
// function, so the reply (dispatched early in onWsMessage, before the generic correlated-ack block —
// it is NOT a mutation ack) can hand the ticket back to whoever's awaiting requestUploadTicket().
const uploadTicketRequests = new Map<number, (ticket: string | null) => void>();
// A ticket request should never hang forever if the connection drops mid-flight (wsSend queues silently
// when the socket isn't open) — bounded so uploadBlob always eventually proceeds (with no ticket, which
// the ruled-instance edge will then correctly 401 on, surfacing the ordinary failure banner) rather than
// hanging on a broken connection.
const uploadTicketTimeoutMs = 5000;

function requestUploadTicket(): Promise<string | null> {
    return new Promise(resolve => {
        const msgId = nextWsMsgId++;
        let settled = false;
        const settle = (ticket: string | null) => {
            if (settled) return;
            settled = true;
            uploadTicketRequests.delete(msgId);
            resolve(ticket);
        };
        uploadTicketRequests.set(msgId, settle);
        setTimeout(() => settle(null), uploadTicketTimeoutMs);
        wsSend({ op: "uploadTicket", id: msgId, clientId: uiStatic.clientId });
    });
}

function parseMissStateOf(ctx: ExecObject): ParseMissState {
    let state = parseMissStateByCtx.get(ctx);
    if (state == null) {
        state = { pending: new Set(), inFlight: new Set(), failed: new Set(), timer: null };
        parseMissStateByCtx.set(ctx, state);
    }
    return state;
}

// wsHooks.parseMiss: record ONE miss and (re)arm the debounce. Skips a text already in flight or already
// known-unparseable-this-generation — no point re-asking until Refresh mints a fresh ctx or the operator
// edits the text into a genuinely different string (a different Set member, handled fresh on its own miss).
function recordParseMiss(ctx: ExecObject, text: string): void {
    if (text.length === 0) return;
    const state = parseMissStateOf(ctx);
    if (state.inFlight.has(text) || state.failed.has(text)) return;
    state.pending.add(text);
    if (state.timer == null)
        state.timer = setTimeout(() => flushParseMisses(ctx, state), parseExprsDebounceMs);
}

// Send the accumulated pending texts as ONE `parseExprs` request. Fires even while the socket isn't yet
// open (wsSend queues); the reply, when it eventually arrives, is still matched by msgId.
function flushParseMisses(ctx: ExecObject, state: ParseMissState): void {
    state.timer = null;
    const texts = Array.from(state.pending);
    state.pending.clear();
    if (texts.length === 0) return;
    for (const t of texts) state.inFlight.add(t);
    const msgId = nextWsMsgId++;
    parseExprsRequests.set(msgId, { ctx, state, texts });
    wsSend({ op: "parseExprs", id: msgId, texts });
}

// The parseExprsResult reply: merge every returned AST straight into ctx.exprs (mutating the LIVE memo'd
// object in place — the "purely local" merge; nothing re-keys evalContext, nothing refetches), then
// invalidate the EXACT (ctx, text) dependency evalCtxExpr recorded on its miss (codeExec.ts's recordProp
// piggyback — see its doc) via the ordinary invalidateProp channel, and repaint. The invalidate step is
// load-bearing: the canvas's rendered output sits behind a MEMOIZED component cache entry ("comp:<slot>")
// that a bare renderUi() would otherwise reuse verbatim (nothing else marks it stale — an isolated eval's
// OWN reads are deliberately thrown away, canvas-eval.md's empty-deps law), so the merge would be invisible
// on screen without it. A text the server had nothing for (omitted from `entries` — unparseable, or
// cap-truncated) joins `failed`, so it isn't re-asked until Refresh starts a fresh ctx.
function applyParseExprsResult(msgId: number, entries: { [text: string]: string }): void {
    const req = parseExprsRequests.get(msgId);
    parseExprsRequests.delete(msgId);
    if (req == null) return; // stray/duplicate reply — nothing to apply
    const { ctx, state, texts } = req;
    const exprsObj = ctx.props["exprs"];
    let changed = false;
    for (const text of texts) {
        state.inFlight.delete(text);
        const astJson = entries[text];
        if (astJson === undefined) { state.failed.add(text); continue; }
        if (exprsObj != null && exprsObj.type === "object") {
            exprsObj.props[text] = {
                type: "object", id: nextParseEntryId--,
                props: { text: { type: "text", value: text }, ast: { type: "text", value: astJson } },
            };
            invalidateProp(ctx.id, "parseMiss:" + text);
            changed = true;
        }
    }
    if (changed) renderUi(); // the walk now finds an AST for the newly-parsed text(s) — same shape as any other repaint
}

// Correlation for a commit's staged creates (atomic-commit Step B): the draft's transient negative id → how
// to re-key it on the ack. The ONE `commit` reply carries an idMap (negId→realId + the minted object's nested
// collection ids); the per-op `pendingAdds` covers only live single arrayAdds, so a commit's N creates need
// their own table. Populated at endCommit, consumed by the commit ack's batch remap, then cleared.
interface PendingCommitCreate { join: CreateJoin; obj: ExecObject }
const pendingCommitCreates = new Map<number, PendingCommitCreate>();

// ── data-conflict resolution state (M13 slice 6) ────────────────────────────────────────────────────
//
// A same-field COLLISION reply (`conflicts` present) is NOT the plain rollback: the draft is KEPT (mine
// stays in obj.props — the just-committed optimistic values are already there and we do NOT undo them), the
// generic form's coarse banner is driven by `ctx.conflicts`, and the user chooses. Per committing ctx we
// remember (a) how to RE-SEND its rejected commit (keep-mine = force at the now-fresh base) and (b) how to
// DROP mine back to the server's values (take-theirs, using the reply's per-field `theirs`). Keyed by the
// committing ctx (a WeakMap → drops with the ctx on unmount/navigation, no manual cleanup). The failed
// commit's msgId maps to its ctx so the reply can find it (the journal entry also carries `entry.ctx`, but
// a conflict-recovered entry is spliced without the ctx-status settle, so keep the direct link).
interface ConflictResolution {
    ctx: ExecCtx;
    resend(): void;                                 // re-fire this exact commit (keep-mine) at the fresh base
    editObjs: { obj: ExecObject; prop: string }[];  // the edits' targets — to overwrite with `theirs`
}
const conflictByMsgId = new Map<number, ConflictResolution>();
const conflictByCtx = new WeakMap<ExecCtx, ConflictResolution>();

// ── beforeunload guard (M13 Track-B B5) ──────────────────────────────────────────────────────────────
//
// While ANY ctx holds unresolved conflicts, the operator's draft (mine, kept in obj.props) is unsaved and
// unrecoverable: an F5 / tab-close silently loses it (slice 6's "accepted coarse limit"). Register a single
// window.beforeunload handler that prompts the browser's leave-confirmation when conflicts are outstanding,
// and remove it the moment they all resolve — so the guard is present ONLY when there is something to lose.
// The set tracks which ctxs are currently in conflict (a ctx enters on a conflict reply, leaves when its
// conflicts empty); the listener toggles on the set's non-emptiness. `beforeUnloadGuard` holds the single
// registered listener so it is added/removed exactly once (idempotent). Guarded on `typeof window` so the
// non-browser twin (never loaded, but defensive) and any headless context are inert.
const ctxsInConflict = new Set<ExecCtx>();
let beforeUnloadGuard: ((e: BeforeUnloadEvent) => void) | null = null;
function syncUnloadGuard(): void {
    if (typeof window === "undefined") return;
    if (ctxsInConflict.size > 0 && beforeUnloadGuard == null) {
        beforeUnloadGuard = e => { e.preventDefault(); e.returnValue = ""; };
        window.addEventListener("beforeunload", beforeUnloadGuard);
    } else if (ctxsInConflict.size === 0 && beforeUnloadGuard != null) {
        window.removeEventListener("beforeunload", beforeUnloadGuard);
        beforeUnloadGuard = null;
    }
}

// The single conflict-write path in ws.ts: set a ctx's conflicts (via codeExec's setCtxConflicts so the
// banner re-renders) AND maintain the beforeunload guard's tracking set. Non-empty items → the ctx is in
// conflict (guard on); empty → resolved (guard off if no other ctx conflicts). Every ws.ts conflict write
// (the reply populate, the coarse/fine resolutions' clear-to-[]/shrink) goes through here.
function applyCtxConflicts(ctx: ExecCtx, items: ExecValue[]): void {
    setCtxConflicts(ctx, items);
    if (items.length > 0) ctxsInConflict.add(ctx);
    else ctxsInConflict.delete(ctx);
    syncUnloadGuard();
}

// Three-lens review fix 4b: ctxs showing the transient "updated" status (take-theirs's confirmation,
// symmetric to keep-mine's commit-ack "saved") awaiting the refetch that follows to LAND, so the reply
// handler (onWsMessage's refetch branch) can settle each back to "idle" once the authoritative merge is
// in — a Set, not a WeakMap: iterated wholesale on every refetch reply (rare, small), and a ctx that
// unmounts before its refetch lands is simply dropped without ever needing to be looked up by identity.
const pendingUpdatedCtxs = new Set<ExecCtx>();

// One conflicted field as it arrives on the wire (M13 slice 6 — WsHandler.ConflictFieldWire). Base/mine/
// theirs are bare JSON scalars (a text string, a number, a bool, an ISO date, a ref id) or null.
type WireScalar = string | number | boolean | null;
interface WireConflict { object: number; typeName: string; field: string; base: WireScalar; mine: WireScalar; theirs: WireScalar; }

// The server's `theirs` value per (ctx, object, field), captured from a conflict reply so take-theirs can
// overwrite the edited field with it WITHOUT a round-trip. Keyed by ctx (WeakMap → drops with the ctx); the
// inner key is "object:field". Cleared implicitly when the ctx's conflict is resolved (a fresh reply
// rebuilds it). `undefined` from the lookup = no recorded theirs for that field (it wasn't the collision).
const takeTheirsValues = new WeakMap<ExecCtx, Map<string, WireScalar>>();
function takeTheirsValue(ctx: ExecCtx, objectId: number, prop: string): ExecValue | undefined {
    const raw = takeTheirsValues.get(ctx)?.get(objectId + ":" + prop);
    return raw === undefined ? undefined : wireScalarToExec(raw);
}

// A bare wire scalar (from a conflict payload) as an ExecValue for writing back into obj.props on take-mine's
// inverse (take-theirs). null → the tagged null; a number → int (the store's conflictable numeric fields are
// int/decimal — decimal round-trips through int only losslessly for whole values, but a conflict field's
// theirs is re-fetched authoritatively right after, so this write is a momentary display value the refetch
// corrects); a bool/text as-is. Refs (an id number) also land as int, which is the shape a ref prop's
// display already tolerates until the refetch replaces it.
function wireScalarToExec(v: WireScalar): ExecValue {
    if (v === null) return { type: "null" };
    if (typeof v === "boolean") return { type: "bool", value: v };
    if (typeof v === "number") return { type: "int", value: v };
    return { type: "text", value: v };
}

// One conflict as the Code-visible ExecObject the fine <ConflictBar> reads (M13 Track-B B5). Carries the
// full wire payload — object id, typeName, field, and base/mine/theirs as ExecValues — so the bar can group
// by object, label by type, and show mine-vs-theirs inline BEFORE the operator picks. id 0 = a transient
// display-only object (never reconciled/keyed as a real db object).
function conflictItem(c: WireConflict): ExecValue {
    return {
        type: "object", id: 0,
        props: {
            object: { type: "int", value: c.object },
            typeName: { type: "text", value: c.typeName },
            field: { type: "text", value: c.field },
            base: wireScalarToExec(c.base),
            mine: wireScalarToExec(c.mine),
            theirs: wireScalarToExec(c.theirs),
        } as { [name: string]: ExecValue },
    };
}

// A conflict reply for a journaled commit (M13 slice 6). Finds the commit's registered resolution (by
// msgId), retires the journal entry WITHOUT undoing it (mine stays in obj.props — the whole point), records
// the per-field `theirs` for take-theirs, builds the ctx's `ctx.conflicts` list (each `{ field }`) so the
// coarse banner renders, re-pins the ctx's base to the fresh version (so keep-mine forces), settles the
// Save status, ALSO surfaces the global error banner (the no-silent-clobber fallback for a custom render
// that ignores ctx.conflicts), and re-renders. Returns false when no registered commit matches (a conflict
// reply for a non-form/uncorrelated send — the caller falls back to the ordinary error path).
function handleConflictReply(msgId: number, error: string, conflicts: WireConflict[], newVersion?: number): boolean {
    const res = conflictByMsgId.get(msgId);
    conflictByMsgId.delete(msgId);
    if (res == null) return false; // not a registered form commit — let the plain-error path handle it
    const ctx = res.ctx;
    // Retire the failed commit's journal entry but do NOT undo it — mine stays. (rollbackJournal would undo,
    // reverting the form to base; the conflict path deliberately keeps the draft.) onReject clears the
    // create-remap bookkeeping just as a rollback would, without touching the optimistic values.
    const idx = journal.findIndex(e => e.msgId === msgId);
    if (idx >= 0) {
        const [failed] = journal.splice(idx, 1);
        failed.onReject?.();
        if (failed.ctx != null) { failed.ctx.pending = 0; setCtxStatus(failed.ctx, "idle"); }
    }
    // Record theirs per edited field, for take-theirs.
    const theirsMap = new Map<string, WireScalar>();
    for (const c of conflicts) theirsMap.set(c.object + ":" + c.field, c.theirs);
    takeTheirsValues.set(ctx, theirsMap);
    // Build the Code-visible conflict list (M13 Track-B B5 — FINE per-field UI): one object per collision
    // carrying the FULL payload the wire already ships — `object`/`typeName`/`field` plus base/mine/theirs
    // (converted from bare wire scalars to ExecValues via wireScalarToExec). The coarse bar used only
    // `{field}`; the fine <ConflictBar> groups by `object`, labels each group by `typeName`, and shows
    // mine-vs-theirs inline so the operator SEES both sides before choosing (the headline obligation). The
    // transient id 0 is display-only (never keyed/reconciled as a real object).
    applyCtxConflicts(ctx, conflicts.map(conflictItem));
    // Re-pin the ctx's base to the CURRENT version (the reply's newVersion) so a keep-mine re-commit forces
    // at a fresh base and the guard passes it. Never regress.
    if (typeof newVersion === "number") ctxBaseVersion.set(ctx, Math.max(ctxBaseVersion.get(ctx) ?? 0, newVersion));
    console.error("Commit rejected — conflicting changes:", error);
    // The global error banner (uiStatic.lastError, rendered by ui.ts) is the NO-SILENT-CLOBBER FALLBACK: an
    // app that never surfaces the conflict itself must still learn its edits weren't saved (the "reload"
    // advice is the only door there). But an app that DOES surface it — the generic <ConflictBar>, or a
    // custom render's own resolver reading ctx.conflicts — has a real door, and the global "reload = discard
    // your draft" banner would be redundant AND contradictory next to it. So: render once, then raise the
    // fallback banner ONLY if that render surfaced NO door for this ctx (conflictSurfacedThisRender). Keyed
    // on "did a resolver read ctx.conflicts", not on app type — a custom app opts out simply by handling the
    // conflict. Decided AFTER the render (refreshErrorBanner runs at commit); never a side-effect in render.
    // Today only ObjectForm opens a conflict-carrying staging ctx and it always renders <ConflictBar>, so the
    // fallback is a defensive net — its one clearly-reachable trigger is a navigate-away between Save and this
    // reply (the form gone → no door → the operator still learns the save failed on whatever page they're on).
    uiStatic.lastError = null; // this conflict supersedes any prior error; the fallback (if any) is set below
    renderUi();
    if (!conflictSurfacedThisRender.has(ctx.id)) { uiStatic.lastError = error; renderUi(); }
    return true;
}

// ── optimistic-concurrency anti-clobber (baseVersion) ───────────────────────────────────────────────
//
// DECISIONS.md "App versioning — the full design (M13 clump)" (§0's baseVersion bullet) + the pulled-
// ahead detection-only slice: two sessions editing the same object must not silently clobber each
// other. The server tracks each object's last-modified STORE VERSION; a ctx.commit() carries the
// version its ctx was based on (baseVersion); the server rejects iff an object the commit EDITS
// changed after that version (JsonFileInstanceStore.CommitBatch). This is the client half: remembering
// the version, and stamping it onto a committing ctx.
//
// clientKnownVersion is the client's own single remembered HEAD — kept simple (one number, not
// per-object) matching the design's "the client remembers it": every data-carrying reply (SSR first
// paint, a refetch) advances it, and EVERY successful mutating op's ack (not just `commit`'s — see
// onWsMessage) re-advances it to the version the server says that write landed at. This matters beyond
// `commit`: a LIVE write (autosave, a create's arrayAdd, a ref pick) bumps the store's HEAD exactly like
// a commit does, so the client must learn it from THAT ack too — otherwise it silently drifts behind its
// own history (e.g. create a user via arrayAdd, then ctx.commit() an edit to that SAME object: without
// this the edit's baseVersion would predate the create's own bump and be wrongly rejected as stale
// against itself — the regression this fixed, caught by Access.feature's "admin creates a user and sets
// a password" scenario). Seeded from the SSR first paint's window.initStoreVersion (set BEFORE this
// bundle loads — see UiLayout's inline bootstrap script).
let clientKnownVersion = initStoreVersion;

// A ctx's OWN baseVersion, pinned the FIRST time it stages real work (its first commit — see
// beginCommit), matching the design's "a ctx captures it when it opens/first stages": staying pinned
// across that SAME ctx's later commits until one actually succeeds (whose ack re-pins it to the fresh
// result — see the commit ack in onWsMessage) keeps a long-open form's base honest even if OTHER,
// unrelated client activity advances clientKnownVersion in the meantime (a refetch triggered by a
// different part of the page must not silently make a stale-loaded form look fresh). A WeakMap needs no
// manual cleanup — an entry drops with its ctx (component unmount / navigation) automatically.
const ctxBaseVersion = new WeakMap<ExecCtx, number>();

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
// Returns the retired entry's ctx (or undefined — no match, or a ctx-less live edit), so a `commit` op's
// ack (onWsMessage) can re-pin that ctx's anti-clobber baseVersion to this commit's result.
function commitJournal(msgId: number): ExecCtx | undefined {
    const idx = journal.findIndex(e => e.msgId === msgId);
    if (idx < 0) return undefined;
    const [done] = journal.splice(idx, 1);
    // Form-Save feedback: as this commit's last in-flight send retires, settle the form to "Saved" and
    // re-render so the inline indicator updates (the ONE new wire — the ack path did not re-render before).
    // setCtxStatus invalidates the ctx-status var dep so the memoized component view actually recomputes.
    if (done.ctx != null && --done.ctx.pending === 0 && done.ctx.status === "saving") {
        setCtxStatus(done.ctx, "saved");
        renderUi();
    }
    return done.ctx;
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

// Drop a staged create's optimistic row (atomic-commit Step B): the commit journal entry's undo reverts
// it on a server reject — remove the draft from the set it was added to (set join) or unset the reference
// it was pointed at (ref join), so a denied changeset leaves NO orphan row. invalidateMember/Prop re-renders.
function dropStagedCreate(c: CommitCreate): void {
    if (c.join.kind === "set") {
        const i = c.join.set.items.findIndex(it => it.value === c.draft);
        if (i >= 0) c.join.set.items.splice(i, 1);
        invalidateMember(c.join.set.id);
    } else {
        if (c.join.parent.props[c.join.prop] === c.draft) c.join.parent.props[c.join.prop] = { type: "null" };
        invalidateProp(c.join.parent.id, c.join.prop);
    }
}

// Re-apply a staged create's optimistic row (atomic-commit Step B): the commit journal entry's redo, used
// when an EARLIER entry's rollback reverse-replayed this one. The mirror of dropStagedCreate.
function restageCreate(c: CommitCreate): void {
    if (c.join.kind === "set") {
        if (!c.join.set.items.some(it => it.value === c.draft))
            c.join.set.items.push({ key: c.draft.id, value: c.draft });
        invalidateMember(c.join.set.id);
    } else {
        c.join.parent.props[c.join.prop] = c.draft;
        invalidateProp(c.join.parent.id, c.join.prop);
    }
}

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
const deadSessionReloadKey = "deenv.deadSessionReloaded";

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

function assetUrl(path: string): string {
    const b = basePrefix();
    return initAssetAuthority
        ? `${location.protocol}//${initAssetAuthority}${b}${path}`
        : `${b}${path}`;
}

async function persistLogin(name: unknown, password: unknown): Promise<void> {
    await fetch(assetUrl("/session"), {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name, password })
    });
}

async function persistLogout(): Promise<void> {
    await fetch(assetUrl("/session"), { method: "DELETE", credentials: "include" });
}

// uploadBlob(file): POST a File's raw bytes to the instance's blob pool upload edge
// (docs/plans/assets-design.md), returning the pool-assigned content-addressed name
// (`<hash>.<ext>`), or "" on any failure (network error, non-2xx, an unexpected body) — the caller
// (ui.ts's file-input wiring) treats "" as "nothing to write" and leaves the bound field untouched.
// `assetUrl("/assets")` is THIS file's own idiom for reaching a non-app-tree endpoint (the /session
// precedent above) — the same base sys.assetUrl/window.initBlobBase resolves server-side, so upload
// and display agree on one origin without a second base computation.
//
// UPLOAD TICKET (assets slice 2, §2): the ambient session cookie doesn't ride to the asset origin, so a
// ruled instance needs a ticket. The client does NOT duplicate the floor's dormant/ruled posture logic —
// it always TRIES to fetch one over the already-authenticated WS (requestUploadTicket), and the server
// decides whether one is needed (null back on a dormant floor or an anonymous session — see
// HandleUploadTicket); the header is sent only when a ticket came back, which is exactly right on a
// dormant instance too (no ticket sent, matching slice 1's behavior verbatim there).
//
// UX review fix: a failure used to just return "" with NOTHING shown — an operator hitting the
// (tested) 10 MB cap saw a dead control. Routes into the SAME global error banner (uiStatic.lastError,
// rendered by ui.ts) the rejected-commit path uses (rollbackJournal above) — one failure surface, not
// a bespoke one just for images.
async function uploadBlob(file: File): Promise<string> {
    try {
        const ticket = await requestUploadTicket();
        const headers: Record<string, string> = { "Content-Type": file.type || "application/octet-stream" };
        if (ticket != null) headers["X-Upload-Ticket"] = ticket;
        const res = await fetch(assetUrl("/assets"), {
            method: "POST",
            headers,
            body: file,
        });
        if (!res.ok) {
            uiStatic.lastError = uploadFailureMessage(res.status);
            renderUi();
            return "";
        }
        const data = await res.json() as { name?: unknown };
        if (typeof data.name === "string") return data.name;
        uiStatic.lastError = "Image upload failed — unexpected server response.";
        renderUi();
        return "";
    } catch {
        uiStatic.lastError = "Image upload failed — network error.";
        renderUi();
        return "";
    }
}

function uploadFailureMessage(status: number): string {
    if (status === 401) return "Upload requires login.";
    if (status === 413) return "Image upload failed — file too large.";
    if (status === 415) return "Image upload failed — unsupported file type.";
    return "Image upload failed — rejected by the server.";
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
            // Inside a commit bracket (beginCommit..endCommit): buffer the edit into the batch instead of
            // sending an individual objectPropChange. The local optimistic write (obj.props[prop] = val) and
            // the cache invalidation have already happened in codeExec.ts before this hook fires, so the
            // render is already correct; only the server send is deferred. endCommit flushes ONE `commit`.
            if (commitEdits != null) {
                commitEdits.push({ obj, prop, value, before,
                    wire: { objectId: obj.id, prop, value: scalarOf(value) } });
                return;
            }
            // Live edit (autosave, path-write, live collection op) — send immediately, unchanged.
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
        // as the target id). `callback` (docs/plans/host-action-success-signal.md) — codeExec.ts has
        // already split it OUT of `args` before this hook ever sees them (so it never scalarOf's to a
        // bogus null wire arg) — is registered under THIS send's msgId; onWsMessage's hostAction
        // ok-branch looks it up by the REPLY's id and invokes it before the refetch.
        hostAction: (action, args, callback) => {
            const msgId = nextWsMsgId++;
            if (callback != null) hostActionCallbacks.set(msgId, callback);
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
            const n = bareScalar(name), p = bareScalar(password);
            persistLogin(n, p).finally(() =>
                wsSend({ op: "login", clientId: uiStatic.clientId, name: n, password: p }));
        },
        // The MIRROR of login (sys.logout, M-auth login UI 1e-2). Clears the session's principal back to
        // anonymous over the WS — no credentials, just the clientId (the server's `logout` op is idempotent
        // and always replies ok). Like login it stages NOTHING and pushes NO journal entry; the REPLY
        // (handled in onWsMessage) refetches so the page swaps the root view back to the anonymous gate at
        // the SAME URL. No correlation id — the reply is recognized by its `op` ("logout").
        logout: () => {
            persistLogout().finally(() =>
                wsSend({ op: "logout", clientId: uiStatic.clientId }));
        },
        // Form-Save feedback + atomic-commit batch: open the commit bracket so the propChange hook buffers
        // edits (Step A) and the commitCreate hook buffers creates (Step B) instead of firing individual
        // objectPropChange / arrayAdd / setReferenceField messages. endCommit flushes the whole changeset as
        // ONE `commit` message (ONE journal entry, ONE stateGen bump) the server applies all-or-none.
        beginCommit: ctx => {
            // ponytail: single-level only — a commit never nests inside a commit (the staged-walk never
            // calls commit()), so the buffers are reset unconditionally with no txDepth guard. Step B
            // preserves that invariant: the create-mode form's inner commit fires BEFORE the enclosing
            // form's, never DURING its staged-walk (a nested commit only transfers creates up, no send).
            committingCtx = ctx;
            commitEdits = [];     // open the edit buffer — propChange hooks into it from now on
            commitCreates = [];   // open the create buffer — commitCreate hooks into it from now on
            // Anti-clobber: pin this ctx's baseVersion the FIRST time it commits (its "first stages" —
            // there is no earlier client-side hook point; ctx.new()/staging writes fire no wsHook — see the
            // ctxBaseVersion doc above for why this stays correct in practice). A LATER commit from the
            // SAME (still-mounted) ctx keeps its ALREADY-pinned base — re-pinning here would use
            // clientKnownVersion at THIS commit's time, discarding the fact that an intervening commit from
            // this ctx may already have advanced it more precisely (the ack handler does that re-pin).
            if (!ctxBaseVersion.has(ctx)) ctxBaseVersion.set(ctx, clientKnownVersion);
        },
        // A staged create in a final commit (atomic-commit Step B): buffer the draft + its join. endCommit
        // turns each into a `creates` entry (the draft's scalar props) + a `relations` entry (the set/ref the
        // server links it into — which is ALSO how the server resolves the create's TYPE, so no type is sent).
        commitCreate: (draft, join) => { commitCreates?.push({ draft, join }); },
        endCommit: () => {
            const edits = commitEdits ?? [];
            const creates = commitCreates ?? [];
            commitEdits = null; commitCreates = null; // close the buffers — later mutations go the live path
            if (edits.length === 0 && creates.length === 0) { committingCtx = null; return; } // nothing staged
            // Edits: the per-edit (obj, prop, before/value) captured by propChange at the moment each write
            // landed (Step A). Creates: each draft's transient id correlated for the ack's batch remap, plus
            // the wire create (its scalar props) and relation (set/ref). ONE journal entry → ONE ack commits
            // all (remapping every created id), ONE reject rolls all back atomically (edits reverted AND
            // created rows dropped). Roots = every edited object + every created object (kept alive for undo).
            const editSnapshots = edits.map(e => ({ obj: e.obj, prop: e.prop, before: e.before, after: e.value }));
            const wireCreates: object[] = [];
            const wireRelations: object[] = [];
            for (const c of creates) {
                pendingCommitCreates.set(c.draft.id, { join: c.join, obj: c.draft });
                // `value` carries the draft's scalar props in the SAME tagged { props: { name: leaf } } shape
                // an arrayAdd ships (objectOf), so the server reads it with the SAME ExecObjectValue.
                wireCreates.push({ tempId: c.draft.id, value: objectOf(c.draft) });
                if (c.join.kind === "set")
                    wireRelations.push({ kind: "set", setId: c.join.set.id, childId: c.draft.id });
                else
                    wireRelations.push({ kind: "ref", parentId: c.join.parent.id, prop: c.join.prop, childId: c.draft.id });
            }
            // The committing ctx (form-Save lifecycle + M13 slice 6 conflict resolution). Read BEFORE clearing
            // committingCtx (the only reference to which ctx this bracket belongs to).
            const ctx = committingCtx;
            // sendCommit fires the SAME batch at a supplied base — reused for keep-mine's FORCE re-commit at
            // the now-fresh base (M13 slice 6). Each call allocates a fresh msgId + journal entry (so the
            // re-commit is journaled/ack'd/rejected like any commit); on a conflict reply it re-registers the
            // resolution keyed by the NEW msgId. committingCtx must be set while recordMutation runs (it tags
            // entry.ctx for the Save lifecycle), so sendCommit brackets it.
            function sendCommit(baseVersion: number | undefined): void {
                const msgId = nextWsMsgId++;
                const prevCommitting = committingCtx;
                committingCtx = ctx; // recordMutation reads this to tag entry.ctx (Save lifecycle)
                recordMutation({
                    msgId,
                    undo: () => {
                        for (const s of editSnapshots) { s.obj.props[s.prop] = s.before; invalidateProp(s.obj.id, s.prop); }
                        for (const c of creates) dropStagedCreate(c);
                    },
                    redo: () => {
                        for (const s of editSnapshots) { s.obj.props[s.prop] = s.after; invalidateProp(s.obj.id, s.prop); }
                        for (const c of creates) restageCreate(c);
                    },
                    // The created objects are reachable through their join's set/parent (listed below), but a ref
                    // create's object sits in a field, so list each draft explicitly too — the reachability GC
                    // marks it alive until this entry retires (it must survive for the undo to drop it).
                    onReject: () => { for (const c of creates) pendingCommitCreates.delete(c.draft.id); },
                    roots: [...edits.map(e => e.obj), ...creates.map(c => c.draft),
                            ...creates.map(c => c.join.kind === "set" ? c.join.set : c.join.parent)],
                });
                committingCtx = prevCommitting;
                // Register how to resolve a conflict on THIS send (M13 slice 6): keep-mine re-fires at the
                // fresh base the reply re-pinned; take-theirs overwrites the edits' targets with the reply's
                // `theirs`. Keyed by msgId (the reply carries it) and by ctx (the banner's buttons address it).
                if (ctx != null) {
                    const res: ConflictResolution = {
                        ctx,
                        resend: () => sendCommit(ctxBaseVersion.get(ctx)),
                        editObjs: editSnapshots.map(s => ({ obj: s.obj, prop: s.prop })),
                    };
                    conflictByMsgId.set(msgId, res);
                    conflictByCtx.set(ctx, res);
                }
                // Rebuild the wire edits from the CURRENT prop values at send time (NOT a snapshot captured at
                // the first commit). This matters for the B5 per-field resolution's resend: a take-theirs field
                // was overwritten in obj.props with the server's value, so the re-commit must carry THEIRS (a
                // no-op at the fresh base), while a keep-mine field still holds mine (a deliberate force). The
                // ORIGINAL send reads the same props (= mine), so it is unchanged. scalarOf handles a null prop.
                const wireEdits = editSnapshots.map(s => ({ objectId: s.obj.id, prop: s.prop, value: scalarOf(s.obj.props[s.prop]) }));
                wsSend({ op: "commit", id: msgId, clientId: uiStatic.clientId, baseVersion,
                    edits: wireEdits, creates: wireCreates, relations: wireRelations });
            }
            const baseVersion = ctx != null ? ctxBaseVersion.get(ctx) : undefined;
            committingCtx = null; // close the bracket; sendCommit re-sets it around recordMutation
            sendCommit(baseVersion);
        },
        // Conflict resolution (M13 slice 6) — the coarse banner's buttons (via codeExec's ctx.keepMine /
        // ctx.takeTheirs). keepMine re-fires the rejected commit at the ctx's now-fresh base (a deliberate
        // FORCE overwrite of theirs — chosen consent is the whole point). takeTheirs drops mine: overwrite
        // each edited field with the server's value (recorded on the conflict reply) + a refetch, and clear
        // the conflict. Both clear ctx.conflicts (the banner disappears).
        keepMine: ctx => {
            const res = conflictByCtx.get(ctx);
            if (res == null) return;
            conflictByCtx.delete(ctx);
            applyCtxConflicts(ctx, []); // leave conflict mode (guard off); the resend's own reply drives the next outcome
            // Three-lens review fix 1: the global banner is a REJECTION notice ("your edits were NOT saved…
            // reload"). Choosing Keep mine is the resolution — clearing it here (not at handleConflictReply's
            // SET, which stays unconditional/load-bearing for no-silent-clobber) means the banner never
            // outlives the state it described; leaving the OLD "reload" text up here would be actively WRONG
            // (the just-forced commit has NOT reloaded and the draft is about to be re-sent, not stale).
            uiStatic.lastError = null;
            res.resend();
        },
        takeTheirs: ctx => {
            const res = conflictByCtx.get(ctx);
            if (res == null) return;
            conflictByCtx.delete(ctx);
            // Drop mine: restore each edited field to the server's current value (captured on the conflict
            // reply, keyed by object+field). A field with no recorded `theirs` (edited but not itself the
            // collision) is left as mine — take-theirs resolves the COLLISION; the refetch reconciles the rest.
            for (const eo of res.editObjs) {
                const theirs = takeTheirsValue(ctx, eo.obj.id, eo.prop);
                if (theirs !== undefined) { eo.obj.props[eo.prop] = theirs; invalidateProp(eo.obj.id, eo.prop); }
            }
            applyCtxConflicts(ctx, []);
            // Three-lens review fix 1: same as keepMine — the rejection banner no longer describes reality
            // once the user has resolved by taking theirs (the draft is gone, values are now authoritative).
            uiStatic.lastError = null;
            needsServerData = true; // refresh authoritative state (theirs) for anything beyond the edited fields
            // Three-lens review fix 4b: a transient confirmation symmetric to Keep-mine's "Saved" — surfaced
            // through the SAME client-only ctx.status mechanism (setCtxStatus). "updated" is CLIENT-only,
            // like ctx.conflicts/ctx.status themselves (documented on ExecCtx.status's declaration; no twin
            // case — the C# side already returns the constant "idle" for ANY status read). Registered in
            // pendingUpdatedCtxs so the NEXT refetch reply (the one this take-theirs triggers) settles it
            // back to "idle" once the authoritative merge lands — mirrors how a commit ack settles "saving"
            // to "saved" via ctx.pending, just without a correlated msgId (a refetch reply is uncorrelated).
            setCtxStatus(ctx, "updated");
            pendingUpdatedCtxs.add(ctx);
            renderUi();
            maybeRefetch();
        },
        // Per-field resolution (M13 Track-B B5 — the fine <ConflictBar>'s per-field buttons, via codeExec's
        // ctx.resolveField(object, field, take)). The model PROGRESSIVELY SHRINKS ctx.conflicts, deliberately
        // sidestepping a client-only picks COLLECTION (a known framework constraint — a component-state array
        // can't round-trip a refetch; B4 hit it). Each call resolves ONE (object, field):
        //   • take=true (Take theirs): write the server's value (recorded on the conflict reply, keyed by
        //     object:field) into that edited field's obj.props — the SAME per-field overwrite whole-ctx
        //     takeTheirs does — then remove the item. mine is discarded for this field only.
        //   • take=false (Keep mine): just remove the item — mine already sits in obj.props, nothing to write.
        // When the LAST item is removed, ctx.conflicts is empty → re-commit at the fresh base via res.resend()
        // (mirroring keepMine): keep-mine fields FORCE-overwrite; take-theirs fields now equal the server's
        // current value so they no-op — no re-conflict. The re-commit's ack drives the coarse "Saved"/banner-
        // clear path (slice 6). CLIENT-only; the failed-commit edits + theirs values live here (recorded on
        // the reply), so the C# twin no-ops (like keepMine/takeTheirs — twin parity, no conformance).
        resolveField: (ctx, object, field, take) => {
            const res = conflictByCtx.get(ctx);
            if (res == null) return;
            if (take) {
                const eo = res.editObjs.find(e => e.obj.id === object && e.prop === field);
                if (eo != null) {
                    const theirs = takeTheirsValue(ctx, object, field);
                    if (theirs !== undefined) { eo.obj.props[field] = theirs; invalidateProp(object, field); }
                }
            }
            // Shrink ctx.conflicts by this one (object, field). Each item is the display object built by
            // conflictItem — its `object`/`field` props identify it.
            const remaining = ctx.conflicts.filter(c =>
                !(c.type === "object" && c.props["object"]?.type === "int" && c.props["object"].value === object
                    && c.props["field"]?.type === "text" && c.props["field"].value === field));
            applyCtxConflicts(ctx, remaining);
            if (remaining.length === 0) {
                // All fields resolved — leave conflict mode and re-commit at the fresh base (keep-mine forces,
                // take-theirs no-ops). Mirrors the coarse keepMine finalize: clear the global rejection banner
                // (it no longer describes reality) and delete the ctx's registration (the resend re-registers).
                conflictByCtx.delete(ctx);
                uiStatic.lastError = null;
                res.resend();
            } else {
                renderUi();
            }
        },
        // M12 auto-live parse-op — see the section doc above (module top) + codeExec.ts's WsHooks.parseMiss.
        parseMiss: (ctx, text) => recordParseMiss(ctx, text),
    });
}

function onWsMessage(msg: { op?: string; id?: number; tempId?: number; newId?: number; ok?: boolean;
                            collections?: { [prop: string]: { id: number; elementTypeName?: string } };
                            idMap?: { tempId: number; realId: number; collections?: { [prop: string]: { id: number; elementTypeName?: string } } }[];
                            newVersion?: number;
                            sessionAlive?: boolean;
                            conflicts?: WireConflict[];
                            entries?: { [text: string]: string };
                            ticket?: string; exp?: number;
                            state?: ServerDtState; error?: string }): void {
    // M12 auto-live parse-op — dispatched FIRST, ahead of the correlated mutating-ack block below. A
    // parseExprsResult reply carries a (self-correlating, via parseExprsRequests) numeric `id` but is NOT a
    // mutation ack: it stages nothing, journals nothing, and must not be routed through the ack path. Review
    // fix: that block's success leg (no `msg.error`) unconditionally clears `uiStatic.lastError` (:1093 pre-
    // fix) — a read-only parse reply arriving while the "your edits were NOT saved… reload" safety banner is
    // up would silently dismiss it (plus a wasted second renderUi, since applyParseExprsResult already
    // repaints when it merges). Returns unconditionally — a parseExprsResult reply carries no `state`/
    // `newVersion`/`conflicts` any later branch could still act on.
    if (msg.op === "parseExprsResult" && typeof msg.id === "number" && msg.entries != null) {
        applyParseExprsResult(msg.id, msg.entries);
        return;
    }
    // uploadTicket (assets slice 2) — dispatched FIRST for the SAME reason as parseExprsResult: it is a
    // self-correlating request/reply, not a mutation ack (stages nothing, journals nothing), and must not
    // fall into the correlated ack block below (which would try to commit/rollback a non-existent journal
    // entry for this id). A ticket is present when the server minted one; absent (undefined) otherwise —
    // both collapse to the resolver receiving `null`.
    if (msg.op === "uploadTicket" && typeof msg.id === "number") {
        uploadTicketRequests.get(msg.id)?.(msg.ticket ?? null);
        return;
    }
    // Correlated accept/reject first: an error rolls the journal back, an ok commits.
    if (typeof msg.id === "number") {
        // A same-field COLLISION reply (M13 slice 6): `conflicts` present alongside `error`. Handle it BEFORE
        // the plain-error branch — that branch runs the journal UNDO (reverting the form to its base), which
        // would discard MINE. A conflict KEEPS mine (the just-committed optimistic values stay in obj.props)
        // and drives the coarse banner; see handleConflictReply.
        if (msg.conflicts != null && msg.error != null && handleConflictReply(msg.id, msg.error, msg.conflicts, msg.newVersion))
            return;
        // Not a conflict: this commit's send resolved (ok or a plain reject), so drop its conflict-resolution
        // registration (a form commit registers one per send — see sendCommit; without this it would leak).
        conflictByMsgId.delete(msg.id);
        if (msg.error != null) {
            // A registered success callback for this id NEVER runs on error (docs/plans/host-action-
            // success-signal.md) — drop the registration so it can't fire later (it can't: msgIds are
            // one-shot per send) and can't leak either.
            hostActionCallbacks.delete(msg.id);
            // A non-journaled send (a host action stages nothing): no journal entry matches, so
            // surface the error and re-render WITHOUT a journal replay.
            if (!rollbackJournal(msg.id, msg.error)) {
                uiStatic.lastError = msg.error;
                console.error("Host action rejected by the server:", msg.error);
                renderUi();
            }
            return;
        }
        const committedCtx = commitJournal(msg.id);
        // Three-lens review fix 1: ANY successful mutating ack proves forward progress, which makes a prior
        // rejection notice (uiStatic.lastError — a REJECTION banner, "your edits were NOT saved… reload")
        // stale REGARDLESS of which ctx it came from — it is single shared page state, not per-ctx, so a
        // plain-Save on ctx B must not leave ctx A's earlier conflict banner up describing a state that no
        // longer holds. This is the "plain-Save-resolution path" the coarse bar's own buttons already cover
        // via their own explicit clears (keepMine/takeTheirs) — this is the fallback for every OTHER ack.
        // renderUi() ONLY when it actually changed (commitJournal itself renders on a ctx settling to
        // "saved", but a LIVE-edit ack — autosave, no ctx — does not; without this the DOM banner would go
        // stale until some unrelated later render happened to run).
        if (uiStatic.lastError != null) { uiStatic.lastError = null; renderUi(); }
        // An atomic-commit (Step B) `commit` ack: batch-remap every created object's transient id to the real
        // one the server minted (negId→realId + the minted nested-collection ids). Done AFTER commitJournal
        // retires the entry, so the optimistic rows now address their real ids. (A commit with only edits
        // carries no idMap — no-op.)
        if (msg.op === "commit" && msg.idMap != null && msg.idMap.length > 0) applyCommitRemap(msg.idMap);
        // Anti-clobber: EVERY successful mutating op's ack carries newVersion (write/addEntry/removeEntry/
        // objectPropChange/commit/setReferenceField/arrayAdd/arrayRemove — see WsHandler.cs's response
        // records), not just `commit`'s. Advance clientKnownVersion universally (never regress: take the
        // max) — a LIVE write (autosave, a create's arrayAdd, a ref pick) bumps the store's HEAD exactly
        // like a commit does, and the client must not silently drift behind its OWN live writes: a create
        // via arrayAdd, immediately followed by a ctx.commit() editing that SAME just-created object, would
        // otherwise send a stale baseVersion and be wrongly rejected against its own history (the bug this
        // fixes — reproduced by Access.feature's "admin creates a user and sets a password" scenario,
        // whose password edit follows the user's arrayAdd create). A `commit` ack ADDITIONALLY re-pins the
        // COMMITTING ctx's own base (committedCtx is undefined for every other op, so this is a no-op then),
        // so its NEXT commit (scenario: two saves in a row from one session) is based on what it just
        // applied, not the version it opened with.
        if (typeof msg.newVersion === "number") {
            clientKnownVersion = Math.max(clientKnownVersion, msg.newVersion);
            if (committedCtx != null) ctxBaseVersion.set(committedCtx, msg.newVersion);
        }
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
        if (msg.sessionAlive === false && sessionStorage.getItem(deadSessionReloadKey) !== "1") {
            sessionStorage.setItem(deadSessionReloadKey, "1");
            location.reload();
            return;
        }
        if (msg.sessionAlive !== false) sessionStorage.removeItem(deadSessionReloadKey);
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
    } else if (msg.op === "hostAction" && msg.ok) {
        // A host action (sys.create / sys.delete / sys.rename / etc.) completed server-side. A host
        // action can change a MEMOIZED store set the live view renders — e.g. the operator IDE now lists
        // db.instances (a set inside a `comp:` SetTable), and sys.create/delete/rename mutate it through
        // the kernel mirror, NOT through the journaled mutation path that calls invalidateMember. The
        // refetch MERGES the fresh membership into the set's array, but the SetTable component's foreach
        // memo (a `comp:` entry, preserved across a plain merge) would keep re-handing back its stale row
        // tree. resetViewState() drops the `comp:` slot-cache (ui.ts) — the SAME wholesale-rebuild fix
        // login/logout use — so the SetTable re-renders fresh over the merged set. (A bare refetch alone
        // sufficed only while the list read sys.instances, re-built per-render from the live registry and
        // never memoized as a set.) Client-only orchestration: no twin, no conformance.
        //
        // ALSO drop any `publishPreview:`/`mergePreview:` server-backed read (M13 Track-B B3/B4): a publish or
        // mergeBranch host action changes exactly the state those reads reflect (the target's data file + its
        // published-commit stamp; a branch's head after a merge commit), and those entries are cached with
        // EMPTY deps (like diffCommits/schema — see codeExec.ts), so no store-mutation invalidation ever
        // staled them. Without this drop, re-opening the preview after an Apply would reuse the pre-apply
        // report (still showing the just-applied changes, or a merge conflict/Merge button that already
        // landed) instead of the fresh "up to date"/"already merged" state. Cheap + precise: keyed by prefix,
        // normally a handful of entries. `diffCommits:` is deliberately NOT dropped — a merge or publish never
        // rewrites committed history, so a commit-to-commit diff stays valid. (Any host action can move a
        // target — create/delete/rename change the instance set previews list over — so this is unconditional
        // on hostAction, not publish/merge-only.)
        for (const key of Array.from(uiStatic.cache.keys()))
            if (key.startsWith("publishPreview:") || key.startsWith("mergePreview:")) uiStatic.cache.delete(key);
        // The success callback (docs/plans/host-action-success-signal.md), keyed by THIS reply's id (so
        // two in-flight actions each run only their own). Runs BEFORE resetViewState/refetch — an app
        // clearing its own ui vars in the callback must not be resurrected by the refetch that follows
        // (the A2 scalar carve-out makes a same-scalar merge a no-op, so this ordering is safe). A
        // throwing callback must never skip the refresh of a successfully-applied action — try/finally.
        //
        // Callbacks are FULL handlers, routed through the SAME idiom onClick uses (ui.ts:687): memo-
        // bypassed + a commit-on-success transaction (runHandlerTransaction). Consistency-by-construction
        // — a future callback that stages a write or fires a NESTED host action must behave exactly like
        // any other handler (atomic, buffered sends, VNA/action-miss handling), not a special bare-call
        // path. No `action` (the action-miss re-invoke identity) is passed: a callback isn't reached via a
        // render-slot closure the way onClick is, so it has no (fnId, slot) to record — a VNA inside a
        // callback falls back to runHandlerTransaction's un-recorded flush-and-rethrow leg, same as any
        // handler built outside a render.
        const callback = typeof msg.id === "number" ? hostActionCallbacks.get(msg.id) : undefined;
        if (typeof msg.id === "number") hostActionCallbacks.delete(msg.id);
        try {
            if (callback != null)
                runHandlerTransaction(() =>
                    runWithMemoBypass(() => callFunction(callback, { lastId: uiStatic.lastId, ambient: rootAmbient() })));
        } finally {
            resetViewState();
            maybeRefetch();
        }
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
        // Anti-clobber: this refetch's render reflects the store as of msg.state.storeVersion — advance
        // clientKnownVersion to it (never regress: a superseded-gen reply already returned above, but an
        // ordinary in-order reply's version only ever moves forward). A ctx already pinned to an OLDER
        // base (mid-edit when this refetch landed) keeps that pin — see ctxBaseVersion's doc.
        if (typeof msg.state.storeVersion === "number") clientKnownVersion = Math.max(clientKnownVersion, msg.state.storeVersion);
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
        // Three-lens review fix 4b: this refetch's merge is the authoritative state a take-theirs asked
        // for — settle every ctx still showing "updated" back to "idle" (the confirmation has done its
        // job; the merged data speaks for itself now) and invalidate their status var deps so a rendered
        // indicator drops it. Cheap: the set is normally empty (populated only by takeTheirs).
        if (pendingUpdatedCtxs.size > 0) {
            for (const c of pendingUpdatedCtxs) if (c.status === "updated") setCtxStatus(c, "idle");
            pendingUpdatedCtxs.clear();
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
//   • TRANSIENT object (negative id) → by VALUE — its props ({ type:"object", props:{…} }) RECURSIVELY
//     (a nested transient draft ships whole, an in-store prop as an id-ref, null present-and-null), which
//     the server reconstructs as a throwaway transient graph and discards after harvesting. This is the
//     COMMON case for component state: a `var state = { open: false }` is a transient object (a
//     component-local object is non-top, so a `state.open = …` toggle re-renders via invalidateProp — a
//     SCALAR `var` in a component scope is NOT reactive and so is never the toggle), so the round-trip
//     MUST carry it. The recursion matters when that state HOLDS a nested transient draft (SetTable's
//     `state.draft = sys.new(desc)`): the open create-form renders RefSelect/Field over the draft, and
//     only a draft that round-trips whole lets the server reproduce the open form and harvest the data its
//     `foreach db.designs` demands (no hidden footprint anchor needed). It is pure VIEW-STATE (a popup-open
//     flag + a fresh default-valued draft), not a draft that FEEDS a query — the harvested data depends on
//     WHICH branch runs / WHETHER the form is open, never on the object's field VALUES — so a crafted value
//     can't widen the floor-gated read (I3). (Draft-objects whose VALUES DRIVE a query stay deferred; they
//     are a different concern, and this change does not claim to support them.) See stateValueOf below.
function slotState(): { [slotKey: string]: { [name: string]: object } } {
    const out: { [slotKey: string]: { [name: string]: object } } = {};
    for (const [key, entry] of uiStatic.cache) {
        if (!key.startsWith("comp:") || key.endsWith(":view")) continue; // the setup entry, not its :view
        if (entry.stale || entry.result.type !== "fn") continue;          // a render closure = a stateful component
        const locals: { [name: string]: object } = {};
        for (const [name, item] of Object.entries(entry.result.scope.items)) {
            if (item.isReadOnly) continue; // a bound param — the server re-binds it; only `var` state ships
            const v = item.value;
            // GUARD (loud, not silent — DECISIONS "Loud guards over silent failures"): a collection placed
            // DIRECTLY in a component's `var state` is the client-only-transient-collection case slotState
            // does NOT ship (it would re-introduce collection identity; the server re-loads store collections
            // fresh). Nothing does this today; if a component adds it, fail HERE with a clear message rather
            // than silently dropping it → a mystery empty render downstream. (A collection NESTED in a draft —
            // a fresh `sys.new` set prop — is the benign case transientPropsOf skips; see its note.)
            if (v.type === "array")
                throw new Error(`slotState: component view-state local '${name}' is a collection — a `
                    + `client-only transient collection in a component's var state is not shipped yet (it `
                    + `would re-introduce collection identity). Nest store-backed data, or extend slotState.`);
            locals[name] = stateValueOf(v); // scalar by value · in-store object → id-ref · transient → by value (recursive)
        }
        if (Object.keys(locals).length > 0) out[key] = locals;
    }
    return out;
}

// One component view-state value as a tagged wire value, on the SAME identity axis slotState's top level
// uses (does the server already hold this object? = id sign): a scalar by value; an IN-STORE object
// (positive id) as an id-ref the server resolves against its canonical load; a TRANSIENT object (negative
// id) BY VALUE — { type:"object", props:{…} } with EVERY prop recursed through this same function — so a
// nested transient draft (e.g. SetTable's `state.draft = sys.new(desc)`) round-trips whole; null/other as
// its tagged form ({ type:"null" }), so a fresh draft's `design: null` arrives PRESENT-and-null (sys.field
// must find the prop, not throw on absence). Like first-paint's negative-id-transient ship (ClientState.cs)
// but ARRAYS are skipped (a collection in view-state would re-introduce identity; the server re-loads
// collections fresh from the store). I3: this ships pure VIEW-STATE whole — the harvest (`foreach db.designs`) depends on
// WHETHER the form is open, never on the draft's field VALUES, so a crafted value can't widen a floor-gated
// read. A draft whose VALUES drive a query stays the DEFERRED concern; this does not claim to support it.
function stateValueOf(v: ExecValue): object {
    if (v.type === "object") return v.id > 0 ? { type: "object", id: v.id } : { type: "object", props: transientPropsOf(v) };
    if (v.type === "int" || v.type === "bool" || v.type === "text") return scalarOf(v);
    return { type: "null" };
}

// Every prop of a transient object as tagged wire values, recursively (a nested transient prop is itself a
// { type:"object", props:{…} }; an in-store prop an id-ref; null a { type:"null" }) — the by-value shape
// slotState ships a component's `var state` (and any nested draft) in, reconstructed server-side as a
// throwaway ExecObject graph (WsHandler.SlotLocalFromWire per field). A collection PROP is skipped — the
// common BENIGN case is a fresh draft's empty set prop (`sys.new` mints `set of X` as []), which create
// forms never depend on; guarding it would break every create form. (Distinct from slotState's GUARD, which
// rejects a collection placed DIRECTLY in `var state`.) STILL-SILENT DEFERRED gap: a nested draft array the
// harvest genuinely DEPENDS on is indistinguishable HERE from the benign empty-set case, so it drops
// silently — tracked, see memory project_slotstate_recurse_nested_drafts.
function transientPropsOf(value: ExecObject): { [name: string]: object } {
    const props: { [name: string]: object } = {};
    for (const [name, v] of Object.entries(value.props))
        if (v.type !== "array") props[name] = stateValueOf(v);
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
        if (item.value.type === "object") rekeyCreatedObject(item.value, tempId, realId, collections);
    }
    if (item == null || item.value.type !== "object") registerRemap(tempId, realId);
    // The re-keyed member changes what dependents render (row data-keys), so cached
    // computations over this array must rebuild.
    invalidateMember(arrayId);
    renderUi();
}

// Re-key a just-persisted CREATED OBJECT from its transient negative id to the real extent id (the shared
// core of remapAddedId, reused by the atomic-commit Step B batch remap): set the object's id, index it under
// the real id, re-key its store-minted nested collections (so adds into them persist), and register the
// negId→realId maps + ack. Used for BOTH a live arrayAdd's member and a commit batch's creates (a set member
// or a ref target) — a ref-created object has no parent array, so the id/collections/maps re-key is exactly
// what it needs (its containing field already points at the same object instance, whose id we mutate here).
function rekeyCreatedObject(obj: ExecObject, tempId: number, realId: number,
                            collections?: { [prop: string]: { id: number; elementTypeName?: string } }): void {
    obj.id = realId;
    uiStatic.state.objects[realId] = obj;
    for (const [prop, coll] of Object.entries(collections ?? {})) {
        const propArr = obj.props[prop];
        if (propArr?.type === "array") {
            propArr.id = coll.id;
            propArr.kind = "set";
            propArr.elementTypeName = coll.elementTypeName;
            uiStatic.state.arrays[coll.id] = propArr;
        }
    }
    registerRemap(tempId, realId);
}

// Record a transient→real id mapping and ack it so the server can drop its own transient entry for tempId:
// from here on the client addresses the object by its real id and never sends the transient one again (every
// op referencing it sent BEFORE this point was already ordered ahead of this ack). Uncorrelated (no journal).
function registerRemap(tempId: number, realId: number): void {
    uiStatic.state.localToServerIds[tempId] = realId;
    uiStatic.state.serverToLocalIds[realId] = tempId;
    wsSend({ op: "ackRemap", clientId: uiStatic.clientId, tempId });
}

// Apply an atomic-commit (Step B) `commit` ack's batch remap: for each created object the server minted, re-key
// its transient negative id to the real extent id (via the SAME rekeyCreatedObject the live arrayAdd uses),
// using the per-create `collections` the server shipped so nested sets persist. The commit's journal entry has
// already retired (commitJournal on the ok). One re-render after the whole batch refreshes every re-keyed row.
function applyCommitRemap(idMap: { tempId: number; realId: number;
                                   collections?: { [prop: string]: { id: number; elementTypeName?: string } } }[]): void {
    for (const m of idMap) {
        const pending = pendingCommitCreates.get(m.tempId);
        const obj = pending?.obj ?? (uiStatic.state.objects[m.tempId] as ExecValue | undefined);
        if (obj != null && obj.type === "object") rekeyCreatedObject(obj, m.tempId, m.realId, m.collections);
        if (pending != null) {
            const join = pending.join;
            if (join.kind === "set") invalidateMember(join.set.id);
            else invalidateProp(join.parent.id, join.prop);
        }
        pendingCommitCreates.delete(m.tempId);
    }
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

// A scalar (or shallow structured) ExecValue as the wire { type, value } the server expects. Scalars ship as
// { type, value }; an ARRAY ships as { type:"array", items:[...] } and an OBJECT as { type:"object",
// props:{ name: … } }, recursing so a Code array-of-objects-of-scalars crosses NATIVELY (M13 Track-B B4 — the
// FIRST structured host-action arg, sys.mergeBranch's `resolutions: [{ id, take }]`). This is exactly the
// shape the server's existing ArgResolutionsOptional already parses ({type:"array",items} → each
// {type:"object",props} → each prop a tagged scalar), so it is a client-only wire addition, no server change.
// Anything not int/bool/text/array/object (a function, a tag, a null) still serializes to null — the arg is
// dropped, the conservative behavior every current host-action arg already relies on.
function scalarOf(value: ExecValue): object {
    switch (value.type) {
        case "int": case "bool": case "text": return { type: value.type, value: value.value };
        case "array": return { type: "array", items: value.items.map(i => scalarOf(i.value)) };
        case "object": {
            const props: { [name: string]: object } = {};
            for (const [name, v] of Object.entries(value.props)) props[name] = scalarOf(v);
            return { type: "object", props };
        }
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
