# Transparent Client Mutations — T2–T4 (revised, minimal)

> **For Hermes:** Execute with subagent-driven-development — a fresh `delegate_task` per task, two-stage
> review (spec compliance, then code quality), proceed only when BOTH pass. TDD: each task red→green.
> Every commit message: `feat(scope): ...` / `fix(scope): ...` / `refactor(scope): ...`, conventional prefixes.
>
> **Grilled-and-corrected assumptions (see the appendix at the bottom):**
> - T2 parsing is a ~15-line `ParseRelation` extension, not the interesting part.
> - The store is durability-safe on failure (no `Save` until apply completes) but NOT in-memory-roll-back;
>   immutability would remove the need for prevalidation — tracked separately (see "Future foundation").
> - T3 does NOT touch `endCommit`'s behavior. The designer handler runs in the *handler* bracket
>   (`runHandlerTransaction`/`flushHandlerTx`), not the ObjectForm commit bracket. T3 is about aggregating
>   the handler bracket's set intents into ONE `commit` frame at flush; `buildCommitWire` is extracted only
>   as the shared builder BOTH paths call.
> - Unlink-of-non-member is currently a silent no-op (store apply loop has no membership check). Doc wants
>   loud reject. Keep the 4-line guard (T2.6) — it is product contract, not rollback.
>
> **Baseline (full suite, this branch):** 962 total, 948 ok, 14 failed — all 14 are browser/Playwright e2e
> flakes (`ReadExtent("MetaProp")` 60s timeouts). Gate = "no NEW failures vs that baseline." Per-task: run the
> targeted scenarios listed; for any task touching `ws.ts`/`WsHandler`, also run `DesignerSourceTests` (parses
> `instances/1/app.deenv`).
>
> **Decisions locked (from review):**
> - D1: T2.4 `newNode(...)` helper to kill ~7 repeated node field-bags — also DRY for T4's new create site.
> - D2: T2 unlinks parsed straight into `mutations`, NOT forced into the `ParsedRelation` union.
> - D3: T2.6 add 4-line membership prevalidation (loud reject, doc-favored).
> - D4: T3 extract `buildCommitWire()` from `endCommit`; `flushHandlerTx` aggregates handler set-intents via it.
> - D5(a): do NOT block T2–T4 on the immutable-store foundation; capture it separately below.

---

## T2 — Server: accept the new set link/unlink + session remap   (server-only; nothing client-facing yet)

Wire: `set` (→`SetLinkMutation`), `setByProp` (→`SetLinkByPropMutation`), `setUnlink`
(→`SetUnlinkMutation`), `setUnlinkByProp` (→`SetUnlinkByPropMutation`). The four
`CommitMutation` records ALREADY EXIST (T1). T2 makes `WsHandler` accept them.

### T2.1 `feat(ws): record create-id map on commit, like HandleArrayAdd`
- File: `DeEnv/Http/WsHandler.cs` `HandleCommit` (~L855–956).
- Today `HandleCommit` calls `CommitBatch` (which mints real ids + returns `Creates` with the
  tempId→real map) but DOES NOT publish that map to the session. Precedent: `HandleArrayAdd`
  (~L1474) does `if (req.TempId is {} t) session?.MapTransientId(t, id);` plus returns
  `created.Collections` so the client re-keys transient arrays.
- Mirror exactly: after `CommitBatch` in `HandleCommit`, for each `CommitCreateResult` publish
  `session?.MapTransientId(tempId, realId)`, and include the per-create `Collections` map in the
  commit response (so the client can re-key transient `children`/`elseChildren` sets created inside
  the batch — same reason as `HandleArrayAdd`).
- WHY: a `commit` may mint several objects (e.g. wrapper + nested), and follow-up ops the client
  sends still address them by temp id. Without remap they dangle. This is the server half of
  "the session owns temp-id remap" from the doc (L27–30). Nothing client yet — just record mapping.
- RED: assert `MapTransientId` is called for each create + response carries the collections map.
  GREEN: add a `WsHandler` test that commits a two-create batch and asserts the session maps both
  temp→real and the response lists their collection ids.
- Targeted: `DesignerSourceTests`, `Host_SideActionsSys_*Feature` (server-side instance ops).

