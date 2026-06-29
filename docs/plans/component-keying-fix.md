# Component slot/closure keying fix — plan

A **latent core-reconciliation defect** that blocks reusable nested components (it's why
the `Table` extraction and `KebabMenu` adoption thrash). NOT a live breakage — shipped
generic-UI nesting is statically positioned, so it's genuinely distinct. This is a
**foundation fix** ("solid core") before generalizing the lib or building on it.

> **✅ LANDED 2026-06-29 — commit `2f7f518` on main. The navigator analysis below is
> OVER-SCOPED (historical); the real fix is recorded here.**
>
> **The journey:** navigator analysis = "segment every `comp:` slot path" (over-scoped) →
> a RED repro re-diagnosed it precisely → a first implementation attempt (a `renders`
> side-effect guard that refused to read-hit a compute that *built a tag*) was caught by
> the architecture review as **structurally incomplete**: a render-prop returning a **bare
> scalar** (`body={() => row.n}` → text) builds no tag, so it slipped through and still
> collided → the **correct** fix landed.
>
> **The real defect:** it's a **TS-twin-only** divergence. A render-prop/lambda call was
> memoized by `"fn:" + fn.id + args` with **no slot segment** (`codeExec.ts` `executeCall`,
> `CodeExecutor.cs` `CallFunction`). The same `body()` invoked at two component child-slots
> — or once per `foreach` row — collides on one key. C# `Memoize` is write-only (never
> read-hits → always recomputes → correct); TS `memoize` read-short-circuits, so the 2nd
> call replays the 1st's render. NOT the `comp:` composition.
>
> **THE FIX:** fold the live **`slotPath` into the `fn:` call memo key, identically in both
> twins** — `"fn:" + fn.id + "@" + slotPath.join("/")` (TS) / `$"fn:{id}@{Join("/",SlotPath)}"`
> (C#). Each call-site/row gets a distinct key → disambiguates **every** shape, including the
> bare-scalar render-prop a side-effect heuristic can't see. Identical composition is
> mandatory: a `fn:` result the server SHIPS (private inputs) is found by the client **by
> key**, so the twins must agree. Same-site re-render still cache-hits; only cross-site
> pure-helper sharing is lost (negligible — expensive computes have their own `extent:`/
> `where:` keys).
>
> **Gate (all green):** 3 conformance regressions — render-prop at two slots, foreach fresh
> closures returning a component, **foreach returning a bare scalar** — plus the
> `Access.feature` per-row-menu Gherkin; C# conformance **138/138**; full suite **zero new
> failures** (the 8 reds are pre-existing on main, unrelated). Unblocks reusable nested
> components (KebabMenu, the Table generalization).

## Root cause (one defect, two surfaces)
A component's identity is `"comp:" + slotPath.join("/")` (+ `#key` if `key=` given),
built in `executeComponentValue` ([codeExec.ts:1394](../../DeEnv/Instance/codeExec.ts);
C# `ExecuteComponentValue` `CodeExecutor.cs:1469`). `slotPath` is a **single module-level
array** (`codeExec.ts:301`), mutated only in `executeTagChildren` (push static AST child
index, `:1352`) and `executeTagForEach` (push `"row"+memberId`, `:1528`). It is **a flat
positional thread over the whole render tree — NEVER reset/segmented at a component
boundary** (`invokeFn`/`InvokeClosure` run the body without touching `slotPath`).

Plus `objectArgKey` (`codeExec.ts:1459`) keys object args by `i + ":" + id` but **excludes
functions** (`:1463`).

- **Surface 1 — nested-component collision.** A component reached *through a render-prop/
  callback* (`body(close)`, `rowActions(m)`, `createForm`) emits its tags at the slotPath
  live **at the call site** (the host's path), not where the prop was defined. Two such
  components at the same positional index across that boundary get the same `comp:` key →
  `memoize(slotKey,…)` (`:1426`) hands the second the first's cached view.
- **Surface 2 — foreach fresh-closure arg.** A data arg (`rowActions(m)`) works: the
  `"row"+id` slot segment AND `objectArgKey` both differ per row. A **closure** arg is a
  function → invisible to `objectArgKey`, and if the closure-receiving component isn't
  itself inside the foreach's `row` segment, every row resolves to the same slot key +
  empty argsKey → all rows get the FIRST instance. Client-only **infinite loop**:
  `rebindComponentArgs` (`:1473`) never reaches a fixpoint when the closure's captured
  scope churns but its argsKey stays empty (C# renders once, so it doesn't loop — a
  twin-asymmetry hazard).

Shared defect: **slot path is a flat positional thread, not a hierarchy of component
identities; and arg-keying is object-only.**

## Fix (two coordinated changes, TWIN-LOCKSTEP)
- **A — segment the slot path per component boundary.** In `executeComponentValue`
  (`codeExec.ts:1394` / C# `:1469`), around invoking setup + view (`invokeFn` `:1426,1436`
  / `InvokeClosure` C# `:1116,:1475,:1487`), **push the component's own slotKey as a fresh
  path root and restore on exit**, so a child keys as `comp:<parentSlotKey>/<child-local>`.
  Two children via different parent instances then can never share a key.
- **B — per-invocation discriminator for closure args.** Fold the closure's captured
  non-top scope key (the walk `closureKey` already does, `:373`) into the slot/arg key, so
  a per-row `body`/`close` gets a per-row segment. The missing dual of `objectArgKey` for
  functions — key on **stable captured identity (object ids)**, NOT the function's
  ephemeral identity (over-keying → a different infinite loop).

Both must change **identically** in `CodeExecutor.cs` and `codeExec.ts` (the slot key
drives the seed map `applySeed` `:1445` and the handler index `HandlerKey` `:1330`; SSR +
hydrate must derive byte-identical keys).

## Sequence — REPRO-FIRST (the bug isn't in any shipped scenario)
1. **Build the failing repro** (safe): conformance cases + a Gherkin that concretely
   trigger BOTH surfaces — (i) two distinct components at the same global positional index
   across a callback boundary → assert distinct views; (ii) a foreach passing a fresh
   per-row closure to a render-prop component → assert each row sees its own closure. Pin
   the exact construction before touching the twins.
2. **Design the fix against the repro** → confirm before the core change.
3. **Implement** twin-lockstep + the regression proofs below.

## Risk map (what a botched fix regresses — each needs a proof)
- **Stable component re-keys every render** (off-by-one in push/restore) → `var state`
  resets every keystroke (lost focus/draft). *Proof:* type into a nested create-form field
  across re-renders; value + caret survive (`ui.ts:543` caret guard).
- **Negative-id remap** — confirm the foreach `"row"+id` slot segment and the DOM
  `data-key` (`:1532`) stay the SAME id source so a just-added row's component state moves
  across remap. *Proof:* add-a-set-member then edit before ack; the row's component (e.g.
  KebabMenu) keeps open/closed state across negative→positive remap (`ui.ts:479`).
- **objectArgKey over-keying** — a literal lambda rebuilt every render must NOT re-bind the
  component every render. *Proof:* a render-prop component with a STABLE closure arg is not
  re-bound (no extra recompute), alongside the per-row-fresh case that MUST get distinct ids.
- **Seed application** — a re-keyed slot silently drops the client view-state seed
  (`applySeed` `:1445`) → a client-toggled popup fails to reproduce server-side → wrong
  harvest. *Proof:* a seed conformance case under the new nesting.

## Key anchors
- Slot key: `codeExec.ts:1394-1397`; C# `:1469-1473`
- Slot path mutation: `codeExec.ts:1352-1356`, `:1528-1531`; C# `:1425-1427`, `:1552-1554`
- Component invoke (no reset — fix site): `codeExec.ts:1426,1436,1492`; C# `:1475,1487,1116`
- closureKey: `codeExec.ts:373-379`; C# `:1233-1240`
- objectArgKey (function-blind): `codeExec.ts:1459-1465`; rebind `:1473-1487`
- Handler slot/key: `codeExec.ts:1322,1330`; C# `:1390,1400`
- Reconcile + remap: `ui.ts:461-500`; slot-cache drop on nav `ui.ts:390-402`
