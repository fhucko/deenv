using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// DataConflict.feature (M13 slice 6 — field-level conflicts). Two families, mirroring ConcurrencySteps
// (distinct step wording so the two features' Reqnroll bindings never collide):
//   • WsHandler-level (in-process, two bound sessions over the SAME store) — proves disjoint auto-merge,
//     the same-field {base, mine, theirs} payload (base straight from the log), multi-object all-or-none,
//     set-add commute, and the no-baseVersion legacy path — all store/wire observables, no browser.
//   • REAL two-browser-session scenarios (Page + Page2, SAME TestInstanceServer/store) — prove the coarse
//     banner, Keep mine / Take theirs, and the custom-render global-banner fallback (the full client
//     round-trip through ws.ts + the generic-UI Code).
[Binding]
public sealed class DataConflictSteps(InstanceContext ctx)
{
    // ── fixtures ──────────────────────────────────────────────────────────────────

    // The conflict fixture DATA is the same Note{title,count} ids 2,3 shape ConcurrencySteps uses (a fresh
    // unique data file, seeded from empty).
    [Given("the conflict fixture app")]
    public void GivenFixture()
    {
        ctx.DataFilePath = Path.Combine(Path.GetTempPath(), "deenv-dataconflict-" + Guid.NewGuid().ToString("N") + ".json");
        ctx.Description = InstanceContext.ConcurrencyFixtureDb();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    [Given("the conflict fixture app is served")]
    public async Task GivenFixtureServed()
    {
        ctx.DataFilePath = Path.Combine(Path.GetTempPath(), "deenv-dataconflict-" + Guid.NewGuid().ToString("N") + ".json");
        ctx.Description = InstanceContext.ConcurrencyFixtureDb();
        ctx.Server = new TestInstanceServer();
        await ctx.Server.StartAsync(ctx.Description, ctx.DataFilePath);
        ctx.Store = ctx.Server.Store;
    }

    [Given("the custom-render conflict fixture app is served")]
    public async Task GivenCustomFixtureServed()
    {
        ctx.DataFilePath = Path.Combine(Path.GetTempPath(), "deenv-customconflict-" + Guid.NewGuid().ToString("N") + ".json");
        ctx.Description = InstanceContext.CustomRenderConflictDb();
        ctx.Server = new TestInstanceServer();
        await ctx.Server.StartAsync(ctx.Description, ctx.DataFilePath);
        ctx.Store = ctx.Server.Store;
    }

    // ── WsHandler-level sessions (in-process) ───────────────────────────────────────

    private WsHandler? _ws1, _ws2;
    private string _clientId1 = "", _clientId2 = "";
    private ClientSessionStore? _sessions;
    private string _reply1 = "", _reply2 = "";
    private int _sharedBase;
    private int _oneBase;

    private (WsHandler Ws, string ClientId) NewBoundSession()
    {
        _sessions ??= new ClientSessionStore();
        var session = _sessions.Create();
        var ws = new WsHandler(ctx.Store!, ctx.Description!, _sessions);
        return (ws, session.Id);
    }

    [Given("two conflict sessions loaded the store at the current version")]
    public void GivenBothLoaded()
    {
        (_ws1, _clientId1) = NewBoundSession();
        (_ws2, _clientId2) = NewBoundSession();
        _sharedBase = ctx.Store!.CurrentVersion;
    }

    [Given("one conflict session loaded the store at the current version")]
    public void GivenOneLoaded()
    {
        (_ws1, _clientId1) = NewBoundSession();
        _oneBase = ctx.Store!.CurrentVersion;
    }

    // ── commits (title / count / batch / set add), each at the loaded (stale) base ─────────

    [When("conflict session 1 commits note {int}'s title to {string} at its base")]
    public void WhenS1Title(int id, string title) => _reply1 = TitleCommit(_ws1!, _clientId1, id, title, _sharedBase);

    [When("conflict session 2 commits note {int}'s title to {string} at its base")]
    public void WhenS2Title(int id, string title) => _reply2 = TitleCommit(_ws2!, _clientId2, id, title, _sharedBase);

    [When("conflict session 1 commits note {int}'s count to {int} at its base")]
    public void WhenS1Count(int id, int count) => _reply1 = CountCommit(_ws1!, _clientId1, id, count, _sharedBase);

    [When("that conflict session commits note {int}'s title to {string} at its base")]
    public void WhenOneTitle(int id, string title) => _reply1 = TitleCommit(_ws1!, _clientId1, id, title, _oneBase);

    [When("another conflict session commits note {int}'s title to {string} with no base version")]
    public void WhenOtherNoBase(int id, string title)
    {
        var (ws, clientId) = NewBoundSession();
        _reply2 = ws.ProcessMessage($$"""
            { "op": "commit", "clientId": "{{clientId}}",
              "edits": [ { "objectId": {{id}}, "prop": "title", "value": { "type": "text", "value": "{{title}}" } } ] }
            """);
    }

    [When("conflict session 2 commits note {int}'s title {string} and note {int}'s title {string} in one batch at its base")]
    public void WhenS2Batch(int idA, string titleA, int idB, string titleB) =>
        _reply2 = _ws2!.ProcessMessage($$"""
            { "op": "commit", "clientId": "{{_clientId2}}", "baseVersion": {{_sharedBase}},
              "edits": [
                { "objectId": {{idA}}, "prop": "title", "value": { "type": "text", "value": "{{titleA}}" } },
                { "objectId": {{idB}}, "prop": "title", "value": { "type": "text", "value": "{{titleB}}" } }
              ] }
            """);

    [When("conflict session 2 re-commits that batch at the current version")]
    public void WhenS2Recommit() =>
        _reply2 = _ws2!.ProcessMessage($$"""
            { "op": "commit", "clientId": "{{_clientId2}}", "baseVersion": {{ctx.Store!.CurrentVersion}},
              "edits": [
                { "objectId": 2, "prop": "title", "value": { "type": "text", "value": "Two's title" } },
                { "objectId": 3, "prop": "title", "value": { "type": "text", "value": "Two's other" } }
              ] }
            """);

    // A set add: mint a new Note and link it into db.notes, the SAME creates+relations shape ws.ts's
    // endCommit sends. Both sessions do this from the same base — the point is they COMMUTE.
    [When("conflict session 1 adds a new note titled {string} to the notes set at its base")]
    public void WhenS1Add(string title) => _reply1 = AddNoteCommit(_ws1!, _clientId1, title, _sharedBase, -101);

    [When("conflict session 2 adds a new note titled {string} to the notes set at its base")]
    public void WhenS2Add(string title) => _reply2 = AddNoteCommit(_ws2!, _clientId2, title, _sharedBase, -102);

    private string AddNoteCommit(WsHandler ws, string clientId, string title, int baseVersion, int tempId)
    {
        var setId = DbNotesSetId();
        return ws.ProcessMessage($$"""
            { "op": "commit", "clientId": "{{clientId}}", "baseVersion": {{baseVersion}},
              "edits": [],
              "creates": [ { "tempId": {{tempId}}, "value": { "props": { "title": { "type": "text", "value": "{{title}}" } } } } ],
              "relations": [ { "kind": "setAdd", "setId": {{setId}}, "childId": {{tempId}} } ] }
            """);
    }

    private int DbNotesSetId()
    {
        var db = ctx.Store!.ReadById(1)!.Value.Fields;
        return ((SetValue)db.Fields["notes"]).Id;
    }

    private static string TitleCommit(WsHandler ws, string clientId, int id, string title, int baseVersion) =>
        ws.ProcessMessage($$"""
            { "op": "commit", "clientId": "{{clientId}}", "baseVersion": {{baseVersion}},
              "edits": [ { "objectId": {{id}}, "prop": "title", "value": { "type": "text", "value": "{{title}}" } } ] }
            """);

    private static string CountCommit(WsHandler ws, string clientId, int id, int count, int baseVersion) =>
        ws.ProcessMessage($$"""
            { "op": "commit", "clientId": "{{clientId}}", "baseVersion": {{baseVersion}},
              "edits": [ { "objectId": {{id}}, "prop": "count", "value": { "type": "int", "value": {{count}} } } ] }
            """);

    // ── accept / conflict assertions ────────────────────────────────────────────────

    [Then("conflict session 1's commit is accepted")]
    public async Task ThenS1Accepted() => await AssertAccepted(_reply1);

    [Then("conflict session 2's commit is accepted")]
    public async Task ThenS2Accepted() => await AssertAccepted(_reply2);

    private static async Task AssertAccepted(string reply)
    {
        using var doc = JsonDocument.Parse(reply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsFalse();
    }

    [Then("conflict session 2's commit is rejected as a conflict")]
    public async Task ThenS2Conflict()
    {
        using var doc = JsonDocument.Parse(_reply2);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsTrue();
        await Assert.That(doc.RootElement.TryGetProperty("conflicts", out var conflicts)).IsTrue();
        await Assert.That(conflicts.GetArrayLength() > 0).IsTrue();
    }

    // One conflict field flattened to plain values (base/mine/theirs read as strings — the fixtures conflict
    // on text fields). Materialized WHILE the JsonDocument is alive, so no ObjectDisposedException later.
    private sealed record Conflict(int Object, string TypeName, string Field, string? Base, string? Mine, string? Theirs);

    private static List<Conflict> Conflicts(string reply)
    {
        using var doc = JsonDocument.Parse(reply);
        return doc.RootElement.GetProperty("conflicts").EnumerateArray().Select(c => new Conflict(
            c.GetProperty("object").GetInt32(),
            c.GetProperty("typeName").GetString() ?? "",
            c.GetProperty("field").GetString() ?? "",
            Str(c, "base"), Str(c, "mine"), Str(c, "theirs"))).ToList();
    }

    // A conflict value as a string (the fixtures conflict on text fields), or null when absent/null.
    private static string? Str(JsonElement c, string prop) =>
        c.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [Then("the conflict payload names field {string} on note {int}")]
    public async Task ThenConflictNamesField(string field, int objectId) =>
        await Assert.That(Conflicts(_reply2).Any(c => c.Field == field && c.Object == objectId)).IsTrue();

    [Then("the conflict payload does not name note {int}")]
    public async Task ThenConflictOmitsNote(int objectId) =>
        await Assert.That(Conflicts(_reply2).Any(c => c.Object == objectId)).IsFalse();

    [Then("the conflict's base is {string}")]
    public async Task ThenConflictBase(string expected) =>
        await Assert.That(Conflicts(_reply2)[0].Base).IsEqualTo(expected);

    [Then("the conflict's mine is {string}")]
    public async Task ThenConflictMine(string expected) =>
        await Assert.That(Conflicts(_reply2)[0].Mine).IsEqualTo(expected);

    [Then("the conflict's theirs is {string}")]
    public async Task ThenConflictTheirs(string expected) =>
        await Assert.That(Conflicts(_reply2)[0].Theirs).IsEqualTo(expected);

    // ── store assertions ────────────────────────────────────────────────────────────

    [Then("conflict note {int}'s stored title is {string}")]
    public async Task ThenStoredTitle(int id, string expected) =>
        await Polling.EventuallyAsync(
            () => ctx.Store!.ReadById(id) is { } hit && hit.Fields.Fields.TryGetValue("title", out var v)
                && v is TextValue { Text: var t } && t == expected,
            $"note {id}'s stored title to become '{expected}'");

    [Then("conflict note {int}'s stored count is {int}")]
    public async Task ThenStoredCount(int id, int expected) =>
        await Polling.EventuallyAsync(
            () => ctx.Store!.ReadById(id) is { } hit && hit.Fields.Fields.TryGetValue("count", out var v)
                && v is IntValue { Value: var n } && n == expected,
            $"note {id}'s stored count to become {expected}");

    [Then("the conflict notes set has {int} members")]
    public async Task ThenNotesSetSize(int expected) =>
        await Polling.EventuallyAsync(
            () => ctx.Store!.ReadNode(NodePath.FromSegments(["notes"])) is SetValue s && s.Members.Count == expected,
            $"the notes set to hold {expected} members");

    // ── (3)/(4)/(7) real two-browser sessions ───────────────────────────────────────

    [Given("conflict session 1 opens the note at {string}")]
    public async Task GivenS1Opens(string path)
    {
        ctx.Page = await SharedBrowser.NewPageAsync(ctx.BaseUrl);
        await ctx.Page.GotoReadyAsync(path);
        await ctx.Page.WaitReadyAsync();
    }

    [Given("conflict session 2 opens the note at {string}")]
    public async Task GivenS2Opens(string path)
    {
        ctx.Page2 = await SharedBrowser.NewPageAsync(ctx.BaseUrl);
        await ctx.Page2.GotoReadyAsync(path);
        await ctx.Page2.WaitReadyAsync();
    }

    [Given("conflict session 1 opens the custom note page")]
    public async Task GivenS1OpensCustom()
    {
        ctx.Page = await SharedBrowser.NewPageAsync(ctx.BaseUrl);
        await ctx.Page.GotoReadyAsync("/");
        await ctx.Page.WaitReadyAsync();
    }

    [Given("conflict session 2 opens the custom note page")]
    public async Task GivenS2OpensCustom()
    {
        ctx.Page2 = await SharedBrowser.NewPageAsync(ctx.BaseUrl);
        await ctx.Page2.GotoReadyAsync("/");
        await ctx.Page2.WaitReadyAsync();
    }

    [When("conflict session 1 saves the title {string}")]
    public async Task WhenS1BrowserSave(string title) => await FillTitleAndSave(ctx.Page!, "input.title", ".object-form button.save", title);

    [When("conflict session 2 saves the title {string}")]
    public async Task WhenS2BrowserSave(string title) => await FillTitleAndSave(ctx.Page2!, "input.title", ".object-form button.save", title);

    // B5 per-field: edit BOTH note fields (title text + count int) in one form and Save, so the commit
    // stages two edits to ONE object — the two-field collision the fine bar renders as two rows in a group.
    [When("conflict session 1 saves note {int}'s title {string} and count {int}")]
    public async Task WhenS1SaveTwoFields(int id, string title, int count) => await FillTwoAndSave(ctx.Page!, title, count);

    [When("conflict session 2 saves note {int}'s title {string} and count {int}")]
    public async Task WhenS2SaveTwoFields(int id, string title, int count) => await FillTwoAndSave(ctx.Page2!, title, count);

    private static async Task FillTwoAndSave(IPage page, string title, int count)
    {
        await page.WaitReadyAsync();
        await page.Locator("input.title").FillAsync(title);
        await page.Locator("input.count").FillAsync(count.ToString());
        await page.Locator(".object-form button.save").First.ClickAsync();
    }

    // B5 per-field resolution: find the .conflict-field-row whose humanized field name matches (case-
    // insensitively — the bar humanizes "title" → "Title"), then click its per-field Keep-mine / Take-theirs
    // button. The row is disambiguated by object group in the DOM; matching on the field name is sufficient
    // for the single-object fixture (two distinct field rows).
    [When("conflict session 2 takes theirs for field {string}")]
    public async Task WhenS2TakeTheirsField(string field) => await ClickFieldButton(field, "button.conflict-field-take");

    [When("conflict session 2 keeps mine for field {string}")]
    public async Task WhenS2KeepMineField(string field) => await ClickFieldButton(field, "button.conflict-field-keep");

    private async Task ClickFieldButton(string field, string buttonSel)
    {
        var row = ctx.Page2!.Locator(".conflict-field-row")
            .Filter(new LocatorFilterOptions { Has = ctx.Page2.Locator(".conflict-field-name", new PageLocatorOptions { HasTextString = Humanize(field) }) });
        await row.Locator(buttonSel).ClickAsync();
    }

    // The label humanization the bar applies (first letter upper) — matches sys.humanize for a single word.
    private static string Humanize(string field) => field.Length == 0 ? field : char.ToUpper(field[0]) + field[1..];

    // The custom-render fixture composes <ObjectForm>, so its title input + Save are the generic selectors;
    // the wrapping .custom-note render is what makes it a custom page (scenario 7 asserts the GLOBAL banner).
    [When("conflict session 1 saves the custom title {string}")]
    public async Task WhenS1CustomSave(string title) => await FillTitleAndSave(ctx.Page!, "input.title", ".object-form button.save", title);

    [When("conflict session 2 saves the custom title {string}")]
    public async Task WhenS2CustomSave(string title) => await FillTitleAndSave(ctx.Page2!, "input.title", ".object-form button.save", title);

    private static async Task FillTitleAndSave(IPage page, string inputSel, string saveSel, string title)
    {
        await page.WaitReadyAsync();
        await page.Locator(inputSel).FillAsync(title);
        await page.Locator(saveSel).First.ClickAsync();
    }

    [Then("conflict session 1's save lands in the store")]
    public async Task ThenS1SaveAccepted() =>
        await Polling.EventuallyAsync(
            () => ctx.Store!.ReadById(2) is { } hit && hit.Fields.Fields.TryGetValue("title", out var v)
                && v is TextValue { Text: var t } && (t == "Session 1 wins" || t == "Session 1 first"),
            "session 1's save to land in the store");

    [Then("conflict session 1's custom save lands in the store")]
    public async Task ThenS1CustomSaveAccepted() =>
        await Polling.EventuallyAsync(
            () => ctx.Store!.ReadById(2) is { } hit && hit.Fields.Fields.TryGetValue("title", out var v)
                && v is TextValue { Text: "Custom one" },
            "session 1's custom save to land in the store");

    [Then("conflict session 2 sees the conflict banner naming {string}")]
    public async Task ThenS2SeesBanner(string field)
    {
        await ctx.Page2!.Locator(".conflict-bar").WaitForAsync();
        var text = await ctx.Page2.Locator(".conflict-bar").InnerTextAsync();
        // The banner humanizes the field label ("title" → "Title"); match case-insensitively.
        await Assert.That(text.Contains(field, StringComparison.OrdinalIgnoreCase)).IsTrue();
        await Shot("banner");
    }

    // B5: the fine bar shows THEIRS (the landed value) inline before the operator picks — the headline
    // obligation. Asserted as its own DOM node (.conflict-theirs) so a regression that renders only the
    // field name (the coarse behavior) is caught.
    [Then("conflict session 2's conflict bar shows theirs value {string}")]
    public async Task ThenS2BarShowsTheirs(string expected) =>
        await ctx.Page2!.Locator(".conflict-theirs", new PageLocatorOptions { HasTextString = expected })
            .WaitForAsync();

    [Then("conflict session 2's conflict bar shows mine value {string}")]
    public async Task ThenS2BarShowsMine(string expected) =>
        await ctx.Page2!.Locator(".conflict-mine", new PageLocatorOptions { HasTextString = expected })
            .WaitForAsync();

    // B5 disambiguation: the collisions are grouped BY OBJECT under a labeled header (typeName + " #" + id),
    // so two objects render as two distinguishable groups (not a flat "field, field" list). The single-object
    // fixture produces one group; asserting its label carries the object identity proves the grouping/labeling
    // mechanism that makes multiple objects distinguishable.
    [Then("conflict session 2's conflict bar group is labeled for note {int}")]
    public async Task ThenS2GroupLabeled(int id) =>
        await ctx.Page2!.Locator(".conflict-group-label", new PageLocatorOptions { HasTextString = "Note #" + id })
            .WaitForAsync();

    [When("conflict session 2 clicks Take theirs")]
    public async Task WhenS2TakeTheirs() => await ctx.Page2!.Locator("button.conflict-take").ClickAsync();

    [When("conflict session 2 clicks Keep mine")]
    public async Task WhenS2KeepMine()
    {
        await ctx.Page2!.Locator("button.conflict-keep").ClickAsync();
        // Wait for the force re-commit to resolve (banner clears) before the store assertion + shot.
        await ctx.Page2.Locator(".conflict-bar").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
        await Shot("keep-mine");
    }

    [Then("conflict session 2's title field shows {string}")]
    public async Task ThenS2TitleShows(string expected)
    {
        var input = ctx.Page2!.Locator("input.title");
        await input.WaitForAsync();
        await Assert.That(await input.InputValueAsync()).IsEqualTo(expected);
        await Shot("take-theirs");
    }

    [Then("conflict session 2's conflict banner is gone")]
    public async Task ThenS2BannerGone() =>
        await ctx.Page2!.Locator(".conflict-bar").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });

