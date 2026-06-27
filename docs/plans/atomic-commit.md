# Atomic commit — the ctx's staged set (edits + creates) as one transaction

**Status: BUILDING — Step A (atomic edits) LANDED `ec076eb`/`932430d` (suite 550), the internal foundation (not yet "the feature"); Step B (transient-create staging, the twin change) next. Invariants (the validity "teeth") deferred — see end.** The destination
is a commit where **the `ctx` is the true atomic unit**: everything staged in it — field edits, *and* new
objects, *and* the relations linking them — applies as one all-or-nothing transaction. Off the slice-2 (form
Save feedback) thread; pairs with `docs/plans/virtual-password-field.md` (the reactive status that renders the
outcome) and memory `project_persistence_modes`.

## Problem — two faces of one incoherence

`ctx.commit()` is not the atomic unit, in two ways:
1. **Edits fan out.** A commit sends N independent `objectPropChange` messages, each applied/acked on its own —
   one denied write leaves the others persisted (partial save). Order-dependent, too (each handler re-reads
   the object, so a later field sees earlier ones' committed effects).
2. **Creates bypass it entirely.** New objects write **live** — the create form's Save fires `set.add`
   immediately (`arrayAdd`), persisting + remapping before any commit runs. So the model is **split**: edits
   stage and commit, creates fire live. "Atomic commit" over only the edits is a leaky promise — the commit
   isn't the unit if creating an object skips it.

Same root: the `ctx` should be the unit of persistence; today it's the unit of *edits only*.

## Why now (the honest why)

Beyond atomicity-as-correctness, doing this **retires the create-then-populate model** — live `set.add`, the
create-live-then-edit window, the per-op negative→real id remap — which has caused repeated bugs (the
transient-id editing drop `cfdf380`, the create-draft staleness fixes `51b78fa`). Staging the whole new-object
graph and committing it once closes that race-prone window, and makes the `ctx` the **single** coherent
persistence model instead of two.

The cost is real and stated up front: this **re-architects how `create` works** across the generic UI, and it
trades create-then-populate complexity for staged-subtree + batched-remap complexity. "Cleaner" is the goal,
not a guarantee — which is why this is spec → review → build, carefully, not a quick slice.

## The boundary — the ctx's staged set

The atomic unit is **everything in `ctx.staged`**:
- **edits** — `(existingObject, prop, value)`;
- **creates** — new objects (negative ids) with their fields;
- **relations** — the links placing a new (or existing) object into a set / a reference.

The commit is the transaction over all of it. The `ctx` **is** the unit of work. Creates **stage** (they no
longer fire live) — that is what graph-save *is*, and it is intrinsic to atomic commit, not a follow-on.

**The surface is the custom render, not the generic UI.** A hand-written `fn render()` already opens `ambient
ctx = ctx.new()` and can compose fields/forms over **several** objects — a new parent + new children + the
relations — all staging into that one `ctx`, committed together. So multi-object graph commits ARE authorable
today (and testable via a small custom-render fixture); the generic UI is just one *constrained* consumer that
happens to author only edits + a single scalar create. Limiting the commit primitive to the generic UI's
current shape would be a premature restriction on the flexible base — the base must support what a custom
render expresses, and real apps (an order + its line items) need exactly that. The generic UI rides the same
commit for the cases it can express; it does not bound the primitive.

## The design

**Staging a create (the create-flow change).** Today a create form (SetTable's create card, RefEditor's
create-new) builds a `sys.new` draft (negative id) and on Save fires `set.add` / ref-create **live**. The
change: on Save it **stages** the new object + its relation into the ambient `ctx`; the draft's field edits
stage as usual. Nothing persists until the enclosing `ctx` commits.

**One `commit` op.** Payload = `{ edits: [(id, prop, value)], creates: [(negId, type, fields)], relations:
[(parentId, prop, childId)] }` (ids may be negative for staged creates). One msgId. Replaces the per-field
`objectPropChange` fan-out *and* the live `set.add` for staged creates. (`objectPropChange` / `set.add` remain
for any genuinely-live path — see open questions on live-vs-staged collections.)

**Server — validate-all, then create + link + apply, all-or-none.** The `commit` handler, inside one store
transaction: resolve ids; **allocate real ids for all creates and remap negative→real across the whole batch**
(edits/relations referencing a new object pick up its real id); run the access floor (`RequireWrite`) on every
write + create; if all pass, create + link + apply through ONE store batch op (one `Save()`, OS-atomic); reply
`{ ok, idMap }`. Any failure → nothing persists, reply `{ error }`.

**Store — batch create + write behind the interface.** A new `IInstanceStore` batch method (CLAUDE rule 6)
doing the creates + field writes under one held `_sync` + one `Save()`. No on-disk format change.

**Client — one journal entry.** The commit collects edits + creates + relations into ONE message + ONE
`JournalEntry` whose `undo()` reverts the edits *and drops the created objects*; `roots` = the batch's objects.
One `stateGen` bump. The single ack carries the `idMap` (negative→real), applied like today's per-`arrayAdd`
remap but once for the batch. `ctx.pending` → 0/1; the reactive `ctx.status` ("Saving… → Saved") is unchanged.

## Build sequencing (incremental, but "done" = the whole thing)

The deliverable is the coherent unit (edits + creates). It is built and verified in steps; the edits-only
checkpoint is **not** released as "atomic commit" — that would be the split model rightly rejected:

- **Step A — the commit protocol for edits — DONE (`ec076eb`/`932430d`, suite 550).** One `commit` message
  (creates-capable payload *shape*, so B doesn't re-cut it), validate-all-then-apply, one journal entry, one
  store batch-write (`WriteFieldBatch`). Review-confirmed byte-identical floor decision + true all-or-none.
  Checkpoint: atomic *edits*. Landed as the foundation, NOT released as the feature (creates aren't in yet).
- **Step B — stage creates + relations.** The create-flow change (Save stages, not lives), creates/relations in
  the payload, the subtree id-remap, the bundled-undo journal entry. Completes the coherent unit. **Done = A + B.**

## Decided

- **The `ctx` is the atomic unit** — edits + creates + relations, multi-object.
- **Creates stage** (no longer live) — graph-save is intrinsic to atomic commit.
- **Authorization decisions unchanged**, application made atomic.
- **Storage atomicity via one `Save()`** behind a new batch interface method (no bypass, no format change).
- **Built incrementally; the edits-only checkpoint is not released as the feature.**
- **Stage-vs-live by the transient/existing discriminator (review-resolved).** `set.add` / `setReference` of a
  **transient (id < 0) draft** STAGES into the ambient `ctx`; the same op on an **existing (id > 0)** member
  stays **live**. The *same id discriminator already in the code* (`codeExec.ts:179`), so the split is
  principled — building new graph stages and commits atomically; poking an existing collection stays immediate.
- **The honest cost:** staging creates is a **twin/interpreter change** (`addToCollection` consults
  `nearestStagingCtx` for transient members) with a **conformance case** — not the transport-only shortcut I'd
  hoped. Justified now that the custom-render surface makes multi-object graph commits real.
- **The store batch is a model-term mutation list** (CLAUDE rule 6) — a closed union over
  create / link / writeField / writeRef under one `_sync` + one `Save()`, not a flat op/KV list. Validate-all
  builds candidates exactly as today's per-op handlers do (id 0 for creates), so the floor decision is
  identical, just hoisted before the apply.

## Open questions

- **The generic create card's `ctx` boundary** (generic-UI-only). With transient-stages-into-`ctx`, a generic
  SetTable create card inside a staging ObjectForm would stage into the *form's* `ctx` — so its own Save no
  longer persists (you'd Save the outer form). That's a generic-UI UX shift. Options: the create card opens its
  **own** child `ctx` (commits its create on its own Save — the common single-create case, no UX change), or it
  genuinely stages into the parent. Pin per-surface; this affects only the *generic* consumer, not the
  custom-render capability.
- **Validate pre- vs post-batch state** (carried over) — lean: pre-commit snapshot for edits.
- **Partial-graph failure** — a create whose sibling write is denied: the whole graph rolls back (no orphan).
  Confirm the bundled `undo()` drops created objects cleanly and the negative ids never leak as real.
- **Error / idMap payload shape** — `{ error }` whole-commit (lean); `{ ok, idMap }` on success.

## Deferred / out of scope

- **Invariant "teeth"** (uniqueness, no-oversell, ranges) — server-checked *validity* on top of the atomic
  commit. **Deferred 2026-06-27** (no current/near-term app needs them). Design recorded: per-type Code
  predicates at the write floor over the *resulting* state, with `db` in scope; `unique` as an optimizable
  sugar. Build when a dogfooded app hits the wall.
- **Sequential-ids** — server *allocation* (a counter under the commit lock), a distinct mechanism.
- **Multi-client conflict, a server-side warm graph, cross-machine atomicity** — the real-time / distributed
  pillar. (Single-client validate-then-apply under the store lock is in; cross-session coordination is not.)

## Gherkin

- **Atomic edit:** two editable scalars, one of which an access rule denies; stage both; commit; assert
  **neither** persisted (today the allowed one slips through).
- **Atomic create-graph** (driven by a small **custom-render test fixture** — the generic UI doesn't author
  multi-object graphs yet, so a hand-written `fn render()` stages a parent + a child + the relation in one
  `ctx`): commit; assert both exist, linked, with real ids — and on a forced failure (a denied write in the
  batch), **neither** exists (no
  orphan, no partial graph).
- **Happy paths:** a multi-field edit commits and shows "Saved"; a create commits and the new object appears
  with its real id.
