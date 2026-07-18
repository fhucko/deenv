# Durable lists + ExecSet / ExecDict / ExecList

**Date:** 2026-07-17  
**Status:** **DONE** (slices 1–5 landed 2026-07-17; commits through docs). List-level OCC deferred to general multi-user concurrency.  
**Scope authority:** product + technical resolutions in the durable-lists decision package (do not re-litigate).  
**Builds on:** M5 object model, M6 twin interpreters, unified-commit wire (`docs/plans/2026-07-12_220000-unified-commit-all-ops.md`).

---

## Goal

Make **list** a first-class durable cardinality (ordered sequence; duplicates allowed) end-to-end — schema, storage, runtime, unified commit, GC, ACL, publish reshape, generic UI — and retire the single kind-tagged **`ExecArray` / `ArrayKind`** runtime shape in favor of three sealed collection types: **`ExecSet` / `ExecDict` / `ExecList`**.

## Non-goals

- Index URLs (`/listField/0`)
- CRDT / OT list merge
- Set-of-scalars
- Real-time multi-user push
- Custom storage engine
- Changing set OCC commute rules (set link/unlink stay as today)
- **Per-node / collection OCC** (`listVersion`, `expectedListVersion`, …) — deferred to general multi-user/sessions work covering **all non-scalar mutable non-constants** (object + set + dict + list, …), not lists alone
- Dogfood / deploy schedule
- Using the word **multiset** anywhere in code, docs, or comments (list is list; set is set)

---

## Current baseline (verified)

| Layer | set | dict | list |
|--------|-----|------|------|
| Exec | `ExecArray` + `ArrayKind.Set` | `ExecArray` + `ArrayKind.Dict` | `ExecArray` + `ArrayKind.List` **ephemeral only** (neg id) |
| Schema `Cardinality` | yes | yes | **absent** |
| Storage | `StoredSet` / `SetValue` | `StoredDict` / `DictionaryValue` | **absent** |
| AppParse / AppPrint | `set of T` | `dict of T by K` | **absent** |
| DbBridge | load set | load dict + R7 owner | list only via `LoadExtent` / derived |
| Wire commit | `setAdd`/`setRemove` | `dictAdd`/`dictRemove` | **none** |
| URL | `/set/memberId` | `/dict/key` | **none** (INSTANCE_MODEL still says lists dropped) |
| Designer order | set + member `order` + `orderForAppend` / `moveRow` / `OrderedMembers` | n/a | n/a |

**Key files today**

- Runtime: `DeEnv/Code/ExecValues.cs` (`ArrayKind`, `ExecArray`, `ExecItem`, `ExecSysFunction.Target`)
- Twin: `DeEnv/Instance/codeExec.ts`, `DeEnv/Instance/dt.ts`, `DeEnv/Instance/ws.ts`, `DeEnv/Instance/workbench.ts`
- Bridge / emit: `DeEnv/Code/DbBridge.cs`, `DeEnv/Code/ClientState.cs`, `DeEnv/Code/CodeExecutor.cs`
- Schema: `DeEnv/Instance/InstanceDescription.cs`, `AppParse.cs`, `AppPrint.cs`, `TypeResolver.cs`
- Storage: `DeEnv/Storage/StoreModel.cs`, `NodeValue.cs`, `IInstanceStore.cs`, `JsonFileInstanceStore.cs`, `StoredDataValidator.cs`
- Wire: `DeEnv/Http/WsHandler.cs`
- Designer order hack: `DeEnv/Designer/SchemaBridge.cs` (`OrderedMembers`), `DeEnv/instances/1/app.deenv` (`orderForAppend` / `moveRow`)
- Docs to overturn: `INSTANCE_MODEL.md` L40–72, `DECISIONS.md` “One Code array” + M5 collections

---

## Target shapes (normative for implementers)

### Cardinality

```text
Cardinality = Single | Set | Dictionary | List
```

App syntax:

```text
items: list of Task          // objects (refs in slots; same id may appear twice)
tags:  list of text          // scalars (values in slots)
scores: list of int
```

**Set remains object-only** (intentional asymmetry). List accepts every value type the model already supports (object + scalar including password/image), applying existing leaf write/read rules **per slot**.

### Storage

```text
StoredList { Id: int, Items: StoredValue[] }
// object slots → StoredRef (same target id may appear at multiple indices)
// scalar slots → StoredLeaf (same rules as other scalar leaves)
// No per-list version field in this work — see Concurrency (deferred OCC)
```

API/read graph:

```text
ListValue(int Id, IReadOnlyList<NodeValue> Items)
```

JSON tag: `"list"` with `id` + ordered `items` array (not a map).

### Runtime (post-rename)

