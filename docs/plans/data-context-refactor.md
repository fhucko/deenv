# Data-context refactor — build plan (revised 2026-06-23)

**Goal:** extract data-state into a first-class client **ambient context** (overlay +
transaction), replacing drafts-living-in-component-memo-state. Fixes the two known reactivity
bugs structurally and gives staged / commit / discard + a dirty bit a real home.

## Status

**COMPLETE — the generic ObjectForm now runs on `ctx`; 430/430, both twins.** The staging overlay
is live in the real app: the framework provides a live root `ctx`, the form opens a staging child,
edits stage (stored value untouched), Save commits / Discard reverts. Create-form drafts isolate.

| | commit | what |
|---|---|---|
| plan | `aa5d11c` | this doc |
| slice 1 | `979a32e` | `ambient name = value` dynamic-scope vars (tree-positional resolve, save/restore per block) |
| slice 2 | `ed5c747` | `ExecCtx` overlay — `ctx.new`/`commit`/`discard`/`dirty` + obj-prop read/write interception |
| slice 3a | `1f59e95` | closures capture their ambient → deferred onClick handlers resolve `ctx` from birthplace |
| parser | `e56f7e7` | parse + print `ambient name = value` (GenericUi.cs is parsed Code text) |
| 3b | `cebdb3c`+ | framework wiring — live root `ctx` provided at the render + onClick entries (SSR + client) |
| 3c | this commit | ObjectForm on `ctx`: drop drafts/`setFields`, `ambient ctx = ctx.new(autosave)`, `save`=commit / `discard`=discard; `setValue` + `obj.prop` writes stage; **create-form drafts isolate (id<0→live)** |

## Settled surface
- `ambient ctx = ctx.new()` — provide a staging sub-context (run-once, in a component body).
- `ctx.commit()` / `ctx.discard()` / `ctx.dirty` — built-in methods (the `set.add` mechanism).
- `sys.new(type)` — unchanged, makes objects. The root context is framework-provided + **live**
  (writes persist); a `ctx.new()` child stages. Interception is a **no-op when no `ctx`** is
  provided (the real app today) → zero regression.

## The coupling that reshaped the rest (found 2026-06-23)

A staging `ctx` is **ambient**, so everything rendered inside the form inherits it — including the
nested `RefEditor`/`SetTable`, whose own `sys.new` create-drafts are edited via `<Input
obj={state.draft}>` (an `obj.prop` write → stages in the **parent** form's ctx). So the create-form
drafts get entangled in the edit transaction. ⇒ **the ObjectForm rewrite is NOT separable from the
create-forms** — they're one chunk. Each create-form must open its **own** `ctx` (nested,
isolating its draft) or go live. The old slice-3/slice-4 split collapses.

## What landed (3b + 3c)

### 3b — framework wiring (safe prep, both no-ops until 3c uses them; land green first)
- **Root provision:** `SsrRenderer.ExecuteRender` (server) and the client render entry set
  `context.Ambient = live root ctx`. No-op (root is live; existing draft-based forms still write
  to props).
- **Capture-restore reaches the reactive paths:** verify/wire that the **component-render**
  invocation (`executeComponentValue`) and **onClick** handler invocation (`ui.ts`) route through
  the `RunBody`/`runBody` capture-restore (so a component's `render`/`save`/`discard` resolve their
  captured `ctx`). No-op until something captures a non-null ambient.
- Verify: build + suite 430-green after each (pure prep, no behaviour change).

### 3c — the form rewrite (the behaviour change; the browser suite is the test)
- **ObjectForm:** drop `var state = {draft: sys.new}` + `setFields`. `ambient ctx =` a staging
  child (default) or the live root (`autosave==true`); Fields bind `obj` directly;
  `save`=`ctx.commit()`, `discard`=`ctx.discard()`.
- **Field two-way binding:** `sys.field`'s `setValue` must stage through the nearest ctx (slice 2
  only intercepted the assignment path, not `setValue`).
- **Create-forms isolate:** a nested `RefEditor`/`SetTable`/`DictTable` create-draft (`sys.new`,
  negative id) writes **live** — the `id>0` staging gate excludes it — so its `<Input>` edits never
  stage in the enclosing form's ctx; Save adds the now-complete draft (`set.add`/`setRef`), Cancel
  drops it. (The "each opens its own `ctx.new()`" idea was superseded by the simpler gate — see
  "How 3c converged".)
- **Gherkin (done, all green):** edit stages (stored value unchanged until Save), Save persists,
  Discard reverts, **nav discards** (the form's ctx drops on unmount — proven via an SPA round-trip),
  create-form isolation. Stale `setFields`/`state.draft` scenario comments rewritten to behavior-level.

## Guardrails
- Tests-first per slice; both twins in lockstep via `conformance.json`; storage seam untouched
  (commit replays through today's per-field WS ops).
- Privacy stays deferred; keep the transfer/fetch seam clean so it re-layers later.

## Deferred (separate, later)
Conflict-resolver (commit-time CAS) · graph-save (recursive create + subtree
id-remap) · privacy re-layer · per-page vs per-form ctx (settled per-form for now) · declared
ambient consumption (static checker) · the indented-block `ambient` sub-scope form — **colonless** (`ambient x = v` then a deeper-indented block, like `fn`/`if`/`foreach`; provides `x` only within the block). The block MODE is kept; only the colon punctuation was dropped.

## How 3c converged (2026-06-23)

Two bugs the first attempt hit, both fixed:
1. **A `formContext` helper shared its ctx across slots** — `CallFunction` memoizes every call by
   `(fn-id, args)`, so a helper returning a fresh staging ctx got cached and reused across renders /
   slots. Fixed by making autosave an **argument to `ctx.new(autosave)`** (a built-in ctx-method, not
   a memoized fn call): the form opens its ctx inline in its own body, no helper.
2. **Create-form drafts entangled in the parent form's transaction** (the coupling above). Fixed with
   the **id<0→live** gate: a transient `sys.new` draft (negative id) writes live, so its `<Input>`
   edits never stage in the enclosing ObjectForm's ctx. Conformance overlay cases then stage onto an
   injected **positive-id** object (the gate excludes id<0 literals).

The `id>0` gate covers all three write sites (`obj.prop` assign, `setValue`/`field`, C# assign);
discard also invalidates the reverted props so the client re-renders them.

## Loose ends (post-review, 2026-06-23)

Two reviewers (architecture + ui) cleared the slice; their notes:
- **`sys.setFields` now has zero Code consumers** — ObjectForm was its only one. Kept as a public
  builtin (removing one is a structural surface change → ask first); its docs no longer claim
  ObjectForm uses it. A clean deletion candidate if the user wants the surface trimmed.
- **`ctx.dirty` is built + conformance-tested but unconsumed** — the form shows Save/Discard
  unconditionally. Intentional foundation for the deferred unsaved-indicator / dirty-gated Save; flag
  it if it stays naked much longer.
- **The `id>0` gate** is a proxy for "real identity in the live store"; a just-added object pending
  its neg→real remap is also id<0 but has no route, so it can't reach a staging form today. The gate
  comment names the assumption to revisit if that changes.
- Per-field commit was made atomic by the atomic-commit arc (`docs/plans/atomic-commit.md`,
  2026-06-27): a commit now ships one all-or-none `commit` WS op over the whole changeset, replacing
  the N independent per-field WS ops described here. Recursive multi-level graph-save (nested create +
  subtree id-remap) remains deferred.
