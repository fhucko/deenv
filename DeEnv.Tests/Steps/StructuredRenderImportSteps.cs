using DeEnv.Code;
using DeEnv.Designer;
using DeEnv.Instance;
using DeEnv.Storage;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// StructuredRenderImport.feature — M12 S1b (SchemaBridge.ImportRender): convert a design authored as
// `ui` TEXT (a custom `fn render()`) INTO the structured MetaNode tree, then clear the `ui` field. Drives
// ImportRender directly over a REAL designer store (the same test-local meta-schema shape
// DesignSnapshotSteps uses, extended with `render set of MetaNode` + MetaNode/MetaAttr), then proves the
// lossless round-trip through the real ProjectDesignDocument. No wire, no WS, no interpreter change.
[Binding]
public sealed class StructuredRenderImportSteps
{
    // The designer meta-schema, mirroring the live designer (instances/1): a Design carries its render as
    // both a `ui` text field AND a structured `render set of MetaNode` (the S1a shape). A custom-UI design
    // needs a valid Db root type, so Db is seeded too.
    private const string MetaSchema =
        """
        types
            Db
                designs set of Design
            Design
                label text
                initialData text
                access text
                common text
                ui text
                render set of MetaNode
                fns set of MetaFn
                vars set of MetaVar
                types set of MetaType
            MetaNode
                kind text
                tag text
                expr text
                item text
                collection text
                condition text
                attrs set of MetaAttr
                children set of MetaNode
                elseChildren set of MetaNode
                order int
            MetaAttr
                name text
                value text
                order int
            MetaFn
                name text
                params text
                body set of MetaNode
                vars set of MetaVar
                order int
            MetaVar
                name text
                init text
                order int
            MetaType
                name text
                baseType text
                values text
                order int
                props set of MetaProp
            MetaProp
                name text
                type text
                cardinality text
                keyType text
                multiline bool
                order int
        """;

    private readonly IInstanceStore _designer =
        new JsonFileInstanceStore(Path.GetTempFileName(), InstanceDescriptionLoader.Load(MetaSchema));

    private int _designId;
    private string _originalUi = "";
    private Exception? _error;

    private NodePath DesignPath => NodePath.Root.Field("designs").Key(_designId.ToString());

    // ── Given: seed a design with a `ui` render text (+ a valid Db type) ─────────────────────────────

    [Given("a design whose `ui` text is a fn render\\() returning <main class=\"hello\"> containing an <h1> whose child is the text \"Hi\"")]
    public void GivenNestedRenderDesign() => SeedDesign(
        "ui\n    fn render()\n        return <main class=\"hello\">\n            <h1>\n                \"Hi\"\n");

    // S6a: `foreach`/`if` render forms now IMPORT to structured `kind="for"`/`kind="if"` rows (the refusal
    // lifted). This design's render has a foreach loop over `db.notes` AND an if/else keyed off `db.greeting`
    // — the load-bearing round-trip proof (import → structured rows → project back ≡ canonical original).
    [Given("a design whose `ui` text is a fn render\\(\\) whose body has a foreach loop and an if with an else branch")]
    public void GivenForeachAndIfRenderDesign() => SeedDesign(
        "ui\n    fn render()\n        return <main>\n" +
        "            foreach note in db.notes\n" +
        "                <li>\n" +
        "                    note.title\n" +
        "            if db.greeting == \"hi\"\n" +
        "                <p>\n" +
        "                    \"hello\"\n" +
        "            else\n" +
        "                <p>\n" +
        "                    \"bye\"\n");

    // M12 F1: a `ui` with `fn render()` + a scalar HELPER fn (single-return ternary) + a COMPONENT fn
    // (single-return element with a param) — the load-bearing shape structured fns import.
    [Given("a design whose `ui` text is a fn render\\(\\) plus a scalar helper function and a component function with a param")]
    public void GivenHelperAndComponentDesign() => SeedDesign(
        "ui\n"
        + "    fn helperLabel(active)\n        return active ? \"Yes\" : \"No\"\n"
        + "    fn NoteCard(note)\n        return <li>\n            note.title\n"
        + "    fn render()\n        return <main>\n            \"hi\"\n");

    // M12 V1: a `ui` with a TOP-LEVEL var, a stateless component (NoteCard), AND a real stateful setup/view
    // component (Counter — the canonical shape confirmed against reality: a state var, a nested unparameterized
    // `fn render()`, `return render`) besides `fn render()`. The load-bearing shape V1's import lifts the last
    // two refusals for.
    [Given("a design whose `ui` text has a top-level var, a stateless component, and a stateful Counter component")]
    public void GivenVarsAndStatefulComponentDesign() => SeedDesign(
        "ui\n"
        + "    var greeting = \"hi\"\n"
        + "    fn NoteCard(note)\n        return <li>\n            note.title\n"
        + "    fn Counter()\n        var count = 0\n        fn render()\n            return <button onClick={() => count = count + 1}>\n                count\n        return render\n"
        + "    fn render()\n        return <main>\n            \"hi\"\n");

