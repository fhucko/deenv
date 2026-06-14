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
    // Maps scenario key ("1") → assigned identity, so navigation steps can
    // build the correct URL (/customers/<id>) without hardcoding ids.
    private readonly Dictionary<string, int> _customerIds = new();
    private readonly Dictionary<string, int> _orderIds    = new();

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
        var id = ctx.Store!.CreateObject("Customer", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]   = new TextValue(name),
            ["email"]  = new TextValue(email),
            ["active"] = new BoolValue(bool.Parse(active))
        }));
        ctx.Store!.AddToSet(NodePath.Root.Field("customers"), id);
        _customerIds[key] = id;
    }

    [Given(@"an order {string} of customer {string} with total {string}")]
    public void GivenOrder(string orderKey, string customerKey, string total)
    {
        var ordersPath = NodePath.Root.Field("customers").Key(_customerIds[customerKey].ToString()).Field("orders");
        var id = ctx.Store!.CreateObject("Order", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["date"]    = new DateValue(DateOnly.FromDateTime(DateTime.Today)),
            ["total"]   = new DecimalValue(decimal.Parse(total, CultureInfo.InvariantCulture)),
            ["shipped"] = new BoolValue(false)
        }));
        ctx.Store!.AddToSet(ordersPath, id);
        _orderIds[orderKey] = id;
    }

    [Given(@"a setting {string} with value {string}")]
    public void GivenSetting(string key, string value)
    {
        ctx.Store!.WriteDictionaryEntry(
            NodePath.Root.Field("settings"), new TextValue(key), new TextValue(value));
    }

    // ── When (navigate to seeded customer/order by scenario key) ──────────────

    [When(@"I navigate to the customer {string}")]
    public async Task WhenNavigateToCustomerAsync(string key)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl + "/customers/" + _customerIds[key]);
    }

    [When(@"I navigate to the order {string} of customer {string}")]
    public async Task WhenNavigateToOrderAsync(string orderKey, string customerKey)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl
            + "/customers/" + _customerIds[customerKey]
            + "/orders/" + _orderIds[orderKey]);
    }

    // ── When (edit + save) ─────────────────────────────────────────────────────

    [When(@"I set the {string} field to {string}")]
    public async Task WhenSetFieldAsync(string field, string value)
    {
        // Self-hosted forms class inputs by prop name (input.name) and autosave each edit;
        // the retiring C# auto-form keyed them with data-field and committed on Save.
        var selfHosted = ctx.Page!.Locator($"input.{field}");
        var input = await selfHosted.CountAsync() > 0 ? selfHosted.First
            : ctx.Page!.Locator($"input[data-field='{field}']").First;
        await input.FillAsync(value);
    }

    [When("I save")]
    public async Task WhenSaveAsync()
    {
        // The self-hosted UI autosaves each edit (no Save button) — here we just wait for the
        // WS write to flush. The retiring C# auto-form commits on its Save button; click it
        // when present. [data-wired] ensures the WS is open and the handler attached.
        var saveButton = ctx.Page!.Locator("form#node-form[data-wired] button[type='submit']");
        if (await saveButton.CountAsync() > 0) await saveButton.ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(500); // let the WS write reach the server
    }

    // ── When (create entry) ────────────────────────────────────────────────────

    [When("I click New")]
    public async Task WhenClickNewAsync()
    {
        // Wait until the page settles into either UI: the self-hosted inline add form, or
        // the C# auto-form's (wired) New button.
        await ctx.Page!.Locator(".set-new, .dict-new, button[data-newentry][data-wired]")
            .First.WaitForAsync();
        // The retiring C# auto-form reveals its create form behind a New button. The
        // self-hosted set/dict tables show their add form (.set-new/.dict-new) inline —
        // nothing to click, so this is a no-op there.
        var newButton = ctx.Page.Locator("button[data-newentry][data-wired]");
        if (await newButton.CountAsync() > 0)
        {
            await newButton.First.ClickAsync();
            await ctx.Page.Locator("form.create-form").WaitForAsync();
        }
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
    public async Task ThenCreateErrorAsync() =>
        // The self-hosted dict table shows a non-empty .dict-error when an add is rejected
        // (e.g. a duplicate key). The reactive re-render fills it after the Add click.
        await ctx.Page!.WaitForFunctionAsync(
            "() => (document.querySelector('.dict-error')?.textContent ?? '').trim().length > 0");
}
