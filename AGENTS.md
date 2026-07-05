# AGENTS.md

Canonical project context for AI coding agents. Read this first, then the
linked files. Model-specific entrypoints such as `CLAUDE.md` must only point
back here; do not duplicate project rules there.

## What this project is

A web-first development environment for building database-backed web apps,
where developers design data visually and use it as objects. Full mission in
**VISION.md**.

## Read these before working

- **VISION.md** — the full long-term mission. The destination. Do not treat
  it as the task list.
- **ROADMAP.md** — the sequenced milestones. This *is* the task list.
- **STAGES.md** — the product-stage lens on the mission (Stage 1 =
  single-operator MVP, settled; later stages + the self-hosted-image north
  star). Points at VISION pillars / ROADMAP milestones rather than restating
  them.
- **EXPECTATIONS.md** — what "good" looks like: project structure, build
  rules, and the qualities a slice must meet beyond just running.
- **INSTANCE_MODEL.md** — the instance type system, URL-as-navigation, and
  the one-form-per-type rendering rule.
- **INSTANCE_DESCRIPTION_FORMAT.md** — the canonical JSON shape of a
  hand-written instance description, with rules and worked examples.
- **DECISIONS.md** — key architectural decisions and their reasoning.

## Ground rules — important

1. **Build the current milestone only.** Milestones 1–11 + **M-auth** (access
   control) + the **client data layer** are **DONE** — ROADMAP.md is the
   sequenced record, DECISIONS.md the reasoning. **Current focus = the
   usable-MVP gates** (see "Current focus" below). Cross-machine/multi-kernel +
   distributed ACID, fault/resource isolation, real-time, and the custom
   render-coupled storage engine stay out of scope unless explicitly asked.
   Schema versioning is M13 (after the UI milestones); it sits on instance
   management.

2. **Later milestones are not "later details."** Real-time/multi-user, the
   custom language, the render-coupled storage engine, and the multi-device
   distributed runtime are deliberate future milestones, not things to bolt
   on now. If a request would pull one into current work, say so and confirm
   before proceeding.

3. **Project structure is fixed.** One solution, two projects: `DeEnv` (the
   whole environment; the instance lives inside it) and `DeEnv.Tests`
   (Reqnroll Gherkin tests). Must build in Visual Studio 2026. Do not split
   the instance into its own project.

4. **TypeScript only — no hand-written JavaScript.** Author `.ts`; compiled
   `.js` is build output, never edited by hand, gitignored. TS is compiled
   via the `Microsoft.TypeScript.MSBuild` NuGet package wired into the
   `DeEnv` build — no `npm install` / `package.json`.

5. **Milestone 1 storage is a plain JSON file**, written by simple rewrite.
   Do NOT introduce SQLite, Postgres, or ASP.NET MVC for milestone 1. The
   HTTP layer is a minimal handler (.NET `HttpListener` or minimal-API
   `WebApplication`).

6. **All storage access goes through an interface** — never direct file
   calls. The interface speaks the **model's terms (paths, nodes,
   dictionary entries), not flat key-value.** Exact shape is TBD in code
   (propose it in plan mode). This seam lets storage be
   swapped in later milestones. Getting it right in milestone 1 matters.

7. **The instance description is written by hand for now** — no designer, no
   generator. The instance is the only focus.

8. **No custom storage engine yet.** A custom render-coupled engine *is* a
   real long-term pillar (Vision pillar 5), but a late milestone. For now,
   plain JSON behind the interface. See DECISIONS.md.

9. **The app document is the authoring surface.** An instance is ONE text
   file (`instance.app`: types + initialData + common/ui code — see
   INSTANCE_DESCRIPTION_FORMAT.md), parsed by `AppParse`/`CodeParse` and
   printed back by `AppPrint`/`CodePrint` (round-trip stable). JSON is
   internal only (the in-memory `InstanceDescription`, the wire, storage).
   Code executes on twin C#/TS interpreters kept in lockstep by a shared
   conformance suite; a type checker and editor tooling are future layers.

10. **Watch for hidden scope.** This project's main risk is breadth. A plain
    sentence ("other users see a notification") can contain an entire future
    milestone. When a request looks like it spans milestones, split it and
    flag it rather than quietly building the big version.

