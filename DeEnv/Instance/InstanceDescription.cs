using System.Text.Json;
using DeEnv.Code;

namespace DeEnv.Instance;

// Bool…DateTime are the leaf VALUE base types; Object and Enum are the two type-KINDS
// (a type whose baseType is Object or Enum is a declared type, never a prop's leaf type).
// An enum VALUE is a value name and travels/stores/interprets as Text — there is no new
// storage value-kind, wire tag, or Code-runtime value; the type-kind only carries its
// ordered value names (TypeDefinition.Values) for validation and the generic UI <select>.
//
// Password is a leaf base-type NAME (used in prop position like `text`/`int`, never a
// declared type) whose VALUE behaves exactly like Text everywhere — parser, storage, wire,
// interpreter — EXCEPT two kernel chokepoints (M-auth, the `password` type): the load
// boundary ships "" instead of the stored hash (DbBridge / AccessFloor.ScalarObject) and the
// WS write layer PBKDF2-hashes a plaintext before the store (WsHandler.HashPasswordFields). So
// every value switch maps Password → Text (mirroring Enum → Text); only those two chokepoints
// key on BaseType.Password. It is its OWN member (not a Text alias) so NameOf stays unambiguous
// (BaseTypes round-trips `password` ↔ Password) and AppPrint reproduces it.
// Image is a leaf base-type NAME exactly like Password (same mechanics, above): its VALUE behaves
// like Text everywhere — parser, storage, wire, interpreter — and holds a content-addressed blob
// pool NAME (`<sha256-hex>.<ext>`), never bytes (docs/plans/assets-design.md, Storage/IBlobPool.cs).
// No load-boundary blanking (unlike Password) — a hash is not a secret — so it needs no chokepoint
// of its own; every value switch simply maps Image → Text (mirroring Enum/Password → Text).
public enum BaseType { Bool, Int, Decimal, Text, Date, DateTime, Object, Enum, Password, Image }

public enum Cardinality { Single, Dictionary, Set, List }

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

// One access-control rule (M-auth): a deny-by-default ruleset entry over the object model.
// `Type` is the rule's subject (a declared type name, e.g. "Milestone"); `Verbs` the actions
// it grants (read | create | edit | delete, or `*` = all); `When` an OPTIONAL Code condition
// (an AST node) the existing interpreter evaluates over { currentUser, object } — null = the
// rule always applies. The read floor (AccessFloor) enforces `read` only this slice; the other
// verbs parse/print but are not yet checked. A per-field form (a `Field` subject) is a later
// slice — a type-level rule only, for now.
public record AccessRule(
    string Type,
    IReadOnlyList<string> Verbs,
    ICodeValue? When = null);

// Parsed from ONE app text document (AppParse) — the only authoring surface. This
// record (and its JSON form) is internal: the in-memory model and the wire.
//
// `Rules` is the M-auth access ruleset (the `access` section). ADDITIVE: absent/empty ⇒ the
// app is DORMANT — allow-all, exactly today's behavior (conditions never run, no login). The
// rules ARE the activation switch; writing the first rule turns enforcement on.
public record InstanceDescription(
    IReadOnlyList<TypeDefinition>? Types = null,
    InstanceUi? Ui = null,
    InstanceCommon? Common = null,
    InstanceInitialData? InitialData = null,
    IReadOnlyList<AccessRule>? Rules = null);
