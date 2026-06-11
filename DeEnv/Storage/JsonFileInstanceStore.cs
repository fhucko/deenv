using System.Text.Json.Nodes;
using DeEnv.Instance;

namespace DeEnv.Storage;

// JSON-file store for the object-graph model. One uniform format: every value is
// a tagged object.
//
//   { "extents": { "<Type>": { "<id>": { "type":"object","typeName":T,"id":N,"fields":{…} } } },
//     "root":    { "type":"object","typeName":"Db","id":1 } }      // or a scalar value
//
// Value forms (the `type` discriminator is a fixed structural word):
//   scalar            { "type":"text", "value":"Ada" }            // no identity
//   object reference  { "type":"object", "typeName":T, "id":N }   // points into an extent
//   set               { "type":"set", "members": { "<id>": <object-ref> } }
//   dictionary        { "type":"dictionary", "entries": { "<key>": <value> } }
//
// An object's fields exist ONLY in its extent entry (the single source of truth);
// every object value held in a field/member/entry is the id-only reference form.
public sealed class JsonFileInstanceStore : IInstanceStore
{
    private readonly string _filePath;
    private readonly InstanceDescription _desc;
    private readonly TypeResolver _resolver;
    private readonly System.Text.Json.JsonSerializerOptions _writeOpts = new() { WriteIndented = true };

    public JsonFileInstanceStore(string filePath, InstanceDescription desc)
    {
        _filePath = filePath;
        _desc = desc;
        _resolver = new TypeResolver(desc);

        if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            File.WriteAllText(filePath, InitialJson());
    }

    // ── read ────────────────────────────────────────────────────────────────────

    public NodeValue? ReadNode(NodePath path)
    {
        if (path.Segments.Count > 0 && path.Segments[0] == "~") return null; // id-route → renderer

        var doc = LoadDoc();
        var db = _desc.Db();
        if (db == null) return null;

        if (doc["root"] is not JsonObject rootVal) return null;

        // Scalar Db root: the root is the value itself.
        if (rootVal["type"]?.GetValue<string>() != "object")
            return path.IsRoot ? LeafFromTagged(rootVal) : null;

        var curFields = FieldsOf(doc, rootVal);
        if (curFields == null) return null;
        var curType = db;

        var segs = path.Segments;
        for (var i = 0; i < segs.Count; i++)
        {
            var prop = curType.Props?.FirstOrDefault(p => p.Name == segs[i]);
            if (prop == null) return null;
            var elemType = ResolveTypeDef(prop.Type);
            var last = i == segs.Count - 1;

            switch (prop.Cardinality)
            {
                case Cardinality.Set:
                {
                    var setNode = curFields[prop.Name] as JsonObject;
                    if (last) return BuildSetValue(doc, setNode, elemType);

                    var member = (setNode?["members"] as JsonObject)?[segs[i + 1]] as JsonObject;
                    var mf = member == null ? null : FieldsOf(doc, member);
                    if (mf == null) return null;
                    if (i + 1 == segs.Count - 1) return BuildObject(doc, mf, elemType);
                    curFields = mf; curType = elemType; i++; continue;
                }
                case Cardinality.Dictionary:
                {
                    var dictNode = curFields[prop.Name] as JsonObject;
                    if (last) return BuildDictionary(doc, dictNode, elemType, (prop.KeyType ?? "text"));

                    var entry = (dictNode?["entries"] as JsonObject)?[segs[i + 1]] as JsonObject;
                    if (entry == null) return null;
                    if (i + 1 == segs.Count - 1)
                        return elemType.BaseType == BaseType.Object
                            ? BuildObjectFromRef(doc, entry, elemType)
                            : LeafFromTagged(entry);
                    if (elemType.BaseType != BaseType.Object) return null;
                    var ef = FieldsOf(doc, entry);
                    if (ef == null) return null;
                    curFields = ef; curType = elemType; i++; continue;
                }
                default: // Single
                {
                    if (_desc.IsObjectType(prop.Type))
                    {
                        // A single object-typed prop is a reference.
                        if (curFields[prop.Name] is not JsonObject refVal
                            || refVal["id"]?.GetValue<int>() is not int id)
                            return last ? new ReferenceValue(null, prop.Type) : null;
                        var rf = FieldsOf(doc, refVal);
                        if (rf == null) return last ? new ReferenceValue(null, prop.Type) : null;
                        if (last) return BuildObject(doc, rf, elemType);
                        curFields = rf; curType = elemType; continue;
                    }

                    if (!last) return null;
                    var v = curFields[prop.Name] as JsonObject;
                    return v != null ? LeafFromTagged(v) : DefaultBase(elemType.BaseType);
                }
            }
        }

        return BuildObject(doc, curFields, curType);
    }

