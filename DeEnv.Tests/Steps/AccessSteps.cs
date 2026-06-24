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

    // The fixture already carries the rule; this step makes its presence an explicit, asserted
    // precondition (the ruleset is what activates enforcement).
    [Given("the access rule {string}")]
    public async Task GivenAccessRule(string rule)
    {
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
}
