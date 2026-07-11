# Data composition — libraries with data, shared stores, cross-app refs (session agenda)

*2026-07-11. Derived from a design conversation the day after
`docs/plans/user-libraries.md` landed (companion doc — the code-only v1
mechanism). Status: **conversation-derived agenda + positions taken — the
draft-and-grill design session has NOT been run, nothing accepted, nothing
scheduled.** Positions below are defaults from the conversation, recorded so
the session starts from here instead of from scratch; every one is open to the
grill.*

## The library-kinds ladder

1. **Code-only** — components + fns. Designed and grilled (user-libraries.md).
   Needs NO types: the stdlib is the existence proof (~580 lines, zero type
   declarations) — lib components are generic over the CONSUMER's schema via
   descriptors (`sys.schema`/`sys.extent`/`sys.new`); the rest take scalars
   and literals. Types appear only as workbench preview-fixtures (already
   fenced in the v1 doc).
2. **Schema libs** — a lib contributes reusable TYPES (Address, Money, the
   domain models in docs/domain/). No own data.
3. **Data-owning libs** — types + rows: comments-on-anything, audit, tags,
   notifications, i18n. The Django-reusable-apps / WordPress-plugin class.
4. **Extension libs** — hook on app events without the app calling them.
5. **Whole-app templates** — NOT libs; design cloning already covers this.

## Positions taken in conversation (defaults, un-grilled)

### Where lib data lives (rungs 2-3)

**In the consumer's store, under a per-lib container** (Postgres-schema
style — the encapsulate-user-namespace rule one level up), never in a
separate lib instance: the instance is the unit of atomicity/access/
versioning/backup, and lib data must join the app's atomic ctx and rules.
Publish merges the lib's types into the consumer's schema; a lib upgrade =
republish with a moved pin, migrating the lib's types through the EXISTING
semantic-migrations pipeline. Pin-and-link survives: code stays linked; types
MATERIALIZE because data outlives code — that's publish doing its normal job,
not vendoring. The container/namespace shape is the key session decision.

Second legitimate stateful-extension shape: the **companion app** (the
access-log/analytics design precedent — kernel-side recording + a separate
app doing rollups). Rule of thumb: data about the app's objects needing its
transactions → data lib; data about the whole kernel / crossing apps →
companion app.

### Hooks (rung 4) — the fork is a USER decision

- **Explicit composition** — app calls `Comments(order)` / `audit.record(...)`.
  Works with v1 already, zero new mechanism, covers more than it sounds like.
- **Implicit data-change hooks** (`on db.orders.add`, trigger-style, inside
  the same atomic ctx) — what makes "install it and it just works" real. HARD
  specifically because of the twins: mutations apply optimistically on the
  client, so hooks either run identically in both twins (conformance surface,
  ordering, cascade/loop guards) or are server-only with reconciliation. The
  twin question is the heart of the session.
- **Kernel chokepoints** — precedented (analytics design) for transparent
  READ-side cross-cutting extensions; no language event system.

### Apps interconnecting data — three shapes, one story

- **(a) Modules of one system → ONE store.** Don't federate one business's
  data; sovereignty properties break at the seam and it re-imports the
  distributed problems parked at Stage 5 (pillar 7 stays LAST). Modules =
  per-lib containers inside one store.
- **(b) Shared-store mounts** — the strongest shape (user-driven): store ≠
  instance stops being 1:1; N apps MOUNT one store (registry gains the
  store/app split). **One schema owner per store**; satellite apps are
  code-only designs — exactly the v1 lib document shape, so the machinery
  converges. Per-app access sections over a SHARED user set (users are db
  data) = database-roles-per-application; enforcement stays the per-app
  kernel floor. The one genuinely new machinery: **cross-app invalidation** —
  a write through app A must re-render app B's warm sessions on the same
  store (fan-out of the existing per-instance change propagation). Portable
  image unit becomes store + all mounted apps. Honest check kept: one app
  with access rules + login-as-state already covers public-vs-admin; the
  split buys separate designs/publish cadence/origins/versioning — confirm
  those are the real wants.
- **(c) Cross-instance READ refs** — for genuinely separate apps linking
  loosely: qualified `(app, id)` refs, kernel-mediated (the rule-12 delegate
  precedent), committed data only, owner opts in via its access section,
  **NO cross-instance writes** (cross-instance ACID stays Stage 5 — the
  guard that keeps this from smuggling pillar 7). Hard parts named:
  cross-app identity (whose user, whose rules — a Stage-3 question arriving
  early), cross-instance footprints/reactivity, independent schema evolution
  (loud dangling semantics).

### Consumer awareness + backwards compatibility

- **Awareness = local queries, not a protocol.** Every connection leaves a
  durable local trace (lib pins on boundary+registry, mounts in the registry,
  read grants in the owner's access section) — the designer surfaces
  consumers per design/store; guards at dangerous ops (the v1 delete-guard is
  the first).
- **Compat, local-first = impact analysis + coordinated republish, NOT
  version coexistence.** The schema owner's publish preview reports the blast
  radius across consumers (their code is structured rows in the design-host —
  analyzable; previews-as-read-builtins is the Track-B pattern). Coexistence
  (store serving N schema versions through adapter views) is REJECTED
  locally — against one-shape-at-a-time; it becomes a capstone-day question
  only when consumers aren't yours, and pins are the right carrier for
  semver/deprecation then.
- **The grant is the contract surface** — what an owner granted foreign
  consumers is what triggers impact reporting when changed; internals never
  exposed churn freely. One general mechanism (access rules), no new
  "exported" primitive.
- **Removal semantics: ordered, not forbidden** (user challenged
  ossification — grants must not freeze the schema). `DROP … RESTRICT` /
  `CASCADE` model: default refuses removal WITH the consumer list, remedy =
  republish consumers first, then remove; explicit force lets the owner
  remove and accept the enumerated loud breakage (flexible-base rule: the
  base always allows removal; safety is tooling on top). Generic-UI consumers
  adapt automatically (they reflect current schema); only by-name custom code
  pins the surface, and the scan is best-effort where names are computed —
  runtime still fails loud. History unaffected: pins/time-travel keep the
  removed field's era. Remote/capstone: deprecation windows + majors on
  pins — decided then, not foreclosed now.

## Session shape when run

Draft + fable grill (the /design skill flow), producing this doc's accepted
form; grill seams to hammer: the container/namespace shape, the hooks twin
question, schema-owner enforcement on shared stores, cross-app invalidation
vs the warm-session model, impact-report completeness limits, and whether
each shape has a real driving consumer (same gate discipline as
user-libraries — nothing here has one today).
