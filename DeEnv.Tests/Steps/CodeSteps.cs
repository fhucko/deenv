using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for the Code milestone, Stage 2: server-side rendering of a hand-written
// `ui` component to HTML. The renderer is driven directly (no browser) since the
// client runtime does not exist yet — the contract under test is the SSR output.
[Binding]
public sealed class CodeSteps(InstanceContext ctx)
{
    // ── Given ─────────────────────────────────────────────────────────────────

    [Given("the tasks UI instance seeded with sample tasks")]
    public void GivenTasksUiSeeded()
    {
        ctx.Description = InstanceContext.TasksUiDb();
        var store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        ctx.Store = store;

        // Out of priority order on purpose, so orderBy has work to do.
        SeedTask(store, "Beta", done: false, priority: 2);
        SeedTask(store, "Alpha", done: true, priority: 1);
        SeedTask(store, "Gamma", done: false, priority: 3);
    }

    [Given("the code instance:")]
    public void GivenCodeInstance(string json)
    {
        ctx.Description = InstanceDescriptionLoader.Load(json);
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    [Given("a generic instance with no code")]
    public void GivenGenericInstance()
    {
        ctx.Description = InstanceContext.ObjectGraphDb();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    private static void SeedTask(IInstanceStore store, string title, bool done, int priority)
    {
        var id = store.CreateObject("Task", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["title"] = new TextValue(title),
            ["done"] = new BoolValue(done),
            ["priority"] = new IntValue(priority),
        }));
        store.AddToSet(NodePath.Root.Field("tasks"), id);
    }

    // ── When ──────────────────────────────────────────────────────────────────

    [When("the page at {string} is rendered")]
    public void WhenPageRendered(string path)
    {
        var renderer = new SsrRenderer(ctx.Store!, ctx.Description!);
        ctx.RenderedHtml = renderer.Render(path);
    }

    // ── Then ──────────────────────────────────────────────────────────────────

    [Then("the rendered HTML contains {string}")]
    public async Task ThenHtmlContains(string fragment)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(ctx.RenderedHtml!).Contains(fragment);
    }

    [Then("the rendered HTML does not contain {string}")]
    public async Task ThenHtmlDoesNotContain(string fragment)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(ctx.RenderedHtml!.Contains(fragment)).IsFalse();
    }

    [Then("the rendered HTML contains {string} before {string}")]
    public async Task ThenHtmlContainsInOrder(string first, string second)
    {
        var html = ctx.RenderedHtml!;
        var i = html.IndexOf(first, StringComparison.Ordinal);
        var j = html.IndexOf(second, StringComparison.Ordinal);
        await Assert.That(i).IsGreaterThanOrEqualTo(0);
        await Assert.That(j).IsGreaterThan(i);
    }

    [Then("the rendered HTML contains {string} exactly {int} times")]
    public async Task ThenHtmlContainsCount(string fragment, int count)
    {
        await Assert.That(Occurrences(ctx.RenderedHtml!, fragment)).IsEqualTo(count);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
