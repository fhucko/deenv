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

1. **Build the current milestone only.** Per ROADMAP.md, Milestones 1–7 are
   done (M4 designer as self-hosting; M5 the object model; M6 Code: reactive
   UI on twin interpreters; M7 the app document: one text file — types +
   initialData + code — with parser and printer; JSON is internal only — see
   DECISIONS.md). The current milestone is **Milestone 8: UI customization**
   — views (per-type / per-path render functions) over the generic UI, with
   `fn render()` becoming optional (plan: ui-customization.md). **Schema
   versioning stays postponed** (self-hosted on top of Code). Later
   milestones are out of scope unless explicitly asked.

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

Milestones 1–7 are done. The current milestone is **Milestone 8: UI
customization** — views over the generic UI, chosen per request by a
rendering-function decision (plan: ui-customization.md). A **type view**
(`view Customer(customer)`) replaces the generic object page for that type
(breadcrumbs stay); a **path view** (`view "/reports"(path)`) takes over a
URL subtree; `fn render()` becomes optional sugar for the root path view;
everything without a view stays the generic auto-form. View pages are full
code pages — memo cache, two-way binding, WS mutations, warm-session refetch
all unchanged. Designer editing of views, fragment-level islands, and the
self-hosted generic UI are explicitly deferred.

**Milestone 7 (the app document) just landed** — one text file describes a
whole instance: `types` + optional `initialData` + optional `common`/`ui`
code, in an app.txt-style indentation-based language (JSX-like tags,
expression precedence). `DeEnv/Code/Parsing` is the combinator core (offset
cursor, positioned errors); `CodeParse`/`AppParse` parse, `CodePrint`/
`AppPrint` print the canonical form back (round-trip tested: parse∘print is
the identity, print∘parse a fixpoint). **JSON is retired from authoring** —
the `InstanceDescription` record and its JSON form are internal (in-memory +
wire; the client still receives AST, there is no TS parser). The committed
default app is `DeEnv/instance.app` (the todo app: types + seed + UI in ~130
lines); the designer runs `meta.app` and the bridge publishes designs by
printing the same format. Format reference: INSTANCE_DESCRIPTION_FORMAT.md.

**Milestone 6 (Code) just landed** — user-authored behaviour and UI as
hand-written JSON AST in the schema document (`ui`/`common` sections),
interpreted by twin C#/TS interpreters (`DeEnv/Code/CodeExecutor.cs` /
`DeEnv/Instance/codeExec.ts`) kept in lockstep by a shared conformance suite
(`DeEnv/Code/conformance.json`). SSR renders the first paint; the client
hydrates and takes over (identity-keyed reconciliation, two-way binding).
Transfer/reactivity is a **memo cache**: computation results ship with
dependency refs, never input values — privacy is structural, and the first
paint never calls the server. Mutations persist over the WS against a
per-client **warm session** (clientId, hello, 10s claim window) with a
field-level **change journal** (rollback on server reject) and
negative→real id remapping; hidden-dependency recomputes go through
`refetch`. The schema document also carries a normalized **initialData**
seed. The committed default instance (`DeEnv/instance.schema.json`) is the
**todo app**, driven end-to-end by `TodoApp.feature`. Full decision record
in DECISIONS.md ("Code milestone — how it was delivered").

Milestone 4 was delivered as **self-hosting** (see DECISIONS.md): the designer is
the instance runtime running a hand-written meta-schema (`DeEnv/meta.schema.json`),
and `DeEnv/Designer/SchemaBridge.cs` projects the designed data into a canonical
`instance.schema.json` (validated by `InstanceDescriptionLoader.Load`). A `--mode`
switch (VS launch profiles Instance / Designer / Export) flips between authoring,
exporting, and running; the instance runtime itself is untouched. Specced by
`DeEnv.Tests\Features\Bridge.feature`. Storage remains plain JSON behind the
storage interface; no SQLite/versioning yet.
