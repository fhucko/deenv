using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

[Binding]
public sealed class SchemaSteps(InstanceContext ctx)
{
    // ── Given ─────────────────────────────────────────────────────────────────

    [Given("the app description:")]
    public void GivenAppDescription(string appText)
    {
        ctx.SchemaJson = appText;
    }

    [Given("an app description file describing a single-bool Db")]
    public void GivenAppFileBoolDb()
    {
        ctx.SchemaFilePath = Path.GetTempFileName();
        File.WriteAllText(ctx.SchemaFilePath,
            """
            types
                Db
                    ready bool

            """);
    }

    // ── When ──────────────────────────────────────────────────────────────────

    [When("the document is loaded")]
    public void WhenDocumentLoaded()
    {
        try
        {
            ctx.LoadedDescription = InstanceDescriptionLoader.Load(ctx.SchemaJson!);
        }
        catch (Exception ex)
        {
            ctx.LoadError = ex;
        }
    }

    [When("the instance is started from that file")]
    public async Task WhenStartedFromFileAsync()
    {
        ctx.Description = InstanceDescriptionLoader.LoadFile(ctx.SchemaFilePath!);
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Server = new TestInstanceServer();
        await ctx.Server.StartAsync(ctx.Description, ctx.DataFilePath);
        ctx.Store = ctx.Server.Store;
    }

    // ── Then ──────────────────────────────────────────────────────────────────

    [Then("the document loads successfully")]
    public async Task ThenLoadsSuccessfullyAsync()
    {
        await Assert.That(ctx.LoadError).IsNull();
        await Assert.That(ctx.LoadedDescription).IsNotNull();
    }

    [Then("the root type is named {string}")]
    public async Task ThenRootTypeNamedAsync(string name)
    {
        await Assert.That(ctx.LoadedDescription!.Db()).IsNotNull();
        await Assert.That(ctx.LoadedDescription!.Db()!.Name).IsEqualTo(name);
    }

    [Then("loading is rejected with an error mentioning {string}")]
    public async Task ThenRejectedMentioningAsync(string phrase)
    {
        await Assert.That(ctx.LoadError).IsNotNull();
        await Assert.That(ctx.LoadError).IsTypeOf<SchemaValidationException>();
        await Assert.That(ctx.LoadError!.Message).Contains(phrase);
    }

    // ── enum support (first slice) ──────────────────────────────────────────────

    [Then("the type {string} is an enum with values {string}")]
    public async Task ThenEnumWithValues(string typeName, string commaList)
    {
        var type = ctx.LoadedDescription!.FindType(typeName);
        await Assert.That(type).IsNotNull();
        await Assert.That(type!.BaseType).IsEqualTo(BaseType.Enum);
        var expected = commaList.Split(',').Select(s => s.Trim()).ToArray();
        await Assert.That(type.Values).IsEquivalentTo(expected);
    }

    // ── multiline text presentation attribute ───────────────────────────────────

    // The `multiline` keyword parsed onto the prop sets its Multiline bool (the model field the
    // generic UI's descriptor reads to render a <textarea>); a plain prop's stays false.
    [Then("the {string} prop of {string} is multiline")]
    public async Task ThenPropMultiline(string propName, string typeName) =>
        await Assert.That(PropOf(typeName, propName).Multiline).IsTrue();

    [Then("the {string} prop of {string} is not multiline")]
    public async Task ThenPropNotMultiline(string propName, string typeName) =>
        await Assert.That(PropOf(typeName, propName).Multiline).IsFalse();

    private PropDefinition PropOf(string typeName, string propName) =>
        ctx.LoadedDescription!.FindType(typeName)!.Props!.First(p => p.Name == propName);

    // parse(print(d)) ≡ d (structural) AND the printed form is a fixpoint — the same
    // round-trip the printer tests assert, here as the enum scenario's named proof.
    [Then("the document round-trips through the printer")]
    public async Task ThenRoundTrips()
    {
        var first = ctx.LoadedDescription!;
        var printed = AppPrint.Print(first);
        var second = AppParse.Parse(printed);
        var a = JsonSerializer.SerializeToNode(first, SchemaJson.Options)!;
        var b = JsonSerializer.SerializeToNode(second, SchemaJson.Options)!;
        await Assert.That(JsonNode.DeepEquals(a, b)).IsTrue();
        await Assert.That(AppPrint.Print(second)).IsEqualTo(printed);
    }

    // ── enum off-list enforcement on the WRITE path (WsHandler) ─────────────────

    private WsHandler? _enumWs;
    private string _enumReply = "";
    private const int OrderId = 2; // the seeded Order in the enum fixture

    [Given("the enum fixture instance is running")]
    public void GivenEnumFixtureInstance()
    {
        ctx.Description = InstanceContext.EnumFixtureDb();
        ctx.DataFilePath = Path.GetTempFileName();
        File.Delete(ctx.DataFilePath); // let the store seed from initialData
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        _enumWs = new WsHandler(ctx.Store, ctx.Description);
    }

    [When("the order's status is set to {string} over the WS")]
    public void WhenSetStatus(string value) =>
        _enumReply = _enumWs!.ProcessMessage(
            $$"""{ "op": "commit", "edits": [ { "objectId": {{OrderId}}, "prop": "status", "value": { "type": "text", "value": "{{value}}" } } ], "creates": [], "relations": [] }""");

    [Then("the change is accepted")]
    public async Task ThenAccepted()
    {
        using var doc = JsonDocument.Parse(_enumReply);
        await Assert.That(doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()).IsTrue();
    }

    [Then("the change is rejected")]
    public async Task ThenRejected()
    {
        using var doc = JsonDocument.Parse(_enumReply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsTrue();
    }

    [Then("the order's stored status is {string}")]
    public async Task ThenStoredStatus(string expected)
    {
        var order = ctx.Store!.ReadExtent("Order")[OrderId];
        await Assert.That(order.Fields["status"]).IsEqualTo((NodeValue)new TextValue(expected));
    }
}
