# Memo cache — Stage 4

**Status: IMPLEMENTED (4a), extended through Stages 4b–6.** The proposal below was approved
and built across three slices (server memo+deps, wire format, client cache). Refinements made
during the build:

- `MemberDep` keys by the **collection's runtime array id** (recorded inside `where`/`orderBy`
  AND by a `foreach` running inside a computation), not a separate notion.
- **`VarDep` (Stage 6):** a writable top-scope UI-state var read inside a computation is a
  third dependency kind; assigning the var invalidates dependent entries (client-local — each
  client owns its vars). Read-only items (`db`, functions) are never deps.
- **Leaf promotion (Stage 6):** a computation whose result is a TAG TREE (a page fn) is
  display — its result has no wire form (the client re-renders it), so the reads it made are
  promoted to leaves and ship. A value-returning computation's reads stay private deps. This is
  what keeps `where` predicates private while page functions work.
- **Purity rules (Stage 6):** an identity-creating computation (its result is a transient
  OBJECT minted inside it — a `getNewX()` factory) is never cached; event handlers run with the
  cache bypassed (they may be side-effecting). Derived arrays stay cacheable.
- **Drafts are client-owned (Stage 6):** transient objects in client state ship complete (all
  props), and a refetch's scope merge never overwrites a var holding a client draft.
- Each var initializer is itself a memoized computation (`var:<name>`), so its inputs become
  deps, not leaves. Tag/function-valued entries are skipped on the wire.
- The `sensitive` flag is **deleted**; `salary` is private purely because it is an input to a
  computation (a dep), never a rendered leaf. `server-only` no longer carries privacy meaning;
  rendering a field is the choice to expose it.
- The hidden-dependency recompute goes through the WS **refetch** (Stage 4b): the server
  re-renders over the client's warm session and returns authoritative state. §7's
  recompute-vs-refetch call went the other way in practice: the client **try-recomputes and
  treats "Value not available" as the refetch trigger** (with stale-result reuse until the
  reply lands) — simpler than an up-front deps-present check and proven by the todo app.

The original proposal text follows (wire shapes updated as built).

---

**Status: PROPOSAL.** This is the concrete structure shapes for the memoization/dependency
cache that replaces the `sensitive` flag and the access-scope counter (see plan Stage 4).
Nothing here is built yet — it's for sign-off on shapes before any code.

Core idea (recap): every **computation** (a function call, a `where`/`orderBy`) is memoized,
keyed by `(function, arguments)`, holding its **result** + the **dependencies** it read **as
references, never values**. The server ships displayed leaves + cache entries; inputs to a
computation never cross the wire, so data not rendered is private by construction.

---

## 1. Dependencies — what a computation depends on

A computation's result can change if any of these change, so all are tracked:

- **Prop reads** — every `(objectId, propName)` the computation read. → `PropDep`
- **Collection membership** — every set/list whose membership it observed (iterated, filtered,
  ordered, counted). Add/remove of any member must invalidate. → `MemberDep`
- **(finer, optional)** which specific entries it read — lets a per-entry change invalidate
  without touching the whole collection. Start coarse (whole-collection membership) unless
  payload/over-invalidation forces finer; flagged as a knob (§7).

> This is the note from review: the cache must know **all objects + props accessed AND the
> list-entry/membership access**, so both kinds are first-class deps.

```
PropDep    = (objectId: int, prop: string)
MemberDep  = (collectionId: int)            // the set/array whose membership was observed
VarDep     = (name: string)                 // a writable top-scope UI-state var (Stage 6)
Deps       = { props: PropDep[], members: MemberDep[], vars: VarDep[] }
```

## 2. Cache key — `(function, arguments)`

```
Arg        = ObjectRef(id) | ArrayRef(id) | Scalar(value)   // objects/arrays by identity
CacheKey   = (callee, args: Arg[])
```

`callee` identity:
- **user function** → `CodeFunction.Id` (already on the AST).
- **derived collection** (`where`/`orderBy`) → `(method, sourceCollectionId, lambdaFnId)` — the
  source array's id + the method + the lambda's `CodeFunction.Id`.

