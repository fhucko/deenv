# Designer → lib collapse — plan

Drive the **dogfood designer (instance 1) toward its irreducible custom floor** by
lifting every recurring UI pattern into the generic lib. The designer's
custom-line-count is the dev-UX metric: build-apps-with-least-code means the
designer (a real app) should hand-write almost nothing the lib could supply.
**Without compromising functionality or UX** (memory `feedback_least_custom_app_code`).
*2026-06-28.*

## The irreducible custom floor

After collapse, the designer should hand-write only:

1. **Host ops** — `sys.create` / `cloneInstance` / `delete` / `rename` / `setDesign`
   (and a future `publish`). Calling them is custom; the scaffolding around them isn't.
2. **The prop-type picker** — the `<select>` offering *this design's own types* as
   field types (designer app.deenv ~105–113). "Options come from sibling rows in the
   same form" is intelligence the generic UI can't synthesize. This is the permanent
   custom heart of the design editor.

Everything else — the instances table, columns, empty state, row-link nav, create
forms, inline-nested-set editing, the action menu, inline rename, the detail
scaffold — is lib scaffolding we either already have (`SetTable`/`ObjectForm`/
`Field`/`ConfirmButton`) or should add.

## What already works (don't re-litigate)

- `designsListPage` already composes `<SetTable>` + `<ConfirmButton>` + `<Field>` —
  maximally thin.
- A **stateful component tag-invoked inside a `foreach` works** (`<instanceActions>`
  per row, shipped).
- An **event-handler closure prop inside a `foreach` works** (`ConfirmButton`'s
  `onConfirm`, shipped).
- A **render-prop that receives DATA inside a `foreach` works** (`SetTable`'s
  `rowActions(m)`, shipped).
