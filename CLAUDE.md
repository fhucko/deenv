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

1. **Build the current milestone only.** Per ROADMAP.md, Milestones 1–8 are
   done (M4 designer as self-hosting; M5 the object model; M6 Code: reactive
   UI on twin interpreters; M7 the app document: one text file — types +
   initialData + code — with parser and printer; M8 UI customization was
   **dropped 2026-06-13** in favour of **two UI modes** — fully custom (`fn
   render()`) or fully auto (the generic UI) — see DECISIONS.md). **M9
   (self-hosted generic UI) is DONE (2026-06-14)**: the generic UI is re-expressed in
   Code as a library (`objectForm`/`refEditor`/`setTable`/`dictTable`/`leafForm` over
   schema-as-data; builtins `field`/`humanize`/`extent`/`setRef`/`nest`/`clone`; `obj.prop
   = x`), and is now the **default** renderer (no opt-in). Object forms, references, set
   tables, objects-that-hold-sets, dictionaries (route + entries), and a self-hosted
   NotFound all self-host; the **C# auto-form, `instance.ts`, and the `/js` client are
   deleted** — the self-hosted UI is the sole renderer. Infra (`/ws`, `/js` bundle) is on
   a separate port so the app owns a clean data URL space; framework context (`db`,
   `path`, `status`) lives in a system scope and the generic-UI internals in a sibling
   `internal` scope outside userspace. **Multi-instance management (the kernel host) is the
   active milestone (Milestone 10, refocused 2026-06-14; first slice landed 2026-06-14)** — the
   kernel host is the entry point (NO run modes; `kernel.json` is the single source of what
   runs). Build only M10 work next — the operator-facing create/switch/delete COMMANDS/UI as image Code
   (the C# mechanism is done; the host-action channel + `sys.publish(schema, targetId)` AND
   `sys.create(schema, appPort, infraPort)` — both project a passed schema object — have landed), and
   promoting the registry to a restricted kernel-instance; see Current focus + ROADMAP.md.
   Cross-machine/multi-kernel + distributed ACID, fault/resource isolation, real-time, and the
   management commands stay out of scope unless explicitly asked. Schema versioning steps back to
   M11 (it sits on instance management).

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

