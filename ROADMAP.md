# Roadmap

The mission (see VISION.md) is large. This document sequences it into
finishable milestones. **A milestone is done when it works and is usable —
not when the code exists.**

Ground rule: build the current milestone only. Later milestones are out of
scope until the current one is finished. Sequencing is not the opposite of
ambition — for a mission this size, it is the only way the ambition ever
becomes real.

The milestones below run in order down to a single **Future work** divider: everything above
it is done or in progress, everything below is future and out of scope. When a milestone is
completed, move its entry above the divider.

**Current focus (2026-06-21).** Milestones 1–11 are done. The active phase is the
**usable-MVP gates**: (1) non-destructive apply — data survives a schema change — ✅ done;
(2) a minimal real deploy (a self-contained build as a systemd service behind nginx) ✅
done; (3) dogfood one real app — in progress (`instances/5`, `devlog`). The visual
designer is deferred until after the MVP; M13 (schema versioning) sits on instance
management. See CLAUDE.md "Current focus" for detail. **M-auth (access control) — DONE 2026-06-25**: the access
engine, self-hosted login/logout, the `devlog` dogfood, first-admin bootstrap, and multi-user
management all landed (spec `docs/plans/m-auth.md`, decision in DECISIONS.md). **The client data layer
(render-as-planner) — DONE 2026-06-26** (6 slices, suite 537; its entry is below). The M-auth follow-ups
(deploy login wiring, remove-user/role-edit) live in Near-future too.

---

## Milestone 1 — The instance, single-boolean Db  ← START HERE

The **instance** — the thing that runs a user's app — running the smallest
possible instance: a `Db` that is literally one boolean. The instance is
hardcoded; no schema designer, no IDE, no editing the app yet.

A vertical slice: narrow, but it goes all the way down — UI, object layer,
and storage. Specced by the feature files in `DeEnv.Tests\Features`:
Instance, BoolRootInstance, Navigation, BooleanPersistence.

Stack for this milestone (see DECISIONS.md and EXPECTATIONS.md):
- One solution, two projects: `DeEnv` (the whole thing, instance inside it)
  and `DeEnv.Tests` (Reqnroll Gherkin tests). Buildable in VS2026.
- TypeScript only, no hand-written JS; compiled via
  `Microsoft.TypeScript.MSBuild`.
- Minimal HTTP handler — .NET `HttpListener` or minimal-API
  `WebApplication`. No ASP.NET MVC.
- Storage is a plain JSON file, simple rewrite. No SQLite, no ACID yet.
- **Storage is accessed through an interface** that speaks the model's terms
  (paths, nodes, dictionary entries), not flat key-value, and never the file
  API directly. Exact shape TBD in code. This seam is what
  lets the storage implementation be swapped later — see Milestone 7.
- The instance's **formal description is written by hand** for now — no
  designer, no generator. The instance is the only focus this milestone.

Done when: the instance is served and the checkbox value persists
across a reload (crash-durability is explicitly out — see DECISIONS.md).

## Milestone 2 — Generalise the instance beyond one boolean

Let an instance have more than one field, and more than one type. The
hardcoded boolean becomes a small declarative description of the data.

Done when: an instance can be defined with multiple fields and runs.

## Milestone 3 — The schema document

Promote that description into a clean, declarative **JSON schema document**
(tables, columns, types, relationships) — the format the generator reads,
the versioning diffs, what code eventually compiles against.

Done when: instances are defined by a validated JSON schema document.

## Milestone 4 — The schema designer  ← DONE (delivered as self-hosting)

Originally scoped as a bespoke web canvas. **Delivered instead by self-hosting**:
the designer is the instance runtime running a hand-written meta-schema
(`DeEnv/meta.schema.json`), so a schema is authored as ordinary data through the
existing generic UI. A bridge (`DeEnv/Designer/SchemaBridge.cs`) projects that
data into a canonical `instance.schema.json` (validated by the normal loader) and
the instance runs it. A `--mode` switch (VS launch profiles
Instance / Designer / Export) flips between authoring, exporting, and running.
The pretty card-grid surface and an in-app "Run" action are intentionally left to
later milestones (UI customization, and the code milestone) — see
DECISIONS.md.

Done when: a user can design a schema with no code and run it. ✓

## Milestone 5 — The object model (identity, references, sets)  ← DONE

