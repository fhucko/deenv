# Instance model

How an instance is described, addressed, and rendered. This is the
functional core that was missing when the first slice was scrapped.

## Type system (recursive)

An instance description is a **list of type definitions**. One type named
`Db` is the **root**. `Db` may be a value of *any* type — including a bare
base type.

The canonical JSON shape is in **INSTANCE_DESCRIPTION_FORMAT.md**. This
document describes the conceptual model; that one describes the format on
disk.

A type is one of:
- **base type** — `bool`, `int`, `decimal`, `text`, `date`, `datetime`, `password`.
- **enum** — a user-defined closed set of text values.
- **object** — a user-defined named type with a **fixed, ordered set of
  fields**, each field a `(name, type, cardinality)` triple. Structure is
  declared up front, not dynamic.
- **reference to <type>** — a single field whose value is an object that
  lives in its type's extent (not owned inline). Declared `propName TypeName`.
- **set of <type>** — a collection keyed by **member identity**. Declared
  `propName set of TypeName`. Element type must be an **object** type. Members
  live in their type's extent; the set holds unique references. Addressed as
  `/setField/memberId`.
- **dictionary of <type>** — a collection keyed by a **user-chosen stable
  key** (text or int). Declared `propName dict of TypeName by keyType` (app
  syntax) / `dictionary of` in older prose. Addressed as `/dictField/key`.
- **list of <type>** — an **ordered sequence** of slots. Declared
  `propName list of TypeName`. Element type may be an **object or a scalar**
  (including password/image with existing leaf write/read rules per slot).
  Sequence is authoritative (no position field on members). The same object
  id may appear at more than one index. Object members are addressed as
  `/listField/memberId` (**entity id + membership ≥ 1**, never an index).
  Non-object members have **no** member URL.

All non-constant values (objects, sets, dictionaries, lists) carry an **intrinsic
`id`** (monotonic int, stored separately from props) — identity is intrinsic
to being a non-constant. Objects live in per-type **extents** (flat id-keyed
pools); sets, lists, and references point into extents, not own their targets.

Everything below (addressing, rendering, breadcrumbs) is a recursion over
this definition. The bool-root case is simply the recursion bottoming out
immediately.

## URL = navigation into the data

Every node is addressable by URL; the path walks the data tree from `Db`:
- `/` — the root value.
- `/field` — a field of the current object.
- `/dictField/42` — the entry with **key** 42 of a dictionary.
- `/setField/7` — the set member with **object id** 7.
- `/listField/7` — an object that appears in the list (membership ≥ 1); **not**
  `/listField/0` (index is never a URL segment).
- nests arbitrarily: `/customers/42/orders/7/total`.

A URL addresses a node; reading returns that node's data; writes target the
URL of the thing changed (read/write addressing is symmetric).

## Stable addressing (no index URLs)

**URLs never use list indices.** Sets address by member id; dictionaries by
user key; lists of objects by member id + membership check. A URL always means
the same object until that object is deleted (or unlinked from every path),
not "whatever is currently at position N."

**Ordering.**
- **List** — authored order **is** the collection: sequence is truth. Reorder
  mutates the list (insert/move/removeAt), not a position field on members.
- **Set / dictionary** — no inherent order. Derived display order uses
  `orderBy` / ordinary fields on members when order is an **attribute of the
  entity** (priority, due date). Prefer a **list** when order is arrangement
  **in this collection** (menu, children tree, steps).

**Runtime.** `where` / `orderBy` always mint a **new list** (ephemeral until
assigned to a durable list prop and committed). Durable and ephemeral lists
share the same runtime type (`ExecList`); positive id = in db, negative = local.

**Concurrency.** Per-node optimistic concurrency for non-scalar mutables
(object, set, dict, list, …) is a later multi-user track — not list-only.

## Rendering: one form per type

Traversal granularity is **one form per type instance**:
- A **bool root** (or any base-typed leaf node) renders as a one-field form —
  a single **checkbox**/input plus a Save button. The bool root is the
  simplest valid instance.
- An **object** renders as a form of its fields.
- A field that is a **dictionary**, **set**, or **list** renders as an **HTML
  table**; object rows link by stable id/key; lists also expose reorder
  controls. Scalar list slots edit inline on the table (no member page).
- Leaf fields render as form inputs.

The whole app is: forms for objects, tables for collections, click a row to
descend (where the element is an object).

## Navigation granularity: collections are the boundary

A single page shows one node and **everything reachable from it except
across a collection.** Concretely:
- **Single-valued nested objects render inline**, however deep. A customer
  with an address (and whatever the address nests) is all on one page,
  edited together. No depth limit, no inline-vs-link judgment.
- **Sets, dictionaries, and lists are navigation boundaries.** Each renders
  as a table; you navigate (new page) by clicking an object row — set/list by
  member id, dict by key — never by list index.
- **A set is identity-keyed unique membership**; a **list is ordered sequence**
  (duplicates of the same object id allowed; URL still uses entity id +
  membership, not which occurrence).

The self-hosted generic UI (Milestone 9) follows this exactly: it is
**path-walk** — an object page renders its scalars and single references inline,
and each set as an inline table whose member rows link to the nested member URL
(`/notes/3`, `/customers/2/orders/3`). The `/~/<id>` id-route remains only a C#
fallback, not something the generic UI generates.

