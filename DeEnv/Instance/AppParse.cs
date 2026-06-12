using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Code;
using DeEnv.Code.Parsing;
using static DeEnv.Code.Parsing.Parse;

namespace DeEnv.Instance;

// The app document grammar: ONE text file describes a whole instance — `types`,
// optional `initialData`, optional `common`, optional `ui` — parsed into the same
// InstanceDescription the retired JSON schema document used to declare. JSON remains
// internal only (the in-memory model, the wire, storage).
//
//   types
//       Db
//           users: set of User
//           settings: dict of text by text
//       User
//           name: text
//           boss: User?
//       Flag: bool                      ← a leaf type alias
//
//   initialData
//       Db 1
//           users: [2]
//       User 2
//           name: "User 1"
//
//   common / ui — the Code sections (see CodeParse).
public static class AppParse
{
    private static Parser<string> Name => Regex("[a-zA-Z_][a-zA-Z_0-9]*");

    // ── types ────────────────────────────────────────────────────────────────────

    // `name: type` / `name: set of Type` / `name: dict of Type by key`, `?` = nullable.
    private static Parser<Func<string, PropDefinition>> PropType => OneOf(
        Seq(Text("set"), Ws1, Text("of"), Ws1, Name,
            (_, _, _, _, elem) => (Func<string, PropDefinition>)(name =>
                new PropDefinition(name, elem, Cardinality.Set))),
        Seq(Text("dict"), Ws1, Text("of"), Ws1, Name,
            Optional(Seq(Ws1, Text("by"), Ws1, Name, (_, _, _, k) => k)),
            (_, _, _, _, elem, key) => (Func<string, PropDefinition>)(name =>
                new PropDefinition(name, elem, Cardinality.Dictionary, key))),
        Seq(Name, Optional(Text("?")),
            (type, nullable) => (Func<string, PropDefinition>)(name =>
                new PropDefinition(name, type, Cardinality.Single, Nullable: nullable != null))));

    private static Parser<PropDefinition> Prop =>
        Seq(Name, Ws0, Text(":"), Ws0, PropType, (name, _, _, _, make) => make(name));

    // An object type (name + indented props) or a leaf alias (`Name: baseType`).
    private static IndentedParser<TypeDefinition> TypeEntry => indent => OneOf(
        Seq(Name, Ws0, Text(":"), Ws0, Name, NlOrEnd,
            (name, _, _, _, baseName, _) => LeafType(name, baseName)),
        Seq(Name, NlOrEnd,
            IndentLookahead(indent, Ws1, propIndent =>
                Many1(Seq(Text(propIndent), Prop, NlOrEnd, (_, p, _) => p).SkipEmptyLinesBefore())),
            (name, _, props) => new TypeDefinition(name, BaseType.Object, props)));

    // Mapping unknown base names is deferred until the whole parse is chosen, so a
    // partial candidate never throws mid-backtracking; the marker type is resolved
    // in ResolveLeaves below.
    private static TypeDefinition LeafType(string name, string baseName) =>
        new(name, BaseTypes.IsName(baseName) ? BaseTypes.Parse(baseName) : UnknownBase, Props: null);

    private const BaseType UnknownBase = (BaseType)(-1);

    private static Parser<TypeDefinition[]> TypesSection =>
        Seq(Text("types"), NlOrEnd,
            IndentLookahead("", Ws1, indent =>
                Many1(Seq(Text(indent), TypeEntry(indent), (_, t) => t).SkipEmptyLinesBefore())),
            (_, _, types) => types)
        .SkipEmptyLinesBefore();

    // ── initialData ──────────────────────────────────────────────────────────────
    // Seeds parse into the same friendly JSON shapes the store consumes: plain
    // scalars, sets as arrays of member ids, single refs as bare ids.

