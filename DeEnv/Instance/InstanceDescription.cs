using System.Text.Json;
using DeEnv.Code;

namespace DeEnv.Instance;

public enum BaseType { Bool, Int, Decimal, Text, Date, DateTime, Object }

public enum Cardinality { Single, Dictionary, Set }

// Plain-data records. All JSON casing comes from SchemaJson.Options (camelCase
// property policy + string-enum converter) — no per-property attributes, no logic.
public record PropDefinition(
    string Name,
    string Type,
    Cardinality Cardinality = Cardinality.Single,
    string? KeyType = null,
    bool Nullable = false);

public record TypeDefinition(
    string Name,
    BaseType BaseType,
    IReadOnlyList<PropDefinition>? Props = null);

// A top-level UI state variable (session/UI state: path, selection, transient
// newItem). Client-held; for SSR the initializer seeds its first-paint value.
public record UiVar(string Name, ICodeValue? Value = null);

// The `ui` section: client-held state variables, shared component functions, and
// the entry-point `render` function. When present, code owns all routing and the
// generic auto-form is no longer used.
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
