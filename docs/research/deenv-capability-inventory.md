# deenv apps + designer capability inventory — what does self-hosting require?

Research for the live-first reboot (map issue #1), resolving issue #4.
Date: 2026-07-31. Sources: this repo only — INSTANCE_MODEL.md,
INSTANCE_DESCRIPTION_FORMAT.md, DECISIONS.md, VISION.md, ROADMAP.md,
EXPECTATIONS.md, docs/plans/app-versioning-design.md,
docs/plans/versioning-slices.md, and the committed instances
(`DeEnv/instances/1` designer, `2` todo, `3`/`4` CRM shapes, `5` devlog,
`6` kitchen-sink), plus a skim of `DeEnv/{Code,Storage,Kernel,Http,Designer}`.

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

The headline finding is at the end: the environment-level core is **small
(five capabilities)**; nearly everything the reference loads use is
framework-level, and deenv has already *proven* several of those parts can
live in the image (the generic UI, the component library, the designer
itself are all Code/data today).

---

## A. What the existing apps rest on

### A1. Typed object model — types, identity, extents, sets/dicts/lists, references, GC

**What it is.** An instance is a list of type definitions rooted at `Db`;
every non-constant value carries an intrinsic monotonic int `id`; objects
live in per-type extents (flat id-keyed pools); sets/lists/references point
into extents, never own; lifetime is mark-sweep reachability from the root
(INSTANCE_MODEL.md; DECISIONS "Milestone 5 — the object model").
`remove` detaches, deletion is GC's call, never guaranteed (DECISIONS
"Client/server mutation model", rule 2).

**Classification: framework-level, on an environment-level substrate.**
The *schema language*, collection shapes, and GC policy are a data-model
framework — installable, replaceable (a reboot could ship a different one).
But the substrate it needs — durable, identity-bearing state the
environment can restore exactly — is environment-level (see E1). The split
matters: "restores to its exact last state" is a core promise, and the core
cannot restore state it does not own the persistence of.

### A2. Instance description / app document (one text file, parse+print round-trip)

**What it is.** One `instance.app`/`app.deenv` document: `types`,
`initialData`, `access`, `common`, `ui`; parsed by `AppParse`/`CodeParse`,
printed by `AppPrint`/`CodePrint`, `parse(print(d))` is the identity
(INSTANCE_DESCRIPTION_FORMAT.md; DECISIONS "The app document — M7"). JSON
is internal only.

**Classification: framework-level.** The authoring format belongs to the
data/app framework. The round-trip printer discipline is a framework
property the designer depends on (it is the visual↔text sync engine —
ROADMAP "Visual component designer"). Nothing about the live core requires
*this* format.

### A3. Code execution — the language and the twin-interpreter lockstep

**What it is.** A purpose-built interpreted language (statements, exprs,
JSX-like tags, lambdas, ambients) executed by two hand-maintained
interpreters — C# server (`CodeExecutor.cs`) and TS client (`codeExec.ts`) —
kept byte-identical by a shared conformance suite (`conformance.json`;
DECISIONS "Code milestone — M6"). The interpreter is deliberately
synchronous; a fn missing data is abandoned and re-run, not paused
(DECISIONS "Client data layer").

**Classification: split.** *A live code runtime* — load, replace, and run
code at runtime — is **environment-level** (E2): it is the mechanism of
"anything can be changed at runtime". The *twin-lockstep discipline*
specifically (same code, same result, on server and browser) is
**framework-level**: it is the isomorphic-rendering strategy of the web-UI
part, not a property every conceivable part needs. A reboot could install a
web framework with a different client story without touching the core.

### A4. Auto/generic UI rendering (one form per type)

**What it is.** Any app without `fn render()` is rendered by ONE
synthesized Code `fn render()` that routes every URL via `sys.resolve(path)`
and composes the public component library (`ObjectForm`/`SetTable`/
`DictTable`/`RefEditor`/`LeafForm`) — forms for objects, tables for
collections, collections as navigation boundaries, URL = a walk through the
data (INSTANCE_MODEL.md "Rendering"; ROADMAP M9/M11).

