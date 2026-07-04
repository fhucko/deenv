using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Designer;

// The bridge from the self-hosted designer to a runnable instance.
//
// The designer designs a whole app as ordinary data. The unit it projects is a
// `Design` node: a `types` set of MetaType (each holding a `props` set of MetaProp)
// — the STRUCTURED part — plus four `initialData`/`access`/`common`/`ui` TEXT fields
// that carry the other app-document sections verbatim. `Project` turns the structured
// `types` into TypeDefinitions; `ProjectDesignDocument` assembles the whole app
// document (printed types + the verbatim sections), validates it with the normal
// loader, and returns it as text. A publish writes that text onto a target and
// resets the target's data; a create hands it to the kernel to spawn a new instance.
//
// The text fields hold the VERBATIM section source INCLUDING the section
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

// The per-commit caches (M13 slice 2): the canonical printed app document + a name-path → intrinsic-id
// map over its types and props. Text alone is names-only (insufficient for rename-aware diff); the id
// map re-attaches the M5 identity a by-name projection otherwise drops. IdMap keys are "TypeName" (the
// type's MetaType row id) and "TypeName.propName" (the prop's MetaProp row id) — dotted name-paths,
// unambiguous because ProjectDesignDocument's own validation already requires unique names. The map keys
// EXACTLY what the projected document shows, nothing more — so an enum type contributes only its own type
// entry and NO prop entries, even if leftover MetaProp members linger in its `props` set after an
// object→enum base-type flip (Project's enum branch hardcodes Props: null; its values live in a single
// text field with no per-value identity, so a value rename/reorder is a textual diff, not an identity
// one). Derived and rebuildable from the design at any time — never itself authoritative (slice 3
// persists it on the Commit row; nothing here writes storage).
public sealed record DesignSnapshot(string Text, IReadOnlyDictionary<string, int> IdMap);

public static class SchemaBridge
{
    // Build a design's per-commit snapshot: the canonical printed app document, then a name-path→id map
    // walked over the SAME structure ProjectDesignDocument prints (types, each type's props), keeping the
    // member ids OrderedObjects/Project discard. Text is computed FIRST — ProjectDesignDocument validates
    // (types, then the whole assembled document) and THROWS SchemaValidationException on an invalid
    // design — so an invalid design yields no snapshot at all, not a partial id map. (Snapshot inherits
    // that validate-or-throw behavior; how a future sys.commitDesign surfaces "can't commit an invalid
    // design" to the caller is that slice's decision, not this one's.) Same design state → byte-identical
    // Text on every call: the printer is canonical and the verbatim sections are passed through unchanged.
    public static DesignSnapshot Snapshot(NodeValue design)
    {
        var text = ProjectDesignDocument(design); // throws on an invalid design — before the map is built
        var idMap = new Dictionary<string, int>();

        if (design is ObjectValue d && d.Fields.TryGetValue("types", out var typesNode))
            foreach (var (typeId, type) in OrderedMembers(typesNode))
            {
                var typeName = TextField(type, "name");
                idMap[typeName] = typeId;

                // Emit prop entries ONLY when the projection actually prints props for this type. Project's
                // ENUM branch hardcodes Props: null (an enum carries a value list, no props), so an enum's
                // props NEVER reach the document — even if leftover MetaProp members linger in its set after
                // a base-type flip (object → enum). Mirror that exclusion here so the map keys EXACTLY what
                // the printed doc shows: a phantom "EnumType.leftoverProp" entry would make a slice-4 diff
                // misclassify a prop the document has no trace of. Every other base (object, and the scalar
                // BaseTypes.IsName aliases) carries its props through the projection — keep walking those.
                if (TextField(type, "baseType") != "enum" && type.Fields.TryGetValue("props", out var propsNode))
                    foreach (var (propId, prop) in OrderedMembers(propsNode))
                        idMap[$"{typeName}.{TextField(prop, "name")}"] = propId;
            }

        return new DesignSnapshot(text, idMap);
    }

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
        // before their keyword, so the spacing is cosmetic / canonical). ORDER MATTERS — it must match
        // the document grammar (types, initialData, access, common, ui) so the reassembled text parses;
        // `access` (the M-auth ruleset, incl. the host-action `sys` subject) sits between initialData and
        // common, exactly as AppParse.Document expects.
        var sections = new List<string> { typesSection.TrimEnd('\n') };
        if (design is ObjectValue d)
            foreach (var name in new[] { "initialData", "access", "common", "ui" })
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
                        // keyType is meaningful ONLY for a dictionary. The designer now renders the key-type
                        // field only when the cardinality IS dictionary (progressive disclosure), but a
                        // single/set prop could still carry a leftover value from a hand-written document —
                        // ignore it here unless dictionary (a set that declared a keyType is rejected on load).
                        var keyType = cardinality == Cardinality.Dictionary
                            && TextField(prop, "keyType") is { Length: > 0 } key ? key : null;
                        // `multiline` is a presentation flag valid ONLY on a single text prop (the loader
                        // rejects it elsewhere). The designer's toggle is shown only for that shape, but a
                        // hand-written document could carry a stale flag on a retyped prop — so project it
                        // ONLY when the prop is still a single text prop, mirroring how keyType is ignored
                        // off a dictionary. A missing field defaults false (the same defensive read).
                        var propType = TextField(prop, "type");
                        var multiline = cardinality == Cardinality.Single
                            && propType == "text"
                            && BoolField(prop, "multiline");
                        props.Add(new PropDefinition(
                            TextField(prop, "name"),
                            propType,
                            cardinality,
                            keyType,
                            Multiline: multiline));
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