Give the data a real object model. Today it is a pure containment *tree*:
a prop is a scalar, an inline object, or a dictionary that *owns* its
children. There is no way for one entity to point at another by identity.
This milestone makes it an **object graph**, the C# way:

- **Intrinsic identity** on every non-constant (objects and dictionaries;
  scalars are value types, no identity). Monotonic `int`, stored as metadata
  separate from props. No schema change — identity is intrinsic to being a
  non-constant.
- **References, no ownership.** Objects live in **per-type extents** (a flat
  id-keyed pool per type); a single object-typed prop *is* a reference, and
  object-typed collection entries hold references too. The same object can be
  referenced from many places and is one object.
- **Sets.** A `set` is a collection of objects keyed by their **own
  identity** — replacing the surrogate-keyed `dict<Object> auto-int`.
  Dictionaries stay for genuine maps where the **key is meaningful data you
  chose** (e.g. scalar `settings`). Three shapes: single / set / dictionary.
- **Addressing keeps the existing navigation** — a URL is a walk through the
  graph: set → member identity, dictionary → key, single → field; with an
  id-route fallback for following a bare reference.
- **Lifetime by GC** — mark-sweep reachability from the root collects objects
  no reference can reach.
- **UI:** a reference field / set offers pick-existing-or-create-new.

First slice: identity + one type's extent + a set of references + identity
addressing + pick-or-create + GC, proven by "the same object via two
references is one object" and "dropping the last reference collects it."
Migrating all collections to sets, and teaching the designer/meta-schema to
author refs/sets, are follow-up slices.

Done when: data is an object graph — objects have identity, are referenced
(not owned), collected into sets, and shared references resolve to one object. ✓

## Milestone 6 — Code (reactive UI as AST)  ← DONE

