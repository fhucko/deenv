using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

[Binding]
public sealed class FilterExpressionSteps(InstanceContext ctx)
{
    private readonly Dictionary<string, int> _taskIds   = new();
    private readonly Dictionary<string, int> _personIds = new();

    // ── Given (instance + seed data) ───────────────────────────────────────────

    [Given("a filter-task instance")]
    public void GivenFilterTaskInstance()
    {
        ctx.Description = InstanceContext.FilterTaskDb();
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    [Given(@"a task {string} with done {word} and priority {int}")]
    public void GivenTask(string title, string done, int priority)
    {
        var id = ctx.Store!.CreateObject("Task", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["title"]    = new TextValue(title),
            ["done"]     = new BoolValue(bool.Parse(done)),
            ["priority"] = new IntValue(priority)
        }));
        ctx.Store!.AddToSet(NodePath.Root.Field("tasks"), id);
        _taskIds[title] = id;
    }

    [Given(@"a person {string}")]
    public void GivenPerson(string name)
    {
        var id = ctx.Store!.CreateObject("Person", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name)
        }));
        _personIds[name] = id;
    }

    [Given(@"a task {string} assigned to {string} with done {word} and priority {int}")]
    public void GivenTaskAssigned(string title, string assigneeName, string done, int priority)
    {
        var taskId = ctx.Store!.CreateObject("Task", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["title"]    = new TextValue(title),
            ["done"]     = new BoolValue(bool.Parse(done)),
            ["priority"] = new IntValue(priority)
        }));
        ctx.Store!.AddToSet(NodePath.Root.Field("tasks"), taskId);
        ctx.Store!.SetReference(
            NodePath.Root.Field("tasks").Key(taskId.ToString()).Field("assignee"),
            _personIds[assigneeName]);
        _taskIds[title] = taskId;
    }

    // ── When ────────────────────────────────────────────────────────────────────

    [When("I navigate to the tasks set")]
    public async Task WhenNavigateToTasksSetAsync()
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl + "/tasks");
    }

    [When(@"I type the filter {string}")]
    public async Task WhenTypeFilterAsync(string expression)
    {
        await ctx.Page!.Locator(".set-filter input").FillAsync(expression);
        await ctx.Page.WaitForTimeoutAsync(600); // 300 ms debounce + evaluation
    }

    [When("I clear the filter")]
    public async Task WhenClearFilterAsync()
    {
        await ctx.Page!.Locator(".set-filter input").ClearAsync();
        await ctx.Page.WaitForTimeoutAsync(600);
    }

    // ── Then ────────────────────────────────────────────────────────────────────

    [Then("the filter input shows an error")]
    public async Task ThenFilterInputShowsErrorAsync()
    {
        var input = ctx.Page!.Locator(".set-filter input");
        var hasError = await input.EvaluateAsync<bool>("el => el.classList.contains('filter-error')");
        await Assert.That(hasError).IsTrue();
    }

    [Then(@"only the row {string} is visible")]
    public async Task ThenOnlyRowVisibleAsync(string text)
    {
        var visible = ctx.Page!.Locator("table tbody tr:not([hidden])");
        await Assert.That(await visible.CountAsync()).IsEqualTo(1);
        await Assert.That(await visible.First.InnerTextAsync()).Contains(text);
    }

    [Then(@"{int} rows are visible")]
    public async Task ThenRowsVisibleAsync(int count)
    {
        var visible = ctx.Page!.Locator("table tbody tr:not([hidden])");
        await Assert.That(await visible.CountAsync()).IsEqualTo(count);
    }
}
