# Instance description format

The JSON shape that describes an instance. Hand-written for now (no
designer, no generator). The format is intentionally minimal: a type is a
shape; cardinality and nullability live on the *usage* (the prop), not on
the type itself.

## Top-level shape

```json
{
  "types": [ /* type definitions */ ]
}
```

The root is the type named **`Db`**. The root is implicitly **single** and
**non-null** — these are rules, not fields, and need not be stated.

## Type definition

| Field      | Required        | Meaning                                                             |
| ---------- | --------------- | ------------------------------------------------------------------- |
| `name`     | yes             | Identifier in the type registry; how other types refer to it.       |
| `baseType` | yes             | One of: `bool`, `int`, `decimal`, `text`, `date`, `datetime`, `object`. |
| `props`    | object types only | Ordered list of fields. Only present when `baseType` is `object`. |

A type carries **no cardinality and no nullability.** A type is just a
shape. (See "Why no aliases" below.)

## Prop

| Field         | Required | Default      | Meaning                                                         |
| ------------- | -------- | ------------ | --------------------------------------------------------------- |
| `name`        | yes      | —            | Field name within the object.                                   |
| `type`        | yes      | —            | Name of a type defined in `types`.                              |
| `cardinality` | no       | `single`     | `single` or `dictionary`.                                       |
| `nullability` | no       | `non-null`   | `non-null` or `nullable`.                                       |

Omit `cardinality` and `nullability` when they take the default. Read
literally: anything not stated is single and non-null.

## Dictionary keys

Dictionary keys are **`text`** in Milestone 1. (Per-dictionary key types
and key-field designation are deferred.)

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

- Exactly one type named `Db` exists.
- Every prop's `type` resolves to a type in `types`.
- `props` is present iff `baseType` is `object`.
- `cardinality` and `nullability` only appear on props, never on type
  definitions.
- `cardinality` is `single` or `dictionary`. `nullability` is `non-null` or
  `nullable`. No other values.
