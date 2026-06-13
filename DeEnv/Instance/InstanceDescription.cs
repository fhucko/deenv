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

// A SYNTHESIZED generic-UI view (render-time only; never authored or printed — user
// `view` declarations were dropped in favour of two modes, fully auto / fully custom).
// `Type` is the type it renders; `Prop` (when set) marks a reference- or set-route view
// owning the page for prop `Type.Prop` (e.g. Db.lead / Db.notes), bound to the parent
// object. The function is anonymous — the target rides here, never on Fn.Name.
public record UiView(string? Type, CodeFunction Fn, string? Prop = null);

// The `ui` section: client-held state variables, shared component functions, views,
// and the optional entry-point `render` function (the implicit root path view —
// when present, code owns the whole URL space). Without it, views customize parts
// of the generic UI and everything else stays the auto-form.
// `Generic` is the opt-in to the self-hosted generic UI: when set, object pages of
// all-scalar types that lack an explicit view are rendered by the Code `objectForm`
// library (synthesized into per-type views at render time) instead of the C# auto-form.
public record InstanceUi(
    IReadOnlyList<UiVar>? Vars = null,
    IReadOnlyList<CodeFunction>? Functions = null,
    CodeFunction? Render = null,
    IReadOnlyList<UiView>? Views = null,
    bool Generic = false);

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
