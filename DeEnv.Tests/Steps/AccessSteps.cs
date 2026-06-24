using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// M-auth slice 1 — the access read floor. Driven in-process at the SsrRenderer level (like the
// SelfHostedUi render scenarios): the observable is WHAT IS IN THE SHIPPED STATE — the full rendered
// document, body PLUS the window.initData island the client receives. A denied object never enters the
// `db` graph, so its title is absent from BOTH. The principal is bound floor-first (the harness sets
// ctx.PrincipalUserId; the render step passes it into Render) — no password login this slice.
[Binding]
public sealed class AccessSteps(InstanceContext ctx)
{
    // ── Background ──────────────────────────────────────────────────────────────

    // The devlog-shaped fixture: a Milestone set + a User with a `role` enum, carrying the type-level
    // read rule. The store seeds the admin/member users + the "Gate #3" milestone from initialData.
    [Given(@"an app whose Db holds a set of {string} and a {string} with a {string} enum \(Admin, Member\)")]
    public void GivenAccessApp(string memberType, string userType, string roleProp)
    {
        ctx.Description = InstanceContext.AccessFixtureDb();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    // Install an access rule so it is in EFFECT — the ruleset is what activates enforcement. The rule text
    // is a full line "<Type> <verbs> [where <cond>]"; this slice's rules are all `Milestone` rules, so the
    // leading "Milestone " is stripped and the rest accumulated as a rule line, then the fixture is rebuilt
    // from ALL accumulated lines (read from the Background + a write scenario's verb rule) so every declared
    // rule is active at once. The rule is PARSED by AppParse exactly as the app's own would be (not
    // hand-built), then asserted present. The store is rebuilt over the SAME seed (identical types ⇒ the
    // data still fits) so the seeded milestone/users remain.
    [Given("the access rule {string}")]
    public async Task GivenAccessRule(string rule)
    {
        // The feature writes inner quotes as `\"` (the Gherkin escape for a literal `"` inside the quoted
        // argument); Reqnroll passes the backslashes through, so unescape them before the rule is parsed.
        rule = rule.Replace("\\\"", "\"");
        const string prefix = "Milestone ";
        await Assert.That(rule.StartsWith(prefix)).IsTrue();
        ctx.AccessRuleLines.Add(rule[prefix.Length..]);

        ctx.Description = InstanceContext.AccessFixtureWithRules(ctx.AccessRuleLines.ToArray());
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);

        await Assert.That(ctx.Description!.Rules).IsNotNull();
        await Assert.That(ctx.Description!.Rules!.Any(r => r.Type == "Milestone" && r.Verbs.Contains("read")))
            .IsTrue();
    }

    // The admin (Ada) and member (Bob) are seeded by the fixture's initialData; assert both are present.
    [Given("a seeded admin user and a seeded member user")]
    public async Task GivenSeededUsers()
    {
        await Assert.That(ctx.Store!.ReadById(InstanceContext.AccessAdminId)).IsNotNull();
        await Assert.That(ctx.Store!.ReadById(InstanceContext.AccessMemberId)).IsNotNull();
    }

    // The "Gate #3" milestone is seeded by the fixture's initialData; this names it for the scenarios.
    [Given("one seeded {string} titled {string}")]
    public void GivenSeededMilestone(string type, string title)
    {
        // Seeded via initialData on store construction — nothing to do here; the assertions read it back.
    }

    // Drop the `access` section: the SAME shape with no rules proves the dormant (allow-all) path. Rebuilt
    // over the same data (identical types ⇒ the seeded data still fits) so the milestone is present to load.
    [Given("the app has no access rules")]
    public async Task GivenNoAccessRules()
    {
        ctx.Description = InstanceContext.AccessFixtureDb(withAccessRule: false);
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        await Assert.That(ctx.Description!.Rules is null || ctx.Description!.Rules!.Count == 0).IsTrue();
    }

    // ── the principal (floor-first harness bind) ────────────────────────────────

    [Given("the current user is the admin")]
    public void GivenCurrentUserAdmin() => ctx.PrincipalUserId = InstanceContext.AccessAdminId;

    [Given("the current user is the member")]
    public void GivenCurrentUserMember() => ctx.PrincipalUserId = InstanceContext.AccessMemberId;

    [Given("there is no current user")]
    public void GivenNoCurrentUser() => ctx.PrincipalUserId = null;

    // ── When ────────────────────────────────────────────────────────────────────

    // Render at the given path with the bound principal — the floor gates what enters the shipped graph.
    [When("the page state is rendered for {string}")]
    public void WhenPageStateRendered(string path)
    {
        var renderer = new SsrRenderer(ctx.Store!, ctx.Description!);
        ctx.RenderedHtml = renderer.Render(path, principalUserId: ctx.PrincipalUserId).Html;
    }

    // ── Then ────────────────────────────────────────────────────────────────────

