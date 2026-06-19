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
        // The object Db's single bool field defaults to false; no explicit seed needed.
        ctx.Description = InstanceContext.BoolDb();
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    // ── When ──────────────────────────────────────────────────────────────────

    // Matches: When I navigate to the root URL "/"
    [When(@"I navigate to the root URL {string}")]
    public async Task WhenNavigateToRootAsync(string path)
    {
        await EnsureServerAndBrowserAsync();
        // GotoReadyAsync waits for the client to hydrate (data-hydrated), so the checkbox handler is
        // attached before we click — the deterministic replacement for the old fixed 800ms sleep. (A
        // click before the WS opens is fine: the client queues sends and flushes them on open, see ws.ts.)
        await ctx.Page!.GotoReadyAsync(ctx.BaseUrl + path);
    }

    // The single bool field renders inside the generic ObjectForm, which now STAGES scalar edits
    // (autosave off by default) — so clicking the checkbox toggles the staged draft (the DOM updates),
    // and persistence happens on Save (the shared "I save the form" step). This step just clicks.
    [When("I click the checkbox")]
    public async Task WhenClickCheckboxAsync() =>
        await ctx.Page!.Locator("input[type='checkbox']").ClickAsync();

    // The staged toggle has reached the sovereign store (Save's WS op flushed) — awaited before a
    // reload so the SSR re-read sees the committed value (replaces a fixed sleep).
    [Then("the root bool eventually persists as checked")]
    public async Task ThenRootBoolPersists() =>
        await Polling.EventuallyAsync(
            () => ctx.Store!.ReadNode(NodePath.Root.Field("ready")) is BoolValue { Value: true },
            "the checkbox toggle to persist");

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

        // A fresh isolated page on the shared browser (launched once for the whole run; see SharedBrowser).
        ctx.Page ??= await SharedBrowser.NewPageAsync(ctx.BaseUrl);
    }
}
