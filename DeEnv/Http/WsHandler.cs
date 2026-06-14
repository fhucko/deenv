using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Http;

// Transport-agnostic WebSocket message dispatcher.
// One incoming JSON message → one outgoing JSON response (request/response model).
// The transport (GenHTTP websocket) calls ProcessMessage and sends the result.
public sealed class WsHandler
{
    private readonly IInstanceStore _store;
    private readonly InstanceDescription _desc;
    private readonly TypeResolver _resolver;
    private readonly ClientSessionStore? _sessions;
    private readonly LiveRegistry _registry;
    private readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public WsHandler(IInstanceStore store, InstanceDescription desc, ClientSessionStore? sessions = null,
        LiveRegistry? registry = null)
    {
        _store = store;
        _desc = desc;
        _resolver = new TypeResolver(desc);
        _sessions = sessions;
        _registry = registry ?? new LiveRegistry();
    }

    // The warm per-client session a code-UI message addresses (clientId minted at SSR).
    private ClientSession? Session(JsonElement root) =>
        _sessions != null && root.TryGetProperty("clientId", out var c) && c.GetString() is { } id
            ? _sessions.Get(id) : null;

    // ── message dispatch ──────────────────────────────────────────────────────

    public string ProcessMessage(string json)
    {
        int? id = null;
        var op = "?";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var ide) && ide.ValueKind == JsonValueKind.Number)
                id = ide.GetInt32();

            op = root.GetProperty("op").GetString() ?? "";
            var pathStr = root.TryGetProperty("path", out var pe) ? pe.GetString() ?? "/" : "/";
            var path = ParsePath(pathStr);

            var result = op switch
            {
                "write"            => HandleWrite(path, pathStr, root),
                "addEntry"         => HandleAddEntry(path, pathStr, root),
                "removeEntry"      => HandleRemoveEntry(path, pathStr, root),
                "hello"            => HandleHello(root),
                "objectPropChange" => HandleObjectPropChange(root),
                "setReferenceField" => HandleSetReferenceField(root),
                "arrayAdd"         => HandleArrayAdd(root),
                "arrayRemove"      => HandleArrayRemove(root),
                "refetch"          => HandleRefetch(pathStr, root),
                _                  => Error($"Unknown op '{op}'")
            };
            return WithId(result, id);
        }
        catch (Exception ex)
        {
            // The reply carries the reason to the client (reject → rollback); the log
            // is the server-side record of it.
            Console.Error.WriteLine($"WS '{op}' failed: {ex.Message}");
            return WithId(Error(ex.Message), id);
        }
    }

    // Echo the request's correlation id back so the client can match reply→request.
    private string WithId(string json, int? id)
    {
        if (id is null) return json;
        var node = JsonNode.Parse(json)!;
        node["id"] = id.Value;
        return node.ToJsonString(_jsonOpts);
    }

    // ── write ─────────────────────────────────────────────────────────────────

    private string HandleWrite(NodePath path, string pathStr, JsonElement root)
    {
        var typeInfo = _resolver.ResolveType(path);
        if (typeInfo == null)
            return Error($"Path '{pathStr}' does not resolve.");

        if (!root.TryGetProperty("value", out var valEl))
            return Error("Missing 'value' in write message.");

        var value = DeserializeLeaf(valEl, typeInfo.Type);

        // A SCALAR dictionary entry's value lives at its path but is addressed by (dict, key)
        // — WriteLeaf can't walk into a dict, so upsert the entry. (An OBJECT entry's field
        // path, e.g. /customers/42/name, has an object parent and writes through WriteLeaf.)
        if (!path.IsRoot
            && _resolver.ResolveType(NodePath.FromSegments(path.Segments.Take(path.Segments.Count - 1)))
               is { Cardinality: Cardinality.Dictionary } parentInfo)
        {
            var dictPath = NodePath.FromSegments(path.Segments.Take(path.Segments.Count - 1));
            _store.WriteDictionaryEntry(dictPath, ParseKey(path.Segments[^1], parentInfo.KeyTypeName ?? "text"), value);
        }
        else
        {
            _store.WriteLeaf(path, value);
        }

        var response = new JsonObject { ["op"] = "write", ["path"] = pathStr, ["ok"] = true };
        return response.ToJsonString(_jsonOpts);
    }

    // ── addEntry (create on the create-form Save) ──────────────────────────────

    private string HandleAddEntry(NodePath path, string pathStr, JsonElement root)
    {
        var typeInfo = _resolver.ResolveType(path);
        if (typeInfo == null)
            return Error($"Path '{pathStr}' does not resolve.");

        if (typeInfo.Cardinality == Cardinality.Set)
            return HandleAddSetMember(path, pathStr, typeInfo, root);

        if (typeInfo.Cardinality != Cardinality.Dictionary)
            return Error($"Path '{pathStr}' is not a dictionary.");

        if (!root.TryGetProperty("value", out var valueEl))
            return Error("Missing 'value' in addEntry message.");
        if (!root.TryGetProperty("key", out var keyEl) || keyEl.GetString() is not { } keyStr || keyStr.Length == 0)
            return Error("A dictionary entry requires a non-empty 'key'.");

        var value = DeserializeValue(valueEl, typeInfo.Type);
        var key = ParseKey(keyStr, typeInfo.KeyTypeName ?? "text");
        _store.CreateEntry(path, key, value); // throws on duplicate → caught as { error }

        var response = new JsonObject
        {
            ["op"]   = "addEntry",
            ["path"] = pathStr,
            ["ok"]   = true,
            ["key"]  = KeyString(key)
        };
        return response.ToJsonString(_jsonOpts);
    }

    // ── removeEntry ────────────────────────────────────────────────────────────

    private string HandleRemoveEntry(NodePath path, string pathStr, JsonElement root)
    {
        var typeInfo = _resolver.ResolveType(path);
        if (typeInfo == null)
            return Error($"Path '{pathStr}' does not resolve.");
        if (!root.TryGetProperty("key", out var keyEl) || keyEl.GetString() is not { } keyStr)
            return Error("Missing 'key' in removeEntry message.");

        if (typeInfo.Cardinality == Cardinality.Set)
        {
            if (!int.TryParse(keyStr, out var memberId))
                return Error("Set member key must be an integer identity.");
            _store.RemoveFromSet(path, memberId);
        }
        else if (typeInfo.Cardinality == Cardinality.Dictionary)
        {
            _store.RemoveDictionaryEntry(path, ParseKey(keyStr, typeInfo.KeyTypeName ?? "text"));
        }
        else
        {
            return Error($"Path '{pathStr}' is not a dictionary or set.");
        }

        var response = new JsonObject { ["op"] = "removeEntry", ["path"] = pathStr, ["ok"] = true };
        return response.ToJsonString(_jsonOpts);
    }

    // ── set members + references (object model) ─────────────────────────────────

    private string HandleAddSetMember(NodePath path, string pathStr, ResolvedTypeInfo typeInfo, JsonElement root)
    {
        int id;
        if (root.TryGetProperty("refId", out var refEl) && refEl.ValueKind == JsonValueKind.Number)
        {
            id = refEl.GetInt32();
            _store.AddToSet(path, id); // link an existing object
        }
        else if (root.TryGetProperty("value", out var valueEl))
        {
            var obj = (ObjectValue)DeserializeValue(valueEl, typeInfo.Type);
            id = _store.CreateObject(typeInfo.Type.Name, obj); // mint a new object…
            _store.AddToSet(path, id);                         // …then link it
        }
        else
        {
            return Error("addEntry on a set requires 'refId' (existing) or 'value' (new).");
        }

        var response = new JsonObject
        {
            ["op"]  = "addEntry",
            ["path"] = pathStr,
            ["ok"]  = true,
            ["key"] = id.ToString()
        };
        return response.ToJsonString(_jsonOpts);
    }

    // ── code-owned UI mutations (the Code runtime, identity-addressed) ──────────

    // A two-way-bound prop write from the client: persist a single leaf field on the
    // object with this intrinsic id, after validating it against the schema — the
    // object's type must declare the prop as a single scalar field, and the value
    // must fit its declared base type. (The client already applied the change
    // optimistically; a reject rolls it back.)
    private string HandleObjectPropChange(JsonElement root)
    {
        if (!root.TryGetProperty("objectId", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            return Error("objectPropChange requires a numeric 'objectId'.");
        if (!root.TryGetProperty("prop", out var propEl) || propEl.GetString() is not { } prop)
            return Error("objectPropChange requires 'prop'.");
        if (!root.TryGetProperty("value", out var valEl))
            return Error("objectPropChange requires 'value'.");

        var objectId = idEl.GetInt32();
        if (_store.ReadById(objectId) is not { } hit)
            return Error($"No object with id {objectId}.");
        var propDef = _desc.FindType(hit.TypeName)?.Props?.FirstOrDefault(p => p.Name == prop);
        if (propDef is null)
            return Error($"Type '{hit.TypeName}' has no field '{prop}'.");
        if (propDef.Cardinality != Cardinality.Single || _desc.IsObjectType(propDef.Type))
            return Error($"Field '{prop}' on '{hit.TypeName}' is not a scalar field.");

        _store.WriteField(objectId, prop, LeafForType(valEl, BaseTypes.Parse(propDef.Type)));

        var response = new JsonObject { ["op"] = "objectPropChange", ["ok"] = true };
        return response.ToJsonString(_jsonOpts);
    }

    // Set/clear a single object REFERENCE prop on the object with this intrinsic id —
    // the self-hosted reference editor's persist path (setRef). `refId` points at an
    // existing extent object; `value` ({ props }) mints a new object and points at it
    // (reply carries its real id); `clear` unsets. GC runs after (an orphaned target
    // is collected). Identity-addressed so it serves both a reference route and an
    // embedded reference field uniformly.
    private string HandleSetReferenceField(JsonElement root)
    {
        if (!root.TryGetProperty("objectId", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            return Error("setReferenceField requires a numeric 'objectId'.");
        if (!root.TryGetProperty("prop", out var propEl) || propEl.GetString() is not { } prop)
            return Error("setReferenceField requires 'prop'.");

        var objectId = idEl.GetInt32();
        if (_store.ReadById(objectId) is not { } hit)
            return Error($"No object with id {objectId}.");
        var propDef = _desc.FindType(hit.TypeName)?.Props?.FirstOrDefault(p => p.Name == prop);
        if (propDef is null)
            return Error($"Type '{hit.TypeName}' has no field '{prop}'.");
        if (propDef.Cardinality != Cardinality.Single || !_desc.IsObjectType(propDef.Type))
            return Error($"Field '{prop}' on '{hit.TypeName}' is not a single reference.");
        var targetType = _desc.FindType(propDef.Type)!;

        int? newId = null;
        if (root.TryGetProperty("refId", out var refEl) && refEl.ValueKind == JsonValueKind.Number)
        {
            _store.WriteReference(objectId, prop, refEl.GetInt32(), targetType.Name);
        }
        else if (root.TryGetProperty("value", out var valueEl))
        {
            newId = _store.CreateObject(targetType.Name, ExecObjectValue(valueEl, targetType));
            _store.WriteReference(objectId, prop, newId, targetType.Name);
        }
        else if (root.TryGetProperty("clear", out _))
        {
            _store.WriteReference(objectId, prop, null, targetType.Name);
        }
        else
        {
            return Error("setReferenceField requires 'refId', 'value', or 'clear'.");
        }

        var response = new JsonObject { ["op"] = "setReferenceField", ["ok"] = true };
        if (newId is { } nid) response["newId"] = nid;
        return response.ToJsonString(_jsonOpts);
    }

    // The WS's first message on open: claims the session minted at SSR (keeping it past
    // the claim window). The session carries no data — a refetch re-renders from a fresh
    // store load — so the report is informational; `sessionAlive: false` just means the
    // hello arrived past the window.
    private string HandleHello(JsonElement root)
    {
        var alive = Session(root) != null;
        return new JsonObject { ["op"] = "hello", ["sessionAlive"] = alive }.ToJsonString(_jsonOpts);
    }

    // Re-render the code UI and return authoritative client state. Called when a mutation
    // leaves a cache entry the client cannot recompute locally (a hidden dependency). The
    // render runs over a FRESH load from the store — the single source of truth — so it
    // reflects every committed change, not a per-client mirror that could have diverged.
    private string HandleRefetch(string pathStr, JsonElement root)
    {
        Session(root); // slide liveness; the session holds no data

        // Load the graph once from the store; object-valued vars (the client's selection)
        // resolve to the same instances the render uses, so selection-dependent data the
        // first paint never shipped gets computed.
        var db = Code.DbBridge.LoadRoot(_store, _desc, new Code.ExecContext());
        var byId = IndexObjects(db);

        var sessionVars = new Dictionary<string, Code.IExecValue>();
        if (root.TryGetProperty("vars", out var vars) && vars.ValueKind == JsonValueKind.Object)
            foreach (var v in vars.EnumerateObject())
                if (SessionVarFromWire(v.Value, byId) is { } value)
                    sessionVars[v.Name] = value;

        // Transients mint below the client's id floor (no collisions with its local drafts).
        var lastId = root.TryGetProperty("lastId", out var le) && le.ValueKind == JsonValueKind.Number
            ? le.GetInt32() : 0;
        // The refetch renderer gets the SAME live registry provider as the SSR path, so a refetch
        // re-render reflects the kernel's current instances — no stale `instances` list.
        var state = new SsrRenderer(_store, _desc, registry: _registry).RenderState(pathStr, sessionVars, db, lastId);
        return new JsonObject { ["op"] = "refetch", ["state"] = state }.ToJsonString(_jsonOpts);
    }

    // Index every persisted object in a loaded graph by its intrinsic id (for resolving
    // an object-valued session var to the instance the render will use).
    private static Dictionary<int, Code.ExecObject> IndexObjects(Code.ExecObject root)
    {
        var byId = new Dictionary<int, Code.ExecObject>();
        void Walk(Code.IExecValue value)
        {
            switch (value)
            {
                case Code.ExecObject o:
                    if (o.Id > 0 && byId.TryAdd(o.Id, o))
                        foreach (var p in o.Props.Values) Walk(p);
                    break;
                case Code.ExecArray a:
                    foreach (var item in a.Items) Walk(item.Value);
                    break;
            }
        }
        Walk(root);
        return byId;
    }

    private static Code.IExecValue? SessionVarFromWire(JsonElement el, Dictionary<int, Code.ExecObject> byId)
    {
        if ((el.TryGetProperty("type", out var t) ? t.GetString() : null) == "object")
            return el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                   && byId.TryGetValue(idEl.GetInt32(), out var obj)
                ? obj : null;
        return ExecValueFromWire(el);
    }

    // A scalar session var as the client interpreter holds it: { "type", "value" }.
    private static Code.IExecValue ExecValueFromWire(JsonElement el) =>
        (el.TryGetProperty("type", out var t) ? t.GetString() : null) switch
        {
            "int"  => new Code.ExecInt { Value = el.GetProperty("value").GetInt32() },
            "bool" => new Code.ExecBool { Value = el.GetProperty("value").GetBoolean() },
            "text" => new Code.ExecText { Value = el.GetProperty("value").GetString() ?? "" },
            _ => new Code.ExecNull(),
        };

    // A new set member built on the client (its negative id is transient): mint a real
    // object into the extent, link it into the set, and echo the negative→real id mapping
    // so the client can re-key its optimistic copy.
    private string HandleArrayAdd(JsonElement root)
    {
        if (!root.TryGetProperty("setId", out var setEl) || setEl.ValueKind != JsonValueKind.Number)
            return Error("arrayAdd requires a numeric 'setId'.");
        if (!root.TryGetProperty("typeName", out var tnEl) || tnEl.GetString() is not { } typeName)
            return Error("arrayAdd requires 'typeName'.");
        if (_desc.FindType(typeName) is not { } typeDef)
            return Error($"Unknown type '{typeName}'.");

        // The target set must exist and must declare exactly this element type.
        var setId = setEl.GetInt32();
        var elementType = _store.SetElementType(setId);
        if (elementType is null)
            return Error($"No set with id {setId}.");
        if (elementType != typeName)
            return Error($"Set {setId} holds '{elementType}' members, not '{typeName}'.");

        var value = root.TryGetProperty("value", out var valEl)
            ? ExecObjectValue(valEl, typeDef)
            : new ObjectValue(new Dictionary<string, NodeValue>());
        var id = _store.CreateObject(typeName, value);
        _store.AddToSet(setId, id);

        // The store minted the new object's collection props with their own intrinsic
        // ids; echo them so the client re-keys its transient arrays (else later adds
        // into them would silently not persist).
        var minted = _store.ReadById(id);
        var collections = new JsonObject();
        foreach (var prop in typeDef.Props ?? [])
            if (prop.Cardinality == Cardinality.Set
                && minted?.Fields.Fields.GetValueOrDefault(prop.Name) is SetValue sv)
                collections[prop.Name] = new JsonObject { ["id"] = sv.Id, ["elementTypeName"] = prop.Type };

        // `newId`, not `id` — the reply's `id` slot is the request correlation id.
        var response = new JsonObject { ["op"] = "arrayAdd", ["newId"] = id, ["collections"] = collections };
        if (root.TryGetProperty("tempId", out var te) && te.ValueKind == JsonValueKind.Number)
            response["tempId"] = te.GetInt32();
        return response.ToJsonString(_jsonOpts);
    }

    private string HandleArrayRemove(JsonElement root)
    {
        if (!root.TryGetProperty("setId", out var setEl) || setEl.ValueKind != JsonValueKind.Number)
            return Error("arrayRemove requires a numeric 'setId'.");
        if (!root.TryGetProperty("objectId", out var objEl) || objEl.ValueKind != JsonValueKind.Number)
            return Error("arrayRemove requires a numeric 'objectId'.");

        var setId = setEl.GetInt32();
        var objectId = objEl.GetInt32();
        if (_store.SetElementType(setId) is null)
            return Error($"No set with id {setId}.");
        _store.RemoveFromSet(setId, objectId);

        var response = new JsonObject { ["op"] = "arrayRemove", ["ok"] = true };
        return response.ToJsonString(_jsonOpts);
    }

    // A new object's scalar props as the client ships them ({ "props": { name: leaf } }),
    // validated against the declared type: unknown or non-scalar fields are rejected,
    // and each value must fit its prop's declared base type.
    private ObjectValue ExecObjectValue(JsonElement el, TypeDefinition type)
    {
        var fields = new Dictionary<string, NodeValue>();
        if (el.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject())
            {
                var propDef = type.Props?.FirstOrDefault(d => d.Name == p.Name)
                    ?? throw new InvalidOperationException($"Type '{type.Name}' has no field '{p.Name}'.");
                if (propDef.Cardinality != Cardinality.Single || _desc.IsObjectType(propDef.Type))
                    throw new InvalidOperationException($"Field '{p.Name}' on '{type.Name}' is not a scalar field.");
                fields[p.Name] = LeafForType(p.Value, BaseTypes.Parse(propDef.Type));
            }
        return new ObjectValue(fields);
    }

    // Convert a wire scalar ({ type, value }) to the prop's DECLARED base type. The
    // wire's claimed type must agree: int/bool/text exactly; decimal/date/datetime
    // arrive as the Code runtime's text projection (see DbBridge.ScalarToExec) and
    // are parsed. Anything else is a type mismatch → reject.
    private static NodeValue LeafForType(JsonElement el, BaseType declared)
    {
        var wireType = el.TryGetProperty("type", out var t) ? t.GetString() : null;
        var v = el.TryGetProperty("value", out var vv) ? vv : default;
        return (declared, wireType) switch
        {
            (BaseType.Int, "int")       => new IntValue(v.GetInt32()),
            (BaseType.Bool, "bool")     => new BoolValue(v.GetBoolean()),
            (BaseType.Text, "text")     => new TextValue(v.GetString() ?? ""),
            (BaseType.Decimal, "text")  => new DecimalValue(decimal.Parse(v.GetString() ?? "", System.Globalization.CultureInfo.InvariantCulture)),
            (BaseType.Date, "text")     => new DateValue(DateOnly.Parse(v.GetString() ?? "")),
            (BaseType.DateTime, "text") => new DateTimeValue(DateTimeOffset.Parse(v.GetString() ?? "")),
            _ => throw new InvalidOperationException($"A '{wireType}' value does not fit the declared '{declared}' field."),
        };
    }

    // ── NodeValue serialization ───────────────────────────────────────────────

    internal static JsonNode SerializeNodeValue(NodeValue node) => node switch
    {
        BoolValue b      => new JsonObject { ["type"] = "bool",     ["value"] = b.Value },
        IntValue i       => new JsonObject { ["type"] = "int",      ["value"] = i.Value },
        DecimalValue d   => new JsonObject { ["type"] = "decimal",  ["value"] = d.Value },
        TextValue t      => new JsonObject { ["type"] = "text",     ["value"] = t.Text },
        DateValue d      => new JsonObject { ["type"] = "date",     ["value"] = d.Value.ToString("yyyy-MM-dd") },
        DateTimeValue dt => new JsonObject { ["type"] = "datetime", ["value"] = dt.Value.ToString("O") },

        ObjectValue obj  => SerializeObject(obj),
        DictionaryValue dv => SerializeDictionary(dv),
        SetValue sv      => SerializeSet(sv),
        ReferenceValue r => new JsonObject { ["type"] = "object", ["typeName"] = r.TypeName, ["id"] = r.TargetId },

        _ => throw new InvalidOperationException($"Unhandled NodeValue type: {node.GetType().Name}")
    };

    private static JsonObject SerializeObject(ObjectValue obj)
    {
        var fields = new JsonObject();
        foreach (var (k, v) in obj.Fields)
            fields[k] = SerializeNodeValue(v);
        return new JsonObject { ["type"] = "object", ["fields"] = fields };
    }

    private static JsonObject SerializeSet(SetValue sv)
    {
        var members = new JsonObject();
        foreach (var (id, v) in sv.Members)
            members[id.ToString()] = SerializeNodeValue(v);
        return new JsonObject { ["type"] = "set", ["members"] = members };
    }

    private static JsonObject SerializeDictionary(DictionaryValue dv)
    {
        var entries = new JsonArray();
        foreach (var (k, v) in dv.Entries)
        {
            entries.Add(new JsonObject
            {
                ["key"]   = SerializeNodeValue(k),
                ["value"] = SerializeNodeValue(v)
            });
        }
        return new JsonObject { ["type"] = "dictionary", ["entries"] = entries };
    }

    // ── NodeValue deserialization ─────────────────────────────────────────────

    private static NodeValue DeserializeLeaf(JsonElement el, TypeDefinition type) =>
        type.BaseType switch
        {
            BaseType.Bool     => new BoolValue(el.GetBoolean()),
            BaseType.Int      => new IntValue(el.ValueKind == JsonValueKind.String ? int.Parse(el.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : el.GetInt32()),
            BaseType.Decimal  => new DecimalValue(el.ValueKind == JsonValueKind.String ? decimal.Parse(el.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : el.GetDecimal()),
            BaseType.Text     => new TextValue(el.GetString() ?? ""),
            BaseType.Date     => new DateValue(DateOnly.Parse(el.GetString() ?? "")),
            BaseType.DateTime => new DateTimeValue(DateTimeOffset.Parse(el.GetString() ?? "")),
            _ => throw new InvalidOperationException($"Cannot deserialize leaf of type {type.BaseType}")
        };

    // Deserialize a node value (object body or base leaf) from a raw JSON value.
    // Object: reads each non-dictionary leaf field from the element (dictionary
    // fields are navigation boundaries and are not part of a create/edit form).
    private NodeValue DeserializeValue(JsonElement el, TypeDefinition type)
    {
        if (type.BaseType != BaseType.Object)
            return DeserializeLeaf(el, type);

        var fields = new Dictionary<string, NodeValue>();
        foreach (var prop in type.Props ?? [])
        {
            if (prop.Cardinality == Cardinality.Dictionary) continue;
            if (el.TryGetProperty(prop.Name, out var fe))
                fields[prop.Name] = DeserializeValue(fe, ResolveTypeDef(prop.Type));
        }
        return new ObjectValue(fields);
    }

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

    private static string KeyString(NodeValue key) => key switch
    {
        IntValue i       => i.Value.ToString(),
        TextValue t      => t.Text,
        DecimalValue d   => d.Value.ToString(),
        BoolValue b      => b.Value.ToString().ToLowerInvariant(),
        DateValue d      => d.Value.ToString("yyyy-MM-dd"),
        DateTimeValue dt => dt.Value.ToString("O"),
        _ => throw new InvalidOperationException($"Cannot use {key.GetType().Name} as a key.")
    };

    // ── helpers ───────────────────────────────────────────────────────────────

    private static NodePath ParsePath(string urlPath)
    {
        var segs = urlPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return NodePath.FromSegments(segs);
    }

    private static string Error(string message)
    {
        var obj = new JsonObject { ["error"] = message };
        return obj.ToJsonString();
    }
}
