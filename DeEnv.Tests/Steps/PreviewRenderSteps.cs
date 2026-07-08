using DeEnv.Code;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// PreviewRender.feature — M12 S3a. Drives the server-side preview compute (SsrRenderer.BuildPreviewRenderData)
// and the call-site revival (CodeExecutor.RevivePreviewTree) directly over a REAL designer store (the real
// instances/1 meta-schema, so Design/MetaNode/MetaType are declared), proving the DATA the memo would ship
// revives to the design's real rendered structure against its initialData seed — no browser. The end-to-end
// splice + hydration + liveness is proven by the @m12 browser scenario.
[Binding]
public sealed class PreviewRenderSteps
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deenv-prevrender-" + Guid.NewGuid().ToString("N"));
    private IInstanceStore _designer = null!;
    private int _designId;
    private IExecValue _tree = null!;
    private int _previewDirsBefore;

    private NodePath DesignPath => NodePath.Root.Field("designs").Key(_designId.ToString());

    private void StartDesigner(string initialData)
    {
        Directory.CreateDirectory(_dir);
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1)); // the real designer meta-schema
        _designer = new JsonFileInstanceStore(Path.Combine(_dir, "designer-data.json"), meta);
        _designId = _designer.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["label"]       = new TextValue("preview-app"),
            ["initialData"] = new TextValue(initialData),
            ["access"]      = new TextValue(""),
            ["common"]      = new TextValue(""),
            ["ui"]          = new TextValue(""), // empty — the structured render is the authority
        }));
        _designer.AddToSet(NodePath.Root.Field("designs"), _designId);
    }

    private int AddDbType(bool withGreetingProp)
    {
        var db = _designer.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("Db"), ["baseType"] = new TextValue("object"), ["order"] = new IntValue(0),
        }));
        _designer.AddToSet(DesignPath.Field("types"), db);
        if (withGreetingProp)
        {
            var greeting = _designer.CreateObject("MetaProp", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["name"] = new TextValue("greeting"), ["type"] = new TextValue("text"), ["order"] = new IntValue(0),
            }));
            _designer.AddToSet(DesignPath.Field("types").Key(db.ToString()).Field("props"), greeting);
        }
        return db;
    }

    [Given("a preview design whose render shows the seeded greeting inside a main.hello > h1 and a button with an onClick handler")]
    public void GivenRenderWithGreetingAndHandler()
    {
        // Start with an empty seed; the following `And its initialData seeds …` Given fills the field in.
        StartDesigner(initialData: "");
        AddDbType(withGreetingProp: true);

        // main.hello
        var main = CreateNode(tag: "main");
        _designer.AddToSet(DesignPath.Field("render"), main);
        var mainPath = DesignPath.Field("render").Key(main.ToString());
        AddAttr(mainPath, name: "class", value: Quote("hello"));

        // main > h1 > db.greeting  (an EXPRESSION leaf reading the seeded root prop)
        var h1 = CreateNode(tag: "h1");
        AddToChildren(mainPath, h1);
        var h1Path = mainPath.Field("children").Key(h1.ToString());
        AddToChildren(h1Path, CreateNode(tag: "", expr: "db.greeting")); // leaf expr → the seed value

        // main > button (onClick handler) > "Click"
        var button = CreateNode(tag: "button");
        AddToChildren(mainPath, button);
        var buttonPath = mainPath.Field("children").Key(button.ToString());
        AddAttr(buttonPath, name: "onClick", value: "() => db.greeting"); // a handler — must be stripped
        AddToChildren(buttonPath, CreateNode(tag: "", expr: Quote("Click")));
    }

    [Given("its initialData seeds the greeting {string}")]
    public void GivenInitialDataSeed(string greeting)
    {
        var seed = $"initialData\n    Db 1\n        greeting: \"{greeting}\"\n";
        _designer.WriteField(_designId, "initialData", new TextValue(seed));
    }

    [Given("a preview design that does not project to a valid app document")]
    public void GivenInvalidDesign()
    {
        StartDesigner(initialData: "");
        AddDbType(withGreetingProp: false); // an object Db with NO props — ProjectDesignDocument rejects it
        var main = CreateNode(tag: "main");
        _designer.AddToSet(DesignPath.Field("render"), main);
    }

    [When("the design's preview is computed")]
    public void WhenComputed()
    {
        _previewDirsBefore = PreviewTempDirCount();
        var meta = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(1));
        var renderer = new SsrRenderer(_designer, meta);
        var ctx = new ExecContext();
        // The delegate takes the design ExecObject only for its id (it re-reads the node from the store).
        var data = renderer.BuildPreviewRenderData(
            new ExecObject { Id = _designId, Props = new Dictionary<string, IExecValue>() }, ctx);
        _tree = CodeExecutor.RevivePreviewTree(data, ctx);
    }

    [Then("the preview revives to a <main class={string}> whose <h1> text is {string}")]
    public async Task ThenMainH1(string cls, string text)
    {
        var main = FirstTag(_tree);
        await Assert.That(main.Name).IsEqualTo("main");
        await Assert.That(AttrText(main, "class")).IsEqualTo(cls);
        var h1 = FirstChildTag(main, "h1");
        await Assert.That(TextOf(h1)).IsEqualTo(text); // the SEEDED value — proves the throwaway store self-seeded
    }

    [Then("the revived <button> has no onClick handler attribute")]
    public async Task ThenButtonStripped()
    {
        var button = FirstChildTag(FirstTag(_tree), "button");
        await Assert.That(button.Attributes.ContainsKey("onClick")).IsFalse();
    }

    [Then("no preview temp files are left behind")]
    public async Task ThenNoTempFiles() =>
        await Assert.That(PreviewTempDirCount()).IsEqualTo(_previewDirsBefore);

    [Then("the preview revives to a <div class={string}> without throwing")]
    public async Task ThenErrorDiv(string cls)
    {
        var div = FirstTag(_tree);
        await Assert.That(div.Name).IsEqualTo("div");
        await Assert.That(AttrText(div, "class")).IsEqualTo(cls);
    }

    // ── tree walkers ──────────────────────────────────────────────────────────────

    // The revived root is a fragment (an ExecArray that splices flat); its first tag child is the render root.
    private static ExecTag FirstTag(IExecValue tree) => tree switch
    {
        ExecArray arr => arr.Items.Select(i => i.Value).OfType<ExecTag>().First(),
        ExecTag t     => t,
        _ => throw new InvalidOperationException("The revived preview has no root tag."),
    };

    private static ExecTag FirstChildTag(ExecTag tag, string name) =>
        tag.Children.OfType<ExecTag>().First(t => t.Name == name);

    private static string? AttrText(ExecTag tag, string name) =>
        tag.Attributes.TryGetValue(name, out var v) && v is ExecText t ? t.Value : null;

    private static string TextOf(ExecTag tag) =>
        string.Concat(tag.Children.OfType<ExecText>().Select(t => t.Value));

    private int PreviewTempDirCount() =>
        Directory.GetDirectories(Path.GetTempPath(), "deenv-preview-*").Length;

    // ── design node builders (mirrors StructuredRenderSteps) ────────────────────────

    private static string Quote(string s) => "\"" + s + "\"";

    private int CreateNode(string tag, string expr = "") =>
        _designer.CreateObject("MetaNode", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["tag"]   = new TextValue(tag),
            ["expr"]  = new TextValue(expr),
            ["order"] = new IntValue(0),
        }));

    private void AddToChildren(NodePath nodePath, int child) =>
        _designer.AddToSet(nodePath.Field("children"), child);

    private void AddAttr(NodePath nodePath, string name, string value)
    {
        var id = _designer.CreateObject("MetaAttr", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]  = new TextValue(name),
            ["value"] = new TextValue(value),
            ["order"] = new IntValue(0),
        }));
        _designer.AddToSet(nodePath.Field("attrs"), id);
    }

    [AfterScenario]
    public void Cleanup()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
