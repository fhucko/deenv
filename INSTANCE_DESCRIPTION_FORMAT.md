# App description format

An instance is described by **one text document** (`instance.app`): types, an
optional `initialData` seed, and optional code (`common` / `ui`). Hand-written
today; the designer bridge prints the same format when publishing a design.
JSON is internal only — the parsed `InstanceDescription`, the wire, and storage.

Blocks are indentation-scoped (four spaces canonical; a block's indent is fixed
by its first line). The printer (`AppPrint`/`CodePrint`) emits the canonical
form, and `parse(print(description))` is the identity.

## Sections

```
types           ← required
initialData     ← optional: first-run seed
access          ← optional: deny-by-default rules (M-auth)
common          ← optional: shared functions
ui              ← optional: vars + functions + fn render()
```

When `ui` is present, code owns all routing and rendering; without it the
generic auto-form serves the instance.

## types

```
types
    Db
        users set of User
        settings dict of text by text
        lead User
    User
        name text
        boss User?
        status Status
    Status enum
        active
        suspended
    Flag bool
```

- **No colons in the `types` section** — a declaration is `name thing` (the name
  and what it is, separated by a space). The colon was dropped (it added nothing).
- An **object type** is a name with indented props; a **leaf alias** is
  `Name baseType` (one of `bool`, `int`, `decimal`, `text`, `date`, `datetime`);
  an **enum type** is `Name enum` then an indented list of its bare value names.
- A prop is `name type` — a base type, an enum type, or another type's name (a
  single object-typed prop *is* a reference). `set of X` declares a set (X must be
  an object type; members are keyed by their own identity). `dict of X by key`
  declares a dictionary (`by key` optional, default `text`). `list of X` declares
  an ordered sequence (X may be an object or a scalar; object members are
  addressed by id + membership, never by index). A trailing `?` marks
  the prop nullable.
- An **enum-typed prop** is a scalar: its value is one of the enum's value names
  (stored as that name, unset = empty). The generic UI renders it as a `<select>`
  whose options are the values, displayed humanized (`inProgress` → "In Progress").
