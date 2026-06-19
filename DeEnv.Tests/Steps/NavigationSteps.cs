using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

[Binding]
public sealed class NavigationSteps(InstanceContext ctx)
{
    // ── Background ────────────────────────────────────────────────────────────

    // Matches: Given an instance whose Db is an object "Shop"
    [Given(@"an instance whose Db is an object {string}")]
    public void GivenShopDb(string _)
    {
        ctx.Description = InstanceContext.ShopDb();
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    // Matches: And Shop has a field "customers" that is a dictionary of Customer
    [Given(@"Shop has a field {string} that is a dictionary of Customer")]
    public void GivenCustomersDictionary(string _) { /* defined in ShopDb description */ }

    // Matches: And Customer is an object with fields "name" (text) and "active" (bool)
    [Given(@"Customer is an object with fields {string} \(text\) and {string} \(bool\)")]
    public void GivenCustomerFields(string _1, string _2) { /* defined in ShopDb description */ }

    // Matches: And customers contains key "42" with name "Acme" and active true
    [Given(@"customers contains key {string} with name {string} and active true")]
    public void GivenCustomerEntry(string key, string name)
    {
        ctx.Store!.WriteDictionaryEntry(
            NodePath.Root.Field("customers"),
            new TextValue(key),
            new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["name"]   = new TextValue(name),
                ["active"] = new BoolValue(true)
            }));
    }

    // Matches: Given customers has no key "99"
    [Given(@"customers has no key {string}")]
    public void GivenNoKey(string key)
    {
        ctx.Store!.RemoveDictionaryEntry(NodePath.Root.Field("customers"), new TextValue(key));
    }

    // ── When ──────────────────────────────────────────────────────────────────

    // Matches: When I navigate to "/customers/42"
    [When(@"I navigate to {string}")]
    public async Task WhenNavigateToAsync(string path)
    {
        await EnsureServerAndBrowserAsync();
        // This step is the entry point for read-only checks (Navigation.feature) AND for scenarios that
        // go on to interact — add a dict/set entry, edit a field (Entries/ObjectModel). So wait for
        // hydration: a click/fill before the client hydrates silently no-ops. The cost to the read-only
        // callers is one hydration wait (cheap; the marker is set right after the first client render).
        await ctx.Page!.GotoReadyAsync(ctx.BaseUrl + path);
    }

    // Matches: When I click the row for key "42" in the customers table
    // The self-hosted dictTable links each row's key cell to the nested entry URL via the
    // stretched row anchor (a.row-link); the retiring C# table used tr[data-nav].
    [When(@"I click the row for key {string} in the customers table")]
    public async Task WhenClickRowAsync(string key)
    {
        await ctx.Page!.Locator($"table a:text-is('{key}')").First.ClickAsync();
        await ctx.Page.WaitForUrlContentAsync(new System.Text.RegularExpressions.Regex($".*/customers/{key}$"));
    }

    // ── Then ──────────────────────────────────────────────────────────────────

    // Matches: Then I see a form for "Shop"
    // The self-hosted object page is a div.object-form with an <h2> heading; the retiring
    // C# auto-form used a <form id="node-form">. Either way the heading is the type name.
    [Then(@"I see a form for {string}")]
    public async Task ThenFormForAsync(string typeName)
    {
        var text = await ctx.Page!.Locator(".object-form h2, form h2").First.InnerTextAsync();
        await Assert.That(text).IsEqualTo(typeName);
    }

    // Matches: And the "customers" field renders as a table
    [Then(@"the {string} field renders as a table")]
    public async Task ThenFieldIsTableAsync(string _)
    {
        var count = await ctx.Page!.Locator(".field table").CountAsync();
        await Assert.That(count).IsGreaterThanOrEqualTo(1);
    }

    // Matches: And the "name" field shows "Acme"
    [Then(@"the {string} field shows {string}")]
    public async Task ThenFieldShowsAsync(string fieldName, string expected)
    {
        var input = await FieldInputAsync(fieldName);
        var value = await input.GetAttributeAsync("value") ?? "";
        await Assert.That(value).IsEqualTo(expected);
    }

    // Matches: And the "active" field shows a checked checkbox
    [Then(@"the {string} field shows a checked checkbox")]
    public async Task ThenFieldCheckedAsync(string fieldName)
    {
        var cb = await FieldInputAsync(fieldName);
        await Assert.That(await cb.IsCheckedAsync()).IsTrue();
    }

    // A field input on either UI: the self-hosted form classes inputs by prop name
    // (input.name); the retiring C# auto-form keyed them by path (data-path$='/name').
    private async Task<ILocator> FieldInputAsync(string fieldName)
    {
        var selfHosted = ctx.Page!.Locator($"input.{fieldName}");
        return await selfHosted.CountAsync() > 0
            ? selfHosted.First
            : ctx.Page!.Locator($"input[data-path$='/{fieldName}']").First;
    }

    // Matches: Then the URL is "/customers/42"
    [Then(@"the URL is {string}")]
    public async Task ThenUrlIsAsync(string expectedPath)
    {
        await Assert.That(ctx.Page!.Url).Contains(expectedPath);
    }

    // Matches: Then the breadcrumbs read "Db / customers / 42"
    [Then(@"the breadcrumbs read {string}")]
    public async Task ThenBreadcrumbsReadAsync(string expected)
    {
        var text = await ctx.Page!.Locator("nav.breadcrumbs").InnerTextAsync();
        var normalized = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        await Assert.That(normalized).IsEqualTo(expected);
    }

    [Then("I see a not-found view")]
    public async Task ThenNotFoundAsync()
    {
        await Assert.That(await ctx.Page!.ContentAsync()).Contains("Not found");
    }

    // Matches: And the breadcrumbs let me return to "/customers"
    [Then(@"the breadcrumbs let me return to {string}")]
    public async Task ThenBreadcrumbsReturnAsync(string path)
    {
        var count = await ctx.Page!.Locator($"nav.breadcrumbs a[href='{path}']").CountAsync();
        await Assert.That(count).IsGreaterThanOrEqualTo(1);
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
