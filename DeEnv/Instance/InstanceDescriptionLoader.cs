using System.Text.Json;

namespace DeEnv.Instance;

public static class InstanceDescriptionLoader
{
    // Valid prop / keyType base names (no 'object' — that is only a type's baseType).
    private static readonly HashSet<string> BaseTypeNames =
        ["bool", "int", "decimal", "text", "date", "datetime"];

    // Valid values for a type definition's baseType (the leaf base types plus 'object').
    private static readonly HashSet<string> KnownBaseTypes =
        ["bool", "int", "decimal", "text", "date", "datetime", "object"];

    public static InstanceDescription Load(string json)
    {
        InstanceDescription? desc;
        try
        {
            desc = JsonSerializer.Deserialize<InstanceDescription>(json);
        }
        catch (JsonException ex)
        {
            throw new SchemaValidationException($"Schema document is not valid JSON: {ex.Message}");
        }

        if (desc == null)
            throw new SchemaValidationException("Schema document is not valid JSON: deserialized to null.");

        Validate(desc);
        return desc;
    }

    public static InstanceDescription LoadFile(string path) =>
        Load(File.ReadAllText(path));

    private static void Validate(InstanceDescription desc)
    {
        // Type names must be unique. This single rule subsumes "more than one Db":
        // a duplicate Db is just a duplicate name.
        var seen = new HashSet<string>();
        foreach (var name in desc.AllTypes.Select(t => t.Name))
        {
            if (!seen.Add(name))
                throw new SchemaValidationException(
                    $"Duplicate type name '{name}': every type must have a unique name.");
        }

        // The root type 'Db' must exist (uniqueness already forbids a second one).
        if (!seen.Contains("Db"))
            throw new SchemaValidationException(
                "Schema document must define exactly one root type named 'Db'.");

        foreach (var type in desc.AllTypes)
        {
            if (!KnownBaseTypes.Contains(type.BaseTypeRaw))
                throw new SchemaValidationException(
                    $"Type '{type.Name}' has unknown baseType '{type.BaseTypeRaw}'. " +
                    $"Valid base types are: {string.Join(", ", KnownBaseTypes)}.");

            if (type.BaseType == BaseType.Object)
            {
                if (type.Props == null || type.Props.Count == 0)
                    throw new SchemaValidationException(
                        $"Type '{type.Name}' has baseType 'object' but no props.");

                var propNames = new HashSet<string>();
                foreach (var prop in type.Props)
                {
                    if (!propNames.Add(prop.Name))
                        throw new SchemaValidationException(
                            $"Type '{type.Name}' has duplicate prop name '{prop.Name}'.");

                    ValidateProp(prop, type.Name, seen);
                }
            }
            else if (type.Props != null)
            {
                throw new SchemaValidationException(
                    $"Type '{type.Name}' has props but baseType is not 'object'.");
            }
        }
    }

    private static void ValidateProp(PropDefinition prop, string typeName, HashSet<string> typeNames)
    {
        if (!BaseTypeNames.Contains(prop.TypeName) && !typeNames.Contains(prop.TypeName))
            throw new SchemaValidationException(
                $"Prop '{prop.Name}' on type '{typeName}' references unknown type '{prop.TypeName}'.");

        if (prop.Cardinality == Cardinality.Dictionary
            && prop.KeyTypeName != null
            && !BaseTypeNames.Contains(prop.KeyTypeName))
            throw new SchemaValidationException(
                $"Prop '{prop.Name}' on type '{typeName}' has unknown keyType '{prop.KeyTypeName}'.");

        if (prop.CardinalityRaw != null
            && prop.CardinalityRaw != "single"
            && prop.CardinalityRaw != "dictionary"
            && prop.CardinalityRaw != "set")
            throw new SchemaValidationException(
                $"Prop '{prop.Name}' on type '{typeName}' has invalid cardinality '{prop.CardinalityRaw}'.");

        // A set is a collection of object references keyed by member identity, so
        // its element type must be an object type and it carries no key fields.
        if (prop.Cardinality == Cardinality.Set)
        {
            if (BaseTypeNames.Contains(prop.TypeName))
                throw new SchemaValidationException(
                    $"Prop '{prop.Name}' on type '{typeName}' is a set of '{prop.TypeName}', " +
                    $"but a set's element type must be an object type.");
            if (prop.KeyTypeName != null)
                throw new SchemaValidationException(
                    $"Prop '{prop.Name}' on type '{typeName}' is a set and cannot declare keyType " +
                    $"(set members are keyed by their own identity).");
        }
    }
}
