# DeEnv

> A web-first development environment for building database-backed web apps —
> **design your data visually, work with it as objects, and have it run,
> without a DevOps degree.**

*This README leads with the destination. For where the project actually is right
now, jump to [Current state](#current-state).*

---

## The vision

DeEnv's bet is that building **and running** a database-backed web app should not
require a DevOps degree, a sprawl of managed cloud services, or hand-written SQL,
`fetch` calls, and API endpoints. You design *data* — entities, fields, types,
relationships — and work with it as plain objects. Everything between the object
and the database is generated and hidden.

The mission has **two equal halves**:

- **Building** — no SQL, no `fetch`, no endpoints. Design data, write logic
  against objects: `db.tasks.where(t => !t.done)`. The network round-trip still
  happens — it's a web app — but the platform generates and hides it.
- **Running** — operating the system collapses toward a single act: place an
  image, start it once, and it runs. "Without a DevOps degree" is half the
  mission, not a footnote.

The full destination — the pillars, drawn in [VISION.md](VISION.md):

- **Visual data design.** Design the schema on a canvas; the schema is the spine
  of the whole system.
- **Objects, not server calls.** Object code in user space; the round-trip is
  generated and hidden.
- **Git-style schema versioning.** Snapshot, diff, and branch the schema —
  renames exact, because every entity carries identity.
- **Data time-travel.** The live data versioned with full history: view the
  database as it was at any past moment.
- **A render-coupled storage engine.** A storage engine co-designed with the
  renderer, so it knows what the UI is about to display and loads accordingly —
  something a general-purpose engine structurally cannot do.
- **A distributed runtime.** Configured to scale across ordinary machines —
  up to sharding a single app's data across them, with ACID transactions and
  strong consistency maintained across the distribution.
- **One IDE-grade environment.** Schema design, code, versioning, and instance
  management in a single web-first app.

Its entry wedge is the **over-served middle**: the small-to-mid apps today's
cloud makes overwrought and overpriced — booking tools, CRMs, work-order
trackers, inventories, small shops. Genuine horizontal scale is the mission's
latest destination, not its first claim.
The operator must be *data-model literate* (they understand "this links to that")
but need not be a professional developer.

This is a real, sequenced mission, built in finished pieces — which brings us to
where those pieces are today.

## Current state

**An early, single-operator MVP — built solo, AI-assisted, in the open.** It runs
real apps end-to-end: you hand-author an app and it persists and reacts. It is
**not yet production-ready** — single-user, **no authentication**, plain-JSON
storage.
Schema versioning, real-time / multi-user, the custom storage engine, and the
distributed runtime are still ahead.

### What works today

- **Visual data design** — a self-hosted schema *designer* (itself one of the
  apps DeEnv hosts) to edit types and properties as data, then publish.
- **A real object model** — intrinsic identity, per-type extents, **references**
  (shared identity, no ownership), **sets** keyed by identity, and
  **dictionaries** for genuine maps. Lifetime by garbage collection from the root.
- **Reactive UI** — server-rendered first paint, then the client hydrates and
  takes over (identity-keyed DOM reconciliation, two-way binding). Mutations
  persist optimistically over a WebSocket with a local change journal and
  rollback on reject.
- **Code** — user logic and UI in a small purpose-built language, run on **twin
  C# / TypeScript interpreters** kept in lockstep by a shared conformance suite.
- **Multi-instance kernel** — one process hosts many apps at once, each with its
  own sovereign data, addressed by path (`/apps/<name>`).
- **Non-destructive schema changes** — your data survives an added field, a
  dropped field, a scalar type conversion, or a single→set reshape.

Milestones 1–11 are done; [ROADMAP.md](ROADMAP.md) sequences the rest.

## An app is one text file

A whole instance — its **types**, an optional **seed**, and optional **code** — is
described by one document (`instance.app`):

```
types
    Db
        tasks set of Task
    Task
        title text
        done bool
        priority Priority
    Priority enum
        low
        high
```

That's a complete, runnable app. With **no UI code at all**, DeEnv's *self-hosted
generic UI* renders it: object forms, set tables, reference pickers, enum
dropdowns — the **minimal-by-default** common case is zero configuration.

When you want full control, add a `ui` section and own the whole screen with a
reactive render function:

```
ui
    fn render()
        return <main>
            <h1>
                "Tasks"
            foreach task in db.tasks
                <label class="task">
                    <input type="checkbox" checked={task.done}>
                    task.title
```

The UI is exactly **two modes** — fully *auto* (the generic UI) or fully *custom*
(`fn render()`) — and the auto UI is itself written in DeEnv's own code as a
library, so a "generic" app is literally a custom-render app.

## Architecture highlights

A few things that make DeEnv unusual under the hood:

- **Twin interpreters + a conformance suite.** The same Code AST executes on a C#
  server interpreter and a TypeScript client interpreter; a shared conformance
  test pins them to identical behavior.
- **Self-hosting UI.** The generic auto-UI is re-expressed in DeEnv's own Code as
  a component library — the platform building its own front door. The per-URL
  routing is one synthesized render function composing that library.
- **Structural privacy via a memo cache.** Every computation boundary ships with
  its *dependency references* — never its input values — so data read only inside
  a computation never leaves the server, with no annotations. The first paint
  never calls the server back.
- **Storage behind a model-shaped seam.** All storage goes through an interface
  that speaks the model's terms (paths, nodes, entries), never flat key-value or
  the file API. It's plain JSON today; the seam is what lets a render-coupled
  engine replace it later without touching the rest.

## Tech stack

- **.NET 9 / C#** — the kernel, interpreters, storage, and HTTP layer
  ([GenHTTP](https://genhttp.org/)).
- **TypeScript** — the client runtime, compiled via `Microsoft.TypeScript.MSBuild`
  as part of the build. **No `npm`, no `package.json`** — authored as `.ts`,
  compiled `.js` is build output.
- **Reqnroll + TUnit** (on Microsoft.Testing.Platform), with **Playwright** for
  browser-facing scenarios. Behavior is specced in Gherkin `.feature` files.
- Builds in **Visual Studio 2026** (one solution, `DeEnv.slnx`).

## Getting started

Prerequisites: the **.NET 9 SDK**. (TypeScript is compiled automatically by the
build — nothing to install.)

```bash
# Build
dotnet build DeEnv.slnx

# Run the kernel host (serves every app in DeEnv/kernel.json)
dotnet run --project DeEnv
```

Then open the hosted apps:

- `http://localhost:8080/apps/designer/` — the schema designer
- `http://localhost:8080/apps/todo/` — the todo example
- also `/apps/crm/`, `/apps/shop/`, `/apps/devlog/`

Run the test suite:

```bash
dotnet test DeEnv.Tests
```

Deploying to a server (self-contained build + systemd + nginx) is documented in
[`deploy/DEPLOY.md`](deploy/DEPLOY.md).

## Repository layout

```
DeEnv/            The environment: kernel host, twin interpreters, storage, HTTP
  Code/           The Code language — parser, printer, interpreter, conformance
  Instance/       The self-hosted UI runtime (TypeScript) + generic UI
  Kernel/         The multi-instance kernel host + registry
  Storage/        The storage interface and the JSON implementation
  instances/      One hand-written app document per hosted instance (app.deenv)
  kernel.json     The registry: which instances run, and on which ports
DeEnv.Tests/      Reqnroll Gherkin features + step definitions
docs/             Plans and domain reference notes
deploy/           Deployment runbook + systemd unit
```

## Learn more

The design is documented in depth. Good entry points:

- [`VISION.md`](VISION.md) — the full long-term mission (the nine pillars).
- [`ROADMAP.md`](ROADMAP.md) — the sequenced milestones (what's built, what's next).
- [`STAGES.md`](STAGES.md) — the product-stage lens, from MVP to the distributed vision.
- [`DECISIONS.md`](DECISIONS.md) — key architectural decisions and their reasoning.
- [`INSTANCE_DESCRIPTION_FORMAT.md`](INSTANCE_DESCRIPTION_FORMAT.md) — the `instance.app` format.

## Philosophy

DeEnv is **free and open source**, built and maintained by a single **steward**
(the Linus / Evan You model) and funded by a day job — with AI as the force
multiplier that makes a solo, multi-year build viable. The durable advantage is
meant to be the **community**, not code secrecy or hosting.

**License:** released under the [MIT License](LICENSE).
