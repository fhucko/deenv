using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for UiCustomization.feature — the shop app (DeEnv/shop.app) driven through a
// real browser: a type view replaces the Customer page, a path view owns /dashboard,
// and the remaining URLs stay the generic auto-form. A code page loads the /ui-js
// bundle and mounts into #app; a generic page loads /js and renders #node-form.
[Binding]
public sealed class UiCustomizationSteps(InstanceContext ctx)
{
    [Given("the shop app is running")]
    public async Task GivenShopAppRunning()
    {
        ctx.Description = InstanceContext.ViewsUiDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    [When("I open {string}")]
    public async Task WhenOpen(string path)
    {
        await ctx.Page!.GotoAsync(path);
        await ctx.Page.WaitForSelectorAsync("body");
    }

    // ── page kind ────────────────────────────────────────────────────────────────

    [Then("the page is a code page")]
    public async Task ThenCodePage()
    {
        // The code bundle is referenced and the client mounts into #app.
        await ctx.Page!.WaitForSelectorAsync("#app [data-key]");
        var html = await ctx.Page.ContentAsync();
        await Assert.That(html).Contains("/ui-js");
    }

    [Then("the page is a generic auto-form")]
    public async Task ThenGenericPage()
    {
        await ctx.Page!.WaitForSelectorAsync("#node-form");
        var html = await ctx.Page.ContentAsync();
        await Assert.That(html).Contains("/js");
        await Assert.That(html.Contains("/ui-js")).IsFalse();
    }

    [Then("the page shows {string}")]
    public async Task ThenShowsSelector(string selector) =>
        await ctx.Page!.WaitForSelectorAsync(selector);

    [Then("the page shows the breadcrumbs")]
    public async Task ThenBreadcrumbs() =>
        await ctx.Page!.WaitForSelectorAsync("nav.breadcrumbs");

    // ── type-view content ────────────────────────────────────────────────────────

    [Then("the page shows the open order {string}")]
    public async Task ThenOpenOrder(string item) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.open-order')].some(e => e.textContent === {JsString(item)})");

    [Then("the page does not show the open order {string}")]
    public async Task ThenNoOpenOrder(string item) =>
        await Assert.That((await ctx.Page!.Locator(".open-order").AllInnerTextsAsync())).DoesNotContain(item);

    // ── path-view content ────────────────────────────────────────────────────────

    [Then("the active customer {string} is listed")]
    public async Task ThenActiveListed(string name) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.active-customer')].some(e => e.textContent === {JsString(name)})");

    [Then("the active customer {string} is not listed")]
    public async Task ThenActiveNotListed(string name) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => ![...document.querySelectorAll('.active-customer')].some(e => e.textContent === {JsString(name)})");

    // ── interaction ──────────────────────────────────────────────────────────────

    [When("I set the email to {string}")]
    public async Task WhenSetEmail(string email) =>
        await ctx.Page!.Locator("input.email").FillAsync(email);

    [When("I uncheck active")]
    public async Task WhenUncheckActive()
    {
        await ctx.Page!.Locator("input.active").UncheckAsync();
        // Let the optimistic write reach the store before navigating away.
        await EventuallyAsync(() => ctx.Store!.ReadExtent("Customer").Values
            .Any(o => o.Fields.TryGetValue("active", out var v) && v is BoolValue { Value: false }));
    }

    // "the store eventually has a <Type> whose <field> is <value>" is shared with
    // TodoSteps — reused, not redefined.

    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    // Polls a store condition (the WS round-trip is async); IOExceptions from a
    // concurrent file write are transient and retried.
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
