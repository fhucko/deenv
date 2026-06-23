# Data-context refactor — build plan

**Goal:** extract data-state into a first-class client **ambient context** (overlay +
transaction), replacing drafts-living-in-component-memo-state. Fixes the two known
reactivity bugs structurally and gives staged / commit / discard + a dirty bit a real home.

## Why (the mess)
Drafts (`state.draft` in `GenericUi` ObjectForm/SetTable/RefEditor) are locals inside the
component's slot-keyed memo — editing state and component-lifecycle share one entry. That's
the root of bug-1 (the disabled `bug1-off` re-bind) and bug-2 (refetch drops the comp entry),
and why nav (`resetViewState`) silently nukes drafts. The server data path is comparatively
clean — this is a **client** refactor. (Full map: project memory.)

## Settled model
- **Context = full-access overlay over the store** — a sparse staged map over a parent,
  read-through; provided ambiently; not bound to one object. Root context is
  framework-provided (the live store, parentless). Sub-contexts via `ctx.new()` (child of the
  receiver). Stack `store ← root ← page/form ← …`; commit flushes to parent, discard drops.
- **Ambient = dynamic scoping.** `ambient name = value` provides/overrides for the rest of the
  providing function's extent (its callees + rest-of-scope), via bindings on **owner-tree
  nodes** + nearest-up resolution. Provide is a language construct (can't be delegated to a
  function — it pops on return). Consume is implicit (read the name / call ctx methods).
  Closures capture their owner-tree position. **No global slot** (the render tree isn't linear).
- **Surface:** `ambient ctx = ctx.new()` (provide a sub-context); `ctx.commit()` /
  `ctx.discard()` / `ctx.dirty` (built-in methods, same mechanism as `set.add`); `sys.new(type)`
  unchanged (objects). Writes route through the nearest ctx (root = live = persist; staging ctx
  = stage). Dirty is **local** (own overlay only; commit moves data up).
- **No dispose** (GC + reference-drop). **Run-once** provide (context not re-minted per render).

## Guardrails
- Gherkin/conformance **before** code, each slice. Both twins (`CodeExecutor.cs` +
  `codeExec.ts`) stay in lockstep via `conformance.json`.
- **Storage seam untouched:** commit replays the staged map through the existing per-field WS
  ops. No bulk/atomic path yet.
- Privacy stays deferred; keep the transfer seam clean so it re-layers later.

## Slices

### 1. Ambient facility (substrate)
- **Syntax:** `ambient name = value` (rest-of-scope + indented block, no colon) + bare-name
  consume. `CodeParse.cs`, `CodePrint.cs`, `CodeAst.cs`.
- **Runtime (both twins):** owner-tree nodes carry a `name → value` binding map; a consumer
  resolves by walking up from its node; provide adds a binding at the current node; closures
  capture their node. Extend the existing slot-path/owner structure (`ExecValues.cs`
  `ExecContext`, `codeExec.ts` `slotPath`).
- **Tests:** conformance — provide→consume-below, shadow, walk-up, closure-captures-position.
  Gherkin — survives re-render + closure resolves at click (client-only).
- Proven on a **plain ambient var**. No data context yet.

### 2. Context + overlay + interception (first consumer)
- **Value:** an overlay (parent ref + sparse `(objId, prop) → value` staged map, read-through).
  `ExecValues.cs`.
- **Root:** framework-provides the root context (live store as base) in scope setup
  (`SsrRenderer.cs`).
- **Methods:** `ctx.new()`, `ctx.commit()`, `ctx.discard()`, `ctx.dirty` — add a
  context-methods dispatch alongside `CollectionMethods` in both twins.
- **Interception:** prop read resolves through the nearest ctx overlay; prop write/assignment
  stages in the nearest ctx (root = live → existing persist path). `CodeExecutor.cs`
  (`RecordPropAccess` + assignment), `codeExec.ts` (prop read + `executeAssignment` /
  `persistFieldEdit`).
- **Commit:** walk the staged map → emit existing per-field WS ops (or merge into parent
  overlay if nested). `sys.new` untouched.
- **Tests:** conformance — overlay read / write-stages-not-persists / commit-to-parent / discard.

### 3. Rewire ObjectForm (behavior + bug fixes land)
- `GenericUi.cs` — ObjectForm = `ambient ctx = ctx.new()` + fields (bind `obj`, stage via
  interception) + `if ctx.dirty: Save(ctx.commit) / Discard(ctx.discard)`. Drop
  `var state = {draft: sys.new}` + `setFields`.
- **Delete dead code:** `codeExec.ts` `bug1-off` guard, `argsKey` write, `rebindComponentArgs`,
  `objectArgKey`.
- **Tests (Gherkin):** stage-not-persisted, Save-commits, Discard-reverts, nav-discards,
  reopen-empty.

### 4. Follow-ups
CreateForm/SetTable/RefEditor onto ctx (`sys.new` + `ctx.commit`) · nested commit-to-parent ·
Gherkin cleanup pass (prune obsolete, rewrite internals-coupled scenarios to behavior level).

## Deferred (separate, later)
Conflict-resolver (commit-time CAS; temp first-wins) · atomic batch commit · graph-save
(recursive create + subtree id-remap) · privacy re-layer · per-page vs per-form context scope ·
declared ambient consumption (static checker) · detached/parentless user contexts.