    private ObjectValue BuildObject(JsonObject doc, JsonObject fields, TypeDefinition type)
    {
        var map = new Dictionary<string, NodeValue>();
        foreach (var prop in type.Props ?? [])
        {
            var elemType = ResolveTypeDef(prop.Type);
            switch (prop.Cardinality)
            {
                case Cardinality.Set:
                    map[prop.Name] = BuildSetValue(doc, fields[prop.Name] as JsonObject, elemType);
                    break;
                case Cardinality.Dictionary:
                    map[prop.Name] = BuildDictionary(doc, fields[prop.Name] as JsonObject, elemType, (prop.KeyType ?? "text"));
                    break;
                default:
                    if (_desc.IsObjectType(prop.Type))
                        map[prop.Name] = new ReferenceValue((fields[prop.Name] as JsonObject)?["id"]?.GetValue<int>(), prop.Type);
                    else
                    {
                        var v = fields[prop.Name] as JsonObject;
                        map[prop.Name] = v != null ? LeafFromTagged(v) : DefaultBase(elemType.BaseType);
                    }
                    break;
            }
        }
        return new ObjectValue(map);
    }

    private ObjectValue? BuildObjectFromRef(JsonObject doc, JsonObject objRef, TypeDefinition type)
    {
        var f = FieldsOf(doc, objRef);
        return f == null ? null : BuildObject(doc, f, type);
    }

    private SetValue BuildSetValue(JsonObject doc, JsonObject? setNode, TypeDefinition elemType)
    {
        var members = new Dictionary<int, NodeValue>();
        if (setNode?["members"] is JsonObject m)
            foreach (var (k, v) in m)
                if (v is JsonObject objRef && BuildObjectFromRef(doc, objRef, elemType) is { } obj)
                    members[int.Parse(k)] = obj;
        return new SetValue(setNode?["id"]?.GetValue<int>() ?? 0, members);
    }

    private DictionaryValue BuildDictionary(JsonObject doc, JsonObject? dictNode, TypeDefinition elemType, string keyType)
    {
        var entries = new Dictionary<NodeValue, NodeValue>();
        if (dictNode?["entries"] is JsonObject e)
            foreach (var (k, v) in e)
            {
                if (v is not JsonObject entry) continue;
                NodeValue? val = elemType.BaseType == BaseType.Object
                    ? BuildObjectFromRef(doc, entry, elemType)
                    : LeafFromTagged(entry);
                if (val != null) entries[ParseKey(k, keyType)] = val;
            }
        return new DictionaryValue(entries);
    }

    // ── write ─────────────────────────────────────────────────────────────────

    public void WriteLeaf(NodePath path, NodeValue value)
    {
        var doc = LoadDoc();
        if (path.IsRoot)
        {
            doc["root"] = ToTagged(value); // scalar Db root
            SaveDoc(doc);
            return;
        }
        var parent = WalkToObjectFields(doc, ParentPath(path))
            ?? throw new InvalidOperationException($"Parent of {path} is not an object.");
        parent.Fields[path.Segments[^1]] = ToTagged(value);
        SaveDoc(doc);
    }

