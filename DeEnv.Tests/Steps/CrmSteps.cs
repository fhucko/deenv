using System.Globalization;
using System.Text.RegularExpressions;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

[Binding]
public sealed class CrmSteps(InstanceContext ctx)
{
    // ── Given (instance + seed data) ───────────────────────────────────────────

    [Given("a CRM instance")]
    public void GivenCrmInstance()
    {
        ctx.Description = InstanceContext.CrmDb();
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    [Given(@"a customer {string} with name {string} email {string} active {word}")]
    public void GivenCustomer(string key, string name, string email, string active)
    {
        ctx.Store!.WriteDictionaryEntry(
            NodePath.Root.Field("customers"),
            new IntValue(int.Parse(key)),
            new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["name"]   = new TextValue(name),
                ["email"]  = new TextValue(email),
                ["active"] = new BoolValue(bool.Parse(active))
            }));
    }

    [Given(@"an order {string} of customer {string} with total {string}")]
    public void GivenOrder(string orderKey, string customerKey, string total)
    {
        var orders = NodePath.Root.Field("customers").Key(customerKey).Field("orders");
        ctx.Store!.WriteDictionaryEntry(
            orders,
            new IntValue(int.Parse(orderKey)),
            new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["date"]    = new DateValue(DateOnly.FromDateTime(DateTime.Today)),
                ["total"]   = new DecimalValue(decimal.Parse(total, CultureInfo.InvariantCulture)),
                ["shipped"] = new BoolValue(false)
            }));
    }

    [Given(@"a setting {string} with value {string}")]
    public void GivenSetting(string key, string value)
    {
        ctx.Store!.WriteDictionaryEntry(
            NodePath.Root.Field("settings"), new TextValue(key), new TextValue(value));
    }

    // ── When (edit + save) ─────────────────────────────────────────────────────

    [When(@"I set the {string} field to {string}")]
    public async Task WhenSetFieldAsync(string field, string value)
    {
        await ctx.Page!.Locator($"input[data-field='{field}']").FillAsync(value);
    }

    [When("I save")]
    public async Task WhenSaveAsync()
    {
        // [data-wired] ensures the WS is open and the submit handler is attached.
        await ctx.Page!.Locator("form#node-form[data-wired] button[type='submit']").ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(500); // let the WS write reach the server
    }

    // ── When (create entry) ────────────────────────────────────────────────────

    [When("I click New")]
    public async Task WhenClickNewAsync()
    {
        await ctx.Page!.Locator("button[data-newentry][data-wired]").First.ClickAsync();
        await ctx.Page.Locator("form.create-form").WaitForAsync();
    }

    [When(@"I set the create field {string} to {string}")]
    public async Task WhenSetCreateFieldAsync(string name, string value)
    {
        await ctx.Page!.Locator($"form.create-form input[name='{name}']").FillAsync(value);
    }

    [When(@"I set the create key to {string}")]
    public async Task WhenSetCreateKeyAsync(string key)
    {
        await ctx.Page!.Locator("form.create-form input[name='__key']").FillAsync(key);
    }

    [When(@"I set the create value to {string}")]
    public async Task WhenSetCreateValueAsync(string value)
    {
        await ctx.Page!.Locator("form.create-form input[name='__value']").FillAsync(value);
    }

    [When("I save the create form")]
    public async Task WhenSaveCreateFormAsync()
    {
        await ctx.Page!.Locator("form.create-form button[data-save]").ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(700); // reload to the list on success, or error shown
    }

    [When("I save and open the create form")]
    public async Task WhenSaveOpenCreateFormAsync()
    {
        await ctx.Page!.Locator("form.create-form button[data-saveopen]").ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(700); // navigates to the new entry on success
    }

    [When("I cancel the create form")]
    public async Task WhenCancelCreateFormAsync()
    {
        await ctx.Page!.Locator("form.create-form button[data-cancel]").ClickAsync();
    }

    [When(@"I delete the row for key {string}")]
    public async Task WhenDeleteRowAsync(string key)
    {
        await ctx.Page!.Locator($"button[data-delentry][data-wired][data-key='{key}']").ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(600); // removeEntry + reload
    }

    // ── Then ───────────────────────────────────────────────────────────────────

    [Then(@"the URL matches {string}")]
    public async Task ThenUrlMatchesAsync(string pattern)
    {
        await Assert.That(Regex.IsMatch(ctx.Page!.Url, pattern)).IsTrue();
    }

    [Then(@"the table has {int} rows")]
    public async Task ThenTableRowsAsync(int count)
    {
        await Assert.That(await ctx.Page!.Locator("table tbody tr").CountAsync()).IsEqualTo(count);
    }

    [Then("I see a create error")]
    public async Task ThenCreateErrorAsync()
    {
        await Assert.That(await ctx.Page!.Locator("form.create-form p.error").IsVisibleAsync()).IsTrue();
    }
}
