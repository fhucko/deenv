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
    private readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public WsHandler(IInstanceStore store, InstanceDescription desc)
    {
        _store = store;
        _desc = desc;
        _resolver = new TypeResolver(desc);
    }

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
        if (typeInfo == null || typeInfo.Cardinality != Cardinality.Dictionary)
            return Error($"Path '{pathStr}' is not a dictionary.");

        var template = _store.NewEntryTemplate(path);
        var response = new JsonObject
        {
            ["op"]   = "newEntryTemplate",
            ["path"] = pathStr,
            ["template"] = SerializeNodeValue(template),
            ["keyGeneration"] = (typeInfo.KeyGeneration ?? KeyGeneration.Manual) == KeyGeneration.Auto
                ? "auto" : "manual"
        };
        return response.ToJsonString(_jsonOpts);
    }

    // ── addEntry (create on the create-form Save) ──────────────────────────────

    private string HandleAddEntry(NodePath path, string pathStr, JsonElement root)
    {
        var typeInfo = _resolver.ResolveType(path);
        if (typeInfo == null || typeInfo.Cardinality != Cardinality.Dictionary)
            return Error($"Path '{pathStr}' is not a dictionary.");

        if (!root.TryGetProperty("value", out var valueEl))
            return Error("Missing 'value' in addEntry message.");

        var value = DeserializeValue(valueEl, typeInfo.Type);
        var keyGen = typeInfo.KeyGeneration ?? KeyGeneration.Manual;

        NodeValue key;
        if (keyGen == KeyGeneration.Auto)
        {
            key = _store.CreateEntry(path, value);
        }
        else
        {
            if (!root.TryGetProperty("key", out var keyEl) || keyEl.GetString() is not { } keyStr || keyStr.Length == 0)
                return Error("Manual-key dictionary requires a non-empty 'key'.");
            key = ParseKey(keyStr, typeInfo.KeyTypeName ?? "text");
            _store.CreateEntry(path, key, value); // throws on duplicate → caught as { error }
        }

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
        if (typeInfo == null || typeInfo.Cardinality != Cardinality.Dictionary)
            return Error($"Path '{pathStr}' is not a dictionary.");
        if (!root.TryGetProperty("key", out var keyEl) || keyEl.GetString() is not { } keyStr)
            return Error("Missing 'key' in removeEntry message.");

        _store.RemoveDictionaryEntry(path, ParseKey(keyStr, typeInfo.KeyTypeName ?? "text"));

        var response = new JsonObject { ["op"] = "removeEntry", ["path"] = pathStr, ["ok"] = true };
        return response.ToJsonString(_jsonOpts);
    }

    // ── NodeValue serialization ───────────────────────────────────────────────

    internal static JsonNode SerializeNodeValue(NodeValue node) => node switch
    {
        BoolValue b      => new JsonObject { ["kind"] = "bool",     ["value"] = b.Value },
        IntValue i       => new JsonObject { ["kind"] = "int",      ["value"] = i.Value },
        DecimalValue d   => new JsonObject { ["kind"] = "decimal",  ["value"] = d.Value },
        TextValue t      => new JsonObject { ["kind"] = "text",     ["value"] = t.Text },
        DateValue d      => new JsonObject { ["kind"] = "date",     ["value"] = d.Value.ToString("yyyy-MM-dd") },
        DateTimeValue dt => new JsonObject { ["kind"] = "datetime", ["value"] = dt.Value.ToString("O") },

        ObjectValue obj  => SerializeObject(obj),
        DictionaryValue dv => SerializeDictionary(dv),

        _ => throw new InvalidOperationException($"Unhandled NodeValue type: {node.GetType().Name}")
    };

    private static JsonObject SerializeObject(ObjectValue obj)
    {
        var fields = new JsonObject();
        foreach (var (k, v) in obj.Fields)
            fields[k] = SerializeNodeValue(v);
        return new JsonObject { ["kind"] = "object", ["fields"] = fields };
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
        return new JsonObject { ["kind"] = "dictionary", ["entries"] = entries };
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
                fields[prop.Name] = DeserializeValue(fe, ResolveTypeDef(prop.TypeName));
        }
        return new ObjectValue(fields);
    }

    private TypeDefinition ResolveTypeDef(string name) =>
        name is "bool" or "int" or "decimal" or "text" or "date" or "datetime"
            ? new TypeDefinition(name, name)
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