    // The WHOLE shipped document (body + the window.initData island) must carry the title — proving the
    // object loaded into the graph the client receives.
    [Then("the shipped data includes a {string} titled {string}")]
    public async Task ThenShippedIncludes(string type, string title)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(ctx.RenderedHtml!.Contains(title)).IsTrue();
    }

    // The title must be absent from the WHOLE document — neither displayed nor leaked through initData —
    // proving the denied object never entered the shipped graph.
    [Then("the shipped data includes no {string}")]
    public async Task ThenShippedExcludes(string type)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(ctx.RenderedHtml!.Contains("Gate #3")).IsFalse();
    }

    // The render produced a real page, not the SSR error fallback (the anonymous condition fails CLOSED,
    // it does not throw the render down).
    [Then("the render does not error")]
    public async Task ThenRenderDoesNotError()
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(ctx.RenderedHtml!.Contains("<h1>Error</h1>")).IsFalse();
    }

    // ── write enforcement (the mutation seam, driven at WsHandler) ──────────────
    //
    // The write floor lives in WsHandler (server-side), so these steps drive it directly — exactly like
    // the enum off-list write scenario (SchemaSteps). The principal is bound floor-first on the WS SESSION:
    // a ClientSessionStore mints a session, the test sets its PrincipalUserId, and the WS message carries
    // its clientId (the same seam the password-login slice will SET; no login handshake here). The
    // observable is the STORE after the attempted mutation — applied for an allowed write, unchanged for a
    // denied one (the client's rollback restores the optimistic change, but the SERVER simply never wrote).

    private ClientSessionStore? _sessions;
    private string _writeReply = "";

    // Build a WsHandler over the fixture's store, with a session bound to the currently-chosen principal
    // (ctx.PrincipalUserId). The clientId threads into each op so the handler resolves the principal.
    private (WsHandler Ws, string ClientId) BoundWs()
    {
        _sessions ??= new ClientSessionStore();
        var session = _sessions.Create();
        session.PrincipalUserId = ctx.PrincipalUserId;
        var ws = new WsHandler(ctx.Store!, ctx.Description!, _sessions);
        return (ws, session.Id);
    }

    // edit: an objectPropChange on the seeded Milestone (addressed by its intrinsic id).
    [When("the {word} edits the {string} titled {string} to set {string} to {string}")]
    public void WhenEditsMilestone(string _who, string _type, string _title, string prop, string value)
    {
        var (ws, clientId) = BoundWs();
        _writeReply = ws.ProcessMessage(
            $$"""{ "op": "objectPropChange", "clientId": "{{clientId}}", "objectId": {{InstanceContext.AccessMilestoneId}}, "prop": "{{prop}}", "value": { "type": "text", "value": "{{value}}" } }""");
    }

    // create: an arrayAdd minting a new Milestone into the Db.milestones set.
    [When("the current user adds a {string} titled {string} to the milestones set")]
    public void WhenAddsMilestone(string _type, string title)
    {
        var (ws, clientId) = BoundWs();
        var setId = MilestonesSetId();
        _writeReply = ws.ProcessMessage(
            $$"""{ "op": "arrayAdd", "clientId": "{{clientId}}", "setId": {{setId}}, "typeName": "Milestone", "value": { "props": { "title": { "type": "text", "value": "{{title}}" } } } }""");
    }

    // delete: an arrayRemove dropping the seeded Milestone from the Db.milestones set.
    [When("the current user removes {string} {int} from the milestones set")]
    public void WhenRemovesMilestone(string _type, int memberId)
    {
        var (ws, clientId) = BoundWs();
        var setId = MilestonesSetId();
        _writeReply = ws.ProcessMessage(
            $$"""{ "op": "arrayRemove", "clientId": "{{clientId}}", "setId": {{setId}}, "objectId": {{memberId}} }""");
    }

    // The intrinsic id of the Db.milestones set (the seeded root's set) — needed to address arrayAdd/remove.
    private int MilestonesSetId()
    {
        var set = (SetValue)((ObjectValue)ctx.Store!.ReadNode(NodePath.Root)!).Fields["milestones"];
        return set.Id;
    }

    [Then("the mutation is accepted")]
    public async Task ThenMutationAccepted()
    {
        using var doc = JsonDocument.Parse(_writeReply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsFalse();
    }

    [Then("the mutation is rejected")]
    public async Task ThenMutationRejected()
    {
        using var doc = JsonDocument.Parse(_writeReply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsTrue();
    }

    // ── store assertions (the authoritative outcome) ────────────────────────────

    [Then("the stored {string} {int} has {string} equal to {string}")]
    public async Task ThenStoredFieldEquals(string type, int id, string prop, string expected)
    {
        var obj = ctx.Store!.ReadById(id);
        await Assert.That(obj).IsNotNull();
        await Assert.That(obj!.Value.Fields.Fields[prop]).IsEqualTo((NodeValue)new TextValue(expected));
    }

    [Then("the milestones set contains a {string} titled {string}")]
    public async Task ThenSetContainsTitled(string type, string title) =>
        await Assert.That(MilestoneTitles().Contains(title)).IsTrue();

    [Then("the milestones set contains no {string} titled {string}")]
    public async Task ThenSetExcludesTitled(string type, string title) =>
        await Assert.That(MilestoneTitles().Contains(title)).IsFalse();

    [Then("the milestones set contains no {string} {int}")]
    public async Task ThenSetExcludesId(string type, int id) =>
        await Assert.That(MilestoneMemberIds().Contains(id)).IsFalse();

    [Then("the milestones set still contains {string} {int}")]
    public async Task ThenSetStillContainsId(string type, int id) =>
        await Assert.That(MilestoneMemberIds().Contains(id)).IsTrue();

    // The titles of every Milestone in the store's extent (a created member lands here).
    private List<string> MilestoneTitles() =>
        ctx.Store!.ReadExtent("Milestone").Values
            .Select(o => o.Fields.TryGetValue("title", out var t) && t is TextValue tv ? tv.Text : "")
            .ToList();

    // The member ids of the Db.milestones set (a removed member is dropped from it). SetValue.Members is
    // keyed by member id, so its Keys ARE the member ids.
    private List<int> MilestoneMemberIds()
    {
        var set = (SetValue)((ObjectValue)ctx.Store!.ReadNode(NodePath.Root)!).Fields["milestones"];
        return set.Members.Keys.ToList();
    }
}