### T2.2 `feat(ws): ParseRelation recognizes setByProp / setUnlink / setUnlinkByProp`
- File: `DeEnv/Http/WsHandler.cs` `ParseRelation` (~L1012) + the apply switch (~L920).
- Add cases for the three new op names (JSON): produce the already-existing `SetLinkByPropMutation`,
  `SetUnlinkMutation`, `SetUnlinkByPropMutation`. Per D2, unlinks are NOT modeled as `ParsedRelation`
  (they have no typed child) — parse them straight into the `mutations` list `HandleCommit` builds
  (~L930), not into the `relations` list used by the ref/set-by-id arms.
- `setByProp` needs `(ownerRef, prop, memberRef)`; `setUnlinkByProp` same; `setUnlink` needs
  `(setId, memberRef)`.
- Precedent for the by-prop resolution lives in the store (T1's `SetLinkByPropMutation`/`SetUnlinkByPropMutation`
  apply arms already resolve the owner temp id + set prop) — so `ParseRelation` just needs to emit
  the record; the store does the rest (F4 from grill: T2.4 shrinks — no client-side type derivation).
- RED/GREEN: unit test in `CodeClientTests`/a new `WsHandler` test asserting `setByProp` JSON →
  `SetLinkByPropMutation`, `setUnlink` → `SetUnlinkMutation`, `setUnlinkByProp` → `SetUnlinkByPropMutation`.
- Targeted: `CodeClientTests`, `StoreConcurrencyTests`.

### T2.3 `feat(ws): access-floor on set link/unlink members (forgery + denial)`
- File: `DeEnv/Http/WsHandler.cs` `HandleCommit`. **Depends on T2.2** (operates in the SECOND pass T2.2 adds
  for `setByProp`/`setUnlink`/`setUnlinkByProp` — do NOT duplicate; T2.3 only ADDS floor checks there).
- Precedent (L923–945, the existing `relations` switch): a positive member being linked is floor-checked as a
  `create`:
  `if (childRef >= 0 && _store.ReadById(childRef) is { } linked) RequireWrite(floor, "create", linked.TypeName, Code.AccessFloor.ScalarObject(linked.TypeName, childRef, linked.Fields, _desc));`
  and a `ref` link also floor-checks the OWNER as an `edit`:
  `RequireWrite(floor, "edit", owner.TypeName, Code.AccessFloor.ScalarObject(owner.TypeName, parentId, owner.Fields, _desc));`
- Apply the SAME two floors to the new ops in T2.2's pass:
  - `setByProp(ownerId, prop, childId)`: floor-check the OWNER as `edit` (you mutate its set prop) + the MEMBER
    (`childId >= 0`) as `create`. A transient (`< 0`) member already passed the create gate in T2.2's
    `createTypeByTempId`/createBatch path — skip.
  - `setUnlink(setId, childId)` / `setUnlinkByProp(ownerId, prop, childId)`: floor-check the MEMBER (`childId >= 0`)
    as `create` (forgery + denial: you must be able to read the object you detach). `setUnlinkByProp` also
    floor-checks the OWNER as `edit` (mirrors the link side). `setUnlink` (by raw setId) has no owner handle —
    the member floor is the guard (matching how a `set` link is gated purely by its member today).
- This is a few `if (id >= 0 && _store.ReadById(id) is {} x) RequireWrite(...)` lines mirroring the existing two.
  No new machinery.
- RED/GREEN: a `WsHandler` test asserting a link/unlink of an UNREADABLE member is rejected (floor denial), and
  that a readable one is allowed. Mirror existing floor-denial tests if any; else add to the T2.2 fixture.
