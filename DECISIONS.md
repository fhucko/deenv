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
  the process dies mid-write. The cheap fix was taken: writes are
  write-to-temp-then-atomic-rename, so a reader never sees a half-written
  file.
- **Isolation (the I in ACID)** — concurrent-writer safety. Not needed until
  the multi-user milestone.

**The document is held in memory.** It is loaded (and validated against the
app's types) once at startup and kept as the authoritative copy: reads serve
from it, a mutation edits it in place and then rewrites the file for
durability. This drops the per-operation read-and-reparse the original design
did on every call. It is correct *because the instance is single-process* —
nothing else writes the file behind our back, so the in-memory copy can't go
stale (the cross-process version of that is the same class of problem as
cross-session staleness — the real-time/multi-user milestone). Still a plain
JSON file behind the interface; not a storage engine.

**Critical seam:** all storage access goes through an interface, never
direct file calls. Principle: the interface speaks the **model's terms —
paths, nodes, dictionary entries — not flat key-value** (key-value is the
wrong vocabulary for navigable nested data, and a thicker model-shaped
boundary is what later lets temporal/render-coupled engines express
operations like "node at path P as of time T"). Exact signatures are TBD in
code — propose them in plan mode against something concrete. This interface
is what makes every later storage swap safe.

**Custom storage engine — the only storage milestone.** A bespoke engine
built ground-up, render-coupled from the start — no SQLite/Postgres interim
step. The plain JSON file stays until the real-time / multi-user milestone,
where a lightweight concurrent safety fix (write-lock / atomic rename) is
added inline. A full custom engine is deferred until after the code milestone, UI
customization, schema versioning, and real-time milestones, because:
- It needs a renderer rich enough to couple to. The engine's value is knowing
  what the UI is about to render — that signal only becomes meaningful once
  lists, relationships, custom UIs, and real-time views exist.
- The API is not yet designed. It must support data filtering at fetch time
  and participate in rendering to determine what to load, preload, and cache,
  including for custom UIs.

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

## Milestone 4 — the schema designer is self-hosted, not a separate tool

**The roadmap's M4 ("a web canvas to create tables, columns, and relationships
visually") was delivered as self-hosting rather than a bespoke designer UI.** The
designer *is* the instance runtime running a hand-written **meta-schema**
(`DeEnv/meta.schema.json`): a `Db` holding `types` (a dictionary of `MetaType`),
each `MetaType` holding `props` (a dictionary of `MetaProp`). You author a schema
as ordinary data through the generic nested-dictionary UI that already exists.

Reasoning:
- **A schema document maps directly onto the existing instance model** —
  objects + base fields + dictionaries-of-objects — so the designer's data layer
  needs no new engine. M2/M3 already render and CRUD exactly this shape.
- **A dedicated card-grid designer would be throwaway.** Making it *look* like a
  designer (card grid, dropdowns, inline validation) is the future **UI
  customization** pillar (VISION "user-controlled rendering"); building a one-off
  now would be replaced when that lands. The generic UI is accepted as the M4
  authoring surface.
- **The instance runtime stays untouched.** Only the composition root
  (`Program.cs`) changed. The renderer, WebSocket handler, storage, loader, and
  `instance.ts` are not modified — the designer reuses them verbatim.

**The bridge (`DeEnv/Designer/SchemaBridge.cs`)** projects the designer's node
data into a canonical schema document and **validates it with the normal loader**
(`InstanceDescriptionLoader.Load`), then publishes it as `instance.schema.json`
and resets the instance data. `Project` is pure (testable without a server);
`Export` does the file orchestration. It lives *beside* the runtime, not inside
it.

**An explicit `order` (int) field** on `MetaType`/`MetaProp` future-proofs
prop/type **ordering**: auto-int keys only encode immutable creation order, so an
explicit field is what makes reordering (and insert-between) possible. The
projection sorts by `order` then key and strips `order` from the output. Works
today as a plain number input; later drag-drop just writes the same field — no
schema or projection change.

**Mode switching is configuration, not code branches.** `Program.cs` reads a
`--mode` arg (`instance` | `designer` | `export`); `instance`/`designer` share the
identical hosting block with different schema+data files, and `export` runs the
bridge and exits. Visual Studio launch profiles (`Properties/launchSettings.json`:
**Instance / Designer / Export schema (bridge)**) drive it from the run-button
dropdown.

**Deliberately NOT built (still future):**
- **UI customization** — a real card-grid/drag-drop designer surface.
- **An in-app "Run" button** — turning designer data into a running instance from
  *inside* the app is the first action/effect primitive, i.e. the
  code milestone. Export stays an external VS mode until
  then; the same `Project` function gets reused behind an action when it arrives.

## Milestone 5 — the object model (identity, references, sets, no ownership, GC)

**M5 was redirected from schema versioning to giving the data a real object
model.** Today the data is a pure containment *tree*: a prop is a scalar, an
inline object, or a dictionary that *owns* its children. There is no way for one
entity to point at another. This milestone makes it a C#-style **object graph**.
(The redirect path: versioning → computation → references → identity-first →
this. See "Schema versioning is postponed" below.)

**Intrinsic identity on non-constants.** We draw C#'s value-vs-reference line:
scalars (`bool/int/decimal/text/date/datetime`) are value types with no identity;
**objects and dictionaries carry an intrinsic identity.** Identity is *not* a
schema-declared per-prop thing — it is intrinsic to being a non-constant, so the
schema is unchanged. This is the key enabler and it pays off twice: references
need it, and it makes a future *structural* schema diff able to tell rename from
remove+add (identity, not name, is matched) — which is why versioning can wait.
- **Format: monotonic `int`** (GUIDs rejected as ugly). To stay honest about the
  known multi-device future, the int space will later be partitioned into
  **reserved ranges per node** — no central-counter assumption is baked in now,
  same spirit as the storage-interface seam.
- **Stored as a tagged envelope in a per-type extent** (e.g.
  `{ "type":"object","typeName":"Customer","id":5,"fields":{ "name":{"type":"text","value":"Ada"} } }`),
  because identity is metadata, not a field. Every value — scalars and object
  references alike — is a tagged object (`{ "type":"…", … }`); there are no raw
  bare values. The `root` is an object reference into `extents.Db`
  (`{ "type":"object","typeName":"Db","id":1 }`), or a scalar tagged value for a
  scalar Db. This is a real storage-format evolution; the format is now uniform
  and the legacy dual-mode path is gone.

**References, no ownership (object graph).** "There is no owner, just like in
C#." A `Dictionary<int,Customer>` doesn't *own* its customers; it holds
references to objects on the heap. So:
- Objects live in **per-type extents** — a flat id-keyed pool per type.
- **A single object-typed prop *is* a reference**, and object-typed collection
  entries hold references too. Verified safe to repurpose the single+object-type
  combination: today the renderer routes such a prop to `AppendInput`, which has
  no `ObjectValue` case, so it renders a dead `<span>` — nothing meaningfully
  embeds a single object today, so this breaks nothing.
- **No `relation`/`target` schema fields.** The prop's **type is the candidate
  source**; intrinsic identity makes "all objects of type T" enumerable
  (type→id index). Candidate pool = all objects of that type (sufficient now).
- The same object can be referenced from many places and is genuinely *one*
  object — that shared-object identity is the proof the model works.

**Sets — and auto-int dictionaries of objects retire.** With intrinsic identity,
a `dict<Object> auto-int` key is a redundant surrogate id. Three collection
shapes, split by *what supplies the key*:
- **set** — objects keyed by their **own identity** (no surrogate). Replaces
  every auto-int dict-of-objects. URL segment = the object's id.
- **dictionary** — a genuine map where the **key is meaningful data you chose**
  (e.g. `settings`, scalar values). **Kept.** `keyGeneration: auto` largely
  retires.
- **single** — one.

**Addressing keeps today's navigation.** A URL is a **walk through the graph
from the root**, not an ownership path. Each collection addresses a slot by what
identifies it there: **set → member identity** (`/customers/7`), **dictionary →
key** (`/settings/europe`), **single → field name**. The same object may be
reachable by multiple routes (it's a graph) — there is no single canonical URL;
an **id-route fallback** (`/~/42`) follows a bare reference not reached through a
named collection. A dict-of-objects entry is addressed by its key; the object's
identity stays internal.

**Lifetime by GC** (chosen over explicit-destroy + dangling). No owner ⇒ delete
splits into *unlink* (drop a reference) vs *destroy*. Lifetime is **mark-sweep
reachability GC from the root (`Db`)**: an object no reference can reach is
collected.

**UI: pick-existing-or-create-new.** A reference field / set "New" lets the user
pick an existing object of the type or mint a new one into the extent.

**Scope honesty (ground rule 10).** This grew from "a reference feature" into a
**storage reconception** — normalized per-type extents, an identity-addressed
graph, and GC — which touches the storage foundation earlier than M6 planned.
That was flagged and chosen deliberately. It is sizeable; build it in thin
slices. **Slice 1** (`93d972e`): identity + per-type extents + a **set** of
references + identity addressing + pick-or-create + GC. **Slice 2** (`5a1392f`):
all `dict<Object> auto-int` props migrated to sets; uniform tagged value format
everywhere (no dual-mode); `keyGeneration`/auto retired; `kind`→`type` on the
wire. Proven by "the same object via two references is one object" and "dropping
the last reference collects it." Remaining: teaching the designer to author sets/refs.

## Schema versioning is postponed (was Milestone 5)

**Git-style schema versioning is no longer next**, and will not be built as
bespoke snapshot/diff C# now. It will be implemented **in the environment itself,
after a code milestone exists.** Reasoning:
- **It is behavior-shaped, not data-shaped.** M4 could self-host because
  *designing a schema is data* and the runtime already edits data. Versioning is
  *behavior* — commit, hash, walk a parent DAG, diff — and there is no
  computation/effect primitive yet. "Do it in itself" therefore depends on that
  primitive existing.
- **It rides the storage foundation M6 reshapes,** and is the sibling of the
  future *data-level temporal versioning* — cleaner to do both together on a real
  store than to build a throwaway JSON-snapshot git on plain files now.
- **The one reusable piece is the diff,** not the store: a **structural,
  identity-based** schema diff (over the designer data, which is identity-keyed),
  so renames are exact rather than line-based remove+add. That design survives;
  the snapshot/branch/merge machinery waits. Branches and 3-way structural merge
  are themselves later sub-milestones.

## Concurrency, saving, and locking (eshop/CRM)

Primary early use cases are custom eshop and CRM — multi-user domains where
records are edited concurrently. Decisions:

- **Version stamp: deferred (revised in Milestone 2).** Originally planned as a
  per-entity stamp "from Milestone 1 onward," it was never built and has been
  **deferred to the multi-user / optimistic-concurrency milestone**. Reason:
  *where* the version belongs is genuinely unresolved — a per-object stamp
  resets when the whole DB is replaced (so a whole-DB version is needed too),
  and which granularity optimistic concurrency wants is a decision best made
  with that milestone's full context. Nothing reads a version yet, so there is
  no cost to deferring; the stored file shape carries no version field today.

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

- **Save UX (revised in Milestone 2): explicit Save on every node.** There is
  no immediate save-on-change; every editable node — object forms *and* single
  leaf values, including bool checkboxes — commits via a Save button. This is
  one consistent interaction (no toggle/form split) and is the natural
  version-commit unit for the future optimistic stale-version flow, whereas
  immediate per-field saving would fight it. Supersedes the earlier "toggles
  save immediately" rule.

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

## Code

Code is not just a parser — it's a type checker, runtime, debugger, standard
library, editor tooling, and docs. Building it now also means no existing
ecosystem helps. Early milestones use **TypeScript** for generated code,
inheriting its editor and debugger for free.

**Code starts interpreted — no host platform required.** The earlier "waits
until there is a platform" framing is retired: interpretation is the platform.
Stored internally as a JSON object tree (the same model the rest of the
environment uses); presented as editable text only when the user is editing
it. The milestone is placed before UI customization and schema versioning
because both depend on code being present.

**Known uses code must cover:**
- Filter expressions on data in custom UI views (e.g. `task.done == true`
  narrows a list to matching objects). This is the first concrete surface —
  a filter predicate evaluates against the object model and returns a
  filtered set.
- UI rendering logic (what to show, how to lay it out).
- Schema versioning behaviour (commit, diff, walk history) — built inside
  the environment.

What remains deferred: a compiled target, a type checker, a standard library,
and editor tooling. Those come once real users can inform them.

## Code execution model: client/server, three states, and action queuing

**No server/client distinction for the user.** Code looks the same everywhere.
The runtime routes execution; the user never specifies where something runs.

**Two kinds of operations:**

- *Expressions* (conditions, filters, computed values) — pure and deterministic
  over their inputs. Run anywhere; client and server always agree given the same
  data. Apparent disagreements are data-staleness conflicts, not expression
  conflicts — handled by the real-time conflict model, not by code.

- *Actions* (mutations, effects) — the client executes optimistically; the
  server is authoritative. If the action needs data not in the client working
  set (not loaded, or security-sensitive), it runs server-side instead — to
  avoid loading data to the client just to run a computation, and to keep
  sensitive data off the client.

**Client three states:**

Every loaded object set exists in three simultaneous states on the client:

1. *Server data* — last server-confirmed state, updated by real-time pushes.
2. *Client before* — snapshot at the moment the client first diverged from the
   server (the fork point). Stays fixed while an editing session is open.
3. *Client after* — current optimistic state; accumulates all local action
   results.

All three are needed for proper conflict resolution:
- `server data ≠ client before` → a concurrent server change is detected
- 3-way merge: base = client before, ours = client after, theirs = server data
- Auto-merge when different fields were changed on each side; surface a
  resolution UI when the same field was changed on both sides
- `client before` is the clean rollback target if the user abandons changes

**Local change journal:**

The client journals every field change made by client-side actions at field
granularity. Used for rollback on server rejection (replay in reverse to reach
`client before`) and for the 3-way merge (makes `client after` inspectable
field by field).

**Action queue — parallel by default, serial when dependent:**

Multiple actions can be in-flight simultaneously. They are parallel by default:
both accumulate field-level changes into `client after`. The runtime serializes
only when a data dependency is detected — B reads a field A wrote, or both
write the same field. On server rejection of one action, only actions that
depended on it need rollback or replay; independent parallel actions are
unaffected. If a conflict resolution UI is open, new actions are blocked until
the conflict is resolved — you cannot queue on an unresolved base.

## Code milestone — how it was delivered (M6)

Extracted from the app14/app15 prototype and adapted onto the M5 object model;
the prototype folders were the port reference (untracked) and are deleted once
the milestone lands. Key decisions made while landing it:

- **Hand-written AST, no parser.** All code is JSON AST in the schema document
  (`ui` + `common` sections), `"type"`-discriminated, camelCase via the shared
  serializer options. Text syntax is a future layer.
- **Twin interpreters, conformance-guarded.** The C# server interpreter
  (`DeEnv/Code/CodeExecutor.cs`) and the TS client twin
  (`DeEnv/Instance/codeExec.ts`) are hand-maintained; a shared suite
  (`DeEnv/Code/conformance.json`) runs through both, so drift fails a test.
- **One Code array.** A single kind-tagged runtime collection
  (`ExecArray { id, kind: set|dict|list, items: [{key, value}] }`) is used
  byte-for-byte the same on the server, the wire, and the client; the
  storage↔runtime bridge (DbBridge) is the only shape boundary. "In db" is the
  id sign (positive = persisted, negative = transient) — no flag.
- **Privacy is structural — the memo cache.** Every computation boundary
  (fn call, where/orderBy) is memoized by (function id, argument identities)
  with its result and dependency REFS (object props, set membership, UI-state
  vars) — never input values. Data read only inside a value-returning
  computation never ships; no `sensitive` flag exists. A TAG-returning
  computation is display, so its reads ship (rendering a field = choosing to
  expose it). Identity-creating factories (result = transient object minted
  inside) are never cached; event handlers bypass the cache (side effects).
- **First paint never calls the server**; later, a stale or missing value
  triggers a `refetch` — the server re-renders, **always over a fresh load
  from the store** (the single source of truth), so the result reflects every
  committed change. A per-client `clientId` (minted at SSR, shipped in the
  page, claimed by the WS `hello` within a 10s window) is the seam the
  real-time milestone will hang push on, but it carries no data — there is no
  per-client warm graph to keep in sync or to go stale against another
  session's change. (Earlier the session mirrored each mutation into a warm
  graph and refetch rendered over it; that cache could diverge from the store
  on a cross-session change, so it was removed — see the cleanup note below.)
- **Optimistic mutations are provisional.** Each is journaled (field-level,
  with captured before-values) and sent with a correlation id; an error reply
  reverse-replays the journal past the failed entry (rollback), an ok commits
  it. This is the delivered core of the three-state model (server-data =
  current state with the journal undone); the action queue and 3-way merge
  stay with the real-time milestone.
- **Drafts are client-owned.** A transient object in client state ships
  complete and is never overwritten by a refetch; server re-render transients
  mint below the client's id floor so negative ids never collide.
- **initialData** — the schema document carries a hand-authored normalized
  seed (extents; plain scalars, sets as id arrays, refs as bare ids) applied
  by the store on first run. The committed default instance is the todo app.

## The app document — one text file, JSON internal only (M7)

An instance is described by ONE hand-editable text document (`instance.app`):
`types`, an optional `initialData` seed, and code (`common`/`ui`) in an
app.txt-style indentation language. Decisions made landing it:

- **JSON is retired from authoring.** The earlier JSON schema document and the
  brief codeFile-sidecar form are gone; `InstanceDescription` and its JSON
  serialization are internal — the in-memory model and the wire. The client
  still receives the Code AST as JSON (`initUi`); there is no TS parser, text
  never crosses the wire.
- **One grammar, ported from the prototype** (`app14/DeEnv/Parsing` +
  `CodeParse`), upgraded with an offset cursor (no substring slicing) and a
  failure high-water mark (a broken document reports line/column). Combinator
  semantics were kept identical — `Many1` yields shorter matches first and the
  grammar relies on that backtracking — and section/document mapping runs only
  AFTER the full-input parse is chosen (mapping inside a combine throws on
  partial candidates).
- **The printer is the inverse.** `AppPrint`/`CodePrint` emit the canonical
  form (four-space indent, minimal parens by precedence, vars→fns→render);
  `parse(print(d))` is the identity and the canonical text is a fixpoint —
  this is how the designer will present stored code as editable text.
- **The designer bridge publishes text**: `Project` builds the typed
  description, the shared validation pipeline runs on it, `AppPrint` writes
  `instance.app`. Two M3 validation rules became inexpressible in the text
  format (object type without props; non-object type with props) — they
  remain enforced semantically and are exercised through the bridge.
- Grammar fixes over the prototype: `0` literals, text escapes, parentheses,
  a real postfix chain (`a.where(p).orderBy(k)`), static arity checking.

## Stored data must match the running app (startup guard)

Found in the field: a data file left behind by an older schema made the todo
app half-work — the first paint rendered, but every mutation was rejected
server-side ("No set with id 0") and reloads lost all changes. The store only
seeds when its data file is missing or empty, so it silently ran over stale
data. Decisions (specced by `StoredData.feature`):

- **Fail loudly at startup, never reseed over an existing file.** On
  construction over an existing data file, `StoredDataValidator` checks the
  document structurally against the app's types: unknown extent types,
  stored fields the app doesn't declare, a stored kind contradicting the
  declaration (scalar/set/dictionary/reference, scalar tag vs base type),
  references to objects that don't exist, and legacy collections without
  intrinsic ids are all rejected with the file path, the offending detail,
  and the remedy named (`StoredDataException`; Program.cs exits 1). Deleting
  or moving the file is a deliberate user act — data is never dropped
  silently.
- **Additive evolution stays cheap.** A declared prop missing from stored
  fields is fine (reads fall back to defaults), so adding a prop to a type
  does not invalidate existing data. This is a structural-compatibility
  guard, not schema versioning — versioning (migrations on shape changes
  that DO conflict) stays a postponed milestone.
- **One data file per app document** (`<stem>-data.json` via `AppPaths`,
  always in the run directory), so `--app` switching never mixes data. The
  bridge's export deletes the target data file before reseeding — an export
  deliberately replaces the instance's data, and is the one path allowed to.

## UI customization — views, a per-request rendering decision (M8)

Rendering was all-or-nothing (a `ui` with `fn render()` owned every URL, or the
generic auto-form owned all of them). M8 added **views** — render functions
bound to a type or a path — so an app customizes parts of the generic UI or
takes over a subtree. Decisions:

- **A rendering-function decision, generic fallback.** `SsrRenderer.ResolveView`
  picks per request: longest segment-aware path-view prefix → that view; else a
  type view when generic routing resolves to that type's object page (and no
  segment is a dictionary — dict entries aren't in the Code runtime); else the
  generic auto-form. So an app's customized pages and its generic pages coexist
  in one URL space.
- **`fn render()` becomes optional — the implicit root view `"/"`.** A
  whole-app takeover (the todo app) is just `render`; partial-override apps
  define no render. User was explicit: don't force a render function for
  partial overrides.
- **A view page is a full code page.** The view fn takes `render`'s place in the
  pipeline (memo cache, two-way binding, WS mutations, journal/rollback,
  warm-session refetch). The routed object / path binds as a CALL ARGUMENT, not
  a top-scope var — so it never ships as scope or is overridden by a stale
  client var, and refetch re-resolves the same view from the path it carries.
- **Type-view pages keep the generic breadcrumb chrome** (plain server links,
  no client JS); path views and the root render own the page. Every code page
  now mounts into `<div id="app">`.
- **Designer view-editing, fragment-level islands, and the self-hosted generic
  UI are deferred.** The last became M9 (now in progress, slice 1 landed) — a
  *reflective library* (`objectForm` over schema-as-data) plugged in at lowest
  dispatch precedence; views are the seam it uses. See the M9 section below.

### Refetch reads the store, not a per-client warm graph (cleanup)

The Code milestone kept a per-client warm ExecObject graph (the session) that
each mutation mirrored into, and `refetch` re-rendered over it. That graph is a
second source of truth that only the owning session updates, so any change from
**another session** (each tab/WS connection is its own session = its own user)
left it stale — and a refetch trusting it could silently drop committed changes.
Since the store already holds every change from every session, refetch now
re-renders over a **fresh store load**; the per-mutation graph mirroring is
deleted and the session shrinks to the clientId/claim handle. Behavior is
unchanged for a single session (its warm graph held exactly its own persisted
mutations); a refetch now also reflects other sessions' committed changes when
it runs. Making a page refetch *automatically* on an external change (vs. on its
own next interaction) — live push — stays the real-time/multi-user milestone.

## Self-hosted generic UI — slice 1: object forms via synthesized views (M9)

The next milestone after M8: re-express the C# generic auto-form in the Code
language itself, so the bespoke renderer (and the separate generic client,
`instance.ts`) can eventually retire. Decided to deliver it as **slices**; slice
1 is the generic **object form** for an all-scalar type, proven end-to-end. The
hard sub-problems (set tables, reference pick-or-create needing extent
enumeration from Code, and dictionaries — which aren't in the Code runtime, a
roadmap-future layer) are later slices. Decisions:

- **Reflection = schema-as-data, not new metadata APIs.** The library
  `objectForm(obj, meta)` iterates an ordinary Code value `meta: { name, props:
  [{ name, baseType }] }` with the existing `foreach`/`.prop`. No new
  introspection built-ins; the type descriptor *is* data.
- **Three builtins, all schema-driven.** Code had no `obj[name]` (object-prop
  access requires a literal symbol), so a form driven by schema-iterated names
  needed primitives:
  - `field(obj, name)` — dynamic by-name access, the reflective twin of
    `obj.member` (same dep/leaf bookkeeping on the server). Its client `setValue`
    **autosaves**: it persists each edit over the per-field `objectPropChange` WS
    op immediately, like static `obj.member` — no Save button. (A Save button +
    a `save()` builtin with staged edits was tried for C#-form parity, then
    dropped: autosave is consistent with the reference picker and the reactive
    code pages, and less ceremony — see the minimal-by-default principle.)
  - `humanize(text)` — a prop name → a label ("companyName" → "Company name"),
    so labels match the C# form. Canonical impl in `DeEnv.Code.TextUtil` (shared
    with `SsrRenderer`), mirrored in `codeExec.ts`.
  Both are in both twin interpreters; both have conformance cases; the validator
  knows them as builtins.
