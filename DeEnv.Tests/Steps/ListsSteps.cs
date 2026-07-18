using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Slice 2+3 durable-list Gherkin bindings — store/load + CommitBatch list mutators (OCC-free).
[Binding]
public sealed class ListsSteps(InstanceContext ctx)
{
    private int? _capturedListId;

    [Then(@"the root list ""([^""]+)"" is empty with a positive id")]
    public async Task ThenRootListEmptyPositiveId(string prop)
    {
        var list = RootList(prop);
        await Assert.That(list.Id).IsGreaterThan(0);
        await Assert.That(list.Items.Count).IsEqualTo(0);
        _capturedListId = list.Id;
    }

    [Then(@"the root list ""([^""]+)"" has member ids ([\d, ]+) in order")]
    public async Task ThenRootListMemberIds(string prop, string csv)
    {
        var expected = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).ToArray();
        var list = RootList(prop);
        _capturedListId ??= list.Id;
        await Assert.That(list.Items.Count).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
            await Assert.That(((ReferenceValue)list.Items[i]).TargetId).IsEqualTo(expected[i]);
    }

    [Then(@"the root list ""([^""]+)"" kept its positive id")]
    public async Task ThenRootListKeptId(string prop)
    {
        var list = RootList(prop);
        await Assert.That(list.Id).IsGreaterThan(0);
        await Assert.That(_capturedListId).IsNotNull();
        await Assert.That(list.Id).IsEqualTo(_capturedListId!.Value);
    }

    [Then(@"the root list ""([^""]+)"" has one member")]
    public async Task ThenRootListOneMember(string prop)
    {
        var list = RootList(prop);
        await Assert.That(list.Items.Count).IsEqualTo(1);
        await Assert.That(list.Items[0]).IsTypeOf<ReferenceValue>();
    }

    [Then(@"the extent ""([^""]+)"" contains exactly (\d+) object")]
    public async Task ThenExtentContainsExactlyNObjects(string typeName, int n)
    {
        var actual = ctx.Store!.ReadExtent(typeName).Count;
        await Assert.That(actual).IsEqualTo(n);
    }

    [When(@"member (\d+) is removed from set id (\d+)")]
    public void WhenRemoveFromSetById(int memberId, int setId)
    {
        ctx.Store!.RemoveFromSet(setId, memberId);
    }

    [When(@"the root list ""([^""]+)"" is replaced with member ids ([\d, ]+)")]
    public void WhenRootListReplaced(string prop, string csv)
    {
        var list = RootList(prop);
        _capturedListId = list.Id;
        var ids = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).ToArray();
        var items = ids.Select(id => (StoredValue)new StoredRef("Task", id)).ToList();
        ctx.Store!.CommitBatch([], [new ListReplaceMutation(list.Id, items)]);
    }

    [When(@"member (\d+) is inserted at index (\d+) of root list ""([^""]+)""")]
    public void WhenInsertMember(int memberId, int index, string prop)
    {
        var list = RootList(prop);
        _capturedListId = list.Id;
        ctx.Store!.CommitBatch([], [new ListInsertMutation(list.Id, index, new StoredRef("Task", memberId))]);
    }

    [When(@"the root list ""([^""]+)"" moves index (\d+) to (\d+)")]
    public void WhenMove(string prop, int from, int to)
    {
        var list = RootList(prop);
        ctx.Store!.CommitBatch([], [new ListMoveMutation(list.Id, from, to)]);
    }

    [When(@"index (\d+) is removed from root list ""([^""]+)""")]
    public void WhenRemoveAt(int index, string prop)
    {
        var list = RootList(prop);
        ctx.Store!.CommitBatch([], [new ListRemoveAtMutation(list.Id, index)]);
    }

    [When(@"a new Task titled ""([^""]+)"" is created and inserted at index (\d+) of root list ""([^""]+)""")]
    public void WhenCreateAndInsert(string title, int index, string prop)
    {
        var list = RootList(prop);
        ctx.Store!.CommitBatch(
            [new CommitCreate(-1, "Task", new ObjectValue(new Dictionary<string, NodeValue> {
                ["title"] = new TextValue(title),
            }))],
            [new ListInsertMutation(list.Id, index, new StoredRef("Task", -1))]);
    }

    [Then(@"the extent ""([^""]+)"" has objects ([\d, ]+) only")]
    public async Task ThenExtentHasObjectsOnly(string typeName, string idsCsv)
    {
        var expected = idsCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).OrderBy(x => x).ToArray();
        var actual = ctx.Store!.ReadExtent(typeName).Keys.OrderBy(x => x).ToArray();
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    private ListValue RootList(string prop)
    {
        var root = ctx.Store!.ReadById(1)
            ?? throw new InvalidOperationException("Db root missing.");
        return root.Fields.Fields[prop] as ListValue
            ?? throw new InvalidOperationException($"Root has no list prop '{prop}'.");
    }
}
