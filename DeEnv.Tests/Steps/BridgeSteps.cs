using DeEnv.Designer;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

[Binding]
public sealed class BridgeSteps(InstanceContext ctx)
{
    private const string Sentinel = "UNCHANGED-SENTINEL";

    private readonly string _metaPath = Path.Combine(AppContext.BaseDirectory, "meta.schema.json");

    // Designer-data authoring state (per scenario).
    private InstanceDescription? _meta;
    private IInstanceStore? _designer;
    private string _designerDataPath = "";
    private string _exportedSchemaPath = "";
    private string _exportedDataPath = "";
    private readonly Dictionary<string, int> _typeKeys = new();

    // Meta-schema load + export results.
    private InstanceDescription? _metaLoaded;
    private Exception? _metaError;
    private Exception? _exportError;
    private InstanceDescription? _exported;

    // ── Given: meta-schema document ─────────────────────────────────────────────

    [Given("the meta-schema document")]
    public void GivenTheMetaSchemaDocument() { /* meta.schema.json ships to the test output */ }

    [When("the meta-schema is loaded")]
    public void WhenMetaSchemaLoaded()
    {
        try { _metaLoaded = InstanceDescriptionLoader.LoadFile(_metaPath); }
        catch (Exception ex) { _metaError = ex; }
    }

    [Then("the meta-schema loads successfully")]
    public async Task ThenMetaLoadsAsync()
    {
        await Assert.That(_metaError).IsNull();
        await Assert.That(_metaLoaded).IsNotNull();
    }

    [Then("the meta-schema defines a type {string}")]
    public async Task ThenMetaDefinesTypeAsync(string name)
    {
        await Assert.That(_metaLoaded!.FindType(name)).IsNotNull();
    }

    // ── Given: a designer instance + designed types/props ───────────────────────

    [Given("a designer instance")]
    public void GivenDesignerInstance()
    {
        _meta = InstanceDescriptionLoader.LoadFile(_metaPath);
        _designerDataPath = Path.GetTempFileName();
        _designer = new JsonFileInstanceStore(_designerDataPath, _meta);

        // A target schema file pre-seeded with a sentinel, so an invalid export
        // (which must write nothing) is detectably unchanged.
        _exportedSchemaPath = Path.GetTempFileName();
        File.WriteAllText(_exportedSchemaPath, Sentinel);
    }

