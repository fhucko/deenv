# deenv apps + designer capability inventory — what does self-hosting require?

Research for the live-first reboot (map issue #1), resolving issue #4.
Date: 2026-07-31 (complete rewrite of the partial draft). Sources: this repo
only — INSTANCE_MODEL.md, INSTANCE_DESCRIPTION_FORMAT.md, DECISIONS.md,
VISION.md, ROADMAP.md, EXPECTATIONS.md, docs/plans/app-versioning-design.md,
docs/plans/versioning-slices.md, and the committed instances
(`DeEnv/instances/1` designer, `2` todo, `3`/`4` CRM shapes, `5` devlog,
`6` kitchen-sink), plus a skim of
`DeEnv/{Code,Storage,Kernel,Http,Designer,Instance}`.

## The question

The new environment starts empty; system parts (databases, HTTP endpoints,
web UI, external connections, frameworks) are added/removed/updated at
runtime with no restart and no browser refresh, and it always restores to
its exact last state. The existing deenv apps AND the designer with full
self-modification are **reference loads**: the new environment must make
them *expressible* (not port the code). So: for each capability those loads
rest on, what is it, and does it belong

- **app-level** — expressible in ordinary app code/data; the environment
  only needs to not prevent it;
- **framework-level** — an *installable system part* in the new world:
  added/removed/updated at runtime like any other part;
- **environment-level** — must live in the live core; nothing installable
  can supply it because everything installable depends on it.

**Headline.** The environment-level core is **small (five capabilities)**.
Nearly everything the reference loads use is framework-level, and deenv has
already *proven* several of those parts can live in the image (generic UI,
component library, designer, version history as ordinary rows).

---

## Reference loads (what "expressible" means)

| Load | Path | Shape | Exercises |
|------|------|-------|-----------|
| Designer / IDE | `instances/1/app.deenv` (~1460 lines) | custom `fn render()`, meta-types as data, `locked` Commit/Branch, granted `sys` authority | full self-modification stack (B*), host actions, canvas eval, auth gate |
| Todo | `instances/2/app.deenv` | custom UI, sets, `sys.new`/`sys.schema`, components (`itemAdder`), lib `Input` | A3–A5, A9–A10 |
| CRM bare | `instances/3/app.deenv` | types only — fully auto UI | A1, A4 defaults |
| CRM seeded | `instances/4/app.deenv` | types + `initialData`, auto UI | A1–A2 seed |
| Devlog | `instances/5/app.deenv` | types + seed + `access` + User/role/password, no custom UI | A7 (worked access example) |
| Kitchen-sink | `instances/6/app.deenv` | enums, refs, sets, dicts (scalar + object), nested sets | A1 collection matrix |

The designer is the hard reference: if the reboot can host *that*, it can
host the rest. The small apps pin the minimum data/UI/access surface.

---

## A. What the existing apps rest on

### A1. Typed object model — types, identity, extents, sets/dicts/lists, references, GC

**What it is.** An instance is a list of type definitions rooted at `Db`
(object root). Types: base (`bool`/`int`/`decimal`/`text`/`date`/`datetime`/
`password` + `image`), enum, object (fixed ordered fields), reference,
`set of` (identity-keyed), `dict of … by key`, `list of` (ordered sequence;
object members addressed by id + membership, never index). Every non-constant
value carries an intrinsic monotonic int `id`; objects live in per-type
extents; collections/refs point into extents, never own. Lifetime is
mark-sweep reachability from the root; `remove` detaches, deletion is GC's
call, never guaranteed (INSTANCE_MODEL.md; DECISIONS M5 + mutation rule 2).

**Classification: framework-level, on an environment-level substrate.**
Schema language, collection shapes, and GC *policy* are a data-model
framework — installable/replaceable. Durable identity-bearing state the
environment can restore exactly is environment-level (E1).

### A2. Instance description / app document (one text file, parse+print)

**What it is.** One `app.deenv` document: `types`, optional `initialData`,
`access`, `common`, `ui`. Parsed by `AppParse`/`CodeParse`, printed by
`AppPrint`/`CodePrint`; `parse(print(d))` is identity
(INSTANCE_DESCRIPTION_FORMAT.md; DECISIONS M7). JSON is internal only
(in-memory model, wire, storage).

**Classification: framework-level.** Authoring format belongs to the
data/app framework. Round-trip printer discipline is what the designer uses
as visual↔text sync (SchemaBridge projection). Core need not own *this*
syntax.

### A3. Code execution — language + twin-interpreter lockstep

**What it is.** Purpose-built interpreted language (statements, exprs,
JSX-like tags, lambdas, ambients, `server fn` never shipped to client)
executed by two hand-maintained interpreters — C# (`CodeExecutor.cs`) and TS
(`codeExec.ts`) — kept lockstep by `conformance.json` (DECISIONS M6).
Interpreter is synchronous; a fn missing data is abandoned and re-run, not
paused (client data layer). Scopes chain `system ← lib ← app`
(SsrRenderer).

**Classification: split.** *A live code runtime* (load/replace/run code,
including effect-free eval of broken code) is **environment-level** (E2).
The twin-lockstep isomorphic discipline specifically is **framework-level**
— the web-UI part's strategy, not a property every part needs.

### A4. Auto/generic UI (one form per type)

**What it is.** No `fn render()` ⇒ ONE synthesized Code `fn render()` routes
every URL via `sys.resolve(path)` and composes the public library
(`ObjectForm`/`SetTable`/`DictTable`/`RefEditor`/`LeafForm`/`Field`/`Input`/
`LoginForm`/`ConflictBanner`…) — forms for objects, tables for collections,
collections as navigation boundaries, URL = walk through data
(INSTANCE_MODEL.md "Rendering"; ROADMAP M9/M11; `GenericUi.cs`).

**Classification: framework-level — already proven image-resident.** M9
deleted the C# auto-form. Clearest proof rich system parts live above the
core. Reboot should treat "generic UI" as an installable part from day one.

### A5. Custom UI — components, slot identity, reactivity

**What it is.** `fn render()` owns the URL space. A tag whose name resolves
to an in-scope fn is a *component* with render-tree-positional ("slot path")
identity — runs once per slot, keeps state across re-renders; `foreach`
per-row identity; opt-in `key=` (ROADMAP M11). Two modes only: fully auto
OR fully custom composing the same library (DECISIONS M8 SUPERSEDED).
Todo (`instances/2`) and designer both written against this.

**Classification: framework-level.** Web-UI framework contract.

### A6. Rendering pipeline — SSR, hydration, SPA, warm session

**What it is.** Server runs render → HTML + `initData` (touched memo-cache
snapshot; structural privacy) + `initUi` (render AST) + `initInfraPort`.
Client merges, re-executes, hydrates, then SPA-navigates client-side.
Mutations over warm WS with optimistic journal + rollback. Infra
(`/ws`, `/js`) on a separate port so the app URL space stays clean data
paths (INSTANCE_MODEL.md "Rendering architecture"; DECISIONS M6/M9).

**Classification: framework-level, with one environment-level obligation.**
Pipeline is the web framework. "No browser refresh, ever" obliges the
*environment* to keep live sessions/connections alive across part updates
(E3). A framework can only deliver no-refresh if the core does not drop
transport when parts change.

### A7. Access control — deny-by-default rules, roles, login

**What it is.** Deny-by-default ruleset per type/field:
`{subject, verbs, where?}`; conditions = pure Code over
`{db, currentUser, now, client, object}`; role = ordinary `User.role` enum;
login is a *state*, not a route; `locked` = write-immutable (history rows);
`sys` subject gates host actions and is deny-by-default even when data rules
are open (DECISIONS M-auth; INSTANCE_DESCRIPTION_FORMAT `access`;
`instances/5` worked example; designer gates everything on Admin + locks
Commit/Branch). Known gap today: dict entries not yet gated.

**Classification: split.** Rule language, role convention, login components
(`LoginForm`), bootstrap flow: **framework-level**. The *enforcement seam*
— "kernel floor, below Code, on the store/wire seam, non-bypassable" — plus
irreducible crypto/session-token check before app code runs (DECISIONS "C#
is the kernel"): **environment-level** (E4).

### A8. Client data layer — render-as-planner ("the view is the query")

**What it is.** Client ships actual view-state as `(action, state)` intent
over twin-stable fn ids; server reproduces the exact render, harvests the
dependency footprint (memo cache = structural privacy: only what a
computation touched ever ships), returns exactly that; one state-generation
guard covers the async window; client mark-sweep GC mirrors the store
(DECISIONS "Client data layer"; ROADMAP, suite 537). Framed as M11
altitude, NOT pillar-5 render-coupled storage.

**Classification: framework-level.** Transfer/runtime layer of the web
framework. Expressibility mostly falls out of A3 + E4 (meter what leaves).

### A9. Mutation model — ctx staging, unified commit, optimistic concurrency

**What it is.** Edits stage in ambient `ctx` overlay; `ctx.commit()` is the
SOLE user-facing persistence op (wire ops mirror model ops 1:1; seven live
mutating wire ops deleted). `remove` detaches; GC at end of commit
(temporary synchronous). Commits carry `baseVersion`; stale disjoint
commits auto-merge; same-field collisions surface (DECISIONS "Atomic
commit", "Client/server mutation model"; app-versioning-design §0).
Host actions (`publish`/`commitDesign`/…) remain the separate authority path.

**Classification: framework-level.** Transaction vocabulary belongs to the
data framework. Durability of a committed batch rides E1.

### A10. Server-backed reads (`sys.` returning builtins)

**What it is.** Server-computed data reaches Code by RETURNING from a
`sys.` call at render time — `sys.schema`, `sys.resolve`, `sys.canRead`,
`sys.diffCommits`, `sys.publishPreview`, `sys.mergePreview`,
`sys.evalContext` — memoized, shipped, refetch-on-miss; never an ambient
the framework writes behind Code's back (AGENTS.md rule 12). Host actions
remain fire-and-effect and return nothing.

**Classification: framework-level discipline on an environment-level hook.**
Needs the core to let a part inject compute into another part's render path
(the kernel-supplied delegate for cross-instance `sys.publishPreview`).
That hook is E5.

### A11. Multi-instance hosting, host actions, boot isolation

**What it is.** One kernel process hosts every instance from `kernel.json`
(bootstrap), path/port-routed, each with a sovereign store. Operator ops
exposed to Code as host actions: `sys.create` / `cloneInstance` / `delete` /
`publish` / `rename` / `setDesign` / … . Failing instance load is skipped
LOUDLY (503) while others boot (ROADMAP M10; DECISIONS "Multi-instance
management", "Per-instance boot isolation"). Management *experience* is
image Code (designer's instances page) — "kernel gains the mechanism, not
the management experience."

**Classification: environment-level (mechanism), app-level (experience).**
Hosting N parts, binding transports, supervising failures, exposing
management ops *inward* to governed code = E3 + E5. The UI stays an app.

### A12. URL-as-navigation + labeled breadcrumbs

**What it is.** Every node addressable by URL walking from `Db`; URLs never
use list indices; collections are page boundaries; single-valued nests
inline; breadcrumbs = labeled trail of the path (INSTANCE_MODEL.md;
EXPECTATIONS functional bar).

**Classification: framework-level.** Organizing principle of the data/UI
framework, not a core primitive. Core only needs addressable resources /
routable endpoints as part of E3 transports.

### A13. Storage seam in model terms

**What it is.** All storage access through an interface speaking paths,
nodes, extents, dictionary entries — not flat key-value; never direct file
calls from app/framework logic (AGENTS.md rule 6; `IInstanceStore`). Plain
JSON rewrite today; custom render-coupled engine is late (VISION pillar 5).

**Classification: environment-level substrate under E1.** The *seam shape*
(model-terms, swappable backend) is core architecture. Concrete JSON file
engine is today's installable-or-bundled implementation detail.

---

## B. What the designer + full self-modification rest on

The designer (`instances/1/app.deenv`) is an ordinary app: meta-types
(`Design`/`MetaType`/`MetaProp`/`MetaNode`/`MetaFn`/`MetaVar`/`MetaUse`/
`MetaAttr`/`Instance`/`Commit`/`Branch`/`User`) as data, hand-rolled
`fn render()`, lib tables/forms, and `sys.*` host actions for everything
with authority. **The IDE is an app-level artifact.** What it consumes:

### B1. Schema-as-data + projection to a runnable app

**What it is.** A design is ordinary rows; `SchemaBridge` projects designed
data into the canonical app document; `sys.publish(design, instanceId, …)`
applies it; `sys.setDesign` rebinds (DECISIONS M4/M10;
`DeEnv/Designer/SchemaBridge.cs`).

**Classification: framework-level.** Meta-schema + bridge = design-tooling
part. Hook needed: "replace a running part's definition from inside" (E5).

### B2. Non-destructive apply — schema change under live data

**What it is.** Publish PRESERVES data that still fits: additive fields
default, removed fields drop, scalar type changes value-convert
(unconvertible → default + reported), single→set reshapes
(`JsonFileInstanceStore.MigrateTowardSchema`; DECISIONS "Data must survive
schema changes"). Usable-MVP gate #1.

**Classification: framework-level — reboot's defining stress case.**
Migration policy = data framework. Environment obligation underneath is
E3's hardest form: *update a part that owns live state without losing state
or session*. An environment that only hot-swaps stateless parts fails the
reference load.

### B3. Append-only history — changeset log, WAL, fsck

**What it is.** Every instance gets an append-only changeset log
(`app-data.log.jsonl` + genesis; append THEN snapshot as a law);
`app-data.json` is a derived head materialization; fsck =
`replay(genesis→head) == head`; the log is the only non-derivable artifact
(app-versioning-design.md §0; versioning-slices slice 1; DECISIONS M13).

**Classification: environment-level (the journal), framework-level (the
entry semantics).** "Always restores to its exact last state" *is* a
durable crash-consistent journal — E1. Which entries mean "design commit"
vs "data edit" is versioning semantics above it.

### B4. Commit / branch / publish — design versioning + authority inversion

**What it is.** Design commit = `Commit` row (message, parent, mergeParent,
logSeq, cached canonical text + name-path→id map, optional migration /
`revertMigration`) minted by `sys.commitDesign` as ONE atomic log entry;
branches = `Branch` rows; **design-data + history are truth**, the app file
is a publish artifact, boot is one-time adoption (versioning-slices 2–3).
`Commit`/`Branch` are write-locked to host actions only.

**Classification: framework-level, data stored app-level.** Striking deenv
result: version history is *ordinary rows in the designer's own store*,
governed by ordinary access rules. Reboot should keep "history is data" —
needs only E1 + E5 (privileged ops minting locked rows).

### B5. Rename-safe migration — identity diff, boundary entries

**What it is.** Publish diffs two commits by *intrinsic identity* (per-commit
idMap), so a rename is a rename, never remove+add; renames carry data
through a deploy; publish writes a boundary-marked log entry recording the
commit it migrated FROM (versioning-slices slice 4). Parent-walk heuristic
was proven wrong — boundary markers are load-bearing.

**Classification: framework-level.** Wholly determined by A1 identity + B3
log. Environment need only not destroy identity across part updates.

### B6. Conflict handling — field-level three-way

**What it is.** Origin-keyed three-way merge over branches
(`sys.createBranch` / `sys.mergeBranch` / `sys.mergePreview`, report +
resolve-by-args); stale commits with disjoint fields auto-merge; same-field
collisions return `{base, mine, theirs}` and render keep-mine/take-theirs
(`ConflictBanner` / `ctx.resolveField`) (versioning-slices 5–6;
app-versioning-design §1).

**Classification: framework-level.** Merge policy over the versioning part.
Three-way *base* comes from the log (B3/E1).

### B7. Time-travel clones

**What it is.** `sys.cloneInstance(id, atSeq)` materializes an instance as
it was at a past log seq, with exact era-schema resolution via boundary
base-commit stamps (versioning-slices slice 7). Branch-on-real-data =
publish to a clone.

**Classification: framework-level** on the E1 log. Also the designer's
safe-experimentation story — reboot wants the same for *system parts*.

### B8. Semantic migrations + revert

**What it is.** A commit can carry `fn Type(old)` migrations run through
the C# interpreter at publish (collapse-step-collapse range walking, one
boundary entry, dry-run parity, crash re-stamp guard); `sys.revertCommit`
restores previous commit by identity with literal-id resurrection and
identity re-add restoration; `Commit.revertMigration` carries authored
reverse migrations (`MigrationRunner.cs`; DECISIONS M13).

**Classification: framework-level.** User-authored code run with authority
at a controlled moment — needs E2 + E4 + B3.

### B9. Evaluating in-progress (possibly broken) designs

**What it is.** Designer previews a design under construction:
`sys.evalContext(design, …)` builds an evaluation context over the draft,
`sys.renderTree` renders a canvas preview; degraded eval context "chips its
leaves instead of lying" (commit fb68372) — broken pieces degrade locally
instead of failing the whole preview.

**Classification: framework-level (tooling), resting on environment-level
property.** Core must make it cheap and safe to evaluate wrong code —
sandboxed, effect-free (immutability unifier: run it, discard write
overlay, keep reads — DECISIONS client data layer). Without this the
environment is a production console, not an IDE.

### B10. Host-action authority (`sys` subject)

**What it is.** Kernel ops exposed to Code gated by the same access floor
via the `sys` subject, deny-by-default even when the app's data rules are
open; only the designer grants it (`instances/1` access block; DECISIONS
M-auth extended). Designer surface actually used:
`create`/`cloneInstance`/`delete`/`publish`/`publishPreview`/`rename`/
`setDesign`/`commitDesign`/`createBranch`/`mergeBranch`/`mergePreview`/
`revertCommit`/`evalContext`/`renderTree` plus ordinary reads
(`schema`/`id`/`nest`/…).

**Classification: environment-level.** Self-hosting keystone: management
surface is (a) callable from inside, (b) denied by default, (c) granted by
data. Without it "the designer is just an app" is unsafe; with it, no
privileged binary UI is needed.

---

## C. The trichotomy, rolled up

### Environment-level — the irreducible live core (5 capabilities)

| # | Capability | Grounding |
|---|-----------|-----------|
| E1 | **Durable identity-bearing state with a crash-consistent, append-only journal**, accessed through a model-terms storage seam. Restore-to-exact-last-state IS the journal; intrinsic identity must survive renames, part updates, restores. | INSTANCE_MODEL identity; M13 slice 1 WAL/fsck; "log is the only non-derivable artifact"; `IInstanceStore` |
| E2 | **A live code runtime** — load, replace, and execute code at runtime, including effect-free/degraded evaluation of code that may be wrong. | M6 interpreters; immutability-unifier; degraded eval contexts |
| E3 | **Part hosting + supervision with session continuity** — add/remove/update parts (including stateful ones) with no restart, no browser refresh, no dropped live sessions; a failing part degrades loudly and locally; transports stay up across updates. | M10 kernel host; per-instance boot isolation; non-destructive apply; warm WS sessions |
| E4 | **A non-bypassable effect floor** — every effect (data access, network, host op) from installed/app code passes a gate the core owns; deny-by-default for authority; session-token / crypto primitives before app code. | M-auth "kernel floor, below Code, on the store/wire seam"; `sys` subject; DECISIONS "C# is the kernel" irreducible list |
| E5 | **Self-management exposed inward** — the environment's own operations (create/clone/delete/publish/replace-part, render-time compute delegates across parts) are callable from governed code as data-returning or effectful ops. | M10 host actions; AGENTS rule 12; `sys.publishPreview`'s cross-instance delegate |

Today's deenv kernel (interpreter + storage interface + multi-instance/port
supervisor + boot + auth floor) is approximately E1–E5. The reboot's job is
to make that core *live* (parts — including what is today "kernel-adjacent"
framework — addable without recompile), not to grow it.

### Framework-level — installable system parts

- **Data-model part**: typed object model (A1), app-document format +
  round-trip printer (A2), mutation model / ctx / unified commit (A9),
  access-rule language + login components (A7 surface), URL-as-navigation
  conventions (A12).
- **Web-UI part**: twin-lockstep isomorphic execution (A3 surface), generic
  UI library (A4 — already Code today), components/reactivity (A5),
  SSR/hydration/SPA pipeline + infra-port split (A6), render-as-planner
  data layer (A8), server-backed-read convention (A10), scope chain
  system/lib/app.
- **Versioning part**: non-destructive apply (B2), commit/branch/publish
  semantics + authority inversion (B4), rename-safe identity diff (B5),
  three-way merge + conflicts (B6), time-travel clones (B7), semantic
  migrations + revert (B8).
- **Design-tooling part**: meta-schema + SchemaBridge (B1), draft
  evaluation + canvas preview (B9).

### App-level — ordinary apps in the new world

- Reference apps: todo (`2`), devlog (`5`), CRM shapes (`3`/`4`/`6`) —
  types + seed + optional custom render + access rules, nothing else.
- **The designer/IDE** — decisive: one ordinary ~1460-line app document
  holding meta-rows and `locked` Commit/Branch rows plus granted `sys`
  authority. Management *experience* stays app-level; only the *mechanism*
  is core (M10 discipline; K2 lesson in DECISIONS: keep tooling in the
  malleable image, never freeze it in the kernel).

---

## D. Explicitly NOT required to express the reference loads

Do not pull these into the reboot core just because VISION names them.
Reference loads do not need them expressible on day one:

| Deferred | Why out |
|----------|---------|
| Distributed ACID / multi-device shard (VISION pillar 7) | Single-process multi-instance covers designer + apps |
| Custom render-coupled storage engine (pillar 5) | Plain journaled store + model-terms seam is enough; A8 is M11 altitude |
| Visual component designer (M12) | Explicitly deferred post-MVP; designer today is hand-rolled Code UI |
| Full multi-user real-time collab | Optimistic `baseVersion` + field conflicts cover the designer path; presence/OT not used |
| Type checker / editor tooling | Runtime errors today; designer validates structurally via bridge |
| External API/DB connectors | No reference app uses them; reboot *enables* installable connectors via E3/E4/E5, does not bundle them |
| Fault/resource isolation between parts | Correctness isolation (sandbox) yes; CPU/mem isolation is later |

---

## E. What this means for the reboot

1. **Build E1–E5 first, nothing more.** Every reference capability
   decomposes into those five plus installable parts. deenv's own history
   validates the direction: C# auto-form → Code library (M9), C# per-URL
   dispatch → one Code render (M11), designer → an app (M4/M10) — each
   time something moved *out* of the core, it got easier to change.

2. **The hard environment test is B2 under E3**: update a part that owns
   live state (schema change on a running app) with no restart, no
   refresh, no data loss, session intact. deenv passes this today via
   publish+migrate for *apps*; the reboot must pass it for *system parts*
   too (databases, endpoints, frameworks). Today's kernel is still
   recompile-and-restart (DECISIONS "The self-hosted image", updated
   2026-07-14 toward radical liveness).

3. **Identity and the journal are one decision, not two.** Rename-safety,
   conflicts' three-way base, time-travel, revert-with-resurrection all key
   off intrinsic identity recorded in the append-only log. If E1 gets
   identity wrong, the entire versioning part becomes unbuildable above it.

4. **Authority-as-data (B10/E4/E5) is what makes "starts empty" viable**:
   an empty environment plus a granted management capability is enough to
   install everything else from inside. Login/session crypto stays under
   the floor; role/rule/UI stay installable.

5. **Keep the designer an app.** The K2 caution (DECISIONS) is load-bearing
   for the reboot: if tooling calcifies into the core, "live-first" fails
   the people who have to change the system. E5 exists so the IDE never
   needs a privileged side channel.

---

## F. Quick lookup — capability → level

| Capability | Level |
|------------|-------|
| Append-only durable journal + identity | **E** (E1) |
| Model-terms storage seam | **E** (E1) |
| Live code load/replace/run + sandboxed eval | **E** (E2) |
| Part host, transport bind, session continuity, supervision | **E** (E3) |
| Non-bypassable effect/auth floor + session crypto | **E** (E4) |
| Inward host ops + cross-part render-time delegates | **E** (E5) |
| Typed object model / GC policy | F (data) |
| App document parse/print | F (data) |
| Twin-interpreter lockstep | F (web-UI) |
| Generic UI library | F (web-UI) — already image |
| Components / slot identity | F (web-UI) |
| SSR / hydration / SPA / memo structural privacy | F (web-UI) |
| Access rule language + LoginForm | F (data/UI) |
| Render-as-planner client data layer | F (web-UI) |
| ctx / unified commit | F (data) |
| `sys.*` returning-read convention | F on E5 hook |
| URL-as-navigation / breadcrumbs | F (data/UI) |
| SchemaBridge + meta-schema | F (design) |
| Non-destructive migrate / rename-safe publish | F (versioning) |
| Commit/Branch rows + commitDesign/publish | F (versioning), data app-level |
| Three-way merge / conflicts UI | F (versioning/UI) |
| Time-travel clone | F (versioning) |
| Semantic migrations + revert | F (versioning) |
| Canvas eval / degraded preview | F (design) on E2 |
| Designer / todo / devlog / CRM apps | **A** |
| Multi-device ACID, custom storage engine, visual component designer | out of reference scope |
