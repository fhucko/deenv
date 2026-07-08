using DeEnv.Code;
using DeEnv.Designer;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Anti-regression guards for the committed operator-IDE source (DeEnv/instances/1/app.app). The smell
// removed: the designer used to embed a duplicated, escaped-string copy of EVERY hosted app
// (todo/crm/shop/itself) inside its single file as `initialData`, kept in sync by a generator. Now each
// app's own instances/<id>/app.app is the single source of truth and the kernel reverse-projects them
// into the design library at first boot — so the designer file must be ONLY its meta-schema (`types`)
// plus its hand-rolled `ui`, with NO embedded peer app source.
public sealed class DesignerSourceTests
{
    private static string DesignerSource => File.ReadAllText(InstanceContext.AppFixture(1));

    // The designer file declares no `initialData` section at all: parsing it yields a description whose
    // InitialData is absent. The whole embedded design library (the duplication) is gone — designs come
    // from the kernel's first-boot reverse-projection of each app's own document, not from this file.
    [Test]
    public async Task The_designer_document_embeds_no_initialData_seed()
    {
        var desc = AppParse.Parse(DesignerSource);
        await Assert.That(desc.InitialData).IsNull();
    }

    // The designer file does not contain any peer app's source as text content. The previous embedded
    // seed carried todo/crm/shop's type names and UI text as escaped strings; their absence proves no
    // peer app source is duplicated here. (These tokens are specific to the OTHER apps — the designer's
    // own meta-schema is Db/Design/MetaType/MetaProp, never TodoItem/Customer/Product.)
    [Test]
    public async Task The_designer_document_does_not_embed_peer_app_source()
    {
        var source = DesignerSource;
        // Type names that belong to the hosted apps, never to the designer's own meta-schema.
        foreach (var peerTypeName in new[] { "TodoItem", "TodoList", "Customer", "Product", "Order" })
            await Assert.That(source).DoesNotContain(peerTypeName);

        // No escaped-string section source: the embedded seed carried each app's whole document as
        // `\n`-escaped string literals (e.g. `ui: "ui\n    fn render()\n..."`), so the file was littered
        // with the literal two-char escape `\n`. The designer's own hand-written UI uses REAL newlines,
        // never that escape — so the embedded duplication is exactly what introduced `\n` literals.
        // Their total absence is the precise anti-regression for the smell.
        await Assert.That(source).DoesNotContain("\\n");

        // No embedded `Design` initialData blob: the designer file declares a `Design` TYPE (its
        // meta-schema), but must not seed any `Design` OBJECT (`    Design <id>` in an initialData
        // section) — those are the reverse-projected designs the kernel seeds at runtime.
        await Assert.That(source).DoesNotContain("    Design 13");
        await Assert.That(source).DoesNotContain("    Design 27");
    }

    // The cleaned file still round-trips (parse∘print is the identity, the printed form a fixpoint),
    // exactly as AppPrintTests.The_crm_and_designer_documents_round_trip requires — the rewrite must not
    // have produced a document the printer can't reproduce.
    [Test]
    public async Task The_cleaned_designer_document_round_trips()
    {
        var first = AppParse.Parse(DesignerSource);
        var printed = AppPrint.Print(first);
        var second = AppParse.Parse(printed);
        await Assert.That(AppPrint.Print(second)).IsEqualTo(printed);
    }

