using System.Text.Json.Nodes;
using DeEnv.Instance;

namespace DeEnv.Code;

// The data-transfer boundary: serializes the rendered top scope into the client's
// first-paint state (ServerDtState — see DeEnv/Instance/dt.ts), shipping ONLY what
// the client-run render actually read. Two rules enforce secure-by-default transfer:
//
//   • Access-scoped — an object ships only the props the render accessed, an array
//     only the items it iterated. Data touched solely by server-side computations
//     (var initializers / server-only functions, where access recording is
//     suppressed) never appears, so row- and field-level scoping fall out for free.
//   • Sensitive-denied — a field marked `sensitive` is never serialized. Because
//     server-side accesses are suppressed, a *recorded* access of a sensitive field
//     means client-run code read it directly — an invariant violation, surfaced as
//     an error rather than a silent leak.
//
//   { objects: { "<id>": { isInDb, props: { <name>: DtValue } } },
//     arrays:  { "<id>": { isInDb, items: [ { id, value: DtValue } ] } },
//     scope:   { "<key>": { isReadOnly, value: DtValue } } }
public static class ClientState
{
    public static JsonObject Serialize(ExecScope topScope, ExecContext context, InstanceDescription desc)
    {
        // Recorded (client-run) accesses, indexed for lookup.
        var accessedProps = new Dictionary<ExecObject, HashSet<string>>();
        foreach (var (obj, name) in context.AccessedObjectProps)
            if (name != null) (accessedProps.TryGetValue(obj, out var s) ? s : accessedProps[obj] = []).Add(name);

        // Items are tracked by their own identity, not per-array: a foreach over a
        // derived collection (where/orderBy) iterates the SAME item instances as the
        // source set, so the source array ships exactly the items that were rendered.
        var accessedItems = new HashSet<ExecArrayItem>();
        foreach (var (_, item) in context.AccessedArrayItems)
            if (item != null) accessedItems.Add(item);

        var objects = new JsonObject();
        var arrays = new JsonObject();
        var scope = new JsonObject();
        var seenObjects = new HashSet<int>();
        var seenArrays = new HashSet<int>();

        JsonObject Simple(JsonObject value) => new() { ["type"] = "simple", ["value"] = value };

        JsonObject DtValue(IExecValue value) => value switch
        {
            ExecObject o => ObjectRef(o),
            ExecArray a => ArrayRef(a),
            ExecInt i => Simple(new JsonObject { ["type"] = "int", ["value"] = i.Value }),
            ExecBool b => Simple(new JsonObject { ["type"] = "bool", ["value"] = b.Value }),
            ExecText t => Simple(new JsonObject { ["type"] = "text", ["value"] = t.Value }),
            _ => Simple(new JsonObject { ["type"] = "null" }),
        };

        JsonObject ObjectRef(ExecObject o)
        {
            if (seenObjects.Add(o.Id))
            {
                var props = new JsonObject();
                objects[o.Id.ToString()] = new JsonObject { ["isInDb"] = o.IsInDb, ["props"] = props };
                if (accessedProps.TryGetValue(o, out var names))
                    foreach (var name in names)
                    {
                        if (IsSensitive(o.TypeName, name))
                            throw new CodeRuntimeException(
                                $"Render reads sensitive field '{o.TypeName}.{name}' on the client. " +
                                $"Move it behind a server-only computation that ships only its result.");
                        if (o.Props.TryGetValue(name, out var pv)) props[name] = DtValue(pv);
                    }
            }
            return new JsonObject { ["type"] = "object", ["id"] = o.Id };
        }

        JsonObject ArrayRef(ExecArray a)
        {
            if (seenArrays.Add(a.Id))
            {
                var items = new JsonArray();
                arrays[a.Id.ToString()] = new JsonObject { ["isInDb"] = a.IsInDb, ["items"] = items };
                foreach (var item in a.Items)
                    if (accessedItems.Contains(item))
                        items.Add(new JsonObject { ["id"] = item.Id, ["value"] = DtValue(item.Value) });
            }
            return new JsonObject { ["type"] = "array", ["id"] = a.Id };
        }

        bool IsSensitive(string? typeName, string propName) =>
            typeName != null && desc.FindType(typeName)?.Props?.FirstOrDefault(p => p.Name == propName)?.Sensitive == true;

        foreach (var (key, item) in topScope.Items)
        {
            if (item.Value is ExecFunction) continue; // functions are re-defined from initUi
            scope[key] = new JsonObject { ["isReadOnly"] = item.IsReadOnly, ["value"] = DtValue(item.Value) };
        }

        return new JsonObject { ["objects"] = objects, ["arrays"] = arrays, ["scope"] = scope };
    }
}
