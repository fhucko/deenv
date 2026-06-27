using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Atomic-commit step definitions (AtomicCommit.feature). The `commit` op is driven directly at the
// WsHandler level (in-process, like the Access write-enforcement scenarios) — no browser needed.
// The observable is the STORE after the attempted batch: all-or-none persistence.
[Binding]
public sealed class AtomicCommitSteps(InstanceContext ctx)
{
    private string _reply = "";
    private ClientSessionStore? _sessions;

    // ── Background ──────────────────────────────────────────────────────────────

    [Given("the two-field commit fixture app")]
    public void GivenTwoFieldFixture()
    {
        ctx.Description = InstanceContext.TwoFieldCommitFixtureDb();
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    // Install an Item access rule for the atomic-commit denial scenario. The rule line is the verb +
    // optional condition (e.g. `edit where currentUser.role == "Admin"`). The fixture is rebuilt over
    // the SAME seed so the seeded item/users remain. Mirrors the AccessSteps pattern but uses a distinct
    // step text ("Item access rule") to avoid colliding with AccessSteps' "the access rule" binding.
    [Given(@"the Item access rule {string}")]
    public void GivenItemAccessRule(string rule)
    {
        rule = rule.Replace("\\\"", "\"");
        // Extract the type name + rule line. The rule text is "Item <verbLine>", e.g. "Item edit where …".
        const string prefix = "Item ";
        if (!rule.StartsWith(prefix)) return;
        var ruleLine = rule[prefix.Length..];
        ctx.Description = InstanceContext.TwoFieldCommitFixtureDb(ruleLine);
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    // ── When ────────────────────────────────────────────────────────────────────

    // A WS session bound to the currently-chosen principal (mirrors the AccessSteps pattern).
    private (WsHandler Ws, string ClientId) BoundWs()
    {
        _sessions ??= new ClientSessionStore();
        var session = _sessions.Create();
        session.PrincipalUserId = ctx.PrincipalUserId;
        var ws = new WsHandler(ctx.Store!, ctx.Description!, _sessions);
        return (ws, session.Id);
    }

    // Happy-path commit: two valid fields on item 2.
    [When("the commit sends title {string} and count {int}")]
    public void WhenCommitTwoValid(string title, int count)
    {
        var (ws, clientId) = BoundWs();
        _reply = ws.ProcessMessage($$"""
            {
              "op": "commit",
              "clientId": "{{clientId}}",
              "edits": [
                { "objectId": {{InstanceContext.TwoFieldItemId}}, "prop": "title", "value": { "type": "text",  "value": "{{title}}" } },
                { "objectId": {{InstanceContext.TwoFieldItemId}}, "prop": "count", "value": { "type": "int",   "value": {{count}} } }
              ]
            }
            """);
    }

    // Failure-path commit: one valid field + one that names a non-existent prop. Today the valid
    // objectPropChange would persist; after the fix NEITHER persists (the batch rolls back as a unit).
    [When("the commit sends title {string} and an unknown field {string}")]
    public void WhenCommitOneValidOneBad(string title, string unknownProp)
    {
        var (ws, clientId) = BoundWs();
        _reply = ws.ProcessMessage($$"""
            {
              "op": "commit",
              "clientId": "{{clientId}}",
              "edits": [
                { "objectId": {{InstanceContext.TwoFieldItemId}}, "prop": "title",      "value": { "type": "text", "value": "{{title}}" } },
                { "objectId": {{InstanceContext.TwoFieldItemId}}, "prop": "{{unknownProp}}", "value": { "type": "text", "value": "bad" } }
              ]
            }
            """);
    }

    // Access-denial commit: both title and count edits in one batch; the member role is denied.
    [When("the member commits edits to both {string} and {string} of item {int}")]
    public void WhenMemberCommitsTwoFields(string prop1, string prop2, int itemId)
    {
        var (ws, clientId) = BoundWs();
        _reply = ws.ProcessMessage($$"""
            {
              "op": "commit",
              "clientId": "{{clientId}}",
              "edits": [
                { "objectId": {{itemId}}, "prop": "{{prop1}}", "value": { "type": "text", "value": "Hacked title" } },
                { "objectId": {{itemId}}, "prop": "{{prop2}}", "value": { "type": "int",  "value": 99 } }
              ]
            }
            """);
    }

    // ── Then ────────────────────────────────────────────────────────────────────

    [Then("the commit is accepted")]
    public async Task ThenCommitAccepted()
    {
        using var doc = JsonDocument.Parse(_reply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsFalse();
    }

    [Then("the commit is rejected")]
    public async Task ThenCommitRejected()
    {
        using var doc = JsonDocument.Parse(_reply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsTrue();
    }

    // Integer store assertion (count field). Mirrors AccessSteps.ThenStoredFieldEquals for int.
    [Then("the stored {string} {int} has {string} equal to {int}")]
    public async Task ThenStoredIntFieldEquals(string type, int id, string prop, int expected)
    {
        var obj = ctx.Store!.ReadById(id);
        await Assert.That(obj).IsNotNull();
        await Assert.That(obj!.Value.Fields.Fields[prop]).IsEqualTo((NodeValue)new IntValue(expected));
    }
}