    // Refusal fixtures: import must leave the design entirely UNTOUCHED (nothing minted, `ui` unchanged).

    [Given("a design whose `ui` text is a fn render\\(\\) plus a server-only function")]
    public void GivenServerOnlyDesign() => SeedDesign(
        "ui\n    server fn secretHelper()\n        return \"shh\"\n    fn render()\n        return <main>\n            \"hi\"\n");

    [Given("a design whose `ui` text is a fn render\\(\\) plus a function returning a lambda")]
    public void GivenLambdaReturnDesign() => SeedDesign(
        "ui\n    fn makeCounter()\n        return () => 5\n    fn render()\n        return <main>\n            \"hi\"\n");

    [Given("a design whose `ui` text is a fn render\\(\\) plus a function with multiple statements")]
    public void GivenMultiStatementDesign() => SeedDesign(
        "ui\n    fn helperLabel(active)\n        var x = active\n        return x ? \"Yes\" : \"No\"\n"
        + "    fn render()\n        return <main>\n            \"hi\"\n");

    // M12 V1: a stateful component carrying an EXTRA named helper function (`doConfirm`, GenericUi's real
    // ConfirmButton shape) besides its state var and nested render — outside the accepted shape (MetaVar has
    // a row for a state VAR, not a nested helper FUNCTION), so the whole import must still refuse.
    [Given("a design whose `ui` text is a fn render\\(\\) plus a stateful component with an extra helper function")]
    public void GivenStatefulWithExtraHelperDesign() => SeedDesign(
        "ui\n"
        + "    fn ConfirmButton()\n        var confirming = false\n        fn doConfirm()\n            confirming = false\n        fn render()\n            return <span>\n                \"x\"\n        return render\n"
        + "    fn render()\n        return <main>\n            \"hi\"\n");

    // M12 V1: a stateful component whose direct return is a LAMBDA (`return () => count`, no named nested
    // `render()`) — grammar-legal but never the shape any real code uses; V1 only imports the observed
    // nested-`fn render()` shape (a `[var, return-lambda]` pair is neither the plain single-`return` shape
    // nor the stateful shape), so this stays refused.
    [Given("a design whose `ui` text is a fn render\\(\\) plus a function with a state var and a direct lambda return")]
    public void GivenStateVarWithDirectLambdaReturnDesign() => SeedDesign(
        "ui\n    fn Counter()\n        var count = 0\n        return () => count\n"
        + "    fn render()\n        return <main>\n            \"hi\"\n");

