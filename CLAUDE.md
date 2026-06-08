# CLAUDE.md

Project context for Claude Code. Read this first, then the linked files.

## What this project is

A web-first development environment for building database-backed web apps,
where developers design data visually and use it as objects. Full mission in
**VISION.md**.

## Read these before working

- **VISION.md** — the full long-term mission. The destination. Do not treat
  it as the task list.
- **ROADMAP.md** — the sequenced milestones. This *is* the task list.
- **EXPECTATIONS.md** — what "good" looks like: project structure, build
  rules, and the qualities a slice must meet beyond just running.
- **INSTANCE_MODEL.md** — the instance type system, URL-as-navigation, and
  the one-form-per-type rendering rule.
- **INSTANCE_DESCRIPTION_FORMAT.md** — the canonical JSON shape of a
  hand-written instance description, with rules and worked examples.
- **DECISIONS.md** — key architectural decisions and their reasoning.

## Ground rules — important

1. **Build the current milestone only.** Per ROADMAP.md, Milestones 1–4 are
   done (M4 delivered as self-hosting — see DECISIONS.md). The current milestone is
   **Milestone 5: the object model** — intrinsic `int` identity on non-constants,
   references (no ownership; per-type extents), sets, identity/key addressing, and
   GC. **Schema versioning was postponed** (to be self-hosted after a computation
   milestone — see DECISIONS.md / ROADMAP.md). Later milestones are out of scope
   unless explicitly asked.

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

9. **No custom language yet.** Generated/authored UI code is TypeScript.

10. **Watch for hidden scope.** This project's main risk is breadth. A plain
    sentence ("other users see a notification") can contain an entire future
    milestone. When a request looks like it spans milestones, split it and
    flag it rather than quietly building the big version.

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

Milestones 1–4 are done. The current milestone is **Milestone 5: the object
model** — give the data a C#-style object graph: intrinsic `int` identity on
non-constants (objects and dictionaries; scalars have none), **references with no
ownership** (objects live in per-type extents; a single object-typed prop *is* a
reference), **sets** (collections keyed by a member's own identity, replacing
surrogate-keyed `dict<Object> auto-int`; dictionaries stay for meaningful keyed
maps), addressing that keeps today's navigation (set → identity, dict → key,
single → field), and **GC** (mark-sweep from the root). UI offers
pick-existing-or-create-new. This is a deliberate storage reconception
(normalized per-type extents, identity-addressed) flagged under ground rule 10 —
cut thin slices. First slice + full rationale in DECISIONS.md / ROADMAP.md.
**Schema versioning was postponed** — to be self-hosted after a computation
milestone; the reusable piece already designed is the structural identity-based
schema diff.

Milestone 4 was delivered as **self-hosting** (see DECISIONS.md): the designer is
the instance runtime running a hand-written meta-schema (`DeEnv/meta.schema.json`),
and `DeEnv/Designer/SchemaBridge.cs` projects the designed data into a canonical
`instance.schema.json` (validated by `InstanceDescriptionLoader.Load`). A `--mode`
switch (VS launch profiles Instance / Designer / Export) flips between authoring,
exporting, and running; the instance runtime itself is untouched. Specced by
`DeEnv.Tests\Features\Bridge.feature`. Storage remains plain JSON behind the
storage interface; no SQLite/versioning yet.
