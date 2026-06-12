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
    // Pure projection: designer Db node tree → the typed description (no text yet).
    public static InstanceDescription Project(NodeValue designerDb)
    {
        var types = new List<TypeDefinition>();

        if (designerDb is ObjectValue db && db.Fields.TryGetValue("types", out var typesNode))
        {
            foreach (var type in OrderedObjects(typesNode))
            {
                var name = TextField(type, "name");
                var baseName = TextField(type, "baseType");

                var props = new List<PropDefinition>();
                if (type.Fields.TryGetValue("props", out var propsNode))
                    foreach (var prop in OrderedObjects(propsNode))
                        props.Add(new PropDefinition(
                            TextField(prop, "name"),
                            TextField(prop, "type"),
                            TextField(prop, "cardinality") switch
                            {
                                "" or "single" => Cardinality.Single,
                                "set"          => Cardinality.Set,
                                "dictionary"   => Cardinality.Dictionary,
                                var other => throw new SchemaValidationException(
                                    $"Prop on type '{name}' has unknown cardinality '{other}'."),
                            },
                            TextField(prop, "keyType") is { Length: > 0 } key ? key : null));

                if (baseName == "object")
                    // Emit props only when there are some, so a designed object type
                    // without props is rejected by the shared validation ("no props").
                    types.Add(new TypeDefinition(name, BaseType.Object, props.Count > 0 ? props : null));
                else if (BaseTypes.IsName(baseName))
                    types.Add(new TypeDefinition(name, BaseTypes.Parse(baseName), props.Count > 0 ? props : null));
                else
                    throw new SchemaValidationException($"Type '{name}' has unknown baseType '{baseName}'.");
            }
        }

        return new InstanceDescription(types);
    }

    // Read the designer's data, project it, validate it, and publish it as the
    // instance's app document (text). Throws SchemaValidationException (writing
    // nothing) when the designed schema is invalid — the same validation pipeline
    // as any hand-written document.
    public static void Export(
        string metaAppPath, string designerDataPath,
        string targetAppPath, string targetDataPath)
    {
        var meta = InstanceDescriptionLoader.LoadFile(metaAppPath);
        var store = new JsonFileInstanceStore(designerDataPath, meta);

        var db = store.ReadNode(NodePath.Root)
            ?? throw new SchemaValidationException("Designer data could not be read.");

        var desc = Project(db);

        // Validate before writing: throws on an invalid design, leaving files as-is.
        InstanceDescriptionLoader.ValidateDescription(desc);

        File.WriteAllText(targetAppPath, AppPrint.Print(desc));
        // Reset the instance's data through the storage seam: reinitialize to the
        // new schema's initial document immediately (no stale data until next start).
        new JsonFileInstanceStore(targetDataPath, desc).Reset();
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
            && existing is SetValue { Members.Count: > 0 })
            return; // already has designed data — leave it alone

        var source = InstanceDescriptionLoader.LoadFile(sourceSchemaPath);
        var typesPath = NodePath.Root.Field("types");
        var typeOrder = 1;

        foreach (var type in source.AllTypes())
        {
            var typeId = designer.CreateObject("MetaType",
                new ObjectValue(new Dictionary<string, NodeValue>
                {
                    ["name"]     = new TextValue(type.Name),
                    ["baseType"] = new TextValue(JsonNamingPolicy.CamelCase.ConvertName(type.BaseType.ToString())),
                    ["order"]    = new IntValue(typeOrder * 10)
                }));
            designer.AddToSet(typesPath, typeId);

            var propsPath = typesPath.Key(typeId.ToString()).Field("props");
            var propOrder = 1;
            foreach (var prop in type.Props ?? [])
            {
                var fields = new Dictionary<string, NodeValue>
                {
                    ["name"]  = new TextValue(prop.Name),
                    ["type"]  = new TextValue(prop.Type),
                    ["order"] = new IntValue(propOrder * 10)
                };
                if (prop.Cardinality != Cardinality.Single)
                    fields["cardinality"] = new TextValue(prop.Cardinality == Cardinality.Set ? "set" : "dictionary");
                if (prop.Cardinality == Cardinality.Dictionary)
                    fields["keyType"] = new TextValue((prop.KeyType ?? "text"));

                var propId = designer.CreateObject("MetaProp", new ObjectValue(fields));
                designer.AddToSet(propsPath, propId);
                propOrder++;
            }
            typeOrder++;
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    // Member objects of a set node, sorted by the `order` field then by identity
    // (identity as a stable tiebreak / fallback when order is absent or equal).
    private static IEnumerable<ObjectValue> OrderedObjects(NodeValue? set)
    {
        if (set is not SetValue sv)
            return [];

        return sv.Members
            .Where(e => e.Value is ObjectValue)
            .Select(e => (obj: (ObjectValue)e.Value, order: IntField((ObjectValue)e.Value, "order"), id: e.Key))
            .OrderBy(x => x.order).ThenBy(x => x.id)
            .Select(x => x.obj);
    }

    private static string TextField(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is TextValue t ? t.Text : "";

    private static int IntField(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is IntValue i ? i.Value : 0;

    private static void AddIfPresent(JsonObject obj, string key, string value)
    {
        if (!string.IsNullOrEmpty(value))
            obj[key] = value;
    }
}
