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
            var seed = DesignerSeed.Build([("app-" + id, designId, committed)]);
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
}