    [Given("a designed type {string} with base type {string}")]
    public void GivenDesignedType(string name, string baseType)
    {
        var id = _designer!.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]     = new TextValue(name),
            ["baseType"] = new TextValue(baseType),
            ["order"]    = new IntValue(0)
        }));
        _designer!.AddToSet(NodePath.Root.Field("types"), id);
        _typeKeys[name] = id;
    }

    [Given("the type {string} has a prop {string} of type {string}")]
    public void GivenProp(string typeName, string propName, string propType) =>
        AddProp(typeName, propName, propType, "", "", order: 0);

    [Given("the type {string} has a prop {string} of type {string} with order {int}")]
    public void GivenPropWithOrder(string typeName, string propName, string propType, int order) =>
        AddProp(typeName, propName, propType, "", "", order);

    [Given("the type {string} has a set prop {string} of type {string}")]
    public void GivenSetProp(string typeName, string propName, string propType) =>
        AddProp(typeName, propName, propType, "set", "", order: 0);

    private void AddProp(string typeName, string propName, string propType,
        string cardinality, string keyType, int order)
    {
        var propsPath = NodePath.Root.Field("types").Key(_typeKeys[typeName].ToString()).Field("props");
        var fields = new Dictionary<string, NodeValue>
        {
            ["name"]  = new TextValue(propName),
            ["type"]  = new TextValue(propType),
            ["order"] = new IntValue(order)
        };
        if (cardinality.Length > 0)
            fields["cardinality"] = new TextValue(cardinality);
        if (keyType.Length > 0)
            fields["keyType"] = new TextValue(keyType);
        var id = _designer!.CreateObject("MetaProp", new ObjectValue(fields));
        _designer!.AddToSet(propsPath, id);
    }

    // ── When: export ────────────────────────────────────────────────────────────

    [When("the design is exported")]
    public void WhenExported()
    {
        _exportedDataPath = Path.GetTempFileName();
        try
        {
            SchemaBridge.Export(_metaPath, _designerDataPath, _exportedSchemaPath, _exportedDataPath);
            _exported = InstanceDescriptionLoader.LoadFile(_exportedSchemaPath);
        }
        catch (Exception ex)
        {
            _exportError = ex;
        }
    }

    [When("an instance is started from the exported schema")]
    public async Task WhenInstanceStartedAsync()
    {
        ctx.Description = InstanceDescriptionLoader.LoadFile(_exportedSchemaPath);
        ctx.DataFilePath = _exportedDataPath;
        ctx.Server = new TestInstanceServer();
        await ctx.Server.StartAsync(ctx.Description, ctx.DataFilePath);
        ctx.Store = ctx.Server.Store;

        ctx.Playwright = await Playwright.CreateAsync();
        ctx.Browser = await ctx.Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        ctx.Page = await ctx.Browser.NewPageAsync(new BrowserNewPageOptions { BaseURL = ctx.BaseUrl });
        await ctx.Page.GotoAsync(ctx.BaseUrl + "/");
    }

    // ── Then: exported document ─────────────────────────────────────────────────

    [Then("the exported document loads successfully")]
    public async Task ThenExportedLoadsAsync()
    {
        await Assert.That(_exportError).IsNull();
        await Assert.That(_exported).IsNotNull();
    }

    [Then("the exported type {string} has a prop {string}")]
    public async Task ThenExportedTypeHasPropAsync(string typeName, string propName)
    {
        var prop = _exported!.FindType(typeName)?.Props?.FirstOrDefault(p => p.Name == propName);
        await Assert.That(prop).IsNotNull();
    }

    [Then("the exported type {string} has a single reference prop {string} of type {string}")]
    public async Task ThenExportedSingleReferencePropAsync(string typeName, string propName, string refType)
    {
        var prop = _exported!.FindType(typeName)?.Props?.FirstOrDefault(p => p.Name == propName);
        await Assert.That(prop).IsNotNull();
        await Assert.That(prop!.Cardinality).IsEqualTo(Cardinality.Single);
        await Assert.That(prop.TypeName).IsEqualTo(refType);
        await Assert.That(_exported!.IsObjectType(prop.TypeName)).IsTrue();
    }

    [Then("a reference link {string} is present")]
    public async Task ThenReferenceLinkPresentAsync(string fieldName)
    {
        var count = await ctx.Page!.Locator($"a[href='/{fieldName}']").CountAsync();
        await Assert.That(count).IsGreaterThanOrEqualTo(1);
    }

    [Then("the exported type {string} has a set prop {string} of type {string}")]
    public async Task ThenExportedSetPropAsync(string typeName, string propName, string elemType)
    {
        var prop = _exported!.FindType(typeName)?.Props?.FirstOrDefault(p => p.Name == propName);
        await Assert.That(prop).IsNotNull();
        await Assert.That(prop!.Cardinality).IsEqualTo(Cardinality.Set);
        await Assert.That(prop.TypeName).IsEqualTo(elemType);
    }

    [Then("the exported type {string} has a dictionary prop {string} of type {string}")]
    public async Task ThenExportedDictionaryPropAsync(string typeName, string propName, string elemType)
    {
        var prop = _exported!.FindType(typeName)?.Props?.FirstOrDefault(p => p.Name == propName);
        await Assert.That(prop).IsNotNull();
        await Assert.That(prop!.Cardinality).IsEqualTo(Cardinality.Dictionary);
        await Assert.That(prop.TypeName).IsEqualTo(elemType);
    }

    [Then("the exported type {string} lists prop {string} before {string}")]
    public async Task ThenExportedOrderAsync(string typeName, string before, string after)
    {
        var props = _exported!.FindType(typeName)!.Props!.ToList();
        var ib = props.FindIndex(p => p.Name == before);
        var ia = props.FindIndex(p => p.Name == after);
        await Assert.That(ib).IsGreaterThanOrEqualTo(0);
        await Assert.That(ia).IsGreaterThanOrEqualTo(0);
        await Assert.That(ib).IsLessThan(ia);
    }

    [Then("the export is rejected with an error mentioning {string}")]
    public async Task ThenExportRejectedAsync(string phrase)
    {
        await Assert.That(_exportError).IsNotNull();
        await Assert.That(_exportError).IsTypeOf<SchemaValidationException>();
        await Assert.That(_exportError!.Message).Contains(phrase);
    }

    [Then("the target schema file is unchanged")]
    public async Task ThenTargetUnchangedAsync()
    {
        await Assert.That(File.ReadAllText(_exportedSchemaPath)).IsEqualTo(Sentinel);
    }

    [Then("the {string} field is present")]
    public async Task ThenFieldPresentAsync(string fieldName)
    {
        var count = await ctx.Page!.Locator($"input[data-path$='/{fieldName}']").CountAsync();
        await Assert.That(count).IsGreaterThanOrEqualTo(1);
    }
}
