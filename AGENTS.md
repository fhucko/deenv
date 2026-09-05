# AGENTS.md

Canonical project context for AI coding agents. Read this first. Model-specific
entrypoints such as `CLAUDE.md` must only point back here; do not duplicate
project rules there.

## What this project is

A web-first development environment for building database-backed web apps,
where developers design data visually and use it as objects. Near-term work
is sequenced. The long-term mission lives in **VISION.md**; do not treat that
file as the task list.

## Load on demand

Do not `@import` these. Read the matching doc or skill when the task needs it.

| Doc / skill | Use when |
|---|---|
| [VISION.md](VISION.md) | mission / north star, not the task list |
| [ROADMAP.md](ROADMAP.md) | sequenced milestones and what is current |
| [STAGES.md](STAGES.md) | product-stage lens |
| [EXPECTATIONS.md](EXPECTATIONS.md) | slice quality / minimal-by-default criterion |
| [INSTANCE_MODEL.md](INSTANCE_MODEL.md) | types, URL navigation, one-form-per-type |
| [INSTANCE_DESCRIPTION_FORMAT.md](INSTANCE_DESCRIPTION_FORMAT.md) | `instance.app` shape |
| [DECISIONS.md](DECISIONS.md) | architectural precedent |
| [docs/TESTING.md](docs/TESTING.md) | Playwright locators and test patterns |
| skill `build` | isolated worktree build/merge loop |
| skill `design` | design doc + grill |
| skill `fix-tests` | failing tests one at a time |

## Ground rules

1. **Build the current milestone only.** Milestones 1-11, **M-auth**, and the
   **client data layer** are done. Current focus is the usable-MVP gates (see
   below). Cross-machine/multi-kernel, distributed ACID, fault/resource
   isolation, real-time, and the custom render-coupled storage engine stay out
   of scope unless explicitly asked. Schema versioning is M13 and sits on
   instance management.

2. **Later milestones are not later details.** Real-time/multi-user, the custom
   language, the render-coupled storage engine, and the multi-device distributed
   runtime are deliberate future milestones. If a request would pull one in,
   say so and confirm before proceeding.

3. **Project structure is fixed.** One solution, two projects: `DeEnv` (the
   whole environment; the instance lives inside it) and `DeEnv.Tests`
   (Reqnroll Gherkin tests). Must build in Visual Studio 2026. Do not split
   the instance into its own project.

4. **TypeScript only - no hand-written JavaScript.** Author `.ts`; compiled
   `.js` is build output, never edited by hand, gitignored. TS compiles via
   `Microsoft.TypeScript.MSBuild` wired into the `DeEnv` build - no
   `npm install` / `package.json`.

5. **Milestone 1 storage is a plain JSON file**, written by simple rewrite.
   Do not introduce SQLite, Postgres, or ASP.NET MVC for that layer. The HTTP
   layer is a minimal handler (.NET `HttpListener` or minimal-API
   `WebApplication`).

6. **All storage access goes through an interface** - never direct file calls.
   The interface speaks the model's terms (paths, nodes, dictionary entries),
   not flat key-value. This seam lets storage be swapped later.

7. **The instance description is written by hand for now** - no designer, no
   generator. The instance is the only focus.

8. **No custom storage engine yet.** A render-coupled engine is a late
   milestone. For now, plain JSON behind the interface. See DECISIONS.md.

9. **The app document is the authoring surface.** An instance is ONE text file
   (`instance.app`: types + initialData + common/ui code - see
   INSTANCE_DESCRIPTION_FORMAT.md), parsed by `AppParse`/`CodeParse` and
   printed back by `AppPrint`/`CodePrint` (round-trip stable). JSON is
   internal only (in-memory `InstanceDescription`, the wire, storage). Code
   executes on twin C#/TS interpreters kept in lockstep by a shared
   conformance suite; a type checker and editor tooling are future layers.

10. **Watch for hidden scope.** A plain sentence can contain an entire future
    milestone. Split and flag rather than quietly building the big version.

11. **Minimal by default.** Least boilerplate, most defaults, unless
    minimalism would hinder the functionality. Zero-config is the common
    case; write configuration only to deviate. Any flag/opt-in is temporary
    scaffolding to delete. See EXPECTATIONS.md.

12. **Server data reaches Code by RETURNING from a `sys.` call.** When app
    Code needs data the server computes - a schema descriptor, an access
    capability, a structural diff, a publish preview - deliver it as a
    value-returning `sys.` builtin: a server-backed read computed during
    render, memoized, shipped in the client state, and reused by the client
    (a cache miss throws "Value not available" -> refetch). This is the
    `sys.schema` / `sys.canRead` / `sys.diffCommits` / `sys.publishPreview`
    shape - the fn returns the data and the render uses it inline. Do NOT
    surface server-computed data through a mutable `ui var`, an
    ambient/global the framework writes behind Code's back, or an async
    host-action-reply holder. If it looks like it needs a holder because the
    value is produced asynchronously (a host action's reply), first check
    whether it can instead be computed at render time and returned - even
    when the data is cross-instance, by wiring the compute into the render
    path (that is how `sys.publishPreview` reaches another instance's
    committed data through a kernel-supplied delegate). Reserve a
    client-only holder (`ctx.status` / `ctx.conflicts` - client-only, C#
    twin returns the empty constant, no conformance case) ONLY for state
    that is genuinely push/async and cannot be a render-time return. Host
    actions (`sys.publish`, `sys.commitDesign`, ...) remain fire-and-effect
    and return nothing; reads return values.

13. **Client/server mutation model - the `commit` is the only persistence
    op.** Every server-side save goes through `ctx commit`; the client never
    persists via a live op. User Code sees ONLY plain `commit` (the form's
    `ctx.commit`), never `beginCommit`/`endCommit` (runtime-internal
    bracket primitives). `remove` detaches; deletion is GC's call, never
    guaranteed - there is no `delete` operation. Every wire op mirrors a
    model op (no parallel vocabulary). Full ground rules + rationale:
    DECISIONS.md "Client/server mutation model - ground rules".

## Testing

Behavior is specced in Gherkin `.feature` files in `DeEnv.Tests\Features`.
Every scenario is tagged with its milestone (e.g. `@milestone-1`,
`@milestone-future`). When working a milestone, filter to that tag and
ignore the rest. Split any scenario whose lines belong to different
milestones. A `@milestone-1` scenario must be passable with the milestone-1
stack (plain JSON, minimal HTTP handler) - if it cannot pass, it is
mis-tagged, not a reason to expand the stack.

Test stack (settled - do not change it): **Reqnroll running on TUnit.** Do
not switch to NUnit/xUnit, do not re-wire Reqnroll, do not introduce a
different assertion library. All step definitions must fit that setup.
Browser-facing steps, when reached, use Playwright. See
[docs/TESTING.md](docs/TESTING.md).

## Current focus

Usable-MVP gate #3 (dogfood one real app) is in progress. M13 core is
complete. M12 (visual component designer) stays deferred until after the
MVP. Details: [ROADMAP.md](ROADMAP.md) and [DECISIONS.md](DECISIONS.md).
