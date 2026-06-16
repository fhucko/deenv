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
public record PropDefinition(
    string Name,
    string Type,
    Cardinality Cardinality = Cardinality.Single,
    string? KeyType = null,
    bool Nullable = false);

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

// A SYNTHESIZED generic-UI view (render-time only; never authored or printed — user
// `view` declarations were dropped in favour of two modes, fully auto / fully custom).
// `Type` is the type it renders; `Prop` (when set) marks a reference- or set-route view
// owning the page for prop `Type.Prop` (e.g. Db.lead / Db.notes), bound to the parent
// object. The function is anonymous — the target rides here, never on Fn.Name.
public record UiView(string? Type, CodeFunction Fn, string? Prop = null);

// The `ui` section: client-held state variables, shared component functions, the
// synthesized generic views, and the optional entry-point `render` function. With
// `fn render()` the code owns the whole URL space (fully-custom UI); without it the
// self-hosted generic UI is the default (synthesized into per-type Views at render
// time by GenericUi.Effective). `Views` is never authored — only synthesized.
public record InstanceUi(
    IReadOnlyList<UiVar>? Vars = null,
    IReadOnlyList<CodeFunction>? Functions = null,
    CodeFunction? Render = null,
    IReadOnlyList<UiView>? Views = null);

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
