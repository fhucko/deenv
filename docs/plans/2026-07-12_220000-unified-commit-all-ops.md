# Unified `commit` — every model op in one frame (remove, not delete)

> **For Hermes:** Plan-only. Execute via subagent-driven-development (one fresh `delegate_task` per task,
> two-stage review: spec compliance then code quality). TDD red→green per task. Conventional commits.
>
> **TEST INVOCATION (cost 2 wasted runs 2026-07-12):** `dotnet test DeEnv.Tests -- --treenode-filter
> "/*/*/<RealClassName>/*"` from **PowerShell** (git-bash mangles `/*/…` → 0 tests). Never
> `dotnet test DeEnv.Tests.csproj --treenode-filter` (no `--` → TUnit prints help, exit 5). `dotnet test
> --no-build` runs a STALE assembly — always `--no-incremental` with a fresh build.
>
> **DISCIPLINE (user, 2026-07-12):**
> 1. **Every wire/commit operation MIRRORS a model data operation** — no parallel vocabulary. The client
>    never invents an op the store lacks.
> 2. **`remove` is the verb; `delete` is NOT an operation.** Removing/unlinking detaches an object from its
>    references; actual deletion is GC's call and is NOT guaranteed (an orphaned object may still be retained,
>    e.g. AppLog's `Remove` record keeps the full prior object for history-resurrection, AppLog.cs L87-90).
>    There is NO bulk "detach-from-every-edge" op — mirroring C#/JS, you drop individual references
>    (`RefLink`→null, `SetUnlink`/`SetUnlinkByProp`, `DictRemove`) and let GC collect the orphan. The union
>    has `DictRemoveMutation` (targeted) but NO `RemoveMutation`. `remove` semantics = targeted unlinks + GC.
> 3. **To save ANY data on the server, `ctx commit` MUST be called.** No operation may persist outside a
>    `commit` frame. The 7 live mutating wire ops are deleted (client never sends them; server rejects them),
>    not kept as a fallback. The ONLY server persistence path is `HandleCommit`.
> 4. **Cleanup + unify naming.** Names must be consistent across client/server so a reader can tell which
>    layer a type lives in. Concrete collision to fix: the client `CommitCreate { draft, join }` interface
>    (ws.ts L70) and the server `CommitCreate(int TempId, …)` record (IInstanceStore.cs L192) share a name
>    but are different shapes/layers — rename the client one. `beginCommit`/`endCommit` are RUNTIME-INTERNAL
>    bracket primitives (not surfaced to user Code).
> 5. **User Code sees ONLY plain `commit`** — never `beginCommit`/`endCommit` (or any begin/end verb). The
>    form Save calls `ctx.commit` (one verb); a handler just mutates and the RUNTIME auto-wraps it in a commit
>    on success (`runHandlerTransaction`); a top-level edit is auto-wrapped by the runtime (micro-commit). User
>    Code's persistence vocabulary is exactly one word: `commit`. Bracketing is an implementation detail hidden
>    inside the runtime. (Verified: app.deenv already calls no `beginCommit`/`endCommit` — only `ctx.commit`
>    on the form path; handlers mutate the model and the runtime brackets them.)

## Goal

The single `commit` frame carries EVERY model mutation: scalar edit, ref write, set link/unlink,
create, dict write/remove, and **remove** (detach). `commit` is the SOLE server persistence op. The 7 live
mutating wire ops — `write`, `objectPropChange`, `setReferenceField`, `arrayAdd`, `arrayRemove`,
`addEntry`, `removeEntry` — are **deleted**: the client buffers every mutation into `commit`; the server
rejects any of them with an error. Only NON-mutating ops survive (`hello`, `refetch`, `hostAction`,
`ackRemap`, `parseExprs`, `login`, `logout`). (Design persistence — `setDesign`/`commitDesign`/`publish` —
is M13's separate authority; noted as the one remaining non-`commit` persistence path, deferred, NOT in
this slice.)

