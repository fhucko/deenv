using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Shared UI-testing steps used across features (navigation + page-kind assertions).
// A self-hosted code page mounts into #app and bootstraps the client bundle (/js) from
// the infra port — the page carries window.initInfraPort.
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
        await Assert.That(html).Contains("initInfraPort");
    }

    [Then("the page shows {string}")]
    public async Task ThenShowsSelector(string selector) =>
        await ctx.Page!.WaitForSelectorAsync(selector);
}
