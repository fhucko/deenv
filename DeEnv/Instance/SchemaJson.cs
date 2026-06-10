using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeEnv.Instance;

// The single serialization contract for schema documents and the Code AST — the
// same options on both sides of the wire, so server and client exchange identical
// data apart from the casing. PascalCase CLR members map to camelCase JSON via the
// naming policy (no per-property attributes); enums serialize by member name.
public static class SchemaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