- **What FAILS:** a render-prop that receives a **fresh closure** as its argument
  inside a `foreach` (`KebabMenu`'s `body(close)`) — the client `closureKey` collides
  across rows (all rows capture the first). This is the one precise framework
  limitation. `KebabMenu` therefore sits in the lib as **dead code** (nothing
  consumes it).

## Slices (leverage order)

### Slice A — instances LIST through `SetTable`  ·  highest leverage
Collapse the hand-rolled `<table>` + create form (~55 lines) into a `SetTable` call,
reusing the existing working `instanceActions` as `rowActions`.

- **Leading approach:** present the kernel registry **as data** — give `sys.instances`
  a set-shape (`foreach`/`any`/`sys.id`) + a synthesized descriptor (`sys.schema` for
  the registry row: `app` / `path` / `design`) — so the EXISTING `SetTable` renders it
  with **no SetTable change**. Aligns with the "kernel-as-data" read path
  (`SsrRenderer` already exposes `sys.instances` for exactly this) — a general
  mechanism, not a special case.
- **rowActions:** pass the existing `instanceActions` (host-op kebab) — already
  foreach-safe.
- **Create:** host `sys.create` is **floor** (a host op), so keep the design-picker +
  name create form **custom**, rendered below the table. Do NOT bend `SetTable`'s
  `set.add` create contract for a host op.
- **Open question:** does the registry need a real ACL/`canWrite` story for `SetTable`
  to show its chrome, or do we render `SetTable` in a read+rowActions mode and own the
  create button ourselves? Settle when slicing.
- **Gate:** the existing designer Gherkin (instances list renders, create, rename,
  clone, delete) stays green. No twin change expected (descriptor synthesis is C#
  host-side, like the existing descriptor registry).
- **Review:** ui-architecture + ux.

### Slice B — instance DETAIL through a generic scaffold  ·  medium
Collapse `instanceSelectorPage` (the head + rename + actions + `designSelector`).

- Most of this is **floor** (host ops). The reusable scaffold is the page frame +
  the rename affordance (→ Slice E) + the actions menu (already `instanceActions`).
- `designSelector` (pick + Apply via `sys.setDesign`) is a host op → stays custom.
- Likely small once A + E land.
- **Gate:** existing detail-page Gherkin green. **Review:** ui-architecture + ux.

### Slice C — inline-expanded editable nested set  ·  bigger, independent
The design editor's type list (each with a prop list) is structurally an
**inline-expanded editable nested set** — a generic pattern (invoice line-items,
order rows, accounting splits — the domain docs). The lib lacks it; the generic UI
only navigates *into* each member.

- **New lib component:** an editable set rendered with all members **expanded inline**
  (add / remove / empty), member content supplied by the caller.
- **Keeps custom:** the sibling-type prop picker (the floor). The picker is the
  per-row custom content the component must let the caller inject.
- **Depends on Slice D** if injection uses a render-prop receiving the row + a fresh
  callback; avoidable if the caller writes a named per-row component (the working
  `instanceActions` shape). Decide injection style during D.
- **Gate:** designer schema-editing Gherkin (add/remove type, add/remove prop, pick a
  sibling type, enum values, code areas) green. **Review:** ui-architecture + ux.

### Slice D — foreach-safe action menu (de-dup the kebab)  ·  enabler
Make a shared menu consumable inside a `foreach`, so `instanceActions` (and Slice C's
row injection) compose it instead of re-inlining, and the lib's dead `KebabMenu`
becomes live.

- **Root cause to confirm first:** the fresh-closure-arg `closureKey` collision.
- **Two approaches** (pick after investigation):
  - (a) **Fix client closure keying** — disambiguate a closure passed within a
    `foreach` iteration by slot/row. Robust, general, also unblocks render-prop
    composition everywhere (preferred per `feedback_robust_solutions` *if* contained).
  - (b) **Redesign the menu contract** so the caller never receives a fresh `close`
    closure (pass data; the menu owns close via toggle/backdrop) — dodges the bug, no
    framework surgery. Lazier; loses nothing if auto-close-on-action still works.
- **Gate:** a Gherkin proving the menu works across multiple rows (each row's menu
  independent); if (a), a client-reconcile test for the keying. **Review:**
  architecture-reviewer (client/interpreter).

### Slice E — `EditableLabel` (inline rename)  ·  small polish
The rename pattern (`renameId`/`renameName` + input/Save/Cancel) appears 2× in the
designer. Lift to `<EditableLabel value= onSave=>`. Used by A and B.
- **Gate:** rename Gherkin green. **Review:** ui-architecture + ux.

## Sequencing & dependencies

```
A (instances list)  ──┐
E (EditableLabel)   ──┼─→ B (instance detail)
D (foreach menu)    ──┘
C (inline nested set) — independent; D informs its injection style
```

Start with **A** (biggest single reduction, low risk — host-side descriptor + reuse).
**E** and **D** are small enablers that feed **B**. **C** is the largest and most
independent; schedule after the instances side is collapsed and **D** has settled the
injection style.

## Out of scope (don't pull in)

- **Full kernel-as-image** (registry as a writable multi-kernel set, distributed ops)
  — Slice A presents the registry **as read data with a descriptor**, nothing more.
  The write ops stay host builtins (the floor). AGENTS.md ground rule 1.
- **A publish button** — doesn't exist yet; when added it's a host op (custom) and may
  be M13/versioning territory — confirm intent before it drives anything.
- **M12 visual component designer** — deferred until after the MVP.

## Slice A is SHELVED — the instances list stays custom (2026-06-29)

The instance-list friction's root cause is that **instances are foreign kernel data**
(`sys.instances` — transient rows, real id in a prop, store-local `designId`). Collapsing
the list onto `SetTable` needs either:
- **`idProp`** (address rows by a field) — **rejected**: single-use scaffolding on the
  core component, needed *only* because the registry is foreign/projected data; the
  milestone below deletes the need. Not worth bolting onto `SetTable`.
- the **multi-designer milestone** (instances become stored objects → `SetTable` just
  works, no special-casing) — **deferred** (`docs/plans/multi-designer-instances.md`;
  Stage-2, ahead of the MVP bar).

So **Slice A is shelved**: the instances list resists the generic component precisely
because it's foreign data, and there's no clean *general* small fix. It stays hand-rolled
until the milestone collapses it for free.

**Near-term designer-cleanup = the GENERAL slices only** — Slices C (inline-expanded
editable set), D (foreach-safe menu), E (EditableLabel). These earn their lib place
(multiple consumers across apps), need no `idProp`, and don't touch the foreign-data
problem. B (instance detail) partially depends on A/the milestone, so it waits too.

## Done = the designer hand-writes only the floor

The success measure: the designer's `app.deenv` shrinks to **the 5 host ops + the
sibling-type picker + thin route wiring**, with every table / form / menu / rename /
nested-set coming from the lib — and the rendered UX unchanged or better.
