# Atomic commit — the ctx's staged set (edits + creates) as one transaction

**Status: DONE 2026-06-27.** Step A (atomic edits) `ec076eb`/`932430d`; B1 (the card/form collapse) `5597cba`; B2 (stage creates + relations, commit the whole changeset) `e2bb210`/`ad2d544` (suite 561, both twins + conformance). **The `ctx` is now the atomic unit over an arbitrary changeset** — edits + creates + relations, all-or-none, with the credential chokepoint intact and the floor un-widenable. Invariants (the validity "teeth") + sequential-ids stay deferred — see end. The destination
was a commit where **the `ctx` is the true atomic unit**: everything staged in it — field edits, *and* new
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
  the payload, the batch id-remap (flat — every staged create gets a real id; any negative reference is
  remapped, no tree walk), the bundled-undo journal entry. Completes the coherent unit. **Done = A + B.**

## Step B — design (grounded in the create-flow map)

**Key finding: a SINGLE create is already atomic.** `set.add(draft)` sends one `arrayAdd` carrying the draft's
scalar fields; the server mints the object WITH those fields and links it in one op (`WsHandler.HandleAddSetMember`).
So Step B's value is **not** single-create atomicity (already there) — it's (a) **atomicity over an arbitrary
changeset**: a custom render can stage edits + creates + relations across *possibly-unrelated* entities (a User
edit, a new Order, a Product edit) and commit them as one transaction — a connected parent+children graph is
just one shape of this, not the requirement; and (b) **coherence** (creates flow through the same `ctx`/commit
model as edits). The authoring surface for (a) is the **custom render**; the
generic UI only ever stages one create.

**Today's draft flow (the starting point).** A `sys.new` draft (id<0) holds its field edits entirely on
`obj.props` — the `id>0` staging gate (`codeExec.ts:179`) makes draft edits write live to the draft object,
never into `ctx.staged`. `set.add(draft)` fires `sendArrayItemAdd` live, ignoring any staging `ctx`. So a draft
is NOT in `ctx.staged` today.

**The model — nested contexts as a unit-of-work** (the settled mental model). Contexts nest. A write inside a
context is **isolated** there (reads see through to the parent + store; writes buffer) until that context
commits. A context's commit **transfers** its staged changes — edits AND creates AND relations, *uniformly* —
into its **parent**, UNLESS it is the outermost transaction, in which case it **persists** to the store as one
atomic `commit`. "Outermost transaction" = a context whose parent is the live store AND that actually commits
(**has a Save**). A context only defers its children if it's a real staging transaction; a container *without*
a Save — the **Db root** (collections only, no scalars — `7dc42a8`) or a **top-level set route** — holds a
**live** context, so its creates persist immediately (nothing to defer to). This **overturns** the earlier
"creates always mint" special-case: creates obey the **same** rule as edits (defer to parent, persist at the
outermost). One uniform rule; the position decides defer-vs-persist, not the change kind.

**The join point.** A staged create is `{ draft, join }` — the draft object **by reference** (fields read at
commit, never snapshotted, so a later `draft.x = …` isn't lost — this is what *retires* the create-then-populate
staleness, `cfdf380`/`51b78fa`) + where it attaches (the set/ref it joins). The join is the unit that travels up
the context chain; at the final commit, the create mints and the join applies.

**The mechanism:**
1. **`ExecCtx` gains a `creates` slot** (both twins): `creates: Array<{ draft: ExecObject, join: set|ref }>` —
   the draft reference + its join, beside `staged` (edits). No field snapshot.
2. **`addToCollection` / `setReference` gain a staging branch** (both twins + a conformance case): a **transient
   (id<0) draft** under a staging `ctx` → stage `{draft, join}` into `creates`; an **existing (id>0) member stays
   live** (the same `id>0` discriminator already in the code). The row still pushes to `arr.items` **locally**
   (optimistic UI unchanged); only the *persistence* defers.
