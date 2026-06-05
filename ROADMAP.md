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
the versioning diffs, the language eventually compiles against.

Done when: instances are defined by a validated JSON schema document.

## Milestone 4 — The visual schema designer

A web canvas to create tables, columns, and relationships visually,
outputting the Milestone 3 JSON document.

Done when: a user can design a schema with no code and run it.

## Milestone 5 — Schema versioning

Git-style versioning by snapshotting the JSON schema document and diffing
snapshots.

Done when: schema changes can be saved as versions and diffed.

## Milestone 6 — Real storage engine (interim)

Move storage off the plain JSON file onto SQLite or Postgres — gaining
durability, crash recovery, indexing, and isolation without building them.
Swapped in behind the storage interface from Milestone 1. The custom
object-oriented data layer sits on top.

Done when: instances persist through a proper ACID engine, single machine.

## Milestone 7 — Render-coupled storage engine

The custom storage engine co-designed with the renderer (Vision pillar 5).
It uses knowledge of what the UI is about to render to drive loading and
preloading. Swapped in behind the same storage interface.

Why here and not earlier: a render-aware engine needs (a) a *correct*
storage layer to build on — correctness before cleverness — and (b) a
renderer rich enough to have interesting load patterns (lists, relationships,
views). Neither exists until the milestones above are done. See DECISIONS.md.

Done when: loading/preloading decisions are driven by render intent.

---

## Future milestones (NOT scoped — do not build yet)

- **Real-time / multi-user.** "Other users see a notification" — push, live
  sync, presence. Concurrency handled in C# (single server). Many milestones
  away.
- **Custom language.** Waits until there is a platform to host it.
- **Multi-device / distributed runtime + distributed ACID.** The hardest
  part of the mission. Architecture, designed for when reached — not bolted
  on. An in-process C# lock does NOT solve cross-machine coordination.
- **Predictive prefetching + client-side caching.** Loading data the
  current view implies you'll visit next. This is VISION pillar 5 (the
  render-coupled engine) arriving early — keep it out of early milestones.
- **Data-level temporal versioning.** Full history of live data; view the db
  at any past moment. Reshapes storage to never-overwrite. Data-level
  counterpart to schema versioning (Milestone 5). Late — changes the storage
  foundation; the storage interface seam keeps it possible.
- **UI customization + auto-tabs.** User-controlled rendering; auto-tabbing
  deep single-page forms. Presentation layer over the model.
- **Desktop wrapper.**

These are real and they stay in the vision. They are simply not next.
