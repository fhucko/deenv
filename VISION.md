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

## Who uses this

There is **one operator** — the person driving the tool. They must be
*technically literate*, but they need not be a professional developer. The
dividing line is **data-model literacy**: the operator understands entities,
fields, types, and relationships ("this links to that"). They are *not*
assumed to write app code, configure servers, or write SQL. Templates and
(later) AI domain expertise lower that floor over time, but do not remove it.

Two operator profiles:

- **Professional developers** — indie hackers on their own product,
  freelancers, agencies.
- **Technically-minded non-developers** — sysadmins, data analysts, power
  users, technical founders, ops people. This group is the heart of the
  "without a DevOps degree" promise: not being a full developer should not
  stop you from getting a real database-backed app running.

Common situations the single operator is in:

1. **Solo / own product** — building a SaaS or side project alone. Wants
   speed and to not fight infrastructure.
2. **Freelancer + client, live design** — the operator drives the tool while
   a client supplies domain knowledge across the table (e.g. designing a
   custom ERP for an eshop backend on the spot). The client never touches the
   tool, so the design must be *legible enough to narrate*. Templates and
   domain hints earn their keep here.
3. **Internal / business tooling** — an admin panel, ops dashboard, or
   workflow tool for the operator's own team. Requirements shift once people
   use it, so cheap schema iteration matters more than upfront polish.
4. **Agency / repeat patterns** — the same shapes (eshop, booking, CRM,
   inventory) rebuilt per client with a ~20% twist. Templates are the
   economic argument, not a nicety.
5. **Prototype / MVP** — throwaway-or-grow validation; get a real running app
   fast, harden later if it survives.

**Where code fits.** The operator designs *data*, not code. When code is
needed, a developer writes it. Later, AI assistance lets the
non-developer operators (e.g. sysadmins) produce that code too, with the
option of **developer review** before it ships. The operator stays
data-model-literate; the coding burden moves from "developer only" to
"AI-assisted operator, developer-reviewed" over time.

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
