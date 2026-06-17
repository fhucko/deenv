# M11 — Reactivity foundation: analysis + first-slice plan

**Status:** design/plan for a dedicated implementation session — *not built*. M11 is
SolidJS-style reactive components + the public component library (the UI middle-ground);
this doc is the **foundation half** (the reactivity refactor) and its **first slice**.
Land M10 first — don't interleave. See DECISIONS "UI middle-ground — one public component
library + SolidJS-style reactivity" and ROADMAP M11. *Grounded by codebase-navigator +
milestone-planner, 2026-06-17.*

## 1. Analysis — how reactivity works today, and what M11 changes

Today **one function — `memoize`** (`DeEnv/Code/CodeExecutor.cs:448`, keyed by
`fn-id + argument identities`) — does **three jobs at once**:
1. **Privacy** — leaf-promotion (`CodeExecutor.cs:471`): a *tag*-returning computation's reads
   ship (display); a *value*-returning one's reads stay private. No flags — the return type decides.
2. **Value memoization** — `where`/`orderBy`/value-fn results + dependency refs, for reactivity.
3. **Component identity / run-once** — a component (`refEditor`/`setTable`/…) is a fn whose body
   mints `var state = {draft}` and returns a render closure; the *cached closure* holds the state,
   so "run once" = "memo key unchanged."

**`__descs` / `__dictDescs`** (`DeEnv/Code/GenericUi.cs:311`, parked in the `internal` scope) exist
**solely** because job #3 rides argument identity: a component's memo key includes its descriptor
arg's object id, so a rebuilt descriptor → new id → new key → body re-runs → **draft resets**.
`__descs` freezes the descriptor's id. (Flagged as a GOTCHA in the M9 notes.)

**M11 = decouple #3 from #1+#2.** Give **components a positional/keyed identity** (a render-tree
slot), separate from the arg-keyed `memoize` that value-computations keep. Then a rebuilt descriptor
is irrelevant and **`__descs` dissolves**.

**Privacy survives the split.** Components are *display* (they return tags) → their direct reads
should ship anyway. The privacy-sensitive things are the **value-computations a component calls**
(`where`, `computeDiscount`…), which stay on `memoize` with leaf-promotion intact. So pulling
components off `memoize` does **not** endanger privacy.

**A free alignment.** The DOM reconciliation **already** keys rows positionally — `foreach` stamps
each row with the object id (sets) / item key (lists) (`DeEnv/Instance/codeExec.ts:817`) and the
client reuses keyed nodes (`DeEnv/Instance/ui.ts:77`). Solid's `<For>` (key-by-identity) / `<Index>`
(key-by-position) map onto that existing keying — **lift the DOM key up to be the component-instance
key**, don't invent a second scheme.

**The hard parts:**
- **A new conformance surface — the biggest piece.** The component lifecycle (setup-once,
  state-persists, reset-on-key) has **zero** conformance coverage today (tag results are excluded
  from the runner). Conformance cases for the new lifecycle are essential or the C#/TS twins drift.
- **`ExecContext` gains slot/position tracking** in both interpreters.
- **`memoBypass`** (event handlers) vs the setup/render split needs re-examination.

## 2. First slice — positional component-instance identity

Give components a **render-tree-positional identity** in both interpreters, decoupled from the
arg-keyed `memoize` — so a component runs its body **once per slot** and its local state survives
re-renders **even when its argument objects are rebuilt fresh every render**. Pure twin-interpreter
runtime change: no public-library surface, no `__descs` deletion yet.

> The existing `ComponentFormApp` calls `newForm()` with **no argument**, so it does NOT reproduce
> the bug (a no-arg call has a stable key). The first-slice fixture must pass a **rebuilt descriptor
> argument each render** — exactly what the generic UI does and what `__descs` patches.

**Proof (Gherkin)** — new `@milestone-11` scenario in `DeEnv.Tests/Features/SelfHostedUi.feature`,
beside the existing "A component holds creation state and resets after Create":

```gherkin
@milestone-11 @single-user
Scenario: A component's draft survives a render even when its argument is rebuilt fresh
  Given the rebuilt-descriptor component app is running
  When I open "/"
  And I fill the draft title with "Buy mi"
  And I toggle the unrelated flag       # parent re-renders, rebuilding the descriptor arg
  Then the draft title is still "Buy mi"
  When I click create
  Then the note list eventually shows "Buy mi"
  And the draft title is empty
```

Backed by a new fixture (`ComponentFormRebuiltDescDb` in `InstanceContext.cs`) where the component
takes a descriptor parameter and the call-site **rebuilds it as a fresh object literal every
render** (no `__descs`-style stable registry). Fails on `main` today (rebuilt descriptor → new id →
new memo key → body re-runs → draft lost); passes once components key on **slot, not argument**.
Exercised both SSR-then-hydrate and live, so both interpreters are on the hook.

