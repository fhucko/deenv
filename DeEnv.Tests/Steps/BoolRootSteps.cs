using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

[Binding]
public sealed class BoolRootSteps(InstanceContext ctx)
{
    // ── Given ─────────────────────────────────────────────────────────────────

    [Given("an instance whose Db is a bool with value false")]
    public void GivenBoolFalse()
    {
        ctx.Description = InstanceContext.BoolDb();
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        ctx.Store.WriteLeaf(NodePath.Root, new BoolValue(false));
    }

    // ── When ──────────────────────────────────────────────────────────────────

    // Matches: When I navigate to the root URL "/"
    [When(@"I navigate to the root URL {string}")]
    public async Task WhenNavigateToRootAsync(string path)
    {
        await EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl + path);
        // Wait for JS to load and the WebSocket 'open' event to fire (attachHandlers runs then).
        await ctx.Page!.WaitForTimeoutAsync(800);
    }

    [When("I click the checkbox")]
    public async Task WhenClickCheckboxAsync()
    {
        await ctx.Page!.Locator("input[type='checkbox']").ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(500); // allow WS write to reach server and persist
    }

    [When("I reload")]
    public async Task WhenReloadAsync()
    {
        await ctx.Page!.ReloadAsync();
    }

    // ── Then ──────────────────────────────────────────────────────────────────

    [Then("I see a single checkbox")]
    public async Task ThenSingleCheckboxAsync()
    {
        var checkboxes = await ctx.Page!.Locator("input[type='checkbox']").AllAsync();
        await Assert.That(checkboxes.Count).IsEqualTo(1);
    }

    // Shared by Instance.feature (HTTP-only, no browser) and BoolRootInstance.feature (browser).
    [Then("the checkbox is unchecked")]
    public async Task ThenUncheckedAsync()
    {
        if (ctx.Page != null)
        {
            await Assert.That(await ctx.Page.Locator("input[type='checkbox']").IsCheckedAsync()).IsFalse();
        }
        else
        {
            using var http = new HttpClient();
            var html = await http.GetStringAsync(ctx.BaseUrl + "/");
            await Assert.That(html).Contains("input type=\"checkbox\"");
            await Assert.That(html).DoesNotContain(" checked");
        }
    }

    [Then("the checkbox is checked")]
    public async Task ThenCheckedAsync()
    {
        await Assert.That(await ctx.Page!.Locator("input[type='checkbox']").IsCheckedAsync()).IsTrue();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task EnsureServerAndBrowserAsync()
    {
        if (ctx.Server == null)
        {
            ctx.Server = new TestInstanceServer();
            await ctx.Server.StartAsync(ctx.Description!, ctx.DataFilePath);
            ctx.Store = ctx.Server.Store;
        }

        if (ctx.Browser == null)
        {
            ctx.Playwright = await Playwright.CreateAsync();
            ctx.Browser = await ctx.Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            ctx.Page = await ctx.Browser.NewPageAsync(new BrowserNewPageOptions { BaseURL = ctx.BaseUrl });
        }
    }
}