User-authored behaviour and UI, extracted from the app14/app15 prototype and
adapted onto the M5 object model. All code is **hand-written AST** (JSON, in
the schema document's `ui`/`common` sections) — no text parser yet. Two
hand-maintained twin interpreters (C# server / TS client) kept in lockstep by
a shared conformance suite. Delivered in stages (plan: cozy-humming-metcalfe):

- **SSR + client runtime** — the render fn executes server-side for the first
  paint, the client hydrates and takes over (identity-keyed DOM
  reconciliation, two-way binding); code owns routing via a two-way `path`.
- **The memo cache** — every computation boundary (fn call, where/orderBy) is
  memoized with its result and **dependency refs (props / membership / vars),
  never input values**, so privacy is structural: data read only inside a
  value-returning computation never ships, with no annotation. First paint
  never calls the server.
- **WS mutations over a warm session** — SSR mints a per-client session
  (clientId, 10s claim window, hello claims it); optimistic mutations persist
  (prop change / set add+remove, negative→real id remap) and journal locally;
  a server reject reverse-replays the journal (rollback); hidden-dependency
  recomputes refetch over the client's warm graph.
- **initialData** — a hand-authored normalized seed (extents, friendly form)
  applied on first run.
- **The todo app** — the committed default instance (`instance.schema.json`):
  users → todoLists → items, selection drill-down, drafts, done-state,
  page navigation — driven end-to-end by Gherkin/Playwright.

Done when: the todo app — authored as data (types + ui AST + seed) — runs,
persists, and reacts, with both interpreters conformant. ✓

## Milestone 7 — The app document (text syntax, parser + printer)  ← DONE

One text document describes a whole instance: `types`, an optional
`initialData` seed, and code (`common`/`ui`) in an app.txt-style language —
indentation blocks, JSX-like tags, expression precedence — ported from the
prototype's combinator parser (offset cursor + positioned errors added).
**JSON is retired from authoring**: the parsed `InstanceDescription` and its
JSON form are internal only (the in-memory model and the wire — the client
still receives AST; there is no TS parser). The designer bridge publishes a
design by printing the same format. The printer (description → canonical
text) ships with round-trip tests: `parse(print(d))` is the identity and the
canonical form is a fixpoint.

Done when: the todo app is authored as `instance.app` (one file: types +
seed + UI), the whole suite stays green on the parsed text, and parse/print
round-trips are stable. ✓ Plan: code-text-syntax.md.

## Milestone 8 — UI customization (views)  ← DONE, then DROPPED (2026-06-13)

M8 added user-authored **views** (a type view `view Customer(c)`, a path view
`view "/dashboard"(p)`) to customize parts of the generic UI. It was **dropped**:
the middle layer was awkward and db-structure-coupled, and its value uncertain.
The UI is now **two modes** — fully **custom** (`fn render()`) or fully **auto**
(the generic UI). "Auto with overrides" is deferred to a cleaner mechanism: the
custom mode *composing the generic UI as a library* (M9 makes the generic UI that
library). The synthesized-view dispatch is kept as the generic UI's internal
routing only. See DECISIONS.md ("UI customization — views (M8) — SUPERSEDED").

## Milestone 9 — Self-hosted generic UI  ← DONE (2026-06-14)

The auto-form experience is
re-expressed in Code as a reflective library (`objectForm`/`refEditor`/`setTable`/
`dictTable`/`leafForm` over schema-as-data; builtins `field`/`humanize`/`extent`/
`setRef`/`nest`/`clone`) and is now the **default** renderer — an app with no
`fn render()` self-hosts. Object forms, references, set tables, objects-that-hold-sets
(inline tables, nested path-walk links), dictionaries (route + entries), and a
self-hosted NotFound all render in Code; the **C# auto-form, `instance.ts`, and the
`/js` C# client are deleted** — the self-hosted UI is the sole renderer. Infra
(`/ws`, the `/js` bundle) is on a separate port (clean app URL space); framework
context lives in a `system` scope with the generic-UI internals in a sibling
`internal` scope. Specced by `SelfHostedUi.feature` + the migrated milestone-1/2/4/5
features. See DECISIONS.md.

## Milestone 10 — Multi-instance management (single-process, single-operator)  ← DONE

One kernel process **hosts every instance in `kernel.json` at once**, each addressed by
**path** (`/apps/<name>`) under the kernel's two shared ports (an app port + an asset
port) with its own sovereign data, driven by an **instance registry** (which instances
exist) as **kernel-owned data**. (The earlier per-instance port-pair model was replaced
by path routing, commit 27c6d98.) The
substrate under schema versioning's *apply*, the Stage-2 test-instance loop, and
the self-hosted-image north star — the unit that gets versioned/applied/tested is
an instance, so instance management is the layer underneath.

**First slice** (hosting/wiring only — no Code/interpreter change): factor the
"build + start the app+infra hosts for one instance" out of `Program.cs`'s single,
blocking `RunAsync` tail into a thin C# **kernel supervisor** that starts every
instance in a registry and blocks on a shutdown signal. The registry is a plain
`kernel.json` the kernel reads **without the interpreter** (the sanctioned
bootstrap subset). Proven by two scenarios: the kernel hosts two instances on
distinct ports, both serving their root; a change in one leaves the other
unchanged (**data sovereignty**). **Landed**, and run modes were **removed
entirely** (user direction): the kernel host is the sole entry point and
`kernel.json` is the single source of what runs — a single instance is just a
one-entry registry, so there is no `--mode`/`--app` and no regression in hosting
one app. The designer becomes a registry entry; the M4 export/publish bridge is
now exposed to Code as host actions, not a CLI mode. Built in `DeEnv/Kernel/`
(`RegistryReader`/`KernelHost`/`HostedInstance`), specced by `Kernel.feature`
(`@milestone-10`); suite green 238/238. Several more slices landed: **`list`** (the registry is
readable from image Code as a read-only `instances` global — an app renders the list itself, the
first kernel-as-data read path), **`create`** (add an instance to a RUNNING kernel: minted id,
id-keyed sovereign store, operator-set ports, persisted; the `instances` view is live — no stale
data), and **`switch`/`delete`** (re-bind a running instance's ports / remove one + collect its
store) — the full create/list/switch/delete *mechanism* in C#. Then the **`sys` namespace** (the
framework builtins + `instances` under `sys`) and the **host-action channel** (Code triggers a
server-side host op): `sys.publish(schema, targetId)` runs the M4 schema export onto an existing
instance and `sys.create(schema, name)` spawns a new one — both project a passed
schema object (carried by its id; the designer's `Db { types }` meta-schema is unchanged).
Then the **operator designer + ops**: `designer.app` (now `instances/1/app.app`) gained a HAND-ROLLED
custom `fn render()` (a type/prop editor + the `sys.instances` list + per-instance
create/clone/delete/publish controls), replacing its auto generic UI — explicit image Code, NOT a
hidden callable designer (the compose path is rejected). The ops: `sys.delete(id)`,
`sys.cloneInstance(sourceId)` (copies app doc + data), per-instance `sys.publish(db, id)`.
Underpinned by a **uniform id-based instance identity model**: every instance has a stable unique int
id; storage is fully id-based (`instances/<id>/`); the registry `app` field is a display NAME label
(used for nothing functional, no `.app`); the boot-vs-created distinction is removed (ops work on any
instance by id). **Named create + rename then completed the operator flow** (the create form takes a
display name → `sys.create(schema, name)`; a per-instance Rename → `sys.rename(id,
name)` edits the registry label). Remaining: richer editing. See DECISIONS.md ("Operator instance ops +
the id-based instance identity model"). **Per-instance boot isolation** hardened 2026-07-02 (after the
deploy outage where one stale designer doc took every hosted app down): a failing instance load is
skipped LOUDLY — full error to the journal, its mount answering an explicit 503 — while the kernel and
the other instances boot normally; the design-host sync is guarded the same way. Boot-time only; see
DECISIONS.md ("Per-instance boot isolation").

**Kernel discipline:** the kernel gains the *mechanism* (host N instances, bind
ports, hold the registry) — **not** the management *experience*. Create/list/
switch/delete as the IDE are **image Code** (later slices); a C# admin panel would
be the M4 mistake (a one-off the self-hosted IDE later tears out).

**Deferred (kept out to stay single-process / single-operator):** cross-machine /
kernel-to-kernel connectivity + distributed ACID (the *Multi-device* pillar below,
Stage 5); RUNTIME fault/resource isolation between instances (Stage 5 — boot-time
skip-a-bad-instance landed 2026-07-02; runtime crash/CPU/memory containment did not); real-time/multi-
user; dynamic create/destroy-while-running and the management commands (follow-up
slices); promoting the registry to a real *restricted* kernel-instance (north
star). See STAGES.md + DECISIONS.md ("Multi-instance management — the kernel host").

## Milestone 11 — Reactive components + the public component library (the UI middle-ground)  ← DONE 2026-06-19

The generic-UI-as-first-consumer COLLAPSE landed (suite 348) — `sys.resolve(path)` + ONE
synthesized Code `fn render()` composing the library replaced the C# per-URL dispatch; a
generic app is now literally the custom-render path. *(Scheduled as M11 by user decision
2026-06-16, pulled ahead of schema versioning, which moves to M13.)* **Slices 1–3 + 4a/4b +
(b) + the dict follow-on + the public library's first slice landed (suite 315):** components
get a **render-tree-positional ("slot path") identity** decoupled from the argument-keyed
memo, so a component runs once per slot and its state survives a re-render with rebuilt
arguments; slice 2 extends the slot path through `foreach` (per-row, by member identity — the
same key the DOM reconciler uses), so a component in a list keeps independent state that
follows the object across reorder/remove; slice 3 adds an opt-in `key={...}` directive that
folds into the slot identity (caller-controlled reset); 4a + 4b moved the generic UI's
components onto tag-invocation (object-form nested ones + the ref/set/dict ROOT views via
value-position recognition); and slice (b) + the dict follow-on replaced BOTH descriptor
registries (`__descs` type + `__dictDescs` dict) with a `sys.schema(typeName)` /
`sys.schema(type, prop)` builtin (server-resolved + shipped like `sys.extent`) and deleted both.
**Recognition = pure name-resolution** (a tag whose name is an in-scope function — any
function, top-level or local — is a component; `<div>` stays an element), keyed by slot via the
**existing** memo (untouched, additive). Run-once-across-re-renders is a client behavior (C#'s
`Memoize` is write-only → server renders once), proven by the `@milestone-11` Gherkin scenarios;
a new unified `setup + renders[]` conformance protocol proves the deterministic core (recognition,
by-name binding, splice, local-component capture, sibling + foreach-row slot uniqueness) on both
twins. The **public component library** landed too — a `lib` scope (`system ← lib ← app`) makes
the PascalCase components (`ObjectForm`/`RefEditor`/`Input`/`Field`/…) composable from a
hand-written `fn render()`, with the generic UI as the library's **first consumer** (its own
completeness proof). Delivers VISION pillar 8's "auto with overrides" via the mechanism settled
in DECISIONS ("UI middle-ground"). See `docs/plans/m11-reactivity-foundation.md`.