    public void WriteObject(NodePath path, ObjectValue value)
    {
        var doc = LoadDoc();
        var target = WalkToObjectFields(doc, path)
            ?? throw new InvalidOperationException($"Path {path} is not a writable object.");

        foreach (var prop in target.Type.Props ?? [])
            if (prop.Cardinality == Cardinality.Single && !_desc.IsObjectType(prop.Type)
                && value.Fields.TryGetValue(prop.Name, out var v))
                target.Fields[prop.Name] = ToTagged(v);

        SaveDoc(doc);
    }

    public void WriteField(int objectId, string prop, NodeValue value)
    {
        var doc = LoadDoc();
        if (ExtentEntryById(doc, objectId)?["fields"] is not JsonObject fields)
            throw new InvalidOperationException($"No object with id {objectId}.");
        fields[prop] = ToTagged(value);
        SaveDoc(doc);
    }

    // Walk to the fields of the object a path lands on (following set/dict/refs).
    private (JsonObject Fields, TypeDefinition Type)? WalkToObjectFields(JsonObject doc, NodePath path)
    {
        var db = _desc.Db();
        if (db == null || doc["root"] is not JsonObject rootVal
            || rootVal["type"]?.GetValue<string>() != "object")
            return null;

        var curFields = FieldsOf(doc, rootVal);
        if (curFields == null) return null;
        var curType = db;

        var segs = path.Segments;
        for (var i = 0; i < segs.Count; i++)
        {
            var prop = curType.Props?.FirstOrDefault(p => p.Name == segs[i]);
            if (prop == null) return null;
            var elemType = ResolveTypeDef(prop.Type);

            if (prop.Cardinality == Cardinality.Set)
            {
                if (i + 1 >= segs.Count) return null;
                var member = ((curFields[prop.Name] as JsonObject)?["members"] as JsonObject)?[segs[i + 1]] as JsonObject;
                var mf = member == null ? null : FieldsOf(doc, member);
                if (mf == null) return null;
                curFields = mf; curType = elemType; i++; continue;
            }
            if (prop.Cardinality == Cardinality.Dictionary)
            {
                if (i + 1 >= segs.Count || elemType.BaseType != BaseType.Object) return null;
                var entry = ((curFields[prop.Name] as JsonObject)?["entries"] as JsonObject)?[segs[i + 1]] as JsonObject;
                var ef = entry == null ? null : FieldsOf(doc, entry);
                if (ef == null) return null;
                curFields = ef; curType = elemType; i++; continue;
            }
            if (_desc.IsObjectType(prop.Type))
            {
                if (curFields[prop.Name] is not JsonObject refVal) return null;
                var rf = FieldsOf(doc, refVal);
                if (rf == null) return null;
                curFields = rf; curType = elemType; continue;
            }
            return null; // scalar is not an object
        }
        return (curFields, curType);
    }

    // ── object-graph mutations ──────────────────────────────────────────────────

    public int CreateObject(string typeName, ObjectValue fields)
    {
        var doc = LoadDoc();
        var id = MintObject(doc, typeName, fields);
        SaveDoc(doc);
        return id;
    }

    public void AddToSet(NodePath setPath, int id)
    {
        var doc = LoadDoc();
        var typeName = _resolver.ResolveType(setPath)?.Type.Name
            ?? throw new InvalidOperationException($"{setPath} does not resolve.");
        var set = EnsureCollection(doc, setPath, "set", "members");
        set[id.ToString()] = ObjectRef(typeName, id);
        SaveDoc(doc);
    }

    public void RemoveFromSet(NodePath setPath, int id)
    {
        var doc = LoadDoc();
        if (CollectionNode(doc, setPath, "members") is { } members)
            members.Remove(id.ToString());
        CollectGarbage(doc);
        SaveDoc(doc);
    }

    // ── set ops by intrinsic id (a set is found by its own id, not a path) ──────────