    private static Parser<JsonNode?> SeedValue => OneOf<JsonNode?>(
        CodeParse.TextLiteral.ConvertTo(t => (JsonNode?)JsonValue.Create(t.Value)),
        Regex(@"-?[0-9]+\.[0-9]+").ConvertTo(d => (JsonNode?)JsonValue.Create(
            decimal.Parse(d, System.Globalization.CultureInfo.InvariantCulture))),
        Regex("-?(0|[1-9][0-9]*)").ConvertTo(i => (JsonNode?)JsonValue.Create(int.Parse(i))),
        Text("true").ConvertTo(_ => (JsonNode?)JsonValue.Create(true)),
        Text("false").ConvertTo(_ => (JsonNode?)JsonValue.Create(false)),
        Seq(Text("["), Ws0,
            Many0Separated(Text(","), Seq(Ws0, Regex("[1-9][0-9]*"), Ws0, (_, id, _) => id)),
            Text("]"),
            (_, _, ids, _) => (JsonNode?)new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(int.Parse(id))).ToArray())));

    private static IndentedParser<(string Type, string Id, JsonObject Fields)> SeedEntry => indent =>
        Seq(Name, Ws1, Regex("[1-9][0-9]*"), NlOrEnd,
            Optional(IndentLookahead(indent, Ws1, fieldIndent =>
                Many1(Seq(Text(fieldIndent), Name, Ws0, Text(":"), Ws0, SeedValue, NlOrEnd,
                    (_, field, _, _, _, value, _) => (field, value)).SkipEmptyLinesBefore()))),
            (type, _, id, _, fields) =>
            {
                // DeepClone: backtracking re-runs this combine over candidates that
                // share the same parsed JsonNode instances — a node can only have
                // one parent, so each candidate's object gets its own copies.
                var obj = new JsonObject();
                foreach (var (field, value) in fields ?? []) obj[field] = value?.DeepClone();
                return (type, id, obj);
            });

    private static Parser<(string Type, string Id, JsonObject Fields)[]> InitialDataSection =>
        Seq(Text("initialData"), NlOrEnd,
            IndentLookahead("", Ws1, indent =>
                Many1(Seq(Text(indent), SeedEntry(indent), (_, e) => e).SkipEmptyLinesBefore())),
            (_, _, entries) => entries)
        .SkipEmptyLinesBefore();

    // ── the document ─────────────────────────────────────────────────────────────

    private static Parser<(TypeDefinition[] Types,
                           (string, string, JsonObject)[]? Seeds,
                           ICodeStatement[]? Common,
                           ICodeStatement[]? Ui)> Document =>
        Seq(TypesSection,
            Optional(InitialDataSection),
            Optional(CodeParse.Section("common")),
            Optional(CodeParse.Section("ui")),
            (types, seeds, common, ui) => (types, seeds, common, ui))
        .SkipEmptyLinesAfter();

    // Parse a whole app document. Syntax errors throw CodeParseException (positioned);
    // semantic mapping errors throw SchemaValidationException.
    public static InstanceDescription Parse(string source)
    {
        var (types, seeds, common, ui) = Run(Seq(Document, Ws0, (d, _) => d), source);
        return new InstanceDescription(
            Types: ResolveLeaves(types),
            Ui: ui == null ? null : CodeParse.MapUi(ui),
            Common: CodeParse.MapCommon(common),
            InitialData: MapInitialData(seeds));
    }

    private static IReadOnlyList<TypeDefinition> ResolveLeaves(TypeDefinition[] types)
    {
        foreach (var type in types)
            if (type.BaseType == UnknownBase)
                throw new SchemaValidationException(
                    $"Type '{type.Name}' has an unknown baseType (expected one of: {string.Join(", ", BaseTypes.Names)}).");
        return types;
    }

    private static InstanceInitialData? MapInitialData((string Type, string Id, JsonObject Fields)[]? seeds)
    {
        if (seeds == null) return null;
        var extents = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>();
        var pools = new Dictionary<string, Dictionary<string, JsonElement>>();
        foreach (var (type, id, fields) in seeds)
        {
            if (!pools.TryGetValue(type, out var pool))
            {
                pool = [];
                pools[type] = pool;
                extents[type] = pool;
            }
            if (pool.ContainsKey(id))
                throw new SchemaValidationException($"initialData has two '{type}' entries with id {id}.");
            pool[id] = JsonSerializer.SerializeToElement(fields);
        }
        return new InstanceInitialData(extents);
    }
}
