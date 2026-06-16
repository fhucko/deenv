using System.Text.Json.Nodes;
using DeEnv.Instance;

namespace DeEnv.Storage;

// The startup guard: structural validation of an EXISTING data document against the
// running app's types, before the instance starts serving. It catches a data file
// left behind by a different or older app — unknown extent types, stored fields the
// app does not declare, a stored kind contradicting the declaration, legacy documents
// without intrinsic collection ids, dangling references — and fails loudly with the
// file and the offending detail named, instead of half-working over stale data
// (mutations silently rejected, reloads losing changes).
//
// Deliberately tolerant of additive schema evolution: a declared prop missing from
// the stored fields is fine (reads fall back to defaults). It never reseeds over an
// existing file; the error names the remedy and leaves the decision to the user.
public static class StoredDataValidator
{
    public static void Validate(JsonObject doc, InstanceDescription desc, string filePath) =>
        new Walk(desc, filePath).Document(doc);

    private sealed class Walk(InstanceDescription desc, string filePath)
    {
        // Extent ids per type, collected up front so references can be checked.
        private readonly Dictionary<string, HashSet<int>> _ids = new();

        private void Fail(string detail) => throw new StoredDataException(
            $"Data file '{filePath}' does not match the running app: {detail} " +
            "Delete or move the file to reseed it from the app's initialData.");

        public void Document(JsonObject doc)
        {
            var extents = doc["extents"] as JsonObject ?? new JsonObject();
            CollectExtentIds(extents);

            foreach (var (typeName, pool) in extents)
            {
                var type = desc.FindType(typeName)!; // known: CollectExtentIds checked
                foreach (var (idText, env) in (JsonObject)pool!)
                    Fields(typeName, idText, ((JsonObject)env!)["fields"] as JsonObject
                        ?? new JsonObject(), type);
            }

            Root(doc);
        }

        // First pass over the extents: every type must be a declared object type and
        // every entry envelope well-formed; collect ids for the reference checks.
        private void CollectExtentIds(JsonObject extents)
        {
            foreach (var (typeName, pool) in extents)
            {
                var type = desc.FindType(typeName);
                if (type is null)
                    Fail($"the data has an extent of type '{typeName}', which the app does not declare.");
                if (type!.BaseType != BaseType.Object)
                    Fail($"the data has an extent of type '{typeName}', which is not an object type.");
                if (pool is not JsonObject poolObj)
                {
                    Fail($"the extent of '{typeName}' is malformed.");
                    return;
                }

                var ids = _ids[typeName] = new HashSet<int>();
                foreach (var (idText, env) in poolObj)
                {
                    if (!int.TryParse(idText, out var id)
                        || env is not JsonObject e
                        || e["id"]?.GetValue<int>() != id
                        || e["typeName"]?.GetValue<string>() != typeName)
                        Fail($"the extent entry '{typeName}/{idText}' is malformed.");
                    ids.Add(id);
                }
            }
        }

        private void Fields(string typeName, string id, JsonObject fields, TypeDefinition type)
        {
            foreach (var (name, node) in fields)
            {
                var prop = type.Props?.FirstOrDefault(p => p.Name == name);
                if (prop is null)
                {
                    Fail($"stored object {typeName}/{id} has a field '{name}' the app does not declare.");
                    return;
                }
                if (node is not JsonObject value)
                {
                    Fail($"field '{name}' on {typeName}/{id} is malformed.");
                    return;
                }

                var where = $"field '{name}' on {typeName}/{id}";
                switch (prop.Cardinality)
                {
                    case Cardinality.Set:
                        Collection(value, where, "set", "members", prop.Type);
                        foreach (var (memberId, member) in value["members"] as JsonObject ?? new JsonObject())
                        {
                            Reference(member, prop.Type, where);
                            if (!int.TryParse(memberId, out var mid)
                                || (member as JsonObject)?["id"]?.GetValue<int>() != mid)
                                Fail($"{where} has a member keyed '{memberId}' that does not match its reference.");
                        }
                        break;

                    case Cardinality.Dictionary:
                        Collection(value, where, "dictionary", "entries", prop.Type);
                        foreach (var (_, entry) in value["entries"] as JsonObject ?? new JsonObject())
                        {
                            if (desc.IsObjectType(prop.Type))
                                Reference(entry, prop.Type, where);
                            else
                                Scalar(entry as JsonObject, prop.Type, where);
                        }
                        break;

                    default:
                        if (desc.IsObjectType(prop.Type))
                            Reference(value, prop.Type, where);
                        else
                            Scalar(value, prop.Type, where);
                        break;
                }
            }
        }

