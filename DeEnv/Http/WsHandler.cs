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
    private readonly TypeResolver _resolver;
    private readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public WsHandler(IInstanceStore store, InstanceDescription desc)
    {
        _store = store;
        _resolver = new TypeResolver(desc);
    }

    // ── message dispatch ──────────────────────────────────────────────────────

    public string ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var op = root.GetProperty("op").GetString() ?? "";
            var pathStr = root.TryGetProperty("path", out var pe) ? pe.GetString() ?? "/" : "/";
            var path = ParsePath(pathStr);

            return op switch
            {
                "read"  => HandleRead(path, pathStr),
                "write" => HandleWrite(path, pathStr, root),
                _       => Error($"Unknown op '{op}'")
            };
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
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
            BaseType.Int      => new IntValue(el.GetInt32()),
            BaseType.Decimal  => new DecimalValue(el.GetDecimal()),
            BaseType.Text     => new TextValue(el.GetString() ?? ""),
            BaseType.Date     => new DateValue(DateOnly.Parse(el.GetString() ?? "")),
            BaseType.DateTime => new DateTimeValue(DateTimeOffset.Parse(el.GetString() ?? "")),
            _ => throw new InvalidOperationException($"Cannot deserialize leaf of type {type.BaseType}")
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