    public void AddToSet(int setId, int objectId)
    {
        var doc = LoadDoc();
        var members = FindSetNode(doc, setId)?["members"] as JsonObject
            ?? throw new InvalidOperationException($"No set with id {setId}.");
        var typeName = ExtentEntryById(doc, objectId)?["typeName"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"No object with id {objectId}.");
        members[objectId.ToString()] = ObjectRef(typeName, objectId);
        SaveDoc(doc);
    }

    public void RemoveFromSet(int setId, int objectId)
    {
        var doc = LoadDoc();
        if (FindSetNode(doc, setId)?["members"] is JsonObject members)
            members.Remove(objectId.ToString());
        CollectGarbage(doc);
        SaveDoc(doc);
    }

    // Locate a set node by its intrinsic id (sets live in object fields, in extents).
    private static JsonObject? FindSetNode(JsonObject doc, int setId)
    {
        foreach (var (_, pool) in Extents(doc))
            if (pool is JsonObject p)
                foreach (var (_, env) in p)
                    if (env is JsonObject e && e["fields"] is JsonObject fields)
                        foreach (var (_, fv) in fields)
                            if (fv is JsonObject node && node["type"]?.GetValue<string>() == "set"
                                && node["id"]?.GetValue<int>() == setId)
                                return node;
        return null;
    }

    public void SetReference(NodePath fieldPath, int? id)
    {
        var doc = LoadDoc();
        var parent = WalkToObjectFields(doc, ParentPath(fieldPath))
            ?? throw new InvalidOperationException($"Parent of {fieldPath} is not an object.");
        var field = fieldPath.Segments[^1];
        if (id is null)
            parent.Fields.Remove(field);
        else
        {
            var typeName = _resolver.ResolveType(fieldPath)?.Type.Name
                ?? throw new InvalidOperationException($"{fieldPath} does not resolve.");
            parent.Fields[field] = ObjectRef(typeName, id.Value);
        }
        CollectGarbage(doc);
        SaveDoc(doc);
    }

    public IReadOnlyDictionary<int, ObjectValue> ReadExtent(string typeName)
    {
        var doc = LoadDoc();
        var map = new Dictionary<int, ObjectValue>();
        if (Extents(doc)[typeName] is JsonObject pool && _desc.FindType(typeName) is { } type)
            foreach (var (k, env) in pool)
                if (env is JsonObject e && e["fields"] is JsonObject f)
                    map[int.Parse(k)] = BuildObject(doc, f, type);
        return map;
    }

    public (string TypeName, ObjectValue Fields)? ReadById(int id)
    {
        var doc = LoadDoc();
        foreach (var (typeName, pool) in Extents(doc))
            if (pool is JsonObject p && p[id.ToString()] is JsonObject env
                && env["fields"] is JsonObject f && _desc.FindType(typeName) is { } type)
                return (typeName, BuildObject(doc, f, type));
        return null;
    }

    // ── dictionary entries (manual keys; values are scalars or object references) ──

    public NodeValue NewEntryTemplate(NodePath path)
    {
        var typeInfo = _resolver.ResolveType(path)
            ?? throw new InvalidOperationException($"Path {path} does not resolve.");
        if (typeInfo.Cardinality is not (Cardinality.Dictionary or Cardinality.Set))
            throw new InvalidOperationException($"{path} is not a dictionary or set.");
        return BuildDefault(typeInfo.Type);
    }

    public void CreateEntry(NodePath dictPath, NodeValue key, NodeValue value)
    {
        var doc = LoadDoc();
        var entries = CollectionNode(doc, dictPath, "entries");
        if (entries != null && entries.ContainsKey(KeyToString(key)))
            throw new InvalidOperationException(
                $"An entry with key '{KeyToString(key)}' already exists at {dictPath}.");
        WriteDictionaryEntryInto(doc, dictPath, key, value);
        SaveDoc(doc);
    }