    private void SeedDesign(string uiText)
    {
        _originalUi = uiText;
        _designId = _designer.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["label"]       = new TextValue("hello-app"),
            ["initialData"] = new TextValue(""),
            ["access"]      = new TextValue(""),
            ["common"]      = new TextValue(""),
            ["ui"]          = new TextValue(uiText),
        }));
        _designer.AddToSet(NodePath.Root.Field("designs"), _designId);

        // A custom-UI app still needs a valid Db root type, or the projection's type validation rejects it.
        var dbId = _designer.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("Db"), ["baseType"] = new TextValue("object"), ["order"] = new IntValue(0),
        }));
        _designer.AddToSet(DesignPath.Field("types"), dbId);
        var greetingId = _designer.CreateObject("MetaProp", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("greeting"), ["type"] = new TextValue("text"), ["order"] = new IntValue(0),
        }));
        _designer.AddToSet(DesignPath.Field("types").Key(dbId.ToString()).Field("props"), greetingId);
    }

    // ── When: import (and the re-import setup) ───────────────────────────────────────────────────────

    [When("the design's render is imported to structured rows")]
    public void WhenImported()
    {
        try { SchemaBridge.ImportRender(_designer, _designId); }
        catch (Exception ex) { _error = ex; }
    }

    [Given("the design's render has already been imported to structured rows")]
    public void GivenAlreadyImported() => SchemaBridge.ImportRender(_designer, _designId);

    // ── Then ─────────────────────────────────────────────────────────────────────────────────────────

    [Then("the design's `ui` text field is empty")]
    public async Task ThenUiEmpty()
    {
        var design = (ObjectValue)_designer.ReadNode(DesignPath)!;
        await Assert.That(((TextValue)design.Fields["ui"]).Text).IsEqualTo("");
    }

    [Then("the design's `render` set holds the structured tree")]
    public async Task ThenRenderPopulated()
    {
        var design = (ObjectValue)_designer.ReadNode(DesignPath)!;
        await Assert.That(((SetValue)design.Fields["render"]).Members.Count).IsEqualTo(1);
    }

    // The imported `main` root's `children` set holds, in order, the for-row and the if-row this design's
    // render authors — walk it to find the row of the given `kind` (S6a: the import lift's own assertion).
    private ObjectValue RowOfKind(string kind)
    {
        var design = (ObjectValue)_designer.ReadNode(DesignPath)!;
        var root = (ObjectValue)((SetValue)design.Fields["render"]).Members.Single().Value;
        var children = (SetValue)root.Fields["children"];
        return children.Members.Select(m => (ObjectValue)m.Value)
            .Single(n => ((TextValue)n.Fields["kind"]).Text == kind);
    }

    [Then("the imported for row has item {string} and collection {string}")]
    public async Task ThenForRowShape(string item, string collection)
    {
        var row = RowOfKind("for");
        await Assert.That(((TextValue)row.Fields["item"]).Text).IsEqualTo(item);
        await Assert.That(((TextValue)row.Fields["collection"]).Text).IsEqualTo(collection);
    }

    [Then("the imported if row has an else branch")]
    public async Task ThenIfRowHasElse()
    {
        var row = RowOfKind("if");
        await Assert.That(((SetValue)row.Fields["elseChildren"]).Members.Count).IsGreaterThan(0);
    }

    // The heart of the slice: import then project is the IDENTITY on the render. The projected document's
    // `ui` section must equal the canonical form of the ORIGINAL `ui` text (parse∘print) — nothing about the
    // tree was lost or reshaped by the import → project round-trip.
    [Then("projecting the design yields a `ui` section equal to the canonical form of the original render")]
    public async Task ThenProjectsToCanonicalOriginal()
    {
        var design = _designer.ReadNode(DesignPath)!;
        var projected = SchemaBridge.ProjectDesignDocument(design);
        var expectedUi = AppPrint.PrintUi(CodeParse.ParseUiSection(_originalUi)).TrimEnd('\n');
        await Assert.That(projected).Contains(expectedUi);
    }

    [Then("the import fails with a schema validation error")]
    public async Task ThenImportFailed()
    {
        await Assert.That(_error).IsNotNull();
        await Assert.That(_error).IsTypeOf<SchemaValidationException>();
    }

    // ── M12 F1: structured fns ──────────────────────────────────────────────────────────────────────

    [Then("the design's `fns` set holds {int} structured functions")]
    public async Task ThenFnsCount(int count)
    {
        var design = (ObjectValue)_designer.ReadNode(DesignPath)!;
        await Assert.That(((SetValue)design.Fields["fns"]).Members.Count).IsEqualTo(count);
    }

    [Then("the imported function {string} has params {string} and a body root")]
    public async Task ThenImportedFunctionShape(string name, string paramsText)
    {
        var design = (ObjectValue)_designer.ReadNode(DesignPath)!;
        var fns = ((SetValue)design.Fields["fns"]).Members.Select(m => (ObjectValue)m.Value).ToList();
        var fn = fns.Single(f => ((TextValue)f.Fields["name"]).Text == name);
        await Assert.That(((TextValue)fn.Fields["params"]).Text).IsEqualTo(paramsText);
        await Assert.That(((SetValue)fn.Fields["body"]).Members.Count).IsEqualTo(1);
    }

    [Then("the design's `render` set is empty")]
    public async Task ThenRenderEmpty()
    {
        var design = (ObjectValue)_designer.ReadNode(DesignPath)!;
        await Assert.That(((SetValue)design.Fields["render"]).Members.Count).IsEqualTo(0);
    }

    [Then("the design's `ui` text field is unchanged")]
    public async Task ThenUiUnchanged()
    {
        var design = (ObjectValue)_designer.ReadNode(DesignPath)!;
        await Assert.That(((TextValue)design.Fields["ui"]).Text).IsEqualTo(_originalUi);
    }

    // ── M12 V1: MetaVar rows ────────────────────────────────────────────────────────────────────────

    [Then("the design's `vars` set holds {int} design-level state variable\\(s\\)")]
    public async Task ThenDesignVarsCount(int count)
    {
        var design = (ObjectValue)_designer.ReadNode(DesignPath)!;
        await Assert.That(((SetValue)design.Fields["vars"]).Members.Count).IsEqualTo(count);
    }

    [Then("the imported function {string} has {int} state variable\\(s\\)")]
    public async Task ThenImportedFunctionVarCount(string name, int count)
    {
        var design = (ObjectValue)_designer.ReadNode(DesignPath)!;
        var fns = ((SetValue)design.Fields["fns"]).Members.Select(m => (ObjectValue)m.Value).ToList();
        var fn = fns.Single(f => ((TextValue)f.Fields["name"]).Text == name);
        await Assert.That(((SetValue)fn.Fields["vars"]).Members.Count).IsEqualTo(count);
    }
}
