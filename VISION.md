# Vision

This is the long-term mission. It is intentionally large. Nothing here is
cut. This document is the destination, not the next task — see ROADMAP.md
for what is actually being built now.

## The mission

A development environment for building database-backed web applications,
where the developer designs data visually and works with it as objects —
never hand-writing SQL, fetch calls, or API endpoints.

The goal is to make building and running a database-backed app approachable
for developers who find today's cloud overwhelming: design an app, and have
it run, without a DevOps degree.

## Pillars of the full vision

1. **Visual data design.** Design a database schema on a canvas — tables,
   columns, types, relationships — without writing code. The schema is the
   spine of the whole system.

2. **Object-oriented data access — no server calls in user code.** The
   developer writes object code (e.g. `project.Tasks.Where(t => t.Done)`).
   The network call still happens — it must, it's a web app — but the
   platform generates and hides it. The user's mental model is pure objects.

3. **Git-style versioning (schema).** Schema/type-definition changes are
   versioned: snapshots, diffs, branching. Built on diffing a declarative
   schema document, not on reimplementing Git.

4. **Data-level temporal versioning.** The live data itself is versioned
   with full history, so the db can be viewed as it was at any past moment
   ("time travel"). This is the data-level counterpart to pillar 3 (which
   versions the schema): pillar 3 versions structure, this versions values
   over time. Reshapes storage into never-overwrite / full-change-history.
   (Late milestone — it changes the storage foundation, so it comes only
   after the foundation is proven. The storage interface seam keeps it
   possible. See DECISIONS.md.)

5. **A render-coupled storage engine.** Eventually the instance has its own
   custom storage engine, *co-designed with the renderer*. Because it knows
   what the UI is about to display, it can decide what to load and preload
   in ways a general-purpose engine structurally cannot. The coupling is the
   value — this is not reinventing SQLite, it is doing something existing
   engines cannot. (Late milestone — see ROADMAP.md and DECISIONS.md.)

6. **Custom language.** A purpose-built developer language for an immersive,
   full experience. (Late milestone. Waits until there is a platform to host
   it. See DECISIONS.md.)

7. **Multi-device / distributed runtime.** The system can be configured to
   run across multiple machines, scaling on ordinary VPSes, the way cloud
   infrastructure does — ACID maintained across that distribution.
   (Late milestone. This is architecture, not a detail. See DECISIONS.md.)

8. **UI customization.** Later, users can customize how the UI renders —
   including auto-tabs for deep structures (presenting a large
   single-page form across tabs). A presentation layer over the model, not a
   change to it. (Late milestone.)

9. **An IDE-grade environment.** The whole experience — schema design, code,
   versioning, instancing — lives in one cohesive app, web-first, with a
   possible desktop wrapper later.

## Honest scope note

This is a multi-year mission. Some pillars — distributed ACID in particular —
represent an industry's worth of engineering. That is not a reason to
abandon them. It is the reason the work must be **sequenced**: a mission
ships in finished pieces, or it ships never. Keep this document whole; let
ROADMAP.md decide order.
