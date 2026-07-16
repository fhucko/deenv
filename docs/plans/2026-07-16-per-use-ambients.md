# Implementation Plan

## Goal

Per MetaUse configuration, authors can declare ambient fakes (`currentUser`, `path`, and any other free-form names) that bind as real read-only sandbox/canvas scope vars so designer previews evaluate instead of erroring — while missing ambients still surface the real `Variable … not found` error (canvas-never-lies).

## Product defaults (settled — do not re-open)

| Decision | Default |
|----------|---------|
| Fakeable names | Free-form MetaAttr names: `currentUser`, `path`, and anything the author writes |
| `status` | **Not** a product ambient. Do **not** suggest it in UX copy. Binder **skips** name `status` (leave unbound → honest miss). Optional soft hint if author types `status` |
| Missing ambient | Unbound → real error / chip. Never invent a default |
| Binding mechanism | **Scope / body-binding items** (read-only), mirroring SSR `system.Items` and workbench `db` — **not** `ExecContext.ambient` frames, **not** `ctx.props.ambients` |
| `evalContext.ambients` bag | Leave reserved-empty in `BuildEvalContext` |
| currentUser fake shape | Expression-driven; recommend object literals e.g. `{ role: "Admin", name: "Ada" }`. Not a fixed DTO |
| path fake shape | Text expression, e.g. `"/items/1"` |
| Arg vs ambient null | Missing arg AST → null param. Missing ambient → unbound → error. Do not unify |
| Slice size | **One small slice** (schema+UI+collector+two bind sites+pins). Ordered worker steps below |

## Gherkin first

### Keep unchanged (isolation / never-lies)

| File | Scenario | Why keep |
|------|----------|----------|
| `DeEnv.Tests/Features/DesignerComponents.feature` | `A component reading an unseeded ambient shows the real error in its configuration card, and the page stays alive` (~447) | No ambient rows authored → still `"Variable currentUser not found"`; proves page stays alive + second config add |
| `DeEnv.Tests/Features/DesignerLibrary.feature` | `A throwing instance handler shows the real error without disabling the page or its sibling instance` (~531) | Thrower has **no** ambient fake → handler still errors; sibling Counter still works |

Update **comments only** on those scenarios: unseeded ambient is still the isolation proof; fakes are opt-in via MetaUse.ambients (not “ambients not implemented”).

### New scenarios (happy path)

Add under `DesignerComponents.feature` (same `@m12 @single-user` family; **not** forced into `@m12-live-isolation` unless isolation is the claim):

1. **`A configuration ambient fake for currentUser makes the live instance render instead of erroring`**
   - Convert ambient-reading (or field-reading) component; add configuration; **do not** assert error.
   - Add ambient row: name `currentUser`, value `{ role: "Admin" }` (see authoring below).
   - Assert live instance shows expected text (e.g. `"Admin"` from `currentUser.role`).
   - Optionally assert a second config **without** ambient still errors (same page) — strong isolation+fake combo; keep short.

2. **`Editing a configuration ambient remounts that live instance`** (optional if (1) already remounts on set-value via refresh+reset steps)
   - Set ambient, assert value A; change ambient value; assert value B on **that** card only.

3. **Static canvas (secondary pin)** — only if expandFn path ships in same slice:
   - Before or alongside live mount, assert configuration preview content is not an expr-chip for the ambient-backed field when fakes are authored.
   - If static path is deferred to a micro-follow, pin live only and note residual risk (pre-mount SSR body may still chip).

**Do not** flip existing error scenarios to success without adding ambient rows.

### Fixture note for happy path

Existing isolation fixture (`AmbientReadingComponentConvertibleRender`) is:

```text
fn Broken()
    return <div>
        currentUser
```

Printing a whole object is a poor assertion target. Prefer a **new** convertible fixture for the happy path, e.g.:

```text
fn Greeter()
    return <div class="who">
        currentUser.role
```

Isolation fixture stays as-is (error string only cares that the symbol is missing).

### Steps to add (mirror arg steps)