    public void WriteDictionaryEntry(NodePath path, NodeValue key, NodeValue value)
    {
        var doc = LoadDoc();
        WriteDictionaryEntryInto(doc, path, key, value);
        SaveDoc(doc);
    }

    private void WriteDictionaryEntryInto(JsonObject doc, NodePath path, NodeValue key, NodeValue value)
    {
        var typeInfo = _resolver.ResolveType(path)
            ?? throw new InvalidOperationException($"{path} does not resolve.");
        var entries = EnsureCollection(doc, path, "dictionary", "entries");

        if (value is ObjectValue obj && typeInfo.Type.BaseType == BaseType.Object)
        {
            var id = MintObject(doc, typeInfo.Type.Name, obj);
            entries[KeyToString(key)] = ObjectRef(typeInfo.Type.Name, id);
        }
        else
        {
            entries[KeyToString(key)] = ToTagged(value);
        }
    }

    public void RemoveDictionaryEntry(NodePath path, NodeValue key)
    {
        var doc = LoadDoc();
        if (CollectionNode(doc, path, "entries") is { } entries)
            entries.Remove(KeyToString(key));
        CollectGarbage(doc);
        SaveDoc(doc);
    }

    // ── helpers: doc + extents ──────────────────────────────────────────────────

    private JsonObject LoadDoc()
    {
        var text = File.Exists(_filePath) ? File.ReadAllText(_filePath) : "";
        var doc = (string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text)) as JsonObject
                  ?? new JsonObject();
        if (doc["extents"] is not JsonObject) doc["extents"] = new JsonObject();
        if (doc["root"] is null) doc["root"] = InitialRootNode();
        return doc;
    }

    private void SaveDoc(JsonObject doc) => File.WriteAllText(_filePath, doc.ToJsonString(_writeOpts));

    private static JsonObject Extents(JsonObject doc) => (JsonObject)doc["extents"]!;

    // The fields of the object an object-value (ref or root) points at, via its extent.
    private static JsonObject? FieldsOf(JsonObject doc, JsonObject objVal)
    {
        var typeName = objVal["typeName"]?.GetValue<string>();
        if (typeName == null || objVal["id"]?.GetValue<int>() is not int id) return null;
        return Extents(doc)[typeName] is JsonObject pool && pool[id.ToString()] is JsonObject env
            ? env["fields"] as JsonObject
            : null;
    }

    private static JsonObject? ExtentEntryById(JsonObject doc, int id)
    {
        foreach (var (_, pool) in Extents(doc))
            if (pool is JsonObject p && p[id.ToString()] is JsonObject env)
                return env;
        return null;
    }

    private int MintObject(JsonObject doc, string typeName, ObjectValue fields)
    {
        if (Extents(doc)[typeName] is not JsonObject pool)
        {
            pool = new JsonObject();
            Extents(doc)[typeName] = pool;
        }
        var id = MintId(doc);
        var type = _desc.FindType(typeName)
            ?? throw new InvalidOperationException($"Unknown type '{typeName}'.");
        pool[id.ToString()] = new JsonObject
        {
            ["type"] = "object",
            ["typeName"] = typeName,
            ["id"] = id,
            ["fields"] = BuildFieldsJson(doc, type, fields),
        };
        return id;
    }

    // New object's stored fields: provided scalars, empty collections (each with its
    // own intrinsic id), unset refs.
    private JsonObject BuildFieldsJson(JsonObject doc, TypeDefinition type, ObjectValue provided)
    {
        var fjson = new JsonObject();
        foreach (var prop in type.Props ?? [])
        {
            if (prop.Cardinality == Cardinality.Set)
                fjson[prop.Name] = new JsonObject { ["type"] = "set", ["id"] = MintId(doc), ["members"] = new JsonObject() };
            else if (prop.Cardinality == Cardinality.Dictionary)
                fjson[prop.Name] = new JsonObject { ["type"] = "dictionary", ["id"] = MintId(doc), ["entries"] = new JsonObject() };
            else if (!_desc.IsObjectType(prop.Type))
                fjson[prop.Name] = provided.Fields.TryGetValue(prop.Name, out var v)
                    ? ToTagged(v)
                    : DefaultTagged(ResolveTypeDef(prop.Type).BaseType);
            // single object props start unset (absent)
        }
        return fjson;
    }

    private static JsonObject ObjectRef(string typeName, int id) =>
        new() { ["type"] = "object", ["typeName"] = typeName, ["id"] = id };

    // The collection node ("set"/"dictionary") at path, creating it if absent.
    private JsonObject EnsureCollection(JsonObject doc, NodePath path, string type, string slot)
    {
        var parent = WalkToObjectFields(doc, ParentPath(path))
            ?? throw new InvalidOperationException($"Parent of {path} is not an object.");
        var field = path.Segments[^1];
        if (parent.Fields[field] is not JsonObject node || node[slot] is not JsonObject)
        {
            node = new JsonObject { ["type"] = type, ["id"] = MintId(doc), [slot] = new JsonObject() };
            parent.Fields[field] = node;
        }
        return (JsonObject)node[slot]!;
    }

    private JsonObject? CollectionNode(JsonObject doc, NodePath path, string slot)
    {
        var parent = WalkToObjectFields(doc, ParentPath(path));
        return (parent?.Fields[path.Segments[^1]] as JsonObject)?[slot] as JsonObject;
    }

    // Intrinsic ids come from one global counter shared by objects, sets and dicts, so
    // every mutable thing has a unique stable id. Falls back to the max extent id for a
    // legacy doc with no counter yet.
    private static int MintId(JsonObject doc)
    {
        var next = (doc["nextId"]?.GetValue<int>() ?? ExtentMaxId(doc)) + 1;
        doc["nextId"] = next;
        return next;
    }

    private static int ExtentMaxId(JsonObject doc)
    {
        var max = 0;
        foreach (var (_, pool) in Extents(doc))
            if (pool is JsonObject p)
                foreach (var (k, _) in p)
                    if (int.TryParse(k, out var n) && n > max) max = n;
        return max;
    }

    // Mark-sweep from the root: any object value reachable is kept; the rest swept.
    private static void CollectGarbage(JsonObject doc)
    {
        var visited = new HashSet<int>();

        void Mark(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o when o["type"]?.GetValue<string>() == "object" && o["id"]?.GetValue<int>() is int id:
                    if (visited.Add(id) && ExtentEntryById(doc, id) is { } env)
                        Mark(env["fields"]);
                    break;
                case JsonObject o:
                    foreach (var (_, v) in o) Mark(v);
                    break;
                case JsonArray a:
                    foreach (var v in a) Mark(v);
                    break;
            }
        }

        Mark(doc["root"]);

        foreach (var (_, pool) in Extents(doc))
            if (pool is JsonObject p)
                foreach (var key in p.Select(kv => kv.Key).ToList())
                    if (int.TryParse(key, out var n) && !visited.Contains(n))
                        p.Remove(key);
    }

    // ── helpers: tagged values ──────────────────────────────────────────────────

    private static JsonObject ToTagged(NodeValue value) => value switch
    {
        BoolValue b      => new JsonObject { ["type"] = "bool",     ["value"] = b.Value },
        IntValue i       => new JsonObject { ["type"] = "int",      ["value"] = i.Value },
        DecimalValue d   => new JsonObject { ["type"] = "decimal",  ["value"] = d.Value },
        TextValue t      => new JsonObject { ["type"] = "text",     ["value"] = t.Text },
        DateValue d      => new JsonObject { ["type"] = "date",     ["value"] = d.Value.ToString("yyyy-MM-dd") },
        DateTimeValue dt => new JsonObject { ["type"] = "datetime", ["value"] = dt.Value.ToString("O") },
        _ => throw new InvalidOperationException($"Cannot tag {value.GetType().Name} as a leaf.")
    };

    private static NodeValue LeafFromTagged(JsonObject tagged)
    {
        var type = tagged["type"]?.GetValue<string>();
        var v = tagged["value"];
        return type switch
        {
            "bool"     => new BoolValue(v!.GetValue<bool>()),
            "int"      => new IntValue(v!.GetValue<int>()),
            "decimal"  => new DecimalValue(v!.GetValue<decimal>()),
            "text"     => new TextValue(v!.GetValue<string>()),
            "date"     => new DateValue(DateOnly.Parse(v!.GetValue<string>())),
            "datetime" => new DateTimeValue(DateTimeOffset.Parse(v!.GetValue<string>())),
            _ => throw new InvalidOperationException($"Not a scalar value: type '{type}'.")
        };
    }

    private NodeValue BuildDefault(TypeDefinition type)
    {
        if (type.BaseType != BaseType.Object)
            return DefaultBase(type.BaseType);

        var fields = new Dictionary<string, NodeValue>();
        foreach (var prop in type.Props ?? [])
        {
            if (prop.Cardinality == Cardinality.Set)
                fields[prop.Name] = new SetValue(0, new Dictionary<int, NodeValue>()); // template; not stored
            else if (prop.Cardinality == Cardinality.Dictionary)
                fields[prop.Name] = new DictionaryValue(new Dictionary<NodeValue, NodeValue>());
            else if (_desc.IsObjectType(prop.Type))
                fields[prop.Name] = new ReferenceValue(null, prop.Type);
            else
                fields[prop.Name] = DefaultBase(ResolveTypeDef(prop.Type).BaseType);
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

    private static JsonObject DefaultTagged(BaseType bt) => ToTagged(DefaultBase(bt));

    private TypeDefinition ResolveTypeDef(string name) =>
        BaseTypes.IsName(name)
            ? BaseTypes.Leaf(name)
            : _desc.FindType(name) ?? throw new InvalidOperationException($"Unknown type '{name}'.");

    private static NodeValue ParseKey(string key, string keyType) => keyType switch
    {
        "int"      => new IntValue(int.Parse(key)),
        "decimal"  => new DecimalValue(decimal.Parse(key)),
        "bool"     => new BoolValue(bool.Parse(key)),
        "date"     => new DateValue(DateOnly.Parse(key)),
        "datetime" => new DateTimeValue(DateTimeOffset.Parse(key)),
        _          => new TextValue(key)
    };

    private static string KeyToString(NodeValue key) => key switch
    {
        IntValue i       => i.Value.ToString(),
        TextValue t      => t.Text,
        DecimalValue d   => d.Value.ToString(),
        BoolValue b      => b.Value.ToString().ToLowerInvariant(),
        DateValue d      => d.Value.ToString("yyyy-MM-dd"),
        DateTimeValue dt => dt.Value.ToString("O"),
        _ => throw new InvalidOperationException($"Cannot use {key.GetType().Name} as a key.")
    };

    private static NodePath ParentPath(NodePath path) =>
        NodePath.FromSegments(path.Segments.Take(path.Segments.Count - 1));

    // ── initial document ────────────────────────────────────────────────────────

    private string InitialJson() => BuildInitialDoc().ToJsonString(_writeOpts);

    private JsonObject BuildInitialDoc()
    {
        if (_desc.InitialData?.Extents is { } seed)
            return BuildSeededDoc(seed);

        var extents = new JsonObject();
        var doc = new JsonObject { ["extents"] = extents, ["nextId"] = 1, ["root"] = InitialRootNode() };
        var db = _desc.Db();
        if (db is { BaseType: BaseType.Object })
            // The root is id 1; its collection props get ids from the counter (which
            // starts at 1, so they mint 2, 3, …) so every set/dict has a stable id.
            extents["Db"] = new JsonObject
            {
                ["1"] = new JsonObject
                {
                    ["type"] = "object", ["typeName"] = "Db", ["id"] = 1,
                    ["fields"] = BuildFieldsJson(doc, db, new ObjectValue(new Dictionary<string, NodeValue>())),
                },
            };
        return doc;
    }

    // First-run document from the schema's hand-authored initialData (normalized
    // extents: plain scalars, sets as arrays of member ids, refs as bare ids — already
    // validated by the loader). nextId starts above every authored id, so the set/dict
    // ids minted here, and everything created later, never collide.
    private JsonObject BuildSeededDoc(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>> seed)
    {
        var extents = new JsonObject();
        var doc = new JsonObject { ["extents"] = extents };

        var maxId = 0;
        foreach (var pool in seed.Values)
            foreach (var idText in pool.Keys)
                maxId = Math.Max(maxId, int.Parse(idText));
        doc["nextId"] = maxId;

        foreach (var (typeName, pool) in seed)
        {
            var type = _desc.FindType(typeName)!;
            var poolJson = new JsonObject();
            extents[typeName] = poolJson;
            foreach (var (idText, fields) in pool)
                poolJson[idText] = new JsonObject
                {
                    ["type"] = "object",
                    ["typeName"] = typeName,
                    ["id"] = int.Parse(idText),
                    ["fields"] = SeededFieldsJson(doc, type, fields),
                };
        }

        doc["root"] = ObjectRef("Db", int.Parse(seed["Db"].Keys.Single()));
        return doc;
    }

    private JsonObject SeededFieldsJson(JsonObject doc, TypeDefinition type, System.Text.Json.JsonElement fields)
    {
        var fjson = new JsonObject();
        foreach (var prop in type.Props ?? [])
        {
            System.Text.Json.JsonElement f = default;
            var has = fields.TryGetProperty(prop.Name, out f);

            switch (prop.Cardinality)
            {
                case Cardinality.Set:
                {
                    var members = new JsonObject();
                    if (has)
                        foreach (var m in f.EnumerateArray())
                            members[m.GetInt32().ToString()] = ObjectRef(prop.Type, m.GetInt32());
                    fjson[prop.Name] = new JsonObject { ["type"] = "set", ["id"] = MintId(doc), ["members"] = members };
                    break;
                }
                case Cardinality.Dictionary:
                    // Seeding dictionary entries: a later slice (dicts are not in the
                    // Code runtime yet); the node still gets its intrinsic id.
                    fjson[prop.Name] = new JsonObject { ["type"] = "dictionary", ["id"] = MintId(doc), ["entries"] = new JsonObject() };
                    break;
                default:
                    if (_desc.IsObjectType(prop.Type))
                    {
                        if (has) fjson[prop.Name] = ObjectRef(prop.Type, f.GetInt32());
                        // absent → unset reference
                    }
                    else
                    {
                        var bt = ResolveTypeDef(prop.Type).BaseType;
                        fjson[prop.Name] = has ? SeededScalar(f, bt) : DefaultTagged(bt);
                    }
                    break;
            }
        }
        return fjson;
    }

    private static JsonObject SeededScalar(System.Text.Json.JsonElement v, BaseType bt) => bt switch
    {
        BaseType.Bool     => ToTagged(new BoolValue(v.GetBoolean())),
        BaseType.Int      => ToTagged(new IntValue(v.GetInt32())),
        BaseType.Decimal  => ToTagged(new DecimalValue(v.GetDecimal())),
        BaseType.Text     => ToTagged(new TextValue(v.GetString() ?? "")),
        BaseType.Date     => ToTagged(new DateValue(DateOnly.Parse(v.GetString() ?? ""))),
        BaseType.DateTime => ToTagged(new DateTimeValue(DateTimeOffset.Parse(v.GetString() ?? ""))),
        _ => throw new InvalidOperationException($"No scalar seed for {bt}"),
    };

    private JsonNode InitialRootNode()
    {
        var db = _desc.Db();
        return db is { BaseType: BaseType.Object }
            ? ObjectRef("Db", 1)
            : DefaultTagged(db?.BaseType ?? BaseType.Bool);
    }
}
