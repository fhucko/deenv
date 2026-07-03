using System.Text;
using System.Text.Json;
using DeEnv.Code;

namespace DeEnv.Instance;

// Prints an InstanceDescription back to app-document text — the inverse of AppParse.
// Canonical form: four-space indentation, sections separated by one blank line, vars
// before functions before render. parse(print(desc)) ≡ desc (the round-trip tests),
// so the designer can present any stored description as editable text.
public static class AppPrint
{
    public static string Print(InstanceDescription desc)
    {
        var sb = new StringBuilder();

        sb.Append("types\n");
        foreach (var type in desc.Types ?? [])
            PrintType(sb, type);

        if (desc.InitialData?.Extents is { } extents)
        {
            sb.Append("\ninitialData\n");
            foreach (var (typeName, pool) in extents)
                foreach (var (id, fields) in pool)
                    PrintSeed(sb, typeName, id, fields);
        }

        if (desc.Rules is { Count: > 0 } rules)
            PrintAccess(sb, rules);

        if (desc.Common?.Functions is { Count: > 0 } commonFns)
        {
            sb.Append("\ncommon\n");
            foreach (var fn in commonFns)
                CodePrint.Function(sb, fn, "    ");
        }

        if (desc.Ui is { } ui)
        {
            sb.Append("\nui\n");
            foreach (var v in ui.Vars ?? [])
            {
                sb.Append("    var ").Append(v.Name);
                if (v.Value != null) sb.Append(" = ").Append(CodePrint.Value(v.Value));
                sb.Append('\n');
            }
            foreach (var fn in ui.Functions ?? [])
            {
                sb.Append('\n');
                CodePrint.Function(sb, fn, "    ");
            }
            if (ui.Render != null)
            {
                sb.Append('\n');
                CodePrint.Function(sb, ui.Render, "    ");
            }
        }

        return sb.ToString();
    }

    // ── types ────────────────────────────────────────────────────────────────────

    private static void PrintType(StringBuilder sb, TypeDefinition type)
    {
        // A type declaration carries no colon (`Name kind` / `name type`): the colon adds no value
        // in the types section. An enum type: `Name enum` then its ordered value names, indented.
        if (type.BaseType == BaseType.Enum)
        {
            sb.Append("    ").Append(type.Name).Append(" enum\n");
            foreach (var value in type.Values ?? [])
                sb.Append("        ").Append(value).Append('\n');
            return;
        }
        if (type.BaseType != BaseType.Object)
        {
            sb.Append("    ").Append(type.Name).Append(' ').Append(BaseTypes.NameOf(type.BaseType)).Append('\n');
            return;
        }
        sb.Append("    ").Append(type.Name).Append('\n');
        foreach (var prop in type.Props ?? [])
            sb.Append("        ").Append(prop.Name).Append(' ').Append(TypeExpr(prop)).Append('\n');
    }

    private static string TypeExpr(PropDefinition prop) => prop.Cardinality switch
    {
        Cardinality.Set => $"set of {prop.Type}",
        Cardinality.Dictionary => prop.KeyType is { } key ? $"dict of {prop.Type} by {key}" : $"dict of {prop.Type}",
        // A single prop: `Type` (+ `?` if nullable), then the optional `multiline` presentation
        // keyword (only ever set on a text prop) — so parse∘print round-trips.
        _ => prop.Type + (prop.Nullable ? "?" : "") + (prop.Multiline ? " multiline" : ""),
    };

    // ── initialData ──────────────────────────────────────────────────────────────

    private static void PrintSeed(StringBuilder sb, string typeName, string id, JsonElement fields)
    {
        sb.Append("    ").Append(typeName).Append(' ').Append(id).Append('\n');
        foreach (var field in fields.EnumerateObject())
            sb.Append("        ").Append(field.Name).Append(": ").Append(SeedValue(field.Value)).Append('\n');
    }

    private static string SeedValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => CodePrint.Value(new CodeText { Value = value.GetString() ?? "" }),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(e => e.GetRawText())) + "]",
        _ => throw new InvalidOperationException($"No seed text form for {value.ValueKind}."),
    };

    // ── access (M-auth) ────────────────────────────────────────────────────────────

    // Print the ruleset as the `access` section — the inverse of AppParse.AccessSection. Rules are
    // GROUPED by their type into one block each, IN FIRST-APPEARANCE ORDER (the canonical form), so
    // parse∘print is the identity on the Rules list and print∘parse is a fixpoint. Each rule line is
    // its verbs space-joined, then an optional `where <expr>` (the condition printed by CodePrint) —
    // UNLESS the type's rule list is exactly the `locked` shape (AppParse.IsLockedShape: a single rule
    // granting create/edit/delete with an always-false condition — what `locked` desugars to, and
    // also what a hand-written `create edit delete where false` means), in which case the canonical
    // form is `locked` — the sugar the parser accepts, printed back so round-tripping a `locked`
    // block reproduces `locked`, never the older `where false` spelling. A MULTI-rule block that
    // happens to include such a line is printed literally (the parser only ever produces the
    // single-rule shape from `locked`, so collapsing here stays lossless).
    private static void PrintAccess(StringBuilder sb, IReadOnlyList<AccessRule> rules)
    {
        sb.Append("\naccess\n");
        foreach (var type in rules.Select(r => r.Type).Distinct())
        {
            sb.Append("    ").Append(type).Append('\n');
            var typeRules = rules.Where(r => r.Type == type).ToList();
            if (typeRules is [{ } sole] && AppParse.IsLockedShape(sole.Verbs, sole.When))
            {
                sb.Append("        locked\n");
                continue;
            }
            foreach (var rule in typeRules)
            {
                sb.Append("        ").Append(string.Join(' ', rule.Verbs));
                if (rule.When is { } when) sb.Append(" where ").Append(CodePrint.Value(when));
                sb.Append('\n');
            }
        }
    }
}
