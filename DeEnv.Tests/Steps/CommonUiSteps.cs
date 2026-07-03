using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Shared UI-testing steps used across features (navigation + page-kind assertions).
// A self-hosted code page mounts into #app and bootstraps the client bundle (/js) from
// the asset host under its mount — the page carries window.initBase (the mount prefix) and
// window.initAssetAuthority (host:port).
[Binding]
public sealed class CommonUiSteps(InstanceContext ctx)
{
    [When("I open {string}")]
    public async Task WhenOpen(string path)
    {
        // "I open" is the entry point for the self-hosted-UI scenarios, which then interact (pick/clear a
        // reference, add a set/dict row, edit a bound field), so wait for hydration here — one gate covers
        // them all. (The read-only Navigation scenarios use "I navigate to", which stays DOMContentLoaded.)
        await ctx.Page!.GotoReadyAsync(path);
        await ctx.Page!.WaitForSelectorAsync("body");
    }

    [Then("the page is a code page")]
    public async Task ThenCodePage()
    {
        // A data-key'd element EXISTS (the page hydrated as self-hosted Code) — `Attached`, not the
        // default `Visible`: the first keyed element may be a hidden one (e.g. a foreach'd <option>
        // inside a <select>), which is still proof the code page rendered.
        await ctx.Page!.WaitForSelectorAsync("#app [data-key]",
            new Microsoft.Playwright.PageWaitForSelectorOptions { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        var html = await ctx.Page.ContentAsync();
        await Assert.That(html).Contains("initBase");
    }

    [Then("the page shows {string}")]
    public async Task ThenShowsSelector(string selector) =>
        await ctx.Page!.WaitForSelectorAsync(selector);

    // The client-twin half of the XSS attribute guards (refreshAttributes in ui.ts): proves the named
    // attribute is genuinely ABSENT from the hydrated DOM, not merely absent from the SSR string.
    [Then("the element {string} has no {string} attribute")]
    public async Task ThenElementHasNoAttribute(string selector, string attribute)
    {
        var el = ctx.Page!.Locator(selector).First;
        await el.WaitForAsync();
        await Assert.That(await el.GetAttributeAsync(attribute)).IsNull();
    }
}
