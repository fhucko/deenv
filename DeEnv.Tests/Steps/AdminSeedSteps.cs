using System.Text.Json;
using DeEnv.Code;
using DeEnv.Http;
using DeEnv.Kernel;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// M-auth login sub-slice 1d — bootstrap / seed the first admin. An app whose schema carries access
// rules is deny-by-default: with no Admin-role User, no one can ever log in. The first admin is seeded
// by a kernel/server-side operation (AdminSeed.Seed) taking operator-provided credentials — NOT a gated
// WS action (chicken-and-egg). These steps invoke the seed operation DIRECTLY against the instance's
// store + description (how the operator UI collects the credentials is a later slice), then prove the
// seeded admin is loginable via the SAME WS `login` path a real login takes (LoginSteps-style).
//
// The fixture (AccessFixtureNoUsers) is the access app shape — a Role enum + a `users set of User` + the
// read rule — with NO seeded users, so the seed creates the first admin from scratch.
[Binding]
public sealed class AdminSeedSteps(InstanceContext ctx)
{
    private Exception? _seedError;

    // ── Given ─────────────────────────────────────────────────────────────────────

    // The access app shape with rules + a User type + a role enum, but NO seeded users — the
    // chicken-and-egg the seed solves. The store seeds only the Db root (so the `users` set exists).
    [Given("an app with access rules, a User type and a role enum but no users yet")]
    public async Task GivenAppWithRulesNoUsers()
    {
        ctx.Description = InstanceContext.AccessFixtureNoUsers();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);

        // Sanity: the app IS rule-active (the bootstrap problem only exists under rules) and starts with
        // no User in the extent.
        await Assert.That(ctx.Description!.Rules!.Count).IsGreaterThan(0);
        await Assert.That(ctx.Store!.ReadExtent(UserConvention.TypeName).Count).IsEqualTo(0);
    }

    [Given("no User holds the {string} role")]
    public async Task GivenNoUserHoldsRole(string role) =>
        await Assert.That(UsersWithRole(role).Count).IsEqualTo(0);

    // ── When ────────────────────────────────────────────────────────────────────────

    // Invoke the seed operation directly — the kernel-side bootstrap, with operator-provided credentials
    // as INPUT. A refusal (a bad role) is captured so the "refused" Then can assert it.
    [When("the operator seeds an admin named {string} with password {string} and role {string}")]
    [Given("the operator seeds an admin named {string} with password {string} and role {string}")]
    public void WhenOperatorSeedsAdmin(string name, string password, string role)
    {
        try
        {
            AdminSeed.Seed(ctx.Store!, ctx.Description!, name, password, role);
        }
        catch (Exception ex)
        {
            _seedError = ex;
        }
    }

    // Trigger a garbage collection through an ordinary GC-triggering mutation (removing a non-existent
    // set member runs CollectGarbage). Proves the seeded admin, being a linked graph member, is not swept.
    [When("a garbage collection is triggered")]
    public void WhenGcTriggered()
    {
        var milestones = (SetValue)((ObjectValue)ctx.Store!.ReadNode(NodePath.Root)!).Fields["milestones"];
        ctx.Store!.RemoveFromSet(milestones.Id, -1); // no such member → a no-op that still runs GC
    }

    // ── Then ──────────────────────────────────────────────────────────────────────

    [Then("a User holds the {string} role")]
    public async Task ThenAUserHoldsRole(string role) =>
        await Assert.That(UsersWithRole(role).Count).IsGreaterThan(0);

    [Then("there is exactly one {string} User")]
    public async Task ThenExactlyOneUser(string role) =>
        await Assert.That(UsersWithRole(role).Count).IsEqualTo(1);

    [Then("no User holds the {string} role")]
    public async Task ThenNoUserHoldsRole(string role) =>
        await Assert.That(UsersWithRole(role).Count).IsEqualTo(0);

    [Then("the seed is refused")]
    public async Task ThenSeedRefused()
    {
        await Assert.That(_seedError).IsNotNull();
        await Assert.That(_seedError is InvalidOperationException).IsTrue();
    }

    // The end-to-end proof the seeded admin is real: log in by name via the SAME WS `login` op a real
    // login takes (resolves the User by name, verifies the plaintext against the stored PBKDF2 hash).
    [Then("the seeded admin can log in as {string} with password {string}")]
    public async Task ThenSeededAdminCanLogIn(string name, string password)
    {
        var sessions = new ClientSessionStore();
        var session = sessions.Create();
        var ws = new WsHandler(ctx.Store!, ctx.Description!, sessions);
        var reply = ws.ProcessMessage(
            $$"""{ "op": "login", "clientId": "{{session.Id}}", "name": "{{name}}", "password": "{{password}}" }""");

        using var doc = JsonDocument.Parse(reply);
        await Assert.That(doc.RootElement.GetProperty("op").GetString()).IsEqualTo("login");
        await Assert.That(doc.RootElement.GetProperty("ok").GetBoolean()).IsTrue();
    }

    // The ids of every User in the extent carrying `role` — the authoritative outcome (a created admin
    // lands in the extent; idempotency means the same role is never minted twice).
    private List<int> UsersWithRole(string role) =>
        ctx.Store!.ReadExtent(UserConvention.TypeName)
            .Where(kv => kv.Value.Fields.GetValueOrDefault("role") is TextValue { Text: var r } && r == role)
            .Select(kv => kv.Key)
            .ToList();
}
