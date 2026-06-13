using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Shared UI-testing steps used across features (navigation + page-kind assertions).
// A code page loads the /ui-js bundle and mounts into #app; a generic (C#) page loads
// /js and renders #node-form.
[Binding]
public sealed class CommonUiSteps(InstanceContext ctx)
{
    [When("I open {string}")]
    public async Task WhenOpen(string path)
    {
        await ctx.Page!.GotoAsync(path);
        await ctx.Page.WaitForSelectorAsync("body");
    }

    [Then("the page is a code page")]
    public async Task ThenCodePage()
    {
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
}
