using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Designer;

// The bridge from the self-hosted designer to a runnable instance.
//
// The designer is the instance runtime running the meta-schema (meta.schema.json):
// its data is a Db holding a `types` dictionary of MetaType, each holding a `props`
// dictionary of MetaProp. `Project` turns that node tree into a canonical schema
// document (the same shape a hand-written instance.schema.json has); `Export` reads
// the designer's data file, projects it, validates it with the normal loader, and
// writes the result as the instance's schema (resetting the instance's data).
//
// This lives beside the instance runtime, not inside it — it never touches the
// renderer, the websocket handler, or the storage engine.
public static class SchemaBridge
{
    // Pure projection: designer Db node tree → canonical schema-document JSON.
    public static string Project(NodeValue designerDb)
    {
        var types = new JsonArray();

        if (designerDb is ObjectValue db && db.Fields.TryGetValue("types", out var typesNode))
        {
            foreach (var type in OrderedObjects(typesNode))
            {
                var typeObj = new JsonObject
                {
                    ["name"]     = TextField(type, "name"),
                    ["baseType"] = TextField(type, "baseType")
                };

                var props = new JsonArray();
                if (type.Fields.TryGetValue("props", out var propsNode))
                {
                    foreach (var prop in OrderedObjects(propsNode))
                    {
                        var propObj = new JsonObject
                        {
                            ["name"] = TextField(prop, "name"),
                            ["type"] = TextField(prop, "type")
                        };
                        // Omit empty optionals — a single-cardinality prop must not
                        // emit "cardinality":"" (the loader rejects that).
                        AddIfPresent(propObj, "cardinality",   TextField(prop, "cardinality"));
                        AddIfPresent(propObj, "keyType",       TextField(prop, "keyType"));
                        AddIfPresent(propObj, "keyGeneration", TextField(prop, "keyGeneration"));
                        props.Add(propObj);
                    }
                }

                // Emit `props` only when there are some, so a non-object type stays
                // props-less (the loader rejects a non-object type carrying props).
                if (props.Count > 0)
                    typeObj["props"] = props;

                types.Add(typeObj);
            }
        }

        var doc = new JsonObject { ["types"] = types };
        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // Read the designer's data, project it, validate it, and publish it as the
    // instance's schema. Throws SchemaValidationException (writing nothing) when the
    // designed schema is invalid — same validation as any hand-written document.
    public static void Export(
        string metaSchemaPath, string designerDataPath,
        string targetSchemaPath, string targetDataPath)
    {
        var meta = InstanceDescriptionLoader.LoadFile(metaSchemaPath);
        var store = new JsonFileInstanceStore(designerDataPath, meta);

        var db = store.ReadNode(NodePath.Root)
            ?? throw new SchemaValidationException("Designer data could not be read.");

        var json = Project(db);

        // Validate before writing: throws on an invalid design, leaving files as-is.
        InstanceDescriptionLoader.Load(json);

        File.WriteAllText(targetSchemaPath, json);
        // Reset the instance's data: empty → the store reinitializes to the new
        // schema's initial value on next start.
        File.WriteAllText(targetDataPath, "");
    }

    // TEMPORARY (testing scaffolding — not a product feature; remove later):
    // reverse of the bridge — seed the designer's data from an existing schema
    // document so Designer mode opens on the current instance schema rather than a
    // blank slate while we try the designer out. Only seeds when the designer has no
    // types yet, so it never clobbers designed-but-unexported edits.
    public static void SeedDesignerData(string metaSchemaPath, string designerDataPath, string sourceSchemaPath)
    {
        var meta = InstanceDescriptionLoader.LoadFile(metaSchemaPath);
        var designer = new JsonFileInstanceStore(designerDataPath, meta);

        if (designer.ReadNode(NodePath.Root) is ObjectValue root
            && root.Fields.TryGetValue("types", out var existing)
            && existing is DictionaryValue { Entries.Count: > 0 })
            return; // already has designed data — leave it alone

        var source = InstanceDescriptionLoader.LoadFile(sourceSchemaPath);
        var typesPath = NodePath.Root.Field("types");
        var typeKey = 1;

        foreach (var type in source.AllTypes)
        {
            designer.WriteDictionaryEntry(typesPath, new IntValue(typeKey),
                new ObjectValue(new Dictionary<string, NodeValue>
                {
                    ["name"]     = new TextValue(type.Name),
                    ["baseType"] = new TextValue(type.BaseTypeRaw),
                    ["order"]    = new IntValue(typeKey * 10)
                }));

            var propsPath = typesPath.Key(typeKey.ToString()).Field("props");
            var propKey = 1;
            foreach (var prop in type.Props ?? [])
            {
                var fields = new Dictionary<string, NodeValue>
                {
                    ["name"]  = new TextValue(prop.Name),
                    ["type"]  = new TextValue(prop.TypeName),
                    ["order"] = new IntValue(propKey * 10)
                };
                if (prop.Cardinality == Cardinality.Dictionary)
                {
                    fields["cardinality"]   = new TextValue("dictionary");
                    fields["keyType"]       = new TextValue(prop.EffectiveKeyType);
                    fields["keyGeneration"] = new TextValue(prop.KeyGeneration == KeyGeneration.Auto ? "auto" : "manual");
                }
                designer.WriteDictionaryEntry(propsPath, new IntValue(propKey), new ObjectValue(fields));
                propKey++;
            }
            typeKey++;
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    // Object entries of a dictionary node, sorted by the `order` field then by key
    // (key as a stable tiebreak / fallback when order is absent or equal).
    private static IEnumerable<ObjectValue> OrderedObjects(NodeValue? dict)
    {
        if (dict is not DictionaryValue dv)
            return [];

        return dv.Entries
            .Where(e => e.Value is ObjectValue)
            .Select(e => (obj: (ObjectValue)e.Value, order: IntField((ObjectValue)e.Value, "order"), key: KeyInt(e.Key)))
            .OrderBy(x => x.order).ThenBy(x => x.key)
            .Select(x => x.obj);
    }

    private static string TextField(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is TextValue t ? t.Text : "";

    private static int IntField(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is IntValue i ? i.Value : 0;

    private static int KeyInt(NodeValue key) => key is IntValue i ? i.Value : 0;

    private static void AddIfPresent(JsonObject obj, string key, string value)
    {
        if (!string.IsNullOrEmpty(value))
            obj[key] = value;
    }
}
