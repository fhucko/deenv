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

    // `name type` / `name set of Type` / `name dict of Type by key`, `?` = nullable (no colon —
    // the name and its type are separated by whitespace; see Prop). A single scalar prop may carry
    // an optional trailing `multiline` keyword (`notes text multiline`) — a presentation attribute
    // that makes the generic UI render a <textarea>; it is grammatically valid only after a single
    // prop's type (never on a set/dict — there it simply fails to parse), and the loader further
    // restricts it to `text` props.
    private static Parser<Func<string, PropDefinition>> PropType => OneOf(
        Seq(Text("set"), Ws1, Text("of"), Ws1, Name,
            (_, _, _, _, elem) => (Func<string, PropDefinition>)(name =>
                new PropDefinition(name, elem, Cardinality.Set))),
        Seq(Text("dict"), Ws1, Text("of"), Ws1, Name,
            Optional(Seq(Ws1, Text("by"), Ws1, Name, (_, _, _, k) => k)),
            (_, _, _, _, elem, key) => (Func<string, PropDefinition>)(name =>
                new PropDefinition(name, elem, Cardinality.Dictionary, key))),
        Seq(Name, Optional(Text("?")), Optional(Seq(Ws1, Text("multiline"), (_, kw) => kw)),
            (type, nullable, multiline) => (Func<string, PropDefinition>)(name =>
                new PropDefinition(name, type, Cardinality.Single,
                    Nullable: nullable != null, Multiline: multiline != null))));

    // A prop is `name <type>` — the name and its type separated by whitespace, no colon.
    private static Parser<PropDefinition> Prop =>
        Seq(Name, Ws1, PropType, (name, _, make) => make(name));

    // A type declaration is one of three forms, discriminated by what follows the name — NOT by
    // the order of these alternatives (the parser is non-deterministic: `Parse.Run` enumerates all
    // parses and returns the unique one that consumes the whole document). No colon anywhere; the
    // forms are mutually exclusive at the token, so at most one ever matches:
    //   • `Name enum` + an indented bare value-name list  → an enum type.
    //   • `Name <baseType>` where baseType is any name EXCEPT the reserved `enum` keyword (the
    //     Filter) → a leaf alias. The Filter keeps it from also matching `Name enum`, so the
    //     enum-vs-leaf choice does not hinge on which alternative is listed first.
    //   • `Name` then a newline + indented props → an object type. Distinguished from the other two
    //     by having NO second token on the line (`Ws1` is space/tab only, never the newline), so a
    //     bare `Db` never matches the leaf/enum forms.
    private static IndentedParser<TypeDefinition> TypeEntry => indent => OneOf(
        Seq(Name, Ws1, Text("enum"), NlOrEnd,
            IndentLookahead(indent, Ws1, valueIndent =>
                Many1(Seq(Text(valueIndent), Name, NlOrEnd, (_, v, _) => v).SkipEmptyLinesBefore())),
            (name, _, _, _, values) => new TypeDefinition(name, BaseType.Enum, Props: null, Values: values)),
        Seq(Name, Ws1, Name.Filter(baseName => baseName != "enum"), NlOrEnd,
            (name, _, baseName, _) => LeafType(name, baseName)),
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
                Many1(Seq(Text(indent), TypeEntry(indent), (_, t) => t).SkipEmptyLinesBefore()))
                .SkipEmptyLinesBefore(),
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
                Many1(Seq(Text(indent), SeedEntry(indent), (_, e) => e).SkipEmptyLinesBefore()))
                .SkipEmptyLinesBefore(),
            (_, _, entries) => entries)
        .SkipEmptyLinesBefore();

    // ── access (M-auth) ────────────────────────────────────────────────────────────
    // The deny-by-default ruleset section. Mirrors `types` in shape (a header + indented
    // type-blocks, each block a type name then its indented rule lines):
    //
    //   access
    //       Milestone
    //           read            where currentUser.role == "Admin"
    //           read create     where currentUser.role == "Member"
    //           *
    //       Commit
    //           locked
    //
    // A rule line is EITHER `locked` — sugar for "every write denied, reads unaffected" (see
    // below) — OR a verb list (read | create | edit | delete, or `*` = all) and an OPTIONAL
    // `where <expr>` condition — REUSING the existing CodeParse expression parser, not a new
    // condition grammar. Absent ⇒ the rule always applies. The condition AST is stored verbatim
    // on the rule and evaluated by the existing interpreter at the floor (over { currentUser,
    // object }). This section is OPTIONAL — absent ⇒ no rules ⇒ the app is dormant (allow-all).

    private static readonly string[] VerbNames = ["read", "create", "edit", "delete"];

    private static Parser<string> Verb => OneOf(VerbNames.Append("*").Select(Text).ToArray());

    // `locked` (M13 slice — the framework-history immutability idiom) desugars to EXACTLY what
    // `create edit delete where false` already means under AccessFloor.Can: RULED for every write
    // verb, with a condition that never holds — so every create/edit/delete is denied and reads
    // stay whatever they otherwise are (unruled ⇒ open, exactly as `where false` leaves them
    // today). It is spelling sugar, not a new floor concept — AccessFloor needs no change; the
    // printer recognizes this exact shape (WriteVerbs + WhenAlwaysFalse) and prints `locked` back
    // (AppPrint.PrintAccess). `WhenAlwaysFalse` is a single canonical CodeBool instance so the
    // printer's shape check is a reference-independent VALUE comparison (see IsLockedShape).
    public static readonly string[] WriteVerbs = ["create", "edit", "delete"];
    private static readonly ICodeValue WhenAlwaysFalse = new CodeBool { Value = false };

    // Does this exact (verbs, when) shape mean "locked"? Same verb SET as WriteVerbs (order-
    // independent — a hand-written `delete edit create where false` is the identical rule) and a
    // condition that is the literal `false` (structurally, not by reference — a freshly re-parsed
    // `where false` line is an equal-but-distinct CodeBool instance). Shared by the printer
    // (canonicalize `where false` back to `locked`) and by anyone needing "is this a locked rule".
    public static bool IsLockedShape(IReadOnlyList<string> verbs, ICodeValue? when) =>
        when is CodeBool { Value: false } && verbs.ToHashSet().SetEquals(WriteVerbs);

    // A rule line is `locked` (no verbs, no condition — the shape below is synthesized) or an
    // ordinary verb-list line. `IsLocked` is consulted ONLY by the enclosing type-block, to
    // validate `locked` is the subject's sole line and never appears under `sys` — it never
    // reaches AccessRule (the synthesized Verbs/When are indistinguishable from hand-written
    // `create edit delete where false` from that point on, which is the point: pure sugar).
    private static Parser<(bool IsLocked, string[] Verbs, ICodeValue? When)> LockedRuleLine =>
        Seq(Text("locked"), NlOrEnd, (_, _) => (true, WriteVerbs, (ICodeValue?)WhenAlwaysFalse));

    private static Parser<(bool IsLocked, string[] Verbs, ICodeValue? When)> GrantRuleLine =>
        Seq(Many1Separated(Verb, Ws1),
            Optional(Seq(Ws1, Text("where"), Ws1, CodeParse.Value, (_, _, _, expr) => expr)),
            NlOrEnd,
            (verbs, when, _) => (false, verbs, when));

    // A rule line: `locked`, or a space-separated verb list (`*` is a lone verb) with an optional
    // `where <expr>`, then end-of-line. `locked` is tried FIRST — it is a fixed keyword at this
    // grammar position (never a verb name), so there is no ambiguity to backtrack through.
    private static Parser<(bool IsLocked, string[] Verbs, ICodeValue? When)> Rule =>
        OneOf(LockedRuleLine, GrantRuleLine);

    // One type's rule block: the type name on its own line, then its indented rule lines, each
    // mapped to an AccessRule carrying that type. (Sits at the `access` section's item indent.)
    // `locked` validation happens HERE, where the subject name and the full set of the block's
    // raw lines are both in scope, before they flatten into AccessRule[] (which no longer
    // distinguishes `locked` from a hand-written equivalent — see IsLockedShape):
    //   - `locked` must be the ONLY line for its subject (locked + any grant is ambiguous —
    //     which one governs? — so it is rejected, not silently combined).
    //   - `locked` is meaningless under the `sys` subject (sys is host-action grants; there is no
    //     write floor to lock there), so it is rejected there too.
    private static IndentedParser<AccessRule[]> AccessTypeEntry => indent =>
        Seq(Name, NlOrEnd,
            IndentLookahead(indent, Ws1, ruleIndent =>
                Many1(Seq(Text(ruleIndent), Rule, (_, r) => r).SkipEmptyLinesBefore())),
            (type, _, rules) =>
            {
                if (rules.Any(r => r.IsLocked))
                {
                    if (rules.Length > 1)
                        throw new SchemaValidationException(
                            $"Access subject '{type}' uses 'locked', which must be its ONLY rule " +
                            $"(found {rules.Length} rule lines). 'locked' already denies every write; " +
                            $"add no other grants alongside it.");
                    if (type == Code.AccessFloor.SysSubject)
                        throw new SchemaValidationException(
                            $"'{Code.AccessFloor.SysSubject}' cannot use 'locked': it governs host-action " +
                            $"authority (create/delete/publish/…), not a data type's write floor, so " +
                            $"'locked' has no meaning there.");
                }
                return rules.Select(r => new AccessRule(type, r.Verbs, r.When)).ToArray();
            });

    // The access section: a header then one block per subject. A subject may appear AT MOST ONCE
    // (each block's rules all share one Type — AccessTypeEntry stamps it). The duplicate-subject
    // rejection lives HERE, where block boundaries are still visible (`blocks` is one AccessRule[]
    // per block) — they are LOST by the SelectMany flatten into the single desc.Rules list that the
    // rest of the system (AccessFloor, AppPrint) sees. This matters for correctness, not just tidiness:
    // AccessFloor.Can ORs across EVERY rule for a subject, so a second block granting a write would
    // silently un-do a `locked` (or any deny) in the first — a real bypass, not merely ambiguous noise.
    // Enforcing one-block-per-subject at this parse layer also makes AccessTypeEntry's per-block
    // sole-rule check COMPLETE BY CONSTRUCTION (one subject = one block ⇒ block-local == global), and
    // catches every load path since all of them parse text through here.
    private static Parser<AccessRule[]> AccessSection =>
        Seq(Text("access"), NlOrEnd,
            IndentLookahead("", Ws1, indent =>
                Many1(Seq(Text(indent), AccessTypeEntry(indent), (_, t) => t).SkipEmptyLinesBefore()))
                .SkipEmptyLinesBefore(),
            (_, _, blocks) =>
            {
                var seen = new HashSet<string>();
                foreach (var block in blocks)
                    if (block.Length > 0 && !seen.Add(block[0].Type))
                        throw new SchemaValidationException(
                            $"Duplicate access subject '{block[0].Type}': every subject may appear at " +
                            $"most once in the access section (combine its rules into one block).");
                return blocks.SelectMany(b => b).ToArray();
            })
        .SkipEmptyLinesBefore();

    // ── the document ─────────────────────────────────────────────────────────────

    private static Parser<(TypeDefinition[] Types,
                           (string, string, JsonObject)[]? Seeds,
                           AccessRule[]? Rules,
                           ICodeStatement[]? Common,
                           ICodeStatement[]? Ui)> Design =>
        Seq(TypesSection,
            Optional(InitialDataSection),
            Optional(AccessSection),
            Optional(CodeParse.Section("common")),
            Optional(CodeParse.Section("ui")),
            (types, seeds, rules, common, ui) => (types, seeds, rules, common, ui))
        .SkipEmptyLinesAfter();

    // Parse a whole app document. Syntax errors throw CodeParseException (positioned);
    // semantic mapping errors throw SchemaValidationException.
    public static InstanceDescription Parse(string source)
    {
        var (types, seeds, rules, common, ui) = Run(Seq(Design, Ws0, (d, _) => d), source);
        return new InstanceDescription(
            Types: ResolveLeaves(types),
            Ui: ui == null ? null : CodeParse.MapUi(ui),
            Common: CodeParse.MapCommon(common),
            InitialData: MapInitialData(seeds),
            Rules: rules);
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
