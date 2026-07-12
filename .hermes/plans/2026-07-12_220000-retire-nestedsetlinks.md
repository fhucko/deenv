# Uniform set-member create — retire `NestedSetLinks` + `HandleAddSetMember` `-1` batch

> **For Hermes:** Plan-only. Execute via subagent-driven-development (one fresh `delegate_task` per task,
> two-stage review: spec compliance then code quality). TDD red→green per task. Conventional commits.
>
> **TEST INVOCATION (cost 2 wasted runs 2026-07-12):** `dotnet test DeEnv.Tests -- --treenode-filter
> "/*/*/<RealClassName>/*"` from **PowerShell** (git-bash mangles `/*/…` → 0 tests). Do NOT run
> `dotnet test DeEnv.Tests.csproj --treenode-filter` (no `--` → TUnit prints help, exit 5). `dotnet test
> --no-build` runs a STALE assembly — always `--no-incremental` with a fresh build.
>
> **GRILL OUTCOME (2026-07-12):** the original T4.2 plan was too narrow. The "id-sign smell" is a CLASS:
> the server reinvents the `commit` mint+link inside TWO live ops (`arrayAdd`/`NestedSetLinks`, and
> `addEntry`/`HandleAddSetMember`) each with its own `localTempId = -1`. The user wants ALL creates uniform.
> Investigation found the create-form Save ALREADY uses `commit` (`commitCreate`), and DICTIONARIES cannot
> use `commit` (model forbids — `DictWriteMutation` is server-side only, IInstanceStore.cs L204-213), so
> they correctly stay on `addEntry`. Scope below = kill S1 + S2 (both set-member create paths → `commit` +
> `setByProp`), document the uniform model with the one model-mandated dict exception.

## Goal

Every set-member / object CREATE goes through the single unified `commit` op (carrying `setByProp` for a
fresh-owner link), so the server no longer invents `localTempId = -1` mint+link batches inside `arrayAdd`
or `addEntry`. Uniform mutation model; no id-sign smell; no duplicate create mechanism.

## The smell class (verified by reading)

| site | op | server invent | smell? |
|---|---|---|---|
| `NestedSetLinks` (WsHandler.cs L1729) + `HandleArrayAdd` (L1571) | `arrayAdd` (set-member mint) | `-1` + nested `children:[{refId}]` → `SetLinkByPropMutation(-1,…)` | **S1** ✅ |
| `HandleAddSetMember` (L733) | `addEntry` (set-member mint) | `-1` + `SetLinkByPropMutation(ownerId,prop,-1)` | **S2** ✅ |
| `SchemaBridge` (L892-1011) | printer | `nextTempId--` + `SetLinkByPropMutation` | S3 — server-side model code, NOT a wire smell; the CANONICAL pattern the wire should match. Leave. |
| create-form Save | — | already `commit` via `commitCreate` (codeExec.ts L2877-2947) | not a smell ✓ |
| dictionary `addEntry`/`removeEntry` | — | `_store.CreateEntry` | model forbids `commit` for dicts → KEEP (IInstanceStore.cs L204-213) |
| `setReferenceField` | — | atomic single op | KEEP |

## The uniform model this establishes

```
CLIENT wire ops (after):
  commit            → edits + creates + relations(set / setUnlink / setByProp / setUnlinkByProp)
                       ↑ ALL object/set-member creates (form Save, palette add, wrapNode, addEntry-on-set)
  objectPropChange  → live scalar edit          (atomic, keep)
  setReferenceField → live single-ref write     (atomic, keep)
  arrayRemove       → live set unlink           (atomic, keep)  [add still buffered into commit when in-bracket]
  removeEntry       → live set/dict remove      (atomic, keep)
  addEntry          → dictionary create ONLY    (model does not support dict-in-commit)
```

One create mechanism (`commit`). Two structural rules the plan enforces:
1. A fresh-owner set link is a `setByProp(ownerRef<0, prop, memberRef)` relation on the `commit` — the
   client carries its own transient id, the server resolves the negative owner (already works, T2.5). No
   server-invented `-1`.
2. `arrayAdd`/`addEntry` set-member mints become `commit` creates (buffered when inside a bracket, else a
   standalone `commit` frame — see Task 1/3).

## Plan

### Task 1: `feat(ws): client CommitRelation supports setByProp / setUnlinkByProp`

**Files:** `DeEnv/Instance/ws.ts`
- Extend `CommitRelation.wire` union (L82-88) to add
  `{ kind:"setByProp", ownerRef, prop, childId }` and `{ kind:"setUnlinkByProp", ownerRef, prop, childId }`.
