using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

[Binding]
public sealed class BooleanPersistenceSteps(InstanceContext ctx)
{
    private const string BoolJson = """{ "types": [{ "name": "Db", "baseType": "bool" }] }""";

    [Given("a single-boolean instance with value unchecked")]
    public void GivenUnchecked()
    {
        ctx.Description = InstanceDescriptionLoader.Load(BoolJson);
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        ctx.Store.WriteLeaf(NodePath.Root, new BoolValue(false));
    }

    [Given("a single-boolean instance with value checked")]
    public void GivenChecked()
    {
        ctx.Description = InstanceDescriptionLoader.Load(BoolJson);
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        ctx.Store.WriteLeaf(NodePath.Root, new BoolValue(true));
    }

    [When("the value is set to checked")]
    public void WhenSetChecked() =>
        ctx.Store!.WriteLeaf(NodePath.Root, new BoolValue(true));

    [Then("reading the stored value returns checked")]
    public async Task ThenReturnsChecked()
    {
        var value = ctx.Store!.ReadNode(NodePath.Root);
        await Assert.That(value).IsEqualTo(new BoolValue(true));
    }

    [When("the instance is stopped and started again")]
    public void WhenRestarted()
    {
        // Simulate restart: re-open the store from the same file (no in-memory cache).
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description!);
    }
}
