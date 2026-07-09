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

    // ── M12 S1b: import (`ui` render text → structured MetaNode rows) ─────────────

    // The import walk mirrors the tag tree into MetaNode/MetaAttr rows: nesting depth, MULTIPLE attributes
    // in order, a TEXT leaf, and an EXPRESSION leaf. Proven at the row level (read the minted MetaNode tree
    // back and check its shape) AND at the round-trip boundary (project the imported design and compare to
    // the canonical original). A multi-attr element with both a nested element child and an expression leaf
    // exercises every ImportNode branch.
    [Test]
    public async Task ImportRender_mints_the_row_tree_and_round_trips()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-s1b-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            // <main class="hello" id="root"> with a nested <h1>"Hi" AND a bare-symbol expression leaf `db.greeting`.
            var ui = "ui\n    fn render()\n        return <main class=\"hello\" id=\"root\">\n"
                   + "            <h1>\n                \"Hi\"\n            db.greeting\n";
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("s1b"), ["ui"] = new TextValue(ui),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);

            SchemaBridge.ImportRender(store, designId);

            var designPath = NodePath.Root.Field("designs").Key(designId.ToString());

            // The `ui` text field was cleared (so the S1a gate accepts the structured render).
            var design = (ObjectValue)store.ReadNode(designPath)!;
            await Assert.That(((TextValue)design.Fields["ui"]).Text).IsEqualTo("");

            // Root: <main> with two attrs in order (class, id) and two children (the <h1> element, then the
            // db.greeting leaf).
            var root = OrderedByOrder((SetValue)design.Fields["render"]).Single();
            await Assert.That(Text(root, "tag")).IsEqualTo("main");
            var attrs = OrderedByOrder((SetValue)root.Fields["attrs"]).ToList();
            await Assert.That(attrs.Select(a => Text(a, "name")).ToList()).IsEquivalentTo(new[] { "class", "id" });
            await Assert.That(Text(attrs[0], "value")).IsEqualTo("\"hello\"");
            await Assert.That(Text(attrs[1], "value")).IsEqualTo("\"root\"");

            var children = OrderedByOrder((SetValue)root.Fields["children"]).ToList();
            await Assert.That(children.Count).IsEqualTo(2);
            // Child 0: the nested <h1> element carrying a text leaf.
            await Assert.That(Text(children[0], "tag")).IsEqualTo("h1");
            var h1Kids = OrderedByOrder((SetValue)children[0].Fields["children"]).Single();
            await Assert.That(Text(h1Kids, "tag")).IsEqualTo("");            // a leaf: empty tag
            await Assert.That(Text(h1Kids, "expr")).IsEqualTo("\"Hi\"");    // the text leaf's source
            // Child 1: the expression leaf `db.greeting` (empty tag, expr carries the source).
            await Assert.That(Text(children[1], "tag")).IsEqualTo("");
            await Assert.That(Text(children[1], "expr")).IsEqualTo("db.greeting");

            // The round-trip: projecting the imported design yields the canonical form of the original render.
            var projected = SchemaBridge.ProjectDesignDocument(design);
            var expectedUi = AppPrint.PrintUi(CodeParse.ParseUiSection(ui)).TrimEnd('\n');
            await Assert.That(projected).Contains(expectedUi);
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // ── M12 S6a: `foreach`/`if` render forms import to structured `kind="for"`/`kind="if"` rows ──

    // Import LIFTS the old for/if refusal: a `foreach` loop mints a `kind="for"` row (item + collection
    // source, body under `children`), and an `if`/`else` mints a `kind="if"` row (condition source, the
    // then-branch under `children`, the else-branch under `elseChildren`). Proven at the row level AND at
    // the round-trip boundary — the load-bearing S6a proof.
    [Test]
    public async Task ImportRender_converts_a_foreach_loop_and_an_if_else_to_structured_rows_and_round_trips()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-s6a-forif-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var ui = "ui\n    fn render()\n        return <main>\n"
                   + "            foreach note in db.notes\n"
                   + "                <li>\n                    note.title\n"
                   + "            if db.greeting == \"hi\"\n"
                   + "                <p>\n                    \"hello\"\n"
                   + "            else\n"
                   + "                <p>\n                    \"bye\"\n";
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("forif"), ["ui"] = new TextValue(ui),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);

            SchemaBridge.ImportRender(store, designId);

            var design = (ObjectValue)store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString()))!;
            await Assert.That(((TextValue)design.Fields["ui"]).Text).IsEqualTo(""); // cleared

            var root = OrderedByOrder((SetValue)design.Fields["render"]).Single();
            var children = OrderedByOrder((SetValue)root.Fields["children"]).ToList();
            await Assert.That(children.Count).IsEqualTo(2);

            var forRow = children[0];
            await Assert.That(Text(forRow, "kind")).IsEqualTo("for");
            await Assert.That(Text(forRow, "item")).IsEqualTo("note");
            await Assert.That(Text(forRow, "collection")).IsEqualTo("db.notes");
            var forBody = OrderedByOrder((SetValue)forRow.Fields["children"]).Single();
            await Assert.That(Text(forBody, "tag")).IsEqualTo("li");

            var ifRow = children[1];
            await Assert.That(Text(ifRow, "kind")).IsEqualTo("if");
            await Assert.That(Text(ifRow, "condition")).IsEqualTo("db.greeting == \"hi\"");
            var thenBody = OrderedByOrder((SetValue)ifRow.Fields["children"]).Single();
            await Assert.That(Text(thenBody, "tag")).IsEqualTo("p");
            var elseBody = OrderedByOrder((SetValue)ifRow.Fields["elseChildren"]).Single();
            await Assert.That(Text(elseBody, "tag")).IsEqualTo("p");

            // The round-trip: projecting the imported design yields the canonical form of the original render.
            var projected = SchemaBridge.ProjectDesignDocument(design);
            var expectedUi = AppPrint.PrintUi(CodeParse.ParseUiSection(ui)).TrimEnd('\n');
            await Assert.That(projected).Contains(expectedUi);
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // The else-if fixpoint (M12 S6a review fix 2 — the load-bearing corner of the printer collapse):
    // CodePrint.TagIf prints an `if` whose ElseBody is EXACTLY ONE CodeTagIf as `else if …` (not a nested
    // `else` block wrapping an `if`) — so an imported `if … else if … else …` must mint an OUTER kind="if"
    // row whose `elseChildren` holds exactly ONE NESTED kind="if" row (never two flat rows), which in turn
    // carries the final `else` body in ITS OWN `elseChildren`. Proven at the row shape AND the round-trip.
    [Test]
    public async Task ImportRender_converts_an_else_if_chain_to_a_nested_if_row_and_round_trips()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-s6a-elseif-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var ui = "ui\n    fn render()\n        return <main>\n"
                   + "            if db.a\n"
                   + "                <p>\n                    \"A\"\n"
                   + "            else if db.b\n"
                   + "                <p>\n                    \"B\"\n"
                   + "            else\n"
                   + "                <p>\n                    \"C\"\n";
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("elseif"), ["ui"] = new TextValue(ui),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);

            SchemaBridge.ImportRender(store, designId);

            var design = (ObjectValue)store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString()))!;
            await Assert.That(((TextValue)design.Fields["ui"]).Text).IsEqualTo(""); // cleared

            var root = OrderedByOrder((SetValue)design.Fields["render"]).Single();
            var outerIf = OrderedByOrder((SetValue)root.Fields["children"]).Single();
            await Assert.That(Text(outerIf, "kind")).IsEqualTo("if");
            await Assert.That(Text(outerIf, "condition")).IsEqualTo("db.a");
            var outerThen = OrderedByOrder((SetValue)outerIf.Fields["children"]).Single();
            await Assert.That(Text(outerThen, "tag")).IsEqualTo("p");

            // The outer if's elseChildren holds EXACTLY ONE row, and it is itself a NESTED kind="if" row —
            // not two flat sibling rows — the shape the printer's else-if collapse depends on.
            var outerElse = OrderedByOrder((SetValue)outerIf.Fields["elseChildren"]).ToList();
            await Assert.That(outerElse.Count).IsEqualTo(1);
            var nestedIf = outerElse[0];
            await Assert.That(Text(nestedIf, "kind")).IsEqualTo("if");
            await Assert.That(Text(nestedIf, "condition")).IsEqualTo("db.b");
            var nestedThen = OrderedByOrder((SetValue)nestedIf.Fields["children"]).Single();
            await Assert.That(Text(nestedThen, "tag")).IsEqualTo("p");
            var nestedElse = OrderedByOrder((SetValue)nestedIf.Fields["elseChildren"]).Single();
            await Assert.That(Text(nestedElse, "tag")).IsEqualTo("p");

            // The round-trip: projecting the imported design yields the canonical form of the original
            // render — the nested rows collapse back through CodePrint.TagIf to `else if`, not a nested
            // `else` block wrapping an `if`.
            var projected = SchemaBridge.ProjectDesignDocument(design);
            var expectedUi = AppPrint.PrintUi(CodeParse.ParseUiSection(ui)).TrimEnd('\n');
            await Assert.That(projected).Contains(expectedUi);
            await Assert.That(expectedUi).Contains("else if"); // the fixture actually exercises the collapse
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // The collector invariant (M12 S6a): SchemaBridge.RenderExprSources — the hand-kept parallel walk the
    // canvas's eval-context builder uses to find every expression the canvas walk (BuildRenderTree /
    // renderTreeNode) can look up — must never UNDER-collect. Pins the two S6a-added branches explicitly:
    // a `for` row's `collection` source, and — the easy-to-forget one — an expression INSIDE `elseChildren`
    // (a leaf nested in the else branch of an `if`). Over-collecting (e.g. a literal source) is harmless and
    // not asserted against.
    [Test]
    public async Task RenderExprSources_collects_a_for_collection_and_an_expr_inside_elseChildren()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-s6a-collect-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("collect"), ["ui"] = new TextValue(""),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);
            var designPath = NodePath.Root.Field("designs").Key(designId.ToString());

            // Root <main> holds a `for` row (collection = db.notes, body leaf note.title) and an `if` row
            // (condition = db.flag, then-leaf "\"on\"", else-leaf db.elseExpr — INSIDE elseChildren).
            var main = store.CreateObject("MetaNode", Node("main"));
            store.AddToSet(designPath.Field("render"), main);
            var mainPath = designPath.Field("render").Key(main.ToString());

            var forRow = store.CreateObject("MetaNode", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["kind"] = new TextValue("for"), ["item"] = new TextValue("note"),
                ["collection"] = new TextValue("db.notes"), ["order"] = new IntValue(0),
            }));
            store.AddToSet(mainPath.Field("children"), forRow);
            var forLeaf = store.CreateObject("MetaNode", Node("", "note.title"));
            store.AddToSet(mainPath.Field("children").Key(forRow.ToString()).Field("children"), forLeaf);

            var ifRow = store.CreateObject("MetaNode", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["kind"] = new TextValue("if"), ["condition"] = new TextValue("db.flag"), ["order"] = new IntValue(1),
            }));
            store.AddToSet(mainPath.Field("children"), ifRow);
            var ifPath = mainPath.Field("children").Key(ifRow.ToString());
            var thenLeaf = store.CreateObject("MetaNode", Node("", "\"on\""));
            store.AddToSet(ifPath.Field("children"), thenLeaf);
            var elseLeaf = store.CreateObject("MetaNode", Node("", "db.elseExpr"));
            store.AddToSet(ifPath.Field("elseChildren"), elseLeaf);

            var design = store.ReadNode(designPath)!;
            var sources = SchemaBridge.RenderExprSources(design);

            await Assert.That(sources).Contains("db.notes");     // the for row's collection
            await Assert.That(sources).Contains("note.title");   // the for body leaf
            await Assert.That(sources).Contains("db.flag");      // the if row's condition
            await Assert.That(sources).Contains("\"on\"");       // the then-branch leaf
            await Assert.That(sources).Contains("db.elseExpr");  // the ELSE-branch leaf — the easy-to-forget one
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // Import REFUSES a `ui` section that carries `var`s besides `fn render()`: clearing the whole `ui` text
    // would silently DROP them, so such a design stays as text. Nothing minted, `ui` untouched. (M12 F1
    // lifted the sibling helper-FUNCTION refusal this test used to cover — see the F1 tests below; the
    // `var` refusal STAYS, a later rung.)
    [Test]
    public async Task ImportRender_refuses_a_ui_with_vars_and_imports_nothing()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-s1b-vars-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var ui = "ui\n    var count = 0\n    fn render()\n        return <main>\n            \"hi\"\n";
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("vars"), ["ui"] = new TextValue(ui),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);

            var ex = await Assert.That(() => SchemaBridge.ImportRender(store, designId))
                .Throws<SchemaValidationException>();
            await Assert.That(ex!.Message).Contains("`var`s");

            var design = (ObjectValue)store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString()))!;
            await Assert.That(((SetValue)design.Fields["render"]).Members.Count).IsEqualTo(0); // nothing minted
            await Assert.That(((TextValue)design.Fields["ui"]).Text).IsEqualTo(ui);            // ui untouched
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // ── M12 F1: structured fns (helper + component functions → MetaFn rows) ───────

    // Import now LIFTS the helper/component-function refusal: a scalar helper (single-return ternary) and
    // a component (single-return element with a param) both mint MetaFn rows, in list order, and the
    // round-trip is the identity — the load-bearing F1 proof.
    [Test]
    public async Task ImportRender_converts_a_helper_and_a_component_function_to_MetaFn_rows_and_round_trips()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-f1-fns-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var ui = "ui\n"
                   + "    fn helperLabel(active)\n        return active ? \"Yes\" : \"No\"\n"
                   + "    fn NoteCard(note)\n        return <li>\n            note.title\n"
                   + "    fn render()\n        return <main>\n            \"hi\"\n";
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("fns"), ["ui"] = new TextValue(ui),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);

            SchemaBridge.ImportRender(store, designId);

            var design = (ObjectValue)store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString()))!;
            await Assert.That(((TextValue)design.Fields["ui"]).Text).IsEqualTo(""); // cleared

            var fns = OrderedByOrder((SetValue)design.Fields["fns"]).ToList();
            await Assert.That(fns.Count).IsEqualTo(2);
            await Assert.That(Text(fns[0], "name")).IsEqualTo("helperLabel");
            await Assert.That(Text(fns[0], "params")).IsEqualTo("active");
            var helperBody = OrderedByOrder((SetValue)fns[0].Fields["body"]).Single();
            await Assert.That(Text(helperBody, "tag")).IsEqualTo("");             // a leaf: the ternary source
            await Assert.That(Text(helperBody, "expr")).IsEqualTo("active ? \"Yes\" : \"No\"");

            await Assert.That(Text(fns[1], "name")).IsEqualTo("NoteCard");
            await Assert.That(Text(fns[1], "params")).IsEqualTo("note");
            var cardBody = OrderedByOrder((SetValue)fns[1].Fields["body"]).Single();
            await Assert.That(Text(cardBody, "tag")).IsEqualTo("li");

            // The round-trip: projecting the imported design reproduces the canonical original — render AND
            // both structured functions, in order.
            var projected = SchemaBridge.ProjectDesignDocument(design);
            var expectedUi = AppPrint.PrintUi(CodeParse.ParseUiSection(ui)).TrimEnd('\n');
            await Assert.That(projected).Contains(expectedUi);
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // Import refuses (imports NOTHING) when a top-level function is server-only — projecting it back would
    // silently ship a server-only function to the client, the security downgrade ServerOnly exists to
    // prevent.
    [Test]
    public async Task ImportRender_refuses_a_ui_with_a_server_only_function()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-f1-serveronly-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var ui = "ui\n    server fn secretHelper()\n        return \"shh\"\n    fn render()\n        return <main>\n            \"hi\"\n";
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("serveronly"), ["ui"] = new TextValue(ui),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);

            var ex = await Assert.That(() => SchemaBridge.ImportRender(store, designId))
                .Throws<SchemaValidationException>();
            await Assert.That(ex!.Message).Contains("server-only");

            var design = (ObjectValue)store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString()))!;
            await Assert.That(((SetValue)design.Fields["render"]).Members.Count).IsEqualTo(0);
            await Assert.That(((TextValue)design.Fields["ui"]).Text).IsEqualTo(ui);
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // Import refuses (imports NOTHING) when a top-level function returns a lambda — a stateful setup/view
    // component; importing it would collapse it into one opaque leaf blob with no re-import path.
    [Test]
    public async Task ImportRender_refuses_a_ui_with_a_lambda_returning_function()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-f1-lambda-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var ui = "ui\n    fn makeCounter()\n        return () => 5\n    fn render()\n        return <main>\n            \"hi\"\n";
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("lambda"), ["ui"] = new TextValue(ui),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);

            var ex = await Assert.That(() => SchemaBridge.ImportRender(store, designId))
                .Throws<SchemaValidationException>();
            await Assert.That(ex!.Message).Contains("lambda");

            var design = (ObjectValue)store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString()))!;
            await Assert.That(((SetValue)design.Fields["render"]).Members.Count).IsEqualTo(0);
            await Assert.That(((TextValue)design.Fields["ui"]).Text).IsEqualTo(ui);
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // Import refuses (imports NOTHING) when a top-level function's body is more than a single `return`
    // statement — outside the structured-safe subset this slice imports.
    [Test]
    public async Task ImportRender_refuses_a_ui_with_a_multi_statement_function()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-f1-multistmt-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var ui = "ui\n    fn helperLabel(active)\n        var x = active\n        return x ? \"Yes\" : \"No\"\n"
                   + "    fn render()\n        return <main>\n            \"hi\"\n";
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("multistmt"), ["ui"] = new TextValue(ui),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);

            var ex = await Assert.That(() => SchemaBridge.ImportRender(store, designId))
                .Throws<SchemaValidationException>();
            await Assert.That(ex!.Message).Contains("single");

            var design = (ObjectValue)store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString()))!;
            await Assert.That(((SetValue)design.Fields["render"]).Members.Count).IsEqualTo(0);
            await Assert.That(((TextValue)design.Fields["ui"]).Text).IsEqualTo(ui);
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // Projection refuses a structured function named "render" — MapUi routes any fn literally named
    // "render" into InstanceUi.Render, so it would silently vanish from the projected document.
    [Test]
    public async Task ProjectDesignDocument_refuses_a_structured_function_named_render()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-f1-namedrender-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("namedrender"), ["ui"] = new TextValue(""),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);
            var designPath = NodePath.Root.Field("designs").Key(designId.ToString());

            var main = store.CreateObject("MetaNode", Node("main"));
            store.AddToSet(designPath.Field("render"), main);

            var badFn = store.CreateObject("MetaFn", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["name"] = new TextValue("render"), ["params"] = new TextValue(""), ["order"] = new IntValue(0),
            }));
            store.AddToSet(designPath.Field("fns"), badFn);
            var body = store.CreateObject("MetaNode", Node("", "\"x\""));
            store.AddToSet(designPath.Field("fns").Key(badFn.ToString()).Field("body"), body);

            var design = store.ReadNode(designPath)!;
            var ex = await Assert.That(() => SchemaBridge.ProjectDesignDocument(design))
                .Throws<SchemaValidationException>();
            await Assert.That(ex!.Message).Contains("\"render\"");
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // Projection refuses two structured functions sharing a name — every resolution site (function
    // definition, validator scope, generic-UI library merge) silently keeps only the LAST one, and S1c's
    // set-union merge will produce duplicates routinely, so this refusal is load-bearing for merge.
    [Test]
    public async Task ProjectDesignDocument_refuses_duplicate_structured_function_names()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-f1-dupname-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("dupname"), ["ui"] = new TextValue(""),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);
            var designPath = NodePath.Root.Field("designs").Key(designId.ToString());

            var main = store.CreateObject("MetaNode", Node("main"));
            store.AddToSet(designPath.Field("render"), main);

            foreach (var order in new[] { 0, 1 })
            {
                var fn = store.CreateObject("MetaFn", new ObjectValue(new Dictionary<string, NodeValue>
                {
                    ["name"] = new TextValue("dup"), ["params"] = new TextValue(""), ["order"] = new IntValue(order),
                }));
                store.AddToSet(designPath.Field("fns"), fn);
                var body = store.CreateObject("MetaNode", Node("", "\"x\""));
                store.AddToSet(designPath.Field("fns").Key(fn.ToString()).Field("body"), body);
            }

            var design = store.ReadNode(designPath)!;
            var ex = await Assert.That(() => SchemaBridge.ProjectDesignDocument(design))
                .Throws<SchemaValidationException>();
            await Assert.That(ex!.Message).Contains("dup");
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // Projection refuses `fns` (structured functions) alongside an EMPTY `render` set — the F1 INTERIM gate
    // (ProjectRenderUi assembles Functions alongside Render, so fns have nowhere to project into without a
    // render root).
    [Test]
    public async Task ProjectDesignDocument_refuses_fns_when_render_is_empty()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-f1-fnsnorender-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("fnsnorender"), ["ui"] = new TextValue(""),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);
            var designPath = NodePath.Root.Field("designs").Key(designId.ToString());

            var fn = store.CreateObject("MetaFn", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["name"] = new TextValue("helper"), ["params"] = new TextValue(""), ["order"] = new IntValue(0),
            }));
            store.AddToSet(designPath.Field("fns"), fn);
            var body = store.CreateObject("MetaNode", Node("", "\"x\""));
            store.AddToSet(designPath.Field("fns").Key(fn.ToString()).Field("body"), body);

            var design = store.ReadNode(designPath)!;
            var ex = await Assert.That(() => SchemaBridge.ProjectDesignDocument(design))
                .Throws<SchemaValidationException>();
            await Assert.That(ex!.Message).Contains("`fns`");
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    // A handler attribute (`onClick={() => ...}`) round-trips losslessly: import prints the lambda source
    // into MetaAttr.value, and projecting re-parses it to the identical expression (the print∘parse
    // fixpoint). Real designs' whole point is handlers, so pin the shape explicitly.
    [Test]
    public async Task ImportRender_round_trips_a_handler_attribute()
    {
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var storePath = Path.Combine(Path.GetTempPath(), "deenv-s1b-handler-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonFileInstanceStore(storePath, meta);
        try
        {
            var ui = "ui\n    fn render()\n        return <button onClick={() => db.greeting = \"hi\"}>\n            \"Click\"\n";
            var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"] = new TextValue("handler"), ["ui"] = new TextValue(ui),
            }));
            store.AddToSet(NodePath.Root.Field("designs"), designId);
            AddDbType(store, designId);

            SchemaBridge.ImportRender(store, designId);

            var design = (ObjectValue)store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString()))!;
            await Assert.That(((TextValue)design.Fields["ui"]).Text).IsEqualTo(""); // cleared
            var onClick = OrderedByOrder((SetValue)OrderedByOrder((SetValue)design.Fields["render"]).Single().Fields["attrs"]).Single();
            await Assert.That(Text(onClick, "name")).IsEqualTo("onClick");

            // The lossless proof: projecting the imported design reproduces the canonical original render,
            // handler and all.
            var projected = SchemaBridge.ProjectDesignDocument(design);
            var expectedUi = AppPrint.PrintUi(CodeParse.ParseUiSection(ui)).TrimEnd('\n');
            await Assert.That(projected).Contains(expectedUi);
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    private static IEnumerable<ObjectValue> OrderedByOrder(SetValue set) =>
        set.Members.Values.OfType<ObjectValue>()
            .OrderBy(o => o.Fields.GetValueOrDefault("order") is IntValue i ? i.Value : 0);

    private static string Text(ObjectValue o, string name) =>
        o.Fields.GetValueOrDefault(name) is TextValue t ? t.Text : "";

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