In `DeEnv.Tests/Steps/DesignerSteps.Steps.cs` next to configuration-arg steps (~1564+):

| Step | Behavior |
|------|----------|
| `When I add an ambient to configuration {int}` | Click `button.add-ambient` inside that `.use-row` (scoped under `.use-ambients`) |
| `When I set configuration {int}'s ambient {int} name to {string}` | Fill ambient name input; wait store MetaAttr name |
| `When I set configuration {int}'s ambient {int} value to {string}` | Fill value; wait store; click `refresh-eval`; click instance Reset if present (same discipline as arg value step) |

**Critical:** scope ambient locators under `.use-ambients` and **tighten existing arg steps** to `.use-args` so shared `attrRow` classes (`.node-attr-name` / `.node-attr-value`) do not cross-index.

Update step comments that say “per-use ambients are a LATER rung” (~1748–1752, ~1871–1877).

## Tasks

### 1. Schema + mint + Configurations UI (`app.deenv`)

- File: `DeEnv/instances/1/app.deenv`
- Changes:
  1. On `MetaUse` (schema ~49–52 and any mirrored type block ~110): add `ambients set of MetaAttr`.
  2. `addUse`: mint `{ name: "", args: [], ambients: [], order: ... }`.
  3. Add `addUseAmbient(u)` twin of `addUseArg`: `u.ambients.add({ name: "", value: "", order: orderForAppend(u.ambients) })`.
  4. Optional soft hints only (not projection refusals):
     - empty ambient name → no hard block (same as empty arg name during edit), or light “name required” if product wants parity with `useNameHint`.
     - `a.name == "status"` → soft `"not fakeable (save lifecycle)"` via `useAmbientNameHint`.
  5. Configurations card (~823–854):
     - Wrap args foreach + `+ arg` in `<div class="use-args">`.
     - Second block `<div class="use-ambients">`: foreach `u.ambients.orderBy(...)` → `attrRow(u.ambients, a)` + optional hint; `button.add-ambient` → `addUseAmbient(u)`.
     - Caption tweak: mention ambients (e.g. seed data **and** ambient fakes).
  6. Static synth node (same card): pass ambients for expandFn binding:
     ```text
     sys.renderTree({ kind: "", tag: f.name, expr: "", attrs: u.args, ambients: u.ambients, children: [] }, sys.evalContext(...), design)
     ```
     Real MetaNodes never carry `ambients`; only this transient use-preview object does.
- Acceptance: designer loads; add configuration shows `+ arg` and `+ ambient`; mint creates empty ambients set without runtime field errors.

### 2. Ship ambient expression sources into `ctx.exprs`

- File: `DeEnv/Designer/SchemaBridge.cs`
- Changes:
  - Extend `CollectUseArgSources` **or** add `CollectUseAmbientSources` that walks `use.Fields["ambients"]` MetaAttr `value` texts (same `TextField` / non-empty rule as args).
  - Call from `RenderExprSources` next to `CollectUseArgSources` (~602–603).
  - No TS twin (collector is server-only; documented in `component-workbench.md`).
- Unit pin: extend `DeEnv.Tests/Code/DesignerSourceTests.cs` — twin of `RenderExprSources_collects_a_source_reachable_only_via_a_fn_use_arg` for an ambient-only source (use with empty args, ambient value `" { role: \"Admin\" } "` or `"db.x"`).
- Acceptance: ambient value sources appear in `RenderExprSources` output without being present in body/render trees.

### 3. Workbench sandbox binding + remount signature (primary bind site)

