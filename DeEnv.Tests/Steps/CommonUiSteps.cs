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
        await ctx.Page!.GotoAsync(path);
        await ctx.Page.WaitForSelectorAsync("body");
    }

    [Then("the page is a code page")]
    public async Task ThenCodePage()
    {
        await ctx.Page!.WaitForSelectorAsync("#app [data-key]");
        var html = await ctx.Page.ContentAsync();
        await Assert.That(html).Contains("initInfraPort");
    }

    [Then("the page shows {string}")]
    public async Task ThenShowsSelector(string selector) =>
        await ctx.Page!.WaitForSelectorAsync(selector);
}
