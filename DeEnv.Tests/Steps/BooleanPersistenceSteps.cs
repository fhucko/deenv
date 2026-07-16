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
    // The root must be an object type; the single boolean is a field on it.
    private const string BoolJson =
        """
        types
            Db
                ready bool

        """;
    private static readonly NodePath ReadyPath = NodePath.FromSegments(["ready"]);

    private void Seed(bool ready)
    {
        ctx.Description = InstanceDescriptionLoader.Load(BoolJson);
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        ctx.Store.WriteObject(NodePath.Root, new ObjectValue(new Dictionary<string, NodeValue> { ["ready"] = new BoolValue(ready) }));
    }

    [Given("a single-boolean instance with value unchecked")]
    public void GivenUnchecked() => Seed(false);

    [Given("a single-boolean instance with value checked")]
    public void GivenChecked() => Seed(true);

    [When("the value is set to checked")]
    public void WhenSetChecked() =>
        ctx.Store!.WriteObject(NodePath.Root, new ObjectValue(new Dictionary<string, NodeValue> { ["ready"] = new BoolValue(true) }));

    [Then("reading the stored value returns checked")]
    public async Task ThenReturnsChecked()
    {
        var value = ctx.Store!.ReadNode(ReadyPath);
        await Assert.That(value).IsEqualTo(new BoolValue(true));
    }

    [When("the instance is stopped and started again")]
    public void WhenRestarted()
    {
        // Simulate restart: re-open the store from the same file (no in-memory cache).
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description!);
    }
}
