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

    // The self-hosted setTable's create form is flag-gated (revealed by `+ New`); Save stays on the
    // list (no save-and-open), so we follow the new member's open link to its own page.
    [When(@"I create a new {string} named {string} in the set")]
    public async Task WhenCreateNewInSetAsync(string typeName, string name)
    {
        await ctx.Page!.RevealCreateFormAsync(); // reveal the gated create form (the set page was a read-only nav)
        await ctx.Page!.Locator(".create-form input.name").FillAsync(name);
        await ctx.Page.Locator("button.create-save").First.ClickAsync();
        await ctx.Page.Locator(".set-row", new() { HasTextString = name }).First.WaitForAsync();
        // Wait for the negative→real id remap (positive href).
        await ctx.Page.Locator(".set-row", new() {
            HasTextString = name
        }).Locator("a.row-link[href^=\"/\"]:not([href*=\"/-\"])").First.WaitForAsync();
        await ctx.Page.Locator(".set-row", new() { HasTextString = name })
                      .First.Locator("a.row-link").ClickAsync();
        // Following the open link is a real navigation; wait for the member page URL.
        await ctx.Page.WaitForUrlContentAsync(new Regex(@"/[0-9]+$"));
    }

    // The self-hosted reference editor's create-new: reveal the gated create form, fill the draft, Save.
    [When(@"I create a new {string} named {string} through the reference")]
    public async Task WhenCreateNewThroughReferenceAsync(string typeName, string name)
    {
        // WaitReadyAsync, not just WaitHydratedAsync: Create mints + links the object over the WS
        // (setReferenceField), so the socket must be fully settled (open + claimed) — a
        // hydrated-but-not-ready page would ride the connecting-window outbox and could delay/lose
        // the mutation under load. The reference page was reached by a read-only nav.
        await ctx.Page!.WaitReadyAsync();
        // ponytail: was `.ref-new input.name` + `button.ref-create`; the bespoke ref-new form was
        // replaced by a nested create-mode ObjectForm behind the same `+ New` toggle as SetTable (B1
        // collapse). Reveal it first; the save button is the join-agnostic `.create-save`.
        await ctx.Page!.RevealCreateFormAsync();
        await ctx.Page!.Locator(".create-form input.name").FillAsync(name);
        await ctx.Page.Locator("button.create-save").First.ClickAsync();
        // Wait for the created object to be minted + referenced — the editor shows it as current.
        await ctx.Page.Locator(".ref-current", new() { HasTextString = name }).First.WaitForAsync();
    }

    // The self-hosted reference editor offers candidates in a .ref-pick <select>; pick one and commit
    // with Set (the Set button renders for the chosen candidate, then sys.setRef).
    [When(@"I pick the existing {string} named {string}")]
    public async Task WhenPickExistingAsync(string typeName, string name)
    {
        // WaitReadyAsync, not just WaitHydratedAsync: Set links the reference over the WS
        // (setReferenceField) — wait for the settled (open + claimed) socket so the mutation acts on
        // an established connection. The reference page was reached by a read-only nav.
        await ctx.Page!.WaitReadyAsync();
        await ctx.Page!.Locator("select.ref-pick").First.SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Label = name });
        await ctx.Page.Locator("button.ref-set").First.ClickAsync();
        // Wait for the reference to be set — the editor shows the picked object as current.
        await ctx.Page.Locator(".ref-current", new() { HasTextString = name }).First.WaitForAsync();
    }

    [When(@"I remove {string} from the set {string}")]
    public async Task WhenRemoveFromSetAsync(string name, string setName)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoReadyAsync(ctx.BaseUrl + "/" + setName);
        // WaitReadyAsync before the remove: the unlink (arrayRemove) goes over the WS and triggers
        // server-side GC — wait for the settled (open + claimed) socket so the mutation acts on an
        // established connection (a hydrated-but-not-ready page could ride the connecting-window
        // outbox and delay/lose it under load).
        await ctx.Page!.WaitReadyAsync();
        // The self-hosted setTable's per-row Remove button (.set-remove) unlinks the member.
        await ctx.Page!.Locator(".set-row", new() { HasTextString = name })
                      .Locator("button.set-remove").First.ClickAsync();
        // The row disappears once the server confirms the unlink (and its GC ran) — poll for that.
        await ctx.Page.Locator(".set-row", new() { HasTextString = name }).First
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
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
        await ctx.Page!.GotoContentAsync(ctx.BaseUrl + href1);
        var shown = await ctx.Page!.Locator("input.name").First.GetAttributeAsync("value") ?? "";
        await Assert.That(shown).IsEqualTo(name);

        await ctx.Page.GotoContentAsync(ctx.BaseUrl + "/" + setName);
        var href2 = await MemberHrefAsync(setName, name);
        await Assert.That(href2).IsEqualTo(href1);
    }

    private async Task<string> MemberHrefAsync(string setName, string name) =>
        await ctx.Page!.Locator(".set-row", new() { HasTextString = name })
                      .First.Locator("a.row-link").First.GetAttributeAsync("href") ?? "";

    [Then(@"navigating to {string} shows the {string} field {string}")]
    public async Task ThenNavigatingShowsFieldAsync(string path, string field, string expected)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoContentAsync(ctx.BaseUrl + path);
        var selfHosted = ctx.Page!.Locator($"input.{field}");
        var input = await selfHosted.CountAsync() > 0 ? selfHosted.First
            : ctx.Page.Locator($"input[data-path$='/{field}']").First;
        var value = await input.GetAttributeAsync("value") ?? "";
        await Assert.That(value).IsEqualTo(expected);
    }

    [Then(@"the extent {string} has {int} object(s)")]
    public async Task ThenExtentCountAsync(string typeName, int count)
    {
        // Poll the REAL store count, do not read once: the preceding mutation (a create-through-
        // reference, a pick, a set-remove) lands in the store via an ASYNC WS round-trip — the
        // server mints/links the object and runs mark-sweep GC on its own thread — while the
        // mutating step only waited for the OPTIMISTIC DOM update (the .ref-current text, the row
        // detaching). A single read can therefore observe the extent before that server write +
        // GC has committed (Expected 1 found 0 after a create; Expected 0 found 1 after a remove).
        // Polling gates the assertion on the authoritative outcome actually settling — the store is
        // the single source of truth — and returns the instant it does. (No timer, no budget raise.)
        await Polling.EventuallyAsync(
            () => ExtentCount(typeName) == count,
            $"the extent '{typeName}' to settle at {count} object(s)");
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
}
