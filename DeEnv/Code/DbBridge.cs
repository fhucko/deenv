using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Code;

// The boundary between the Code runtime and the M5 store. Loads the persisted
// object graph (extents, references, sets) into runtime ExecObjects/ExecArrays,
// and converts a transient ExecObject back to an ObjectValue when it is persisted.
//
// Object identity is preserved: a set member's intrinsic id becomes both the
// ExecObject.Id and the ExecArrayItem.Id (identity-keyed), and an object reached
// by two references resolves to one ExecObject (shared via the `loaded` map).
//
// Dictionaries are not yet loaded (later slice); scalars, sets, and single object
// references are.
public static class DbBridge
{
    private const int RootId = 1; // M5 seeds the Db root object at extent id 1.

    // Load the Db root object graph as a runtime ExecObject (IsInDb = true).
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

        var obj = new ExecObject { Props = [], Id = id, TypeName = type.Name, IsInDb = true };
        loaded[id] = obj;

        foreach (var prop in type.Props ?? [])
        {
            var fieldPath = path.Field(prop.Name);
            var elemType = ResolveType(prop.Type, desc);

            switch (prop.Cardinality)
            {
                case Cardinality.Set:
                {
                    var items = new List<ExecArrayItem>();
                    var setId = 0;
                    if (ov.Fields.TryGetValue(prop.Name, out var f) && f is SetValue set)
                    {
                        setId = set.Id; // the set's stored intrinsic id — stable across renders
                        foreach (var (memberId, memberVal) in set.Members)
                            if (memberVal is ObjectValue memberOv)
                                items.Add(new ExecArrayItem
                                {
                                    Id = memberId,
                                    Value = LoadObject(memberOv, elemType!, memberId,
                                        fieldPath.Key(memberId.ToString()), store, desc, loaded, context),
                                });
                    }
                    obj.Props[prop.Name] = new ExecArray
                    {
                        Items = items,
                        Id = setId,
                        IsInDb = true,
                        Path = fieldPath,
                        ElementTypeName = elemType!.Name,
                    };
                    break;
                }

                case Cardinality.Dictionary:
                    // Not yet surfaced to the runtime (later slice).
                    break;

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

    private static IExecValue ScalarToExec(NodeValue? value) => value switch
    {
        IntValue i      => new ExecInt { Value = i.Value },
        TextValue t     => new ExecText { Value = t.Text },
        BoolValue b     => new ExecBool { Value = b.Value },
        DecimalValue d  => new ExecText { Value = d.Value.ToString() }, // no decimal exec value yet
        DateValue d     => new ExecText { Value = d.Value.ToString("yyyy-MM-dd") },
        DateTimeValue t => new ExecText { Value = t.Value.ToString("O") },
        _ => new ExecNull(),
    };

    private static TypeDefinition? ResolveType(string name, InstanceDescription desc) =>
        desc.FindType(name) ?? BaseTypes.Leaf(name);
}