## Milestone — M-auth: access control  ← DONE 2026-06-25 (core delivered; follow-ups → Near-future)

Users + access control, designed in a long interview and built this milestone. A **deny-by-default
ruleset over the object model**, per-**type** and per-**field**: `{type, field?, verbs, condition}`.
**Roles are not a primitive** — a role is just a `User.role` **enum**; a rule's "who" is a condition
(`currentUser.role == "Admin"`). **Conditions are pure Code expressions** (Code-as-data AST) run by the
existing interpreter over `{db, currentUser, now, client, object}`; a condition can test **set
membership** (`customer in customers`), so soft-delete = "move to a `deletedCustomers` set, same type"
(no field flag). Enforcement is a **kernel floor, below Code, on the store/wire seam** (gates reads +
writes, non-bypassable). **Flexible base, simplified by UI** — the role×verb grid is one UI over the
engine. **Users/roles baked into every instance but dormant**; `User` (`name`+`passwordHash`) by
convention; password crypto = kernel builtins; auth UI = `lib` components with **login-as-state**
(custom UI reserves nothing in URL space). Policy ships in the **app document**, constant between
publishes; conditions evaluate live.

**Delivered:** the engine (read floor + write enforcement + floor-hardening); self-hosted password
login/logout (login-as-state, no reserved URL); the `devlog` **public-roadmap** dogfood (public read +
admin-only write, via `accessActive` + a `<SignInBar>`); **first-admin bootstrap** (env-var auto-seed
on kernel boot — `DEENV_ADMIN_PASSWORD`); multi-user management (`<UserAdmin>` create + per-row
set-password, gated on a derived `canManageUsers` so the role stays private); a real-browser e2e
(sign-in/out + create-user → set-password → re-login). Both original open threads — bootstrap and the
dormant→active trigger (the rules ARE the switch, no flag) — **resolved**. Full spec
`docs/plans/m-auth.md`; decision record in DECISIONS.md ("M-auth — access control").