    // Every committed app reverse-projects (DesignerSeed — the kernel's first-boot path) into a Design
    // that forward-projects (SchemaBridge.ProjectDesignDocument — the publish path) back to the SAME app
    // document. This inherits the intent of the deleted DesignerSeedGenerator consistency guard ("editing
    // crm in the IDE edits the REAL crm, and Publish re-publishes the REAL crm") — but now WITHOUT its
    // self-reference exception: the designer's own app (id 1) carries an empty initialData, so its
    // self-design round-trips FAITHFULLY too (the old embedded-seed model could not, so it skipped the
    // designer). Comparison is normalized through parse∘print, so only semantic equality matters.
    [Test]
    public async Task Every_committed_app_reverse_then_forward_projects_to_itself()
    {
        // The committed apps the kernel seeds as designs (1 = designer, 2 = todo, 3 = crm, 4 = shop),
        // with arbitrary distinct designIds (the ids are not load-bearing for THIS round-trip — that the
        // id EQUALS the designId is covered by the kernel seeding scenario).
        var apps = new (int Id, int DesignId)[] { (1, 60), (2, 13), (3, 27), (4, 39) };

        foreach (var (id, designId) in apps)
        {
            var committed = File.ReadAllText(InstanceContext.AppFixture(id));

            // Reverse-project this one app into a one-design seed, seed a throwaway store from it (so the
            // Design is a live node the publish path reads by id), read it back, and forward-project it.
            var seed = DesignerSeed.Build([("app-" + id, designId, committed)], []);
            var designerDesc = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1))
                with { InitialData = seed };

