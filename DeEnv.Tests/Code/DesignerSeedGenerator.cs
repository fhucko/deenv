using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Designer;
using DeEnv.Instance;
using DeEnv.Kernel;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Authoring-time generator for the operator IDE's seed (DeEnv/instances/1/app.app).
//
// The designer seeds its `db.designs` set with a faithful `Design` of every committed app the kernel
// hosts (instances/2 "instance" = todo, 3 = crm, 4 = shop — see kernel.json). Hand-authoring those
// apps' full ui/common/initialData as `\n`-escaped string literals inside instances/1/app.app is
// impractical, so this GENERATES the file at authoring time (NOT a runtime/boot load — the no-load
// model stands): it reads the committed sources, splits each into its top-level sections, parses the
// `types` section into the structured MetaType/MetaProp the type editor edits, keeps the other three
// sections as VERBATIM text (keyword + body, "" when absent — the exact representation SchemaBridge's
// Design text fields carry and ProjectDesignDocument reassembles), assembles the designer's
// InstanceDescription (its OWN types + its OWN ui/common — the IDE render is preserved exactly, NOT
// regenerated — plus an initialData whose root Db holds the generated designs), AppPrints it, and
// overwrites the committed instances/1/app.app.
//
// A design's `label` is the instance's registry `app` field (the IDE's instance↔design matching key),
// read from the source kernel.json by id — the committed app document itself does not carry its label.
//
// It is [Explicit] so it never runs in the normal suite (it rewrites a committed source file); run it
// only to (re)generate the seed after the committed apps change:
//
//   dotnet test --filter "/*/*/DesignerSeedGenerator/*"
//
// Then review + commit the regenerated instances/1/app.app. The regenerated file must stay round-trip
// stable — AppPrintTests.The_crm_and_designer_documents_round_trip pins parse(print(it)) ≡ it.
public sealed class DesignerSeedGenerator
{
    // Which committed apps become seeded designs: the kernel-hosted data apps (instance/crm/shop) AND
    // the designer itself (id 1). The designer is a managed instance like any other, so it is uniform —
    // it has a design in `db.designs` and an instances-list row that resolves to it. Ordered with the
    // designer LAST so the existing three designs keep their seeded ids (13/27/39) — the committed
    // kernel.json pins those, so appending (not prepending) avoids churning them.
    //
    // The designer's design is the ONE inherent non-uniformity: a thing cannot contain itself. Its OWN
    // initialData IS this design library, so projecting it verbatim would recurse/grow on every
    // regeneration. So the designer design carries an EMPTY initialData (a bounded self-snapshot — its
    // types + ui/common verbatim, no seed); everything operator-facing stays uniform. See AddDesign.
    private static readonly int[] SeededInstanceIds = [2, 3, 4, 1];

    // The id of the designer instance (and the app whose design is the bounded self-snapshot).
    private const int DesignerId = 1;

