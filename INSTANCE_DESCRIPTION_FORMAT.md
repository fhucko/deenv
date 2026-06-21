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
  declares a dictionary (`by key` optional, default `text`). A trailing `?` marks
  the prop nullable.
- An **enum-typed prop** is a scalar: its value is one of the enum's value names
  (stored as that name, unset = empty). The generic UI renders it as a `<select>`
  whose options are the values, displayed humanized (`inProgress` → "In Progress").
- The root type **`Db`** must exist and must be an **object** type (it holds the
  app's data — props: scalars, references, sets, dictionaries); it is implicitly
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
  of member ids; a **single reference** is a bare id. Omitted scalars default;
  omitted references stay unset. Exactly one `Db` entry — the root.
- The id counter starts above the highest authored id.

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
  `if` / `else if` / `else`, named `fn`s.
- **Expressions**: literals (`0`, `-7`, `"text"` with `\" \\ \n \t` escapes,
  `true`, `null`), arrays `[a, b]`, objects `{ name: value }`, member/call
  chains (`db.tasks.where(p).orderBy(k)`), the operators
  `. ` → `* / %` → `+ -` → `== != > >= < <=` → `&&` → `||`, parentheses,
  inline lambdas `x => expr` / `(a, b) => expr` (multiline lambdas in `return`/tag positions).
- **Tags** are JSX-like with no closing tag — children are the indented
  block. Attributes: `attr="text"` or `attr={expr}`. Tag-level `if`/`else`
  and `foreach x in collection`.
- A two-way `value`/`checked` binding on an `<input>` must target an
  assignable lvalue (a writable var or a prop chain).

### Two modes: fully custom or fully auto

An app is one of two modes — there is no partial-customization middle layer (the
M8 `view` system was dropped; "auto with overrides" will come later via the custom
mode composing the generic UI as a library):

- **Fully custom** — a `ui` section with `fn render()` owns the whole URL space. A
  full code page (two-way binding, WS mutations, the memo cache, refetch); it ships
  to the client and re-renders there.
- **Fully auto (the default)** — any app *without* a `fn render()` (including an app
  with no `ui` section at all) is rendered by the **self-hosted generic UI** (M9):
  the generic object/reference/set/dictionary pages are rendered by a Code library
  (`objectForm`/`refEditor`/`setTable`/`dictTable` over the type's schema). There is
  no opt-in flag — auto is simply what you get without a custom render. (Navigating
  INTO a dictionary entry still falls to the retiring C# auto-form for now.)

## Validation a loader must enforce

A malformed document is rejected at load with a clear, specific error
(`SchemaValidationException`); a syntax error reports line and column.

- Type names are unique; a root type `Db` exists.
- Every prop's type resolves to a base type or a declared type; a set's
  element type is an object type; prop names are unique within a type.
- `initialData`: known types and fields, globally-unique positive ids, set
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

The committed default app is `DeEnv/instances/2/app.app`, the todo application —
types, seed, and the full UI in one document. The operator designer
(`DeEnv/instances/1/app.app`) is the IDE — a hand-rolled custom `fn render()` over
`db.types` plus the instances list.
