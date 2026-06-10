using System.Text.Json.Nodes;

namespace DeEnv.Code;

// Serializes a rendered top scope (the db object graph + ui/session vars) into the
// client's data-transfer shape (ServerDtState — see DeEnv/Instance/dt.ts) for the
// first-paint window.initData. Functions are omitted: the client re-defines them
// from window.initUi (the AST). This is a FULL transfer; the partial, ExecContext-
// scoped version is Stage 4.
//
//   { objects: { "<id>": { isInDb, props: { <name>: DtValue } } },
//     arrays:  { "<id>": { isInDb, items: [ { id, value: DtValue } ] } },
//     scope:   { "<key>": { isReadOnly, value: DtValue } } }
public static class ClientState
{
    public static JsonObject Serialize(ExecScope topScope)
    {
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
                // Register the entry before recursing so a reference cycle terminates.
                objects[o.Id.ToString()] = new JsonObject { ["isInDb"] = o.IsInDb, ["props"] = props };
                foreach (var (name, propValue) in o.Props) props[name] = DtValue(propValue);
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
                    items.Add(new JsonObject { ["id"] = item.Id, ["value"] = DtValue(item.Value) });
            }
            return new JsonObject { ["type"] = "array", ["id"] = a.Id };
        }

        foreach (var (key, item) in topScope.Items)
        {
            if (item.Value is ExecFunction) continue; // functions are re-defined from initUi
            scope[key] = new JsonObject { ["isReadOnly"] = item.IsReadOnly, ["value"] = DtValue(item.Value) };
        }

        return new JsonObject { ["objects"] = objects, ["arrays"] = arrays, ["scope"] = scope };
    }
}