    // Three-lens review fix 1: the GLOBAL banner (#__error) is a separate DOM element from the coarse
    // .conflict-bar (ThenS2BannerGone above) — both must clear on resolution, or the rejection notice
    // ("your edits were NOT saved… reload") outlives the state it described (actively wrong after Keep
    // mine forces the overwrite). Asserted as its OWN step so a regression that clears only one is caught.
    [Then("conflict session 2's global error banner is gone")]
    public async Task ThenS2GlobalErrorBannerGone() =>
        await ctx.Page2!.Locator("#__error").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });

    // Three-lens review fix 4b: Take theirs' transient confirmation (symmetric to Keep-mine's implicit
    // "Saved" via the commit-ack lifecycle), surfaced through the SAME save-status span as ctx.status.
    [Then("conflict session 2 sees the {string} confirmation")]
    public async Task ThenS2SeesConfirmation(string text) =>
        await ctx.Page2!.Locator(".save-status", new PageLocatorOptions { HasTextString = text })
            .WaitForAsync();

    // Three-lens review fix 2: while ctx.conflicts is non-empty the form-actions Save/Discard row (which
    // includes button.save) must not render at all — the bar's two buttons ARE the complete decision set,
    // so a plain Save can never become a hidden force-overwrite of theirs. Asserted via the DOM count
    // (0 = truly absent, not just visually hidden) rather than a visibility check.
    [Then("conflict session 2's Save button is hidden while the bar shows")]
    public async Task ThenS2SaveButtonHidden()
    {
        await Assert.That(await ctx.Page2!.Locator(".conflict-bar").CountAsync()).IsGreaterThan(0); // the bar IS up
        await Assert.That(await ctx.Page2.Locator(".object-form button.save").CountAsync()).IsEqualTo(0);
    }

    [Then("conflict session 2's Save button is visible again")]
    public async Task ThenS2SaveButtonVisible() =>
        await ctx.Page2!.Locator(".object-form button.save").WaitForAsync();

    // Capture a screenshot of session 2's page for the UI-verification artifact — gated on DEENV_SHOTS so it
    // is OFF in normal runs (no cost/files) and ON only for the mandatory screenshot capture pass. Two
    // scenarios (3 and 4) share the SAME "banner" name (both assert the banner appears via ThenS2SeesBanner)
    // and TUnit runs them in parallel, so a brief write-write race on the same path is expected under the
    // capture pass — retried like the store's own SaveRaw transient-conflict rides (a rare µs-scale window,
    // never a real test failure since this is screenshot tooling only, never an assertion).
    private async Task Shot(string name)
    {
        var dir = Environment.GetEnvironmentVariable("DEENV_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        for (var attempt = 0; ; attempt++)
        {
            try { await ctx.Page2!.ScreenshotAsync(new PageScreenshotOptions { Path = path }); return; }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(100);
            }
        }
    }

    // (The old "conflict session 2 shows the {string} error banner" + "...says the edits were not saved and
    // to reload" steps were removed with the B5 banner fast-follow: a conflict now surfaces the in-form
    // <ConflictBar> instead of the global reload banner. If the no-render FALLBACK branch ever gets a
    // deterministic test, it wants fresh assertions on #__error — see versioning-slices.md slice 13(a).)
}