```text
IExecCollection (thin shared base/interface)
  Id, Items (or shared item shape), ElementTypeName?, Constant?, …
  ExecSet   — identity-keyed; positive id when durable
  ExecDict  — key-keyed; SourcePath / OwnerRef / DictProp when durable
  ExecList  — ordered sequence; positive id when durable; negative when ephemeral
```

- **No residual `ArrayKind` / `Kind` enum.**
- Wire / DT type tag is **`set` | `dict` | `list`** (not `type:"array"` + kind).
- `where` / `orderBy` always mint a **new ephemeral `ExecList`** (negative id).
- Assigning that result to a durable list prop + `ctx.commit` persists via **`ListReplaceMutation`** keeping the **same positive list id** (no list-version check in this work).

### ExecItem keys (normative)

| Collection | `ExecItem.Key` |
|------------|----------------|
| **ExecList** | **ordinal** `0..n-1` (never member object id — otherwise maps keyed by `item.Key` collapse duplicate object slots) |
| **ExecSet** | member object id (unchanged) |
| **ExecDict** | stable entry key / hash (unchanged) |

DbBridge list load must mint ordinal keys, not object ids.

### Foreach / slot-path keying (normative — duplicates)

Today foreach row identity often uses object id (`rowKey = obj.Id`). That **collides** when the same object appears in two list slots.

**Rule:** for `ExecList`, default foreach / component slot key = **ordinal index** (`item.Key`). Set/dict keep identity / entry keys. Opt-in `key=` still available when the author wants identity-stable rows (unique members). Product allows the same object at multiple indices; UI and slot paths must render **two independent rows**.

### List ops (Code surface)

| Method | Semantics |
|--------|-----------|
| `add(v)` | append |
| `insert(index, v)` | insert at index |
| `move(from, to)` | reorder |
| `removeAt(index)` | drop by index |
| `removeFirst` / `removeLast` | ends (optionally first/last match of a value — Code sugar) |
| `removeAll` | clear entire list |
| `removeWhere(pred)` | drop matching slots (non-matching slots keep order) |
| existing `where` / `orderBy` | new `ExecList` (ephemeral) |
| existing `any` / `single` | scan (list + set + dict where already sensible) |

Dict keeps `setEntry`; set keeps identity `add`/`remove`. Do not invent parallel vocabulary for sets. Prefer **omit** a bare `remove(value)` on list (set keeps `remove`) so set/list APIs stay distinct.

### Code → wire map (normative)

Wire has a **small** relation set. Product Code methods compile as:

| Code method | Wire / model op |
|-------------|-----------------|
| `add` / `insert` | `listInsert` (append = insert at `len`) |
| `move` | `listMove` |
| `removeAt` | `listRemoveAt` |
| `removeFirst` / `removeLast` | client resolves index → `listRemoveAt` (or rebuild → `listReplace`) |
| `removeAll` / `removeWhere` / assign list prop | **`listReplace`** (same list id) |

No extra wire kinds for the four remove* sugars. **Slice 3 MVP must ship `listReplace`**; prefer true fine `listInsert`/`listRemoveAt`/`listMove` in the same slice (not deferred open questions that block assign/`removeWhere`).

### Concurrency (normative — list-level OCC deferred)

**Decision (2026-07-17):** do **not** add list-only `listVersion` / `expectedListVersion` / `listVersions` ack in this work. Per-collection OCC only for lists is unclean; general multi-user / multi-session concurrency will land later as a **shared** mechanism (not a list special case).

**Now:**
- List mutations go through **unified commit** like set/dict membership.
- No per-list version field, no expected version on wire, no `CommitResponse.listVersions`.
- Concurrent edits: same class of behavior as set link/unlink today (no list-specific conflict protocol). Existing commit/`baseVersion` field-overlap rules for **object field** edits stay as they are; list ops do not invent a parallel OCC channel.
- Store HEAD / existing `newVersion` ack for the commit batch is enough.

**Later (multi-user/sessions track — out of this plan’s slices):**
- Unified concurrency for **all non-scalar mutable, non-constant values** — not lists alone: at least **object**, **set**, **dict**, **list** (and any other mutable non-scalar that is not a constant descriptor).
- Scalars / constant collections stay out of that OCC surface (or follow whatever the general design decides).
- Mechanism TBD then (version-per-node, etc.); must be one scheme, not a list special case.
- Do not pre-plumb list-only version counters “for later” in Slice 2–3 — avoid dead fields.

Do **not** change set commute / set OCC rules in this work either.

### URL / navigation

- Object members only: `/listField/objectId` requires **membership ≥ 1** (first occurrence is fine for navigation).
- **Never** index segments.
- Non-object lists: no member URL (edit only on owner form / generic list UI).

### ACL

- Membership mutations (add/remove slots that change who/what is referenced) ≈ **set membership** rules on element type / create-or-link.
- Reorder / whole replace ≈ **edit owner** of the list-holding object.

