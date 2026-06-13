using DeEnv.Instance;
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
        File.WriteAllText(ctx.SchemaFilePath, "types\n    Db\n        ready: bool\n");
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
}
