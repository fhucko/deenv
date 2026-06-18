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

    // Bridge.feature is a unit-level test of the M4 ROOT-projection mechanism (SchemaBridge.Project/
    // Export: a `Db` whose root `types` set IS the designed schema). The live designer (instances/1/
    // app.app) has since moved to the `Db { designs }` IDE shape (a SET of whole-app Designs, projected
    // by ProjectDesignDocument — the DESIGN path, exercised by HostAction.feature). So this test owns a
    // TEST-LOCAL `Db { types }` meta-schema, decoupled from the live designer — the same isolation
    // HostActionSteps uses for its own `Db { designs }` meta. It still defines MetaType/MetaProp, so the
    // "meta-schema document loads" scenario holds. (Written to a temp .app per scenario, alongside the
    // existing temp data/export files — these step temp files are left to the OS temp dir, as before.)
    private const string MetaSchema =
        """
        types
            Db
                types set of MetaType
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
                order int
        """;

    // The per-scenario temp path the test-local meta-schema is written to (set by GivenDesignerInstance
    // / WhenMetaSchemaLoaded), used as the meta path for the export.
    private string _designerAppPath = "";

    // Designer-data authoring state (per scenario).
    private InstanceDescription? _meta;
    private IInstanceStore? _designer;
    private string _designerDataPath = "";
    private string _exportedSchemaPath = "";
    private string _exportedDataPath = "";
    private readonly Dictionary<string, int> _typeKeys = new();

    // Write the test-local meta-schema to a temp .app and return its path, so the export reads it as
    // the meta. Idempotent within a scenario (a single temp file reused).
    private string EnsureMetaSchemaFile()
    {
        if (_designerAppPath.Length == 0)
        {
            _designerAppPath = Path.GetTempFileName();
            File.WriteAllText(_designerAppPath, MetaSchema);
        }
        return _designerAppPath;
    }

    // Meta-schema load + export results.
    private InstanceDescription? _metaLoaded;
    private Exception? _metaError;
    private Exception? _exportError;
    private InstanceDescription? _exported;

    // ── Given: meta-schema document ─────────────────────────────────────────────

    [Given("the meta-schema document")]
    public void GivenTheMetaSchemaDocument() { /* the test-local meta-schema is written on demand */ }

    [When("the meta-schema is loaded")]
    public void WhenMetaSchemaLoaded()
    {
        try { _metaLoaded = InstanceDescriptionLoader.LoadFile(EnsureMetaSchemaFile()); }
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
        _meta = InstanceDescriptionLoader.LoadFile(EnsureMetaSchemaFile());
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

    [Given("a designed enum type {string} with values {string}")]
    public void GivenDesignedEnumType(string name, string values)
    {
        // An enum is a MetaType with baseType "enum" and a comma-separated `values` field — exactly the
        // designer's authoring shape. The bridge splits/trims it into the ordered Values list.
        var id = _designer!.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]     = new TextValue(name),
            ["baseType"] = new TextValue("enum"),
            ["values"]   = new TextValue(values),
            ["order"]    = new IntValue(0)
        }));
        _designer!.AddToSet(NodePath.Root.Field("types"), id);
        _typeKeys[name] = id;
    }

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
            SchemaBridge.Export(_designerAppPath, _designerDataPath, _exportedSchemaPath, _exportedDataPath);
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

        // A fresh isolated page on the shared browser (launched once for the whole run; see SharedBrowser).
        ctx.Page = await SharedBrowser.NewPageAsync(ctx.BaseUrl);
        await ctx.Page.GotoContentAsync(ctx.BaseUrl + "/");
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
        await Assert.That(prop.Type).IsEqualTo(refType);
        await Assert.That(_exported!.IsObjectType(prop.Type)).IsTrue();
    }

    [Then("a reference link {string} is present")]
    public async Task ThenReferenceLinkPresentAsync(string fieldName)
    {
        // The self-hosted object form renders a single reference as an inline editor under
        // its labeled field; the retiring C# auto-form rendered it as a link to /<field>.
        var editor = await ctx.Page!.Locator(".ref-editor").CountAsync();
        if (editor > 0) { await Assert.That(editor).IsGreaterThanOrEqualTo(1); return; }
        var link = await ctx.Page!.Locator($"a[href='/{fieldName}']").CountAsync();
        await Assert.That(link).IsGreaterThanOrEqualTo(1);
    }

    [Then("the exported type {string} has a set prop {string} of type {string}")]
    public async Task ThenExportedSetPropAsync(string typeName, string propName, string elemType)
    {
        var prop = _exported!.FindType(typeName)?.Props?.FirstOrDefault(p => p.Name == propName);
        await Assert.That(prop).IsNotNull();
        await Assert.That(prop!.Cardinality).IsEqualTo(Cardinality.Set);
        await Assert.That(prop.Type).IsEqualTo(elemType);
    }

    [Then("the exported type {string} has a dictionary prop {string} of type {string}")]
    public async Task ThenExportedDictionaryPropAsync(string typeName, string propName, string elemType)
    {
        var prop = _exported!.FindType(typeName)?.Props?.FirstOrDefault(p => p.Name == propName);
        await Assert.That(prop).IsNotNull();
        await Assert.That(prop!.Cardinality).IsEqualTo(Cardinality.Dictionary);
        await Assert.That(prop.Type).IsEqualTo(elemType);
    }

    [Then("the exported type {string} is an enum with values {string}")]
    public async Task ThenExportedEnumAsync(string typeName, string values)
    {
        var expected = values.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        var type = _exported!.FindType(typeName);
        await Assert.That(type).IsNotNull();
        await Assert.That(type!.BaseType).IsEqualTo(BaseType.Enum);
        await Assert.That(type.Values).IsNotNull();
        await Assert.That(type.Values!.ToList()).IsEquivalentTo(expected);
    }

    [Then("the exported document declares the enum {string} with values {string}")]
    public async Task ThenExportedDocDeclaresEnumAsync(string typeName, string values)
    {
        // The canonical AppPrint form of an enum: `    Name enum\n` then each value indented 8 spaces.
        // Assert the whole enum block (keyword line + each indented value), not a bare substring that
        // could match a value name elsewhere.
        var doc = File.ReadAllText(_exportedSchemaPath);
        var expected = "    " + typeName + " enum\n"
            + string.Concat(values.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0)
                .Select(v => "        " + v + "\n"));
        await Assert.That(doc.Replace("\r\n", "\n")).Contains(expected);
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
        // Self-hosted inputs are classed by prop name; the C# auto-form keyed them by path.
        var count = await ctx.Page!.Locator($"input.{fieldName}, input[data-path$='/{fieldName}']").CountAsync();
        await Assert.That(count).IsGreaterThanOrEqualTo(1);
    }
}