- File: `DeEnv/Instance/workbench.ts`
- Changes:
  1. **`bindUseAmbients(use, ctx, evalScope)`** (new helper near `renderWorkbenchInstance`):
     - Read `use.props["ambients"]` array; missing/null → no-op (old rows / unit tests).
     - Sort by `order` (same idiom as args).
     - For each row: skip empty name; **skip name `status`**; skip empty value text (do not bind → miss stays honest).
     - `lookupCtxAst` / `evalCtxExpr(valueText, ctx)` — **no** design-var bindings map (same isolation as arg eval: db + ctx.fns).
     - Bind only when eval returns non-null **or** when AST exists and result is legitimate null?  
       **Default:** if AST missing → leave unbound (never bind guessed null). If AST present and eval returns `ExecNull` (author wrote `null`) → bind null. If eval throws/miss → leave unbound (canvas-never-lies).  
       Implementation detail: `evalCtxExpr` returns `null` on miss/throw **and** cannot distinguish ExecNull success — check `lookupCtxAst` first; if AST present, run execute in a tiny local try (or extend evalCtxExpr usage carefully). Prefer: if no AST → skip; if AST present → evaluate with same isolation as `evalCtxExpr` but treat successful `ExecNull` as bindable (copy evalCtxExpr body or add `evalCtxExprAllowNull` only if needed). Minimal path: reuse `evalCtxExpr`; document that literal `null` ambient may not bind (acceptable residual) unless implementer tightens.
     - `evalScope.items[name] = { value, isReadOnly: true }`.
  2. Call **after** `db` / `bindFnMap` / `bindCtxFns`, **before** `executeComponentValue` (~355–369). Params still bind on the call child scope and shadow same-named ambients (live system-scope semantics).
  3. **Remount signature:** rename or extend `useArgsSignature` → include ambient rows with the **same** length-prefix encoding (`n…:v…:a0|1`). Call sites: mount/reset/remount (~512, ~542). Prefer one function `useConfigSignature(use, ctx)` = args sig + `"#"` + ambients sig to avoid accidental collisions.
  4. Update comments at `instanceHandlerContext` / ambient gap (~562–567): ambients bind at render into component scope; handlers close over setup scope; remount on ambient edit refreshes fakes. Keep `ambient: null` on `ExecContext`.
- Acceptance: unit-level or browser: authored `currentUser` fake → no Variable error; remove ambient / empty config → error returns; ambient edit remounts.

### 4. Static canvas binding via expandFn (secondary bind site; twin required)

- Files:
  - `DeEnv/Instance/codeExec.ts` — `expandFn` (~1427–1455)
  - `DeEnv/Code/CodeExecutor.cs` — `ExpandFn` (~1313–1338) **twin**