        // A stored set/dictionary node: right kind, an intrinsic id (legacy files have
        // none — the code UI addresses collections by id), and the member slot present.
        private void Collection(JsonObject node, string where, string kind, string slot, string elemType)
        {
            if (TagOf(node) != kind)
                Fail($"{where} is declared '{kind} of {elemType}' but is stored as '{TagOf(node)}'.");
            if (node["id"]?.GetValue<int>() is null)
                Fail($"{where} has no intrinsic id (a legacy data file).");
            if (node[slot] is not JsonObject)
                Fail($"{where} is malformed: it has no {slot}.");
        }

        // A stored object reference: tagged "object", of exactly the declared type,
        // pointing at an object that exists in that type's extent.
        private void Reference(JsonNode? node, string declaredType, string where)
        {
            if (node is not JsonObject reference || TagOf(reference) != "object")
            {
                Fail($"{where} is declared as a reference to '{declaredType}' but is stored as '{TagOf(node as JsonObject)}'.");
                return;
            }
            var typeName = reference["typeName"]?.GetValue<string>();
            if (typeName != declaredType)
                Fail($"{where} is declared as a reference to '{declaredType}' but references a '{typeName}'.");
            if (reference["id"]?.GetValue<int>() is not int id
                || !(_ids.TryGetValue(declaredType, out var ids) && ids.Contains(id)))
                Fail($"{where} references object {reference["id"]} of type '{declaredType}', which is not stored.");
        }

        // A stored scalar: its tag must be the declared type's base type. An enum value is
        // text-shaped, and must additionally be a declared member of its enum (or empty) —
        // the startup twin of the WS write-path check (WsHandler.HandleObjectPropChange).
        private void Scalar(JsonObject? node, string declaredType, string where)
        {
            var expected = BaseTag(declaredType);
            if (node is null || TagOf(node) != expected)
            {
                Fail($"{where} is declared '{declaredType}' but is stored as '{TagOf(node)}'.");
                return;
            }
            // Only an enum constrains its value set; its stored value is a string (tag "text").
            if (desc.IsEnumType(declaredType)
                && node!["value"]?.GetValue<string>() is { } value
                && !desc.EnumAccepts(declaredType, value))
                Fail($"{where} holds '{value}', which is not a value of enum '{declaredType}'.");
        }

        private void Root(JsonObject doc)
        {
            var db = desc.Db()!;
            if (doc["root"] is not JsonObject root)
            {
                Fail("the document has no root.");
                return;
            }

            if (db.BaseType == BaseType.Object)
                Reference(root, db.Name, "the root");
            else
                Scalar(root, db.Name, "the root");
        }

        private static string? TagOf(JsonObject? node) => node?["type"]?.GetValue<string>();

        // The tag a scalar of this declared type is stored with ("text", "bool", …). An enum
        // value is stored as text (its value name), so its tag is "text" too.
        private string BaseTag(string typeName)
        {
            var baseType = BaseTypes.IsName(typeName)
                ? BaseTypes.Parse(typeName)
                : desc.FindType(typeName)?.BaseType switch
                  {
                      BaseType.Enum => BaseType.Text,
                      { } bt => bt,
                      null => throw new InvalidOperationException($"Unknown type '{typeName}'."),
                  };
            return baseType.ToString().ToLowerInvariant();
        }
    }
}
