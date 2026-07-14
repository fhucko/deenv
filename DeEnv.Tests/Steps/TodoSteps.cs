using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for the todo app (TodoApp.feature) — the committed default instance
// (DeEnv/instances/2/app.app), rebuilt as the M11 auto-with-overrides showcase: a custom
// `fn render()` (a user selector + per-user list cards + per-card item checklists) that
// COMPOSES the public library `<Input>` primitive for each item's checkbox + text field.
// Driven end-to-end through a real browser: SSR first paint, client hydration, optimistic
// mutations persisted over the WS. An item's text/checked is a library-composed <Input>, so
// the item text is read from input.text's VALUE and its done-state from input.checked.
[Binding]
public sealed class TodoSteps(InstanceContext ctx)
{
    // ── Given ───────────────────────────────────────────────────────────────────

    [Given("the todo app is running")]
    public async Task GivenTodoAppRunning()
    {
        ctx.Description = InstanceContext.TodoDb();
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoContentAsync("/");
        await ctx.Page!.WaitForSelectorAsync("[data-key]"); // hydrated (client adds keys)
        // Every Todo scenario mutates over the WS (add user/list/item). Gate on FULL readiness, not just
        // hydration: a mutation staged before the socket is open + the session claimed rides the connecting-
        // window outbox and can be delayed (or its negative→real id remap deferred) under peak load — which
        // is what left a just-added user's selected view churning (the add-list button detaching mid-click).
        await ctx.Page!.WaitReadyAsync();
    }

    // ── When ────────────────────────────────────────────────────────────────────

    // Selecting a user clicks its chip in the user bar; the selected-user section then renders.
    [When("I select the user {string}")]
    public async Task WhenSelectUser(string name)
    {
        await ctx.Page!.Locator("button.user-chip", new() { HasTextString = name }).First.ClickAsync();
        await ctx.Page.Locator("h2.selected-user", new() { HasTextString = name }).WaitForAsync();
    }

    // Add an item to a specific list's card: fill that card's inline add-item input, click its
    // Add button, then wait until every item row in that card carries a real (positive) data-key
    // — i.e. the optimistic add persisted and its id remap re-rendered the row.
    [When("I add a new item {string} to the list {string}")]
    public async Task WhenAddItem(string text, string list)
    {
        var card = Card(list);
        await card.Locator("input.new-item").FillAsync(text);
        await card.Locator("button.add-item-btn").ClickAsync();
        await AllItemKeysRealAsync(list);
    }

    [When("I add a new user {string}")]
    public async Task WhenAddUser(string name)
    {
        await ctx.Page!.Locator("input.new-user").FillAsync(name);
        await ctx.Page.Locator("button.add-user").ClickAsync();
        // Wait for the new user's chip AND for its negative→real id remap to land (a POSITIVE data-key),
        // not just the chip's appearance. The new user becomes `selectedUser`, so until its id is real the
        // render keeps re-running on each WS reply (chipClass reads sys.id(selectedUser)) — which detaches
        // and rebuilds the selected-user section (its add-list button included). A test that selects this
        // user and clicks "Add list" during that churn window hit "element detached from the DOM, retrying"
        // until timeout under load. Gating on the remap settles the view before the next interaction.
        await ctx.Page.Locator("button.user-chip[data-key]:not([data-key^='-'])", new() { HasTextString = name }).First.WaitForAsync();
    }

    [When("I add a new list {string}")]
    public async Task WhenAddList(string name)
    {
        await ctx.Page!.Locator("input.new-list").FillAsync(name);
        await ctx.Page.Locator("button.add-list-btn").ClickAsync();
        await ctx.Page.Locator("h3.list-name", new() { HasTextString = name }).WaitForAsync();
    }

    // Wait until every item row in the given card has a real (positive) data-key — the optimistic
    // add persisted and the negative→real id remap re-rendered the row.
    private async Task AllItemKeysRealAsync(string list)
    {
        var cardSel = $"article.todo-card:has(h3.list-name:has-text({Quoted(list)}))";
        await ctx.Page!.WaitForFunctionAsync(
            "sel => { const card = document.querySelector(sel); if (!card) return false;" +
            " const rows = [...card.querySelectorAll('.item-row')];" +
            " return rows.length > 0 && rows.every(e => +e.getAttribute('data-key') > 0); }",
            cardSel);
    }

