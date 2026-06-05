using System.Text.Json;

namespace DeEnv.Instance;

public static class InstanceDescriptionLoader
{
    private static readonly HashSet<string> BaseTypeNames =
        ["bool", "int", "decimal", "text", "date", "datetime"];

    public static InstanceDescription Load(string json)
    {
        var desc = JsonSerializer.Deserialize<InstanceDescription>(json)
            ?? throw new InvalidOperationException("Failed to deserialize instance description.");

        Validate(desc);
        return desc;
    }

    public static InstanceDescription LoadFile(string path) =>
        Load(File.ReadAllText(path));

    private static void Validate(InstanceDescription desc)
    {
        var typeNames = new HashSet<string>(desc.AllTypes.Select(t => t.Name));

        foreach (var type in desc.AllTypes)
        {
            if (type.BaseType == BaseType.Object)
            {
                if (type.Props == null || type.Props.Count == 0)
                    throw new InvalidOperationException(
                        $"Type '{type.Name}' has baseType 'object' but no props.");

                foreach (var prop in type.Props)
                    ValidateProp(prop, type.Name, typeNames);
            }
            else if (type.Props != null)
            {
                throw new InvalidOperationException(
                    $"Type '{type.Name}' has props but baseType is not 'object'.");
            }
        }
    }

    private static void ValidateProp(PropDefinition prop, string typeName, HashSet<string> typeNames)
    {
        if (!BaseTypeNames.Contains(prop.TypeName) && !typeNames.Contains(prop.TypeName))
            throw new InvalidOperationException(
                $"Prop '{prop.Name}' on type '{typeName}' references unknown type '{prop.TypeName}'.");

        if (prop.Cardinality == Cardinality.Dictionary
            && prop.KeyTypeName != null
            && !BaseTypeNames.Contains(prop.KeyTypeName))
            throw new InvalidOperationException(
                $"Prop '{prop.Name}' on type '{typeName}' has unknown keyType '{prop.KeyTypeName}'.");

        if (prop.CardinalityRaw != null
            && prop.CardinalityRaw != "single"
            && prop.CardinalityRaw != "dictionary")
            throw new InvalidOperationException(
                $"Prop '{prop.Name}' on type '{typeName}' has invalid cardinality '{prop.CardinalityRaw}'.");
    }
}
