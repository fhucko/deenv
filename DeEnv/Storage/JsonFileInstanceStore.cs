using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Instance;

namespace DeEnv.Storage;

public sealed class JsonFileInstanceStore : IInstanceStore
{
    private readonly string _filePath;
    private readonly InstanceDescription _desc;
    private readonly TypeResolver _resolver;
    private readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = true };

    public JsonFileInstanceStore(string filePath, InstanceDescription desc)
    {
        _filePath = filePath;
        _desc = desc;
        _resolver = new TypeResolver(desc);

        // Initialize the file if it doesn't exist OR if it's empty
        // (Path.GetTempFileName creates a 0-byte file that must be treated as uninitialized).
        if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            File.WriteAllText(filePath, InitialJson());
    }

    // ── read ──────────────────────────────────────────────────────────────────

    public NodeValue? ReadNode(NodePath path)
    {
        var rootType = _desc.Db;
        if (rootType == null) return null;

        var root = LoadRoot();
        if (root == null) return null;

        // Walk the type tree and the JSON together. This distinguishes a missing
        // *field* (schema-valid but unmaterialized → return its default, e.g. an
        // empty dictionary) from a missing dictionary *key* (entry absent → null).
        JsonNode? curJson = root;
        var curType = rootType;
        var curCardinality = Cardinality.Single;
        string? curKeyType = null;

        foreach (var seg in path.Segments)
        {
            if (curCardinality == Cardinality.Dictionary)
            {
                // Segment is a dictionary key.
                if (curJson is JsonObject dictObj && dictObj.TryGetPropertyValue(seg, out var entry) && entry != null)
                    curJson = entry;
                else
                    return null; // missing key → not found
                curCardinality = Cardinality.Single;
                curKeyType = null;
            }
            else if (curType.BaseType == BaseType.Object)
            {
                // Segment is a field name.
                var prop = curType.Props?.FirstOrDefault(p => p.Name == seg);
                if (prop == null) return null; // unknown field

                curType = ResolveTypeDef(prop.TypeName);
                curCardinality = prop.Cardinality;
                curKeyType = prop.Cardinality == Cardinality.Dictionary ? prop.EffectiveKeyType : null;

                // Descend the JSON; a missing field leaves curJson null (defaulted below).
                curJson = curJson is JsonObject obj && obj.TryGetPropertyValue(seg, out var child)
                    ? child : null;
            }
            else
            {
                return null; // can't navigate into a leaf
            }
        }

        if (curJson != null)
            return ToNodeValue(curJson, curType, curCardinality, curKeyType);

        // Schema-valid but absent in storage → default value for the resolved type.
        return curCardinality == Cardinality.Dictionary
            ? new DictionaryValue(new Dictionary<NodeValue, NodeValue>())
            : BuildDefault(curType);
    }

    // ── write leaf ────────────────────────────────────────────────────────────

    public void WriteLeaf(NodePath path, NodeValue value)
    {
        if (path.IsRoot)
        {
            SaveRoot(ToJsonNode(value));
            return;
        }

        var root = LoadRoot() ?? NewJsonObject();
        EnsureParent(root, path);
        var parent = Navigate(root, ParentPath(path)) as JsonObject
            ?? throw new InvalidOperationException("Parent path is not a JSON object.");
        parent[path.Segments[^1]] = ToJsonNode(value);
        SaveRoot(root);
    }

    // ── write object ──────────────────────────────────────────────────────────

    public void WriteObject(NodePath path, ObjectValue value)
    {
        var root = LoadRoot() ?? NewJsonObject();

        JsonObject target;
        if (path.IsRoot)
        {
            target = root as JsonObject
                ?? throw new InvalidOperationException("Root is not a JSON object.");
        }
        else
        {
            EnsureParent(root, path);
            var parent = Navigate(root, ParentPath(path)) as JsonObject
                ?? throw new InvalidOperationException($"Parent of {path} is not a JSON object.");
            var last = path.Segments[^1];
            if (parent[last] is not JsonObject existing)
            {
                existing = new JsonObject();
                parent[last] = existing;
            }
            target = existing;
        }

        // Write only the supplied fields (the form's leaf fields); dictionary
        // fields are not in `value.Fields`, so they are left untouched.
        foreach (var (field, v) in value.Fields)
            target[field] = ToJsonNode(v);

        SaveRoot(root);
    }

    // ── dictionary ────────────────────────────────────────────────────────────

    public NodeValue NewEntryTemplate(NodePath dictPath)
    {
        var typeInfo = _resolver.ResolveType(dictPath)
            ?? throw new InvalidOperationException($"Path {dictPath} does not resolve.");
        if (typeInfo.Cardinality != Cardinality.Dictionary)
            throw new InvalidOperationException($"{dictPath} is not a dictionary.");
        return BuildDefault(typeInfo.Type);
    }

    public NodeValue CreateEntry(NodePath dictPath, NodeValue value)
    {
        var key = NextKey(dictPath);
        WriteDictionaryEntry(dictPath, key, value);
        return key;
    }

    public void CreateEntry(NodePath dictPath, NodeValue key, NodeValue value)
    {
        if (EntryExists(dictPath, key))
            throw new InvalidOperationException(
                $"An entry with key '{KeyToString(key)}' already exists at {dictPath}.");
        WriteDictionaryEntry(dictPath, key, value);
    }

    private bool EntryExists(NodePath dictPath, NodeValue key)
    {
        var root = LoadRoot();
        if (root == null) return false;
        return Navigate(root, dictPath) is JsonObject dict && dict.ContainsKey(KeyToString(key));
    }

    // Default value of a type, with object fields recursively defaulted and
    // dictionary fields starting empty. Used for the create-entry template.
    private NodeValue BuildDefault(TypeDefinition type)
    {
        if (type.BaseType != BaseType.Object)
            return DefaultBase(type.BaseType);

        var fields = new Dictionary<string, NodeValue>();
        foreach (var prop in type.Props ?? [])
        {
            fields[prop.Name] = prop.Cardinality == Cardinality.Dictionary
                ? new DictionaryValue(new Dictionary<NodeValue, NodeValue>())
                : BuildDefault(ResolveTypeDef(prop.TypeName));
        }
        return new ObjectValue(fields);
    }

    private static NodeValue DefaultBase(BaseType bt) => bt switch
    {
        BaseType.Bool     => new BoolValue(false),
        BaseType.Int      => new IntValue(0),
        BaseType.Decimal  => new DecimalValue(0m),
        BaseType.Text     => new TextValue(""),
        BaseType.Date     => new DateValue(DateOnly.FromDateTime(DateTime.Today)),
        BaseType.DateTime => new DateTimeValue(DateTimeOffset.Now),
        _ => throw new InvalidOperationException($"No base default for {bt}")
    };

    public void WriteDictionaryEntry(NodePath path, NodeValue key, NodeValue value)
    {
        var root = LoadRoot() ?? NewJsonObject();
        EnsureParent(root, path.Field(KeyToString(key)));
        var dictNode = Navigate(root, path) as JsonObject
            ?? throw new InvalidOperationException($"No dictionary object at {path}.");
        dictNode[KeyToString(key)] = ToJsonNode(value);
        SaveRoot(root);
    }

    public void RemoveDictionaryEntry(NodePath path, NodeValue key)
    {
        var root = LoadRoot();
        if (root == null) return;
        if (Navigate(root, path) is not JsonObject dictNode) return;
        dictNode.Remove(KeyToString(key));
        SaveRoot(root);
    }

    public NodeValue NextKey(NodePath path)
    {
        var typeInfo = _resolver.ResolveType(path)
            ?? throw new InvalidOperationException($"Path {path} does not resolve.");
        var keyTypeName = typeInfo.KeyTypeName ?? "text";

        if (keyTypeName != "int")
            throw new NotSupportedException($"NextKey is only supported for int key types, not '{keyTypeName}'.");

        var root = LoadRoot();
        if (root == null || Navigate(root, path) is not JsonObject dictNode || dictNode.Count == 0)
            return new IntValue(1);

        var max = dictNode.Select(kv => int.TryParse(kv.Key, out var n) ? n : 0).Max();
        return new IntValue(max + 1);
    }

    // ── JSON navigation ───────────────────────────────────────────────────────

    private static JsonNode? Navigate(JsonNode root, NodePath path)
    {
        JsonNode? current = root;
        foreach (var seg in path.Segments)
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(seg, out var child))
                current = child;
            else
                return null;
        }
        return current;
    }

    private static NodePath ParentPath(NodePath path) =>
        NodePath.FromSegments(path.Segments.Take(path.Segments.Count - 1));

    // Creates missing intermediate JSON objects along the path (not including the last segment).
    private static void EnsureParent(JsonNode root, NodePath path)
    {
        JsonNode? current = root;
        var segs = path.Segments.Take(path.Segments.Count - 1);
        foreach (var seg in segs)
        {
            if (current is not JsonObject obj) return;
            if (!obj.TryGetPropertyValue(seg, out var child) || child is null)
            {
                obj[seg] = new JsonObject();
                child = obj[seg];
            }
            current = child;
        }
    }

    // ── NodeValue ↔ JsonNode ──────────────────────────────────────────────────

    private NodeValue ToNodeValue(JsonNode node, TypeDefinition type, Cardinality cardinality, string? keyTypeName)
    {
        if (cardinality == Cardinality.Dictionary)
        {
            if (node is not JsonObject dictObj)
                throw new InvalidOperationException("Expected JSON object for dictionary.");

            var entries = new Dictionary<NodeValue, NodeValue>();
            foreach (var (k, v) in dictObj)
            {
                if (v is null) continue;
                var entryTypeInfo = new ResolvedTypeInfo(type, Cardinality.Single, null);
                entries[ParseKey(k, keyTypeName ?? "text")] =
                    ToNodeValue(v, type, Cardinality.Single, null);
            }
            return new DictionaryValue(entries);
        }

        return type.BaseType switch
        {
            BaseType.Bool     => new BoolValue(node.GetValue<bool>()),
            BaseType.Int      => new IntValue(node.GetValue<int>()),
            BaseType.Decimal  => new DecimalValue(node.GetValue<decimal>()),
            BaseType.Text     => new TextValue(node.GetValue<string>()),
            BaseType.Date     => new DateValue(DateOnly.Parse(node.GetValue<string>())),
            BaseType.DateTime => new DateTimeValue(DateTimeOffset.Parse(node.GetValue<string>())),
            BaseType.Object   => ToObjectValue(node as JsonObject
                                    ?? throw new InvalidOperationException("Expected JSON object."), type),
            _ => throw new InvalidOperationException($"Unknown base type: {type.BaseType}")
        };
    }

    private ObjectValue ToObjectValue(JsonObject obj, TypeDefinition type)
    {
        var fields = new Dictionary<string, NodeValue>();
        foreach (var prop in type.Props ?? [])
        {
            if (!obj.TryGetPropertyValue(prop.Name, out var fieldNode) || fieldNode is null)
            {
                if (!prop.Nullable)
                    fields[prop.Name] = DefaultValue(prop.TypeName, prop.Cardinality, prop.KeyTypeName);
                continue;
            }

            var propType = ResolveTypeDef(prop.TypeName)!;
            fields[prop.Name] = ToNodeValue(fieldNode, propType, prop.Cardinality, prop.KeyTypeName);
        }
        return new ObjectValue(fields);
    }

    private TypeDefinition ResolveTypeDef(string name)
    {
        if (name is "bool" or "int" or "decimal" or "text" or "date" or "datetime")
            return new TypeDefinition(name, name);
        return _desc.FindType(name)
            ?? throw new InvalidOperationException($"Unknown type '{name}'.");
    }

    private static JsonNode ToJsonNode(NodeValue value) => value switch
    {
        BoolValue b      => JsonValue.Create(b.Value)!,
        IntValue i       => JsonValue.Create(i.Value)!,
        DecimalValue d   => JsonValue.Create(d.Value)!,
        TextValue t      => JsonValue.Create(t.Text)!,
        DateValue d      => JsonValue.Create(d.Value.ToString("yyyy-MM-dd"))!,
        DateTimeValue dt => JsonValue.Create(dt.Value.ToString("O"))!,
        ObjectValue o    => ObjectToJson(o),
        DictionaryValue dv => DictToJson(dv),
        _ => throw new InvalidOperationException($"Cannot convert {value.GetType().Name} to JsonNode.")
    };

    private static JsonObject ObjectToJson(ObjectValue obj)
    {
        var result = new JsonObject();
        foreach (var (k, v) in obj.Fields)
            result[k] = ToJsonNode(v);
        return result;
    }

    private static JsonObject DictToJson(DictionaryValue dict)
    {
        var result = new JsonObject();
        foreach (var (k, v) in dict.Entries)
            result[KeyToString(k)] = ToJsonNode(v);
        return result;
    }

    private static NodeValue ParseKey(string key, string keyTypeName) => keyTypeName switch
    {
        "int"     => new IntValue(int.Parse(key)),
        "decimal" => new DecimalValue(decimal.Parse(key)),
        "bool"    => new BoolValue(bool.Parse(key)),
        "date"    => new DateValue(DateOnly.Parse(key)),
        "datetime"=> new DateTimeValue(DateTimeOffset.Parse(key)),
        _         => new TextValue(key)
    };

    private static string KeyToString(NodeValue key) => key switch
    {
        BoolValue b      => b.Value.ToString().ToLowerInvariant(),
        IntValue i       => i.Value.ToString(),
        DecimalValue d   => d.Value.ToString(),
        TextValue t      => t.Text,
        DateValue d      => d.Value.ToString("yyyy-MM-dd"),
        DateTimeValue dt => dt.Value.ToString("O"),
        _ => throw new InvalidOperationException($"Cannot use {key.GetType().Name} as a dictionary key.")
    };

    private static NodeValue DefaultValue(string typeName, Cardinality cardinality, string? keyTypeName)
    {
        if (cardinality == Cardinality.Dictionary)
            return new DictionaryValue(new Dictionary<NodeValue, NodeValue>());

        return typeName switch
        {
            "bool"     => new BoolValue(false),
            "int"      => new IntValue(0),
            "decimal"  => new DecimalValue(0m),
            "text"     => new TextValue(""),
            "date"     => new DateValue(DateOnly.MinValue),
            "datetime" => new DateTimeValue(DateTimeOffset.MinValue),
            _ => new ObjectValue(new Dictionary<string, NodeValue>())
        };
    }

    // ── file I/O ──────────────────────────────────────────────────────────────

    private JsonNode? LoadRoot()
    {
        if (!File.Exists(_filePath)) return null;
        var text = File.ReadAllText(_filePath);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private void SaveRoot(JsonNode node) =>
        File.WriteAllText(_filePath, node.ToJsonString(_writeOpts));

    private string InitialJson()
    {
        var db = _desc.Db;
        if (db == null) return "null";
        return db.BaseType == BaseType.Bool ? "false" : "{}";
    }

    private static JsonObject NewJsonObject() => new();
}
