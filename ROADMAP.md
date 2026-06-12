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

## Milestone 7 — Code text syntax (parser + printer)  ← CURRENT

A C#/TSX-like text form for Code, replacing hand-written AST JSON as the
authoring surface (the AST remains the in-memory and wire form — the client
still receives AST; no TS parser). Indentation-based blocks, JSX-like tags,
expression precedence — `app.txt` style, ported from the prototype's
combinator parser. Code lives in a sidecar file referenced by the schema
document (`"codeFile"`); the loader parses it into the same validated AST,
and inline `ui`/`common` JSON is retired. The printer (AST → canonical text)
ships too, with round-trip tests — the designer will need it to display code.

Done when: the todo app's code is authored as `instance.code` text, the whole
existing suite stays green on the parsed AST, and parse/print round-trips are
stable. Plan: code-text-syntax.md.

---

## Future milestones (NOT scoped — do not build yet)

- **Code, next layers.** A full type-checker (today: structural validation);
  derived-collection mutation semantics; dictionaries surfaced to the Code
  runtime; editor tooling. Enables schema versioning to be built inside the
  environment.

- **UI customization.** User-controlled rendering, powered by code.

- **Schema versioning.** Git-style versioning of the schema, built inside
  the environment itself using the code milestone (versioning is
  behaviour-shaped). The structural identity-based diff is already designed —
  renames are exact because non-constants carry identity (Milestone 5).

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