**Follow-ups deferred to Near-future** (below): wiring login on the deenv.org deploy; remove-user +
inline role-edit; broader auth styling. (Set-password feedback is DONE — see Near-future.)

---

## Client data layer (render-as-planner) — DONE 2026-06-26

The proper fix for the URL-keyed-refetch footgun found closing M-auth: a client-toggled `<UserAdmin>`
(behind `if state.managing`) carried open-state the server never saw, so structural privacy never shipped
its data and it rendered empty. **The view is the query** — the client ships its actual view-state
(component state keyed by render-slot) as an `(action, state)` intent over the twin-stable fn ids; the
server **reproduces the exact render/computation** over it and ships the harvested footprint, with a
state-generation guard (generalizing the login/logout epoch) for the async window. Delivered in 6 slices
on `main` `b63d788..3edc35e` (suite 537): **1a** server-side component-state seeding · **1b** client-ship +
server-reconstruct round-trip · **1c** generation guard (optimistic-clobber safety) · **3** atomic
commit-on-success handlers · **4** action-miss harvest (button-click data access, security-reviewed) ·
**GC** client-reachability sweep. **M11 altitude** (the continuation of `ctx`), **NOT** the pillar-5
render-coupled storage engine (that stays the deferred destination this moves toward). Vetted
*aligned-with-conditions* by vision-keeper. Spec + delivery record: `docs/plans/client-data-layer.md`,
DECISIONS.md.

---

## Near-future — sequenced next, not yet built