            var storePath = Path.Combine(Path.GetTempPath(), "deenv-rtcheck-" + Guid.NewGuid().ToString("N") + ".json");
            var store = new JsonFileInstanceStore(storePath, designerDesc);
            try
            {
                var design = store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString()))!;
                var projected = SchemaBridge.ProjectDesignDocument(design);
                await Assert.That(Canonical(projected)).IsEqualTo(Canonical(committed));
            }
            finally
            {
                File.Delete(storePath);
            }
        }
    }

    // The canonical printed form, so two documents compare by semantics not incidental whitespace.
    private static string Canonical(string appDoc) => AppPrint.Print(AppParse.Parse(appDoc));

    // ── M12 S1a: structured render tree → canonical fn render() ───────────────────

    // A structured render (Design.render, a MetaNode tree) projects to the CANONICAL `fn render()` text —
    // the same text the equivalent hand-written custom UI would print. Proven at the projection boundary
    // (ProjectDesignDocument output contains the canonical render fn), beside the round-trip guards.
    [Test]
    public async Task ProjectDesignDocument_projects_a_structured_render_to_a_canonical_render_fn()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-s1a-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("hello-app"),
                ["ui"]    = new TextValue(""), // empty — the render tree is the authority
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId); // a custom-UI app still needs a valid Db root type

            // main.hello > h1 > "Hi", built top-down so no node is transiently unreachable (the store GCs
            // unreferenced objects on link mutations). `render` is now a SET (holding exactly one root), so
            // the root itself is addressed as a set member (Key(main)), like any other set-owned object.
            var designPath = NodePath.Root.Field("designs").Key(designId.ToString());
            var main = store.CreateObject("MetaNode", Node("main"));
            store.AddToSet(designPath.Field("render"), main);
            var mainPath = designPath.Field("render").Key(main.ToString());
            var cls = store.CreateObject("MetaAttr", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["name"] = new TextValue("class"), ["value"] = new TextValue("\"hello\""), ["order"] = new IntValue(0),
            }));
            store.AddToSet(mainPath.Field("attrs"), cls);
            var h1 = store.CreateObject("MetaNode", Node("h1"));
            store.AddToSet(mainPath.Field("children"), h1);
            var hi = store.CreateObject("MetaNode", Node("", "\"Hi\""));
            store.AddToSet(mainPath.Field("children").Key(h1.ToString()).Field("children"), hi);

            var design = store.ReadNode(designPath)!;
            var projected = SchemaBridge.ProjectDesignDocument(design);

            // The exact canonical render fn the printer produces for the equivalent hand-written UI.
            var expectedUi = AppPrint.PrintUi(CodeParse.ParseUiSection(
                "ui\n    fn render()\n        return <main class=\"hello\">\n            <h1>\n                \"Hi\"\n"));
            await Assert.That(projected).Contains(expectedUi.TrimEnd('\n'));
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // ProjectDesignDocument REFUSES a design carrying both a structured render tree AND a non-empty `ui`
    // text field — the user-decided precedence (the render tree owns the `ui` section, so the text must be
    // empty), surfaced as a SchemaValidationException rather than silently picking one.
    [Test]
    public async Task ProjectDesignDocument_refuses_a_render_tree_alongside_a_non_empty_ui_text()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-s1a-both-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("clash"),
                ["ui"]    = new TextValue("ui\n    fn render()\n        return <main>\n            \"x\"\n"),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            var main = store.CreateObject("MetaNode", Node("main"));
            store.AddToSet(NodePath.Root.Field("designs").Key(designId.ToString()).Field("render"), main);

            var design = store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString()))!;
            var ex = await Assert.That(() => SchemaBridge.ProjectDesignDocument(design))
                .Throws<SchemaValidationException>();
            await Assert.That(ex!.Message).Contains("both a structured `render`");
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    private static ObjectValue Node(string tag, string expr = "") =>
        new(new Dictionary<string, NodeValue>
        {
            ["tag"] = new TextValue(tag), ["expr"] = new TextValue(expr), ["order"] = new IntValue(0),
        });

    private static void AddDbType(JsonFileInstanceStore store, int designId)
    {
        var typesPath = NodePath.Root.Field("designs").Key(designId.ToString()).Field("types");
        var db = store.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("Db"), ["baseType"] = new TextValue("object"), ["order"] = new IntValue(0),
        }));
        store.AddToSet(typesPath, db);
        var greeting = store.CreateObject("MetaProp", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("greeting"), ["type"] = new TextValue("text"), ["order"] = new IntValue(0),
        }));
        store.AddToSet(typesPath.Key(db.ToString()).Field("props"), greeting);
    }

    // ── M12 S0: canonicalize-on-project for the `ui` section ──────────────────────

    // The `ui` canonicalization primitive: a non-canonically-formatted section round-trips through
    // parse∘print to the printer's canonical form, and canonicalizing again is the identity (the
    // fixpoint). This is exactly what ProjectDesignDocument uses to keep the commit/publish artifact
    // stable regardless of how the render code was typed.
    [Test]
    public async Task A_ui_section_canonicalizes_through_parse_then_print()
    {
        var messy = "ui\n  fn render()\n    return <main class=\"x\">\n      \"hi\"\n"; // 2-space nesting
        var canonical = AppPrint.PrintUi(CodeParse.ParseUiSection(messy));

        await Assert.That(canonical).Contains("\n    fn render()");   // re-indented to the canonical 4 spaces
        await Assert.That(messy).DoesNotContain("\n    fn render()");  // the input genuinely was not canonical
        // Idempotent: canonical text canonicalizes to itself.
        await Assert.That(AppPrint.PrintUi(CodeParse.ParseUiSection(canonical))).IsEqualTo(canonical);
    }

    // ProjectDesignDocument emits a CANONICAL `ui` section even when the design carries a
    // non-canonically-formatted one (DesignerSeed carries `ui` verbatim, so this reverse→forward path
    // would otherwise reproduce the messy form). Only `ui` is re-printed; the wiring is what makes the
    // commit/publish artifact stable across authoring formatting (M12 S0).
    [Test]
    public async Task ProjectDesignDocument_canonicalizes_the_ui_section()
    {
        var messyUi = "ui\n  fn render()\n    return <main class=\"x\">\n      \"hi\"\n";
        var appDoc = "types\n    Db\n        greeting text\n\n" + messyUi;

        // Reverse-project into a live Design node (ui carried verbatim), then forward-project it.
        var seed = DesignerSeed.Build([("app-messy", 71, appDoc)], []);
        var designerDesc = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1))
            with { InitialData = seed };
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-s0-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, designerDesc);
        try
        {
            var design = store.ReadNode(NodePath.Root.Field("designs").Key("71"))!;
            var projected = SchemaBridge.ProjectDesignDocument(design);
            await Assert.That(projected).Contains("\n    fn render()");     // canonical 4-space indent
            await Assert.That(projected).DoesNotContain("\n  fn render()");  // the verbatim 2-space form is gone
        }
        finally
        {
            File.Delete(storePath);
        }
    }
}