This is deliberately the simplest consistent rule: the page boundary is
predictable — follow the data from this node, stop at every collection.

Accepted consequence: a type with deep single-valued nesting produces a
large page. Fine for the eshop/CRM "edit the whole record in one place"
case; revisit only if a real type produces an unwieldy page (same spirit as
no-paging). (Future: UI customization / auto-tabs will present large pages
better — build the renderer so a presentation layer can sit on top later.)

URL scheme is unaffected: every node stays addressable (e.g.
`/customers/42/address` resolves), it just renders inline on the parent page
under normal viewing. The URL works; only dictionaries force a new page.

## Rendering architecture

**First paint — SSR.** `SsrRenderer` loads the instance's `fn render()` (or
synthesizes the generic one via `GenericUi.Effective` for apps with no custom
render) and runs it through the Code interpreter. The result is serialized to
HTML. The page shell also embeds:
- `window.initData` — the memo-cache/scope snapshot accessed during the render
  (structural privacy: only what was touched ships, never raw db);
- `window.initUi` — the AST of the `fn render()` + library functions;
- `window.initInfraPort` — the port where `/ws` and the compiled `/js` bundle
  live (separate from the app's clean data URL space).

**`sys.resolve(path)`.** The generic `fn render()` calls `sys.resolve(path)`
to determine what the current URL addresses — returning `{kind, target, parent,
prop, typeName}` where `kind` is one of `object | set | ref | dict | leaf |
notFound`. The render switches on kind and calls the matching library component
(`ObjectForm`, `SetTable`, `RefEditor`, …). This single switch replaced the old
C# per-URL dispatch; a generic app is now literally the custom-render path.

**Client hydration (`init.ts`).** After the page loads the client:
1. Merges `initData` into its local scope (the warm graph).
2. Re-executes `initUi` functions to close over the live scope.
3. Runs the same `fn render()` through the TS interpreter — same Code, same
   result, proven byte-identical by the conformance suite.
4. Marks the page hydrated (`data-hydrated` on `<html>`).

After hydration the client drives SPA navigation: a route change re-runs
`fn render()` client-side over the warm graph.

**Mutations.** Edits go over WebSocket to `WsHandler`. A form's fields stage
into an editing context (`ctx`) and commit atomically on Save (`ctx.commit()`).
If the client's warm graph is missing data it sends a `refetch` — the server
re-runs the render and returns only the delta scope, no HTML.

**Save behavior:** fields stage into a `ctx`; Save commits all-or-none. No
immediate per-field save. Explicit Save is the natural version-commit unit and
pairs with the future conflict model.

**Creating set/dict entries** opens a create-mode `ObjectForm` inline (no
navigable create URL). On Save the draft commits via `set.add`; the create URL
never exists, so a URL only ever addresses a persisted entry.

**Deferred:** predictive prefetching + client-side caching — the render-coupled
storage engine (VISION pillar 5) is the destination this moves toward.
## Breadcrumbs

Breadcrumbs mirror the URL path **exactly, segment for segment** — the link
TARGETS are the cumulative URL prefixes (`/`, `/customers`, `/customers/42`),
so a click is always an in-app navigation and the trail equals the URL path.

The visible TEXT is the **labeled** trail (implemented; the generic UI):

- the **root** is the instance's display name, humanized (`Devlog`) — not the
  root-type name `Db` (which a bare/unit render with no name still falls back
  to);
- a **prop-name** segment is humanized (`customers` → `Customers`,
  `dueDate` → `Due date`);
- an **object** segment (a set member / object-dict entry) becomes that
  object's **label** — its type's `labelProp` value (`/customers/42` →
  `Acme`), so an opaque id reads as a name;
- a **scalar-dictionary entry** segment is the user's literal key, shown
  **verbatim** (`/settings/ORD-001` → `ORD-001`, never humanized);
- a missing/empty label falls back to the humanized raw segment.

The browser-tab `<title>` is the same labeled trail joined under the root
label. Both are computed on the server (SSR) and **re-resolved identically by
the client** on an SPA navigation — server and hydrated trails are
byte-identical (the path-ancestor objects named in the URL ship just their one
`labelProp` leaf for this; nothing else). C# `SsrRenderer.LabelTrail` /
`CodeExecutor.SegmentLabel` and TS `syncBreadcrumbs` / `segmentLabel` are the
twin halves.

Still deferred: long-label truncation/wrapping, and threading the display name
through a non-kernel (bare) render.

## Missing / deleted nodes

A URL that doesn't resolve (deleted dictionary key, unknown field) renders a
**"not found" view with breadcrumbs back to the parent**, so the user is
never stranded. Deletion-while-browsing is well-defined: by-key addressing
means the URL still names the same key; a deleted key cleanly resolves to
"not found" rather than silently showing a different entry. This one rule
covers every unresolvable path.

## Deliberately deferred (recorded, not forgotten)

- **Ordering.** Dictionary entry order is **unspecified** for now —
  whatever the implementation naturally produces. Nothing may *rely* on it:
  tests assert on **content** (key 42 has these values), never on
  **position** (row 0 is key 42). Stored / user-defined order is a later
  refinement.
- **Paging.** Tables render the whole dictionary for now. Known later need.
- **Labeled breadcrumbs** are now implemented (see Breadcrumbs above). What
  remains deferred is only long-label truncation/wrapping.