**Execution order (user, 2026-07-13):** T1–T5 + T6-St1 landed `commit` as the path for edits/refs/set-links/
set-unlinks/creates and DELETED 3 of the 7 live handlers (`objectPropChange`/`setReferenceField`/`arrayRemove`).
The remaining 4 handlers (`write`/`addEntry`/`removeEntry`/`arrayAdd`-mint) require `commit` ops that do not
yet exist: a **dict-write commit relation** and **deferred nested-create staging**. So T6 is split:
- **T6a (this slice's remaining work):** build those two `commit` ops, proven with headless tests.
- **T6b (later slice):** switch the client to use them + delete the 4 remaining live handlers.
This keeps each landing reviewable and avoids a half-cutover.

## Model vocabulary = the contract (every commit op maps to one IInstanceStore op)

| Model op (`IInstanceStore`) | commit form | status |
|---|---|---|
| `WriteField` | `edit {objectId, prop, value}` | ✅ already |
| `WriteReference` (clear = target null) | `RefLinkMutation` | ✅ already |
| `AddToSet` | `SetLinkMutation` | ✅ already |
| `RemoveFromSet` | `SetUnlinkMutation` | ✅ already |
| `CreateObject` | `CommitCreate` | ✅ already |
| `SetLinkByProp` (model helper) | `SetLinkByPropMutation` | ✅ wire (T1/T3) |
| `SetUnlinkByProp` (model helper) | `SetUnlinkByPropMutation` | ✅ wire (T1/T3) |
| `CreateEntry` (dict) | `DictWriteMutation` | server-only today → **T6a.1 promote to wire** |
| `RemoveDictionaryEntry` (dict) | `DictRemoveMutation` | ✅ wire (T2/T3) |
| **`Remove`/detach (unlink everywhere)** | `RemoveMutation` | ✅ wire (T2/T3); then `CollectGarbage` |

## What exists vs must be added (grounded in code reads)

- `CommitMutation` union (IInstanceStore.cs L192-238) ALREADY has: `CommitCreate`, `SetLinkMutation`,
  `RefLinkMutation`, `FieldWriteMutation`, `DictWriteMutation` (server-only), `SetLinkByPropMutation`
  (server-only), `SetUnlinkMutation`, `SetUnlinkByPropMutation` (server-only). **Missing: `DictRemoveMutation`,
  `RemoveMutation`.**
- `CommitBatch` apply loop (JsonFileInstanceStore.cs L626+) already handles every existing kind. Adding two
  kinds = two `case`s in the prevalidation pass (L552) + two in the apply pass (L626) — same `RequireResolvable`
  + schema-gate pattern.
- `CollectGarbage()` (L2355) is a reusable mark-sweep that emits the `Remove` log record for orphans. Every
  existing unlink op calls it (L1001/1033/1122/1150/1920). `RemoveMutation`'s apply arm detaches the object
  from all refs/sets/dicts, then calls `CollectGarbage()` — **reuse, don't duplicate.** No guaranteed deletion.
- `HandleCommit`'s ParseRelation second pass (WsHandler.cs L878+) already parses `setByProp`/`setUnlink`/
  `setUnlinkByProp`. Adding `dict`/`remove` kinds = 2 more `case`s there + server-side-only → wire.
- `DictWriteMutation` is **already applied by the store** (server-constructed today) — promoting it to wire
  needs ONLY `HandleCommit` to emit it; the store side is done.

## Plan

### Task 1: `feat(store): add RemoveMutation + DictRemoveMutation to the union`

**Files:** `DeEnv/Storage/IInstanceStore.cs`, `DeEnv/Storage/JsonFileInstanceStore.cs`
- Add records (mirror existing shapes):
  - `public sealed record RemoveMutation(int ObjectRef) : CommitMutation;` — detach `ObjectRef` from every
    reference/set/dict that points at it.
  - `public sealed record DictRemoveMutation(int OwnerRef, string Prop, NodeValue Key) : CommitMutation;`
    — mirror `DictWriteMutation` (L213) but remove. Keep `Value` out (it's a removal).
- In `CommitBatch` prevalidation pass (L552): `case RemoveMutation(var o): RequireResolvable(o);` and
  `case DictRemoveMutation(var owner, _, _): RequireResolvable(owner);` + dict-prop gate (owner's `Prop` is a
  dictionary — same set-prop check at L580, flipped to `Cardinality.Dictionary`).
- GREEN checkpoint: build clean (no apply yet — store compiles with new records).
- RED→GREEN (Task 5 applies them): a unit test calling `CommitBatch` with a `RemoveMutation` detaches + sweeps.

### Task 2: `feat(store): RemoveMutation applies (detach everywhere) + GC; DictRemoveMutation applies`

**Files:** `DeEnv/Storage/JsonFileInstanceStore.cs`
- Apply pass (L626): 
  - `case RemoveMutation(var o):` resolve real id; walk `_db` and null every `StoredRef`/`StoredSet` member/
    `StoredDict` entry pointing at it (reuse the existing unlink primitives — `UnlinkMember` L1098 for sets;
    for refs, set the owning field to null and `BumpVersion`; for dicts, `Entries.Remove(key)`); then
    `CollectGarbage()` (reuses L2355 — emits `Remove` log record for anything now orphaned). **No guarantee the
    object is deleted** — if still reachable via another ref, GC leaves it. That is correct per the discipline.
  - `case DictRemoveMutation(var owner, var prop, var key):` resolve owner; get its `StoredDict` `prop`;
    `Entries.Remove(KeyString(key))`; `BumpVersion(owner)`. (No GC needed — dict removal doesn't orphan the keyed
    value unless nothing else holds it; let the batch-end GC, if any unlink present, handle it — consistent with
    `RemoveDictionaryEntry` at L1920 which calls `CollectGarbage()`; mirror that.)
- RED: `StoreConcurrencyTests` — a `commit` with `RemoveMutation(id)` detaches `id` from a set + a ref; a second
  object still referencing it survives; an orphan is swept (its `Remove` log record exists).
- GREEN: passes.

### Task 3: `feat(ws): commit accepts dict + remove relations (promote server-only kinds)`

**Files:** `DeEnv/Http/WsHandler.cs`
- ParseRelation second pass (L878): add `case "dictAdd"` → parse `{ownerRef/parentId, prop, key, value}`; resolve
  owner (negative allowed); emit `DictWriteMutation(ownerRef, prop, key, value)` (value via `DeserializeValue`/
  `ParseKey` — same as `HandleAddEntry` L615-617). Add `case "dictRemove"` → `{ownerRef, prop, key}` →
  `DictRemoveMutation`. Add `case "remove"` → `{objectId}` → `RemoveMutation(Resolve(session,objectId))`.
- Access floor: `dictAdd`/`dictRemove` mirror `HandleAddEntry`'s `RequireDictWrite` (L606) — the owner edit floor.
  `remove` floors the object as `delete` (acl verb) — mirror `HandleRemoveEntry`'s `RequireWrite(...,"delete",...)`
  (L639). **This is the one place the `delete` ACL verb maps to a real op** (remove), consistent with
  InstanceDescription.cs L83's verb list.
- Drop the `continue` skip at L838-840 so `dictAdd`/`dictRemove`/`remove` are NOT skipped (they now parse here).
- RED: `CodeClientTests`/`WsHandler` test — a `commit` carrying `dictAdd` writes a dict entry; `remove` detaches.
- GREEN: passes. `DesignerSourceTests` 31/31.

### Task 4: `feat(ws): client CommitRelation union adds dict / dictRemove / remove`

**Files:** `DeEnv/Instance/ws.ts`
- Extend `CommitRelation.wire` (L82-88) with `{kind:"dict", ownerRef, prop, key, value}`, `{kind:"dictRemove",
  ownerRef, prop, key}`, `{kind:"remove", objectId}`. Wire-builder (L1061) already pushes `r.wire` verbatim;
  server accepts all (Tasks 1-3). No builder change.
- RED: `CodeClientTests` — buffered `dict`/`remove` relations carry the right wire shape.

### Task 5: `refactor(ws): client never sends a live mutating op — all buffer into commit`

**Files:** `DeEnv/Instance/ws.ts`
- This task ENFORCES discipline #3 (commit is mandatory). The 7 mutating wire ops are REMOVED from the client
  send surface; their hooks buffer into a commit bracket. There is NO non-commit live fallback.
- **THE GOTCHA (commit scopes differ — state in the PR):**
  - *Form ctx* (`ctx.commit()`): explicit bracket — `beginCommit` (L1011) opens `commitEdits`/`commitCreates`/
    `commitRelations`; `endCommit` (L1032) flushes ONE `commit`. UNCHANGED.
  - *Handler ctx* (`runHandlerTransaction` L463): today it ONLY opens `commitRelations` (L473) AND reinvents
    atomic-rollback + the action-miss round-trip (L483-518). **Do NOT widen it to open all three buffers —
    that duplicates `beginCommit`.** Instead (per user "why not just keep ctx"): collapse the bracket-opening
    into the ONE primitive — the handler wrapper calls `beginCommit`/`endCommit` (same as form + top-level),
    and KEEPS ONLY its handler-specific logic: atomic client-side rollback (`abortHandlerTx` L528) + the
    action-miss fetch-and-rerun (`Value not available` before any work → PendingAction + refetch + re-invoke).
    A plain `ctx.commit()` has neither, so the handler wrapper earns its existence on that behavior alone —
    not on buffer-opening. `runHandlerTransaction` calls the RUNTIME-INTERNAL `beginCommit`/`endCommit` (same
    primitives the form Save uses via `ctx.commit`) plus its handler-specific rollback/miss logic; user Code
    never names a bracket (discipline #5). No rename to a begin/end handler verb.
  - *TOP-LEVEL ctx* (page root render — palette clicks, per-keystroke autosave): has **NO bracket** today; it
    fires an immediate live op. After this task it must **commit immediately per mutation**: wrap each top-level
    mutation in a micro-bracket — `beginCommit(topLevelCtx)` → stage exactly one edit/create/relation →
    `endCommit()` — so it sends ONE `commit` to the server (one-mutation-at-a-time, because there is no larger
    scope to batch into). It is NOT "no commit" and NOT "a big staged commit" — it is a one-shot commit per
    mutation. This is the key difference from form/handler scopes and the reason R5's "buffers non-null" guard
    must use the micro-bracket, not assume an open bracket.
- Concrete edits (delete the live `wsSend`, stage into the open-or-micro bracket instead):
  - `objectPropChange` (L839): DELETE live send. If a bracket is open, buffer an `edit`; ELSE open a
    micro-bracket (beginCommit→push edit→endCommit) and send ONE commit.
  - `write` (L853): DELETE live send; path write → resolve objectId+prop → same as objectPropChange.
  - `setReferenceField` (L865): DELETE live send; buffer/commit a `RefLinkMutation` relation (clear = target null).
  - `arrayAdd` mint (L913-917): DELETE live `arrayAdd`; buffer/commit `commitCreate` (+ `setByProp` join).
  - `arrayRemove` (L941): DELETE live send; buffer/commit `setUnlink`/`setUnlinkByProp`.
  - `entryAdd` (L959): DELETE live `addEntry`; SET→`commitCreate`+`setByProp`; DICT→`dictAdd` relation.
  - `entryRemove` (L970): DELETE live `removeEntry`; SET→`setUnlink`; DICT→`dictRemove`.
- GREEN: `DesignerSourceTests` 31/31; `CodeClientTests` asserts (a) NONE of the 7 live ops is ever sent, and
  (b) a top-level edit produces exactly ONE `commit` with one `edit` (micro-bracket), not a live `objectPropChange`.

### Task 6 (rescoped — two phases): build the `commit` ops FIRST, delete live handlers in a LATER slice

> **SEQUENCING DECISION (user, 2026-07-13):** build the `commit`-side machinery that makes every mutation
> expressible as a `commit` FIRST (so the client has a real target for each of the 4 remaining live ops), then
> switch the client over + delete the 4 live handlers in a SEPARATE slice. This mirrors the proven T5→T6-St1
> order (build commit path → delete dead handler) and avoids a half-cutover where the client sends a `commit`
> the server can't apply. **Do NOT delete `HandleWrite`/`HandleAddEntry`/`HandleRemoveEntry`/`HandleArrayAdd`
> until T6a is done and green.** Three of the seven (`HandleObjectPropChange`, `HandleSetReferenceField`,
> `HandleArrayRemove`) are ALREADY deleted (commit `28b905c`); their client sends already route through
> `commit` (`8c40509` + `fe25f3d`).

#### Phase 1 — T6a: build the two missing `commit` ops (client + server)

These are the only two things still missing for `commit` to cover every model op. Each is its own slice;
build + prove with headless tests BEFORE any client cutover.

**T6a.1 — Server `dictAdd`/`dictRemove` as `commit` relations (kills `HandleWrite`/`HandleAddEntry`/`HandleRemoveEntry`).**
- Server `CommitRelation` union gains `{kind:"dictAdd", ownerRef, prop, key, value}` (scalar value) →
  `DictWriteMutation`; plus the existing `dictRemove` already parses (T2/T3). Apply arms already exist in
  `JsonFileInstanceStore`.
- **Client must be able to NAME the dict `prop`.** Today `ExecArray` (set/dict) carries no `prop`; the dict
  owner is addressed by path, not `(ownerRef, prop).` Add `prop` to the dict `ExecArray` (mirrors how the set
  `ExecArray` knows its containing set's id) so `entryAdd`/`entryRemove`/`pathWrite` can build
  `{kind:"dictAdd", ownerRef, prop, key, ...}` wire. (This is the real blocker the earlier analysis flagged — the
  client literally cannot construct the wire without the dict `prop` name.)
- `entryAdd` / `entryRemove` / `pathWrite` (dict field) hooks: when a bracket is open (or via `stageTopLevel`),
  buffer a `dictAdd`/`dictRemove` relation instead of sending live `addEntry`/`removeEntry`.
- Tests: extend `CommitSetByPropTests` / a new `CommitDictTests` — a `commit` carrying `dictAdd` writes an entry;
  `dictRemove` drops it; floor = owner `edit` (mirrors `RequireDictWrite`).
- NOTE: `DictWriteMutation` is currently server-only (IInstanceStore.cs L207-208, "not from wire"). This task
  PROMOTES it to a wire-accepted `commit` relation — that is the substance of the slice.

**T6a.2 — Deferred nested-create staging into the enclosing `commit` (kills `HandleArrayAdd` mint).** — DONE.
- The mint today goes `create-save` → `join(d)` → `set.add(d)` → `arrayAdd` hook. Two cases:
  - *Top-level / save-less set* (`/orders`): must persist IMMEDIATELY as its own `commit` (stageTopLevel path).
  - *Nested set under an object form* (Order's `lines`): the draft is ALREADY staged into the enclosing form's
    `ctx.creates` by `addToCollection` (codeExec.ts L2283-2286: a transient draft added to a set under a staging
    ctx is pushed to `staging.creates`, never reaching `wsHooks.arrayAdd`). So `wsHooks.arrayAdd`'s mint branch is
    only ever reached for a REAL set (`arr.id > 0`) — the nested case never arrives here. The form's `ctx.commit()`
    mints staged creates + applies their set joins (no graph-walk needed; the staging machinery handles it).
- Fix (`ws.ts` `arrayAdd` mint branch): route the real-set mint through `commitCreate(item.value, {kind:"set",
  set: arr})`, mirroring the existing-member LINK branch — buffer into an open bracket, else `stageTopLevel` one
  commit. The nested case was already deferred by `addToCollection`, so no special-casing was needed.
- Server fix (`WsHandler.HandleCommit` L986): parse the create `value` with `ExecObjectValue(valueEl, typeDef,
  allowSets: true)` — WITHOUT `allowSets`, a draft carrying a nested collection field (e.g. Order's `lines` set)
  threw "Field 'lines' on 'Order' is not a scalar field" and the whole commit was rejected. `allowSets:true` skips
  nested collection fields (they are linked by the create's `set`/`setByProp` relation, never shipped inline),
  exactly as `HandleAddSetMember` does. This was the actual bug that broke the save-less mint; the earlier
  "deferred graph-walk" hypothesis was wrong.
- Tests: `A_create_under_a_save_less_container_persists_immediately` (top-level) + `A_create_under_an_object_form_defers_to_that_forms_save` (nested) BOTH green; `ArrayAddNestedRefTests` green (mint via commit). CodeClientTests 19/19.

#### Phase 2 — T6b (LATER slice, after T6a green): switch client + delete the 4 live handlers

Split into TWO commits (user decision 2026-07-13: narrow path first, dict fork deferred):

**T6b-2 (DONE — array side):** the live `arrayAdd`/`arrayRemove` ops are retired client-side (both link + mint
buffer `set`/`setUnlink`/`setByProp` relations and micro-bracket a `commit`, mirroring `stageTopLevel`'s commit
path — no live `wsSend` for sets). Server deleted `HandleArrayAdd` + `NestedSetLinks` + `ArrayAddResponse` + the
`arrayAdd` case arm. `WsWireShapeTests` now asserts a stray `arrayAdd` frame is rejected as `Unknown op`;
`ArrayAddNestedRefTests` rewritten to mint-via-`commit` (creates + `set` + `setByProp` relations).

**T6b-4 (DEFERRED — R7 fork, per user):** the dict ops `entryAdd`/`entryRemove`/`pathWrite` still send live
`addEntry`/`removeEntry`/`write`, and `HandleWrite`/`HandleAddEntry`/`HandleRemoveEntry` are RETAINED. The full
R7 conversion (dict `ExecArray` carries `prop`+`ownerRef`; server `dictAdd`/`dictRemove` accept object-entry
values; `pathWrite` → `dictAdd` whole-entry) is a separate, later commit. The narrow path keeps the dict
handlers alive as the safety net for object-entry dicts.

Split into parts:
- **T6b-4a (DONE):** server `dictAdd`/`dictRemove` now accept OBJECT dictionary entries (`dict of Config`), not
  just scalars. The `dictAdd` parse branches scalar (`LeafForType`) vs object (`ExecObjectValue(allowSets:true)`,
  the same `{props:{...}}` shape a commit create ships); the `DictWriteMutation` apply arm branches `StoredLeaf`
  vs `MintObject`→`StoredRef` (mirroring `WriteDictionaryEntryInto`). `pathWrite`-equivalent object-entry field
  edits are whole-entry `dictAdd` re-issues (model-faithful: a dict entry IS a value). `CommitDictTests` covers
  add + whole-entry rewrite + remove for an object dict. **Server capability now matches the client's object
  dict needs** — the remaining work is purely the client-side routing (4b/4c) + handler deletion (4d).
- **T6b-4b (DONE):** dict `ExecArray` + entry `ExecObject` now carry R7 addressing — `ownerRef`
  (the dict owner's object id) + `dictProp` (the dictionary-typed prop) on the array, plus `key`
  on each entry object. Populated in `DbBridge` (the runtime render) from the owner/prop/key it
  already knows; emitted by `ClientState` (mirroring the existing `sourcePath` path); merged into
  the client's `ExecArray`/`ExecObject` via `dt.ts` (and copied in `workbench.ts` for the designer
  seed-graph). The client's `setDictEntry` also stamps the entry's `ownerRef`/`dictProp`/`key` from
  the merged dict array, so optimistic edits before a refetch are addressable. **No behavior change**
  — purely extra wire fields; the 3 dict live ops (`addEntry`/`removeEntry`/`write`) still fire.
  `CodeExecutorTests` asserts the model-level addressing for both scalar and object dicts.
- **T6b-4c (DONE 2026-07-13):** client dict hooks → commit ops: `entryAdd`→`dictAdd`, `entryRemove`→`dictRemove`,
  `pathWrite`→`dictAdd` whole-entry (decision flagged + accepted: object-entry field edit = whole-entry rewrite).
- **T6b-4d (DONE 2026-07-13):** delete `HandleWrite`/`HandleAddEntry`/`HandleRemoveEntry` + response records + case arms;
  add `write`/`addEntry`/`removeEntry` reject tests (WsWireShapeTests); client-side routing landed.

Plan T6b as-originally-written is now complete for both array and dict sides. The "commit is the SOLE persistence op" goal
is reached across sets and dicts. (Array side in prior commits; dict side in ad03944 + supporting client addressing.)

> Why split: T6a is the hard, testable engineering (new `commit` op + draft discovery). T6b is mechanical
> deletion that is ONLY safe once T6a is green — doing them together risks a half-cutover (client sends a
> `commit` the server can't apply, or a live op the server no longer serves). The split keeps each land
> reviewable and revertible.

### Task 7: `refactor(app): wrapNode emits setByProp; drop nested children refId`

**Files:** `DeEnv/instances/1/app.deenv`, `DeEnv.Tests/Code/StoreConcurrencyTests.cs`
- `wrapNode` (L489): remove `wrapper.children = [site.node]`; after `site.parentSet.add(wrapper)` buffer
  `setByProp(wrapperDraft,"children",site.node)`; `site.parentSet.remove(site.node)` → `setUnlink`. Net ONE
  `commit`: [create wrapper] + [setByProp(-1,"children",node)] + [setUnlink(parentSet,node)].
- **VERIFICATION GAP (state in PR):** `wrapNode` *click* has no headless test. Prove via `StoreConcurrencyTests`:
  a `commit` with `setByProp(-1,"children",member)` puts member in wrapper.children + unlinks old parent.

### Task 8: `docs: record the unified model + remove-not-delete + commit-is-mandatory`

**Files:** `docs/plans/2026-07-12_143000-transparent-client-mutations-t2-t4.md`, `DECISIONS.md`
- `DECISIONS.md`: "Unified `commit` — every model op (edit / ref / set link-unlink / create / dict write-remove /
  remove-detach) is a `commit` relation or edit. `commit` is the SOLE server persistence op: the 7 live mutating
  wire ops are deleted (client never sends them; server rejects them). `remove` detaches; deletion is GC's call,
  never guaranteed (AppLog retains orphans). No `delete` store op exists — the ACL `delete` verb maps to `remove`."
- Relabel T4.2/T4.3 DONE; note T4.4 (UI macro) still needs sign-off (default: keep).

### Task 9: `refactor: unify naming across client/server commit vocabulary`

**Files:** `DeEnv/Instance/ws.ts`, `DeEnv/Http/WsHandler.cs`
- **Collision fix (discipline #4):** rename the client buffered-create interface `CommitCreate { draft, join }`
  (ws.ts L70) → `StagedCreate`, so it no longer collides with the server `CommitCreate(int TempId, …)` record
  (IInstanceStore.cs L192). Update `commitCreates: CommitCreate[]` → `commitCreates: StagedCreate[]` and all refs.
  (The server `CommitCreate` is the model term — keep it; the client one is a UI-staging buffer — rename it.
  Both are RUNTIME-INTERNAL; no user-code surface changes.)
- **NO begin/end verb in user code (discipline #5):** do NOT rename `runHandlerTransaction`/`flushHandlerTx` to a
  `beginHandlerCommit`/`endHandlerCommit` pair — that would surface a begin/end verb, violating "user code sees
  only plain commit." Keep `runHandlerTransaction` as the runtime's atomic-action wrapper; it calls the
  RUNTIME-INTERNAL `beginCommit`/`endCommit` primitives (same ones the form Save uses via `ctx.commit`) plus its
  handler-specific rollback + action-miss logic. User Code never names a bracket.
- **Audit for residual drift:** grep `beginCommit`/`endCommit` — confirm ALL call sites are inside the runtime
  (ws.ts / codeExec.ts / ui.ts), NONE in app.deenv. `ctx.commit` is the only commit verb app.deenv may use (form
  Save). No behavior change — pure rename of the internal interface + a doc clarification.
- GREEN: build + `DesignerSourceTests` 31/31; `grep -rn "CommitCreate\b" DeEnv/Instance` shows ONLY the server
  record reference (none on the client); `grep -rn "beginCommit\|endCommit" DeEnv/instances` returns NOTHING
  (no user-code surface).

## Files that change

- `DeEnv/Storage/IInstanceStore.cs` — `DictWriteMutation` promoted to wire-accepted (T6a.1)
- `DeEnv/Storage/JsonFileInstanceStore.cs` — apply `DictWriteMutation` from wire (T6a.1)
- `DeEnv/Http/WsHandler.cs` — parse `dict` (scalar + object entry) → `DictWriteMutation` (T6a.1); delete
  `HandleWrite`/`HandleAddEntry`/`HandleRemoveEntry`/`HandleArrayAdd` + `NestedSetLinks` + dead response
  records (T6b)
- `DeEnv/Instance/ws.ts` — dict `ExecArray` carries `prop` (T6a.1); `entryAdd`/`entryRemove`/`pathWrite` buffer
  `dictAdd`/`dictRemove` (T6a.1); `arrayAdd` mint stages into enclosing form ctx OR `stageTopLevel` (T6a.2); delete
  remaining live sends (T6b); rename `CommitCreate`→`StagedCreate` (T9)
- `DeEnv/instances/1/app.deenv` — `wrapNode` setByProp (T7); call-site renames (T9)
- `DeEnv.Tests/Code/CommitDictTests.cs` (NEW) — `commit` dict write/remove applies (T6a.1)
- `DeEnv.Tests/Code/ArrayAddNestedRefTests.cs` — stays (T6a.2 proves mint-via-commit)
- `DeEnv.Tests/Code/StoreConcurrencyTests.cs` — extend remove/wrap (T2,T7)
- `DeEnv.Tests/Steps/AddEntrySetBatchSteps.cs` — commit dict-set-mint (T6b)
- `docs/plans/...t2-t4.md`, `DECISIONS.md` (T8)

## Verification grid (headless gaps explicit)

| Suite | Covers | Headless? |
|---|---|---|
| `StoreConcurrencyTests` | RemoveMutation detach+GC; wrap setByProp | ✅ |
| `CodeClientTests` | each live op buffers correct commit form | ✅ |
| Gherkin `AddEntrySetBatchSteps` / dict steps | server applies commit dict/set-mint | ✅ (real store+session) |
| `DesignerSourceTests` | app.deenv parses | ✅ 31/31 |
| `wrapNode`/`unwrapNode` CLICK | real UI | ❌ no headless harness — indirect only |

## Risks

- **R1 (headless gap):** `wrapNode` click unexercised headless (mitigated by `StoreConcurrencyTests` + `CodeClientTests`).
- **R2 (GC semantics):** `RemoveMutation` must NOT guarantee deletion — if the object stays reachable, GC leaves
  it. Tests must assert a still-referenced object SURVIVES a `remove`. This is the user's explicit discipline.
- **R3 (blast radius):** opening `commitCreates`/`commitEdits` in the handler bracket flips every designer
  handler + palette `add`/`addEntry` to atomic-commit. Audit `app.deenv` for such call sites; confirm dict-create
  routes through `dictAdd` relation (model now supports it), not a stray `addEntry`.
- **R4 (ACL):** `remove` maps to the `delete` verb floor (InstanceDescription.cs L83). Confirm the floor rejects a
  non-deletable object exactly as `HandleRemoveEntry` did — no behavior change.
- **R5 (commit scopes — THE GOTCHA):** discipline #3 means every mutation reaches the server as a `commit`, but
  the THREE scopes stage differently (T5): form ctx = explicit bracket (unchanged); handler ctx = opens all three
  buffers (fe25f3d, done); TOP-LEVEL ctx = NO bracket today → commits **immediately per mutation** via a
  micro-bracket (beginCommit→stage one→endCommit). Risk if missed: a top-level mutation with no open bracket
  hits a deleted-live-op branch and silently drops. Mitigated by the grep guard (no live `op:` sends) + a
  `CodeClientTests` assertion that a top-level edit emits exactly ONE `commit`. T6a.2 ADDS the nested-defer scope:
  a mint under an enclosing form (no bracket open yet) must stage into the form ctx's pending creates WITHOUT an
  `endCommit`, so the form's Save flushes it. Pick graph-walk (ctx.commit discovers negId drafts) or lazy
  `commitCreates` on the enclosing ctx; the two gate tests (`A_create_under_a_save_less_container_persists_immediately`
  + `A_create_under_an_object_form_defers_to_that_forms_save`) must BOTH stay green.
- **R6 (sequence — build ops before deleting handlers):** per the user's 2026-07-13 direction, T6a builds the
  dict-write + nested-create `commit` ops and PROVES them (headless tests) BEFORE T6b deletes the 4 live handlers.
  Doing them together risks a half-cutover: the client sends a `commit` the server can't apply (dict-write not
  yet wired) or a live op the server no longer serves. The split keeps each landing reviewable + revertible.
  Deleting the 4 handlers means a stale/old client or stray message gets `Unknown op` (correct: fail loud, no
  silent partial persist) — but no test/harness may still send those ops (T6b updates them).
- **R7 (dict `prop` naming — NEW, T6a.1):** the client `ExecArray` for dicts must carry the dict `prop` name
  (today it only knows the owning path). Without it `entryAdd`/`entryRemove`/`pathWrite` cannot build a
  `{kind:"dict", ownerRef, prop, key, ...}` wire. Mirror how the set `ExecArray` carries its containing set's id.
- **R8 (naming purity — NEW):** Task 9 is a pure rename; risk is only mechanical breakage. The grep guard (no
  client `CommitCreate`, no `runHandlerTransaction`/`flushHandlerTx`) proves completeness.