3. **`ctx.commit()`:** a **non-final** context flushes its creates+edits+relations **into its parent**; the
   **final** context (parent live, has a Save) builds the `commit` op `{ edits, creates, relations }` and sends.
   The all-or-none **teeth live in `HandleCommit`** (server) — the interpreter staging branch is client-shaped
   (the C# `AddToCollection` mints in-store, so the staging branch only ever runs in the conformance harness;
   that's fine for twin-lockstep, and the conformance case stays strictly in-memory).
4. **Server `HandleCommit` create phase:** validate-all (access on edits + creates — the create candidate built
   exactly as `HandleAddSetMember`: `ScalarObject(type, 0, fields)` + **the password-hash chokepoint**
   `HashPasswordFields`, a SECURITY must so a staged `User` create can't skip hashing); mint all creates; build
   the negId→realId map; apply relations + edits with both sides remapped; one `Save()`. Reply `{ ok, idMap }`.
5. **Flat batch remap (no tree walk):** mint-all-then-link works because creates have **no inter-create field
   deps** (draft fields are scalars; object links are the separate `relations`). A relation/edit referencing a
   negId not in the batch → whole-commit `Error`; negIds never leak as real client-side. Each `idMap` entry
   carries the per-create `collections` map (nested-set remap), reusing `remapAddedId` as the shared re-key.
6. **`ctx.dirty` counts creates** (`staged.size > 0 || creates.length > 0`, both twins) — else a form with only
   a staged create reports not-dirty and the Save chrome misfires.

**The generic create card = a nested `ObjectForm` (the card/form collapse — this IS B3).** Edit and create are
**one component**: `ObjectForm(obj, join?)` — edit an existing object (no join), or create a draft (join = where
it lands). The component is **position-agnostic**: it stages into its ambient context and commits; the *context*
decides defer-vs-persist (nested in a form → defer; root/top-level → immediate). So SetTable's "+ New" and
RefEditor's create-new stop being bespoke create forms and instead reveal a nested `ObjectForm(draft, thisSet)`:
its confirm = the **inner commit** (transfers the create into the enclosing form), Cancel = discard, and the
enclosing form's Save (the **final** commit) persists. This **deletes** the bespoke create-form halves of
SetTable/RefEditor — strictly less code, and the nested-context behavior comes for free because it's the same
component that already nests.

**Build sub-steps (collapse FIRST — it de-risks and removes the inertness problem).** The staging branch can't
land "inert": it would regress the generic card the instant it ships (the card's `set.add` is a transient draft
under the form's staging ctx). So flip the order — unify the card with `ObjectForm` *behavior-preserving* first,
then the persistence slice transparently makes the unified form defer:
- **B1 — the card/form collapse, behavior-preserving.** Replace the bespoke SetTable / RefEditor create forms
  with a nested `ObjectForm` in create-mode (given a draft + join, its confirm does a **live `set.add`** —
  today's exact behavior). Deletes the bespoke create forms; the existing SetTable create tests stay green. Pure
  UI refactor, **no twin / persistence change**. `ui-architecture-reviewer` + `ux-reviewer`.
- **B2 — the persistence.** `addToCollection` / `setReference` stage a transient draft under a staging ctx (both
  twins + conformance); `ctx.commit` flush (non-final → parent; final → the `commit` op); server create phase
  (mint, flat-remap, password-hash chokepoint, `idMap`) + store batch; client batch remap; `ctx.dirty` counts
  creates; `ObjectForm` opens a **live** ctx when it has no stageable scalars (so the Db root / scalar-less
  containers don't trap a create). The collapsed create-`ObjectForm` now **auto-defers** (its `set.add` stages).
  Proven by the custom-render **multi-entity** Gherkin + the generic create now persisting on the form's Save.
  Opus builder (twin + negative-id remap + reconcile — the subtle zone).

**Build sub-steps (incremental):**
- **B1** — `ExecCtx` creates slot + the `addToCollection` staging branch (both twins) + the conformance case
  (stage `set.add` → draft sits in `ctx.staged`, not persisted, until `ctx.commit()`). Staging only; no
  commit/server change yet.
- **B2** — `ctx.commit` flushes creates/relations into the `commit` op; server mint+link+apply + `idMap`; client
  batch remap. The custom-render multi-object Gherkin.
- **B3** — the generic create-card adaptation (own `ctx` + commit), per the decision above.

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
- **Partial-batch failure** — a create whose sibling change in the batch is denied: the whole changeset rolls back (no orphan).
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
- **Atomic changeset** (driven by a small **custom-render test fixture** — only a custom render stages changes
  across multiple entities — a hand-written `fn render()` stages a change to one object + a new object of
  *another, unrelated* type + a relation in one `ctx`): commit; assert all applied with real ids — and on a
  forced failure (a denied change in the batch), **neither** exists (no
  orphan, no partial graph).
- **Happy paths:** a multi-field edit commits and shows "Saved"; a create commits and the new object appears
  with its real id.