    // Write an already-projected, already-validated app document onto a target instance, PRESERVING
    // its existing data across the schema change when the data still fits (non-destructive apply — the
    // migration substrate under M13 versioning; see DECISIONS "Data must survive schema changes").
    // Shared by publish/apply/create paths that already projected a whole Design row.
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
        {
            // Carry the data forward (drop removed fields, convert type-changed scalars). Values that
            // could not be converted are reset to default and REPORTED here — non-silent, not corruption.
            var reset = JsonFileInstanceStore.MigrateTowardSchema(targetDataPath, newDesc);
            if (reset.Count > 0)
                Console.Error.WriteLine(
                    $"[non-destructive apply] {reset.Count} value(s) could not be converted to the new " +
                    $"type and were reset to default: {string.Join(", ", reset)}");
        }

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

    // ── helpers ─────────────────────────────────────────────────────────────────

    // Member objects of a set node, sorted by the `order` field then by identity
    // (identity as a stable tiebreak / fallback when order is absent or equal).
    private static IEnumerable<ObjectValue> OrderedObjects(NodeValue? set) =>
        OrderedMembers(set).Select(m => m.Obj);

    // Same ordering as OrderedObjects (by `order` then by identity), but keeping each member's intrinsic
    // set-key id alongside its object — what Snapshot's id-map walk needs and OrderedObjects discards.
    private static IEnumerable<(int Id, ObjectValue Obj)> OrderedMembers(NodeValue? set)
    {
        if (set is not SetValue sv)
            return [];

        return sv.Members
            .Where(e => e.Value is ObjectValue)
            .Select(e => (Id: e.Key, Obj: (ObjectValue)e.Value, order: IntField((ObjectValue)e.Value, "order")))
            .OrderBy(x => x.order).ThenBy(x => x.Id)
            .Select(x => (x.Id, x.Obj));
    }

    private static string TextField(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is TextValue t ? t.Text : "";

    private static int IntField(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is IntValue i ? i.Value : 0;

    // A bool meta-field, defaulting false when absent — the same defensive read as TextField/IntField,
    // so a MetaProp that predates the `multiline` field (or any node missing it) reads false, not error.
    private static bool BoolField(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is BoolValue b && b.Value;
}
