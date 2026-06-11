using System.Text.Json.Nodes;

namespace DeEnv.Code;

// The data-transfer boundary (Stage 4). Serializes a render's first-paint state:
//
//   • leaves  — the data the render DISPLAYED: every object/array it accessed in an
//     output position (recorded while the DepStack was empty), with the props/items it
//     read. Data read only inside a computation is a dependency, not a leaf, so it is
//     never here → private by construction (no flag).
//   • scope   — the top-scope vars/db, as references into leaves.
//   • cache   — the memoized computations: each `{ key, result, deps }`. The client
//     reuses results and invalidates them by dependency (refs, never values).
//
// See MemoCache.cs / MEMO_CACHE_DESIGN.md.
public static class ClientState
{
    public static JsonObject Serialize(ExecScope topScope, ExecContext context)
    {
        var accessedProps = new Dictionary<ExecObject, HashSet<string>>();
        foreach (var (obj, name) in context.AccessedObjectProps)
            if (name != null) (accessedProps.TryGetValue(obj, out var s) ? s : accessedProps[obj] = []).Add(name);

        // Items by identity: a foreach over a derived collection iterates the SAME item
        // instances as the source set, so the source array ships exactly what was shown.
        var accessedItems = new HashSet<ExecItem>();
        foreach (var (_, item) in context.AccessedItems)
            if (item != null) accessedItems.Add(item);

        var objects = new JsonObject();
        var arrays = new JsonObject();
        var seenObjects = new HashSet<int>();
        var seenArrays = new HashSet<int>();

        JsonObject Simple(JsonObject value) => new() { ["type"] = "simple", ["value"] = value };

        JsonObject DtValue(IExecValue value) => value switch
        {
            ExecObject o => ObjectRef(o),
            ExecArray a => CollectionRef(a),
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
                objects[o.Id.ToString()] = new JsonObject { ["props"] = props };
                if (accessedProps.TryGetValue(o, out var names))
                    foreach (var name in names)
                        if (o.Props.TryGetValue(name, out var pv)) props[name] = DtValue(pv);
            }
            return new JsonObject { ["type"] = "object", ["id"] = o.Id };
        }

        JsonObject CollectionRef(ExecArray a)
        {
            if (seenArrays.Add(a.Id))
            {
                var items = new JsonArray();
                arrays[a.Id.ToString()] = new JsonObject
                {
                    ["kind"] = a.Kind.ToString().ToLowerInvariant(),
                    ["elementTypeName"] = a.ElementTypeName,
                    ["items"] = items,
                };
                foreach (var item in a.Items)
                    if (accessedItems.Contains(item))
                        items.Add(new JsonObject { ["key"] = item.Key, ["value"] = DtValue(item.Value) });
            }
            return new JsonObject { ["type"] = "array", ["id"] = a.Id };
        }

        // Register every accessed object/collection as a leaf (incl. derived lists reached
        // only via a cache result, not from the scope).
        foreach (var o in accessedProps.Keys) ObjectRef(o);
        foreach (var (coll, item) in context.AccessedItems)
            if (item != null) CollectionRef(coll);

        var scope = new JsonObject();
        foreach (var (key, item) in topScope.Items)
        {
            if (item.Value is ExecFunction) continue; // functions come from initUi
            scope[key] = new JsonObject { ["isReadOnly"] = item.IsReadOnly, ["value"] = DtValue(item.Value) };
        }

        var cache = new JsonArray();
        foreach (var (_, entry) in context.Memo)
        {
            // A tag- or function-valued result (a page fn's rendered tree) has no wire
            // form; the client recomputes it from the shipped data on first render.
            if (entry.Result is ExecTag or ExecFunction or ExecSysFunction or ExecNothing) continue;

            var props = new JsonArray();
            foreach (var p in entry.Deps.Props) props.Add(new JsonObject { ["obj"] = p.ObjectId, ["prop"] = p.Prop });
            var members = new JsonArray();
            foreach (var m in entry.Deps.Members) members.Add(m.CollectionId);
            var vars = new JsonArray();
            foreach (var v in entry.Deps.Vars) vars.Add(v.Name);

            cache.Add(new JsonObject
            {
                ["key"] = entry.Key,
                ["result"] = DtValue(entry.Result),
                ["deps"] = new JsonObject { ["props"] = props, ["members"] = members, ["vars"] = vars },
            });
        }

        return new JsonObject
        {
            ["leaves"] = new JsonObject { ["objects"] = objects, ["arrays"] = arrays },
            ["scope"] = scope,
            ["cache"] = cache,
        };
    }
}
