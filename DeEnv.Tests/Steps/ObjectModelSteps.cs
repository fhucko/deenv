using System.Text.RegularExpressions;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for the Milestone 5 object model scenarios (ObjectModel.feature).
//
// Seeding goes through the store API (CreateObject/AddToSet/SetReference) so
// the step definitions stay independent of the on-disk JSON format.
// Mutations during a scenario (create / pick / remove) go through the running
// app (UI + WebSocket), exercising the real identity/extent/GC behavior.
[Binding]
public sealed class ObjectModelSteps(InstanceContext ctx)
{
    // Person ids assigned at seed time, so a scenario can still name an object
    // after the app has collected it (its id is gone from the extent).
    private readonly Dictionary<string, int> _idByName = new();

    // ── Given ───────────────────────────────────────────────────────────────────

    [Given("an object-graph instance")]
    public void GivenObjectGraphInstance()
    {
        ctx.Description = InstanceContext.ObjectGraphDb();
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    [Given(@"a person {string} in the extent referenced by the set {string}")]
    public void GivenPersonInSet(string name, string setName)
    {
        var id = ctx.Store!.CreateObject("Person", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name)
        }));
        ctx.Store!.AddToSet(NodePath.Root.Field(setName), id);
        _idByName[name] = id;
    }

    [Given(@"the single reference {string} references the person {string}")]
    public void GivenSingleReference(string refName, string name)
    {
        var id = _idByName.TryGetValue(name, out var known) ? known : FindPersonId(name);
        ctx.Store!.SetReference(NodePath.Root.Field(refName), id);
    }

    // ── When ────────────────────────────────────────────────────────────────────

    // The self-hosted setTable shows its add form (.set-new) inline; Add stays on the list
    // (no save-and-open), so we follow the new member's open link to its own page.
    [When(@"I create a new {string} named {string} in the set")]
    public async Task WhenCreateNewInSetAsync(string typeName, string name)
    {
        await ctx.Page!.Locator(".set-new input.name").FillAsync(name);
        await ctx.Page.Locator("button.set-add").ClickAsync();
        await ctx.Page.Locator(".set-row", new() { HasTextString = name }).First.WaitForAsync();
        await ctx.Page.WaitForTimeoutAsync(500); // negative→real id remap settles
        await ctx.Page.Locator(".set-row", new() { HasTextString = name })
                      .First.Locator("a.set-open").ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(400);
    }

    // The self-hosted reference editor's create-new: fill the .ref-new draft, then Create.
    [When(@"I create a new {string} named {string} through the reference")]
    public async Task WhenCreateNewThroughReferenceAsync(string typeName, string name)
    {
        await ctx.Page!.Locator(".ref-new input.name").FillAsync(name);
        await ctx.Page.Locator("button.ref-create").ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(400);
    }

    // The self-hosted reference editor offers each candidate as a .ref-pick button.
    [When(@"I pick the existing {string} named {string}")]
    public async Task WhenPickExistingAsync(string typeName, string name)
    {
        await ctx.Page!.Locator("button.ref-pick", new() { HasTextString = name }).First.ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(400);
    }

    [When(@"I open the id-route for {string}")]
    public async Task WhenOpenIdRouteAsync(string name)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl + "/~/" + IdOf(name));
    }

    [When(@"I remove {string} from the set {string}")]
    public async Task WhenRemoveFromSetAsync(string name, string setName)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl + "/" + setName);
        // The self-hosted setTable's per-row Remove button (.set-remove) unlinks the member.
        await ctx.Page.Locator(".set-row", new() { HasTextString = name })
                      .Locator("button.set-remove").First.ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(700); // unlink + GC
    }

    // ── Then ────────────────────────────────────────────────────────────────────

    [Then(@"the set {string} lists {string}")]
    public async Task ThenSetListsAsync(string setName, string name) =>
        // .set-row (class), not `table tbody tr`: the client reconciler appends <tr> directly
        // to <table>, so there is no <tbody> after hydration. Wait for the row to settle.
        await ctx.Page!.Locator(".set-row", new() { HasTextString = name }).First.WaitForAsync();

    [Then(@"following {string} in the set {string} opens the same object both times")]
    public async Task ThenSameObjectBothTimesAsync(string name, string setName)
    {
        var href1 = await MemberHrefAsync(setName, name);
        await Assert.That(Regex.IsMatch(href1, $"/{setName}/\\d+$")).IsTrue();

        // Following it opens the member object (name shows), then re-reading the
        // member's link yields the same identity address — it is one object.
        await ctx.Page!.GotoAsync(ctx.BaseUrl + href1);
        var shown = await ctx.Page.Locator("input.name").First.GetAttributeAsync("value") ?? "";
        await Assert.That(shown).IsEqualTo(name);

        await ctx.Page.GotoAsync(ctx.BaseUrl + "/" + setName);
        var href2 = await MemberHrefAsync(setName, name);
        await Assert.That(href2).IsEqualTo(href1);
    }

    private async Task<string> MemberHrefAsync(string setName, string name) =>
        await ctx.Page!.Locator(".set-row", new() { HasTextString = name })
                      .First.Locator("a.set-open").First.GetAttributeAsync("href") ?? "";

    [Then(@"navigating to {string} shows the {string} field {string}")]
    public async Task ThenNavigatingShowsFieldAsync(string path, string field, string expected)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl + path);
        var selfHosted = ctx.Page.Locator($"input.{field}");
        var input = await selfHosted.CountAsync() > 0 ? selfHosted.First
            : ctx.Page.Locator($"input[data-path$='/{field}']").First;
        var value = await input.GetAttributeAsync("value") ?? "";
        await Assert.That(value).IsEqualTo(expected);
    }

    [Then(@"the extent {string} has {int} object(s)")]
    public async Task ThenExtentCountAsync(string typeName, int count)
    {
        await Assert.That(ExtentCount(typeName)).IsEqualTo(count);
    }

    [Then(@"the id-route for {string} is not found")]
    public async Task ThenIdRouteNotFoundAsync(string name)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl + "/~/" + IdOf(name));
        await Assert.That(await ctx.Page.ContentAsync()).Contains("Not found");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private int ExtentCount(string typeName) =>
        ctx.Store!.ReadExtent(typeName).Count;

    private int FindPersonId(string name)
    {
        foreach (var (id, obj) in ctx.Store!.ReadExtent("Person"))
            if (obj.Fields.TryGetValue("name", out var v) && v is TextValue t && t.Text == name)
                return id;
        throw new InvalidOperationException($"No person named '{name}' in the extent.");
    }

    private int IdOf(string name) =>
        _idByName.TryGetValue(name, out var id) ? id : FindPersonId(name);
}
