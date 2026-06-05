using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

[Binding]
public sealed class InstanceSteps(InstanceContext ctx)
{
    [Given("a single-bool instance")]
    [Given("a single-bool instance that has never been edited")]
    public void GivenSingleBoolInstance()
    {
        ctx.Description = InstanceContext.BoolDb();
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    [When("it is started")]
    [When("I open it")]
    public async Task WhenStartedAsync()
    {
        ctx.Server = new TestInstanceServer();
        await ctx.Server.StartAsync(ctx.Description!, ctx.DataFilePath);
        ctx.Store = ctx.Server.Store;
    }

    [Then("the instance is running")]
    public async Task ThenRunningAsync()
    {
        using var http = new HttpClient();
        var resp = await http.GetAsync(ctx.BaseUrl + "/");
        await Assert.That((int)resp.StatusCode).IsEqualTo(200);
    }

    [Then("its checkbox is visible")]
    public async Task ThenCheckboxVisibleAsync()
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(ctx.BaseUrl + "/");
        await Assert.That(html).Contains("input type=\"checkbox\"");
    }

    // "the checkbox is unchecked" is also used in BoolRootInstance.feature (browser mode).
    // This HTTP-only binding covers the Instance.feature scenario ("I open it" → no browser).
    // BoolRootSteps handles the Playwright version for browser scenarios.
    // Reqnroll picks the step in the same class as the preceding When; but since Reqnroll
    // doesn't scope by class, we merge both modes into BoolRootSteps and remove this duplicate.
    // (Step removed — see BoolRootSteps.ThenUncheckedAsync which handles both modes.)
}
