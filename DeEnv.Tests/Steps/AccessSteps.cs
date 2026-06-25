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

        // Rebuild from BOTH accumulators (Milestone here + any User rule a login-1b scenario added), so the
        // two rule steps are order-independent and every declared rule stays active at once.
        ctx.Description = InstanceContext.AccessFixtureWithRules(
            ctx.AccessRuleLines.ToArray(), ctx.UserAccessRuleLines.ToArray());
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

    // ── floor-hardening (the three review-found bypasses) ───────────────────────

    // Fix 1 — sys.extent gating. Swap in the fixture whose custom `fn render()` lists the Milestone extent
    // via `sys.extent("Milestone")` (the ref-picker seam), carrying the `Milestone read` rule. The store is
    // rebuilt over the same seed so the seeded milestone/users remain. The render then surfaces each
    // candidate in a `.extent-row`, so the listing assertions read whether the denied row leaked.
    [Given("an app that lists the Milestone extent via sys.extent in a custom render")]
    public async Task GivenExtentListingApp()
    {
        ctx.Description = InstanceContext.AccessExtentFixtureDb();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        await Assert.That(ctx.Description!.Rules!.Any(r => r.Type == "Milestone" && r.Verbs.Contains("read")))
            .IsTrue();
    }

    // Client data layer, slice 1a (component-state seed). Swap in the fixture whose whole UI is one
    // stateful root component `<panel>` that reveals the (admin-ruled) milestone rows only when its
    // `state.open` is true, carrying the `Milestone read` rule. The store is rebuilt over the same seed so
    // the seeded milestone/users remain. With the panel's slot unseeded the rows never render → "Gate #3"
    // is not harvested; seeding its slot open reproduces the client's open popup and ships the data.
    [Given("the access-seed app whose panel reveals the milestones only when its slot is open")]
    public async Task GivenSeedPanelApp()
    {
        ctx.Description = InstanceContext.AccessSeedFixtureDb();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        await Assert.That(ctx.Description!.Rules!.Any(r => r.Type == "Milestone" && r.Verbs.Contains("read")))
            .IsTrue();
    }

    // The extent listing is the custom render's ONLY content (just the .extent-row list), so a row's title
    // appears in the rendered document iff it survived the read floor into the candidate list. Present →
    // the admin saw the candidate; absent → the denied member/anonymous reader did not.
    [Then("the extent listing includes a row titled {string}")]
    public async Task ThenExtentIncludes(string title)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(ctx.RenderedHtml!.Contains(title)).IsTrue();
    }

    [Then("the extent listing includes no row titled {string}")]
    public async Task ThenExtentExcludes(string title)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(ctx.RenderedHtml!.Contains(title)).IsFalse();
    }

    // Fix 3 — a throwing condition must DENY, not crash the render. Swap in the fixture whose ONLY rule's
    // condition divides by zero, rebuilt over the same seed. The render step then proves the floor catches
    // the DivideByZeroException (fail closed) rather than letting it crash the SSR render.
    [Given("the only access rule's condition divides by zero")]
    public async Task GivenDivZeroRule()
    {
        ctx.Description = InstanceContext.AccessDivZeroFixtureDb();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
        await Assert.That(ctx.Description!.Rules!.Single(r => r.Type == "Milestone").When).IsNotNull();
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
    // `ctx.Seed` (client data layer, slice 1a) reproduces a component's client view-state when a scenario
    // set it (else null = the unseeded default render).
    [When("the page state is rendered for {string}")]
    public void WhenPageStateRendered(string path)
    {
        var renderer = new SsrRenderer(ctx.Store!, ctx.Description!);
        ctx.RenderedHtml = renderer.Render(path, principalUserId: ctx.PrincipalUserId, seed: ctx.Seed).Html;
    }

    // Seed the named component's render-slot with a `state` object carrying the given `open` flag (client
    // data layer, slice 1a). v1 is a WHOLE-OBJECT overwrite, so the seed replaces the component's `state`
    // var wholesale with `{ open: <value> }`. The panel is a value-position root component, so its slot key
    // is the bare "comp:" (AccessSeedPanelSlot). The next render step passes ctx.Seed into Render, which —
    // after the panel's setup runs — overwrites `state` before invoking its view, reproducing the client's
    // open popup. (Directly injected here; the client SHIP of this state is a later slice.)
    [When("the {string} slot is seeded {string} = {word}")]
    public void WhenSlotSeeded(string _component, string field, string value)
    {
        var open = bool.Parse(value);
        var state = new DeEnv.Code.ExecObject
        {
            Id = -1000,
            Props = new Dictionary<string, DeEnv.Code.IExecValue> { [field] = new DeEnv.Code.ExecBool { Value = open } },
        };
        ctx.Seed = new Dictionary<string, IReadOnlyDictionary<string, DeEnv.Code.IExecValue>>
        {
            [InstanceContext.AccessSeedPanelSlot] = new Dictionary<string, DeEnv.Code.IExecValue> { ["state"] = state },
        };
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

    // The synthesized generic render shows a <SignInBar> (collapsed: a `.sign-in-bar` div) to an anonymous
    // visitor when the app HAS access rules but is NOT anonymousLockedOut (a public app) — the login-as-state
    // entry point that reserves no URL. Asserts on the rendered BODY (not the whole document): the shipped
    // window.initUi island carries the WHOLE library AST — every component's definition, SignInBar's `class=
    // "sign-in-bar"` literal included — so a whole-document substring would match even when nothing rendered.
    [Then("the rendered document includes a sign-in control")]
    public async Task ThenHasSignIn()
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(RenderedBody().Contains("sign-in-bar")).IsTrue();
    }

    // A DORMANT no-auth app (accessActive false) must NOT sprout a stray sign-in control — the affordance is
    // gated on auth being on, so a plain generic app (todo/crm/…) is byte-identical to before this slice.
    [Then("the rendered document includes no sign-in control")]
    public async Task ThenNoSignIn()
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(RenderedBody().Contains("sign-in-bar")).IsFalse();
    }

    // The admin-only "Manage users" control (the `<UserMenu>` button that toggles `<UserAdmin>`), gated on
    // the derived `canManageUsers` capability — NOT the shipped role. Body-only (the AST island ships the
    // component definitions), like the sign-in assertions.
    [Then("the rendered document includes a user-management control")]
    public async Task ThenHasManageUsers()
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(RenderedBody().Contains("manage-users")).IsTrue();
    }

    [Then("the rendered document includes no user-management control")]
    public async Task ThenNoManageUsers()
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(RenderedBody().Contains("manage-users")).IsFalse();
    }

    // A write-affordance marker in the rendered BODY: "form-actions" (ObjectForm Save/Discard), "new-btn"
    // (SetTable New), "set-remove" (SetTable Remove). The generic UI gates each on sys.canWrite(type, verb),
    // so a read-only principal's body carries none. Body-only (the AST island ships the component defs).
    [Then("the rendered body shows a {string} marker")]
    public async Task ThenBodyShowsMarker(string marker)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(RenderedBody().Contains(marker)).IsTrue();
    }

    [Then("the rendered body shows no {string} marker")]
    public async Task ThenBodyShowsNoMarker(string marker)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(RenderedBody().Contains(marker)).IsFalse();
    }

    // The rendered <body> (the markup a visitor sees), EXCLUDING the window.initData/initUi hydration island
    // that ships the full component AST. That island lives in the <head> (UiLayout), so taking from <body>
    // onward yields only what actually RENDERED — the one region where a SignInBar that fired shows up.
    private string RenderedBody()
    {
        var html = ctx.RenderedHtml!;
        var i = html.IndexOf("<body", StringComparison.Ordinal);
        return i < 0 ? html : html[i..];
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

    // Fix 2 — the path `write` op onto a set member's scalar field (`/milestones/<id>/title`). This is the
    // SAME mutation objectPropChange performs but routed through the leaf-path seam (WriteLeaf walks into the
    // set), which was ungated. The `{word}` (admin/member) is the actor label; the bound principal decides.
    [When("the {word} writes {string} of {string} {int} to {string} via the path write op")]
    public void WhenPathWritesField(string _actor, string prop, string _type, int memberId, string value)
    {
        // The `write` op carries a BARE scalar value (ws.ts sends `bareScalar(value)`), not the
        // { type, value } envelope objectPropChange uses — title is text, so a bare JSON string.
        var (ws, clientId) = BoundWs();
        _writeReply = ws.ProcessMessage(
            $$"""{ "op": "write", "clientId": "{{clientId}}", "path": "/milestones/{{memberId}}/{{prop}}", "value": "{{value}}" }""");
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