- **M-auth follow-ups.** Small, non-blocking; do as wanted:
  - **Wire login on the deenv.org deploy** — set `DEENV_ADMIN_PASSWORD` on the box and drop the
    basic-auth gate for `devlog` (gate #2 follow-on). Operator ops action; steps in `deploy/DEPLOY.md`.
  - **Login persistence across page loads — DONE 2026-07-04.** Login-as-state still flips live over the
    existing WS, and a stateless HttpOnly cookie now carries the principal across fresh GETs/reconnects
    (`docs/plans/login-persistence.md`, mechanism slice; suite 685). **Next:** re-gate the designer with
    real access rules (`sys * where currentUser.role == "Admin"` plus data rules + its custom login gate),
    closing the strategic residual and the clone-of-designer edge. Then wire the deploy login and drop the
    basic-auth gate.
  - **remove-user + inline role-edit** on the generic `/users` list / `/users/<id>` page (remove-user = a Remove control on the `/users` set table; role-edit already works on `/users/<id>` — inline-on-the-list is the convenience).
  - **Users-twice dedup — DONE (`b06b532`).** Solved by deleting the client-toggled `<UserAdmin>` popup and moving set-password onto the generic `/users/<id>` page; the menu now links to `/users`. (Not via the round-trip — a real route was the clean fix.)
  - **set-password feedback — DONE.** Reframed 2026-06-26 to a `password`-type field on User (set like
    any field → stages in `ctx` → hashed server-side on Save). **Slice 1** (the `password` type + the
    read-blank/WS-hash chokepoints + `dict` forbids + the masked field) landed `d2e1503`/`79bc1b1`.
    **Slice 2** (the form-Save feedback) landed `eedad11`/`3e632c5` (suite 547): an inline reactive
    `ctx.status` lifecycle on the generic ObjectForm shows **"Saving… → Saved"** near the Save button; a
    rejected save surfaces via the existing global error banner (one failure surface, not two — the inline
    "Couldn't save" branch was dropped in review). Scoped to the **edit** form (the `ctx.commit` path); the
    create form (`set.add`) is a small follow-up if wanted. No setPassword call, no server fn, no
    action-half (effectful server fns rejected — writes belong to ctx/commit). **Remaining: broader
    auth-component styling** (incl. the Save button's primary-green treatment + the indicator's
    color/spacing — the UI-styling axis).

---

## Future work — NOT scoped, do not build yet

- **Code, next layers.** A full type-checker (today: structural validation);
  derived-collection mutation semantics; dictionaries surfaced to the Code
  runtime; editor tooling. Enables schema versioning to be built inside the
  environment.

- **Visual component designer.** A WinForms/XAML-style visual designer over the M11
  public component library: drag/arrange/configure components on a canvas, **show-all** (the
  canvas a synced view of the full `fn render()`; the M7 round-trip printer is the visual↔text
  sync engine), **live preview = the Stage-2 inner-loop mini-instance** (the real interpreted
  renderer — no design/runtime divergence), and the native paren-free **`for … in`** keyword
  desugaring to declarative keyed iteration (the XAML `ItemsControl`/`DataTemplate` role).
  Extends pillar 1 (design visually) from data to UI. Needs M11 + the Stage-2 live-preview
  infra. See DECISIONS ("UI middle-ground → Visual component designer").

- **Admin credential flows (auth, post-MVP).** The best-practice admin-set-password model:
  instead of an admin typing a *permanent* password for another user (today's masked
  `password`-field-on-Save), **generate a temporary password** (shown once, copyable) or **send a reset
  link**, with **force-change-on-first-login**. Feedback becomes inherent (the artifact IS the result)
  and the admin never invents or transmits a lasting secret — the pattern AWS IAM / Google Workspace /
  Okta use. A different credential *model* (generate → show-once → change-on-first-login flag), not just
  feedback. Surfaced by the ux-reviewer closing the Users-twice dedup; the smaller set-password
  *feedback* fix — the form's Save feedback — is DONE separately (slice 2 of
  `docs/plans/virtual-password-field.md`, `eedad11`/`3e632c5`).