- The root type **`Db`** must exist and must be an **object** type (it holds the
  app's data — props: scalars, references, sets, dictionaries, lists); it is implicitly
  single and non-null. A base-typed root (e.g. `Db bool`) is rejected at load.

## initialData

A hand-authored seed the store applies on first run only:

```
initialData
    Db 1
        users: [2]
    User 2
        name: "User 1"
        todoLists: [3]
    TodoList 3
        items: []
```

- Each entry is `TypeName id` (authored positive ids, globally unique) with
  indented `field: value` lines. Scalars are literals; a **set** is an array
  of member ids (order insignificant); a **list** is an array of member ids or
  scalar literals (**order significant**; duplicate object ids allowed); a
  **dict** is a map; a **single reference** is a bare id. Omitted scalars default;
  omitted references stay unset. Exactly one `Db` entry — the root.
- The id counter starts above the highest authored id.

## access

A deny-by-default ruleset, keyed by type name:

```
access
    Milestone
        read            where currentUser.role == "Admin"
        read create     where currentUser.role == "Member"
    Slice
        *
    Commit
        locked
    sys
        * where currentUser.role == "Admin"
```

- A rule line is a space-separated verb list (`read` / `create` / `edit` /
  `delete`, or `*` for all four) and an optional `where <expr>` condition —
  reusing the ordinary Code expression grammar, evaluated over a scope
  `{ currentUser, object }`. Condition absent ⇒ the rule always applies.
- **A subject (type name, or `sys`) may appear at most once** — its rules all
  go in one block. A repeated subject is a load error. This is not just tidiness:
  the floor ORs every rule for a subject together, so a second block granting a
  verb would silently widen (even un-do a `locked`) a rule in the first — so
  duplicate blocks are rejected, and each block holds the subject's complete,
  self-contained policy.
- **`locked`** is sugar for a subject that is **ruled with zero grants**:
  every client `create`/`edit`/`delete` on that type is denied, unconditionally
  (no role, no exception) — **reads are unaffected** (a type with no separate
  `read` rule stays unruled for read, so it loads exactly as it would without
  `locked`; this is write-immutability, not secrecy). It must be the
  subject's **only** rule (pairing it with any other grant is a load error —
  ambiguous, so rejected rather than silently combined; and since a subject
  appears in only one block, there is no second block that could re-grant a
  write around it) and has no meaning under the `sys` subject (host-action
  authority, not a data type's write floor) — both are load errors. Semantically
  identical to `create edit delete where false` (the idiom it replaces); the
  printer canonicalizes any such rule back to `locked`. Used for framework-owned
  history rows (`Commit`/`Branch`) that may only be written by a host action,
  never by a client mutation.
- **The section is entirely optional, and its absence means allow-all** — a
  dormant app has no rules and every type loads freely (today's default). Once
  the section exists, it governs only the types it NAMES: a type with no block
  of its own stays unruled (open), so adding one type's rules does not
  retroactively lock down the rest of the schema.
- An unmet condition (including "no rule grants anonymous access") fails
  **closed** — `currentUser` is `null` for an anonymous request, and a null
  property read (`currentUser.role`) evaluates to `null`, never a throw, so an
  unauthenticated visitor simply fails every role check.
- **`sys` is a reserved subject, not a type** — it is rejected as a declared
  type name. A `sys` rule governs kernel host actions (`create` /
  `cloneInstance` / `delete` / `publish` / `rename` / `setDesign` — the
  operator/designer's own instance-management calls), and unlike ordinary type
  rules it is **deny-by-default even when the app has no `access` section at
  all** — host-action authority is never open by default. Only apps whose own
  Code actually calls a host action (in practice: the designer) need it.
- **Known gap:** dictionary entries (`dict of X`) are not yet gated on read or
  write — don't rely on `access` to protect a dict-valued field yet.
- A `User` type is just an ordinary object type; role-based conditions
  (`currentUser.role == "..."`) assume one exists with a `role` enum prop and
  that principals log in via the built-in password/session mechanism (see
  `DeEnv/instances/5/app.deenv`, the devlog app, for a complete worked
  example — `User`/`Milestone`/`Slice` with per-type rules).

## common and ui — code

Code is an app.txt-style language parsed to the Code AST (the same AST the
twin interpreters execute and the wire ships):

```
common
    server fn hash(p)          ← `server` = never shipped to the client
        return p

ui
    var path = "/"
    var selectedUser

    fn selectUser(user)
        selectedUser = user

    fn render()                ← optional: the whole-app render (root view)
        return <main>
            <input class="new-user" value={newUser.name}>
            <button onClick={addNewUser}>
                "Add user"
            foreach user in db.users
                <div class="user-row">
                    user.name
            if selectedUser != null
                <h2>
                    selectedUser.name
```

- **Statements**: `var x` / `var x = expr`, assignment, `return`, calls,
  `if` / `else if` / `else`, named `fn`s, and `ambient name = value` — provide a
  dynamically-scoped value to callees for the rest of the enclosing scope (its
  first consumer is the data context, below).
- **Expressions**: literals (`0`, `-7`, `"text"` with `\" \\ \n \t` escapes,
  `true`, `null`), arrays `[a, b]`, objects `{ name: value }`, member/call
  chains (`db.tasks.where(p).orderBy(k)`), the operators
  `. ` → `* / %` → `+ -` → `== != > >= < <=` → `&&` → `||`, parentheses,
  inline lambdas `x => expr` / `(a, b) => expr` (multiline lambdas in `return`/tag positions).
- **Tags** are JSX-like with no closing tag — children are the indented
  block. Attributes: `attr="text"` or `attr={expr}`. Tag-level `if`/`else`
  and `foreach x in collection`.
- **Components**: a tag whose name resolves to an in-scope `fn` (top-level or
  local) is a *component* — it runs once per render-tree slot and keeps local
  state across re-renders (an element tag like `<div>` does not). Attributes bind
  to the function's parameters by name. An opt-in `key={expr}` folds into the
  component's slot identity, so changing it resets the component.
- A two-way `value`/`checked` binding on an `<input>` must target an
  assignable lvalue (a writable var or a prop chain).

### Data context (`ctx`)

Edits flow through an ambient **data context** — a staging overlay over the live
store. The framework provides a live root `ctx`; a component opens a staging child
with `ambient ctx = ctx.new()` (or `ctx.new(true)` for a live, autosave context).
While a staging `ctx` is in scope, writing a **persisted** object's field
(`obj.prop = …`, or a two-way `value=` binding) stages in the overlay — the stored
object is untouched until commit:

- `ctx.commit()` — flush the staged writes to the live store (one persisted edit
  per field).
- `ctx.discard()` — drop the overlay; bound inputs re-read the stored value.
- `ctx.dirty` — true while the overlay holds an uncommitted write.

A transient draft (a fresh `sys.new`, not yet persisted) always writes live, so a
create-form's in-progress edits are never swept into the surrounding transaction.

### Two modes: fully custom or fully auto

An app is one of two modes — there is no partial-customization middle layer (the
M8 `view` system was dropped). "Auto with overrides" lives within the custom mode:
a `fn render()` composes the public generic-UI component library (below) instead of
a separate view system.

- **Fully custom** — a `ui` section with `fn render()` owns the whole URL space. A
  full code page (two-way binding, WS mutations, the memo cache, refetch); it ships
  to the client and re-renders there.
- **Fully auto (the default)** — any app *without* a `fn render()` (including an app
  with no `ui` section at all) is rendered by the **self-hosted generic UI**: a single
  synthesized `fn render()` routes every URL via `sys.resolve(path)` and composes the
  public Code component library (`ObjectForm` / `RefEditor` / `SetTable` / `DictTable`
  / `LeafForm`, over the type's schema). There is no opt-in flag — auto is simply what
  you get without a custom render, and a custom render can compose the same PascalCase
  components.

## Validation a loader must enforce

A malformed document is rejected at load with a clear, specific error
(`SchemaValidationException`); a syntax error reports line and column.

- Type names are unique; a root type `Db` exists.
- Every prop's type resolves to a base type or a declared type; a set's
  element type is an object type; prop names are unique within a type.
- `initialData`: known types and fields, globally-unique positive ids, set/list
  members and references point at existing entries of the right type, exactly
  one `Db` entry.
- Code is structurally validated: symbols resolve, assignments target
  writable symbols, two-way bindings target lvalues, named-function call
  arity matches, no duplicate `var` in a block. (Type checking is deferred —
  type mismatches are runtime errors.)
- A `ui` section is optional; when present it may define `fn render()` (fully
  custom) and/or shared vars/helpers. Without a `fn render()`, the app is fully
  auto (the default self-hosted generic UI).

## Worked example

The committed default app is `DeEnv/instances/2/app.deenv`, the todo application —
types, seed, and the full UI in one document. The operator designer
(`DeEnv/instances/1/app.deenv`) is the IDE — a hand-rolled custom `fn render()` over
`db.types` plus the instances list.