### Publish thin reshape

| From → To | Behavior |
|-----------|----------|
| single → list | wrap one value/ref into one-slot list |
| set → list | synthetic order by member id if no migration authored |
| list → set | dedupe by object id |
| designer set + `order` → list | **preferred via authored semantic migration** (`orderBy` old order, drop `order` prop) when the designer rewrite lands |

---

## Sequencing (hard)

| # | Slice | Intent |
|---|--------|--------|
| **1** | **Rename-first** | `ExecArray` → `ExecSet`/`ExecDict`/`ExecList`; list still ephemeral |
| **2** | **Durable list core** | schema + storage + bridge + GC + seed + thin reshape; docs may start here |
| **3** | **Ops + persistable assign** | model ops, wire relations, `ListReplace` (no list-version OCC) |
| **4** | **UI + designer order collapse** | generic list table; designer props → list; drop `order` scaffolding |
| **5** | **Docs finish** | INSTANCE_MODEL / DECISIONS / format / any residual comments |

Do not land durable storage before the runtime rename completes (or land them in the same PR only if Slice 1 is fully green first in that PR). Prefer separate PRs per slice.

---

## Slice 1 — Rename-first: ExecSet / ExecDict / ExecList

### Intent

Eliminate `ExecArray` + `ArrayKind` without changing persistence. All three kinds already exist at runtime; list remains **negative-id / ephemeral**. Behavior freeze except type names and wire type tags.

### Design

**C# (`ExecValues.cs`)**

```csharp
public interface IExecCollection : IExecValue
{
    int Id { get; }
    List<ExecItem> Items { get; }
    string? ElementTypeName { get; set; }
    bool Constant { get; set; }
}

public sealed class ExecSet : IExecCollection { … }
public sealed class ExecDict : IExecCollection
{
    public string? SourcePath { get; set; }
    public int? OwnerRef { get; set; }
    public string? DictProp { get; set; }
    …
}
public sealed class ExecList : IExecCollection { … }
```

- Delete `enum ArrayKind` and `class ExecArray`.
- `ExecSysFunction.Target` → `IExecCollection`.
- Dep / harvest types that pair `(ExecArray, ExecItem)` → `(IExecCollection, ExecItem)`.
- `SetJoin` holds `ExecSet` (not a generic collection).

**TS (`codeExec.ts` / `dt.ts` / `ws.ts` / `workbench.ts`)**

Replace:

```ts
interface ExecArray { type: "array"; kind: "set"|"dict"|"list"; … }
```

with three interfaces (or a discriminated union without a residual kind field on a shared “array” type):

```ts
type ExecCollection = ExecSet | ExecDict | ExecList;
interface ExecSet  { type: "set";  id; items; elementTypeName?; constant?; }
interface ExecDict { type: "dict"; id; items; elementTypeName?; sourcePath?; ownerRef?; dictProp?; constant?; }
interface ExecList { type: "list"; id; items; elementTypeName?; constant?; }
```