- **Synthesis over a runtime `schema` global.** Rather than ship a schema and a
  new `ViewKind.Generic` + client bootstrap, an opted-in app gets, at render
  time, a synthesized `view T(obj)` per all-scalar object type without an
  explicit view; its body is `return objectForm(obj, { …descriptor literal… })`.
  This **plugs into M8's type-view dispatch unchanged**, ships through the
  existing views channel (the descriptor rides as a Code literal in the view
  AST — nothing new on the wire), and needs no `init.ts` change. The client's
  `ExecObject` carries no type name, so passing the descriptor as a call
  argument (not `typeOf(obj)`) avoids a wire change; `typeOf` is deferred to the
  slice that recurses into nested objects.
- **Opt-in is a model flag, not a test hack.** `generic` in the `ui` section →
  `InstanceUi.Generic` (parsed, printed, round-trip tested). It scopes the
  self-hosting so existing apps (todo/shop/crm) are untouched, and is the
  migration seam later slices widen until the C# renderer retires.
- **Synthesis stays render-time; the canonical description is pristine.**
  `GenericUi.Effective` builds an augmented `InstanceUi` (library functions +
  synthesized views, renumbered via `CodeIds` for stable memo keys) used by
  `SsrRenderer` for both the server render and the shipped client AST. `AppPrint`
  still prints the original description (just the `generic` flag), so parse/print
  round-trips are unaffected.