**Milestone 10 is multi-instance management (the kernel host) — refocused 2026-06-14,
first slice LANDED 2026-06-14 (no run modes — the kernel host is the entry point). (Schema
versioning, briefly scoped as M10, stepped back to M11 — it sits on instance management.)
Milestone 9 (self-hosted generic UI) is COMPLETE (2026-06-14).** The generic UI is re-expressed in Code as a reflective library
(`objectForm`/`refEditor`/`setTable`/`dictTable`/`leafForm` over schema-as-data;
builtins `field`/`humanize`/`extent`/`setRef`/`nest`/`clone`; component pattern with
`var state = { draft: clone(…) }` + `obj.prop = x`) and is the **default** renderer —
an app with no `fn render()` self-hosts. Object forms, references (pick/clear/
create-new), set tables, objects-that-hold-sets (inline tables, nested path-walk links
`/notes/3`), dictionaries (route + object/scalar entries, path-addressed editing), and a
self-hosted NotFound (`status = 404`) all render in Code. The **C# auto-form,
`instance.ts`, and the `/js` C# client are deleted** — the self-hosted UI is the sole
renderer. Infra (`/ws` + the `/js` bundle) is served on a **separate port** so the app
owns a clean data URL space. Framework context — `db`, `path`, `status` (all system
vars) — lives in a `system` scope; the generic-UI internals (`__descs`/`__dictDescs` +
the library) live in a **sibling `internal` scope outside userspace**. Driven by
`SelfHostedUi.feature` (`DeEnv/Instance/GenericUi.cs`) plus the migrated
milestone-1/2/4/5 features. See DECISIONS.md ("Self-hosted generic UI" + "Post-M9
refinements") and the project memory. **The current milestone is Milestone 10 — multi-instance
management (the kernel host)** (refocused 2026-06-14; first five slices LANDED 2026-06-14 — the
kernel host, `list` (registry read from Code), `create`, and `switch`/`delete` — the full create/list/
switch/delete *mechanism* in C#): one kernel
process hosts multiple instances at once, each on its own port pair with its own sovereign data,
driven by an instance registry as kernel-owned data — the substrate under schema versioning's
*apply*, the Stage-2 test-instance loop, and the self-hosted-image north star. First slice
(hosting/wiring, no Code/interpreter change), DONE: the single-instance host is factored out of
`Program.cs` into a thin C# supervisor (`DeEnv/Kernel/`: `RegistryReader` reads `kernel.json` as
plain bootstrap data; `KernelHost`/`HostedInstance` start every instance + block on shutdown).
Two instances on two port pairs are both reachable and data-sovereign (`Kernel.feature`,
`@milestone-10`; suite green 229/229). **Run modes were removed (user direction):** the kernel
host is the sole entry point and `kernel.json` is the single source of what runs — a single
instance is just a one-entry registry, so there is no `--mode`/`--app`. The designer is a registry
entry; the M4 export/publish bridge is now exposed to Code as host actions — `sys.publish(schema,
targetId)` (replace an existing instance) and `sys.create(schema, appPort, infraPort)` (spawn a new
one), both projecting a passed schema object — not a CLI mode. Kernel discipline: the kernel gains
the hosting *mechanism* (create/list/switch/delete in C# + the host-action channel); the operator
create/publish/switch/delete COMMANDS-as-the-IDE are image Code. **Slice 1 of that surface landed
2026-06-15: the operator designer** — `designer.app` gained a HAND-ROLLED custom `fn render()` (type/prop
editor + `sys.instances` list + a `sys.create(db, ports)` control), replacing its auto generic UI.
Decided: the designer is explicit hand-written Code, NOT a "hidden callable designer" (no exposing the
generic `objectForm` library to userspace — the compose mechanism is rejected). Follow-ups: per-instance
publish button, delete/switch, richer editing.
Deferred: cross-machine/multi-kernel + distributed ACID, fault/resource isolation, real-time
(Stage 5/later), and the management commands. Schema versioning steps back to M11 (it sits on
this). See ROADMAP.md (Milestone 10), STAGES.md, and DECISIONS.md ("Multi-instance management —
the kernel host").

**Milestone 8 (UI customization — views) was DROPPED (2026-06-13).** The UI is
now **two modes only**: fully **custom** (`fn render()`, owns the whole UI) or
fully **auto** (the generic UI — C# auto-form, or the M9 self-hosted `generic`
opt-in). The user-authored `view T(x)` / `view "/path"(p)` middle layer was
removed (awkward, db-structure-coupled, uncertain value); "auto with overrides"
is deferred to the cleaner mechanism of the custom mode *composing the generic UI
as a library* (`fn render()` calling `objectForm`/`field`/…). The
`InstanceUi.Views` + synthesized-view dispatch are KEPT, but only as the generic
UI's *internal* routing (`GenericUi.Effective` / `ResolveView`) — not user-facing.
See DECISIONS.md ("UI customization — views (M8) — SUPERSEDED") and the
two-UI-modes memory.

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
lines); the designer runs `designer.app` and the bridge publishes designs by
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
per-client session (clientId, hello, 10s claim window — a thin handle; refetch
re-renders from a fresh store load, no warm graph) with a
field-level **change journal** (rollback on server reject) and
negative→real id remapping; hidden-dependency recomputes go through
`refetch`. The schema document also carries a normalized **initialData**
seed. The committed default instance (`DeEnv/instance.schema.json`) is the
**todo app**, driven end-to-end by `TodoApp.feature`. Full decision record
in DECISIONS.md ("Code milestone — how it was delivered").

Milestone 4 was delivered as **self-hosting** (see DECISIONS.md): the designer is
the instance runtime running a hand-written meta-schema (`DeEnv/meta.schema.json`),
and `DeEnv/Designer/SchemaBridge.cs` projects the designed data into a canonical
`instance.schema.json` (validated by `InstanceDescriptionLoader.Load`). (Originally a `--mode`
switch flipped between authoring, exporting, and running; **run modes were removed in M10** —
the kernel host reads `kernel.json`, the designer is a registry entry, and export is to be exposed
to Code.) The instance runtime itself is untouched. Specced by
`DeEnv.Tests\Features\Bridge.feature`. Storage remains plain JSON behind the
storage interface; no SQLite/versioning yet.
