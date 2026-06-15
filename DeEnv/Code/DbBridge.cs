using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Code;

// The boundary between the Code runtime and the M5 store — the *only* place a
// collection changes shape. Loads the persisted object graph (extents, references,
// sets) into runtime ExecObjects/ExecArrays, and converts a transient ExecObject
// back to an ObjectValue when it is persisted.
//
// Object identity is preserved: a set member's intrinsic id becomes both the
// ExecObject.Id and the ExecItem.Key (identity-keyed), and an object reached by
// two references resolves to one ExecObject (shared via the `loaded` map).
//
// Dictionaries are not yet loaded (later slice); scalars, sets, and single object
// references are.
public static class DbBridge
{
    // M5 seeds the Db root object at extent id 1. Public so a host action can recognise the
    // root schema object the designer passes to sys.create / sys.publish (`db`, id 1).
    public const int RootId = 1;

    // The reserved field a dictionary entry object carries its key in (the `__descs`
    // convention). The stdlib reads field(entry, "__key"); descriptors exclude it.
    public const string EntryKeyProp = "__key";

    // Load the Db root object graph as a runtime ExecObject (positive id ⇒ in db).
    public static ExecObject LoadRoot(IInstanceStore store, InstanceDescription desc, ExecContext context)
    {
        var db = desc.Db() ?? throw new InvalidOperationException("No Db type in the schema.");
        if (store.ReadNode(NodePath.Root) is not ObjectValue root)
            throw new InvalidOperationException("Db root is not an object.");
        var loaded = new Dictionary<int, ExecObject>();
        return LoadObject(root, db, RootId, NodePath.Root, store, desc, loaded, context);
    }

    private static ExecObject LoadObject(
        ObjectValue ov, TypeDefinition type, int id, NodePath path,
        IInstanceStore store, InstanceDescription desc,
        Dictionary<int, ExecObject> loaded, ExecContext context)
    {
        if (loaded.TryGetValue(id, out var existing)) return existing;

        var obj = new ExecObject { Props = [], Id = id, TypeName = type.Name };
        loaded[id] = obj;

        foreach (var prop in type.Props ?? [])
        {
            var fieldPath = path.Field(prop.Name);
            var elemType = ResolveType(prop.Type, desc);

            switch (prop.Cardinality)
            {
                case Cardinality.Set:
                {
                    var items = new List<ExecItem>();
                    var setId = 0;
                    if (ov.Fields.TryGetValue(prop.Name, out var f) && f is SetValue set)
                    {
                        setId = set.Id; // the set's stored intrinsic id — stable across renders
                        foreach (var (memberId, memberVal) in set.Members)
                            if (memberVal is ObjectValue memberOv)
                                items.Add(new ExecItem
                                {
                                    Key = memberId,
                                    Value = LoadObject(memberOv, elemType!, memberId,
                                        fieldPath.Key(memberId.ToString()), store, desc, loaded, context),
                                });
                    }
                    obj.Props[prop.Name] = new ExecArray
                    {
                        Items = items,
                        Id = setId,
                        Kind = ArrayKind.Set,
                        ElementTypeName = elemType!.Name,
                    };
                    break;
                }

                case Cardinality.Dictionary:
                {
                    // A dictionary surfaces as a Kind=Dict ExecArray of ENTRY objects, each
                    // carrying its key in a reserved `__key` field (so the stdlib reads it with
                    // field(entry,"__key") — no foreach-key syntax). An object entry is its
                    // scalar fields + __key; a scalar entry is a synthesized { __key, value }.
                    // Entries are display rows here (id stable-by-key, negative = not directly
                    // addressable); editing an entry happens on its own page. The dict carries
                    // its SourcePath — it persists through the PATH-addressed add/remove ops.
                    var items = new List<ExecItem>();
                    var dictId = 0;
                    if (ov.Fields.TryGetValue(prop.Name, out var f) && f is DictionaryValue dict)
                    {
                        dictId = dict.Id;
                        var elementIsObject = desc.IsObjectType(prop.Type);
                        foreach (var (keyVal, entryVal) in dict.Entries)
                        {
                            var keyText = KeyText(keyVal);
                            var entry = new ExecObject
                            {
                                Props = [], Id = KeyHash(dictId, keyText), TypeName = elemType!.Name,
                                // The entry's own node path — its fields persist path-addressed
                                // (a dict entry has no extent id). A scalar entry's value is AT
                                // this path; an object entry's fields hang under it.
                                SourcePath = fieldPath.Key(keyText).ToString(),
                                ScalarEntry = !elementIsObject,
                            };
                            if (elementIsObject && entryVal is ObjectValue entryOv)
                            {
                                foreach (var (n, v) in entryOv.Fields)
                                    if (v is IntValue or TextValue or BoolValue or DecimalValue or DateValue or DateTimeValue)
                                        entry.Props[n] = ScalarToExec(v);
                            }
                            else if (!elementIsObject)
                            {
                                entry.Props["value"] = ScalarToExec(entryVal);
                            }
                            entry.Props[EntryKeyProp] = new ExecText { Value = keyText };
                            items.Add(new ExecItem { Key = entry.Id, Value = entry });
                        }
                    }
                    obj.Props[prop.Name] = new ExecArray
                    {
                        Items = items,
                        Id = dictId,
                        Kind = ArrayKind.Dict,
                        ElementTypeName = elemType!.Name,
                        SourcePath = fieldPath.ToString(),
                    };
                    break;
                }

                default:
                    if (desc.IsObjectType(prop.Type))
                    {
                        // Single object-typed prop: a reference into the extent.
                        if (ov.Fields.TryGetValue(prop.Name, out var f)
                            && f is ReferenceValue { TargetId: int targetId }
                            && store.ReadById(targetId) is { } hit)
                        {
                            obj.Props[prop.Name] = LoadObject(hit.Fields,
                                ResolveType(hit.TypeName, desc)!, targetId, fieldPath,
                                store, desc, loaded, context);
                        }
                        else
                        {
                            obj.Props[prop.Name] = new ExecNull();
                        }
                    }
                    else
                    {
                        obj.Props[prop.Name] = ScalarToExec(
                            ov.Fields.GetValueOrDefault(prop.Name));
                    }
                    break;
            }
        }

        return obj;
    }

