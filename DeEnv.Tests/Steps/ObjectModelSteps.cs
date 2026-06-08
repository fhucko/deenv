using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for the Milestone 5 first slice (ObjectModel.feature).
//
// Seeding and introspection probe the target on-disk shape directly (see
// DECISIONS.md): a per-type extent of identity-enveloped objects, plus a root
// whose set/reference fields hold references (ids) into the extents:
//
//   {
//     "extents": { "Person": { "7": { "id": 7, "fields": { "name": "Ada" } } } },
//     "root":    { "id": 1, "fields": { "people": { "7": { "ref": 7 } },
//                                       "lead":   { "ref": 7 } } }
//   }
//
// Mutations during a scenario (create / pick / remove) go through the running
// app (UI + WebSocket), so they exercise the real identity/extent/GC behavior;
// only the assertions read the file the server wrote.
[Binding]
public sealed class ObjectModelSteps(InstanceContext ctx)
{
    // Person ids assigned at seed time, so a scenario can still name an object
    // after the app has collected it (its id is gone from the extent).
    private readonly Dictionary<string, int> _idByName = new();

    // ── Given ───────────────────────────────────────────────────────────────────

    [Given("an object-graph instance")]
    public void GivenObjectGraphInstance()
    {
        ctx.Description = InstanceContext.ObjectGraphDb();
        ctx.DataFilePath = Path.GetTempFileName();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    [Given(@"a person {string} in the extent referenced by the set {string}")]
    public void GivenPersonInSet(string name, string setName)
    {
        var doc = Doc();
        var id = MintPerson(doc, name);
        AddToSet(doc, setName, id);
        Save(doc);
    }

    [Given(@"the single reference {string} references the person {string}")]
    public void GivenSingleReference(string refName, string name)
    {
        var doc = Doc();
        var id = _idByName.TryGetValue(name, out var known) ? known : FindPersonId(doc, name);
        SetReference(doc, refName, id);
        Save(doc);
    }

    // ── When ────────────────────────────────────────────────────────────────────

    [When(@"I create a new {string} named {string} in the set")]
    public async Task WhenCreateNewInSetAsync(string typeName, string name)
    {
        await ctx.Page!.Locator("button[data-newentry][data-wired]").First.ClickAsync();
        await ctx.Page.Locator("form.create-form").WaitForAsync();
        await ctx.Page.Locator("form.create-form [data-mode='new']").ClickAsync();
        await ctx.Page.Locator("form.create-form input[name='name']").FillAsync(name);
        await ctx.Page.Locator("form.create-form button[data-saveopen]").ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(700);
    }

    [When(@"I create a new {string} named {string} through the reference")]
    public async Task WhenCreateNewThroughReferenceAsync(string typeName, string name)
    {
        // [data-wired] ensures the toggle's click handler is attached before we use it.
        await ctx.Page!.Locator("[data-ref] [data-mode='new'][data-wired]").ClickAsync();
        await ctx.Page.Locator("[data-ref] .ref-new input[name='name']").FillAsync(name);
    }

    [When(@"I pick the existing {string} named {string}")]
    public async Task WhenPickExistingAsync(string typeName, string name)
    {
        await ctx.Page!.Locator("[data-ref] [data-mode='existing'][data-wired]").ClickAsync();
        await ctx.Page.Locator("[data-ref] select[data-pick]")
                      .SelectOptionAsync(new Microsoft.Playwright.SelectOptionValue { Label = name });
    }

    [When(@"I open the id-route for {string}")]
    public async Task WhenOpenIdRouteAsync(string name)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl + "/~/" + IdOf(name));
    }

    [When(@"I remove {string} from the set {string}")]
    public async Task WhenRemoveFromSetAsync(string name, string setName)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl + "/" + setName);
        var id = IdOf(name).ToString();
        await ctx.Page!.Locator($"button[data-delentry][data-wired][data-key='{id}']").ClickAsync();
        await ctx.Page.WaitForTimeoutAsync(700); // unlink + GC + reload
    }

    // ── Then ────────────────────────────────────────────────────────────────────

    [Then(@"the set {string} lists {string}")]
    public async Task ThenSetListsAsync(string setName, string name)
    {
        var rows = ctx.Page!.Locator("table tbody tr", new() { HasTextString = name });
        await Assert.That(await rows.CountAsync()).IsGreaterThanOrEqualTo(1);
    }

    [Then(@"following {string} in the set {string} opens the same object both times")]
    public async Task ThenSameObjectBothTimesAsync(string name, string setName)
    {
        var href1 = await MemberHrefAsync(setName, name);
        await Assert.That(Regex.IsMatch(href1, $"/{setName}/\\d+$")).IsTrue();

        // Following it opens the member object (name shows), then re-reading the
        // member's link yields the same identity address — it is one object.
        await ctx.Page!.GotoAsync(ctx.BaseUrl + href1);
        var shown = await ctx.Page.Locator("input[data-path$='/name']").GetAttributeAsync("value") ?? "";
        await Assert.That(shown).IsEqualTo(name);

        await ctx.Page.GotoAsync(ctx.BaseUrl + "/" + setName);
        var href2 = await MemberHrefAsync(setName, name);
        await Assert.That(href2).IsEqualTo(href1);
    }

    private async Task<string> MemberHrefAsync(string setName, string name) =>
        await ctx.Page!.Locator("table tbody tr", new() { HasTextString = name })
                      .First.Locator("a").First.GetAttributeAsync("href") ?? "";

    [Then(@"navigating to {string} shows the {string} field {string}")]
    public async Task ThenNavigatingShowsFieldAsync(string path, string field, string expected)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl + path);
        var value = await ctx.Page.Locator($"input[data-path$='/{field}']").GetAttributeAsync("value") ?? "";
        await Assert.That(value).IsEqualTo(expected);
    }

    [Then(@"the extent {string} has {int} object(s)")]
    public async Task ThenExtentCountAsync(string typeName, int count)
    {
        await Assert.That(ExtentCount(typeName)).IsEqualTo(count);
    }

    [Then(@"the id-route for {string} is not found")]
    public async Task ThenIdRouteNotFoundAsync(string name)
    {
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync(ctx.BaseUrl + "/~/" + IdOf(name));
        await Assert.That(await ctx.Page.ContentAsync()).Contains("Not found");
    }

    // ── target-shape helpers (test-only probe of the on-disk format) ─────────────

    private JsonObject Doc()
    {
        var text = File.Exists(ctx.DataFilePath) ? File.ReadAllText(ctx.DataFilePath) : "";
        if (!string.IsNullOrWhiteSpace(text) && JsonNode.Parse(text) is JsonObject existing
            && existing["extents"] is not null)
            return existing;

        return new JsonObject
        {
            ["extents"] = new JsonObject(),
            ["root"] = new JsonObject
            {
                ["id"] = 1,
                ["fields"] = new JsonObject(),
            },
        };
    }

    private int MintPerson(JsonObject doc, string name)
    {
        var extents = (JsonObject)doc["extents"]!;
        if (extents["Person"] is not JsonObject people)
        {
            people = new JsonObject();
            extents["Person"] = people;
        }

        var id = people.Count == 0 ? 1 : people.Select(kv => int.Parse(kv.Key)).Max() + 1;
        people[id.ToString()] = new JsonObject
        {
            ["id"] = id,
            ["fields"] = new JsonObject { ["name"] = name },
        };
        _idByName[name] = id;
        return id;
    }

    private static void AddToSet(JsonObject doc, string setName, int id)
    {
        var fields = (JsonObject)((JsonObject)doc["root"]!)["fields"]!;
        if (fields[setName] is not JsonObject set)
        {
            set = new JsonObject();
            fields[setName] = set;
        }
        set[id.ToString()] = new JsonObject { ["ref"] = id };
    }

    private static void SetReference(JsonObject doc, string refName, int id)
    {
        var fields = (JsonObject)((JsonObject)doc["root"]!)["fields"]!;
        fields[refName] = new JsonObject { ["ref"] = id };
    }

    private void Save(JsonObject doc) =>
        File.WriteAllText(ctx.DataFilePath, doc.ToJsonString());

    private int ExtentCount(string typeName)
    {
        var doc = Doc();
        return ((JsonObject)doc["extents"]!)[typeName] is JsonObject ext ? ext.Count : 0;
    }

    private int FindPersonId(JsonObject doc, string name)
    {
        if (((JsonObject)doc["extents"]!)["Person"] is JsonObject people)
            foreach (var (key, node) in people)
                if (node?["fields"]?["name"]?.GetValue<string>() == name)
                    return int.Parse(key);
        throw new InvalidOperationException($"No person named '{name}' in the extent.");
    }

    private int IdOf(string name) =>
        _idByName.TryGetValue(name, out var id) ? id : FindPersonId(Doc(), name);
}
