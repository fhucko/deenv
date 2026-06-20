using System.Text.Json;
using DeEnv.Instance;

namespace DeEnv.Storage;

// JSON-file store for the object-graph model, manipulated through a TYPED model
// (StoreModel.cs). One uniform on-disk format: every value is a tagged object.
//
//   { "extents": { "<Type>": { "<id>": { "type":"object","typeName":T,"id":N,"fields":{…} } } },
//     "root":    { "type":"object","typeName":"Db","id":1 }, "nextId": N }   // or a scalar root
//
// Value forms (the `type` discriminator is a fixed structural word):
//   scalar            { "type":"text", "value":"Ada" }            // no identity
//   object reference  { "type":"object", "typeName":T, "id":N }   // points into an extent
//   set               { "type":"set", "id":N, "members": { "<id>": <object-ref> } }
//   dictionary        { "type":"dictionary", "id":N, "entries": { "<key>": <value> } }
//
// An object's fields exist ONLY in its extent entry (the single source of truth); every
// object value held in a field/member/entry is the id-only reference form. Internally the
// document is the closed StoredValue union, so a generic walk (notably the GC) pattern-
// matches on node kind and never reads a user field key named "type" as a tag.
public sealed class JsonFileInstanceStore : IInstanceStore
{
    private readonly string _filePath;
    private readonly InstanceDescription _desc;
    private readonly TypeResolver _resolver;
    // Shared, read-only after first use (the recommended JsonSerializerOptions pattern): both the
    // instance load/save and the static migrate/load/save helpers serialize through it.
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        // CLR PascalCase props → camelCase JSON (the on-disk keys: extents/root/nextId,
        // typeName/id/fields). Dictionary keys (Extents = type names, Fields = user field
        // names) are NOT renamed — DictionaryKeyPolicy is deliberately unset.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new StoredValueConverter() },
    };

    // The document is loaded into memory ONCE at construction and kept as the
    // authoritative copy: reads serve from it, and a mutation edits it then rewrites the
    // file (write-temp-then-move, atomic for any reader) for durability. This is safe
    // because an instance is single-process — nothing else writes the file behind our
    // back (the cross-process / real-time story is a later milestone). The lock
    // serializes operations against concurrent connections in this one process.
    private readonly object _sync = new();
    private StoreDoc _doc;

    public JsonFileInstanceStore(string filePath, InstanceDescription desc)
    {
        _filePath = filePath;
        _desc = desc;
        _resolver = new TypeResolver(desc);

        if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
        {
            // Seed from the app's initialData (or an empty root) and persist it.
            _doc = BuildInitialDoc();
            Save();
        }
        else
        {
            var doc = LoadDocFromFile();
            // The startup guard: an existing data file must match the running app's
            // types — fail loudly here rather than half-work over stale data.
            StoredDataValidator.Validate(doc, desc, filePath);
            _doc = Normalize(doc);
        }
    }

    // Deserialize the file to the typed model. A malformed / garbage file (not a readable
    // document) becomes a StoredDataException — same remedy message as before.
    private StoreDoc LoadDocFromFile() => LoadRaw(_filePath);

    // Deserialize a data file to the typed model WITHOUT the startup guard (the instance ctor validates
    // separately; the static migrate pass re-validates after reconciling). A garbage / unreadable file
    // becomes a StoredDataException with the same remedy message.
    private static StoreDoc LoadRaw(string path)
    {
        try
        {
            if (JsonSerializer.Deserialize<StoreDoc>(File.ReadAllText(path), Opts) is { } doc)
                return doc;
        }
        catch (JsonException)
        {
        }
        throw new StoredDataException(
            $"Data file '{path}' is not a readable data document. " +
            "Delete or move the file to reseed it from the app's initialData.");
    }

    // ── read ────────────────────────────────────────────────────────────────────

    public NodeValue? ReadNode(NodePath path)
    {
        lock (_sync) return ReadNodeCore(path);
    }

    private NodeValue? ReadNodeCore(NodePath path)
    {
        if (path.Segments.Count > 0 && path.Segments[0] == "~") return null; // id-route → renderer

        var db = _desc.Db();
        if (db == null) return null;
        if (_doc.Root is not { } rootVal) return null;

        // Scalar Db root: the root is the value itself.
        if (rootVal is not StoredRef)
            return path.IsRoot && rootVal is StoredLeaf leaf ? leaf.Scalar : null;

        var curObj = ResolveRef((StoredRef)rootVal);
        if (curObj == null) return null;
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
                    var set = curObj.Fields.GetValueOrDefault(prop.Name) as StoredSet;
                    if (last) return BuildSetValue(set, elemType);

                    var member = set?.Members.GetValueOrDefault(ParseSeg(segs[i + 1])) as StoredRef;
                    var mo = member == null ? null : ResolveRef(member);
                    if (mo == null) return null;
                    if (i + 1 == segs.Count - 1) return BuildObject(mo, elemType);
                    curObj = mo; curType = elemType; i++; continue;
                }
                case Cardinality.Dictionary:
                {
                    var dict = curObj.Fields.GetValueOrDefault(prop.Name) as StoredDict;
                    if (last) return BuildDictionary(dict, elemType, prop.KeyType ?? "text");

                    var entry = dict?.Entries.GetValueOrDefault(segs[i + 1]);
                    if (entry == null) return null;
                    if (i + 1 == segs.Count - 1)
                        return elemType.BaseType == BaseType.Object
                            ? (entry is StoredRef er ? BuildObjectFromRef(er, elemType) : null)
                            : (entry is StoredLeaf el ? el.Scalar : null);
                    if (elemType.BaseType != BaseType.Object) return null;
                    var eo = entry is StoredRef er2 ? ResolveRef(er2) : null;
                    if (eo == null) return null;
                    curObj = eo; curType = elemType; i++; continue;
                }
                default: // Single
                {
                    if (_desc.IsObjectType(prop.Type))
                    {
                        // A single object-typed prop is a reference.
                        if (curObj.Fields.GetValueOrDefault(prop.Name) is not StoredRef refVal)
                            return last ? new ReferenceValue(null, prop.Type) : null;
                        var ro = ResolveRef(refVal);
                        if (ro == null) return last ? new ReferenceValue(null, prop.Type) : null;
                        if (last) return BuildObject(ro, elemType);
                        curObj = ro; curType = elemType; continue;
                    }

                    if (!last) return null;
                    return curObj.Fields.GetValueOrDefault(prop.Name) is StoredLeaf sl
                        ? sl.Scalar
                        : DefaultBase(elemType.BaseType);
                }
            }
        }

        return BuildObject(curObj, curType);
    }

    private ObjectValue BuildObject(StoredObject obj, TypeDefinition type)
    {
        var map = new Dictionary<string, NodeValue>();
        foreach (var prop in type.Props ?? [])
        {
            var elemType = ResolveTypeDef(prop.Type);
            switch (prop.Cardinality)
            {
                case Cardinality.Set:
                    map[prop.Name] = BuildSetValue(obj.Fields.GetValueOrDefault(prop.Name) as StoredSet, elemType);
                    break;
                case Cardinality.Dictionary:
                    map[prop.Name] = BuildDictionary(obj.Fields.GetValueOrDefault(prop.Name) as StoredDict, elemType, prop.KeyType ?? "text");
                    break;
                default:
                    if (_desc.IsObjectType(prop.Type))
                        map[prop.Name] = new ReferenceValue((obj.Fields.GetValueOrDefault(prop.Name) as StoredRef)?.Id, prop.Type);
                    else
                        map[prop.Name] = obj.Fields.GetValueOrDefault(prop.Name) is StoredLeaf sl
                            ? sl.Scalar
                            : DefaultBase(elemType.BaseType);
                    break;
            }
        }
        return new ObjectValue(map);
    }

    private ObjectValue? BuildObjectFromRef(StoredRef objRef, TypeDefinition type)
    {
        var o = ResolveRef(objRef);
        return o == null ? null : BuildObject(o, type);
    }

    private SetValue BuildSetValue(StoredSet? set, TypeDefinition elemType)
    {
        var members = new Dictionary<int, NodeValue>();
        if (set != null)
            foreach (var (k, v) in set.Members)
                if (v is StoredRef objRef && BuildObjectFromRef(objRef, elemType) is { } obj)
                    members[k] = obj;
        return new SetValue(set?.Id ?? 0, members);
    }

    private DictionaryValue BuildDictionary(StoredDict? dict, TypeDefinition elemType, string keyType)
    {
        var entries = new Dictionary<NodeValue, NodeValue>();
        if (dict != null)
            foreach (var (k, v) in dict.Entries)
            {
                NodeValue? val = elemType.BaseType == BaseType.Object
                    ? (v is StoredRef objRef ? BuildObjectFromRef(objRef, elemType) : null)
                    : (v is StoredLeaf leaf ? leaf.Scalar : null);
                if (val != null) entries[ParseKey(k, keyType)] = val;
            }
        return new DictionaryValue(dict?.Id ?? 0, entries);
    }

    // ── write ─────────────────────────────────────────────────────────────────

    public void WriteLeaf(NodePath path, NodeValue value)
    {
        lock (_sync) WriteLeafCore(path, value);
    }

    private void WriteLeafCore(NodePath path, NodeValue value)
    {
        if (path.IsRoot)
        {
            _doc.Root = new StoredLeaf(value); // scalar Db root
            Save();
            return;
        }
        var parent = WalkToObject(ParentPath(path))
            ?? throw new InvalidOperationException($"Parent of {path} is not an object.");
        parent.Object.Fields[path.Segments[^1]] = new StoredLeaf(value);
        Save();
    }

    public void WriteObject(NodePath path, ObjectValue value)
    {
        lock (_sync) WriteObjectCore(path, value);
    }

    private void WriteObjectCore(NodePath path, ObjectValue value)
    {
        var target = WalkToObject(path)
            ?? throw new InvalidOperationException($"Path {path} is not a writable object.");

        foreach (var prop in target.Type.Props ?? [])
            if (prop.Cardinality == Cardinality.Single && !_desc.IsObjectType(prop.Type)
                && value.Fields.TryGetValue(prop.Name, out var v))
                target.Object.Fields[prop.Name] = new StoredLeaf(v);

        Save();
    }

    public void WriteField(int objectId, string prop, NodeValue value)
    {
        lock (_sync)
        {
            var entry = ExtentEntryById(objectId)
                ?? throw new InvalidOperationException($"No object with id {objectId}.");
            entry.Fields[prop] = new StoredLeaf(value);
            Save();
        }
    }

    // Walk to the object a path lands on (following set/dict/refs).
    private (StoredObject Object, TypeDefinition Type)? WalkToObject(NodePath path)
    {
        var db = _desc.Db();
        if (db == null || _doc.Root is not StoredRef rootRef) return null;

        var curObj = ResolveRef(rootRef);
        if (curObj == null) return null;
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
                var member = (curObj.Fields.GetValueOrDefault(prop.Name) as StoredSet)
                    ?.Members.GetValueOrDefault(ParseSeg(segs[i + 1])) as StoredRef;
                var mo = member == null ? null : ResolveRef(member);
                if (mo == null) return null;
                curObj = mo; curType = elemType; i++; continue;
            }
            if (prop.Cardinality == Cardinality.Dictionary)
            {
                if (i + 1 >= segs.Count || elemType.BaseType != BaseType.Object) return null;
                var entry = (curObj.Fields.GetValueOrDefault(prop.Name) as StoredDict)
                    ?.Entries.GetValueOrDefault(segs[i + 1]) as StoredRef;
                var eo = entry == null ? null : ResolveRef(entry);
                if (eo == null) return null;
                curObj = eo; curType = elemType; i++; continue;
            }
            if (_desc.IsObjectType(prop.Type))
            {
                if (curObj.Fields.GetValueOrDefault(prop.Name) is not StoredRef refVal) return null;
                var ro = ResolveRef(refVal);
                if (ro == null) return null;
                curObj = ro; curType = elemType; continue;
            }
            return null; // scalar is not an object
        }
        return (curObj, curType);
    }

    // ── object-graph mutations ──────────────────────────────────────────────────

    public int CreateObject(string typeName, ObjectValue fields)
    {
        lock (_sync)
        {
            var id = MintObject(typeName, fields);
            Save();
            return id;
        }
    }

    public void AddToSet(NodePath setPath, int id)
    {
        lock (_sync)
        {
            var typeName = _resolver.ResolveType(setPath)?.Type.Name
                ?? throw new InvalidOperationException($"{setPath} does not resolve.");
            var set = EnsureSet(setPath);
            set.Members[id] = new StoredRef(typeName, id);
            Save();
        }
    }

    public void RemoveFromSet(NodePath setPath, int id)
    {
        lock (_sync)
        {
            if (SetNodeAt(setPath) is { } set)
                set.Members.Remove(id);
            CollectGarbage();
            Save();
        }
    }

    // ── set ops by intrinsic id (a set is found by its own id, not a path) ──────────

    public void AddToSet(int setId, int objectId)
    {
        lock (_sync)
        {
            var set = FindSetNode(setId)
                ?? throw new InvalidOperationException($"No set with id {setId}.");
            var typeName = ExtentEntryById(objectId)?.TypeName
                ?? throw new InvalidOperationException($"No object with id {objectId}.");
            set.Members[objectId] = new StoredRef(typeName, objectId);
            Save();
        }
    }

    public void RemoveFromSet(int setId, int objectId)
    {
        lock (_sync)
        {
            if (FindSetNode(setId) is { } set)
                set.Members.Remove(objectId);
            CollectGarbage();
            Save();
        }
    }

    // The declared element type of the set carrying this intrinsic id, or null when
    // no set does. Lets a mutation be validated against the schema before it lands.
    public string? SetElementType(int setId)
    {
        lock (_sync)
        {
            foreach (var (typeName, pool) in _doc.Extents)
                if (_desc.FindType(typeName) is { } type)
                    foreach (var entry in pool.Values)
                        foreach (var prop in type.Props ?? [])
                            if (prop.Cardinality == Cardinality.Set
                                && entry.Fields.GetValueOrDefault(prop.Name) is StoredSet set
                                && set.Id == setId)
                                return prop.Type;
            return null;
        }
    }

    // Locate a set node by its intrinsic id (sets live in object fields, in extents).
    private StoredSet? FindSetNode(int setId)
    {
        foreach (var (_, pool) in _doc.Extents)
            foreach (var entry in pool.Values)
                foreach (var fv in entry.Fields.Values)
                    if (fv is StoredSet set && set.Id == setId)
                        return set;
        return null;
    }

    public void WriteReference(int objectId, string prop, int? targetId, string targetTypeName)
    {
        lock (_sync)
        {
            var entry = ExtentEntryById(objectId)
                ?? throw new InvalidOperationException($"No object with id {objectId}.");
            if (targetId is null)
                entry.Fields.Remove(prop);
            else
                entry.Fields[prop] = new StoredRef(targetTypeName, targetId.Value);
            CollectGarbage();
            Save();
        }
    }

    public void SetReference(NodePath fieldPath, int? id)
    {
        lock (_sync)
        {
            var parent = WalkToObject(ParentPath(fieldPath))
                ?? throw new InvalidOperationException($"Parent of {fieldPath} is not an object.");
            var field = fieldPath.Segments[^1];
            if (id is null)
                parent.Object.Fields.Remove(field);
            else
            {
                var typeName = _resolver.ResolveType(fieldPath)?.Type.Name
                    ?? throw new InvalidOperationException($"{fieldPath} does not resolve.");
                parent.Object.Fields[field] = new StoredRef(typeName, id.Value);
            }
            CollectGarbage();
            Save();
        }
    }

    public IReadOnlyDictionary<int, ObjectValue> ReadExtent(string typeName)
    {
        lock (_sync)
        {
            var map = new Dictionary<int, ObjectValue>();
            if (_doc.Extents.GetValueOrDefault(typeName) is { } pool && _desc.FindType(typeName) is { } type)
                foreach (var (id, entry) in pool)
                    map[id] = BuildObject(entry, type);
            return map;
        }
    }

    public (string TypeName, ObjectValue Fields)? ReadById(int id)
    {
        lock (_sync)
        {
            foreach (var (typeName, pool) in _doc.Extents)
                if (pool.GetValueOrDefault(id) is { } entry && _desc.FindType(typeName) is { } type)
                    return (typeName, BuildObject(entry, type));
            return null;
        }
    }

    // ── non-destructive apply: migrate a data file toward a new schema ──────────────

    // Best-effort, in-place reconciliation of an existing data file TOWARD a new schema — the apply's
    // data-carry step (non-destructive apply). It runs BEFORE the startup guard (which stays STRICT),
    // so the migrated file then passes that guard; a change a slice cannot yet carry is left as-is for
    // the apply's fit check to fall back to a reseed, and a garbage/unreadable file is left untouched.
    //
    //   • Slice 2 — removed field → drop the value: on each extent of a STILL-DECLARED type, remove
    //     stored fields the type no longer declares (the object survives; the orphaned value is pruned).
    //   • Slice 3 — scalar TYPE change → convert the value: a single leaf prop whose stored value is a
    //     leaf of a different base tag is converted to the new type (int→text "3", text "3"→int 3, …).
    //     An UNCONVERTIBLE value (text "abc"→int) is reset to the new type's default and RETURNED in the
    //     report — never silent corruption. Structural changes (leaf↔object/set/dict, a removed type's
    //     extent, a rename) are left for the apply to reseed until later slices carry them.
    // Returns the cells whose value could not be converted and were defaulted (the caller surfaces them).
    public static IReadOnlyList<string> MigrateTowardSchema(string dataPath, InstanceDescription desc)
    {
        var unconvertible = new List<string>();
        StoreDoc doc;
        try { doc = LoadRaw(dataPath); }
        catch (StoredDataException) { return unconvertible; } // unreadable → leave for the caller to reseed

        var changed = false;
        foreach (var (typeName, pool) in doc.Extents)
        {
            if (desc.FindType(typeName) is not { } type) continue; // removed type → leave (→ reseed)
            var props = (type.Props ?? []).ToDictionary(p => p.Name);

            foreach (var (id, obj) in pool)
                foreach (var name in obj.Fields.Keys.ToList())
                {
                    // Removed field → drop the value (slice 2).
                    if (!props.TryGetValue(name, out var prop))
                    {
                        obj.Fields.Remove(name);
                        changed = true;
                        continue;
                    }

                    // Scalar type change → convert (slice 3). Only a single leaf prop whose stored value
                    // is a leaf of a different base tag; a structural change is left for the apply to reseed.
                    if (prop.Cardinality == Cardinality.Single
                        && !desc.IsObjectType(prop.Type)
                        && obj.Fields[name] is StoredLeaf leaf
                        && ScalarTag(leaf.Scalar) != LeafTag(prop.Type, desc))
                    {
                        var converted = ConvertScalar(leaf.Scalar, prop.Type, desc);
                        obj.Fields[name] = new StoredLeaf(converted ?? DefaultBase(LeafBase(prop.Type, desc)));
                        if (converted is null) unconvertible.Add($"{typeName}/{id}.{name}");
                        changed = true;
                    }
                }
        }

        if (changed) SaveRaw(dataPath, doc);
        return unconvertible;
    }

    // ── scalar conversion (type-change migration) ───────────────────────────────

    private static string ScalarTag(NodeValue scalar) => scalar switch
    {
        BoolValue => "bool", IntValue => "int", DecimalValue => "decimal",
        TextValue => "text", DateValue => "date", DateTimeValue => "datetime",
        _ => scalar.GetType().Name,
    };

    // The BaseType a leaf prop's stored value carries (an enum stores as text → BaseType.Enum, whose
    // DefaultBase is empty text), and the on-disk tag that base uses (enum → "text").
    private static BaseType LeafBase(string typeName, InstanceDescription desc) =>
        BaseTypes.IsName(typeName) ? BaseTypes.Parse(typeName)
        : desc.FindType(typeName)?.BaseType ?? BaseType.Text;

    private static string LeafTag(string typeName, InstanceDescription desc)
    {
        var b = LeafBase(typeName, desc);
        return b == BaseType.Enum ? "text" : b.ToString().ToLowerInvariant();
    }

    // Convert a scalar to a leaf type, or null when the value cannot be represented (the caller defaults
    // and reports it — never silent corruption). Widening (int→decimal, anything→text) is lossless;
    // narrowing parses (text→int, decimal→int when whole/in range) and yields null when it cannot.
    private static NodeValue? ConvertScalar(NodeValue from, string toTypeName, InstanceDescription desc)
    {
        if (desc.IsEnumType(toTypeName))
        {
            var s = ScalarToText(from);
            return s != null && desc.EnumAccepts(toTypeName, s) ? new TextValue(s) : null;
        }
        return BaseTypes.Parse(toTypeName) switch
        {
            BaseType.Text     => new TextValue(ScalarToText(from) ?? ""),
            BaseType.Int      => ToInt(from),
            BaseType.Decimal  => ToDecimal(from),
            BaseType.Bool     => ToBool(from),
            BaseType.Date     => ToDate(from),
            BaseType.DateTime => ToDateTime(from),
            _ => null,
        };
    }

    private static string? ScalarToText(NodeValue v) => v switch
    {
        TextValue t      => t.Text,
        IntValue i       => i.Value.ToString(),
        DecimalValue d   => d.Value.ToString(),
        BoolValue b      => b.Value ? "true" : "false",
        DateValue d      => d.Value.ToString("yyyy-MM-dd"),
        DateTimeValue dt => dt.Value.ToString("O"),
        _ => null,
    };

    private static NodeValue? ToInt(NodeValue v) => v switch
    {
        IntValue i => i,
        DecimalValue d when decimal.Truncate(d.Value) == d.Value
                            && d.Value >= int.MinValue && d.Value <= int.MaxValue => new IntValue((int)d.Value),
        TextValue t when int.TryParse(t.Text, out var n) => new IntValue(n),
        _ => null,
    };

    private static NodeValue? ToDecimal(NodeValue v) => v switch
    {
        DecimalValue d => d,
        IntValue i     => new DecimalValue(i.Value),
        TextValue t when decimal.TryParse(t.Text, out var d) => new DecimalValue(d),
        _ => null,
    };

    private static NodeValue? ToBool(NodeValue v) => v switch
    {
        BoolValue b => b,
        TextValue t when bool.TryParse(t.Text, out var b) => new BoolValue(b),
        _ => null,
    };

    private static NodeValue? ToDate(NodeValue v) => v switch
    {
        DateValue d      => d,
        DateTimeValue dt => new DateValue(DateOnly.FromDateTime(dt.Value.DateTime)),
        TextValue t when DateOnly.TryParse(t.Text, out var d) => new DateValue(d),
        _ => null,
    };

    private static NodeValue? ToDateTime(NodeValue v) => v switch
    {
        DateTimeValue dt => dt,
        DateValue d      => new DateTimeValue(new DateTimeOffset(d.Value.ToDateTime(TimeOnly.MinValue))),
        TextValue t when DateTimeOffset.TryParse(t.Text, out var dt) => new DateTimeValue(dt),
        _ => null,
    };

    // Reinitialize the data to the schema's initial document (the initialData seed when
    // the schema carries one, else the default empty root) — in memory and on disk.
    // Used for a FRESH publish (a target with no prior data — apply otherwise PRESERVES
    // existing data) and by tests.
    public void Reset()
    {
        lock (_sync)
        {
            _doc = BuildInitialDoc();
            Save();
        }
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
        lock (_sync)
        {
            if (DictNodeAt(dictPath) is { } existing && existing.Entries.ContainsKey(KeyToString(key)))
                throw new InvalidOperationException(
                    $"An entry with key '{KeyToString(key)}' already exists at {dictPath}.");
            WriteDictionaryEntryInto(dictPath, key, value);
            Save();
        }
    }

    public void WriteDictionaryEntry(NodePath path, NodeValue key, NodeValue value)
    {
        lock (_sync)
        {
            WriteDictionaryEntryInto(path, key, value);
            Save();
        }
    }

    private void WriteDictionaryEntryInto(NodePath path, NodeValue key, NodeValue value)
    {
        var typeInfo = _resolver.ResolveType(path)
            ?? throw new InvalidOperationException($"{path} does not resolve.");
        var dict = EnsureDict(path);

        if (value is ObjectValue obj && typeInfo.Type.BaseType == BaseType.Object)
        {
            var id = MintObject(typeInfo.Type.Name, obj);
            dict.Entries[KeyToString(key)] = new StoredRef(typeInfo.Type.Name, id);
        }
        else
        {
            dict.Entries[KeyToString(key)] = new StoredLeaf(value);
        }
    }

    public void RemoveDictionaryEntry(NodePath path, NodeValue key)
    {
        lock (_sync)
        {
            if (DictNodeAt(path) is { } dict)
                dict.Entries.Remove(KeyToString(key));
            CollectGarbage();
            Save();
        }
    }

    // ── helpers: doc + extents ──────────────────────────────────────────────────

    // Patch in the structural slots a hand-seeded or legacy document may omit.
    private StoreDoc Normalize(StoreDoc doc)
    {
        doc.Extents ??= new();
        doc.Root ??= InitialRootValue();
        return doc;
    }

    // Write-temp-then-move: a reader never sees a half-written file.
    private void Save() => SaveRaw(_filePath, _doc);

    // Serialize a doc to a file atomically (temp-then-move). Shared by the instance save and the
    // static migrate pass.
    private static void SaveRaw(string path, StoreDoc doc)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Opts));
        File.Move(tmp, path, overwrite: true);
    }

    // The object a reference (or the root) points at, via its extent. Null if dangling.
    private StoredObject? ResolveRef(StoredRef objRef) =>
        _doc.Extents.GetValueOrDefault(objRef.TypeName)?.GetValueOrDefault(objRef.Id);

    private StoredObject? ExtentEntryById(int id)
    {
        foreach (var pool in _doc.Extents.Values)
            if (pool.GetValueOrDefault(id) is { } entry)
                return entry;
        return null;
    }

    private int MintObject(string typeName, ObjectValue fields)
    {
        if (!_doc.Extents.TryGetValue(typeName, out var pool))
            _doc.Extents[typeName] = pool = new();
        var id = MintId();
        var type = _desc.FindType(typeName)
            ?? throw new InvalidOperationException($"Unknown type '{typeName}'.");
        pool[id] = new StoredObject(typeName, id, BuildFields(type, fields));
        return id;
    }

    // New object's stored fields: provided scalars, empty collections (each with its
    // own intrinsic id), unset refs.
    private Dictionary<string, StoredValue> BuildFields(TypeDefinition type, ObjectValue provided)
    {
        var fields = new Dictionary<string, StoredValue>();
        foreach (var prop in type.Props ?? [])
        {
            if (prop.Cardinality == Cardinality.Set)
                fields[prop.Name] = new StoredSet(MintId(), new());
            else if (prop.Cardinality == Cardinality.Dictionary)
                fields[prop.Name] = new StoredDict(MintId(), new());
            else if (!_desc.IsObjectType(prop.Type))
                fields[prop.Name] = provided.Fields.TryGetValue(prop.Name, out var v)
                    ? new StoredLeaf(v)
                    : new StoredLeaf(DefaultBase(ResolveTypeDef(prop.Type).BaseType));
            // single object props start unset (absent)
        }
        return fields;
    }

    // The set node at path, creating it (with a fresh intrinsic id) if absent.
    private StoredSet EnsureSet(NodePath path)
    {
        var parent = WalkToObject(ParentPath(path))
            ?? throw new InvalidOperationException($"Parent of {path} is not an object.");
        var field = path.Segments[^1];
        if (parent.Object.Fields.GetValueOrDefault(field) is not StoredSet set)
        {
            set = new StoredSet(MintId(), new());
            parent.Object.Fields[field] = set;
        }
        return set;
    }

    // The dictionary node at path, creating it (with a fresh intrinsic id) if absent.
    private StoredDict EnsureDict(NodePath path)
    {
        var parent = WalkToObject(ParentPath(path))
            ?? throw new InvalidOperationException($"Parent of {path} is not an object.");
        var field = path.Segments[^1];
        if (parent.Object.Fields.GetValueOrDefault(field) is not StoredDict dict)
        {
            dict = new StoredDict(MintId(), new());
            parent.Object.Fields[field] = dict;
        }
        return dict;
    }

    private StoredSet? SetNodeAt(NodePath path)
    {
        var parent = WalkToObject(ParentPath(path));
        return parent?.Object.Fields.GetValueOrDefault(path.Segments[^1]) as StoredSet;
    }

    private StoredDict? DictNodeAt(NodePath path)
    {
        var parent = WalkToObject(ParentPath(path));
        return parent?.Object.Fields.GetValueOrDefault(path.Segments[^1]) as StoredDict;
    }

    // Intrinsic ids come from one global counter shared by objects, sets and dicts, so
    // every mutable thing has a unique stable id. Falls back to the max extent id for a
    // legacy doc with no counter yet.
    private int MintId()
    {
        var next = (_doc.NextId != 0 ? _doc.NextId : ExtentMaxId()) + 1;
        _doc.NextId = next;
        return next;
    }

    private int ExtentMaxId()
    {
        var max = 0;
        foreach (var pool in _doc.Extents.Values)
            foreach (var id in pool.Keys)
                if (id > max) max = id;
        return max;
    }

    // Mark-sweep from the root: any object value reachable is kept; the rest swept. The
    // walk is a typed, exhaustive switch on the value union — never a string-key probe —
    // so a user field / dict key named "type" or "id" can never be mistaken for a tag.
    private void CollectGarbage()
    {
        var visited = new HashSet<int>();

        void Mark(StoredValue? value)
        {
            switch (value)
            {
                case StoredRef r: // a reference (or the root) → resolve its extent entry, mark its fields' values
                    if (visited.Add(r.Id) && ResolveRef(r) is { } entry)
                        foreach (var v in entry.Fields.Values) Mark(v);
                    break;
                case StoredSet s:
                    foreach (var v in s.Members.Values) Mark(v);
                    break;
                case StoredDict d:
                    foreach (var v in d.Entries.Values) Mark(v);
                    break;
                case StoredLeaf:
                    break; // scalar leaves reference nothing
            }
        }

        Mark(_doc.Root);

        foreach (var pool in _doc.Extents.Values)
            foreach (var id in pool.Keys.ToList())
                if (!visited.Contains(id))
                    pool.Remove(id);
    }

    // ── helpers: values ─────────────────────────────────────────────────────────

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
                fields[prop.Name] = new DictionaryValue(0, new Dictionary<NodeValue, NodeValue>()); // template; not stored
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
        // An unset enum field defaults to empty (the decided default — NOT the first value);
        // it stores as text, so the <select> shows its empty option until a value is chosen.
        BaseType.Enum     => new TextValue(""),
        BaseType.Date     => new DateValue(DateOnly.FromDateTime(DateTime.Today)),
        BaseType.DateTime => new DateTimeValue(DateTimeOffset.Now),
        _ => throw new InvalidOperationException($"No base default for {bt}")
    };

    private TypeDefinition ResolveTypeDef(string name) =>
        BaseTypes.IsName(name)
            ? BaseTypes.Leaf(name)
            : _desc.FindType(name) ?? throw new InvalidOperationException($"Unknown type '{name}'.");

    // A set member / set path segment is the member object's intrinsic id.
    private static int ParseSeg(string seg) => int.TryParse(seg, out var n) ? n : -1;

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

    private StoreDoc BuildInitialDoc()
    {
        if (_desc.InitialData?.Extents is { } seed)
            return BuildSeededDoc(seed);

        var doc = new StoreDoc { NextId = 1, Root = InitialRootValue() };
        var db = _desc.Db();
        if (db is { BaseType: BaseType.Object })
        {
            _doc = doc; // MintId / BuildFields read the counter off the live doc
            // The root is id 1; its collection props get ids from the counter (which
            // starts at 1, so they mint 2, 3, …) so every set/dict has a stable id.
            doc.Extents["Db"] = new()
            {
                [1] = new StoredObject("Db", 1, BuildFields(db, new ObjectValue(new Dictionary<string, NodeValue>()))),
            };
        }
        return doc;
    }

    // First-run document from the schema's hand-authored initialData (normalized extents:
    // plain scalars, sets as arrays of member ids, refs as bare ids — already validated by
    // the loader). nextId starts above every authored id, so the set/dict ids minted here,
    // and everything created later, never collide.
    private StoreDoc BuildSeededDoc(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> seed)
    {
        var doc = new StoreDoc();
        _doc = doc; // MintId reads/bumps the live counter while seeding collection ids

        var maxId = 0;
        foreach (var pool in seed.Values)
            foreach (var idText in pool.Keys)
                maxId = Math.Max(maxId, int.Parse(idText));
        doc.NextId = maxId;

        foreach (var (typeName, pool) in seed)
        {
            var type = _desc.FindType(typeName)!;
            var poolDict = new Dictionary<int, StoredObject>();
            doc.Extents[typeName] = poolDict;
            foreach (var (idText, fields) in pool)
            {
                var id = int.Parse(idText);
                poolDict[id] = new StoredObject(typeName, id, SeededFields(type, fields));
            }
        }

        doc.Root = new StoredRef("Db", int.Parse(seed["Db"].Keys.Single()));
        return doc;
    }

    private Dictionary<string, StoredValue> SeededFields(TypeDefinition type, JsonElement fields)
    {
        var result = new Dictionary<string, StoredValue>();
        foreach (var prop in type.Props ?? [])
        {
            var has = fields.TryGetProperty(prop.Name, out var f);

            switch (prop.Cardinality)
            {
                case Cardinality.Set:
                {
                    var members = new Dictionary<int, StoredValue>();
                    if (has)
                        foreach (var m in f.EnumerateArray())
                            members[m.GetInt32()] = new StoredRef(prop.Type, m.GetInt32());
                    result[prop.Name] = new StoredSet(MintId(), members);
                    break;
                }
                case Cardinality.Dictionary:
                    // Seeding dictionary entries: a later slice (dicts are not in the
                    // Code runtime yet); the node still gets its intrinsic id.
                    result[prop.Name] = new StoredDict(MintId(), new());
                    break;
                default:
                    if (_desc.IsObjectType(prop.Type))
                    {
                        if (has) result[prop.Name] = new StoredRef(prop.Type, f.GetInt32());
                        // absent → unset reference
                    }
                    else
                    {
                        var bt = ResolveTypeDef(prop.Type).BaseType;
                        result[prop.Name] = new StoredLeaf(has ? SeededScalar(f, bt) : DefaultBase(bt));
                    }
                    break;
            }
        }
        return result;
    }

    private static NodeValue SeededScalar(JsonElement v, BaseType bt) => bt switch
    {
        BaseType.Bool     => new BoolValue(v.GetBoolean()),
        BaseType.Int      => new IntValue(v.GetInt32()),
        BaseType.Decimal  => new DecimalValue(v.GetDecimal()),
        BaseType.Text     => new TextValue(v.GetString() ?? ""),
        // A seeded enum value is its value name — text-shaped (loader-validated membership).
        BaseType.Enum     => new TextValue(v.GetString() ?? ""),
        BaseType.Date     => new DateValue(DateOnly.Parse(v.GetString() ?? "")),
        BaseType.DateTime => new DateTimeValue(DateTimeOffset.Parse(v.GetString() ?? "")),
        _ => throw new InvalidOperationException($"No scalar seed for {bt}"),
    };

    private StoredValue InitialRootValue()
    {
        var db = _desc.Db();
        return db is { BaseType: BaseType.Object }
            ? new StoredRef("Db", 1)
            : new StoredLeaf(DefaultBase(db?.BaseType ?? BaseType.Bool));
    }
}