Slice 1 is specced by `SelfHostedUi.feature` (`@milestone-9`); the all-scalar
`Note` object page renders via `objectForm`, edits persist over the WS, input
kind follows `baseType`, and the Db root stays the C# auto-form.

### Slice 2: references (pick-or-create editor)

Self-hosts the reference editor — a reference **route** (`/lead`) and a reference
**field inside an object form** (`Note.author`) — so a type whose props are
scalars or single references renders entirely in Code. Tried making slice 1 the
default first; it broke (a single-reference route → NotFound when unset, losing
the C# reference editor; the designer also leans on creation/sets/dicts), so the
default stays blocked and references is the next necessary step. Decisions:

- **`extent(typeName)` rides the memo cache, not a wire dump.** The picker's
  candidates are a memoized computation (like `where`/`orderBy`, key
  `extent:<Type>`); only the displayed option labels+ids ship as leaves, so the
  privacy model holds. The client reuses the shipped result; a mint/`setRef`
  coarsely stales `extent:*` → the existing `maybeRefetch` re-renders fresh.
  Labels are read **inline in output position** — wrapping them in a value-
  returning helper made the reads private (unshipped) and the picker vanished on
  hydration.
- **One id-addressed `setRef(obj, prop, value)`** covers both the route and an
  embedded field, because a route *is* a reference prop on its parent object. New
  WS op `setReferenceField { objectId, prop, refId|value|clear }` and store
  method `WriteReference(objectId, prop, targetId, type)` — addressed by id like
  `objectPropChange`, not by path (the path-based `setReference` stays for the C#
  editor). GC runs after, so clearing the last reference collects the target.
- **Reference-route dispatch reuses type-view binding.** `ResolvedTypeInfo`
  gains `IsReference` (the final segment is a single object-reference prop, not a
  set member). A synthesized **reference view** is keyed by (owner type, prop)
  via `UiView.Prop` and bound to the **parent** object — so the unset case is the
  empty editor, not `NotFound`, and the client needs no new wiring (it binds the
  parent by id exactly like a type view; the prop + target descriptor ride as
  literals in the view body).
- **Create-new is deferred.** A draft that accumulates typed fields before mint
  needs persistent per-field state (a top-scope var), which doesn't fit a generic
  stdlib function cleanly. Slice 2 ships pick-existing + clear; create-through-
  reference is a focused follow-up.

Specced by `SelfHostedUi.feature`'s reference scenarios. **Still opt-in**:
default-on remains blocked until object creation, sets, and dicts are also
self-hosted (the designer's full needs).

### Component-local state — the creation foundation (verified, not yet shipped)

Self-hosted CREATION needs a form that accumulates a new object's fields before
minting (real systems create a record *with* its values, not an empty row first).
The clean mechanism is the **component pattern**: a function runs its body ONCE as
init (local state), returns a render function (the reactive part), and resets its
state after Create. Decisions, verified by a prototype scenario (`ComponentFormApp`):

- **Init-once falls out of the memo cache.** A memoized component call caches its
  result — the render *closure* — so the body runs once and the captured draft
  persists across renders; the returned render fn is the reactive part that re-runs
  on dependency change. No new "run-once" runtime concept.
- **State lives in an object PROP, not a local var.** `invalidateVar` is top-scope
  only, so a local `var draft` reassign wouldn't re-render; `invalidateProp` works
  at any depth. So the component holds a wrapper (`var state = { draft }`) and resets
  with `state.draft = getNew()` — prop-reactive, no new invalidation machinery.
- **`obj.prop = x` is the one enabling language addition.** Assignment gained an
  object-field lvalue (a symbol *or* a `.member` chain): `CodeAssignment.Target`
  is now `ICodeValue`, the parser has an `Lvalue`, and both interpreters set the
  field through the same path two-way binding uses (invalidate + persist when
  server-backed). Small and general — most languages have it.

Creation itself (a generic New form = this component over `objectForm` + a Create
button, and create-new in the reference editor) is the next slice, built on this.

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

## C# is the kernel — app logic belongs in the app

The guiding principle for what lives in C# vs. in the app: **C# does as little as possible.** Everything that can be expressed in DeEnv — using its data model, code, and designers — belongs there, not in C#. This extends the M4 self-hosting insight to the whole system.

**What is structurally impossible to express inside DeEnv (irreducible C# layer):**
- TLS termination — bytes on the wire, before the app can speak
- Cryptographic primitives — password hashing, token signing (need native code)
- The session token check — "is this request authenticated" must happen before app-level code runs
- File system, network, process — the OS boundary

**What belongs in the app, not in C#:**
- Login flows, registration, password reset
- User model, roles, permissions
- Auth provider integrations (OAuth, SAML, etc.)
- Session data model and issuance logic
- Permission and visibility rules (filter expressions over the object model)
- Devops workflows, instance management, versioning logic
- The dev environment itself (schema designer, instance creator — extending M4 self-hosting)

**Implication for security timing.** The thin C# layer (TLS + session token validation) can be built as an early hardening slice — it is small and well-defined. The auth model above it (login page, user schema, session management, OAuth integrations) requires the code milestone to exist first, because auth logic is behavior, not data. Build the seam early; fill the app side later.

**The dev environment is also self-hosted.** Schema versioning, instance management, devops workflows, and the designer itself are built in DeEnv using its own primitives. Only the irreducible OS/transport boundary stays in C#.

## Testing: BDD with Gherkin

Behavior is specced in Gherkin `.feature` files first, then made to pass.
Every scenario is tagged with its milestone (`@milestone-1`,
`@milestone-future`, etc.). A scenario whose lines span milestones is split.
A milestone-1 scenario must be passable with the milestone-1 stack — if it
isn't, it is mis-tagged. Green milestone-1 scenarios are the "is v1 done?"
signal; that signal only works if the tagging is honest.
