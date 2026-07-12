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

## Model vocabulary = the contract (every commit op maps to one IInstanceStore op)

| Model op (`IInstanceStore`) | commit form | status |
|---|---|---|
| `WriteField` | `edit {objectId, prop, value}` | ✅ already |
| `WriteReference` (clear = target null) | `RefLinkMutation` | ✅ already |
| `AddToSet` | `SetLinkMutation` | ✅ already |
| `RemoveFromSet` | `SetUnlinkMutation` | ✅ already |
| `CreateObject` | `CommitCreate` | ✅ already |
| `SetLinkByProp` (model helper) | `SetLinkByPropMutation` | server-only today → **promote to wire** |
| `SetUnlinkByProp` (model helper) | `SetUnlinkByPropMutation` | server-only today → **promote to wire** |
| `CreateEntry` (dict) | `DictWriteMutation` | server-only today → **promote to wire** |
| `RemoveDictionaryEntry` (dict) | **`DictRemoveMutation`** (NEW) | must add to union |
| **`Remove`/detach (unlink everywhere)** | **`RemoveMutation`** (NEW) | must add to union; then `CollectGarbage` |

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
- ParseRelation second pass (L878): add `case "dict"` → parse `{ownerRef/parentId, prop, key, value}`; resolve
  owner (negative allowed); emit `DictWriteMutation(ownerRef, prop, key, value)` (value via `DeserializeValue`/
  `ParseKey` — same as `HandleAddEntry` L615-617). Add `case "dictRemove"` → `{ownerRef, prop, key}` →
  `DictRemoveMutation`. Add `case "remove"` → `{objectId}` → `RemoveMutation(Resolve(session,objectId))`.
- Access floor: `dict`/`dictRemove` mirror `HandleAddEntry`'s `RequireDictWrite` (L606) — the owner edit floor.
  `remove` floors the object as `delete` (acl verb) — mirror `HandleRemoveEntry`'s `RequireWrite(...,"delete",...)`
  (L639). **This is the one place the `delete` ACL verb maps to a real op** (remove), consistent with
  InstanceDescription.cs L83's verb list.
- Drop the `continue` skip at L838-840 so `dict`/`dictRemove`/`remove` are NOT skipped (they now parse here).
- RED: `CodeClientTests`/`WsHandler` test — a `commit` carrying `dict` writes a dict entry; `remove` detaches.
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
  - `entryAdd` (L959): DELETE live `addEntry`; SET→`commitCreate`+`setByProp`; DICT→`dict` relation.
  - `entryRemove` (L970): DELETE live `removeEntry`; SET→`setUnlink`; DICT→`dictRemove`.
- GREEN: `DesignerSourceTests` 31/31; `CodeClientTests` asserts (a) NONE of the 7 live ops is ever sent, and
  (b) a top-level edit produces exactly ONE `commit` with one `edit` (micro-bracket), not a live `objectPropChange`.

### Task 6: `refactor(ws): delete live mutating handlers on the server; commit is the only persistence path`

**Files:** `DeEnv/Http/WsHandler.cs`, `DeEnv.Tests/Code/ArrayAddNestedRefTests.cs`, `DeEnv.Tests/Code/*Live*Tests.cs`
- Delete `NestedSetLinks` (L1729) + call (L1571-1577). `HandleArrayAdd` create value no longer carries nested
  `children:[{refId}]`; fresh-owner link arrives as a `setByProp` relation on `commit`.
- Delete the 7 live mutating handlers entirely (discipline #3 — they bypass `commit`): `HandleWrite` (L486),
  `HandleAddEntry` (L586), `HandleRemoveEntry` (L624), `HandleObjectPropChange` (L767), `HandleSetReferenceField`
  (L1159), `HandleArrayAdd` (L1493), `HandleArrayRemove` (L1681). Remove their `case` arms in the `op switch`
  (L447-455) — any received op among `write`/`addEntry`/`removeEntry`/`objectPropChange`/`setReferenceField`/
  `arrayAdd`/`arrayRemove` now hits the `_ => Error("Unknown op …")` default (loud reject, no silent persist).
- `HandleAddSetMember`'s `localTempId=-1` `CommitBatch` branch is gone with `HandleAddEntry`.
- Delete the now-dead response records: `WriteResponse`, `AddEntryResponse`, `RemoveEntryResponse`,
  `ObjectPropChangeResponse`, `SetReferenceFieldResponse`, `ArrayAddResponse`, `ArrayRemoveResponse`.
- Delete `ArrayAddNestedRefTests.cs` + any `*Live*Tests` that exercised the live handlers.
- RED→GREEN: build clean; those tests gone; a Gherkin/unit test asserts a `write`/`arrayAdd`/`objectPropChange`
  frame returns `Unknown op` (error, not a persisted change). `DesignerSourceTests` 31/31.

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

- `DeEnv/Storage/IInstanceStore.cs` — 2 new records (T1)
- `DeEnv/Storage/JsonFileInstanceStore.cs` — apply `RemoveMutation`/`DictRemoveMutation` (T2)
- `DeEnv/Http/WsHandler.cs` — parse `dict`/`dictRemove`/`remove`; delete `NestedSetLinks` + the 7 live handlers + dead response records (T3,T6)
- `DeEnv/Instance/ws.ts` — `CommitRelation` union (T4); DELETE 7 live sends, buffer all into commit (T5); rename `CommitCreate`→`StagedCreate` + handler-bracket pair (T9)
- `DeEnv/instances/1/app.deenv` — `wrapNode` setByProp; call-site renames (T7,T9)
- `DeEnv.Tests/Code/ArrayAddNestedRefTests.cs` — delete (T6)
- `DeEnv.Tests/Code/StoreConcurrencyTests.cs` — extend remove/wrap (T2,T7)
- `DeEnv.Tests/Steps/AddEntrySetBatchSteps.cs` — commit set-mint (T6)
- `DeEnv.Tests/Code/*Live*Tests.cs` — delete (T6)
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
  routes through `dict` relation (model now supports it), not a stray `addEntry`.
- **R4 (ACL):** `remove` maps to the `delete` verb floor (InstanceDescription.cs L83). Confirm the floor rejects a
  non-deletable object exactly as `HandleRemoveEntry` did — no behavior change.
- **R5 (commit scopes — THE GOTCHA):** discipline #3 means every mutation reaches the server as a `commit`, but
  the THREE scopes stage differently (Task 5): form ctx = explicit bracket (unchanged); handler ctx = must also
  open `commitEdits`+`commitCreates` (today only `commitRelations`); TOP-LEVEL ctx = NO bracket today → must
  commit **immediately per mutation** via a micro-bracket (beginCommit→stage one→endCommit). Risk if missed: a
  top-level edit with `commitEdits==null` would hit the deleted-live-op branch and silently drop. Mitigated by
  the Task 5 grep guard (no live `op:` sends) + a `CodeClientTests` assertion that a top-level edit emits exactly
  ONE `commit` (micro-bracket), and that the top-level ctx is identified so the micro-bracket wraps it.
- **R6 (server reject behavior — NEW):** deleting the 7 live handlers means a stale/old client or a stray message
  gets `Unknown op` instead of a persisted change. That is CORRECT (fail loud, no silent partial persist), but
  any TEST/harness still sending those ops must be updated (Task 6 deletes them).
- **R7 (naming purity — NEW):** Task 9 is a pure rename; risk is only mechanical breakage. The grep guard (no
  client `CommitCreate`, no `runHandlerTransaction`/`flushHandlerTx`) proves completeness.