- Targeted: `Host_SideActionsSys_*Feature`, `CodeClientTests`, `CommitSetByPropTests` (T2.2's).

### T2.4 `refactor(app): add newNode(...) helper; wrap/unwrap use generic set ops (no new wire)`
- File: `DeEnv/instances/1/app.deenv` (instance 1 schema doc).
- Add `fn newNode(kind, tag, order)` returning the repeated `{ kind, tag, expr:"", item:"", collection:"",
  condition:"", order, attrs:[], children:[], elseChildren:[] }` bag (D1). Replace the ~7 inline bags
  (`wrapNode`, `addText`/`addFor`/`addIf`/`addAttr`, `insertComponent`, `rootFallbackTarget`?) with calls.
- SEPARATE from T4: this task does NOT change the wire semantics — wrap/unwrap still use `arrayAdd`/
  `arrayRemove` today. The generic-mutations rewrite is T4. Keep T2.4 purely the node-constructor DRY.
- Verified by the same harness T4 will use: `DesignerSourceTests` (parses app.deenv) must stay 31/31.
- RED/GREEN: no behavior change — assert `DesignerSourceTests` 31/31 still green after the helper
  extraction. (If a scenario exercises the exact created-object shape, add one asserting equal shape.)
- Targeted: `DesignerSourceTests`.

### T2.5 `feat(ws): link/unlink of a FRESH owner's set works in HandleCommit`
- **Depends on the StoreDoc→Db rename committing first** (this task touches `JsonFileInstanceStore.cs`; the
  rename subagent is editing that file — do NOT start until the rename commit lands).
- Already largely covered by T1's store apply arms (they resolve a negative owner via `creates`), but
  `HandleCommit` must EMIT the by-prop mutation with the negative owner ref (not an id it resolves
  server-side). T2.2 already passes `ownerRef` verbatim for `setByProp`/`setUnlinkByProp` (precedent:
  `RefLinkMutation` forwards the raw parent ref). So this task is mostly a TEST proving the end-to-end path:
  commit that creates a wrapper AND links the existing node into `wrapper.children` (negative owner) →
  asserts the node is reachable ONLY via the wrapper post-commit (and unlinked from its old parent set).
  This is the real T1 end-to-end proof server-side.
- RED/GREEN: `WsHandler` test (or extend `StoreConcurrencyTests` T1 "wrap" test): create+`setByProp`(negative
  owner,"children",existingId); assert member reachable via new set, unlinked from old.
- Targeted: `StoreConcurrencyTests` (extend the T1 "wrap" test), `DesignerSourceTests`.

### T2.6 `feat(store): reject commit unlink of a non-member (loud, doc-favored)`
- **Depends on the StoreDoc→Db rename committing first** (touches `JsonFileInstanceStore.cs` `CommitBatch`).
- File: `DeEnv/Storage/JsonFileInstanceStore.cs` `CommitBatch` prevalidation loop (the `foreach (var mutation in
  mutations) switch` — `SetUnlinkMutation` arm ~L584, `SetUnlinkByPropMutation` arm ~L588).
- The apply loop's `UnlinkMember` does `set.Members.Remove(id)` — a SILENT NO-OP if absent. Doc L83–84 wants
  commits to prevalidate membership and reject. Add a membership check in the prevalidation arms (so a bad
  unlink throws with `_db` UNTOUCHED — matches the loop's all-or-none contract, see the comment at ~L573).
- Member id resolution: prevalidation uses `RequireResolvable(memberRef)` (proves it resolves) but does NOT
  compute the real id. To check membership you need the resolved id, mirroring the apply loop's `ResolveRefId`:
  - For a positive `memberRef`: real id = `memberRef`.
  - For a negative `memberRef`: it must be a temp create — resolve via the `creates` list
    (`createBatch`/`creates.First(c => c.TempId == memberRef).RealId`). NOTE: in prevalidation the creates
    loop has already run (prevalidation is AFTER the creates loop), so `RealId` is assigned.
  - Then for `SetUnlinkMutation(setId, memberRef)`: `var set = FindSetNode(setId)` (already resolved in the
    arm); `if (!set.Members.Contains(memberId)) throw new InvalidOperationException($"member {memberId} not in set {setId}");`
  - For `SetUnlinkByPropMutation(ownerRef, prop, memberRef)`: resolve owner (tempId→real like the apply arm's
    `ResolveRefId`), get its `prop` StoredSet (`owner.Fields.GetValueOrDefault(prop) is StoredSet set`), then
    same `if (!set.Members.Contains(memberId)) throw ...`.
- ~6 lines total. Per D3.
- NOTE: this is a PRODUCT contract (loud fail), NOT a rollback/durability fix — the file is already safe (no
  Save on throw). See appendix.
- RED/GREEN: `StoreConcurrencyTests` — a commit `setUnlink` of a non-member is REJECTED; a valid unlink still
  succeeds.
- Targeted: `StoreConcurrencyTests`, `ObjectModelIdentityReferencesSetsGCFeature`.



## T3 — Client `ws.ts`: aggregate a handler's set ops into ONE commit   (the real gap)

Designer handlers (`wrapNode`/`unwrapNode`) call raw `arrayAdd`/`arrayRemove`, which run inside
`runHandlerTransaction` (`ws.ts` ~L445) and flush N separate frames via `flushHandlerTx` (~L519:
`for (const text of buffered) wsSendText(text)`). T3 makes ONE handler produce ONE `commit`.

### T3.1 `feat(ws): buffer set link/unlink intent inside runHandlerTransaction`
- File: `DeEnv/Instance/ws.ts`. Add `commitRelations: CommitRelation[]` (parallel to `commitEdits`, L65)
  that opens whenever a commit/transaction bracket is open. `arrayAdd` (L840) and `arrayRemove` (L847)
  — when a bracket is open — push a structured intent (`{ op:'set'|'setByProp'|'setUnlink'|'setUnlinkByProp', ... }`)
  into `commitRelations` instead of sending a frame (exactly like `propChange` buffers into `commitEdits`, L770).
- When NO bracket is open, `arrayAdd`/`arrayRemove` keep today's per-frame send (back-compat for any
  non-transaction caller — there should be none, but preserve behavior).
- RED/GREEN: `CodeClientTests` — a handler that does 2 `arrayAdd`s does NOT emit 2 `arrayAdd` frames;
  it buffers; `endCommit`/flush emits one `commit` carrying both.

### T3.2 `refactor(ws): extract buildCommitWire() from endCommit; shared by both brackets`
- File: `DeEnv/Instance/ws.ts` `endCommit` (~L947). Pull the wire-building (collect `wireCreates`/
  `wireRelations`/`wireEdits`, build the one `commit` message, register the single journal entry with
  reverse-order undo, one msgId, `applyCommitRemap` registration) into `buildCommitWire(args)`. `endCommit`
  calls it unchanged. This is the ONLY change to `endCommit` — its behavior is preserved (D4).
- RED/GREEN: `CodeClientTests` — ObjectForm Save still emits one `commit` with the same shape
  (regression guard that the extraction didn't alter output).

### T3.3 `feat(ws): flushHandlerTx emits one commit via buildCommitWire when relations/edits buffered`
- File: `DeEnv/Instance/ws.ts` `flushHandlerTx` (~L519). On success path, if `commitRelations` (or
  `commitEdits`) holds intents, BUILD the single `commit` frame via `buildCommitWire` and send THAT
  (not the N buffered `arrayAdd`/`objectPropChange` frames). On the action-miss abort path, drop the
  buffered intents (already done — `txSendBuffer=null`); the journal undo already reverts local state.
- This is the doc's L44–52: "buffer semantic mutations, not serialized sends."
- RED/GREEN: `CodeClientTests` — a handler doing `arrayAdd`+`arrayRemove` flushes exactly ONE `commit`
  frame (assert frame count == 1, carrying both ops).

### T3.4 `feat(ws): reject mixed unsupported mutations inside a bracket`
- File: `DeEnv/Instance/ws.ts`. If a bracket buffers set/field intents AND a hook that has no commit
  equivalent (`hostAction`, dict-entry, ref-write) fires, throw before sending (doc L51). The hooks
  already gate on "buffer open?" — add the `if (commitEdits||commitRelations) throw` guard to those.
- RED/GREEN: `CodeClientTests` — a handler mixing `arrayAdd` + `hostAction` throws "unsupported in commit".

### T3.5 `feat(ws): applyCommitRemap handles buffered-relation remaps`
- File: `DeEnv/Instance/ws.ts` `applyCommitRemap` (~L? — the ack path that re-keys transient ids/collections
  from a `commit` reply). Extend it to also remap transient `children`/`elseChildren` set ids carried in
  the commit response's `collections` map (T2.1 published these). No behavior change for the ObjectForm path.
- RED/GREEN: `CodeClientTests` — after a commit that created a wrapper with a nested `children` set, the
  client's local reference to that transient `children` set is re-keyed to the real id (so subsequent
  `arrayAdd` into it persists).

---

## T4 — Retire S5c `NestedSetLinks` specialization   (REALIZED: T4.1 via T3; T4.2/T4.3 DEFERRED)

### T4.1 `refactor(app): wrapNode/unwrapNode → one commit`  — DONE by T3
- `wrapNode`/`unwrapNode` use `site.parentSet.add(wrapper)` / `.remove(site.node)` (→ `arrayAdd`/
  `arrayRemove`), which T3 buffers into ONE `commit`. The `wrapper.children = [site.node]` local set
  is serialized by `objectOf` as `children:{items:[{refId: site.node.id}]}` on the create value, and the
  server's `NestedSetLinks` (inside the SAME `CommitBatch` as the create) emits a
  `SetLinkByPropMutation(wrapper, "children", site.node.id)` — so the node lands inside the wrapper.
  Whole wrap/unwrap = ONE atomic commit. No app.deenv change needed.

### T4.2 `feat(ws): delete NestedSetLinks`  — DEFERRED (load-bearing, not dead)
- `NestedSetLinks` (WsHandler.cs ~L1729, called only from HandleArrayAdd's NEW-member branch at ~L1573)
  is STILL required: it is the only thing that links the EXISTING node into the FRESHLY-CREATED wrapper's
  `children` set. The client's buffered relations (T3) cover only `set`/`setUnlink` (existing-member /
  unlink), NOT `setByProp` (fresh-owner link). So today the fresh-owner link is carried implicitly via the
  create value's nested `refId` items → `NestedSetLinks`. Deleting it would regress `wrapNode` (wrapper
  created empty, node NOT inside it). Same class of defer as D5: correct only after the client can emit a
  `setByProp` relation from app.deenv (a larger client change, out of slice scope + unverifiable headless).