**Classification: framework-level — and already proven image-resident.**
M9 deleted the C# auto-form; the generic UI is a Code library today. It is
the clearest existence proof that rich system parts can live above the
core: the reboot should treat "generic UI" as an installable part from day
one.

### A5. Custom UI — components, slot identity, reactivity

**What it is.** `fn render()` owns the URL space; a tag whose name resolves
to an in-scope fn is a *component* with render-tree-positional ("slot
path") identity — runs once per slot, keeps state across re-renders,
per-row identity through `foreach`, opt-in `key=` (ROADMAP M11). Two modes
only: fully auto or fully custom composing the same library (DECISIONS "UI
customization — M8 — SUPERSEDED").

**Classification: framework-level.** Component identity and reactivity
semantics are the web-UI framework's contract. The todo app
(`instances/2/app.deenv`) and the designer are both written against it.

### A6. Rendering pipeline — SSR first paint, hydration, SPA navigation, no-refresh liveness

**What it is.** Server runs the render → HTML + `initData` (the touched
memo-cache snapshot) + `initUi` (the render AST); the client merges,
re-executes, hydrates, and thereafter re-renders client-side; mutations go
over a warm WS session with an optimistic journal + rollback
(INSTANCE_MODEL.md "Rendering architecture"; DECISIONS "Code execution
model").

**Classification: framework-level, with one environment-level obligation.**
The pipeline is the web framework. But "no browser refresh, ever" — the
reboot's stated bar, and already deenv's target ("the app in the browser
never needs a reload", ROADMAP real-time bullet) — obliges the
*environment* to keep live sessions/connections alive across part
updates (E3). A web framework can only deliver no-refresh updates if the
core does not drop its transport when parts change.

### A7. Access control — deny-by-default rules, roles, login

**What it is.** A deny-by-default ruleset per type/field,
`{type, field?, verbs, condition}`; conditions are pure Code expressions
over `{db, currentUser, now, client, object}`; a role is just a `User.role`
enum; login is a state, not a route; `locked` = write-immutable; the `sys`
subject gates host actions and is deny-by-default even when data rules are
open (DECISIONS "M-auth"; INSTANCE_DESCRIPTION_FORMAT.md `access`;
`instances/5/app.deenv` is the worked example).

**Classification: split.** The rule language, role convention, login
components, bootstrap flow: **framework-level** (they ride the data
framework and the UI library; auth UI is `lib` components today). The
*enforcement seam* — "a kernel floor, below Code, on the store/wire seam,
non-bypassable" (DECISIONS M-auth) — is **environment-level** (E4): a gate
is only a gate if no installable part can route around it. In the new
world this generalizes: every effect a part performs (data read/write,
network, host op) passes a floor the core owns.

### A8. Client data layer — render-as-planner ("the view is the query")

**What it is.** The client ships its actual view-state as an
`(action, state)` intent over twin-stable fn ids; the server reproduces the
exact render, harvests the dependency footprint (structural privacy: only
what a computation touched ever ships), and returns exactly that; one
state-generation guard covers the async window; client-side
mark-sweep GC mirrors the store's (DECISIONS "Client data layer —
render-as-planner"; ROADMAP entry, suite 537).

**Classification: framework-level.** It is the transfer/runtime layer of
the web framework, explicitly framed as "M11 altitude, NOT the pillar-5
render-coupled storage engine". A reboot must make this *expressible*
(a part that can reproduce computations and meter what data leaves the
server), which mostly falls out of A3 + E4.

### A9. Mutation model — ctx staging, unified commit, optimistic concurrency

**What it is.** Edits stage in an ambient `ctx` overlay; `ctx.commit()` is
the SOLE persistence op (all seven live wire ops deleted; every wire op
mirrors a model op); commits carry `baseVersion`, stale disjoint commits
auto-merge, same-field collisions surface (DECISIONS "Atomic commit",
"Client/server mutation model — ground rules";
app-versioning-design.md §0 baseVersion).

**Classification: framework-level.** Transaction vocabulary belongs to the
data framework. The durability of a committed batch, however, rides E1.

### A10. Server-backed reads (`sys.` returning builtins)

**What it is.** Data the server computes reaches Code by RETURNING from a
`sys.` call at render time — `sys.schema`, `sys.resolve`, `sys.canRead`,
`sys.diffCommits`, `sys.publishPreview` — memoized, shipped, refetched on
miss; never an ambient the framework writes behind Code's back (AGENTS.md
rule 12).

**Classification: framework-level discipline on an environment-level hook.**
The pattern is a framework convention, but it needs the core to let a part
inject compute into another part's render path (the kernel-supplied
delegate that lets `sys.publishPreview` reach another instance's committed
data). "Parts can offer render-time-computable services to code" is the
core hook (part of E5).

### A11. Multi-instance hosting, host actions, boot isolation

**What it is.** One kernel process hosts every instance in `kernel.json`,
path-routed, each with a sovereign store; operator ops are host actions
exposed to Code (`sys.create`/`cloneInstance`/`delete`/`publish`/`rename`/
`setDesign`); a failing instance load is skipped LOUDLY (503) while others
boot (ROADMAP M10; DECISIONS "Multi-instance management", "Per-instance
boot isolation").

**Classification: environment-level (the mechanism), app-level (the
experience).** Hosting N parts/apps, binding transports, supervising
failures, and exposing management ops *inward* to governed code is the live
core's job (E3 + E5). The management *experience* is deliberately image
Code — the designer's instances page is an app (DECISIONS: "the kernel
gains the mechanism, not the management experience"). The reboot should
keep exactly that line.

---

## B. What the designer + full self-modification rest on

The designer (`instances/1/app.deenv`, ~1460 lines) is itself an ordinary
app: meta-types (`Design`/`MetaType`/`MetaProp`/`MetaNode`/`MetaFn`…) as
data, a hand-rolled `fn render()`, and `sys.` host actions for everything
with authority. That is the self-hosting result to preserve: **the IDE is
an app-level artifact.** What it consumes:

### B1. Schema-as-data + projection to a runnable app

**What it is.** A design is ordinary rows; `SchemaBridge` projects designed
data into the canonical app document; `sys.publish(design, instanceId, …)`
applies it to a running instance; `sys.setDesign` rebinds (DECISIONS M4,
M10; `DeEnv/Designer/SchemaBridge.cs`).

**Classification: framework-level.** The meta-schema and the bridge are
the design-tooling part. The hook it needs — "replace a running part's
definition from inside" — is E5.

### B2. Non-destructive apply — schema change under live data

**What it is.** A publish PRESERVES data that still fits: additive fields
default, removed fields drop, scalar type changes value-convert
(unconvertible → default + reported), single→set reshapes
(`JsonFileInstanceStore.MigrateTowardSchema`; DECISIONS "Data must survive
schema changes"). Usable-MVP gate #1.

**Classification: framework-level — but the reboot's defining stress
case.** Migration policy belongs to the data framework. The environment
obligation underneath is E3's hardest form: *updating a part that owns live
state without losing the state or the session*. An environment that can
only add/remove stateless parts fails the reference load.

### B3. Append-only history — the changeset log, WAL, fsck

**What it is.** Every instance gets an append-only changeset log
(`app-data.log.jsonl` + genesis; append THEN snapshot as a law);
`app-data.json` is a derived head materialization; fsck =
`replay(genesis→head) == head`; the log is the only non-derivable artifact
(app-versioning-design.md §0; versioning-slices slice 1; DECISIONS "M13").

**Classification: environment-level (the journal), framework-level (the
semantics).** "Always restores to its exact last state" *is* a durable,
crash-consistent journal of state changes — that is E1, the core's first
job. Which entries mean "design commit" vs "data edit" is versioning
semantics, installable above it.

### B4. Commit / branch / publish — design versioning with the authority inversion

**What it is.** A design commit is a `Commit` row (message, parent,
mergeParent, logSeq, cached canonical text + name-path→id map) minted by
`sys.commitDesign` as ONE atomic log entry; branches are `Branch` rows;
design-data + history are truth, the app file is a publish artifact, boot
is one-time adoption (versioning-slices slices 2–3; DECISIONS "M13").
`Commit`/`Branch` are `locked` — writable only by host actions.

**Classification: framework-level, with the data stored app-level.** The
striking deenv result: version history is *ordinary rows in the designer's
own store*, governed by ordinary access rules. The reboot should keep
"history is data" — it needs only E1 (durable log) and E5 (privileged ops
minting locked rows).

### B5. Rename-safe migration — identity diff, boundary entries

**What it is.** Publish diffs two commits by *intrinsic identity* (the
per-commit idMap), so a rename is a rename, never remove+add; renames carry
data through a deploy; the publish writes a boundary-marked log entry
recording the commit it migrated FROM (versioning-slices slice 4; DECISIONS
"M13": a parent-walk heuristic was proven wrong — boundary markers are
load-bearing).

**Classification: framework-level.** Wholly determined by A1's intrinsic
identity plus B3's log. The environment need only not destroy identity
across part updates.

### B6. Conflict handling — field-level three-way

**What it is.** Origin-keyed three-way merge over branches
(`sys.createBranch`/`sys.mergeBranch`, report + resolve-by-args); stale
commits with disjoint fields auto-merge (no retry storms); same-field
collisions return `{base, mine, theirs}` from the log's old-values and
render the keep-mine/take-theirs banner (versioning-slices slices 5–6;
app-versioning-design.md §1).

**Classification: framework-level.** Merge policy over the versioning
part. The three-way *base* comes from the log (B3/E1).

### B7. Time-travel clones

**What it is.** `sys.cloneInstance(id, atSeq)` materializes an instance as
it was at a past log seq, with exact era-schema resolution via boundary
base-commit stamps (versioning-slices slice 7). Branch-on-real-data =
publish to a clone.

**Classification: framework-level** on the E1 log. Also the designer's
safe-experimentation story ("fork disposable instances per candidate
commit"), which the reboot wants for *system parts* too.

### B8. Semantic migrations + revert

**What it is.** A commit can carry `fn Type(old)` migrations run through
the C# interpreter at publish (collapse-step-collapse range walking, one
boundary entry, dry-run parity, crash re-stamp guard); `sys.revertCommit`
restores the previous commit by identity with literal-id resurrection and
identity re-add restoration of historical values; `Commit.revertMigration`
carries authored reverse migrations (AGENTS "Current focus"; DECISIONS
"M13"; `DeEnv/Code/MigrationRunner.cs`).

**Classification: framework-level.** User-authored code run with authority
at a controlled moment — needs E2 (run code) + E4 (gate it) + B3 (the
history to restore from).

### B9. Evaluating in-progress (possibly broken) designs

**What it is.** The designer previews a design under construction:
`sys.evalContext(design, …)` builds an evaluation context over the draft,
`sys.renderTree` renders a canvas preview, and a degraded eval context
"chips its leaves instead of lying" (commit fb68372) — broken pieces
degrade locally instead of failing the whole preview.

**Classification: framework-level (design tooling), resting on an
environment-level property.** The core must make it cheap and safe to
evaluate code that is wrong — sandboxed, effect-free (the immutability
unifier: run it, discard the write overlay, keep the reads — DECISIONS
"Client data layer"). A live-first environment without this is a
production console, not an IDE.

### B10. Host-action authority (`sys` subject)

**What it is.** Kernel ops exposed to Code are gated by the same access
floor via the `sys` subject, deny-by-default even when the app's data rules
are open; only the designer grants it (`instances/1` access block;
DECISIONS "M-auth — Extended 2026-07-02").

**Classification: environment-level.** This is the self-hosting keystone:
the environment's own management surface is (a) callable from inside, (b)
denied by default, (c) granted by data. Without it "the designer is just an
app" is unsafe; with it, no privileged binary is needed.

---

## C. The trichotomy, rolled up

### Environment-level — the irreducible live core (5 capabilities)

| # | Capability | Grounding |
|---|-----------|-----------|
| E1 | **Durable identity-bearing state with a crash-consistent, append-only journal.** Restore-to-exact-last-state IS the journal; intrinsic identity must survive everything (renames, part updates, restores). | INSTANCE_MODEL identity; M13 slice 1 WAL/fsck; "the log is the only non-derivable artifact" |
| E2 | **A live code runtime** — load, replace, and execute code at runtime, including effect-free/degraded evaluation of code that may be wrong. | M6 interpreters; immutability-unifier; degraded eval contexts |
| E3 | **Part hosting + supervision with session continuity** — add/remove/update parts (including stateful ones) with no restart, no browser refresh, no dropped live sessions; a failing part degrades loudly and locally. | M10 kernel host; per-instance boot isolation; non-destructive apply; warm WS sessions |
| E4 | **A non-bypassable effect floor** — every effect (data access, network, host op) from installed/app code passes a gate the core owns; deny-by-default for authority. | M-auth "kernel floor, below Code, on the store/wire seam"; `sys` subject |
| E5 | **Self-management exposed inward** — the environment's own operations (create/clone/delete/publish/replace-part, render-time compute delegates across parts) are callable from governed code as data-returning or effectful ops. | M10 host actions; AGENTS rule 12; `sys.publishPreview`'s cross-instance delegate |

### Framework-level — installable system parts

- **Data-model part**: typed object model (A1), app-document format +
  round-trip printer (A2), mutation model / ctx / unified commit (A9),
  access-rule language + login components (A7 surface).
- **Web-UI part**: twin-lockstep isomorphic execution (A3 surface),
  generic UI (A4 — already Code today), components/reactivity (A5),
  SSR/hydration/SPA pipeline (A6), render-as-planner data layer (A8),
  server-backed-read convention (A10).
- **Versioning part**: non-destructive apply (B2), commit/branch/publish
  semantics (B4), rename-safe identity diff (B5), three-way merge +
  conflicts (B6), time-travel clones (B7), semantic migrations + revert
  (B8).
- **Design-tooling part**: meta-schema + bridge (B1), draft evaluation +
  canvas preview (B9).

### App-level — ordinary apps in the new world

- The reference apps themselves: todo (`instances/2`), devlog
  (`instances/5`), CRM shapes (`instances/3`/`4`/`6`) — types + seed +
  optional custom render + access rules, nothing else.
- **The designer/IDE** — the decisive one: today it is 1460 lines of the
  same app document as everything else, holding only data (meta-rows,
  Commit/Branch rows) plus granted `sys` authority. The management
  *experience* stays app-level; only the *mechanism* is core (M10
  discipline).

## D. What this means for the reboot

1. **The core to build first is E1–E5, nothing more.** Every reference
   capability decomposes into those five plus installable parts. deenv's own
   history validates the direction repeatedly: the C# auto-form was deleted
   in favor of a Code library (M9), the C# per-URL dispatch collapsed into
   one Code render (M11), the designer is an app (M4/M10) — each time
   something moved *out* of the core, it got easier to change.
2. **The hard environment test is B2 under E3**: update a part that owns
   live state (a schema change on a running app) with no restart, no
   refresh, no data loss, session intact. deenv passes this today via
   publish+migrate; the reboot must pass it for *system parts* too
   (databases, endpoints, frameworks), which existing deenv never attempted
   — the kernel is still recompile-and-restart (DECISIONS "The self-hosted
   image", updated 2026-07-14 toward radical liveness).
3. **Identity and the journal are one decision, not two.** Rename-safety,
   conflicts' three-way base, time-travel, revert-with-resurrection all key
   off intrinsic identity recorded in the append-only log. If the reboot's
   E1 gets identity wrong, the entire versioning part becomes unbuildable
   above it.
4. **Authority-as-data (B10/E4) is what makes "starts empty" viable**: an
   empty environment plus a granted management capability is enough to
   install everything else from inside.
