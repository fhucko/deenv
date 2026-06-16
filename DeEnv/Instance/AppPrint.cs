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
        // An enum type: `Name: enum` then its ordered value names, indented like object props.
        if (type.BaseType == BaseType.Enum)
        {
            sb.Append("    ").Append(type.Name).Append(": enum\n");
            foreach (var value in type.Values ?? [])
                sb.Append("        ").Append(value).Append('\n');
            return;
        }
        if (type.BaseType != BaseType.Object)
        {
            sb.Append("    ").Append(type.Name).Append(": ").Append(BaseTypes.NameOf(type.BaseType)).Append('\n');
            return;
        }
        sb.Append("    ").Append(type.Name).Append('\n');
        foreach (var prop in type.Props ?? [])
            sb.Append("        ").Append(prop.Name).Append(": ").Append(TypeExpr(prop)).Append('\n');
    }

    private static string TypeExpr(PropDefinition prop) => prop.Cardinality switch
    {
        Cardinality.Set => $"set of {prop.Type}",
        Cardinality.Dictionary => prop.KeyType is { } key ? $"dict of {prop.Type} by {key}" : $"dict of {prop.Type}",
        _ => prop.Type + (prop.Nullable ? "?" : ""),
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
}