**Touch list:**
- `DeEnv/Code/CodeExecutor.cs` — slot/position tracking on `ExecContext`; route **component** calls
  (a fn call returning a render closure) through a **slot-keyed** lookup distinct from
  `MemoKey`/`ArgKey` (split at `CallFunction:426`). Keep `Memoize`/`PromoteLeaves` exactly as-is for
  value-computations.
- `DeEnv/Instance/codeExec.ts` — the twin change (same slot tracking + component-vs-value split). The
  `foreach` keying (`:817`) is the model.
- `DeEnv/Code/conformance.json` + the runners (`DeEnv.Tests/Code/ConformanceTests.cs` /
  `TsConformanceTests.cs` / `runConformance` at `codeExec.ts:894`) — a **new lifecycle conformance
  case** + a small **re-render harness** (optional `setup` + `renders[]` against one retained
  context, `expect` on the last render). This is the must-do, not an afterthought.
- `DeEnv.Tests/Features/SelfHostedUi.feature` + `Steps/SelfHostedUiSteps.cs` +
  `TestSupport/InstanceContext.cs` — the scenario, 3 steps, the fixture.
- **Not touched:** `GenericUi.cs` (`__descs` stays — removal is follow-up 4), `ui.ts` (its keying is
  the alignment we lean on).

## 3. Follow-up slices (shortest-dependency-first)
1. **This slice** — positional slot identity for one call-site; survives a rebuilt arg; lifecycle
   conformance case.
2. **Lists of components by identity vs position** — extend slot identity through `foreach`/list
   iteration (by-identity ≈ `<For>`, by-index ≈ `<Index>`), reusing the existing `foreach` DOM key.
   Proven by: reorder/remove a row, surviving rows' state moves with identity, not position.
3. **The explicit per-call `key`** — caller-controlled "reset when X changes" (opt-in; common case
   stays zero-config).
4. **Dissolve `__descs` / `__dictDescs`** — rewrite `GenericUi.cs`'s synthesized components to pass
   plain rebuilt descriptors; delete the registries + their `internal`-scope plumbing. The generic
   UI now relies on slot identity; its existing `SelfHostedUi.feature` scenarios are the regression
   proof. **This is where the foundation pays off across every app.**
5. **The public component library + generic-UI-as-first-consumer** — the **feature half** of M11 (a
   designed public `ObjectForm`/`Field`/… API; the generic UI rewritten to compose it). Decomposes
   into several slices; plan separately when the foundation lands.

## 4. Open decisions (a call needed before/at build)
1. **Slot key basis** — render-tree **path** (parent slot + child position; robust under
   conditionals) vs **call-site id + occurrence counter** (simpler, closer to the existing memo-key
   string). *Planner recommends the path approach; user's call — a new identity model in both
   interpreters (ask-before-structural-changes).*
2. **Conformance case shape** — add a re-render protocol (`setup` + `renders[]`) to
   `conformance.json`'s contract, or a separate lifecycle mechanism?
3. **How a "component" is recognized at the split point** — **structural** (any fn call returning a
   render closure / tags is slot-keyed; zero-config, minimal-by-default) vs an **explicit marker**.
   *Planner recommends structural; confirm.*
4. **First-slice fixture scope** — a hand-authored component (off `GenericUi.cs`, the thin choice)
   vs proving it directly by dropping `__descs` in the generic UI (bundles follow-up 4, larger).
   *Planner scoped it to the hand-authored fixture.*

## 5. Risks / seams to honor
- **Twin lockstep is the whole game** — the new lifecycle has zero conformance coverage today; the
  re-render conformance case is load-bearing, not optional (else C#/TS drift). (CLAUDE.md rule 9.)
- **Privacy must not regress** — route *only component calls* off the arg-keyed memo; leave
  `where`/`orderBy`/value-fns (and their leaf-promotion) intact. Existing memo/privacy scenarios
  stay green.
- **Additive, don't restructure** — a parallel slot key *beside* `MemoKey`; don't touch
  `Memoize`/`ArgKey`/`Deps`/`LeafFrame`.
- **Lean on the existing DOM keying** (`ui.ts:77`, `codeExec.ts:817`), don't duplicate it.
- **Minimal-by-default** — no flag for the slot mechanism (it's the default); the only opt-in is the
  rare per-call `key` (follow-up 3).
- **`memoBypass`** (handlers, `codeExec.ts:213`) interaction is a known unknown — keep handlers
  as-is this slice; flag, don't fold a speculative fix in.