    // All objects of a type's extent as a transient list of SCALAR-ONLY ExecObjects
    // (id + scalar props). Enough for the reference picker's candidate list — option
    // label (a text prop) + id. Their object/set/dict props are omitted (the full object
    // loads via the graph after a reference is set). Memoized by the caller.
    public static ExecArray LoadExtent(IInstanceStore store, string typeName, ExecContext context)
    {
        var items = new List<ExecItem>();
        foreach (var (id, ov) in store.ReadExtent(typeName))
        {
            var obj = new ExecObject { Props = [], Id = id, TypeName = typeName };
            foreach (var (name, v) in ov.Fields)
                if (v is IntValue or TextValue or BoolValue or DecimalValue or DateValue or DateTimeValue)
                    obj.Props[name] = ScalarToExec(v);
            items.Add(new ExecItem { Key = id, Value = obj });
        }
        return new ExecArray
        {
            Items = items,
            Id = --context.LastId.Value,
            Kind = ArrayKind.List,
            ElementTypeName = typeName,
        };
    }

    // Convert a transient ExecObject's scalar props to an ObjectValue for CreateObject.
    // (Collection/reference props are created empty by the store — see BuildFieldsJson.)
    public static ObjectValue ToObjectValue(ExecObject obj)
    {
        var fields = new Dictionary<string, NodeValue>();
        foreach (var (name, value) in obj.Props)
        {
            var node = ExecToScalar(value);
            if (node != null) fields[name] = node;
        }
        return new ObjectValue(fields);
    }

    private static NodeValue? ExecToScalar(IExecValue value) => value switch
    {
        ExecInt i  => new IntValue(i.Value),
        ExecText t => new TextValue(t.Value),
        ExecBool b => new BoolValue(b.Value),
        _ => null, // objects/arrays/refs are not scalar fields
    };

    public static IExecValue ScalarToExec(NodeValue? value) => value switch
    {
        IntValue i      => new ExecInt { Value = i.Value },
        TextValue t     => new ExecText { Value = t.Text },
        BoolValue b     => new ExecBool { Value = b.Value },
        DecimalValue d  => new ExecText { Value = d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }, // no decimal exec value yet
        DateValue d     => new ExecText { Value = d.Value.ToString("yyyy-MM-dd") },
        DateTimeValue t => new ExecText { Value = t.Value.ToString("O") },
        _ => new ExecNull(),
    };

    // A dictionary key's string form (matches the store's KeyToString / WsHandler.KeyString).
    private static string KeyText(NodeValue key) => key switch
    {
        IntValue i  => i.Value.ToString(),
        TextValue t => t.Text,
        BoolValue b => b.Value.ToString().ToLowerInvariant(),
        _ => key.ToString() ?? "",
    };

    // A stable negative id for a dict entry, derived from (dict id, key) so it survives
    // re-renders (DOM reconciliation keys off it) without colliding with counter-minted
    // transients. Negative = a display row not directly addressable by intrinsic id.
    private static int KeyHash(int dictId, string keyText)
    {
        unchecked
        {
            var h = 2166136261u; // FNV-1a
            foreach (var c in $"{dictId}/{keyText}") { h ^= c; h *= 16777619u; }
            return -(int)(h & 0x3FFFFFFF) - 1; // keep it negative, away from 0
        }
    }

    private static TypeDefinition? ResolveType(string name, InstanceDescription desc) =>
        desc.FindType(name) ?? BaseTypes.Leaf(name);
}
