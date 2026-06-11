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
    private readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public WsHandler(IInstanceStore store, InstanceDescription desc, ClientSessionStore? sessions = null)
    {
        _store = store;
        _desc = desc;
        _resolver = new TypeResolver(desc);
        _sessions = sessions;
    }

    // The warm per-client session a code-UI message addresses (clientId minted at SSR).
    private ClientSession? Session(JsonElement root) =>
        _sessions != null && root.TryGetProperty("clientId", out var c) && c.GetString() is { } id
            ? _sessions.Get(id) : null;

    // ── message dispatch ──────────────────────────────────────────────────────

    public string ProcessMessage(string json)
    {
        int? id = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var ide) && ide.ValueKind == JsonValueKind.Number)
                id = ide.GetInt32();

            var op = root.GetProperty("op").GetString() ?? "";
            var pathStr = root.TryGetProperty("path", out var pe) ? pe.GetString() ?? "/" : "/";
            var path = ParsePath(pathStr);

            var result = op switch
            {
                "read"             => HandleRead(path, pathStr),
                "write"            => HandleWrite(path, pathStr, root),
                "writeObject"      => HandleWriteObject(path, pathStr, root),
                "newEntryTemplate" => HandleNewEntryTemplate(path, pathStr),
                "addEntry"         => HandleAddEntry(path, pathStr, root),
                "removeEntry"      => HandleRemoveEntry(path, pathStr, root),
                "setReference"     => HandleSetReference(path, pathStr, root),
                "hello"            => HandleHello(root),
                "objectPropChange" => HandleObjectPropChange(root),
                "arrayAdd"         => HandleArrayAdd(root),
                "arrayRemove"      => HandleArrayRemove(root),
                "refetch"          => HandleRefetch(pathStr, root),
                _                  => Error($"Unknown op '{op}'")
            };
            return WithId(result, id);
        }
        catch (Exception ex)
        {
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

    // ── read ──────────────────────────────────────────────────────────────────

    private string HandleRead(NodePath path, string pathStr)
    {
        var node = _store.ReadNode(path);
        if (node == null)
        {
            var obj = new JsonObject { ["op"] = "read", ["path"] = pathStr, ["notFound"] = true };
            return obj.ToJsonString(_jsonOpts);
        }

        var response = new JsonObject
        {
            ["op"]   = "read",
            ["path"] = pathStr,
            ["data"] = SerializeNodeValue(node)
        };
        return response.ToJsonString(_jsonOpts);
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
        _store.WriteLeaf(path, value);

        var response = new JsonObject { ["op"] = "write", ["path"] = pathStr, ["ok"] = true };
        return response.ToJsonString(_jsonOpts);
    }

    // ── writeObject (object-form Save) ─────────────────────────────────────────

    private string HandleWriteObject(NodePath path, string pathStr, JsonElement root)
    {
        var typeInfo = _resolver.ResolveType(path);
        if (typeInfo == null)
            return Error($"Path '{pathStr}' does not resolve.");
        if (typeInfo.Cardinality != Cardinality.Single || typeInfo.Type.BaseType != BaseType.Object)
            return Error($"Path '{pathStr}' is not an object node.");
        if (!root.TryGetProperty("fields", out var fieldsEl))
            return Error("Missing 'fields' in writeObject message.");

        var obj = (ObjectValue)DeserializeValue(fieldsEl, typeInfo.Type);
        _store.WriteObject(path, obj);

        var response = new JsonObject { ["op"] = "writeObject", ["path"] = pathStr, ["ok"] = true };
        return response.ToJsonString(_jsonOpts);
    }

    // ── newEntryTemplate (render the create form) ──────────────────────────────

    private string HandleNewEntryTemplate(NodePath path, string pathStr)
    {
        var typeInfo = _resolver.ResolveType(path);
        if (typeInfo == null)
            return Error($"Path '{pathStr}' does not resolve.");

        if (typeInfo.Cardinality == Cardinality.Set)
        {
            var candidates = new JsonArray();
            foreach (var (id, obj) in _store.ReadExtent(typeInfo.Type.Name))
                candidates.Add(new JsonObject { ["id"] = id, ["label"] = LabelOf(obj) });

            var setResponse = new JsonObject
            {
                ["op"]         = "newEntryTemplate",
                ["path"]       = pathStr,
                ["template"]   = SerializeNodeValue(_store.NewEntryTemplate(path)),
                ["collection"] = "set",
                ["candidates"] = candidates
            };
            return setResponse.ToJsonString(_jsonOpts);
        }

        if (typeInfo.Cardinality != Cardinality.Dictionary)
            return Error($"Path '{pathStr}' is not a dictionary.");

        var response = new JsonObject
        {
            ["op"]   = "newEntryTemplate",
            ["path"] = pathStr,
            ["template"] = SerializeNodeValue(_store.NewEntryTemplate(path)),
            ["collection"] = "dictionary"
        };
        // A dictionary of objects offers pick-existing candidates alongside the key.
        if (typeInfo.Type.BaseType == BaseType.Object)
        {
            var candidates = new JsonArray();
            foreach (var (id, obj) in _store.ReadExtent(typeInfo.Type.Name))
                candidates.Add(new JsonObject { ["id"] = id, ["label"] = LabelOf(obj) });
            response["candidates"] = candidates;
        }
        return response.ToJsonString(_jsonOpts);
    }

    // Label for a candidate object: its first text field, else empty.
    private static string LabelOf(ObjectValue obj)
    {
        foreach (var (_, v) in obj.Fields)
            if (v is TextValue t) return t.Text;
        return "";
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

    private string HandleSetReference(NodePath path, string pathStr, JsonElement root)
    {
        var typeInfo = _resolver.ResolveType(path);
        if (typeInfo == null || typeInfo.Cardinality != Cardinality.Single || typeInfo.Type.BaseType != BaseType.Object)
            return Error($"Path '{pathStr}' is not a single reference.");

        if (root.TryGetProperty("refId", out var refEl) && refEl.ValueKind == JsonValueKind.Number)
        {
            _store.SetReference(path, refEl.GetInt32());           // point at an existing object
        }
        else if (root.TryGetProperty("value", out var valueEl))
        {
            var obj = (ObjectValue)DeserializeValue(valueEl, typeInfo.Type);
            _store.SetReference(path, _store.CreateObject(typeInfo.Type.Name, obj)); // mint + point
        }
        else if (root.TryGetProperty("clear", out _))
        {
            _store.SetReference(path, null);
        }
        else
        {
            return Error("setReference requires 'refId', 'value', or 'clear'.");
        }

        var response = new JsonObject { ["op"] = "setReference", ["path"] = pathStr, ["ok"] = true };
        return response.ToJsonString(_jsonOpts);
    }

    // ── code-owned UI mutations (the Code runtime, identity-addressed) ──────────

    // A two-way-bound prop write from the client: persist a single leaf field on the
    // object with this intrinsic id. Fire-and-forget (the client already applied it
    // optimistically); surgical recompute/delta is a later slice.
    private string HandleObjectPropChange(JsonElement root)
    {
        if (!root.TryGetProperty("objectId", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            return Error("objectPropChange requires a numeric 'objectId'.");
        if (!root.TryGetProperty("prop", out var propEl) || propEl.GetString() is not { } prop)
            return Error("objectPropChange requires 'prop'.");
        if (!root.TryGetProperty("value", out var valEl))
            return Error("objectPropChange requires 'value'.");

        var objectId = idEl.GetInt32();
        _store.WriteField(objectId, prop, ExecLeaf(valEl));

        // Mirror into the warm graph so a later recompute sees the change.
        if (Session(root) is { } s && s.Objects.TryGetValue(objectId, out var obj))
            obj.Props[prop] = ExecValueFromWire(valEl);

        var response = new JsonObject { ["op"] = "objectPropChange", ["ok"] = true };
        return response.ToJsonString(_jsonOpts);
    }

    // The WS's first message on open: claims the warm session minted at SSR, keeping it
    // past the claim window. If the hello arrives too late the session is already gone —
    // `sessionAlive: false` — and later refetches fall back to a full re-render.
    private string HandleHello(JsonElement root)
    {
        var alive = Session(root) != null;
        return new JsonObject { ["op"] = "hello", ["sessionAlive"] = alive }.ToJsonString(_jsonOpts);
    }

    // Re-render the code UI over fresh storage with the client's session vars and return
    // authoritative client state. The client calls this after a mutation leaves a cache
    // entry it cannot recompute locally (a hidden dependency).
    private string HandleRefetch(string pathStr, JsonElement root)
    {
        var sessionVars = new Dictionary<string, Code.IExecValue>();
        if (root.TryGetProperty("vars", out var vars) && vars.ValueKind == JsonValueKind.Object)
            foreach (var v in vars.EnumerateObject())
                sessionVars[v.Name] = ExecValueFromWire(v.Value);

        // Recompute over the session's warm graph (already reflecting this client's
        // mutations); falls back to a fresh load if there is no session.
        var state = new SsrRenderer(_store, _desc).RenderState(pathStr, sessionVars, Session(root)?.Db);
        return new JsonObject { ["op"] = "refetch", ["state"] = state }.ToJsonString(_jsonOpts);
    }

    // A new set member's warm runtime object: its provided scalar props, its real id.
    private static Code.ExecObject WarmObject(JsonElement value, string typeName, int id)
    {
        var props = new Dictionary<string, Code.IExecValue>();
        if (value.TryGetProperty("props", out var p) && p.ValueKind == JsonValueKind.Object)
            foreach (var prop in p.EnumerateObject())
                props[prop.Name] = ExecValueFromWire(prop.Value);
        return new Code.ExecObject { Props = props, Id = id, TypeName = typeName };
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
        if (_desc.FindType(typeName) is null)
            return Error($"Unknown type '{typeName}'.");

        var value = root.TryGetProperty("value", out var valEl)
            ? ExecObjectValue(valEl)
            : new ObjectValue(new Dictionary<string, NodeValue>());
        var setId = setEl.GetInt32();
        var id = _store.CreateObject(typeName, value);
        _store.AddToSet(setId, id);

        // Mirror into the warm graph: a new member object, linked into the warm set.
        if (Session(root) is { } s && s.Sets.TryGetValue(setId, out var set))
        {
            var obj = WarmObject(valEl, typeName, id);
            set.Items.Add(new Code.ExecItem { Key = id, Value = obj });
            s.Objects[id] = obj;
        }

        // `newId`, not `id` — the reply's `id` slot is the request correlation id.
        var response = new JsonObject { ["op"] = "arrayAdd", ["newId"] = id };
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
        _store.RemoveFromSet(setId, objectId);

        // Mirror into the warm graph: drop the member from the warm set.
        if (Session(root) is { } s && s.Sets.TryGetValue(setId, out var set))
            set.Items.RemoveAll(i => i.Value is Code.ExecObject o && o.Id == objectId);

        var response = new JsonObject { ["op"] = "arrayRemove", ["ok"] = true };
        return response.ToJsonString(_jsonOpts);
    }

    // A new object's scalar props as the client ships them: { "props": { name: leaf } }.
    private static ObjectValue ExecObjectValue(JsonElement el)
    {
        var fields = new Dictionary<string, NodeValue>();
        if (el.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject())
                fields[p.Name] = ExecLeaf(p.Value);
        return new ObjectValue(fields);
    }

    // A scalar as the client interpreter ships it: { "type": "int|bool|text", "value": … }.
    private static NodeValue ExecLeaf(JsonElement el)
    {
        var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
        var v = el.TryGetProperty("value", out var vv) ? vv : default;
        return type switch
        {
            "int"  => new IntValue(v.ValueKind == JsonValueKind.String
                ? int.Parse(v.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : v.GetInt32()),
            "bool" => new BoolValue(v.GetBoolean()),
            "text" => new TextValue(v.GetString() ?? ""),
            _ => throw new InvalidOperationException($"Unsupported value type '{type}'.")
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
