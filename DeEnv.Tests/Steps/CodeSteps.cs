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

    // One app text document describes the whole instance (M7).
    [Given("the code instance:")]
    public void GivenCodeInstance(string appText)
    {
        ctx.Description = InstanceDescriptionLoader.Load(appText);
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

    [Given("the people instance seeded with salaries")]
    public void GivenPeopleSeeded()
    {
        ctx.Description = InstanceContext.SensitiveUiDb();
        var store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        ctx.Store = store;
        SeedPerson(store, "Ada", salary: 999); // high earner (> 100)
        SeedPerson(store, "Bob", salary: 5);   // not an earner
    }

    // Same seed, but the app wraps the where-result in a top-scope minted object (NestedFilterPrivacyDb):
    // a shipping object whose nested filtered array must stay access-scoped, not spill its membership.
    [Given("the nested-filter privacy instance seeded with salaries")]
    public void GivenNestedFilterPrivacySeeded()
    {
        ctx.Description = InstanceContext.NestedFilterPrivacyDb();
        var store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        ctx.Store = store;
        SeedPerson(store, "Ada", salary: 999);  // matches box filter (> 100) AND displayed (> 600)
        SeedPerson(store, "Cleo", salary: 500); // matches box filter, NOT displayed (< 600)
        SeedPerson(store, "Bob", salary: 5);    // no match
    }

    private static void SeedPerson(IInstanceStore store, string name, int salary)
    {
        var id = store.CreateObject("Person", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name),
            ["salary"] = new IntValue(salary),
        }));
        store.AddToSet(NodePath.Root.Field("people"), id);
    }

    // ── When ──────────────────────────────────────────────────────────────────

    [When("the page at {string} is rendered")]
    public void WhenPageRendered(string path)
    {
        var renderer = new SsrRenderer(ctx.Store!, ctx.Description!);
        ctx.RenderedHtml = renderer.Render(path).Html;
    }

    // ── Then ──────────────────────────────────────────────────────────────────
    //
    // Assertions are scoped to the rendered <body> — the visible first paint — not the
    // whole document, so the <head>'s window.initData/initUi data island (which carries
    // the same field values) does not pollute substring counts or ordering.

    // Checks the WHOLE document (head data island included), so it catches a value
    // leaking through window.initData even when it is never displayed in the body.
    [Then("the page does not include {string}")]
    public async Task ThenPageDoesNotInclude(string fragment)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(ctx.RenderedHtml!.Contains(fragment)).IsFalse();
    }

    [Then("the rendered HTML contains {string}")]
    public async Task ThenHtmlContains(string fragment)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(RenderedBody()).Contains(fragment);
    }

    [Then("the rendered HTML does not contain {string}")]
    public async Task ThenHtmlDoesNotContain(string fragment)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(RenderedBody().Contains(fragment)).IsFalse();
    }

    [Then("the rendered HTML contains {string} before {string}")]
    public async Task ThenHtmlContainsInOrder(string first, string second)
    {
        var body = RenderedBody();
        var i = body.IndexOf(first, StringComparison.Ordinal);
        var j = body.IndexOf(second, StringComparison.Ordinal);
        await Assert.That(i).IsGreaterThanOrEqualTo(0);
        await Assert.That(j).IsGreaterThan(i);
    }

    [Then("the rendered HTML contains {string} exactly {int} times")]
    public async Task ThenHtmlContainsCount(string fragment, int count)
    {
        await Assert.That(Occurrences(RenderedBody(), fragment)).IsEqualTo(count);
    }

    // Privacy membership pin (Milestone 11): a minted object that ships and nests a filtered
    // collection must ship that collection ACCESS-SCOPED — only its DISPLAYED items, never its full
    // membership. The rows are positive-id db objects, so an undisplayed row's FIELD values never
    // ship regardless (only accessed props of a positive-id object ship); the leak the broad
    // "ship any negative-id array nested in a complete object" rule caused is STRUCTURAL — the
    // undisplayed row's array item + empty object stub. So we assert on the shipped client state
    // (window.initData): the minted collection's shipped item count equals the displayed row count.
    [Then("the minted collection ships only its displayed rows")]
    public async Task ThenMintedCollectionShipsOnlyDisplayed()
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        var displayed = Occurrences(RenderedBody(), "class=\"earner\"");

        var state = ExtractInitData(ctx.RenderedHtml!);
        // The minted box is a negative-id object in the shipped scope; follow its single array prop.
        var boxId = state["scope"]!["box"]!["value"]!["id"]!.GetValue<int>();
        var box = state["leaves"]!["objects"]![boxId.ToString()]!;
        var rowsArrayId = box["props"]!["rows"]!["id"]!.GetValue<int>();
        var rows = state["leaves"]!["arrays"]![rowsArrayId.ToString()]!["items"]!.AsArray();

        // Red under the broad rule (ships the full where-membership), green under the fix.
        await Assert.That(rows.Count).IsEqualTo(displayed);
    }

    private static System.Text.Json.Nodes.JsonNode ExtractInitData(string html)
    {
        const string marker = "window.initData=";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = html.IndexOf(";window.initUi=", start, StringComparison.Ordinal);
        return System.Text.Json.Nodes.JsonNode.Parse(html[start..end])!;
    }

    // The content between <body> and </body> of the rendered page.
    private string RenderedBody()
    {
        var html = ctx.RenderedHtml!;
        var start = html.IndexOf("<body>", StringComparison.Ordinal);
        var end = html.IndexOf("</body>", StringComparison.Ordinal);
        return start < 0 || end < 0 ? html : html[(start + "<body>".Length)..end];
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
