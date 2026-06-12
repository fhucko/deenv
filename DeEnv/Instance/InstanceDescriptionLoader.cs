using System.Text.Json;
using DeEnv.Code;

namespace DeEnv.Instance;

public static class InstanceDescriptionLoader
{
    // `codeText` is the content of the schema's sidecar code file (see CodeFile) —
    // the only way code enters a description. Inline ui/common JSON is rejected:
    // text is the authoring surface; the AST exists in memory and on the wire only.
    public static InstanceDescription Load(string json, string? codeText = null)
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

        if (desc.Ui != null || desc.Common != null)
            throw new SchemaValidationException(
                "Inline 'ui'/'common' JSON is not supported: author code as text in the schema's codeFile.");
        if (desc.CodeFile != null && codeText == null)
            throw new SchemaValidationException(
                $"The schema references code file '{desc.CodeFile}': load it via LoadFile, or pass the code text.");

        if (codeText != null)
        {
            try
            {
                var (common, ui) = CodeParse.ParseDocument(codeText);
                desc = desc with { Ui = ui, Common = common };
            }
            catch (DeEnv.Code.Parsing.CodeParseException ex)
            {
                throw new SchemaValidationException($"Code file: {ex.Message}");
            }
        }

        Validate(desc);
        ValidateInitialData(desc);
        CodeValidator.Validate(desc);
        CodeIds.Assign(desc); // number every CodeFunction for stable memo-cache keys
        return desc;
    }

    public static InstanceDescription LoadFile(string path)
    {
        var json = File.ReadAllText(path);
        // Peek the codeFile reference and read the sidecar relative to the schema.
        string? codeText = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("codeFile", out var cf)
                && cf.GetString() is { } codeFile)
                codeText = File.ReadAllText(
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, codeFile));
        }
        catch (JsonException)
        {
            // Malformed JSON: fall through — Load reports it with the standard error.
        }
        return Load(json, codeText);
    }

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

    // initialData structure: known types, declared fields, exactly one Db entry,
    // globally-unique positive ids, and set members / single refs pointing at
    // existing entries of the right type.
    private static void ValidateInitialData(InstanceDescription desc)
    {
        if (desc.InitialData?.Extents is not { } seed) return;

        var ids = new HashSet<int>();
        foreach (var (typeName, pool) in seed)
        {
            if (desc.FindType(typeName) is null)
                throw new SchemaValidationException($"initialData references unknown type '{typeName}'.");
            foreach (var idText in pool.Keys)
            {
                if (!int.TryParse(idText, out var id) || id <= 0)
                    throw new SchemaValidationException(
                        $"initialData id '{idText}' in '{typeName}' is not a positive integer.");
                if (!ids.Add(id))
                    throw new SchemaValidationException(
                        $"initialData id {id} is used more than once (ids are global).");
            }
        }

        if (!seed.TryGetValue("Db", out var dbPool) || dbPool.Count != 1)
            throw new SchemaValidationException("initialData must contain exactly one 'Db' entry (the root).");

        foreach (var (typeName, pool) in seed)
        {
            var type = desc.FindType(typeName)!;
            foreach (var (idText, fields) in pool)
            {
                if (fields.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new SchemaValidationException(
                        $"initialData entry '{typeName}/{idText}' must be an object of fields.");
                foreach (var field in fields.EnumerateObject())
                {
                    var prop = type.Props?.FirstOrDefault(p => p.Name == field.Name)
                        ?? throw new SchemaValidationException(
                            $"initialData entry '{typeName}/{idText}' has unknown field '{field.Name}'.");

                    if (prop.Cardinality == Cardinality.Set)
                        foreach (var m in field.Value.EnumerateArray())
                            RequireRef(seed, prop.Type, m.GetInt32(), $"{typeName}/{idText}.{field.Name}");
                    else if (prop.Cardinality == Cardinality.Single && desc.IsObjectType(prop.Type))
                        RequireRef(seed, prop.Type, field.Value.GetInt32(), $"{typeName}/{idText}.{field.Name}");
                }
            }
        }
    }

    private static void RequireRef(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>> seed,
        string typeName, int id, string where)
    {
        if (!seed.TryGetValue(typeName, out var pool) || !pool.ContainsKey(id.ToString()))
            throw new SchemaValidationException(
                $"initialData '{where}' references id {id}, but '{typeName}' has no entry with that id.");
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
