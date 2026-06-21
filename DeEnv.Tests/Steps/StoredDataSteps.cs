using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

[Binding]
public sealed class StoredDataSteps(InstanceContext ctx)
{
    // ── Given ─────────────────────────────────────────────────────────────────

    [Given("a stored data file containing:")]
    public void GivenStoredDataFile(string json)
    {
        File.WriteAllText(ctx.DataFilePath, json);
    }

    // ── When ──────────────────────────────────────────────────────────────────

    [When("the store is opened")]
    public void WhenStoreOpened() => OpenStore();

    [When("the store is opened again on the same data file")]
    public void WhenStoreReopened() => OpenStore();

    private void OpenStore()
    {
        try
        {
            ctx.Description ??= InstanceDescriptionLoader.Load(ctx.SchemaJson!);
            ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        }
        catch (Exception ex)
        {
            ctx.LoadError = ex;
        }
    }

    // ── Then ──────────────────────────────────────────────────────────────────

    [Then("opening is rejected with a data error mentioning {string}")]
    public async Task ThenRejectedMentioningAsync(string phrase)
    {
        await Assert.That(ctx.LoadError).IsNotNull();
        await Assert.That(ctx.LoadError).IsTypeOf<StoredDataException>();
        await Assert.That(ctx.LoadError!.Message).Contains(phrase);
    }

    [Then("opening is rejected with a data error mentioning the data file path")]
    public async Task ThenRejectedMentioningPathAsync()
    {
        await Assert.That(ctx.LoadError).IsNotNull();
        await Assert.That(ctx.LoadError).IsTypeOf<StoredDataException>();
        await Assert.That(ctx.LoadError!.Message).Contains(ctx.DataFilePath);
    }

    [Then("the store opens successfully")]
    public async Task ThenStoreOpensAsync()
    {
        await Assert.That(ctx.LoadError).IsNull();
        await Assert.That(ctx.Store).IsNotNull();
    }

    [Then("reading {string} returns text {string}")]
    public async Task ThenReadingReturnsTextAsync(string path, string expected)
    {
        var segs = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var node = ctx.Store!.ReadNode(NodePath.FromSegments(segs));
        await Assert.That(node).IsTypeOf<TextValue>();
        await Assert.That(((TextValue)node!).Text).IsEqualTo(expected);
    }
}
