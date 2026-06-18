# Roadmap

The mission (see VISION.md) is large. This document sequences it into
finishable milestones. **A milestone is done when it works and is usable —
not when the code exists.**

Ground rule: build the current milestone only. Later milestones are out of
scope until the current one is finished. Sequencing is not the opposite of
ambition — for a mission this size, it is the only way the ambition ever
becomes real.

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

---

## Future milestones (NOT scoped — do not build yet)

- **Code, next layers.** A full type-checker (today: structural validation);
  derived-collection mutation semantics; dictionaries surfaced to the Code
  runtime; editor tooling. Enables schema versioning to be built inside the
  environment.

- **Self-hosted generic UI.  ← DONE (2026-06-14).** The auto-form experience is
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

- **M11 — SolidJS-style reactive components + the public component library (the UI
  middle-ground).  ← ACTIVE; reactivity-foundation FIRST SLICE DONE 2026-06-18.** *(Scheduled as
  M11 by user decision 2026-06-16, pulled ahead of schema versioning, which moves to M13.)*
  **First slice landed (suite 308):** components get a **render-tree-positional ("slot path")
  identity** decoupled from the argument-keyed memo, so a component runs once per slot and its
  state survives a re-render with rebuilt arguments. **Recognition = pure name-resolution** (a tag
  whose name is an in-scope function — any function, top-level or local — is a component; `<div>`
  stays an element), keyed by slot via the **existing** memo (untouched, additive). Run-once-across-
  re-renders is a client behavior (C#'s `Memoize` is write-only → server renders once), proven by
  the `@milestone-11` Gherkin scenario; a new unified `setup + renders[]` conformance protocol
  proves the deterministic core (recognition, by-name binding, splice, local-component capture,
  sibling slot-key uniqueness) on both twins. Remaining follow-ups: lists/keys (the `foreach` row
  key; `<For>`/`<Index>`), an explicit per-call `key`, **dissolving `__descs`** (follow-up 4 — the
  payoff across every app), then the **public component library** + generic-UI-as-first-consumer
  (the feature half). See `docs/plans/m11-reactivity-foundation.md`. Delivers pillar 8's "auto with
  overrides" (modify/extend
  *parts* of the generic UI) via the mechanism settled in DECISIONS ("UI middle-ground"):
  **one public component library** (`ObjectForm`/`Field`/`SetTable`/…) that BOTH a custom `fn
  render()` and the generic UI compose — the generic UI rewritten as the library's **first
  consumer** (its own completeness proof) — with **SolidJS-style reactivity** (run-once
  components, no reset on prop change, parent-controlled structural reset; the `__descs`
  reference-stability dissolved into the interpreter). Supersedes the earlier "call the
  `internal` `objectForm`/`field` directly" sketch (an internals-leak, rejected). **Foundation
  first:** the reactivity refactor (a twin-interpreter change; stands alone; kills the
  `__descs` fragility) before the public library on top. *What's left:* the M9 generic UI is
  already a Code library, but lives in `internal` with the fragile descriptor model — so the
  work is the reactivity refactor + promoting it to a clean public API. Decompose via
  milestone-planner; a vision-keeper pass is worth it (this reorders versioning).
- **M12 — Visual component designer.** A WinForms/XAML-style visual designer over the M11
  public component library: drag/arrange/configure components on a canvas, **show-all** (the
  canvas a synced view of the full `fn render()`; the M7 round-trip printer is the visual↔text
  sync engine), **live preview = the Stage-2 inner-loop mini-instance** (the real interpreted
  renderer — no design/runtime divergence), and the native paren-free **`for … in`** keyword
  desugaring to declarative keyed iteration (the XAML `ItemsControl`/`DataTemplate` role).
  Extends pillar 1 (design visually) from data to UI. Needs M11 + the Stage-2 live-preview
  infra. See DECISIONS ("UI middle-ground → Visual component designer").

- **Multi-instance management (single-process, single-operator).  ← M10, first five
  slices DONE 2026-06-14.** One kernel process **hosts multiple instances at once**,
  each on its own port pair with its own sovereign data, driven by an **instance
  registry** (which instances exist + their ports) as **kernel-owned data**. The
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
  instance and `sys.create(schema, name, appPort, infraPort)` spawns a new one — both project a passed
  schema object (carried by its id; the designer's `Db { types }` meta-schema is unchanged).
  Then the **operator designer + ops**: `designer.app` (now `instances/1/app.app`) gained a HAND-ROLLED
  custom `fn render()` (a type/prop editor + the `sys.instances` list + per-instance
  create/clone/delete/publish controls), replacing its auto generic UI — explicit image Code, NOT a
  hidden callable designer (the compose path is rejected). The ops: `sys.delete(id)`,
  `sys.cloneInstance(sourceId, ports)` (copies app doc + data), per-instance `sys.publish(db, id)`.
  Underpinned by a **uniform id-based instance identity model**: every instance has a stable unique int
  id; storage is fully id-based (`instances/<id>/`); the registry `app` field is a display NAME label
  (used for nothing functional, no `.app`); the boot-vs-created distinction is removed (ops work on any
  instance by id). **Named create + rename then completed the operator flow** (the create form takes a
  display name → `sys.create(schema, name, appPort, infraPort)`; a per-instance Rename → `sys.rename(id,
  name)` edits the registry label). Remaining: richer editing. See DECISIONS.md ("Operator instance ops +
  the id-based instance identity model").

  **Kernel discipline:** the kernel gains the *mechanism* (host N instances, bind
  ports, hold the registry) — **not** the management *experience*. Create/list/
  switch/delete as the IDE are **image Code** (later slices); a C# admin panel would
  be the M4 mistake (a one-off the self-hosted IDE later tears out).

  **Deferred (kept out to stay single-process / single-operator):** cross-machine /
  kernel-to-kernel connectivity + distributed ACID (the *Multi-device* pillar below,
  Stage 5); fault/resource isolation between instances (Stage 5); real-time/multi-
  user; dynamic create/destroy-while-running and the management commands (follow-up
  slices); promoting the registry to a real *restricted* kernel-instance (north
  star). See STAGES.md + DECISIONS.md ("Multi-instance management — the kernel host").

- **Schema versioning  (sits on multi-instance management — now M13, after the UI milestones M11–M12).**
  Git-style versioning of the schema, built inside the environment itself using
  the code milestone (versioning is behaviour-shaped). The structural
  identity-based diff is already designed — renames are exact because
  non-constants carry identity (Milestone 5).

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