- Changes:
  - At start of expandFn bodyBindings construction (before params), if `invocationNode` has optional set prop `ambients` (use `orderedMembersOptional` / C# twin):
    - For each ambient MetaAttr: same rules as workbench (skip empty name, skip `status`, skip empty value, eval via `evalCtxExpr` / `EvaluateCtxExpr` with **callerBindings** or empty — prefer **no** callerBindings so fakes only see db+fns, matching workbench).
    - Bind into `bodyBindings` **first** so params can shadow (system-under-call semantics).
  - Do **not** leak design.vars into components; only this optional invocation-level ambients set.
  - Real tree MetaNodes lack `ambients` → optional reader → no behavior change for main canvas.
- Acceptance: use-preview synth with ambients evaluates ambient-backed body expressions without chips when AST+value present; main canvas conformance suite unchanged.
- **If twin churn must be deferred:** ship workbench-only in v1 of this slice and leave static pre-mount body as secondary residual (workbench replaces body on mount; live pins still green). Prefer shipping both in one slice because the expandFn change is small and the twin is mechanical.

### 5. Tests / pin flips

- Files:
  - `DeEnv.Tests/Features/DesignerComponents.feature` — keep error scenario; add happy-path scenario(s); comment updates
  - `DeEnv.Tests/Features/DesignerLibrary.feature` — keep thrower error scenario; comment updates only
  - `DeEnv.Tests/Steps/DesignerSteps.Steps.cs` — ambient steps; scope arg steps under `.use-args`; new Greeter fixture if needed; comment updates
  - `DeEnv.Tests/Code/DesignerSourceTests.cs` — collector ambient source pin
  - `DeEnv.Tests/Code/WorkbenchDeepCopyTests.cs` — only if synthetic `use` objects must tolerate missing `ambients` (should already via null-safe read)
- Acceptance: existing isolation tags green; new ambient happy path green.

### 6. Docs touch (minimal)

- File: `docs/plans/m12-remaining.md` §3 — mark per-use ambients **done** (or in-progress) after ship; point to this plan.
- Optional: short note in `docs/plans/component-workbench.md` fidelity boundary section.

## How currentUser / path fakes are authored

| Ambient | MetaAttr.name | MetaAttr.value (expression source text) | Typical component read |
|---------|---------------|------------------------------------------|------------------------|
| User principal | `currentUser` | `{ role: "Admin", name: "Ada" }` | `currentUser.role`, `currentUser.name` |
| Anonymous | `currentUser` | `null` (if null-bind supported) or omit row for miss | — |
| URL path | `path` | `"/designs"` or `"/items/1"` | `path`, `sys.segment(path, n)` if available |
| Custom | any free name | any expression evaluable under db+fns | author-defined |

**Not** a fixed User DTO: live SSR uses `LoadPrincipal` scalar-only object; designer fakes are ordinary expr results. Residual fidelity gap is acceptable and documented in Risks.

Placeholder in ambient value input: reuse attrRow’s `"\"value\" or expression"`.

## Files to Modify

| File | Change |
|------|--------|
| `DeEnv/instances/1/app.deenv` | MetaUse.ambients; addUse/addUseAmbient; use-args/use-ambients UI; synth node ambients field |
| `DeEnv/Designer/SchemaBridge.cs` | Collect ambient value sources in RenderExprSources |
| `DeEnv/Instance/workbench.ts` | bindUseAmbients; signature includes ambients |
| `DeEnv/Instance/codeExec.ts` | expandFn optional ambients → bodyBindings |
| `DeEnv/Code/CodeExecutor.cs` | ExpandFn twin |
| `DeEnv.Tests/Features/DesignerComponents.feature` | Keep error pin; add happy path |
| `DeEnv.Tests/Features/DesignerLibrary.feature` | Comment-only; keep error pin |
| `DeEnv.Tests/Steps/DesignerSteps.Steps.cs` | Ambient steps; scope args; fixtures/comments |
| `DeEnv.Tests/Code/DesignerSourceTests.cs` | Collector ambient pin |
| `docs/plans/m12-remaining.md` | Status flip when done |

## New Files

- None required for product code.
- Plan artifact: `docs/plans/2026-07-16-per-use-ambients.md` (copy of this plan for the repo plans tree).

## Dependencies

```
Task 1 (schema+UI)
   └─► Task 2 (collector)     // can parallelize with 1 after field name fixed
         └─► Task 3 (workbench bind)  // needs exprs for non-literal fakes
         └─► Task 4 (static expandFn) // needs exprs + synth ambients field from Task 1
               └─► Task 5 (Gherkin + steps)
                     └─► Task 6 (docs status)
```

Gherkin scenarios and step **signatures** can be written first (red), then implement 1→4 until green.

## Non-goals

- Filling or reading `sys.evalContext` / `BuildEvalContext` product bag `ambients` / `params`
- Language `ambient name = value` frames for currentUser/path
- Faking live SSR session principal globally
- Faking `status`, `anonymousLockedOut`, `accessActive`, `canManageUsers`, `sys`, `isGeneric` as first-class UX
- Projection / import / print of MetaUse into app document (uses stay designer-only)
- Fn fingerprint / staleness changes
- W2 state-changes list
- Soft-defaulting missing ambients to null or anonymous user
- Drag-and-drop ambient reorder beyond existing attrRow ▲▼

## Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| Arg/ambient DOM index collision | **High** (breaks existing arg steps) | Separate `.use-args` / `.use-ambients` wrappers; scope steps |
| Ambient edit without signature update | High | Extend remount signature with ambient rows + AST flag |
| Binding from `ctx.props.ambients` by mistake | High | Only read MetaUse row; leave evalContext bag empty |
| `evalCtxExpr` null-vs-miss | Medium | Prefer AST-present path; document literal-null residual if unfixed |
| Static without expandFn change | Medium | Pre-mount chips until workbench mount; pins use live instance |
| Twin expandFn drift | Medium | Same-day C# ExpandFn change + existing canvas conformance |
| currentUser object vs text child | Low | Happy-path fixture uses `currentUser.role` |
| Name collision ambient vs `db` / lib fn | Low | Bind after lib/fns; document last-writer on evalScope for ambients |
| Handler path | Low | Closures capture mount scope; ambient edit remounts via signature |
| Client vs server error string | Low | Pins use client form `Variable currentUser not found` (no quotes) |

## Residual questions (non-blocking defaults)

None block build. Defaults if implementer hits edge cases:

1. **Literal `null` ambient value** — best-effort bind; if `evalCtxExpr` cannot distinguish miss, skip null-literal support in v1.
2. **status row authored** — skip bind + optional soft hint (not hard delete).
3. **Static pin** — prefer ship expandFn twin; if timeboxed, live-only pins suffice for M12 close with noted residual.

## Verification commands

PowerShell from repo root (`C:/Users/Filip/Documents/deenv`). Prefer Release for browser suite consistency with project habit; Debug OK for unit.

```powershell
# Collector unit
dotnet test DeEnv.Tests -c Release -- --treenode-filter "/*/*/*RenderExprSources*"

# Workbench deep-copy / unit (smoke after workbench change)
dotnet test DeEnv.Tests -c Release -- --treenode-filter "/*/*/*Workbench*"

# Isolation pins (must stay green)
dotnet test DeEnv.Tests -c Release -- --treenode-filter "/*/*/*unseeded ambient*"
dotnet test DeEnv.Tests -c Release -- --treenode-filter "/*/*/*throwing instance handler*"

# New happy path (name exact once scenario title lands)
dotnet test DeEnv.Tests -c Release -- --treenode-filter "/*/*/*ambient fake*"

# Broader m12 designer smoke if needed (heavier)
dotnet test DeEnv.Tests -c Release -- --treenode-filter "/*/*/*/*"
# Prefer listing: dotnet test DeEnv.Tests --list-tests | Select-String ambient
```

Never `dotnet test --filter` (VSTest). Use `-- --treenode-filter` with TUnit.

## Recommended agent execution shape

**One build agent, ordered steps (no parallel writers on same files):**

1. **Gherkin + steps (red)** — new scenario(s), ambient steps, scope arg steps under `.use-args`, fixture for `currentUser.role`.
2. **Schema/UI** — `app.deenv` MetaUse.ambients, mint, UI wrappers, synth `ambients: u.ambients`.
3. **Collector** — SchemaBridge + DesignerSourceTests unit.
4. **Workbench bind + signature** — `workbench.ts`.
5. **Static expandFn + C# twin** — `codeExec.ts` + `CodeExecutor.cs`.
6. **Run filters above until green**; update `m12-remaining.md` §3 status.
7. **Review pass** — confirm no `ctx.props.ambients` product use; no AmbientFrame push; isolation scenarios still error without fakes.

Optional split if two agents: (A) schema/UI/collector/tests scaffolding, (B) workbench+expandFn bind — merge order A then B; only B owns runtime bind semantics.

## Architecture recap (implementer checklist)

```
MetaUse.ambients: MetaAttr[]  ──► CollectUseAmbientSources ──► ctx.exprs
       │
       ├─ static: synth { attrs: args, ambients: u.ambients }
       │            └─ expandFn: bodyBindings ← ambients then params then vars
       │
       └─ live: workbench.renderWorkbenchInstance
                  evalScope.items ← ambients (RO) after db/lib/fns
                  args → tag attributes → executeComponentValue
                  ExecContext.ambient stays null
```

## Acceptance Contract notes for build agent

- Do not claim done without green treenode runs on isolation + happy path.
- Do not flip isolation scenarios to success without authored ambient rows.
- Prefer minimal diff; no drive-by refactors in codeExec beyond expandFn ambients.