- `AppState.arrays` → rename to **`collections`** in the same PR if greppable; residual `arrays` name is **not** optional without a done-criterion comment “map of collections by id.” Prefer rename.
- DT leaves: `type: "set"|"dict"|"list"` refs, not `type: "array"`.
- `ClientState.cs` emitter must match (hard C#/TS lockstep cut).

**Mechanical rewrite sites (non-exhaustive but required)**

| Area | Files |
|------|--------|
| Runtime | `ExecValues.cs`, `CodeExecutor.cs`, `DbBridge.cs`, `ClientState.cs`, `MigrationRunner.cs` |
| HTTP / kernel | `WsHandler.cs`, `SsrRenderer.cs`, `PublishReportCode.cs`, `MergeReportCode.cs` |
| Client | `codeExec.ts`, `dt.ts`, `ws.ts`, `workbench.ts`, `init.ts` if it touches array types |
| Tests | `DeEnv.Tests/Steps/TransientIdSteps.cs`, any step casting `ExecArray`; store concurrency tests that touch collections |
| Conformance | cases that assert collection shape in results (usually opaque; update only if suite serializes type tags) |

Pattern: `switch` / pattern-match on concrete type or `is IExecCollection`; never reintroduce a Kind enum “for convenience.”

Shared helpers (optional thin static class, not a Kind enum):

- `CollectionMethods` dispatch still lives on executor; methods validate target kind (`setEntry` only on `ExecDict`, set persist only on `ExecSet`, list mutators deferred to Slice 3).

### Tests

| Kind | What |
|------|------|
| Unit / existing suite | Full solution build; entire existing test suite green (rename-only). |
| Conformance | `conformance.json` twin run — no behavioral cases required if ops unchanged; fix any serialization of type tags. |
| Grep gate | Zero matches for `ExecArray`, `ArrayKind`, `type: "array"` as collection tag (allow Code AST `CodeArray` — that is the **source** literal AST node, not runtime). |

### Done criteria

1. Solution builds in VS2026 (`DeEnv` + `DeEnv.Tests`).
2. No `ExecArray` / `ArrayKind` symbols remain.
3. Wire/DT collection tags are `set`/`dict`/`list`.
4. **Mandatory DT round-trip fixture:** one durable-shaped set + one dict + one (ephemeral) list tag through `ClientState` ↔ client merge — proves tag lockstep (not only a risk note).
5. Existing Gherkin suite passes without new durable-list scenarios.
6. List values still only appear with **negative** ids (ephemeral).
7. `AppState` map is `collections` (or residual `arrays` documented in code comment as collections-by-id).

### Risks

- **Blast radius:** `ExecArray` is ubiquitous; do mechanical rename in one commit, avoid “half Kind enum.”
- **TS/C# lockstep:** `ClientState` ↔ `dt.ts` merge is the sharp edge; a tag mismatch silently drops collections — gate with the DT fixture above.
- **Do not rename `CodeArray` AST** — that is the language literal node (`[…]`), still produces `ExecList`.
- **`workbench.ts` deepCopy / extent** maps use `ExecObject | ExecArray` — update unions carefully.

---

## Slice 2 — Durable list core

### Intent

Persist `list of T` end-to-end: parse, print, validate, mint empty list on object create, load through DbBridge as positive-id `ExecList`, GC, seed, thin publish reshape. **No** full op surface yet (read + empty default + load/save shape only; mutations can wait for Slice 3 except whatever create/mint requires).

### Schema / authoring

| File | Change |
|------|--------|
| `InstanceDescription.cs` | `Cardinality.List` |
| `AppParse.cs` | `list of Name` alternative in `PropType` (mirror `set of`) |
| `AppPrint.cs` | `list of {Type}` |
| `InstanceDescriptionLoader.cs` | validation: list element type rules (object **or** scalar; password/image OK); reject presentation flags that already only apply to single text (existing `multiline` rule — not a new type) |
| `SchemaBridge.cs` / `DesignerSeed.cs` / `DesignMerge.cs` / `DesignDiff.cs` | cardinality word `"list"`; MetaProp options |
| `sys.schema` descriptors (`CodeExecutor` prop descriptors) | `baseType: "list"` (or existing card encoding) so generic UI / `sys.new` mint empty `ExecList` |

### Storage

| File | Change |
|------|--------|
| `StoreModel.cs` | `StoredList(int Id, List<StoredValue> Items)`; converter tag `"list"` |
| `NodeValue.cs` | `ListValue(int Id, IReadOnlyList<NodeValue> Items)` |
| `JsonFileInstanceStore.cs` | BuildFields mint empty list; ReadNode projection; FindListNode; clone-on-write like set/dict; GC `Mark` walks list items; create-object nested collection ids in `CommitCreateResult` |
| `StoredDataValidator.cs` | list shape + element type checks; allow duplicate refs |
| `IInstanceStore.cs` | only if a standalone API is needed for tests; prefer CommitBatch mutations in Slice 3 |

### Durable log + replay (normative — Slice 2 shape, Slice 3 emission)

Today every durable mutation has a closed `LogWrite` union (`AppLog.cs`: `SetLink`/`SetUnlink`/`DictSet`/`FieldWrite`/…) and `AppLogReplay` arms. List mutations need the same for **history / fsck / restart correctness** — this is **not** list OCC.

**Chosen strategy (preferred, mirrors set/dict):** dedicated list `LogWrite`s — e.g. `ListReplace` / `ListInsert` / `ListRemoveAt` / `ListMove` carrying `listId` + payload — **total** in:

- `DeEnv/Storage/AppLog.cs` (union + converter)
- `DeEnv/Storage/AppLogReplay.cs` (apply arms)
- `JsonFileInstanceStore` pending/log emission on CommitBatch list applies
- fsck / validator paths that walk log shapes

**Alt:** always log whole-list as `FieldWrite(owner, prop, oldStoredList, newStoredList)` if dedicated writes prove too heavy — document choice in code if taken.

**Do not** add `_listVersions` / boot rebuild of per-list OCC counters in this work (deferred with multi-user concurrency).

Slice 2: `StoredList` tag + converter accepts list nodes in snapshots. Slice 3: emit and replay list writes. **Test:** mutate list → restart / replay → same items and order (no version-counter assertion).

**Seed / initialData**

- Friendly seed for list-of-objects: JSON array of member ids (order significant; duplicates allowed).
- List-of-scalars: JSON array of scalars.
- Print/parse round-trip in `AppParse` / `AppPrint` seed sections.

### DbBridge

| Card | Runtime |
|------|---------|
| `Cardinality.List` | `ExecList` with **positive** stored id; items in order; object slots load shared `ExecObject` via `loaded` map (same id twice → same instance twice in `Items`); scalar slots `ScalarToExec`; password blanking per existing leaf rules **per slot** |
| ACL floor | object slot denied → **omit denied object slots** (mirror set membership omit), preserving relative order of remaining slots. **Indices renumber** for the filtered view — clients must not cache index across ACL-filtered renders. |

Password slots: same hash-on-write + blank-on-read chokepoints as single password, **per slot**. Image slots: store as text-shaped pool blob name (`BaseType.Image`) — **no** password hash chokepoint; leaf validate/store as image/text per existing single-image rules, **per slot**.

Update stale DbBridge header comment (“Dictionaries are not yet loaded”).

### TypeResolver / URL

| File | Change |
|------|--------|
| `TypeResolver.cs` | list segment: if element is object, next segment is **member id** only if that id appears in the list (≥1); descend to that object type. **Never** parse segment as index. |
| Navigation / breadcrumbs | object-list member pages work; scalar lists stay on owner |

### Publish / design diff

| File | Change |
|------|--------|
| `DesignDiff.cs` | cardinality changes involving `List` |
| `JsonFileInstanceStore` reshape arms | single→list wrap; set→list order-by-id; list→set dedupe; unsupported cases loud like today |
| `PublishReportCode` / report UI | surface list reshapes |

### Tests

| Kind | File / tag |
|------|------------|
| Gherkin | New `DeEnv.Tests/Features/Lists.feature` tagged `@milestone-lists` (or project’s current usable-MVP tag convention — **use a dedicated tag**, do not overload unrelated milestones) |
| Scenarios | parse/print `list of T`; create object with empty list; seed ordered members; reload order preserved; duplicate object id in two slots survives **and foreach/slot paths yield two independent rows** (ordinal keys); GC: only-ref-via-list keeps object, unlink last list/set/ref collects; URL `/items/7` when 7 ∈ list; scalar list has no child URL |
| Unit | `StoredDataValidator` list cases; reshape single→list / set→list / list→set |
| Conformance | optional tiny cases for list literals still ephemeral |

### Done criteria

1. `list of T` round-trips in app document.
2. Empty list minted on create with stable positive id (like set/dict).
3. Reload preserves order and duplicate refs.
4. GC treats list slots as edges.
5. Thin publish reshapes implemented + tested.
6. Docs can start (INSTANCE_MODEL list section) without waiting for UI.

### Risks

- **Duplicate object identity in one list:** load map must not collapse two slots into one item; foreach keys must be ordinal (see Target shapes).
- **Password/image in list slots:** password hash/blank per slot; image pool-name leaf per slot.
- **Log/fsck:** `StoredList` + (by Slice 3) list `LogWrite`s must be total in converter + validator + replay.
- **INSTANCE_MODEL contradiction:** update docs in this slice or immediately after so agents do not “fix” list support.
- Avoid implementing full mutators here — keep Slice 2 load/mint focused.

---

## Slice 3 — List ops + persistable assign (unified commit)

### Intent

User-visible list mutation + assign-where-result persistence. **Unified commit only** — no live standalone list HTTP ops outside the commit relation vocabulary (mirror set/dict post-unified-commit). **No list-level OCC** (deferred).

### Model ops → mutations

Add to `IInstanceStore.cs` / `CommitBatch`:

```csharp
// Whole-list replace (assign where/orderBy result, removeAll, bulk rebuild). Keeps list id.
public sealed record ListReplaceMutation(
    int ListId,
    IReadOnlyList<StoredValue> Items) : CommitMutation;

// Prefer true fine mutations in the same slice for smaller journals:
public sealed record ListInsertMutation(int ListId, int Index, StoredValue Item) : CommitMutation;
public sealed record ListRemoveAtMutation(int ListId, int Index) : CommitMutation;
public sealed record ListMoveMutation(int ListId, int From, int To) : CommitMutation;
```

**Slice 3 MVP:** **`ListReplace` is required** (covers assign + removeAll + removeWhere rebuild). Prefer true fine `ListInsert`/`ListRemoveAt`/`ListMove` **in the same slice**; if time-pressed, client may rebuild→replace for insert/move/removeAt, but assign/`removeWhere` must not wait on a later open question.

Apply arms:

1. Resolve list by id (scan object fields like `FindSetNode`).
2. Apply mutation (no expected-version check).
3. Emit dedicated list `LogWrite`(s) for replay (see Durable log subsection).
4. Existing commit ack: creates remap + store `newVersion` only — **no** `listVersions` map.

### Wire (`WsHandler` + `ws.ts`)

Relations (names mirror model):

```json
{ "kind": "listReplace", "listId": 12, "items": [ … ] }
{ "kind": "listInsert",  "listId": 12, "index": 1, "value": … }
{ "kind": "listRemoveAt","listId": 12, "index": 1 }
{ "kind": "listMove",    "listId": 12, "from": 0, "to": 2 }
```

Item encoding:

- object ref: `{ "type":"object", "typeName":"Task", "id": 7 }` or temp id for creates
- scalar: existing tagged leaf shape

**Creates into lists:** if `add`/`insert` of a new object, batch `CommitCreate` + list mutation in one commit (mirror setAdd + create). May need `listInsert` with temp child id resolved in batch.

**Owner addressing:** optional `listLinkByProp` is **not** required if list id is always known on the client `ExecList` (positive id from load) — same as set’s raw setId path. For lists nested under a just-created parent in the same commit, either:

- mint parent collections in create result (already `CommitCollection`) and remap client list id, or
- add `(ownerRef, prop)` addressed variants like set’s `SetLinkByPropMutation`.

Implement the **create-result remap** path first (already exists for sets); add by-prop only if a test forces mid-batch parent mint.

### Runtime methods

**C# `CodeExecutor` + TS `codeExec.ts` (lockstep + conformance)**

Extend collection method set for `ExecList`:

`add`, `insert`, `move`, `removeAt`, `removeFirst`, `removeLast`, `removeAll`, `removeWhere`, keep `where`/`orderBy`/`any`/`single`.

Rules:

- Ephemeral list (id ≤ 0): mutate in memory only.
- Durable list (id > 0): optimistic local mutate + buffer commit relation (no list version on DT).
- `remove(value)` on list: **prefer omit** — set keeps `remove`; list uses the remove* table above.

**Assign list prop**

In `AssignAndReturn` / TS assign path, when target is object prop typed list and value is `ExecList`:

- Staging ctx: stage a list-replace intent (or replace whole prop in staged fields with list snapshot).
- On `ctx.commit`: emit `listReplace` with items.
- **Same positive list id** preserved server-side (do not mint a new list id on replace).

DT list leaf (no version field):

```ts
// ServerDtList
{ type: "list", id, items, elementTypeName?, constant?: boolean }
```

### ACL

Wire handler:

- insert/add object → create/link rules like setAdd
- removeAt / removeWhere unlinking objects → like setRemove for those objects **and** edit on owner for structure
- move / replace order-only → **edit** on owner type
- Fail closed if floor denies

### Tests

| Kind | Coverage |
|------|----------|
| Conformance | list literal mutators in-memory; where→list; orderBy→list |
| Unit | replace keeps id; insert/move/removeAt; duplicate refs; **AppLog replay** of list mutate → restart → same items/order |
| Gherkin `Lists.feature` | append/commit/reload; insert; move; removeAt; removeWhere; assign `obj.items = obj.items.where(...)` persists; create+append in one commit; **foreach two rows for duplicate id** |
| Password list | write hashes; read blanks per slot |

### Done criteria

1. All product ops work on durable lists through **unified commit only**.
2. No `expectedListVersion` / `listVersions` / `_listVersions` in this slice.
3. Assign of `where`/`orderBy` result persists via `ListReplace` same id.
4. List `LogWrite`s replay after restart (items + order).
5. Twin interpreters lockstep for pure list ops (conformance).
6. Set OCC commute tests unchanged.
7. Code sugars `removeFirst`/`Last`/`All`/`Where` map to wire per Code→wire table (no extra wire kinds).

### Risks

- **Journal undo/redo:** list ops need undo snapshots (full list or inverse op); start with full-list undo capture if fine inverse is hard.
- **Staging + list:** drafts in list slots must join creates like setAdd; define `CreateJoin` variant `listInsert { list, index }` or append-only join.
- **Scalar list wire value encoding** must reuse existing scalar commit codecs (password hash chokepoint).
- Do not add live non-commit list endpoints.
- Do not sneak list-only OCC “for later.”

---

## Slice 4 — Generic UI + designer order collapse

### Intent

Users edit lists without Code; designer stops using set+`order` for position-bearing trees.

### Generic UI (`GenericUi.cs` library Code)

- New `ListTable` (or extend table component) for `baseType == "list"`:
  - rows in stored order
  - object list: row link via `/listField/id` (membership URL)
  - scalar list: inline editors per slot
  - controls: insert, delete row, move up/down (call list ops + commit / autosave pattern used by sets)
- ObjectForm field switch: list branch beside set/dictionary.
- `sys.schema` / descriptors already expose list (Slice 2).

### Designer collapse (`instances/1/app.deenv` + SchemaBridge)

Position-bearing props become **lists** (not sets of ordered members), including at least:

- render tree: `children`, `elseChildren`, `attrs`, `body`
- schema editor: `types`, `props` (if order matters in UI)
- fn editor: `vars`, `uses`, `args`, `ambients`, `fns`

**Delete scaffolding**

- `order` prop on those member types
- `orderForAppend`
- `moveRow` order-swap
- `OrderedMembers` / `orderedMembers` / `OrderedMembersOptional` as **order hacks** — replace with plain list iteration order

**Runtime readers**

- `CodeExecutor.OrderedMembers*` → read list prop directly (or keep name as thin alias over list items during transition, then delete).
- `SchemaBridge.OrderedMembers` → list order.
- TS twins of orderedMembers in `codeExec.ts`.

**Semantic migration (preferred)**

Author `fn Type(old)` migrations (M13) for designer Meta* types:

1. `orderBy` members by old `order` then id  
2. assign into new list prop  
3. drop `order` field  

Wire into publish when designer schema flips set→list. Fallback thin reshape (set→list by id) is wrong for designer trees — **do not rely on it** for designer data.

### Tests

| Kind | Coverage |
|------|----------|
| Gherkin SelfHostedUi / Lists | list table reorder; insert; object row navigation |
| Designer features | add child appends to list; move up/down; unwrap/wrap still preserve order; no `order` field in projected schema |
| Publish | designer migration carries children order across deploy |

### Done criteria

1. Generic UI can CRUD/reorder list fields without custom Code.
2. Designer trees are lists; `orderForAppend` / `moveRow` / member `order` gone from `app.deenv`.
3. SchemaBridge projection order = list order.
4. Designer publish migration preserves structure order.

### Risks

- **Designer is production data** — migration must be tested on a real `instances/1` clone, not only fixtures.
- Large `app.deenv` rewrite; keep behavior parity with Playwright designer scenarios (`Designer*.feature`).
- Temporary dual-read (list or ordered set) is scaffolding to delete within the same slice if possible; do not leave permanent dual path.

---

## Slice 5 — Documentation finish

### Required doc updates

| Doc | Change |
|-----|--------|
| `INSTANCE_MODEL.md` | Add **list of T**; remove “Lists are dropped — not deferred”; document order-as-list, URL membership rule, set-object-only asymmetry |
| `DECISIONS.md` | Replace “One Code array” with three sealed exec collections; record durable list; note list OCC deferred to multi-user; designer order collapse |
| `INSTANCE_DESCRIPTION_FORMAT.md` | `cardinality: list` / app syntax `list of` |
| `EXPECTATIONS.md` / `ROADMAP.md` | only if milestone bookkeeping needs a line (minimal) |
| Code comments | DbBridge header, StoreModel header, ExecValues header |

Docs **may** land with Slice 2 for model truth; finish wording after Slice 4 so designer migration is described accurately.

### Done criteria

- No remaining doc claims that lists are rejected or that runtime is a single `ExecArray`.
- Terminology audit: zero “multiset”.

---

## Files to modify (summary)

### Runtime / wire

- `DeEnv/Code/ExecValues.cs` — sealed collections; delete ArrayKind/ExecArray
- `DeEnv/Code/CodeExecutor.cs` — dispatch, list ops, OrderedMembers→list
- `DeEnv/Code/DbBridge.cs` — load List; extent still ExecList ephemeral
- `DeEnv/Code/ClientState.cs` — DT tags set/dict/list (no listVersion)
- `DeEnv/Code/MigrationRunner.cs` — collection cases
- `DeEnv/Code/conformance.json` — list op cases (Slice 3)
- `DeEnv/Instance/codeExec.ts`, `dt.ts`, `ws.ts`, `workbench.ts`
- `DeEnv/Http/WsHandler.cs` — parse list relations; ACL
- `DeEnv/Http/SsrRenderer.cs` — ExecList minting for reports/registry
- `DeEnv/Kernel/PublishReportCode.cs`, `MergeReportCode.cs`

### Schema / storage

- `DeEnv/Instance/InstanceDescription.cs`, `AppParse.cs`, `AppPrint.cs`, `InstanceDescriptionLoader.cs`, `TypeResolver.cs`
- `DeEnv/Storage/StoreModel.cs`, `NodeValue.cs`, `IInstanceStore.cs` (list mutations; no ListVersions), `JsonFileInstanceStore.cs`, `StoredDataValidator.cs`
- `DeEnv/Storage/AppLog.cs`, `AppLogReplay.cs` — list `LogWrite`s + replay (history/fsck, not OCC)
- `DeEnv/Designer/SchemaBridge.cs`, `DesignerSeed.cs`, `DesignDiff.cs`, `DesignMerge.cs`
- `DeEnv/Instance/GenericUi.cs`
- `DeEnv/instances/1/app.deenv` — list props + drop order scaffolding
- `DeEnv/Kernel/KernelHostActions.cs` — cardinality word map if any
### Tests / docs

- `DeEnv.Tests/Features/Lists.feature` (+ steps)
- Existing features touched by rename (compile fixes)
- `INSTANCE_MODEL.md`, `DECISIONS.md`, `INSTANCE_DESCRIPTION_FORMAT.md`
- This plan: `docs/plans/2026-07-17-durable-lists-and-exec-collections.md`

### New files

- `DeEnv.Tests/Features/Lists.feature` — durable list behavior
- Optional: `DeEnv.Tests/Steps/ListSteps.cs` if not folded into existing store/UI steps
- Optional designer migration snippet under `docs/` only if not fully expressed in app migrations

---

## Dependencies

```text
Slice 1 ──► Slice 2 ──► Slice 3 ──► Slice 4
                │                      │
                └──── docs start        └── docs finish (Slice 5)
```

- Slice 3 depends on Slice 2 list ids + StoredList load/mint.
- Slice 4 depends on Slice 3 mutators (UI reorder) and Slice 2 schema.
- Slice 1 has **no** storage dependency and must land first.

---

## What NOT to do

1. Do **not** reintroduce `ArrayKind` or a single `ExecArray` “with kind.”
2. Do **not** add index URLs or positional addressing in `TypeResolver`.
3. Do **not** implement CRDT/OT or change set commute OCC.
3b. Do **not** add list-only `listVersion` / `expectedListVersion` / `_listVersions` — wait for general multi-user concurrency.
4. Do **not** add set-of-scalars.
5. Do **not** persist list ops outside unified commit.
6. Do **not** use “multiset” terminology.
7. Do **not** keep designer `order` + list dual forever — collapse in Slice 4.
8. Do **not** treat `CodeArray` AST rename as in-scope (source literal stays).
9. Do **not** expand into M12 visual designer or dogfood deploy work.
10. Do **not** guess dogfood timing.

---

## Validation commands (per slice)

```text
# Build
dotnet build DeEnv.sln

# Full suite after rename (Slice 1)
dotnet test DeEnv.Tests

# Lists feature after Slice 2+ (Reqnroll/TUnit — not NUnit TestCategory).
# Tag scenarios @milestone-lists; run by feature class treenode filter (PowerShell quoting):
dotnet test DeEnv.Tests -- --treenode-filter "/*/*/ListsFeature/*"
# See docs/TESTING.md and existing unified-commit plan for the repo's filter pattern.

# Conformance (twin lockstep)
# existing Code conformance test host — run as today after any CodeExecutor/codeExec.ts change
```

Do **not** use `--filter "TestCategory=milestone-lists"` — that does not match this repo's Reqnroll/TUnit setup.

---

## Risk register (cross-cutting)

| Risk | Mitigation |
|------|------------|
| Rename blast radius breaks suite | Slice 1 pure rename; no behavior; full suite gate |
| DT tag drift C#/TS | Shared fixture: one collection of each kind in ClientState round-trip test |
| Duplicate list slots + shared ExecObject | Ordinal ExecItem keys + foreach index keys; load/foreach tests with id twice |
| Premature list-only OCC | Deferred — no listVersion in this plan |
| AppLog/fsck miss list writes | Dedicated List* LogWrites + replay test across restart (items/order) |
| Designer migration data loss | Dedicated publish test on children order; prefer semantic migration |
| Password in list | Chokepoint tests per slot |
| GC misses list edges | GC unit: sole ref from list slot keeps; clear list collects |
| Agents re-litigate INSTANCE_MODEL “lists dropped” | Docs update with Slice 2 |

---

## Suggested PR breakdown

1. **PR1 — Slice 1** rename only  
2. **PR2 — Slice 2** durable core + model docs  
3. **PR3 — Slice 3** ops + wire + conformance + Lists.feature mutators  
4. **PR4 — Slice 4** GenericUi + designer list migration  
5. **PR5 — Slice 5** doc polish + terminology audit  

Each PR: build green + relevant tests; no scope from later slices.

---

## Review notes (2026-07-17)

Reviewer verdict was **request-changes**. Applied before implementation:

1. Durable **AppLog / replay** (dedicated list LogWrites preferred) — for history/fsck, **not** OCC.
2. **Foreach / ExecItem ordinal keys** for duplicate object slots.
3. ~~Full listVersion OCC~~ — **superseded 2026-07-17:** human deferred list-level OCC to general multi-user/sessions; no `expectedListVersion` / `listVersions` / `_listVersions` in this plan.

Plus nits: Code→wire map for remove* sugars; test filter syntax; password vs image per-slot; Slice 1 DT fixture; AppState `collections` rename preference.
