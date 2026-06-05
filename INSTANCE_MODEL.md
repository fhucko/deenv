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
- **base type** — `bool`, `int`, `decimal`, `text`, `date`, `datetime`.
- **object** — a user-defined named type with a **fixed, ordered set of
  fields**, each field a `(name, type)` pair where `type` is any type
  (base, object, or dictionary). Structure is declared up front, not
  dynamic. E.g. `Customer = { name: text, balance: decimal,
  orders: dictionary of Order }`.
- **dictionary of <type>** — the only collection kind. Keyed; each entry
  addressed by a **stable key**.

Everything below (addressing, rendering, breadcrumbs) is a recursion over
this definition. The bool-root case is simply the recursion bottoming out
immediately.

(There is no `set` and no `list`. Dictionary is the only collection — see
"Why dictionaries only" and "Ordering".)

## URL = navigation into the data

Every node is addressable by URL; the path walks the data tree from `Db`:
- `/` — the root value.
- `/field` — a field of the current object.
- `/dictField/42` — the entry with **key** 42 of a dictionary.
- nests arbitrarily: `/customers/42/orders/7/total`.

A URL addresses a node; reading returns that node's data; writes target the
URL of the thing changed (read/write addressing is symmetric).

## Why dictionaries only (stable identity everywhere)

Because dictionaries are the only collection and every entry has a stable
key, **every node in the whole tree is addressed by stable identity**. There
is no positional addressing anywhere, so a URL always means the same node
until that exact node is deleted. This removes the entire class of "the
thing under my URL silently changed" problems that positional lists have.

Lists are dropped — not deferred. Positional addressing is unstable under
deletion/reordering, and their use cases are covered without them:

**Ordering.** Order is provided by a per-dictionary **ordering function**.
Derivable orders sort by real fields. If a user wants explicit/arbitrary
order, they add a **position prop** (e.g. a decimal, so values can sit
between others) and the ordering function sorts by it. Order is therefore
just ordinary data on the entry — editable, versioned, and addressable like
any field, with no special collection behavior. Trade: the user manages
position values themselves; the model does not auto-shift entries to make
room (that auto-management, if ever wanted, is a later feature on top, not a
model change).

## Rendering: one form per type

Traversal granularity is **one form per type instance**:
- A **bool root** renders as just a **checkbox** (no field label) — the
  simplest valid instance.
- An **object** renders as a form of its fields.
- A field that is a **dictionary** renders as an **HTML table**; each row
  links to that entry's form by key.
- Leaf fields render as form inputs.

The whole app is: forms for objects, tables for dictionaries, click a row to
descend.

## Navigation granularity: the dictionary is the only boundary

A single page shows one node and **everything reachable from it except
across a dictionary.** Concretely:
- **Single-valued nested objects render inline**, however deep. A customer
  with an address (and whatever the address nests) is all on one page,
  edited together. No depth limit, no inline-vs-link judgment.
- **Dictionaries are the only navigation boundary.** A dictionary field
  renders as a table; you navigate (new page) only by clicking a row into an
  entry, or by following a field that is a dictionary.

This is deliberately the simplest consistent rule: the page boundary is
predictable — follow the data from this node, stop at every dictionary.

Accepted consequence: a type with deep single-valued nesting produces a
large page. Fine for the eshop/CRM "edit the whole record in one place"
case; revisit only if a real type produces an unwieldy page (same spirit as
no-paging). (Future: UI customization / auto-tabs will present large pages
better — build the renderer so a presentation layer can sit on top later.)

URL scheme is unaffected: every node stays addressable (e.g.
`/customers/42/address` resolves), it just renders inline on the parent page
under normal viewing. The URL works; only dictionaries force a new page.

## Rendering architecture (staged)

**Now (Milestone 1):**
- **Server-rendered first paint.** C# renders complete HTML for each URL.
  Every node is a real page: directly reachable, bookmarkable, works without
  JS. This is what makes "URL = navigation" literal.
- **Client takeover after first paint.** TypeScript then drives the app
  "like React": subsequent navigation is client-side — TS fetches the target
  node's data as JSON and renders its form/table itself, without a full
  server page render.
- **So C# serves two things per node:** rendered HTML (first paint / direct
  URL hits) and a JSON data endpoint (client-side navigation).
- **TS owns** rendering-after-first-paint and all interaction, including
  background save-on-change.

**Save behavior:**
- **Toggles save immediately** — a bool checkbox (and toggle-like flags such
  as "active") save on change, in the background, no full reload.
- **Object forms use explicit save** — multi-field records (the CRM/eshop
  case) are filled and committed together via a Save button. This pairs
  naturally with the future stale-version conflict flow (see DECISIONS.md):
  immediate per-field save would fight optimistic concurrency.

**Far future (deferred):**
- **Predictive prefetching + client-side caching** — loading data for nodes
  the current view implies you'll visit next, so navigation feels instant.
  This is explicitly where the render-coupled storage engine (VISION pillar
  4) belongs: "prefetch what the view implies" *is* that pillar. Not built
  now; do not pull into Milestone 1.

## Breadcrumbs

Breadcrumbs mirror the URL path **exactly, segment for segment.**
`/customers/42` → `Db › customers › 42`. Invariant: breadcrumb trail equals
the URL path. (Labeled breadcrumbs — a name instead of `42` — deferred.)

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
- **Labeled breadcrumbs.**