    [When("I remove the item {string}")]
    public async Task WhenRemoveItem(string text) =>
        await ItemRowAsync(text).Locator("button.remove-item").ClickAsync();

    [When("I check the item {string}")]
    public async Task WhenCheckItem(string text) =>
        await ItemRowAsync(text).Locator("input.checked").CheckAsync();

    // ── Then ────────────────────────────────────────────────────────────────────

    [Then("the page shows the user {string}")]
    public async Task ThenShowsUser(string name) =>
        await ctx.Page!.WaitForSelectorAsync($"button.user-chip:has-text({Quoted(name)})");

    [Then("the page shows the selected user {string}")]
    public async Task ThenShowsSelectedUser(string name) =>
        await ctx.Page!.WaitForSelectorAsync($"h2.selected-user:has-text({Quoted(name)})");

    [Then("the page shows the list {string}")]
    public async Task ThenShowsList(string name) =>
        await ctx.Page!.WaitForSelectorAsync($"h3.list-name:has-text({Quoted(name)})");

    // The item text lives in input.text's VALUE (a composed library <Input>), so match on value.
    [Then("the page shows an item {string}")]
    public async Task ThenShowsItem(string text) =>
        await ctx.Page!.Locator($".item-row input.text[value={JsString(text)}]").First.WaitForAsync();

    [Then("the page does not show an item {string}")]
    public async Task ThenDoesNotShowItem(string text) =>
        await ctx.Page!.Locator($".item-row input.text[value={JsString(text)}]").First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

    [Then("the item {string} is checked")]
    public async Task ThenItemChecked(string text) =>
        await Assert.That(await ItemRowAsync(text).Locator("input.checked").IsCheckedAsync()).IsTrue();

    [Then("the store eventually has a {string} whose {string} is {string}")]
    public async Task ThenStoreHasText(string typeName, string field, string expected) =>
        await EventuallyAsync(() => ctx.Store!.ReadExtent(typeName).Values
            .Any(o => o.Fields.TryGetValue(field, out var v) && v is TextValue t && t.Text == expected));

    [Then("the store eventually has a checked {string}")]
    public async Task ThenStoreHasChecked(string typeName) =>
        await EventuallyAsync(() => ctx.Store!.ReadExtent(typeName).Values
            .Any(o => o.Fields.TryGetValue("checked", out var v) && v is BoolValue { Value: true }));

    // ── locators ──────────────────────────────────────────────────────────────────

    // The list card whose title matches (exact-ish via :has-text on the .list-name heading).
    private ILocator Card(string list) =>
        ctx.Page!.Locator("article.todo-card", new() {
            Has = ctx.Page.Locator("h3.list-name", new() { HasTextString = list })
        }).First;

    // The item row whose composed text <Input> (input.text) holds the given VALUE. The text is in
    // the input's value PROPERTY (set by client render), not the attribute, so it can't be matched by
    // a CSS attribute selector — resolve the row's index in JS (waiting for it), then take that nth row.
    private ILocator ItemRowAsync(string text)
    {
        // Use locator filter to find the row containing the input with the matching value.
        // This waits when the locator is used.
        return ctx.Page!.Locator(".item-row")
            .Filter(new() { Has = ctx.Page.Locator($"input.text[value={JsString(text)}]") })
            .First;
    }

    // A name as a quoted :has-text() argument — quotes/backslashes in the value escaped.
    private static string Quoted(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    // Polls a store condition (the WS round-trip is async). An IOException is the test
    // thread reading the store file while the server writes it — transient, retried.
    private static async Task EventuallyAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return; }
            catch (IOException) { /* store file mid-write — retry */ }
            await Task.Delay(50);
        }
        bool final;
        try { final = condition(); } catch (IOException) { final = false; }
        await Assert.That(final).IsTrue();
    }
}
