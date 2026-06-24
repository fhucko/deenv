using System.Text.Json;
using DeEnv.Code;

namespace DeEnv.Instance;

// Bool…DateTime are the leaf VALUE base types; Object and Enum are the two type-KINDS
// (a type whose baseType is Object or Enum is a declared type, never a prop's leaf type).
// An enum VALUE is a value name and travels/stores/interprets as Text — there is no new
// storage value-kind, wire tag, or Code-runtime value; the type-kind only carries its
// ordered value names (TypeDefinition.Values) for validation and the generic UI <select>.
public enum BaseType { Bool, Int, Decimal, Text, Date, DateTime, Object, Enum }

public enum Cardinality { Single, Dictionary, Set }

// Plain-data records. All JSON casing comes from SchemaJson.Options (camelCase
// property policy + string-enum converter) — no per-property attributes, no logic.
//
// Multiline is a PRESENTATION attribute on a single `text` prop: the value is and stays
// text (no storage/wire/interpreter value change), it only makes the generic UI render a
// <textarea> editor instead of a single-line <input>. Default false (absent ⇒ off — the
// common case is zero-config). Valid only on a single text prop; the loader rejects it
// elsewhere. It is a single bool, NOT a general prop-attribute mechanism — generalize only
// if a second presentation attribute ever appears.
public record PropDefinition(
    string Name,
    string Type,
    Cardinality Cardinality = Cardinality.Single,
    string? KeyType = null,
    bool Nullable = false,
    bool Multiline = false);

// A type's shape: an object type carries Props; an enum type (BaseType.Enum) carries its
// ordered value names in Values; a leaf alias carries neither. Props and Values are mutually
// exclusive (an enum is not an object), enforced by the loader.
public record TypeDefinition(
    string Name,
    BaseType BaseType,
    IReadOnlyList<PropDefinition>? Props = null,
    IReadOnlyList<string>? Values = null);

// A top-level UI state variable (session/UI state: path, selection, transient
// newItem). Client-held; for SSR the initializer seeds its first-paint value.
public record UiVar(string Name, ICodeValue? Value = null);

// The `ui` section: client-held state variables, shared component functions, and the
// entry-point `render` function. With a custom `fn render()` the code owns the whole URL
// space (fully-custom UI); without it the self-hosted generic UI is the default — and
// GenericUi.Effective then synthesizes a generic `fn render()` (a router that calls
// sys.resolve + composes the library) as the effective Render. Either way every page is
// one render; there is no per-URL view dispatch.
public record InstanceUi(
    IReadOnlyList<UiVar>? Vars = null,
    IReadOnlyList<CodeFunction>? Functions = null,
    CodeFunction? Render = null);

// The `common` section: functions shared by server and client. A function may be
// marked server-only (CodeFunction.ServerOnly) so it is never shipped to the client.
public record InstanceCommon(IReadOnlyList<CodeFunction>? Functions = null);

// The hand-authored seed: normalized extents, applied by the store on first run only.
// Each pool maps an authored id to the object's fields in friendly form — plain JSON
// scalars, sets as arrays of member ids, single object refs as bare ids. Must contain
// exactly one Db entry (the root). nextId is computed above the highest authored id.
public record InstanceInitialData(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? Extents = null);

// Parsed from ONE app text document (AppParse) — the only authoring surface. This
// record (and its JSON form) is internal: the in-memory model and the wire.
public record InstanceDescription(
    IReadOnlyList<TypeDefinition>? Types = null,
    InstanceUi? Ui = null,
    InstanceCommon? Common = null,
    InstanceInitialData? InitialData = null);
