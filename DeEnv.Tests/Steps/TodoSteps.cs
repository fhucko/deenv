using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for the todo app (TodoApp.feature) — the committed default instance
// (DeEnv/instance.schema.json) driven end-to-end through a real browser: SSR
// first paint, client hydration, optimistic mutations persisted over the WS,
// and selection-dependent lazy loading via refetch.
[Binding]
public sealed class TodoSteps(InstanceContext ctx)
{
    // ── Given ───────────────────────────────────────────────────────────────────

    [Given("the todo app is running")]
    public async Task GivenTodoAppRunning()
    {
        ctx.Description = InstanceContext.TodoDb();
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync("/");
        await ctx.Page.WaitForSelectorAsync("[data-key]"); // hydrated (client adds keys)
    }

    // ── When ────────────────────────────────────────────────────────────────────

    [When("I select the user {string}")]
    public async Task WhenSelectUser(string name)
    {
        await ctx.Page!.Locator($".user-name:has-text(\"{name}\")").ClickAsync();
        await ctx.Page.WaitForSelectorAsync(".selected-user");
    }

    [When("I select the list {string}")]
    public async Task WhenSelectList(string name)
    {
        await ctx.Page!.Locator($".list-name:has-text(\"{name}\")").ClickAsync();
        await ctx.Page.WaitForSelectorAsync(".selected-list");
    }

    [When("I add a new item {string}")]
    public async Task WhenAddItem(string text)
    {
        await ctx.Page!.Locator("input.new-item").FillAsync(text);
        await ctx.Page.Locator("button.add-item").ClickAsync();
        await AllKeysRealAsync(".item-row"); // persisted + remapped (no mid-step re-render)
    }

    [When("I add a new user {string}")]
    public async Task WhenAddUser(string name)
    {
        await ctx.Page!.Locator("input.new-user").FillAsync(name);
        await ctx.Page.Locator("button.add-user").ClickAsync();
        await AllKeysRealAsync(".user-row");
    }

    // Wait until every row of the selector carries a real (positive) data-key — i.e.
    // the optimistic add has persisted and its id remap re-rendered the row.
    private async Task AllKeysRealAsync(string selector) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('{selector}')].length > 0 && " +
            $"[...document.querySelectorAll('{selector}')].every(e => +e.getAttribute('data-key') > 0)");

    [When("I remove the user {string}")]
    public async Task WhenRemoveUser(string name)
    {
        await ctx.Page!.Locator($".user-row:has-text(\"{name}\") .remove-user").ClickAsync();
    }

    [When("I check the first item")]
    public async Task WhenCheckFirstItem()
    {
        await ctx.Page!.Locator("input.item-check").First.CheckAsync();
    }

    [When("I open the about page")]
    public async Task WhenOpenAbout() => await ctx.Page!.Locator("button.nav-about").ClickAsync();

    [When("I open the users page")]
    public async Task WhenOpenUsers() => await ctx.Page!.Locator("button.nav-users").ClickAsync();

    // ── Then ────────────────────────────────────────────────────────────────────

    [Then("the page shows the user {string}")]
    public async Task ThenShowsUser(string name) =>
        await ctx.Page!.WaitForSelectorAsync($".user-name:has-text(\"{name}\")");

    [Then("the page does not show the user {string}")]
    public async Task ThenDoesNotShowUser(string name) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => ![...document.querySelectorAll('.user-name')].some(e => e.textContent === '{name}')");

    [Then("the page shows the list {string}")]
    public async Task ThenShowsList(string name) =>
        await ctx.Page!.WaitForSelectorAsync($".list-name:has-text(\"{name}\")");

    [Then("the page shows an item {string}")]
    public async Task ThenShowsItem(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('input.item-text')].some(e => e.value === '{text}')");

    [Then("the page shows the done item {string}")]
    public async Task ThenShowsDoneItem(string text) =>
        await ctx.Page!.WaitForSelectorAsync($".item-done:has-text(\"{text}\")");

    [Then("the page shows the about text")]
    public async Task ThenShowsAbout() =>
        await ctx.Page!.WaitForSelectorAsync(".about");

    [Then("the store eventually has a {string} whose {string} is {string}")]
    public async Task ThenStoreHasText(string typeName, string field, string expected) =>
        await EventuallyAsync(() => ctx.Store!.ReadExtent(typeName).Values
            .Any(o => o.Fields.TryGetValue(field, out var v) && v is TextValue t && t.Text == expected));

    [Then("the store eventually has a checked {string}")]
    public async Task ThenStoreHasChecked(string typeName) =>
        await EventuallyAsync(() => ctx.Store!.ReadExtent(typeName).Values
            .Any(o => o.Fields.TryGetValue("checked", out var v) && v is BoolValue { Value: true }));

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
