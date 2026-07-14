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

    // ── Step B: the atomic changeset ────────────────────────────────────────────────────

    [Given("the atomic-changeset fixture app")]
    public void GivenChangesetFixture() => BuildChangeset(null);

    [Given("the atomic-changeset fixture app denying Tag create")]
    public void GivenChangesetFixtureDenyingTagCreate() =>
        // A Tag `create` rule that only an Admin satisfies — so the member's staged Tag create is DENIED,
        // rolling the whole changeset back (the dormant-app default would allow it).
        BuildChangeset("create where currentUser.role == \"Admin\"");

    // The feature Background seeds a two-field fixture into ctx.DataFilePath first, so a changeset scenario
    // must start from a FRESH data file (else its store loads the wrong-shaped two-field seed) — point the
    // store at a new temp path before seeding the changeset schema.
    private void BuildChangeset(string? tagRuleLines)
    {
        ctx.DataFilePath = System.IO.Path.GetTempFileName();
        ctx.Description = InstanceContext.AtomicChangesetFixtureDb(tagRuleLines);
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);
    }

    // The headline changeset: edit an existing Item's title + CREATE a Tag (a separate type) + a RELATION
    // linking the new Tag into Db.tags — all in ONE commit. The Tag is a transient negative id (-1); the
    // set relation names it by that tempId, and the server mints + links + edits atomically. The tags set's
    // intrinsic id is read from the store at runtime (robust to seed-id drift), not hard-coded.
    [When("the changeset edits item {int} title to {string}, creates a Tag {string} and links it into tags")]
    public void WhenChangeset(int itemId, string newTitle, string tagLabel) =>
        SendChangeset(itemId, newTitle, tagLabel);

    // The same changeset fired as the (denied) member — distinct step text only so the scenario reads as
    // "the member's changeset"; the message is identical (the principal is bound via BoundWs).
    [When("the member's changeset edits item {int} title to {string}, creates a Tag {string} and links it into tags")]
    public void WhenMemberChangeset(int itemId, string newTitle, string tagLabel) =>
        SendChangeset(itemId, newTitle, tagLabel);

    private void SendChangeset(int itemId, string newTitle, string tagLabel)
    {
        var (ws, clientId) = BoundWs();
        var tagsSetId = TagsSetId();
        _reply = ws.ProcessMessage($$"""
            {
              "op": "commit",
              "clientId": "{{clientId}}",
              "edits": [
                { "objectId": {{itemId}}, "prop": "title", "value": { "type": "text", "value": "{{newTitle}}" } }
              ],
              "creates": [
                { "tempId": -1, "value": { "props": { "label": { "type": "text", "value": "{{tagLabel}}" } } } }
              ],
              "relations": [
                { "kind": "setAdd", "setId": {{tagsSetId}}, "childId": -1 }
              ]
            }
            """);
    }

    // A FORGED changeset: one create (tempId -1) named by TWO set relations (into tags AND users). The
    // interpreter never emits this (a create has exactly one join); the floor-widening guard rejects it
    // whole — so neither the create nor either link persists.
    [When("a changeset links one create into both tags and users")]
    public void WhenChangesetDoubleRelation()
    {
        var (ws, clientId) = BoundWs();
        var tagsSetId = SetId("tags");
        var usersSetId = SetId("users");
        _reply = ws.ProcessMessage($$"""
            {
              "op": "commit",
              "clientId": "{{clientId}}",
              "edits": [],
              "creates": [
                { "tempId": -1, "value": { "props": { "label": { "type": "text", "value": "forged" } } } }
              ],
              "relations": [
                { "kind": "setAdd", "setId": {{tagsSetId}}, "childId": -1 },
                { "kind": "setAdd", "setId": {{usersSetId}}, "childId": -1 }
              ]
            }
            """);
    }

    // A malformed changeset: a set relation references a create tempId (-99) for which NO create is sent.
    // The store's pre-validate rejects it whole, untouched (the flat-remap invariant's teeth).
    [When("a changeset links a non-existent create into tags")]
    public void WhenChangesetMissingCreate()
    {
        var (ws, clientId) = BoundWs();
        var tagsSetId = TagsSetId();
        _reply = ws.ProcessMessage($$"""
            {
              "op": "commit",
              "clientId": "{{clientId}}",
              "edits": [],
              "creates": [],
              "relations": [
                { "kind": "setAdd", "setId": {{tagsSetId}}, "childId": -99 }
              ]
            }
            """);
    }

    // A staged User create carrying a PLAINTEXT password — to prove the WS hash chokepoint fires for a
    // commit create exactly as it does for arrayAdd (a staged User can never store plaintext).
    [When("a changeset creates a User {string} with password {string} and links it into users")]
    public void WhenChangesetCreatesUser(string name, string password)
    {
        var (ws, clientId) = BoundWs();
        var usersSetId = SetId("users");
        _reply = ws.ProcessMessage($$"""
            {
              "op": "commit",
              "clientId": "{{clientId}}",
              "edits": [],
              "creates": [
                { "tempId": -1, "value": { "props": {
                    "name": { "type": "text", "value": "{{name}}" },
                    "password": { "type": "text", "value": "{{password}}" } } } }
              ],
              "relations": [
                { "kind": "setAdd", "setId": {{usersSetId}}, "childId": -1 }
              ]
            }
            """);
    }

    private int TagsSetId() => SetId("tags");

    // The intrinsic id of a Db-root collection prop's set, read from the store (a SetValue carries its Id) so
    // the changeset names the real set regardless of the seed's mint order.
    private int SetId(string prop)
    {
        var node = ctx.Store!.ReadNode(NodePath.FromSegments([prop]));
        return node is SetValue sv ? sv.Id : throw new InvalidOperationException($"{prop} is not a set.");
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

    // ── Step B: changeset assertions ────────────────────────────────────────────────────

    // An object of `type` with its `label` scalar equal to `label` is present in the extent.
    [Then("a {string} labelled {string} exists in the store")]
    public async Task ThenLabelledExists(string type, string label) =>
        await Assert.That(LabelledIds(type, label).Any()).IsTrue();

    [Then("no {string} labelled {string} exists in the store")]
    public async Task ThenNoLabelledExists(string type, string label) =>
        await Assert.That(LabelledIds(type, label).Any()).IsFalse();

    // The named set (a Db-root collection prop) contains a member of `type` whose `label` matches — proving
    // the create was LINKED, not just minted: the member's id must be both in the extent (by label) and in
    // the set's members.
    [Then("the {string} set contains a {string} labelled {string}")]
    public async Task ThenSetContainsLabelled(string setProp, string type, string label)
    {
        var set = ctx.Store!.ReadNode(NodePath.FromSegments([setProp])) as SetValue;
        await Assert.That(set).IsNotNull();
        var labelled = LabelledIds(type, label).ToHashSet();
        await Assert.That(set!.Members.Keys.Any(labelled.Contains)).IsTrue();
    }

    // The named set (a Db-root collection prop) has no members — the malformed changeset left it untouched.
    [Then("the {string} set is empty")]
    public async Task ThenSetEmpty(string setProp)
    {
        var set = ctx.Store!.ReadNode(NodePath.FromSegments([setProp])) as SetValue;
        await Assert.That(set).IsNotNull();
        await Assert.That(set!.Members.Count).IsEqualTo(0);
    }

    // The commit reply's idMap remaps the staged Tag's transient negative id to a real (positive) extent id.
    [Then("the commit reply maps the new Tag to a real id")]
    public async Task ThenReplyMapsTag()
    {
        using var doc = JsonDocument.Parse(_reply);
        await Assert.That(doc.RootElement.TryGetProperty("idMap", out var idMap)).IsTrue();
        await Assert.That(idMap.GetArrayLength()).IsEqualTo(1);
        var entry = idMap[0];
        await Assert.That(entry.GetProperty("tempId").GetInt32()).IsEqualTo(-1);
        await Assert.That(entry.GetProperty("realId").GetInt32() > 0).IsTrue();
    }

    // Extent ids of `type` whose `label` scalar equals `label`.
    private IEnumerable<int> LabelledIds(string type, string label) =>
        ctx.Store!.ReadExtent(type)
            .Where(kv => kv.Value.Fields.GetValueOrDefault("label") is TextValue { Text: var t } && t == label)
            .Select(kv => kv.Key);

    // The stored password value of the named user is NOT the plaintext (it was hashed by the WS chokepoint).
    [Then("the stored {string} {string} password is not the plaintext {string}")]
    public async Task ThenPasswordNotPlaintext(string type, string name, string plaintext) =>
        await Assert.That(StoredPassword(type, name)).IsNotEqualTo(plaintext);

    // The stored hash verifies against the original plaintext (a real PBKDF2 hash, not a corrupted value).
    [Then("the stored {string} {string} password verifies against {string}")]
    public async Task ThenPasswordVerifies(string type, string name, string plaintext) =>
        await Assert.That(DeEnv.Code.AuthCrypto.Verify(plaintext, StoredPassword(type, name) ?? "")).IsTrue();

    // The raw stored `password` field of the `type` extent object named `name` (an extent read, so it is the
    // real stored hash — the load/ship blanking happens elsewhere).
    private string? StoredPassword(string type, string name) =>
        ctx.Store!.ReadExtent(type)
            .Where(kv => kv.Value.Fields.GetValueOrDefault("name") is TextValue { Text: var n } && n == name)
            .Select(kv => kv.Value.Fields.GetValueOrDefault("password") is TextValue { Text: var p } ? p : null)
            .FirstOrDefault();
}
