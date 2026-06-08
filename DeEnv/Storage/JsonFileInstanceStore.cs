using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Instance;

namespace DeEnv.Storage;

public sealed class JsonFileInstanceStore : IInstanceStore
{
    private readonly string _filePath;
    private readonly InstanceDescription _desc;
    private readonly TypeResolver _resolver;
    private readonly bool _usesExtents;
    private readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = true };

    public JsonFileInstanceStore(string filePath, InstanceDescription desc)
    {
        _filePath = filePath;
        _desc = desc;
        _resolver = new TypeResolver(desc);
        _usesExtents = desc.UsesExtents;

        // Initialize the file if it doesn't exist OR if it's empty
        // (Path.GetTempFileName creates a 0-byte file that must be treated as uninitialized).
        if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            File.WriteAllText(filePath, InitialJson());
    }

    // ── read ──────────────────────────────────────────────────────────────────

    public NodeValue? ReadNode(NodePath path)
    {
        if (_usesExtents) return ReadNodeExtent(path);

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
        if (_usesExtents) { WriteLeafExtent(path, value); return; }

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
        if (_usesExtents) { WriteObjectExtent(path, value); return; }

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
        if (typeInfo.Cardinality != Cardinality.Dictionary && typeInfo.Cardinality != Cardinality.Set)
            throw new InvalidOperationException($"{dictPath} is not a dictionary or set.");
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

    // ── object model (extent mode) ──────────────────────────────────────────────
    //
    // Storage shape: { "extents": { "<Type>": { "<id>": { "id": id, "fields": {…} } } },
    //                  "root": { "id": 1, "fields": {…} } }
    // A set field holds { "<id>": { "ref": id } }; a single object-typed prop holds
    // { "ref": id } (or null). Reads resolve references; lifetime is mark-sweep GC.

    private JsonObject LoadDoc()
    {
        var text = File.Exists(_filePath) ? File.ReadAllText(_filePath) : "";
        var doc = (string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text)) as JsonObject
                  ?? new JsonObject();
        if (doc["extents"] is not JsonObject) doc["extents"] = new JsonObject();
        if (doc["root"] is not JsonObject root)
            doc["root"] = new JsonObject { ["id"] = 1, ["fields"] = new JsonObject() };
        else if (root["fields"] is not JsonObject)
            root["fields"] = new JsonObject();
        return doc;
    }

    private void SaveDoc(JsonObject doc) => File.WriteAllText(_filePath, doc.ToJsonString(_writeOpts));

    private static JsonObject Extents(JsonObject doc) => (JsonObject)doc["extents"]!;
    private static JsonObject RootFields(JsonObject doc) => (JsonObject)((JsonObject)doc["root"]!)["fields"]!;

    private static JsonObject? ExtentEnvelope(JsonObject doc, string typeName, int id) =>
        Extents(doc)[typeName] is JsonObject pool && pool[id.ToString()] is JsonObject env ? env : null;

    private static JsonObject? ExtentEnvelopeAnyType(JsonObject doc, int id)
    {
        foreach (var (_, pool) in Extents(doc))
            if (pool is JsonObject p && p[id.ToString()] is JsonObject env)
                return env;
        return null;
    }

    private static JsonObject EnvFields(JsonObject env) => (JsonObject)env["fields"]!;

    private bool IsReferenceProp(PropDefinition prop) =>
        prop.Cardinality == Cardinality.Single && _desc.IsObjectType(prop.TypeName);

    // ── read ──

    private NodeValue? ReadNodeExtent(NodePath path)
    {
        if (path.Segments.Count > 0 && path.Segments[0] == "~") return null; // id-route handled by renderer

        var doc = LoadDoc();
        var curFields = RootFields(doc);
        var curType = _desc.Db;
        if (curType == null) return null;

        var segs = path.Segments;
        for (var i = 0; i < segs.Count; i++)
        {
            var prop = curType.Props?.FirstOrDefault(p => p.Name == segs[i]);
            if (prop == null) return null;
            var elemType = ResolveTypeDef(prop.TypeName);
            var last = i == segs.Count - 1;

            if (prop.Cardinality == Cardinality.Set)
            {
                var setNode = curFields[prop.Name] as JsonObject;
                if (last) return BuildSetValue(doc, setNode, prop.TypeName, elemType);

                var memberId = segs[i + 1];
                var mid = (setNode?[memberId] as JsonObject)?["ref"]?.GetValue<int>();
                if (mid is null) return null;
                var env = ExtentEnvelope(doc, prop.TypeName, mid.Value);
                if (env == null) return null;
                if (i + 1 == segs.Count - 1) return BuildResolvedObject(doc, EnvFields(env), elemType);
                curFields = EnvFields(env); curType = elemType; i++; continue;
            }

            if (IsReferenceProp(prop))
            {
                var id = (curFields[prop.Name] as JsonObject)?["ref"]?.GetValue<int>();
                if (id is null) return last ? new ReferenceValue(null, prop.TypeName) : null;
                var env = ExtentEnvelope(doc, prop.TypeName, id.Value);
                if (env == null) return last ? new ReferenceValue(null, prop.TypeName) : null;
                if (last) return BuildResolvedObject(doc, EnvFields(env), elemType);
                curFields = EnvFields(env); curType = elemType; continue;
            }

            // scalar single
            if (!last) return null;
            var v = curFields[prop.Name];
            return v != null ? LeafFromJson(v, elemType.BaseType) : DefaultBase(elemType.BaseType);
        }

        return BuildResolvedObject(doc, curFields, curType);
    }

    private ObjectValue BuildResolvedObject(JsonObject doc, JsonObject fields, TypeDefinition type)
    {
        var map = new Dictionary<string, NodeValue>();
        foreach (var prop in type.Props ?? [])
        {
            var elemType = ResolveTypeDef(prop.TypeName);
            if (prop.Cardinality == Cardinality.Set)
                map[prop.Name] = BuildSetValue(doc, fields[prop.Name] as JsonObject, prop.TypeName, elemType);
            else if (IsReferenceProp(prop))
                map[prop.Name] = new ReferenceValue((fields[prop.Name] as JsonObject)?["ref"]?.GetValue<int>(), prop.TypeName);
            else if (prop.Cardinality == Cardinality.Single)
            {
                var v = fields[prop.Name];
                map[prop.Name] = v != null ? LeafFromJson(v, elemType.BaseType) : DefaultBase(elemType.BaseType);
            }
            // dictionaries do not occur in extent-mode schemas (this slice)
        }
        return new ObjectValue(map);
    }

    private SetValue BuildSetValue(JsonObject doc, JsonObject? setNode, string typeName, TypeDefinition elemType)
    {
        var members = new Dictionary<int, NodeValue>();
        if (setNode != null)
            foreach (var (k, v) in setNode)
                if ((v as JsonObject)?["ref"]?.GetValue<int>() is int id
                    && ExtentEnvelope(doc, typeName, id) is { } env)
                    members[int.Parse(k)] = BuildResolvedObject(doc, EnvFields(env), elemType);
        return new SetValue(members);
    }

    private static NodeValue LeafFromJson(JsonNode node, BaseType bt) => bt switch
    {
        BaseType.Bool     => new BoolValue(node.GetValue<bool>()),
        BaseType.Int      => new IntValue(node.GetValue<int>()),
        BaseType.Decimal  => new DecimalValue(node.GetValue<decimal>()),
        BaseType.Text     => new TextValue(node.GetValue<string>()),
        BaseType.Date     => new DateValue(DateOnly.Parse(node.GetValue<string>())),
        BaseType.DateTime => new DateTimeValue(DateTimeOffset.Parse(node.GetValue<string>())),
        _ => throw new InvalidOperationException($"No leaf for base type {bt}")
    };

    // ── walk to an object's fields (following references), for writes ──

    private (JsonObject Fields, TypeDefinition Type)? WalkToObject(JsonObject doc, NodePath path)
    {
        var curFields = RootFields(doc);
        var curType = _desc.Db;
        if (curType == null) return null;

        var segs = path.Segments;
        for (var i = 0; i < segs.Count; i++)
        {
            var prop = curType.Props?.FirstOrDefault(p => p.Name == segs[i]);
            if (prop == null) return null;
            var elemType = ResolveTypeDef(prop.TypeName);

            if (prop.Cardinality == Cardinality.Set)
            {
                if (i + 1 >= segs.Count) return null; // the set itself is not an object
                var setNode = curFields[prop.Name] as JsonObject;
                var mid = (setNode?[segs[i + 1]] as JsonObject)?["ref"]?.GetValue<int>();
                if (mid is null) return null;
                var env = ExtentEnvelope(doc, prop.TypeName, mid.Value);
                if (env == null) return null;
                curFields = EnvFields(env); curType = elemType; i++; continue;
            }

            if (IsReferenceProp(prop))
            {
                var id = (curFields[prop.Name] as JsonObject)?["ref"]?.GetValue<int>();
                if (id is null) return null;
                var env = ExtentEnvelope(doc, prop.TypeName, id.Value);
                if (env == null) return null;
                curFields = EnvFields(env); curType = elemType; continue;
            }

            return null; // a scalar is not an object
        }
        return (curFields, curType);
    }

    private void WriteObjectExtent(NodePath path, ObjectValue value)
    {
        var doc = LoadDoc();
        var target = WalkToObject(doc, path);
        if (target == null) throw new InvalidOperationException($"Path {path} is not a writable object.");

        foreach (var prop in target.Value.Type.Props ?? [])
            if (prop.Cardinality == Cardinality.Single && !_desc.IsObjectType(prop.TypeName)
                && value.Fields.TryGetValue(prop.Name, out var v))
                target.Value.Fields[prop.Name] = ToJsonNode(v);

        SaveDoc(doc);
    }

    private void WriteLeafExtent(NodePath path, NodeValue value)
    {
        if (path.IsRoot) throw new InvalidOperationException("Cannot write a leaf at the root in extent mode.");
        var doc = LoadDoc();
        var parent = WalkToObject(doc, ParentPath(path))
            ?? throw new InvalidOperationException($"Parent of {path} is not an object.");
        parent.Fields[path.Segments[^1]] = ToJsonNode(value);
        SaveDoc(doc);
    }

    // ── object-graph mutations ──

    public int CreateObject(string typeName, ObjectValue fields)
    {
        RequireExtents();
        var doc = LoadDoc();
        if (Extents(doc)[typeName] is not JsonObject pool)
        {
            pool = new JsonObject();
            Extents(doc)[typeName] = pool;
        }

        var id = NextId(doc);
        var type = _desc.FindType(typeName)
            ?? throw new InvalidOperationException($"Unknown type '{typeName}'.");
        var fjson = new JsonObject();
        foreach (var prop in type.Props ?? [])
        {
            if (prop.Cardinality == Cardinality.Set)
                fjson[prop.Name] = new JsonObject();
            else if (prop.Cardinality == Cardinality.Single && !_desc.IsObjectType(prop.TypeName)
                     && fields.Fields.TryGetValue(prop.Name, out var v))
                fjson[prop.Name] = ToJsonNode(v);
        }

        pool[id.ToString()] = new JsonObject { ["id"] = id, ["fields"] = fjson };
        SaveDoc(doc);
        return id;
    }

    public void AddToSet(NodePath setPath, int id)
    {
        RequireExtents();
        var doc = LoadDoc();
        var parent = WalkToObject(doc, ParentPath(setPath))
            ?? throw new InvalidOperationException($"Parent of {setPath} is not an object.");
        var field = setPath.Segments[^1];
        if (parent.Fields[field] is not JsonObject set)
        {
            set = new JsonObject();
            parent.Fields[field] = set;
        }
        set[id.ToString()] = new JsonObject { ["ref"] = id };
        SaveDoc(doc);
    }

    public void RemoveFromSet(NodePath setPath, int id)
    {
        RequireExtents();
        var doc = LoadDoc();
        var parent = WalkToObject(doc, ParentPath(setPath));
        if (parent?.Fields[setPath.Segments[^1]] is JsonObject set)
            set.Remove(id.ToString());
        CollectGarbage(doc);
        SaveDoc(doc);
    }

    public void SetReference(NodePath fieldPath, int? id)
    {
        RequireExtents();
        var doc = LoadDoc();
        var parent = WalkToObject(doc, ParentPath(fieldPath))
            ?? throw new InvalidOperationException($"Parent of {fieldPath} is not an object.");
        parent.Fields[fieldPath.Segments[^1]] = id is null ? null : new JsonObject { ["ref"] = id.Value };
        CollectGarbage(doc);
        SaveDoc(doc);
    }

    public IReadOnlyDictionary<int, ObjectValue> ReadExtent(string typeName)
    {
        RequireExtents();
        var doc = LoadDoc();
        var map = new Dictionary<int, ObjectValue>();
        if (Extents(doc)[typeName] is JsonObject pool && _desc.FindType(typeName) is { } type)
            foreach (var (k, env) in pool)
                if (env is JsonObject e)
                    map[int.Parse(k)] = BuildResolvedObject(doc, EnvFields(e), type);
        return map;
    }

    public (string TypeName, ObjectValue Fields)? ReadById(int id)
    {
        RequireExtents();
        var doc = LoadDoc();
        foreach (var (typeName, pool) in Extents(doc))
            if (pool is JsonObject p && p[id.ToString()] is JsonObject env && _desc.FindType(typeName) is { } type)
                return (typeName, BuildResolvedObject(doc, EnvFields(env), type));
        return null;
    }

    private void RequireExtents()
    {
        if (!_usesExtents)
            throw new NotSupportedException("Object-model operations require a schema that uses sets/references.");
    }

    private static int NextId(JsonObject doc)
    {
        var max = ((JsonObject)doc["root"]!)["id"]?.GetValue<int>() ?? 1;
        foreach (var (_, pool) in Extents(doc))
            if (pool is JsonObject p)
                foreach (var (k, _) in p)
                    if (int.TryParse(k, out var n) && n > max) max = n;
        return max + 1;
    }

    // Mark-sweep from the root: any object no reference can reach is collected.
    private static void CollectGarbage(JsonObject doc)
    {
        var visited = new HashSet<int>();

        void Mark(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o when o.Count == 1 && o["ref"] is JsonValue rv && rv.TryGetValue<int>(out var id):
                    if (visited.Add(id) && ExtentEnvelopeAnyType(doc, id) is { } env)
                        Mark(EnvFields(env));
                    break;
                case JsonObject o:
                    foreach (var (_, v) in o) Mark(v);
                    break;
                case JsonArray a:
                    foreach (var v in a) Mark(v);
                    break;
            }
        }

        Mark(RootFields(doc));

        foreach (var (_, pool) in Extents(doc))
            if (pool is JsonObject p)
                foreach (var key in p.Select(kv => kv.Key).ToList())
                    if (int.TryParse(key, out var n) && !visited.Contains(n))
                        p.Remove(key);
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
        if (_usesExtents) return """{"extents":{},"root":{"id":1,"fields":{}}}""";
        return db.BaseType == BaseType.Bool ? "false" : "{}";
    }

    private static JsonObject NewJsonObject() => new();
}
