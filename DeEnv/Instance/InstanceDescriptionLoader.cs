using System.Text.Json;
using DeEnv.Code;

namespace DeEnv.Instance;

public static class InstanceDescriptionLoader
{
    public static InstanceDescription Load(string json)
    {
        InstanceDescription? desc;
        try
        {
            desc = JsonSerializer.Deserialize<InstanceDescription>(json, SchemaJson.Options);
        }
        catch (JsonException ex)
        {
            throw new SchemaValidationException($"Schema document is not valid JSON: {ex.Message}");
        }

        if (desc == null)
            throw new SchemaValidationException("Schema document is not valid JSON: deserialized to null.");

        Validate(desc);
        CodeValidator.Validate(desc);
        return desc;
    }

    public static InstanceDescription LoadFile(string path) =>
        Load(File.ReadAllText(path));

    private static void Validate(InstanceDescription desc)
    {
        // Type names must be unique. This single rule subsumes "more than one Db":
        // a duplicate Db is just a duplicate name.
        var seen = new HashSet<string>();
        foreach (var name in desc.AllTypes().Select(t => t.Name))
        {
            if (!seen.Add(name))
                throw new SchemaValidationException(
                    $"Duplicate type name '{name}': every type must have a unique name.");
        }

        // The root type 'Db' must exist (uniqueness already forbids a second one).
        if (!seen.Contains("Db"))
            throw new SchemaValidationException(
                "Schema document must define exactly one root type named 'Db'.");

        // An invalid baseType / cardinality is rejected by the enum converter while
        // parsing (a JSON error), so it never reaches here — this validates structure.
        foreach (var type in desc.AllTypes())
        {
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
        if (!BaseTypes.IsName(prop.Type) && !typeNames.Contains(prop.Type))
            throw new SchemaValidationException(
                $"Prop '{prop.Name}' on type '{typeName}' references unknown type '{prop.Type}'.");

        if (prop.Cardinality == Cardinality.Dictionary
            && prop.KeyType != null
            && !BaseTypes.IsName(prop.KeyType))
            throw new SchemaValidationException(
                $"Prop '{prop.Name}' on type '{typeName}' has unknown keyType '{prop.KeyType}'.");

        // A set is a collection of object references keyed by member identity, so
        // its element type must be an object type and it carries no key fields.
        if (prop.Cardinality == Cardinality.Set)
        {
            if (BaseTypes.IsName(prop.Type))
                throw new SchemaValidationException(
                    $"Prop '{prop.Name}' on type '{typeName}' is a set of '{prop.Type}', " +
                    $"but a set's element type must be an object type.");
            if (prop.KeyType != null)
                throw new SchemaValidationException(
                    $"Prop '{prop.Name}' on type '{typeName}' is a set and cannot declare keyType " +
                    $"(set members are keyed by their own identity).");
        }
    }
}
