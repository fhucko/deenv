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

// Concurrency.feature — the optimistic-concurrency anti-clobber guard (baseVersion). Two families:
//
//   (a) a REAL two-browser-session scenario (Page + Page2, both against the SAME TestInstanceServer, so
//       both hit the SAME in-process store) — the only way to prove the FULL client round-trip: the
//       client remembers a version, a ctx captures it as its base, a stale commit is REJECTED and shows
//       the existing global error banner.
//   (b)/(c) driven directly at the WsHandler level (in-process, mirrors AtomicCommitSteps) — object-
//       granularity and same-session-sequential-saves are store/wire observables, no browser needed.
[Binding]
public sealed class ConcurrencySteps(InstanceContext ctx)
{
    // ── fixture ──────────────────────────────────────────────────────────────────

    [Given("the concurrency fixture app is served")]
    public async Task GivenFixtureServed()
    {
        // A fresh, unique data file: this fixture declares its own (Db/Note-only) schema, so it must
        // seed from EMPTY — never inherit a sibling scenario's recycled temp file (the same discipline
        // ClientDataLayerSteps uses for its own fixtures).
        ctx.DataFilePath = Path.Combine(Path.GetTempPath(), "deenv-concurrency-" + Guid.NewGuid().ToString("N") + ".json");
        ctx.Description = InstanceContext.ConcurrencyFixtureDb();
        ctx.Server = new TestInstanceServer();
        await ctx.Server.StartAsync(ctx.Description, ctx.DataFilePath);
        ctx.Store = ctx.Server.Store;
    }

