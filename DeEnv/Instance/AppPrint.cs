using System.Text;

namespace DeEnv.Instance;

// Prints an InstanceDescription back to app-document text (the inverse of AppParse).
// Currently the `types` section — what the designer bridge needs to publish a designed
// schema as a canonical app file. The code printer (common/ui) is the milestone's
// final stage; initialData printing comes with it.
public static class AppPrint
{
    public static string Print(InstanceDescription desc)
    {
        var sb = new StringBuilder();
        sb.Append("types\n");
        foreach (var type in desc.Types ?? [])
            PrintType(sb, type);
        return sb.ToString();
    }

    private static void PrintType(StringBuilder sb, TypeDefinition type)
    {
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
}
