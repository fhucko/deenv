# Instance description format

The JSON shape that describes an instance. Hand-written for now (no
designer, no generator). The format is intentionally minimal: a type is a
shape; cardinality and nullability live on the *usage* (the prop), not on
the type itself.

## Top-level shape

```json
{
  "types":       [ /* type definitions */ ],
  "ui":          { /* optional: vars + functions + render fn (Code AST) */ },
  "common":      { /* optional: shared functions (Code AST) */ },
  "initialData": { /* optional: hand-authored seed, normalized extents */ }
}
```

The root is the type named **`Db`**. The root is implicitly **single** and
**non-null** — these are rules, not fields, and need not be stated.

Only `types` is required. When `ui` is present, code owns all routing and
rendering (the generic auto-form is the no-`ui` fallback). The `ui`/`common`
code is hand-written JSON AST (`"type"`-discriminated nodes, camelCase) — see
the committed todo app (`DeEnv/instance.schema.json`) for the worked example
of all three sections.

## initialData

A hand-authored seed the store applies on first run only (when the data file
does not exist yet). Normalized extents in friendly form:

```json
"initialData": {
  "extents": {
    "Db":   { "1": { "users": [2] } },
    "User": { "2": { "name": "User 1", "todoLists": [3] } },
    "TodoList": { "3": { "name": "List 1", "items": [] } }
  }
}
```

- Each pool maps an **authored positive id** (globally unique) to the
  object's fields.
- Scalars are plain JSON values; a **set** is an array of member ids; a
  **single object reference** is a bare id. Omitted scalars default; omitted
  references stay unset. (Dictionary entries cannot be seeded yet.)
- Exactly one `Db` entry — the root.
- The id counter starts above the highest authored id, so later creations
  never collide.

## Type definition

| Field      | Required        | Meaning                                                             |
| ---------- | --------------- | ------------------------------------------------------------------- |
| `name`     | yes             | Identifier in the type registry; how other types refer to it.       |
| `baseType` | yes             | One of: `bool`, `int`, `decimal`, `text`, `date`, `datetime`, `object`. |
| `props`    | object types only | Ordered list of fields. Only present when `baseType` is `object`. |

A type carries **no cardinality and no nullability.** A type is just a
shape. (See "Why no aliases" below.)

## Prop

| Field           | Required        | Default            | Meaning                                                         |
| --------------- | --------------- | ------------------ | --------------------------------------------------------------- |
| `name`          | yes             | —                  | Field name within the object.                                   |
| `type`          | yes             | —                  | A base type name, or the name of a type defined in `types`.     |
| `cardinality`   | no              | `single`           | `single` or `dictionary`.                                       |
| `keyType`       | dictionary only | `text`             | Base type of the dictionary's keys.                             |
| `keyGeneration` | dictionary only | derived (see below)| `auto` or `manual`.                                             |
| `nullable`      | no              | `false`            | Boolean. `true` makes the field nullable; omit for non-null.    |

Omit `cardinality`, `keyType`, `keyGeneration`, and `nullable` when they
take the default. Read literally: anything not stated is single and non-null.

## Dictionary keys

A dictionary prop declares its key type (`keyType`) and how keys are produced
(`keyGeneration`):

- `keyType` — any base type (`text`, `int`, …). Defaults to `text`.
- `keyGeneration`:
  - **`auto`** — keys are auto-incremented by the platform (next = max + 1).
    Requires a numeric `keyType` (`int`). The create form has no key field.
  - **`manual`** — the user supplies the key when creating an entry. Works with
    any `keyType`. The create form includes a key field.
  - Default: `auto` when `keyType` is `int`, otherwise `manual`.

(Key-field designation — using one of the entry's own fields as its key — is
still deferred.)

## Worked examples

### Bool root — the simplest valid instance

```json
{
  "types": [
    { "name": "Db", "baseType": "bool" }
  ]
}
```

Renders as a single checkbox at `/`.

### Shop — object root with a dictionary of customers

```json
{
  "types": [
    {
      "name": "Db",
      "baseType": "object",
      "props": [
        { "name": "customers", "type": "Customer", "cardinality": "dictionary" }
      ]
    },
    {
      "name": "Customer",
      "baseType": "object",
      "props": [
        { "name": "name",   "type": "text" },
        { "name": "active", "type": "bool" }
      ]
    }
  ]
}
```

Renders `Db` as a form at `/` whose `customers` field is an HTML table.
Each row links to `/customers/{key}`, which renders a Customer form.

## Why no aliases (deferred)

TypeScript allows `type Names = string[]` — a type that *is* a collection.
Applied here, that would mean a type could be e.g. "dictionary of Customer"
in its own right, referenced by name from props. That is a convenience
(reuse) layered over the model, not part of it. It also partially breaks
the rule that cardinality lives on props — once aliases exist, cardinality
can live in two places, and prop sites stop being locally readable.

The current prop-side cardinality model is the canonical form. Aliases
would be sugar over it. **Deferred** as a future convenience; not built now.

## Validation rules a loader must enforce

A malformed document is rejected at load time with a clear, specific error
(`SchemaValidationException`) naming the offending type or prop — never an
obscure failure deeper in the renderer or storage.

- Type names are unique. (This also forbids a second `Db` — a duplicate `Db`
  is just a duplicate name, not a special case.)
- A root type named `Db` exists.
- `baseType` is one of the known base types
  (`bool`, `int`, `decimal`, `text`, `date`, `datetime`, `object`).
- Every prop's `type` resolves to a base type or a type in `types`.
- `props` is present iff `baseType` is `object`.
- Prop names are unique within a type.
- `cardinality` and `nullable` only appear on props, never on type
  definitions.
- `cardinality` is `single`, `set`, or `dictionary`. `nullable` is a boolean.
  No other values.
- A set's element type is an object type (members are keyed by their own
  identity), and a set declares no `keyType`.
- A dictionary's `keyType` is a known base type.
- The document is syntactically valid JSON.
- `initialData` (when present): types and fields exist, ids are unique
  positive integers, set members / single refs point at existing entries of
  the right type, and there is exactly one `Db` entry.
- `ui`/`common` code (when present) passes structural validation: symbols
  declared, assignments target writable symbols, two-way bindings target
  assignable lvalues.