    [Test, Explicit]
    public async Task Generate_the_designer_seed_from_the_committed_apps()
    {
        var root = RepoRoot();
        var designerPath = Path.Combine(root, "DeEnv", "instances", "1", "app.app");

        // id → registry `app` label, from the source kernel.json (the design's label is the instance's
        // label by the IDE's matching convention; the app document does not carry it).
        var labels = RegistryReader.Read(Path.Combine(root, "DeEnv", "kernel.json"))
            .Instances.ToDictionary(e => e.Id, e => e.App);

        // The designer's OWN description (its Db { designs } types + its hand-rolled ui/common render).
        // Parsed from the current committed file so the IDE render is preserved byte-for-byte through
        // the reprint (it is already an AppPrint fixpoint); only its initialData is replaced below.
        var designer = AppParse.Parse(File.ReadAllText(designerPath));

        // Build the designs seed: Db (root, id 1) → a `designs` set of Design objects, each carrying the
        // committed app's structured types (MetaType/MetaProp) + its other sections as verbatim text.
        var seed = new SeedBuilder();
        var designIds = new List<int>();
        foreach (var id in SeededInstanceIds)
        {
            var appText = File.ReadAllText(Path.Combine(root, "DeEnv", "instances", id.ToString(), "app.app"));
            // The designer design is a bounded self-snapshot: project its types + ui/common verbatim, but
            // force its initialData to "" (do NOT copy the designer's own initialData — that IS this seed,
            // so copying it would recurse/grow each regeneration). A thing cannot contain itself.
            designIds.Add(seed.AddDesign(labels[id], appText, emptyInitialData: id == DesignerId));
        }
        seed.AddRootDb(designIds);

        var regenerated = new InstanceDescription(
            Types: designer.Types,
            Ui: designer.Ui,
            Common: designer.Common,
            InitialData: seed.Build());

        var printed = AppPrint.Print(regenerated);

        // Round-trip guard (the FORK-1 check): the regenerated document must parse back to an identical
        // description and be an AppPrint fixpoint — otherwise the escaped-text seed does not survive a
        // parse∘print cycle and we must report rather than commit a broken file.
        var reparsed = AppParse.Parse(printed);
        await Assert.That(AppPrint.Print(reparsed)).IsEqualTo(printed);

        // Write the committed format DIRECTLY — UTF-8 WITH BOM and CRLF line endings. AppPrint emits LF
        // and no BOM, but the committed app.app is BOM+CRLF; emitting it here makes a regeneration produce
        // a clean `git diff` with no manual encoding re-emit. (The escaped `\n` inside the seed strings are
        // two literal chars, not newlines, so the CRLF conversion only touches real line terminators; the
        // double-replace collapses any stray CRLF first so no `\r\r\n` can appear.)
        var committedForm = printed.Replace("\r\n", "\n").Replace("\n", "\r\n");
        File.WriteAllText(designerPath, committedForm, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    // Regression guard for the committed seed (runs in the normal suite — NOT explicit): every seeded
    // design must project, via the REAL publish path (SchemaBridge.ProjectDesignDocument), back to the
    // committed app it was generated from. This pins the deliverable — "editing crm in the IDE edits the
    // REAL crm, and Publish re-publishes the REAL crm" — and fails loudly if the committed apps drift from
    // the seed (a signal to re-run the generator). Comparison is normalized through parse∘print, so only
    // semantic equality matters, not incidental whitespace.
    [Test]
    public async Task The_seeded_designs_project_back_to_the_committed_apps()
    {
        // The registry label → id map (a design's label is its instance's registry `app`), from the
        // committed kernel.json in the source tree (it is not copied to the test output).
        var labels = RegistryReader.Read(KernelJsonPath())
            .Instances.ToDictionary(e => e.App, e => e.Id);

        // Load the committed designer seed (the test-output copy) and seed a throwaway store from it, so
        // its designs are live nodes the publish path can read by id.
        var designer = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-seedcheck-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, designer);
        try
        {
            var designs = (SetValue)((ObjectValue)store.ReadNode(NodePath.Root)!).Fields["designs"];
            foreach (var (memberId, member) in designs.Members)
            {
                var design = (ObjectValue)member;
                var label = ((TextValue)design.Fields["label"]).Text;

                // The designer's OWN design is a bounded self-snapshot, not a faithful round-trip: a thing
                // cannot contain itself, so its initialData is intentionally empty while the committed
                // designer (instances/1/app.app) carries the full design-library seed. Projecting it back
                // therefore yields the designer minus its seed — NOT byte-identical to the committed file.
                // Skip it here (the self-reference limit); the other three MUST still round-trip faithfully.
                if (label == "designer") continue;

                // The committed app this design was generated from (by label → id).
                var committed = File.ReadAllText(InstanceContext.AppFixture(labels[label]));

                // Project the design subtree exactly as a publish would, then compare both documents
                // normalized to the canonical printed form (so seed-order / spacing never matters).
                var projected = SchemaBridge.ProjectDesignDocument(
                    store.ReadNode(NodePath.Root.Field("designs").Key(memberId.ToString()))!);

                await Assert.That(Canonical(projected)).IsEqualTo(Canonical(committed));
            }
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // The canonical printed form of an app document, so two documents are compared by semantics, not by
    // incidental whitespace / section order.
    private static string Canonical(string appDoc) => AppPrint.Print(AppParse.Parse(appDoc));

    // The kernel.json shipped beside the test fixtures (the committed registry), giving id → label.
    private static string KernelJsonPath() =>
        Path.Combine(RepoRoot(), "DeEnv", "kernel.json");

    // Walk up from the test output dir to the repository root (the dir holding DeEnv/instances/1/app.app),
    // so the generator reads + writes the SOURCE tree, not the copied test output.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DeEnv", "instances", "1", "app.app")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repository root (DeEnv/instances/1/app.app).");
    }

    // Split an app document into its top-level sections, keyed by section keyword (types / initialData /
    // common / ui), each value the VERBATIM section text INCLUDING its keyword line and trailing newline,
    // e.g. "ui\n    fn render()\n        return <main>\n            \"hi\"\n". A section runs from its
    // column-0 keyword line through the line before the next column-0 keyword (or EOF), with trailing
    // blank lines trimmed and a single closing newline — the exact form SchemaBridge's Design text fields
    // carry. Only the four known section keywords start a section (so an unindented blank line never
    // does); every app document begins with one (`types`), so there is no leading non-section content.
    private static Dictionary<string, string> SplitSections(string appText)
    {
        var keywords = new HashSet<string> { "types", "initialData", "common", "ui" };
        var lines = appText.Replace("\r\n", "\n").Split('\n');
        var sections = new Dictionary<string, string>();

        string? current = null;
        var body = new List<string>();
        void Flush()
        {
            if (current == null) return;
            while (body.Count > 0 && body[^1].Length == 0) body.RemoveAt(body.Count - 1);
            sections[current] = string.Join("\n", body) + "\n";
        }

        foreach (var line in lines)
        {
            if (keywords.Contains(line))
            {
                Flush();
                current = line;
                body = [line];
            }
            else if (current != null)
            {
                body.Add(line);
            }
        }
        Flush();
        return sections;
    }

    // Accumulates the flat, id-keyed initialData pools (Db / Design / MetaType / MetaProp) the app
    // document's initialData section expresses: every object is a top-level entry with a unique id, sets
    // are arrays of member ids. Ids are assigned sequentially from 2 (the root Db is id 1).
    private sealed class SeedBuilder
    {
        private readonly Dictionary<string, Dictionary<string, JsonElement>> _pools = new();
        private int _nextId = 2;

        // Add one committed app as a Design: structured types (reverse-projected from the parsed `types`
        // section) + the other three sections as verbatim text. Returns the Design's id. When
        // `emptyInitialData` is set (the designer's own design — a thing cannot contain itself), the
        // initialData field is forced to "" so the self-reference is bounded and regeneration is stable.
        public int AddDesign(string label, string appText, bool emptyInitialData = false)
        {
            var desc = AppParse.Parse(appText);
            var sections = SplitSections(appText);

            var typeIds = new List<int>();
            var typeOrder = 1;
            foreach (var type in desc.AllTypes())
                typeIds.Add(AddType(type, typeOrder++ * 10));

            var fields = new JsonObject
            {
                ["label"] = label,
                // The other sections VERBATIM (keyword + body, "" when absent). initialData / common /
                // ui are carried as text exactly as SchemaBridge's Design text fields expect. The
                // designer design's initialData is forced empty (bounded self-snapshot).
                ["initialData"] = emptyInitialData ? "" : sections.GetValueOrDefault("initialData", ""),
                ["common"] = sections.GetValueOrDefault("common", ""),
                ["ui"] = sections.GetValueOrDefault("ui", ""),
                ["types"] = IdArray(typeIds),
            };
            return Add("Design", fields);
        }

        public void AddRootDb(IReadOnlyList<int> designIds) =>
            Pool("Db")["1"] = ToElement(new JsonObject { ["designs"] = IdArray(designIds) });

        public InstanceInitialData Build()
        {
            var extents = _pools.ToDictionary(
                e => e.Key,
                e => (IReadOnlyDictionary<string, JsonElement>)e.Value);
            return new InstanceInitialData(extents);
        }

        // A MetaType seed (name + baseType + order + its props), reverse-projecting a TypeDefinition the
        // same way the type editor / SchemaBridge.Project round-trip it. Returns the MetaType's id.
        private int AddType(TypeDefinition type, int order)
        {
            var propIds = new List<int>();
            var propOrder = 1;
            foreach (var prop in type.Props ?? [])
                propIds.Add(AddProp(prop, propOrder++ * 10));

            var fields = new JsonObject
            {
                ["name"] = type.Name,
                // `object` for object types; the lowercase leaf name for a leaf alias. Mirrors how
                // SchemaBridge.Project reads baseType back (== "object" or a BaseTypes name).
                ["baseType"] = type.BaseType == BaseType.Object
                    ? "object"
                    : BaseTypes.NameOf(type.BaseType),
                ["order"] = order,
                ["props"] = IdArray(propIds),
            };
            return Add("MetaType", fields);
        }

        // A MetaProp seed. cardinality / keyType are emitted only when they deviate from the single,
        // keyless default (matching the existing seed + SchemaBridge.Project's reverse mapping, which
        // reads an absent cardinality as single) — minimal by default.
        private int AddProp(PropDefinition prop, int order)
        {
            var fields = new JsonObject
            {
                ["name"] = prop.Name,
                ["type"] = prop.Type,
                ["order"] = order,
            };
            if (prop.Cardinality == Cardinality.Set)
                fields["cardinality"] = "set";
            else if (prop.Cardinality == Cardinality.Dictionary)
            {
                fields["cardinality"] = "dictionary";
                fields["keyType"] = prop.KeyType ?? "text";
            }
            return Add("MetaProp", fields);
        }

        private int Add(string type, JsonObject fields)
        {
            var id = _nextId++;
            Pool(type)[id.ToString()] = ToElement(fields);
            return id;
        }

        private Dictionary<string, JsonElement> Pool(string type)
        {
            if (!_pools.TryGetValue(type, out var pool))
                _pools[type] = pool = new Dictionary<string, JsonElement>();
            return pool;
        }

        private static JsonArray IdArray(IReadOnlyList<int> ids) =>
            new(ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

        private static JsonElement ToElement(JsonObject obj) =>
            JsonSerializer.SerializeToElement(obj);
    }
}
