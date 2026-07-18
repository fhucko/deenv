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
        var collections = new JsonObject();
        var seenObjects = new HashSet<int>();
        var seenCollections = new HashSet<int>();

        JsonObject Simple(JsonObject value) => new() { ["type"] = "simple", ["value"] = value };

        JsonObject DtValue(IExecValue value) => value switch
        {
            ExecObject o => ObjectRef(o),
            IExecCollection a => CollectionRef(a),
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
                var entry = new JsonObject { ["props"] = props };
                // A dict entry carries its path so a bound field edit persists path-addressed.
                if (o.SourcePath is { } sp)
                {
                    entry["sourcePath"] = sp;
                    entry["scalarEntry"] = o.ScalarEntry;
                }
                // R7 dict addressing for id-based dictAdd/dictRemove.
                if (o.OwnerRef is { } oRef) entry["ownerRef"] = oRef;
                if (o.DictProp is { } oProp) entry["dictProp"] = oProp;
                if (o.Key is { } oKey) entry["key"] = oKey;
                objects[o.Id.ToString()] = entry;
                // Two ship-WHOLE cases, both shipping every prop:
                //   • a Constant (a sys.schema descriptor): provably user-data-free, and its consumer
                //     walks the whole tree — its children are Constant too, so the recursion ships the
                //     nested props/values arrays full (fixes the empty-descriptor-array bug);
                //   • a NEGATIVE-id transient: the client's own draft state — it owns it, a refetch
                //     never refreshes it (the merge skips draft vars), so a withheld prop could never
                //     arrive later. (Pre-dates the descriptor work; an established rule.)
                // Everything else (a positive-id db object) ships only its ACCESSED props — privacy by
                // construction. A non-Constant negative-id object that nests a where/orderBy/literal
                // collection therefore does NOT ship that collection whole: it stays access-scoped below.
                if (o.Constant || o.Id < 0)
                {
                    foreach (var (name, pv) in o.Props) props[name] = DtValue(pv);
                }
                else if (accessedProps.TryGetValue(o, out var names))
                {
                    foreach (var name in names)
                        if (o.Props.TryGetValue(name, out var pv)) props[name] = DtValue(pv);
                }
            }
            return new JsonObject { ["type"] = "object", ["id"] = o.Id };
        }

        static string CollectionTypeTag(IExecCollection a) => a switch
        {
            ExecSet => "set",
            ExecDict => "dict",
            ExecList => "list",
            _ => throw new InvalidOperationException($"Unknown collection type {a.GetType().Name}."),
        };

        JsonObject CollectionRef(IExecCollection a)
        {
            if (seenCollections.Add(a.Id))
            {
                var items = new JsonArray();
                var typeTag = CollectionTypeTag(a);
                var entry = new JsonObject
                {
                    ["type"] = typeTag,
                    ["elementTypeName"] = a.ElementTypeName,
                    ["items"] = items,
                };
                // Dicts persist via path (add/removeEntry) and carry R7 owner addressing.
                if (a is ExecDict dict)
                {
                    entry["sourcePath"] = dict.SourcePath;
                    if (dict.OwnerRef is { } arrRef) entry["ownerRef"] = arrRef;
                    if (dict.DictProp is { } arrProp) entry["dictProp"] = arrProp;
                }
                collections[a.Id.ToString()] = entry;
                // A Constant collection (a descriptor's prop list — props/values/valueProps) ships ALL its
                // items; its element objects are Constant too, so they recurse whole. Otherwise only the
                // DISPLAYED items ship — so a where/orderBy result or array literal (negative-id, NOT
                // Constant) never spills its undisplayed membership, even when nested in a shipped
                // (negative-id) object. Privacy stays STRUCTURAL: what ships is what was accessed.
                foreach (var item in a.Items)
                    if (a.Constant || accessedItems.Contains(item))
                        items.Add(new JsonObject { ["key"] = item.Key, ["value"] = DtValue(item.Value) });
            }
            return new JsonObject { ["type"] = CollectionTypeTag(a), ["id"] = a.Id };
        }

        // Register every accessed object/collection as a leaf (incl. derived lists reached
        // only via a cache result, not from the scope).
        foreach (var o in accessedProps.Keys) ObjectRef(o);
        foreach (var (coll, item) in context.AccessedItems)
            if (item != null) CollectionRef(coll);

        // Ship the render scope AND its parents flat (a custom render walks app → lib → system;
        // a generic view lib → system) — the client rebuilds a single scope; resolution is by
        // name, so the system/lib/app split need not survive the wire. Library functions are
        // skipped (they ride initUi); the first-writer-wins skip below means an app var correctly
        // shadows a lib/system var of the same name on the wire too.
        var scope = new JsonObject();
        for (var s = topScope; s != null; s = s.Parent)
            foreach (var (key, item) in s.Items)
            {
                if (item.Value is ExecFunction || scope.ContainsKey(key)) continue; // fns come from initUi
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
            ["leaves"] = new JsonObject { ["objects"] = objects, ["collections"] = collections },
            ["scope"] = scope,
            ["cache"] = cache,
        };
    }
}