11. **Minimal by default.** Keep the authoring surface as small as possible —
    least boilerplate, most defaults — unless minimalism would hinder the
    functionality. The common case is zero-config; you write configuration only
    to deviate. Any flag/opt-in is justified only while removing it would break
    real behavior, and is then **temporary scaffolding to delete**, not part of
    the permanent surface. See EXPECTATIONS.md ("Design principle — minimal by
    default"); it is judged criterion 5 for a slice.

12. **Server data reaches Code by RETURNING from a `sys.` call.** When app Code
    needs data the server computes — a schema descriptor, an access capability, a
    structural diff, a publish preview — deliver it as a value-**returning** `sys.`
    builtin: a *server-backed read* computed during render, memoized, shipped in
    the client state, and reused by the client (a cache miss throws "Value not
    available" → refetch). This is the `sys.schema` / `sys.canRead` /
    `sys.diffCommits` / `sys.publishPreview` shape — the fn returns the data and
    the render uses it inline. Do NOT surface server-computed data through a
    mutable `ui var`, an ambient/global the framework writes behind Code's back, or
    an async host-action-reply holder. If it *looks* like it needs a holder because
    the value is produced asynchronously (a host action's reply), first check
    whether it can instead be computed at **render time** and returned — even when
    the data is cross-instance, by wiring the compute into the render path (that is
    exactly how `sys.publishPreview` reaches another instance's committed data
    through a kernel-supplied delegate). Reserve a client-only holder (the
    `ctx.status` / `ctx.conflicts` shape — client-only, C# twin returns the empty
    constant, no conformance case) ONLY for state that is genuinely push/async and
    cannot be a render-time return. Host actions (`sys.publish`, `sys.commitDesign`,
    …) remain fire-and-effect and return nothing; reads return values.

## Testing approach

Behavior is specced in Gherkin `.feature` files in `DeEnv.Tests\Features`.
**Every scenario is tagged with its milestone** (e.g. `@milestone-1`,
`@milestone-future`). When working a milestone, filter to that tag and
ignore the rest. Split any scenario whose lines belong to different
milestones. A `@milestone-1` scenario must be passable with the milestone-1
stack (plain JSON, minimal HTTP handler) — if it cannot pass, it is
mis-tagged, not a reason to expand the stack.

Test stack (settled, already configured and working — do NOT change it):
**Reqnroll running on TUnit.** Do not switch to NUnit/xUnit, do not re-wire
Reqnroll, do not introduce a different assertion library. All step
definitions must fit the existing Reqnroll + TUnit setup. (Browser-facing
steps, when reached, use Playwright.)

## Current focus

**Usable-MVP gates** (DECISIONS "Data must survive schema changes" + the
usable-MVP plan): gate #1 non-destructive apply ✅ · gate #2 minimal real
deploy ✅ (a self-contained build as a systemd service behind nginx; login
wiring + TLS are the follow-on) · gate #3 dogfood one real app — **in
progress** (`instances/5`, `devlog`). **M13 app versioning is BUILDING in
parallel** (user's call, 2026-07-03): design settled in
`docs/plans/app-versioning-design.md`, slices in
`docs/plans/versioning-slices.md` — slices 1–6 landed 2026-07-03/04 (the
append-only store log with WAL+fsck; the design-snapshot caches; Commit/Branch
rows + `sys.commitDesign` + the authority inversion — design-data is truth,
boot = one-time adoption; structural diff + rename-safe publish with boundary
log entries — **renames now carry data through a deploy**, the MVP-visible
payoff; branches + origin-keyed three-way merge with report/resolve-by-args;
field-level conflicts — **disjoint edits auto-merge, same-field collisions get
the keep-mine/take-theirs banner**, the fine per-field UI's obligations
ledgered in the slices doc; time-travel clones — `cloneInstance(id, atSeq)`
with exact era-schema resolution via boundary base-commit stamps; plus the
single-store-per-file kernel fix killing a proven commit-clobber/WAL-collision
class). The `locked` access keyword landed alongside. **M13's core + UX surface is
COMPLETE (2026-07-04, suite 715)** — the Commit button landed with lockstep
interpreter wiring; the designer commits, publishes rename-safely, resolves
conflicts, and time-travels. Semantic migrations slice 2 landed 2026-07-05
(suite 737): publish now runs commit-authored `fn Type(old)` migrations through
the C# interpreter, with collapse-step-collapse range walking, one boundary
entry, dry-run parity, crash re-stamp guard, and v1 Int/Text/Bool harvest
ceilings. Deferred-with-intent ledger (compaction, fine conflict UI, branch/history UX) lives in
`docs/plans/versioning-slices.md`; DECISIONS.md carries the milestone entry.
The restoration bundle landed 2026-07-05 (suite 751): `sys.revertCommit` restores the last
commit by identity, literal-id resurrection backs it, identity re-add publish restoration
brings back reachable historical values/rows, TypeAdd closes the pure-type-add diff gap, and
`Commit.revertMigration` carries authored reverse migrations.
The versioned build is NOT yet deployed to the box — deploy + devlog dogfood
is the open decision. **M12 (visual component designer) stays deferred
until after the MVP.** Login persistence + the committed-designer auth flip landed
2026-07-04; remaining M-auth follow-ups (deploy login wiring, remove-user /
inline role-edit, broader auth styling) live in ROADMAP.md
"Near-future".

Everything below is **DONE** — the sequenced record is ROADMAP.md, the
reasoning is DECISIONS.md (this is just the orientation map):

- **M-auth (access control)** — deny-by-default per-type/per-field ruleset;
  conditions = pure Code at a kernel floor below Code; roles = `User.role` enum;
  self-hosted password login-as-state; first-admin env-var bootstrap; multi-user
  management.
- **Client data layer (render-as-planner)** — the view is the query: the client
  ships its view-state intent, the server reproduces the render and ships the
  harvested footprint.
- **M11 reactive components + public library** — slot-path component identity
  (run-once-per-slot), `foreach` keying, opt-in `key=`; `sys.schema`; the `lib`
  scope; the generic-UI-as-first-consumer COLLAPSE (`sys.resolve`).
- **M10 kernel host** — one process hosts N instances from `kernel.json`;
  operator ops (`sys.create`/`cloneInstance`/`delete`/`publish`/`rename`);
  id-based identity (`instances/<id>/`).
- **M9 self-hosted generic UI** — generic UI re-expressed in Code as a library;
  C# auto-form + `instance.ts` + `/js` client deleted; `/ws`+`/js` on a separate
  infra port; framework ctx in a `system` scope.
- **M8 UI customization** — DONE then DROPPED → two UI modes (fully auto OR
  fully custom `fn render()`).
- **M7 app document** — one text file (`instance.app`); combinator parser +
  round-trip printer; JSON internal only.
- **M6 Code** — twin C#/TS interpreters in lockstep via `conformance.json`; memo
  cache (structural privacy); warm WS session + change journal/rollback.
- **M4 designer = self-hosting** (the designer is the instance runtime;
  `SchemaBridge` projects designed data → canonical app doc), **M5 object model**
  (identity, extents, sets, references, GC), **M1–3** (JSON store behind the
  model-terms storage interface; minimal HTTP handler; TS-only).
