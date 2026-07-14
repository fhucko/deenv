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

That promise has **two equal halves.** *Building* the app is the dev half — no
SQL, no fetch, no endpoints; design data, work with objects (the pillars
below). *Running* it is the devops half: "have it run, without a DevOps degree"
is not a footnote to the mission, it is the other half of it. The devops half
is drawn in full in **STAGES.md** (the self-hosted-image north star) — its
thesis is that operating the system collapses toward a single act.

## Radical liveness — nothing is fixed

The original and enduring ambition is a system (initially an environment for applications, later potentially an operating system itself) in which **anything can be changed at runtime, without restart, in both the OS layer and the applications**. Nothing is permanently fixed ("nic není napevno").

This principle includes **total reducibility**. A running full instance of the system can be transformed live into a completely different, minimal one. For example: from a rich deenv environment running on a conventional host OS (Linux + .NET process) into a primitive system whose only capability is to display the current time and which occupies only a few bytes of memory.

**Irreversibility is acceptable by design.** There is no requirement that every transformation must preserve a path back to a previous state. A radical change can be one-way.

For the foreseeable future the system operates as a powerful **userspace application** on an existing host. Over time the same live-mutability principles can allow it to become (or directly provide) operating-system-level functionality.

When sufficiently powerful constructs are added inside the system — whether through Code, data definitions, or higher-order mechanisms — they can redefine core behaviors of the environment. The rest of the system adapts dynamically to the new reality.

Historically, certain architectural choices (in particular the split between a thin trusted kernel implemented in a host language and a malleable image living in data + Code) were made because full runtime redefinition of the substrate was difficult to envision in detail. With AI as a collaborator and force multiplier, deeper self-redefinition of the runtime itself is now considered reachable in the long term. The kernel/image distinction remains a useful pragmatic description of the current implementation approach, but it is not a permanent limitation on what can be changed live.

This principle of radical liveness informs the pillars, the stages, and the north star. It does not relax the sequencing discipline (see ROADMAP.md and AGENTS.md): near-term work continues to focus on the usable-MVP gates and current milestones, while long-term seams and ambitions are documented here so that they are not accidentally foreclosed.

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

6. **Code.** A purpose-built code layer for an immersive, full experience.
   Starts interpreted — no host platform required. (See DECISIONS.md.)

7. **Multi-device / distributed runtime — full horizontal scale.** The system
   can be configured to run across multiple machines, scaling on ordinary
   VPSes the way cloud infrastructure does — and a *single app's* data can
   shard across those machines with ACID transactions maintained across the
   distribution: per-shard replication and consensus, cross-shard
   transactions, automatic splitting and rebalancing, strong consistency
   throughout (the Spanner/CockroachDB class). *(Upgraded 2026-07-06 from a
   replication-only reading: horizontal write-scaling of one app is in scope,
   not only placement of many apps.)* Correctness at this layer is bought
   **simulation-first** — a deterministic fault-injecting cluster simulator is
   the first brick of the distribution work, because it is what lets
   AI-accelerated implementation of known algorithms be *verified*, not just
   written. (Late milestone — it stays last. This is architecture, not a
   detail. See DECISIONS.md and docs/plans/distributed-acid-design.md.)

8. **UI customization.** Later, users can customize how the UI renders —
   including auto-tabs for deep structures (presenting a large
   single-page form across tabs). A presentation layer over the model, not a
   change to it. (Late milestone.)

9. **An IDE-grade environment.** The whole experience — schema design, code,
   versioning, instancing — lives in one cohesive app, web-first, with a
   possible desktop wrapper later.

## Positioning & sustainability

**What it competes with.** Today's cloud is the incumbent, and for the vast
middle of applications it is both overwrought and overpriced — a sprawl of
managed services, each billed separately, each needing someone who knows it.
deenv's **entry wedge** is that over-served middle: a superior solution for the
small-to-mid database-backed apps that do not need hyperscale, from day one.
The ceiling is no longer scoped out *(rewritten 2026-07-06; the earlier text
renounced hyperscale)*: pillar 7's full form — sharded, strongly-consistent
horizontal scale of a single app — puts genuine high-traffic distributed scale
in scope as the **latest** destination, not a renounced one. The honest form of
the claim is temporal, not categorical: superior for the middle *first*; scale
earned *last*, and only as fast as its correctness can be verified
(simulation-first — pillar 7).

**Free, open, and steward-led.** deenv is **free and open source** under the **MIT
license** — chosen for maximum reach and embedding. The considered alternative was
copyleft (GPL/**AGPL**), which would instead keep the core from being taken closed and
out-proprietarised at some adoption friction; permissive won on reach. (Restrictive
source-available licenses like BSL were never on the table — they are not open source.)
The kernel/image distinction is in any case a natural license boundary today, so apps built on
the current host layer stay the author's own regardless (as the Linux kernel's GPL does not reach
userspace). In the long term the radical liveness principle means even this boundary is not permanently fixed. The creator's role is **steward, not founder** —
the Linus Torvalds / Evan You model: build and maintain the core, funded by
**donations and sponsorship**, with a day job as the runway until (if ever) it
earns the right to go full-time. **AI is the force multiplier** that makes this
solo, side-project path newly viable — the collaborator that lets one steward
build what once needed a team.

**The moat is the community.** The paid layer is *not* the creator's company:
hosting, support, and enterprise services are things **others** may build around
the free core (the Red Hat pattern — companies forming around a free kernel). So
the durable advantage is **neither code secrecy nor hosting** — both are
strip-minable (it is what drove Elastic, Mongo, and HashiCorp to restrictive
licenses) — it is the **community and the network**: the active center, the
contributors, and eventually the ecosystem (the capstone in STAGES.md). The enemy
to fear is **obscurity, not imitation.**

## Honest scope note

This is a multi-year mission. Some pillars — distributed ACID in particular,
now in its full sharded form — represent an industry's worth of engineering.
That is not a reason to abandon them. It is the reason the work must be
**sequenced** (a mission ships in finished pieces, or it ships never) and the
reason the distribution layer is approached **verification-first**: the
deterministic simulation harness precedes the algorithms it must prove. Keep
this document whole; let ROADMAP.md decide order.
