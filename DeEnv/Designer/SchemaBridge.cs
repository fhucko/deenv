using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Designer;

// The bridge from the self-hosted designer to a runnable instance.
//
// The designer designs a whole app as ordinary data. The unit it projects is a
// `Design` node: a `types` set of MetaType (each holding a `props` set of MetaProp)
// — the STRUCTURED part — plus three `initialData`/`common`/`ui` TEXT fields that
// carry the other app-document sections verbatim. `Project` turns the structured
// `types` into TypeDefinitions; `ProjectDesignDocument` assembles the whole app
// document (printed types + the verbatim sections), validates it with the normal
// loader, and returns it as text. A publish writes that text onto a target and
// resets the target's data; a create hands it to the kernel to spawn a new instance.
//
// The three text fields hold the VERBATIM section source INCLUDING the section
// keyword and its indentation — e.g. the `ui` field is "ui\n    fn render()\n…",
// the empty string when there is no such section. That representation makes both
// directions trivial: assembly here is "print the types section, then concatenate
// the non-empty section texts" (no per-section sub-parsing — each section parser
// already consumes its own keyword), and the future committed-app → Design split is
// just slicing a document at its section boundaries. Validation (and an empty-section
// app — empty `ui` → generic UI, empty `initialData` → no seed) all fall out of the
// normal AppParse pipeline.
//
// This lives beside the instance runtime, not inside it — it never touches the
// renderer, the websocket handler, or the storage engine.
public static class SchemaBridge
{
    // Project a Design node (structured types + the three verbatim section texts) into a
    // complete, validated app document (text) — the whole app, not just its types, so a
    // published/created instance keeps its custom UI (`fn render()`), seed data, and shared
    // functions. Throws SchemaValidationException on an invalid design (the same validation
    // pipeline as any hand-written document), so a bad design yields no document.
    public static string ProjectDesignDocument(NodeValue design)
    {
        // Validate the projected TYPES first, on the typed description — so a structural type error
        // (e.g. an object Db with no props) surfaces as its precise semantic message ("…has baseType
        // 'object' but no props") rather than the parse error that printing-then-reparsing such an
        // invalid shape would raise (the printer can emit a propless object the parser won't accept).
        var typed = Project(design);
        InstanceDescriptionLoader.ValidateDescription(typed); // throws on invalid types

        // The `types` section, printed from the (now-validated) structured types via the canonical
        // printer. A types-only description prints exactly the `types` section (no other section
        // emitted), so this is just that section's text.
        var typesSection = AppPrint.Print(typed);

        // The other sections, each verbatim INCLUDING its keyword (empty → absent). Concatenated
        // after the types section with a blank line between (the section parsers skip blank lines
        // before their keyword, so the spacing is cosmetic / canonical).
        var sections = new List<string> { typesSection.TrimEnd('\n') };
        if (design is ObjectValue d)
            foreach (var name in new[] { "initialData", "common", "ui" })
                if (TextField(d, name) is { Length: > 0 } section)
                    sections.Add(section.TrimEnd('\n'));

        var document = string.Join("\n\n", sections) + "\n";

        // Validate the WHOLE assembled document via the normal loader (parse + semantic validation):
        // this is what catches a malformed section text or a cross-section error (e.g. a Code/UI or
        // initialData problem). Throws on an invalid design, so nothing is published/spawned. Returning
        // the assembled text (not a re-print) keeps the user's exact section source.
        InstanceDescriptionLoader.Load(document);
        return document;
    }

    // Pure projection: a Design (or legacy Db) node's `types` set → the typed description (types
    // only). Shared by ProjectDesignDocument (which adds the other sections) and the M4 tests.
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
                    {
                        var cardinality = TextField(prop, "cardinality") switch
                        {
                            "" or "single" => Cardinality.Single,
                            "set"          => Cardinality.Set,
                            "dictionary"   => Cardinality.Dictionary,
                            var other => throw new SchemaValidationException(
                                $"Prop on type '{name}' has unknown cardinality '{other}'."),
                        };
                        // keyType is meaningful ONLY for a dictionary. The designer always renders the
                        // key-type field (a conditional one fails to reconcile when it appears), so a
                        // single/set prop may carry a leftover value — ignore it here (a set that declared
                        // a keyType would be rejected on load), keeping the always-shown field harmless.
                        var keyType = cardinality == Cardinality.Dictionary
                            && TextField(prop, "keyType") is { Length: > 0 } key ? key : null;
                        props.Add(new PropDefinition(
                            TextField(prop, "name"),
                            TextField(prop, "type"),
                            cardinality,
                            keyType));
                    }

