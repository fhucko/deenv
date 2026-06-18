# M11 — Reactivity foundation: analysis + first-slice plan

**Status: SLICES 1–2 LANDED 2026-06-18 (suite 310).** M11 is SolidJS-style reactive components +
the public component library (the UI middle-ground); this doc is the **foundation half** (the
reactivity refactor) and its **first slice**. See DECISIONS "UI middle-ground — one public
component library + SolidJS-style reactivity" and ROADMAP M11. *Grounded by codebase-navigator +
milestone-planner, 2026-06-17; built 2026-06-18.*

**What shipped (and two decisions that evolved during the build):**
- **Component recognition = PURE NAME-RESOLUTION, not capitalization** (user decision 2026-06-18).
  A tag is a component iff its name resolves to a function in scope (`<div>` element; `<noteForm>`
  component). Any function — top-level OR locally defined in another component's constructor and
  used in its render (proven by conformance case B). Still "explicit component-as-tag" (the tag is
  the boundary), just split element-vs-component by scope lookup, not casing.
- **Run-once-across-re-renders is CLIENT-only, so it's proven by the Gherkin scenario, not
  conformance.** C#'s `Memoize` (`CodeExecutor.cs:448`) is write-only (the server renders once);
  only the TS client cache-hits on re-render. The conformance cases (must agree on both twins,
  single render) therefore prove the deterministic shared core — recognition, by-name binding,
  splice, local-component capture, and sibling slot-key uniqueness (a collision diverges the twins).
- Slot key = `"comp:"` + the static-AST-child-index path; setup + view both go through the
  **existing** `Memoize` (untouched). `foreach`-row disambiguation is **follow-up 2** (flagged with
  a `HAZARD` comment in `ExecuteComponent`, both twins — follow-up 4 must honor it). The
  name-resolution footgun hit once: renamed the designer's `fn nav`→`navBar` (it returned `<nav>`).
- **Slice 2 (lists/keys) landed too:** `ExecuteTagForEach`/`executeTagForEach` push a per-row slot
  segment = the member's identity (object id, else item key — the same key the DOM reconciler uses),
  so a component inside a `foreach` gets a distinct, identity-stable slot per row (independent state
  that follows the object across reorder/remove). Proven by a foreach-row conformance case + the
  "Per-row component state … across reorder" scenario.
- **Follow-up 4 is meatier than this plan said — `__descs` does TWO jobs:** (1) reference-stability
  (slices 1–2 fix this) AND (2) a **cycle-free cross-type descriptor registry** (a ref/set prop
  carries the OTHER type's NAME; the component resolves that type's descriptor via
  `field(__descs, p.target)` — `GenericUi.cs:414-416`). Slot identity removes (1) but not (2), so
  "delete `__descs`" needs a replacement for the cross-type data, entangled with follow-up 5
  (schema-as-data reflection). Clean split: **4a** = tag-invoke the generic UI components now (relies
  on slot identity; `__descs` stays as a now-just-data registry), then **4b/5** = schema reflection.
- **Still NOT done:** an explicit per-call `key` (follow-up 3); tag-invoking the generic UI +
  `__descs` removal (follow-up 4); the public library (follow-up 5). Implementation memory:
  [[project_m11_reactive_components]].

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
- `DeEnv/Code/CodeExecutor.cs` — slot/position tracking on `ExecContext`; route **tag-invoked
  components** (recognized explicitly — per the decisions below, not by result-inspection) through a
  **slot-keyed** lookup distinct from `MemoKey`/`ArgKey` (the split moves to the tag/component
  invocation point). Keep `Memoize`/`PromoteLeaves` exactly as-is for value-computations.
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

## 4. Decisions (locked 2026-06-17 — Solid-aligned)

Made with the user, via the React/Solid comparison. (Topic-labeled, not numbered, to avoid drift.)

- **Slot key basis → render-tree PATH** (parent slot + child position), not a call-site counter.
  React/Solid's default component identity; robust when conditionals add/remove siblings; extends
  cleanly to the `<For>`/`<Index>`/`key` follow-ups (key augments the path at list boundaries);
  aligns with the existing positional DOM keying. *Both interpreters must compute the identical path
  — conformance-critical.*
- **Component recognition → EXPLICIT (component-as-tag), not structural result-inspection.** Solid/
  React never infer a component from its result — `<Foo/>` is a boundary, a plain `foo()` is not.
  Result-inspection ("did this return tags?") is fragile (conditional/mixed returns) and
  twin-drift-prone; explicit is unambiguous, AST-visible (better for conformance), and aligns with
  the public library (components-as-tags) and with the path decision (a tag's tree position *is* its
  slot path). **Knock-on:** slice 1 introduces the explicit tag-invoked-component boundary, and the
  fixture component is **tag-invoked** (not the `foo()()` pattern) — a small, deliberate expansion
  that builds the right boundary from the start rather than building structural-recognition to later
  rip out. *(The one place we diverge from the planner's recommendation.)*
- **Conformance shape → UNIFIED `setup + renders[]`** (a render sequence against one retained
  context, `expect` on the last render), not a separate mechanism. The new lifecycle is the most
  drift-prone behavior (zero coverage today), so it MUST live in the one suite both twins run —
  exiling it defeats conformance's whole purpose. Back-compatible (single-expr cases unchanged); the
  shared re-render protocol IS the lockstep. *Both runners spec the protocol identically.*
- **First-slice fixture → HAND-AUTHORED thin** (off `GenericUi.cs`; no `__descs` removal), the
  component **tag-invoked** (per the recognition decision). Isolates the mechanism from the
  `__descs`-removal migration (follow-up 4); a failure bisects cleanly (mechanism vs migration). The
  generic UI is proven in follow-up 4, with the existing `SelfHostedUi` scenarios as regression.

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