- KEPT. No deletion.

### T4.3 `test: delete ArrayAddNestedRefTests`  — DEFERRED (tracks T4.2)
- `DeEnv.Tests/Code/ArrayAddNestedRefTests.cs` still exercises the live `arrayAdd`-with-nested-ref path,
  which remains the real (non-regressed) behavior. Delete only when T4.2 is done.

### T4.4 `docs/decision: retire the wrap/unwrap UI macro?` — NEEDS SIGN-OFF (unchanged)
- The doc L88–90 asks whether to keep the wrap/unwrap UI buttons as a layer over generic mutations.
  Open question for the user; no code change.

---

## Future foundation — DEFERRED (D5 reconsidered; dropped from active plan)

**Immutable / event-sourced store — NOT WORTH IT for this goal (2026-07-12 decision).**
Clarification: `_doc` here is the C# server-side in-memory store in `DeEnv/Storage/JsonFileInstanceStore.cs`
(`Doc _doc`), NOT the JS `ws.ts` client (which already ships atomic `commit` frames and has no mutable DB).
The durable append-only JSON changeset log is ALREADY immutable, so "immutable doc" would mean not mutating
the in-memory `Doc` in `CommitBatch` — build a new version, swap + `Save`. The real blocker is perf: full
deep-copy per commit is a cliff on big instances, so true COW needs structural sharing (a persistent map C#
doesn't ship) — a foundation refactor larger than T1–T4 and orthogonal to this goal. The concrete safety it
buys is narrow: a mid-apply throw leaves `_doc` half-mutated in memory, BUT the file is already safe (no
`Save` on throw) and the hole is rare + bounded to process lifetime (next load rebuilds from the immutable
log). The only present "safety net" is the 1 small prevalidation loop — not "complicated rollback logic."
Conclusion: drop it. (Semantic loud-reject on non-member unlink — T2.6 — is a product contract that survives
regardless of immutability; it's 4 lines, not a rollback.)

---

## Appendix — grill corrections that shaped this plan
- F1: T3 originally targeted `beginCommit`/`endCommit` (wrong bracket). Designer handlers use the handler
  bracket (`runHandlerTransaction`). Corrected: T3.1–T3.3 target `flushHandlerTx`.
- F2: unlink prevalidation was a "confirm" — actually a required 4-line add (T2.6).
- F3: don't force unlinks into `ParsedRelation` (no typed child) → parse into `mutations` (D2 / T2.2).
- F4: owner-type derivation for `setByProp` already happens in the store (T1); T2.4 shrinks to access floor.
- User observation: `nodeContext` returning `{node,coll}` collided with `ExecCtx`; renamed to `locateNode`/
  `{node,parentSet}` (committed `bfaafdd`). This plan's T2.4 reuses `parentSet`.
- User insight: an immutable store would remove prevalidation/rollback need — captured as Future foundation.