                if (baseName == "object")
                    // Emit props only when there are some, so a designed object type
                    // without props is rejected by the shared validation ("no props").
                    types.Add(new TypeDefinition(name, BaseType.Object, props.Count > 0 ? props : null));
                else if (baseName == "enum")
                {
                    // An enum carries no props — only a value list, authored in the designer as a single
                    // comma-separated field (the always-rendered `values` input). Split, trim, drop empties.
                    // An enum with zero values is rejected by the shared validation ("no values"), so an
                    // empty field yields no document — correct.
                    var values = TextField(type, "values")
                        .Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
                    types.Add(new TypeDefinition(name, BaseType.Enum, Props: null, Values: values));
                }
                else if (BaseTypes.IsName(baseName))
                    types.Add(new TypeDefinition(name, BaseTypes.Parse(baseName), props.Count > 0 ? props : null));
                else
                    throw new SchemaValidationException($"Type '{name}' has unknown baseType '{baseName}'.");
            }
        }

        return new InstanceDescription(types);
    }

    // Read the designer's data, project it, validate it, and return it as an app document
    // (text) — without writing anywhere. The projection half of Export, shared with `create`
    // (which hands the text to the kernel to spawn a NEW instance, rather than overwriting an
    // existing one). Throws SchemaValidationException on an invalid design (the same validation
    // pipeline as any hand-written document), so a bad design yields no document and no spawn.
    public static string ProjectDocument(string metaAppPath, string designerDataPath)
    {
        var meta = InstanceDescriptionLoader.LoadFile(metaAppPath);
        var store = new JsonFileInstanceStore(designerDataPath, meta);

        var db = store.ReadNode(NodePath.Root)
            ?? throw new SchemaValidationException("Designer data could not be read.");

        var desc = Project(db);
        InstanceDescriptionLoader.ValidateDescription(desc); // throws on an invalid design
        return AppPrint.Print(desc);
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

        WriteDocument(AppPrint.Print(desc), targetAppPath, targetDataPath);
    }

    // Write an already-projected, already-validated app document onto a target instance, PRESERVING
    // its existing data across the schema change when the data still fits (non-destructive apply — the
    // migration substrate under M13 versioning; see DECISIONS "Data must survive schema changes").
    // Shared by Export (the M4 root-Db path) and the kernel's passed-Design publish, so both apply
    // identically.
    //
    // Non-destructive apply — migrate-toward-then-preserve-or-reseed:
    //   • Migrate the existing data TOWARD the new schema (drop removed fields, …), then KEEP it when it
    //     fits — additive (a new prop reads its default) AND subtractive (a removed field is pruned)
    //     changes survive. This is the win: data survives a schema change.
    //   • No data yet (a fresh target), OR a change a slice cannot yet carry forward (a rename, a
    //     type/cardinality change, a wholesale different app) → reseed the new schema's initial
    //     document. Carrying those forward (value conversion, rename remap) is the follow-up slices that
    //     progressively replace this reseed; until then such an apply still resets, as it always has.
    public static void WriteDocument(string documentText, string targetAppPath, string targetDataPath)
    {
        var newDesc = InstanceDescriptionLoader.Load(documentText);
        File.WriteAllText(targetAppPath, documentText);

        var hasData = File.Exists(targetDataPath) && new FileInfo(targetDataPath).Length > 0;
        if (hasData)
            JsonFileInstanceStore.MigrateTowardSchema(targetDataPath, newDesc);

        if (!(hasData && DataFits(targetDataPath, newDesc)))
        {
            // No prior data, or a change this slice cannot carry — drop any prior data and reseed.
            // Delete first because opening a store over incompatible data would trip the startup guard.
            File.Delete(targetDataPath);
            new JsonFileInstanceStore(targetDataPath, newDesc).Reset();
        }
    }

    // Whether the data file still satisfies the schema — i.e. opening a store over it passes the
    // startup guard (StoredDataValidator), which tolerates additive evolution (a newly declared prop
    // absent from stored data reads its default) and rejects removed/changed fields. A clean open means
    // the data carries forward unchanged; a StoredDataException (incompatible, or unreadable) means it
    // does not. The opened store is discarded — a successful open leaves a compatible file untouched.
    private static bool DataFits(string dataPath, InstanceDescription desc)
    {
        try
        {
            _ = new JsonFileInstanceStore(dataPath, desc);
            return true;
        }
        catch (StoredDataException)
        {
            return false;
        }
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
            var typeFields = new Dictionary<string, NodeValue>
            {
                ["name"]     = new TextValue(type.Name),
                ["baseType"] = new TextValue(JsonNamingPolicy.CamelCase.ConvertName(type.BaseType.ToString())),
                ["order"]    = new IntValue(typeOrder * 10)
            };
            // An enum's value list is seeded into the comma-separated `values` field (the same form the
            // type editor edits + SchemaBridge.Project reads back), so a published-then-reseeded enum
            // round-trips. Object/leaf types carry no values (the field stays empty).
            if (type.BaseType == BaseType.Enum)
                typeFields["values"] = new TextValue(string.Join(", ", type.Values ?? []));

            var typeId = designer.CreateObject("MetaType", new ObjectValue(typeFields));
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