- The wire-builder already pushes `r.wire` verbatim (L1061), and the server accepts these kinds (T1/T2),
  so no builder change needed — just the union + the hooks below emitting them.
- GREEN checkpoint: build + `DesignerSourceTests` 31/31 (no behavior change yet).
- RED (later tasks exercise it): add `CodeClientTests` asserting a buffered `setByProp` relation carries
  `{kind:"setByProp", ownerRef:<neg>, prop, childId}`.

### Task 2: `feat(ws): arrayAdd mint → commit create (+ setByProp when fresh-owner)`

**Files:** `DeEnv/Instance/ws.ts`
- `arrayAdd` (L877): today a NEW draft (id<0) fires a live `arrayAdd` (L916). Change: when a commit bracket
  OR handler transaction is open (`commitCreates != null`), buffer a `commitCreate` (draft + join) instead of
  the live `arrayAdd`. If the set is a fresh owner's prop (the wrapper case), the join already yields a
  `setByProp` relation at flush (endCommit wires `set`/`setByProp` from the join — extend L1052-1055 to emit
  `setByProp` when `c.join.set` is a freshly-created draft's prop; for the existing-set case emit `set`).
- Outside any bracket: keep the live `arrayAdd` (back-compat for any non-bracket caller — there should be
  none post-change, but preserve behavior).
- RED: `CodeClientTests` — a bracketed `coll.add(newDraft)` buffers ONE `commitCreate` (not a live
  `arrayAdd`); flush emits ONE `commit` carrying the create + the `set`/`setByProp` relation.
- GREEN: passes. `DesignerSourceTests` 31/31.

### Task 3: `refactor(ws): delete NestedSetLinks; arrayAdd-create no longer nests set refs`

**Files:** `DeEnv/Http/WsHandler.cs`, `DeEnv.Tests/Code/ArrayAddNestedRefTests.cs`
- Delete `NestedSetLinks` (L1729-1756) + its call + `nestedLinks` local (L1571-1577) in `HandleArrayAdd`.
- `HandleArrayAdd`'s create value no longer carries nested `children:[{refId}]` for linking — a plain mint.
  The fresh-owner link now arrives as a `setByProp` relation on a `commit` (T1/T2 wire already supports it).
- Delete `ArrayAddNestedRefTests.cs` (T4.3).
- RED→GREEN: build clean; `ArrayAddNestedRefTests` gone; `HandleArrayAdd` with a plain (no nested-ref) value
  still mints a member; `DesignerSourceTests` 31/31.

### Task 4: `feat(ws): addEntry-on-a-set → commit create (kill S2)`

**Files:** `DeEnv/Instance/ws.ts`, `DeEnv/Http/WsHandler.cs`, `DeEnv.Tests/Steps/AddEntrySetBatchSteps.cs`
- Client `entryAdd` (L947): when the array's sourcePath denotes a SET (it already knows `arr.sourcePath`)
  AND a bracket is open, buffer a `commitCreate` with a `setByProp` join (the set is the owner's prop) instead
  of the live `addEntry`. Dictionary entries (`arr` is a dict) keep the live `addEntry` (model exception).
- Server `HandleAddSetMember` (L733): delete the `localTempId = -1` `CommitBatch` branch. A set-member mint
  now arrives as a `commit` carrying a `create` + `setByProp` relation (or a plain `arrayAdd`-style `set` for
  an existing-set add). Keep the `refId` (existing member) branch + the dict branch.
- **RED:** extend `AddEntrySetBatchSteps` (real store + live session, no browser) — a `commit` minting a set
  member via `setByProp` links it into the owner's prop set in ONE batch (mirrors the existing `addEntry`
  set-batch scenarios, which become the `commit` equivalents). Assert exactly one new object + linked into
  owner prop.
- **GREEN:** the extended scenarios pass; `HandleAddSetMember`'s `-1` branch is gone; `DesignerSourceTests` 31/31.

### Task 5: `refactor(app): wrapNode emits setByProp; drop nested children refId`

**Files:** `DeEnv/instances/1/app.deenv`, `DeEnv.Tests/Code/StoreConcurrencyTests.cs`
- `wrapNode` (L489): remove `wrapper.children = [site.node]`. After `site.parentSet.add(wrapper)`, buffer a
  `setByProp(wrapperDraft, "children", site.node)` — the `add` is the mint (Task 2), the link is explicit
  `setByProp` on the fresh wrapper. `site.parentSet.remove(site.node)` stays as `setUnlink`.
- Net ONE handler → ONE `commit`: [create wrapper] + [setByProp(-1,"children",site.node)] + [setUnlink(parentSet,site.node)].
- `unwrapNode` (L527) unchanged (only moves existing members).
- **VERIFICATION GAP (call out in review):** the real `wrapNode` *click* has no headless test. Prove via
  `StoreConcurrencyTests`: a `commit` with `setByProp(-1,"children",member)` puts member in wrapper.children
  and unlinks from old parent (extend the existing T1 wrap scenario). Client emit proven by `CodeClientTests`
  (Task 2). Only the end-to-end click is unexercised.
- RED: `StoreConcurrencyTests` extended — commit with `setByProp(-1,"children",member)` → member in
  wrapper.children. GREEN: passes. `DesignerSourceTests` 31/31.

### Task 6: `docs(plans)+docs(decision): record the uniform model`

**Files:** `docs/plans/2026-07-12_143000-transparent-client-mutations-t2-t4.md`, `DECISIONS.md`
- Relabel T4.2 "DEFERRED" → "DONE (2026-07-12): S1 `NestedSetLinks` deleted; S2 `HandleAddSetMember` `-1`
  batch deleted; both set-member creates now `commit`+`setByProp`." T4.3 test deleted.
- Add `DECISIONS.md`: "Uniform create model — every object/set-member create is a `commit` carrying
  `setByProp` for a fresh-owner link; the server never invents a temp id. Dictionaries keep `addEntry`
  (model forbids dict-in-commit, IInstanceStore.cs L204-213). `SchemaBridge` is the canonical
  mint+link-by-prop reference."
- **T4.4 (retire wrap/unwrap UI macro):** product sign-off, not code. Default per minimal-by-default: KEEP
  the `wrap`/`unwrap` buttons (thin layer over generic mutations). Record as accepted unless told otherwise.

## Files that change

- `DeEnv/Instance/ws.ts` — `CommitRelation` union (T1); `arrayAdd`→commit (T2); `entryAdd` set→commit (T4);
  handler bracket opens `commitCreates` (T2/5 wire into runHandlerTransaction already opens it per T3 plan).
- `DeEnv/Http/WsHandler.cs` — delete `NestedSetLinks` (T3); delete `HandleAddSetMember` `-1` batch (T4).
- `DeEnv/instances/1/app.deenv` — `wrapNode` `setByProp` (T5).
- `DeEnv.Tests/Code/ArrayAddNestedRefTests.cs` — delete (T3).
- `DeEnv.Tests/Steps/AddEntrySetBatchSteps.cs` — extend to `commit` mint (T4).
- `DeEnv.Tests/Code/StoreConcurrencyTests.cs` — extend wrap scenario (T5).
- `docs/plans/...t2-t4.md`, `DECISIONS.md` (T6).

## Verification grid (be explicit about headless gaps)

| Suite | Covers | Headless? |
|---|---|---|
| `CodeClientTests` | client buffers `commitCreate` + `setByProp` (T2/T4) | ✅ |
| `AddEntrySetBatchSteps` (Gherkin) | server applies `commit` set-mint (T4) | ✅ (real store + session) |
| `StoreConcurrencyTests` | server applies `commit` `setByProp(-1,…)` wrap (T5) | ✅ (real store + session) |
| `ArrayAddNestedRefTests` | deleted (T3) | — |
| `DesignerSourceTests` | app.deenv parses after edits | ✅ 31/31 |
| `wrapNode` / `unwrapNode` CLICK | real UI | ❌ NO headless harness — proven indirectly only |

## Risks

- **R1 (headless gap):** the `wrapNode` *click* is not exercised headless. Mitigated: client emit by
  `CodeClientTests`, server apply by `StoreConcurrencyTests`. State clearly in PR.
- **R2:** opening `commitCreates` in handler bracket + making `arrayAdd`/`entryAdd` set-mints buffer into
  `commit` flips EVERY designer handler that does `coll.add(newX)` / set `addEntry` from live-op to
  atomic-commit. Audit `app.deenv` for such handlers (grep `.add(` / `addEntry` in `onClick`/callback fns) —
  they should all be atomic. Confirm no dict-create accidentally routes through `commit` (model would reject).
- **R3:** `HandleAddSetMember`'s `refId` (link existing) + dict branches must survive Task 4 untouched.
- **R4:** the live `arrayAdd`/`addEntry` fallback (outside a bracket) must remain for correctness; only
  bracketed mints buffer into `commit`.