- **Schema versioning  (sits on multi-instance management — now M13, after the UI milestones).**
  Git-style versioning of the schema, built inside the environment itself using
  the code milestone (versioning is behaviour-shaped). The structural
  identity-based diff is already designed — renames are exact because
  non-constants carry identity (Milestone 5).

  **Full design landed 2026-07-02 → `docs/plans/app-versioning-design.md`** (companion grills
  alongside; DECISIONS "App versioning — the full design"). The milestone is now **app
  versioning**: every instance gets an append-only data log (the pillar-4 substrate — WAL
  durability, baseVersion, time-travel later); the designer's own log carries design commits,
  branches, and structural merge; publish bridges with forward-only migrations
  (derived-structural + commit-attached semantic). The doc carries the settled/open roll-ups and
  the slice spine, and supersedes this bullet's details (including the "branches deferred" line
  below — branches/merge are designed, slice-able late). **Sliced + building since 2026-07-03 → `docs/plans/versioning-slices.md`** (slice 0 =
  the baseVersion anti-clobber check, landed 2026-07-02; slice 1 = the append-only store log
  with WAL + genesis + boot replay + fsck, landed 2026-07-03, suite 628; slice 2 = the design
  snapshot builder — canonical text + name-path→id map, landed 2026-07-03, suite 634; slice 3 =
  Commit/Branch rows + `sys.commitDesign` + the AUTHORITY INVERSION (design-data is truth,
  boot = one-time adoption, atomic single-entry commits), landed 2026-07-03, suite 645; the
  `locked` access keyword + duplicate-subject rejection landed alongside; **slice 4 = structural
  diff + rename-safe publish with boundary log entries — THE MVP-VISIBLE PAYOFF — landed
  2026-07-03, suite 668**; slice 5 = branches + origin-keyed three-way merge (sys.createBranch /
  sys.mergeBranch, report + resolve-by-args), landed 2026-07-03, suite 682; next = slice 6
  (conflict payload + coarse UI — approved wire shape), slice 7 (time-travel), and the
  Commit-button UX slice (lockstep interpreter wiring)).

  **MVP-critical substrate pulled forward — LANDED 2026-06-19/20.** A thin **non-destructive
  apply** — *data survives a schema change* — was built ahead of / interleaved with M11
  (an app you cannot evolve without losing data is useless). What landed (server-side C#,
  `SchemaBridge.WriteDocument` + `JsonFileInstanceStore.MigrateTowardSchema`): an apply PRESERVES
  data that still fits the new schema and carries it forward — additive (new field → default),
  removed-field (dropped), scalar **value conversion** on a type change (int↔text↔decimal, …;
  unconvertible → default + reported), and **single→set** cardinality reshape. The rename gap this
  paragraph parked is now CLOSED: as of M13 slice 4 (2026-07-03), a **versioned publish carries
  renames by intrinsic identity** (the commit id-map supplies what the name-keyed schema drops);
  the name-matching substrate remains only as the one-time fallback for instances that predate
  their design's first commit. See DECISIONS ("Data must survive schema changes").

  **First slice:** in the self-hosted designer, *commit* the current schema-as-
  data as an immutable version (parent pointer → linear history) and *diff* a
  version against its parent by matching types/props on **identity**, so a rename
  reads as a rename (not remove+add). The diff is computed **in Code**
  (self-hosted), persisted **through the storage interface** as immutable
  documents with a parent (no side files), over the app document (never a text
  line-diff, never a return to JSON authoring). Proven by one scenario: rename a
  prop, commit, and the diff reports a rename. Read-only delta — it does not
  mutate live data.

  **Deferred to later sub-milestones / pillars** (kept out to stay thin):
  branches and 3-way structural merge (the latter overlaps the real-time conflict
  model); the safe live-preview / test-instance loop (Stage 2 UX, wants pillar 5);
  applying *conflicting* migrations to live data (the pillar-4 boundary); and all
  data-level *temporal value* versioning (pillar 4). See STAGES.md + DECISIONS.md.

- **Real-time / multi-user.** Live notifications for data changes on
  currently viewed data, with update and conflict resolution. Structural
  (schema) change notifications and conflict resolution — requires schema
  versioning for structural conflict resolution. Storage gets a lightweight
  concurrent safety fix (write-lock / atomic rename) inline as part of this
  milestone. Target state: the app in the browser never needs a reload.

- **Custom storage engine.** The only storage milestone. A bespoke engine
  built ground-up — no SQLite, no Postgres. API TBD; must support data
  filtering at fetch time and be render-coupled: the engine participates in
  rendering to determine exactly what to load, preload, and cache — including
  for custom UIs. Deferred until the renderer has real load patterns to
  couple to.

- **Data-level temporal versioning.** Full history of live data; view the db
  at any past moment. Reshapes storage to never-overwrite. Depends on the
  custom storage engine.

- **Multi-device / distributed runtime + distributed ACID.** The hardest
  part of the mission. An in-process C# lock does not solve cross-machine
  coordination.

- **Desktop wrapper.**

These are real and they stay in the vision. They are simply not next.