(Keys must serialize to a stable string for the client's cache map, e.g. `fn:7|args:o3,a5`.)

## 3. Cache entry

```
CacheEntry = {
  key:    CacheKey,
  result: DtValue,        // filtered/ordered list (array of refs), object ref, or scalar
  deps:   Deps,
}
```

`result` references objects/arrays by id; those objects' **displayed** props ship as leaves
(§5), not inside the entry.

---

## 4. C# (server) — proposed structure changes

**Removed:** `PropDefinition.Sensitive`; `ExecContext.Suppress`; the access-scoped / sensitive
logic in `ClientState`; the `CheckNoServerOnlyRefs`-for-privacy framing in `CodeValidator`.
(`server-only` stays, but only to mean "never runs client-side" — hashing/secrets/side-effects.)

**`ExecContext`** — replace the flat access sets with per-computation capture + a memo table:

```
ExecContext {
  LastId
  // memoization: results captured while rendering, for transfer
  Memo: Dictionary<CacheKey, CacheEntry>
  // dependency capture for the computation currently on top of the stack
  DepStack: Stack<Deps>            // push on entering a computation, pop on exit
  ...
}
```

- Entering a memoized computation: push a fresh `Deps`; on exit, pop it, build the
  `CacheEntry { key, result, deps }`, add to `Memo`, and merge the child deps into the parent on
  the stack (a caller depends on what its callees depended on).
- Every prop read records a `PropDep` onto the top `Deps`; every membership observation records a
  `MemberDep`.

**`CodeExecutor`** — at each computation boundary (user-fn call, `where`, `orderBy`): compute the
`CacheKey`, run the body with a pushed `Deps`, store the entry. No flag/suppress checks.

## 5. Wire format (`initData`) — proposed shape

```
{
  "leaves": {                       // displayed object/array data, by id
    "objects": { "<id>": { "props": { "<name>": DtValue } } },
    "arrays":  { "<id>": { "kind": "set"|"dict"|"list", "elementTypeName": string?,
                           "items": [ { "key": int, "value": DtValue } ] } }
  },
  "scope": { "<key>": { "isReadOnly": bool, "value": DtValue } },
  "cache": [
    { "key":    { "callee": "<fnId|method:src:lambda>", "args": [ Arg ] },
      "result": DtValue,
      "deps":   { "props":   [ { "obj": int, "prop": "<name>" } ],
                  "members": [ int ],
                  "vars":    [ "<name>" ] } }
  ]
}
```

- `leaves` = only what the rendered output shows (object props that became DOM text/attrs/values,
  array items that were rendered). A prop read **only** inside a computation appears in some
  entry's `deps`, never in `leaves` → private.
- `cache` = the memoized computation results + dep-refs.

## 6. Client (TS) — proposed structures

```
clientCache:    Map<string, { result: ExecValue, deps: Deps, stale: boolean }>   // by serialized key
depIndex:       Map<string, Set<string>>   // dep-ref ("o3.prop" / "members:a5") -> cache keys
```

- **Interpreter hook:** at a computation boundary, build the key → look up `clientCache`.
  - hit & fresh → use `result` (no recompute).
  - miss / stale & **all deps present in client state** → recompute locally (instant), refresh entry.
  - miss / stale & **a dep is absent** (hidden) → mark `needsServer` → refetch over WS (Stage 4b).
- **Invalidation:** a client mutation of `(obj, prop)` or a collection add/remove → via `depIndex`,
  mark matching entries `stale`. Next render recomputes (deps present) or refetches (hidden).
- First paint: all entries arrive fresh → zero round-trips.

## 7. Open decisions / knobs (need your call)

1. **Computation granularity — DECIDED (2026-06-11): function-level, for now.** Memoize user
   function calls + the `where`/`orderBy` derived collections (their lambdas are functions). Inline
   `if` conditions and bare expressions are NOT memoized → they re-evaluate on the client, so a
   condition/expression reading hidden data must be **wrapped in a function** to stay private
   (e.g. `if (isRich(p))`, not `if (p.salary > 100)`). Memoizing keyed expression nodes is a
   possible later extension; not now.
2. **Membership granularity** — whole-collection `MemberDep` (coarse, over-invalidates) vs.
   per-entry. **Proposed: whole-collection first.**
3. **Key encoding** — stable string for the client map (`fn:7|args:o3,s"a"`). Bikeshed later.
4. **Recompute-vs-refetch detection** — check deps-present up front, or try-recompute and treat
   "value not available" as the refetch trigger? **Proposed: deps-present check (explicit,
   robust) over exception-driven.**
5. **Dependency-structure exposure** — `deps` reveals *which* field mattered (not its value).
   Acceptable now; the per-user permission policy (deferred) can redact dep-refs later.

---

## 8. What this removes vs. the committed 4a (d1f9dd7)

- `PropDefinition.Sensitive` and its JSON `sensitive` → gone.
- `ExecContext.Suppress` + the recording gates → replaced by `DepStack` capture.
- `ClientState` access-scoped + sensitive-throw → replaced by `leaves + cache` serialization.
- `CodeValidator` server-only-ref check stays only as "server-only never runs client-side"
  (not a privacy mechanism).
- Fixtures: `SensitiveUiDb` keeps working (salary private because it's only a `where` input — no
  flag); `SensitiveLeakUiDb` (direct render of salary) now simply *ships salary as a leaf*,
  because rendering it **is** the choice to expose it — so that scenario changes meaning.
