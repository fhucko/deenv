# Decisions

Key decisions and *why*. Future-you needs the reasoning, not just the
conclusion.

## HTTP layer: GenHTTP (pure-C# embeddable server), not ASP.NET Core

**Milestone 1 originally used minimal-API `WebApplication` (Kestrel/ASP.NET
Core).** It was swapped for **GenHTTP** (`GenHTTP.Core` + modules, v10.5.x), a
lightweight embeddable web server written in pure C# with no ASP.NET Core
dependency. Reason: ASP.NET Core was judged too heavy for what the instance
needs — three routes and a WebSocket.

This is a conscious deviation from CLAUDE.md ground rule 5 (which named
`HttpListener` or minimal-API `WebApplication`). GenHTTP was chosen over raw
`HttpListener` because it gives a real handler/routing/content API while
staying light, and its **native WebSocket module** (not the legacy Fleck one)
keeps the dependency surface small.

Key implementation notes:
- **WebSocket uses `Websocket.Functional()`** (GenHTTP's native impl), NOT
  `Websocket.Create()` (legacy, Fleck-based, obsolete, removed in GenHTTP 11).
  Functional avoids pulling in Fleck.
- **Raw UTF-8 frames** are read/written directly (`frame.Data` /
  `frame.Connection.WriteAsync(bytes, FrameType.Text, true, ct)`) so the JSON
  payload goes on the wire verbatim — GenHTTP's typed `WritePayloadAsync<T>`
  would JSON-wrap a string and break the client's `JSON.parse`.
- **The transport is request/response**: one inbound text frame →
  `WsHandler.ProcessMessage(string)` → one outbound text frame. `WsHandler` is
  transport-agnostic (no raw-socket loop), so the same dispatch logic would
  survive another transport swap.
- **`.Defaults(secureUpgrade: false, strictTransport: false)`** — we serve
  plain HTTP; leaving secure-upgrade on risks HTTP→HTTPS redirects.
- **Client JS is embedded** as an assembly resource
  (`DeEnv.Instance.instance.js`), served at `/js/instance.js`. No `wwwroot`,
  no static-file middleware, no test-side file copying — the JS rides inside
  `DeEnv.dll`, so the in-process test host serves it for free.
- The handler tree is built once in `InstanceApp.Build(...)` and shared by
  both the real host (`Program.cs`) and the test host (`TestInstanceServer`).

The storage-interface seam is unaffected; this is purely the transport layer.

## Storage: plain JSON file now, real engine later, render-coupled engine eventually

**Milestone 1:** storage is a plain JSON file, written by simple rewrite.
No framework. The HTTP layer is a minimal handler (.NET `HttpListener` or
minimal-API `WebApplication`) — no ASP.NET MVC weight.

This is adequate *only because* Milestone 1 is single-user: one writer, no
contention. Two things are consciously deferred:
- **Crash durability** — a plain rewrite can leave a half-written file if
  the process dies mid-write. Accepted for now (hardcoded demo instance).
  The cheap fix when wanted: write-to-temp-then-atomic-rename.
- **Isolation (the I in ACID)** — concurrent-writer safety. Not needed until
  the multi-user milestone.

**Critical seam:** all storage access goes through an interface, never
direct file calls. Principle: the interface speaks the **model's terms —
paths, nodes, dictionary entries — not flat key-value** (key-value is the
wrong vocabulary for navigable nested data, and a thicker model-shaped
boundary is what later lets temporal/render-coupled engines express
operations like "node at path P as of time T"). Exact signatures are TBD in
code — propose them in plan mode against something concrete. This interface
is what makes every later storage swap safe.

**Milestone 6 — interim real engine:** move to SQLite or Postgres, gaining
durability, crash recovery, indexing, and isolation for free rather than
building them. Swapped in behind the storage interface.

**Milestone 7 — render-coupled engine (Vision pillar 5):** a custom storage
engine *is* a real goal — but a specific kind: one co-designed with the
renderer, using knowledge of what the UI will render to drive load/preload.
That coupling is the value; a general-purpose engine cannot do it. It is a
*late* milestone for two reasons:
- Correctness before cleverness. The hard parts (durability, recovery,
  indexing, concurrency) don't get easier for being render-coupled. The
  smart loading layer assumes correct storage underneath it already exists.
- It needs a renderer to couple to. With a one-boolean UI there is nothing
  to be smart about. The engine is only *designable* once the renderer has
  real load patterns (lists, relationships, views).

## Rendering: SSR first paint, then client-side, prefetch deferred

TypeScript's role is rehydration, client-side rendering after first paint,
and interaction handling. The chosen architecture, staged:

- **Milestone 1:** server (C#) renders full HTML on first paint (real pages,
  work without JS, literal URL navigation), then TS takes over for
  client-side navigation, fetching each node's data as JSON and rendering it.
  C# therefore serves both HTML and a JSON data endpoint per node. This is
  the heavier JSON contract (not light data-attribute rehydration) because
  client-side navigation needs node *data*, not just markup.
- **Far future:** predictive prefetching and client-side caching are
  deferred. "Prefetch what the current view implies you'll visit" is exactly
  VISION pillar 5 (the render-coupled storage engine) — so prefetch is that
  pillar arriving early and must NOT leak into Milestone 1. Get SSR-first-
  paint + client-nav working first; predictive loading is an optimization on
  top of navigation that doesn't exist yet.

Client-side caching note: once the client caches fetched nodes, the
single-machine consistency question reappears in miniature (an edited node
may also be cached inside another view). Tractable while single-user; the
cache is client state we own. Revisit when caching is actually built.

## Concurrency, saving, and locking (eshop/CRM)

Primary early use cases are custom eshop and CRM — multi-user domains where
records are edited concurrently. Decisions:

- **Entities carry a version stamp from Milestone 1 onward.** It increments
  on each save. Nothing reads it yet (Milestone 1 is single-user, so no
  conflicts are possible), but it is the exact hook optimistic concurrency
  needs later. Cheap now; adding it later would mean migrating existing data.
  Same "cheap seam now, swap later" discipline as the storage interface.

- **Concurrency model is optimistic, designed at the multi-user milestone.**
  No locking by default. Users edit freely; conflicts are detected at *save*
  time by comparing the version the user started from against the stored
  version. If they differ, the save is rejected and the user sees a
  conflict-resolution prompt ("this record changed since you opened it").
  Pessimistic / user-facing entity locks are avoided — in a web app they
  leak (dead sessions), need fragile timeouts, and frustrate users exactly
  on the busy records that matter. True locks are reserved only for specific
  operations that genuinely require exclusivity.

- **Quantity transactions (eshop stock) are a backend concern, not a UI
  save concern.** Decrementing stock at checkout wants atomic
  "decrement-if-available" operations at the storage layer, not form-level
  locking or last-write-wins. Lands in the storage-engine milestones.

- **Save UX:** toggles (bool checkbox, "active" flags) save immediately in
  the background. Multi-field object forms use explicit Save, which pairs
  with the optimistic stale-version flow above.

None of the multi-user concurrency machinery is built in Milestone 1 — only
the version stamp exists now. The rest is designed when multi-user arrives.

## Multi-user is handled in C#; multi-device is NOT the same thing

Within one process, C# can make multiple users safe — a lock around the
read-modify-write. That covers the multi-user milestone on a single server.

It does NOT coordinate across machines: separate processes don't share a
lock. Multi-device coordination is a separate, harder problem. Do not let
"C# handles synchro" quietly absorb the multi-machine case.

## Multi-device is architecture, not an implementation detail

An implementation detail can be changed later with nothing else affected.
Whether the system runs on one machine or many changes what the data layer
can promise, whether reads are synchronous, what versioning diffs, what
"instancing" means — everything above it depends on it. That makes it a
*foundation*, and foundations cannot be deferred into an existing design.
It is therefore a deliberate future milestone, designed for when reached.

Distributed ACID specifically: the CAP theorem means you cannot have full
ACID + always-available multi-device + tolerance of network failure. Systems
that keep ACID while distributed (Spanner, CockroachDB) run consensus
protocols and are the work of large specialist teams.

## Conflict handling: two different problems

- **Infrastructure conflict** (server replicas disagree) — *can* be hidden
  from the user, but only because the platform pays for it in engineering.
- **Application conflict** (two humans edited the same record) — *cannot* be
  hidden; surface a conflict-resolution dialog. This part is the easy 10%.
The invisible 90% is making replicas agree well enough that the dialog only
fires for genuine human collisions. Roadmap.

## "No server calls in user code" = a custom data access layer, not magic

The network call still happens — it's a web app. The platform generates and
hides it. The user writes object code; the transport is the platform's job.
Generated APIs are `async` where an operation genuinely cannot be
synchronous — pretending otherwise creates worse bugs later.

## Custom language waits

A custom language is not just a parser — it's a type checker, runtime,
debugger, standard library, editor tooling, and docs. Building it now also
means no existing ecosystem helps. Early milestones use **TypeScript** for
generated code, inheriting its editor and debugger for free. The custom
language is designed later, once there is a platform to host it and real
users to inform it.

## Tool stack and project structure

Web-first: **C# backend, TypeScript front-end.** C# stays where it's strong;
the browser-side UI is TypeScript.

**One solution, two C# projects:**
- `DeEnv` — the whole environment, buildable in Visual Studio 2026. The
  instance lives inside this project for now (not separate).
- `DeEnv.Tests` — Gherkin tests via Reqnroll on TUnit, C# step definitions.

**TypeScript:**
- Only TypeScript is authored. **No hand-written JavaScript.** Compiled
  `.js` is build output — never edited, gitignored.
- TS is compiled via the **`Microsoft.TypeScript.MSBuild`** NuGet package,
  wired into the `DeEnv` build. It bundles the TS compiler, so no
  `npm install` / `package.json` is needed.
- Caveat to verify: the package bundles the compiler but needs something to
  *run* it. On a normal VS2026 web-workload install this very likely works
  with nothing else. "Fully buildable on a clean machine" is a claim to
  TEST, not assume — do a fresh-machine build and record any real
  prerequisite (e.g. a JS runtime) here once known.

Early milestones avoid heavyweight frameworks — minimal HTTP handler, not
full ASP.NET MVC. Desktop wrapper is a later option.

VERIFY-ON-CLEAN-MACHINE prerequisite: __________ (fill in after testing).

## Testing: BDD with Gherkin

Behavior is specced in Gherkin `.feature` files first, then made to pass.
Every scenario is tagged with its milestone (`@milestone-1`,
`@milestone-future`, etc.). A scenario whose lines span milestones is split.
A milestone-1 scenario must be passable with the milestone-1 stack — if it
isn't, it is mis-tagged. Green milestone-1 scenarios are the "is v1 done?"
signal; that signal only works if the tagging is honest.
