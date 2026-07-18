using DeEnv.Designer;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// StructuredRender.feature — M12 S1a. The designer stores render code as a MetaNode tag tree owned by
// Design.render (a `list of MetaNode` holding exactly one root); SchemaBridge.ProjectDesignDb
// projects it to the canonical `fn render()` text. This drives the projection over a REAL designer store
// (the real instances/1 meta-schema, so the new `render`/MetaNode/MetaAttr types are declared) and proves
// the artifact by the SAME server-side path a hand-written UI takes (InstanceDescriptionLoader.Load +
// SsrRenderer) — no browser.
[Binding]
public sealed class StructuredRenderSteps
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deenv-srender-" + Guid.NewGuid().ToString("N"));
    private IInstanceStore _designer = null!;
    private int _designId;
    private string _projected = "";

    private NodePath DesignPath => NodePath.Root.Field("designs").Key(_designId.ToString());

    [Given("a design whose `ui` text is empty")]
    public void GivenDesignWithEmptyUi()
    {
        Directory.CreateDirectory(_dir);
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1)); // the real designer meta-schema
        _designer = new JsonFileInstanceStore(Path.Combine(_dir, "designer-data.json"), meta);

        _designId = _designer.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["label"]       = new TextValue("hello-app"),
            ["initialData"] = new TextValue(""),
            ["access"]      = new TextValue(""),
            ["common"]      = new TextValue(""),
            ["ui"]          = new TextValue(""), // empty — the structured render is the authority
        }));
        _designer.AddToSet(NodePath.Root.Field("designs"), _designId);

        // A minimal valid Db root type (a custom-UI app still needs one) — the projected document's `types`.
        var db = _designer.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("Db"), ["baseType"] = new TextValue("object"),
        }));
        DesignerListHelpers.AppendToList(_designer, DesignPath.Field("types"), db, "MetaType");
        var greeting = _designer.CreateObject("MetaProp", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("greeting"), ["type"] = new TextValue("text"),
        }));
        DesignerListHelpers.AppendToList(_designer, DesignPath.Field("types").Key(db.ToString()).Field("props"), greeting, "MetaProp");
    }

    // Build: main.hello > h1 > "Hi". Built TOP-DOWN, each node linked into its (already-reachable) parent as
    // soon as it is created — a node left transiently unreferenced would be swept by the store's GC, which
    // runs on link mutations. The root is linked into Design.render as a SET MEMBER (the render set holds
    // exactly one root); the nested children/attrs sets are then reachable through the Db graph the same way.
    [Given("the design's structured render is a `main` element with class {string} containing an `h1` whose child is the text {string}")]
    public void GivenStructuredRender(string cls, string childText)
    {
        var main = CreateNode(tag: "main");
        DesignerListHelpers.AppendToList(_designer, DesignPath.Field("render"), main, "MetaNode"); // one root

        var mainPath = DesignPath.Field("render").Key(main.ToString());
        // Attribute value + text-leaf expr are EXPRESSION SOURCES: a string value is the QUOTED literal source.
        AddAttr(mainPath, name: "class", value: Quote(cls));               // main.class = "hello"

        var h1 = CreateNode(tag: "h1");
        AddToChildren(mainPath, h1);                                       // main > h1 (h1 now reachable)

        var h1Path = mainPath.Field("children").Key(h1.ToString());
        var text = CreateNode(tag: "", expr: Quote(childText));           // leaf text child (a string-literal source)
        AddToChildren(h1Path, text);                                       // h1 > "Hi"
    }

    private static string Quote(string s) => "\"" + s + "\"";

    [When("the design is projected to an app document")]
    public void WhenProjected()
    {
        var design = _designer.ReadNode(DesignPath)!;
        // ReadNode resolves `render` (now a set) recursively, along with every descendant's children/attrs
        // sets — exactly like `types` — so no resolver is needed.
        _projected = SchemaBridge.ProjectDesignDb(design, _designer);
    }

    // The `()` in `fn render()` is escaped (`\(\)`) — Cucumber Expressions treat `(…)` as an optional group.
    [Then("the document has a `ui` section containing `fn render\\(\\)`")]
    public async Task ThenHasRenderFn()
    {
        await Assert.That(_projected).Contains("\nui\n");
        await Assert.That(_projected).Contains("fn render()");
    }

    [Then("loading that document and rendering it produces HTML with an <h1> reading {string} inside a <main class={string}>")]
    public async Task ThenRendersHtml(string h1Text, string mainClass)
    {
        var desc = InstanceDescriptionLoader.Load(_projected);
        var store = new JsonFileInstanceStore(Path.Combine(_dir, "app-data.json"), desc);
        var html = new SsrRenderer(store, desc).Render("/").Html;

        await Assert.That(html).Contains($"<main class=\"{mainClass}\">");
        await Assert.That(html).Contains("<h1>");
        await Assert.That(html).Contains(h1Text);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private int CreateNode(string tag, string expr = "") =>
        _designer.CreateObject("MetaNode", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["tag"]  = new TextValue(tag),
            ["expr"] = new TextValue(expr),
        }));

    private void AddToChildren(NodePath nodePath, int child) =>
        DesignerListHelpers.AppendToList(_designer, nodePath.Field("children"), child, "MetaNode");

    private void AddAttr(NodePath nodePath, string name, string value)
    {
        var id = _designer.CreateObject("MetaAttr", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]  = new TextValue(name),
            ["value"] = new TextValue(value),
        }));
        DesignerListHelpers.AppendToList(_designer, nodePath.Field("attrs"), id, "MetaAttr");
    }

    [AfterScenario]
    public void Cleanup()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