    // The WsHandler-level scenarios don't need a running HTTP/WS server — just the store + a bound
    // WsHandler per session (mirrors AtomicCommitSteps' BoundWs pattern).
    [Given("the concurrency fixture app")]
    public void GivenFixture()
    {
        ctx.DataFilePath = Path.Combine(Path.GetTempPath(), "deenv-concurrency-" + Guid.NewGuid().ToString("N") + ".json");
        ctx.Description = InstanceContext.ConcurrencyFixtureDb();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    // ── (a) two real browser sessions ───────────────────────────────────────────

    [Given("session 1 opens the note at {string}")]
    public async Task GivenSession1Opens(string path)
    {
        ctx.Page = await SharedBrowser.NewPageAsync(ctx.BaseUrl);
        await ctx.Page.GotoReadyAsync(path);
        await ctx.Page.WaitReadyAsync(); // the Save commits over the WS — wait for the settled socket
    }

    // A SECOND, fully independent browser session (its own Playwright context — no shared cookies/WS/DOM
    // with session 1's Page) against the SAME server, so it observes the SAME store session 1 writes to.
    [Given("session 2 opens the note at {string}")]
    public async Task GivenSession2Opens(string path)
    {
        ctx.Page2 = await SharedBrowser.NewPageAsync(ctx.BaseUrl);
        await ctx.Page2.GotoReadyAsync(path);
        await ctx.Page2.WaitReadyAsync();
    }

    [When("session 1 changes the title to {string} and saves")]
    public async Task WhenSession1ChangesAndSaves(string title) => await FillTitleAndSaveAsync(ctx.Page!, title);

    [When("session 2 changes the title to {string} and saves")]
    public async Task WhenSession2ChangesAndSaves(string title) => await FillTitleAndSaveAsync(ctx.Page2!, title);

    private static async Task FillTitleAndSaveAsync(IPage page, string title)
    {
        await page.WaitReadyAsync();
        await page.Locator("input.title").FillAsync(title);
        await page.Locator(".object-form button.save").First.ClickAsync();
    }

    // Session 1's save is the FIRST commit against fresh data — nothing can be stale under it, so it
    // must land: poll the STORE (not the DOM) for the applied value, the same discriminator the store-
    // level scenarios use, so this step doesn't depend on session 1's OWN banner/status UI.
    [Then("session 1's save is accepted")]
    public async Task ThenSession1Accepted() =>
        await Polling.EventuallyAsync(
            () => ctx.Store!.ReadById(2) is { } hit && hit.Fields.Fields.TryGetValue("title", out var v)
                && v is TextValue { Text: var t } && t.StartsWith("From session 1"),
            "session 1's save to land in the store");

    // Session 2's save is REJECTED: poll for the global error banner (ui.ts refreshErrorBanner) to
    // appear on session 2's OWN page — proof the client-side reject path (rollbackJournal → the banner)
    // fired, not just that the store happens to still hold session 1's value.
    [Then("session 2's save is rejected")]
    public async Task ThenSession2Rejected() =>
        await ctx.Page2!.Locator("#__error").WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

    [Then("session 2 shows the {string} error banner")]
    public async Task ThenSession2ShowsBanner(string expectedSubstring)
    {
        var text = await ctx.Page2!.Locator("#__error").InnerTextAsync();
        await Assert.That(text.Contains(expectedSubstring)).IsTrue();
    }

    // ── (b)/(c) WsHandler-level, two (or one, twice) sessions ───────────────────

    private WsHandler? _ws1, _ws2;
    private string _clientId1 = "", _clientId2 = "";
    private ClientSessionStore? _sessions;
    private string _reply1 = "", _reply2 = "";
    // Scenario (b)'s shared base: captured ONCE when both sessions "load," then used for BOTH commits —
    // reading CurrentVersion fresh at each commit's send time would let session 1's already-landed commit
    // silently hand session 2 a NEWER base than it actually "loaded" at, which would still pass (session 2
    // was never stale for note 3 either way) but would no longer be testing what the scenario text claims
    // (the SAME original base, not a moving target).
    private int _sharedBaseVersion;
    // Scenario (c)'s single session: the base its FIRST commit used, then the version its ack reported —
    // so the second commit can be sent "using the version its first commit landed at" without the test
    // needing to parse the reply twice inline in the step text.
    private int _oneSessionBaseVersion;
    private int _oneSessionLandedVersion;

    private (WsHandler Ws, string ClientId) NewBoundSession()
    {
        _sessions ??= new ClientSessionStore();
        var session = _sessions.Create();
        var ws = new WsHandler(ctx.Store!, ctx.Description!, _sessions);
        return (ws, session.Id);
    }

    // Both sessions "loaded the store at the current version" — the version EVERY object in this fresh
    // fixture is unchanged-since-boot at, i.e. CurrentVersion right now (0, nothing has mutated yet).
    // Reading it explicitly (not hard-coding 0) keeps the step correct if the fixture setup itself ever
    // starts performing a mutation before this point. Captured ONCE, shared by both commits (see the
    // field's doc) — this is the ONE version both sessions "saw."
    [Given("both sessions loaded the store at the current version")]
    public void GivenBothSessionsLoaded()
    {
        (_ws1, _clientId1) = NewBoundSession();
        (_ws2, _clientId2) = NewBoundSession();
        _sharedBaseVersion = ctx.Store!.CurrentVersion;
    }

    [Given("a session loaded the store at the current version")]
    public void GivenOneSessionLoaded()
    {
        (_ws1, _clientId1) = NewBoundSession();
        _oneSessionBaseVersion = ctx.Store!.CurrentVersion;
    }

    [When("session 1 commits note {int}'s title to {string}")]
    public void WhenSession1Commits(int noteId, string title) =>
        _reply1 = SendTitleCommit(_ws1!, _clientId1, noteId, title, _sharedBaseVersion);

    [When("session 2 commits note {int}'s title to {string}")]
    public void WhenSession2Commits(int noteId, string title) =>
        _reply2 = SendTitleCommit(_ws2!, _clientId2, noteId, title, _sharedBaseVersion);

    [When("that session commits note {int}'s title to {string} using its loaded base")]
    public void WhenOneSessionFirstCommit(int noteId, string title)
    {
        _reply1 = SendTitleCommit(_ws1!, _clientId1, noteId, title, _oneSessionBaseVersion);
        _oneSessionLandedVersion = ExtractNewVersion(_reply1);
    }

    [When("that session commits note {int}'s count to {int} using the version its first commit landed at")]
    public void WhenOneSessionSecondCommit(int noteId, int count)
    {
        var (ws, clientId) = (_ws1!, _clientId1);
        _reply1 = ws.ProcessMessage($$"""
            {
              "op": "commit",
              "clientId": "{{clientId}}",
              "baseVersion": {{_oneSessionLandedVersion}},
              "edits": [
                { "objectId": {{noteId}}, "prop": "count", "value": { "type": "int", "value": {{count}} } }
              ]
            }
            """);
    }

    private static string SendTitleCommit(WsHandler ws, string clientId, int noteId, string title, int baseVersion) =>
        ws.ProcessMessage($$"""
            {
              "op": "commit",
              "clientId": "{{clientId}}",
              "baseVersion": {{baseVersion}},
              "edits": [
                { "objectId": {{noteId}}, "prop": "title", "value": { "type": "text", "value": "{{title}}" } }
              ]
            }
            """);

    private static int ExtractNewVersion(string reply)
    {
        using var doc = JsonDocument.Parse(reply);
        return doc.RootElement.GetProperty("newVersion").GetInt32();
    }

    [Then("both commits are accepted")]
    public async Task ThenBothAccepted()
    {
        await AssertAccepted(_reply1);
        await AssertAccepted(_reply2);
    }

    [Then("that commit is accepted")]
    public async Task ThenOneAccepted() => await AssertAccepted(_reply1);

    [Then("that commit is also accepted")]
    public async Task ThenOneAlsoAccepted() => await AssertAccepted(_reply1);

    private static async Task AssertAccepted(string reply)
    {
        using var doc = JsonDocument.Parse(reply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsFalse();
    }

    // ── store assertions (shared by (a)/(b)/(c)) ────────────────────────────────

    [Then("the stored note {int} title is {string}")]
    public async Task ThenStoredNoteTitleIs(int id, string expected) =>
        await Polling.EventuallyAsync(
            () => ctx.Store!.ReadById(id) is { } hit && hit.Fields.Fields.TryGetValue("title", out var v)
                && v is TextValue { Text: var t } && t == expected,
            $"note {id}'s stored title to become '{expected}'");

    [Then("the stored note {int} count is {int}")]
    public async Task ThenStoredNoteCountIs(int id, int expected) =>
        await Polling.EventuallyAsync(
            () => ctx.Store!.ReadById(id) is { } hit && hit.Fields.Fields.TryGetValue("count", out var v)
                && v is IntValue { Value: var n } && n == expected,
            $"note {id}'s stored count to become {expected}");
}
