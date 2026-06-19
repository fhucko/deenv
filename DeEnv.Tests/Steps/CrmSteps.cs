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

    // Values typed into fields since the last save — polled for persistence by the "I save" step.
    private readonly List<string> _pendingEditValues = new();

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
        await ctx.Page!.GotoContentAsync(ctx.BaseUrl + "/customers/" + _customerIds[key]);
    }

    [When(@"I navigate to the order {string} of customer {string}")]
    public async Task WhenNavigateToOrderAsync(string orderKey, string customerKey)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoContentAsync(ctx.BaseUrl
            + "/customers/" + _customerIds[customerKey]
            + "/orders/" + _orderIds[orderKey]);
    }

    // ── When (edit + save) ─────────────────────────────────────────────────────

    [When(@"I set the {string} field to {string}")]
    public async Task WhenSetFieldAsync(string field, string value)
    {
        // Self-hosted forms class inputs by prop name (input.name) and autosave each edit;
        // the retiring C# auto-form keyed them with data-field and committed on Save.
        await ctx.Page!.WaitHydratedAsync(); // the bound input's handler must be attached before we type
        var selfHosted = ctx.Page!.Locator($"input.{field}");
        var input = await selfHosted.CountAsync() > 0 ? selfHosted.First
            : ctx.Page!.Locator($"input[data-field='{field}']").First;
        await input.FillAsync(value);
        _pendingEditValues.Add(value);
    }

    [When("I save")]
    public async Task WhenSaveAsync()
    {
        // The self-hosted UI autosaves each edit (no Save button); the retiring C# auto-form commits on
        // its Save button — click it when present ([data-wired] = WS open + handler attached).
        var saveButton = ctx.Page!.Locator("form#node-form[data-wired] button[type='submit']");
        if (await saveButton.CountAsync() > 0) await saveButton.ClickAsync();
        // Poll the persisted store file until every edit has flushed to it — replaces a fixed 500ms guess
        // and, unlike a DOM check, actually proves the autosave reached disk.
        foreach (var value in _pendingEditValues)
            await Polling.EventuallyAsync(
                () => File.ReadAllText(ctx.DataFilePath).Contains(value),
                $"the edit '{value}' to persist");
        _pendingEditValues.Clear();
    }

    // ── When (create entry) ────────────────────────────────────────────────────

    // The self-hosted set/dict create form is flag-gated: clicking `+ New` swaps the table for the
    // labeled create form (hidden until asked).
    [When("I click New")]
    public async Task WhenClickNewAsync() =>
        await ctx.Page!.RevealCreateFormAsync();

    // ── Then ───────────────────────────────────────────────────────────────────

    [Then(@"the URL matches {string}")]
    public async Task ThenUrlMatchesAsync(string pattern)
    {
        await Assert.That(Regex.IsMatch(ctx.Page!.Url, pattern)).IsTrue();
    }

    [Then("I see a create error")]
    public async Task ThenCreateErrorAsync() =>
        // The self-hosted dict table shows a non-empty .dict-error when an add is rejected
        // (e.g. a duplicate key). The reactive re-render fills it after the Add click.
        await ctx.Page!.WaitForFunctionAsync(
            "() => (document.querySelector('.dict-error')?.textContent ?? '').trim().length > 0");
}
