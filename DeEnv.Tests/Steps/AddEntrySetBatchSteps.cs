using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Drives the addEntry set-value mint+link batching fix at the WS-HANDLER seam (real store + a live
// client session), the same level TransientIdSteps drives the sibling arrayAdd fix — no browser. Pins
// the equivalence the fix must hold: a value-branch set add still mints exactly ONE new object, links it
// into exactly the set the path addressed, reports that object's real id + fields correctly, and bumps
// the store's HEAD version by exactly 2 (one for the create, one for the link) — the same delta the old
// two-call (CreateObject + AddToSet) path produced, now inside one atomic CommitBatch.
[Binding]
public sealed class AddEntrySetBatchSteps
{
    private const string Schema =
        """
        types
            Db
                items set of Item
            Item
                name text
                children set of Child
            Child
                name text
        """;

    // camelCase so the serialized messages are the wire bytes WsRequest deserializes (mirrors WsHandler).
    private static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deenv-addentrysetbatch-" + Guid.NewGuid().ToString("N"));
    private InstanceDescription _desc = null!;
    private IInstanceStore _store = null!;
    private WsHandler _ws = null!;
    private string _clientId = "";
    private string _reply = "";
    private int _versionBefore;
    private int _reportedId;
    private int _parentId;

    // ── Given ────────────────────────────────────────────────────────────────────

    [Given("a Db instance with a root set of Item and a live client session")]
    public void GivenInstance()
    {
        Directory.CreateDirectory(_dir);
        var appPath = Path.Combine(_dir, "app.app");
        File.WriteAllText(appPath, Schema);
        _desc = InstanceDescriptionLoader.LoadFile(appPath);
        _store = new JsonFileInstanceStore(Path.Combine(_dir, "app-data.json"), _desc);

        var sessions = new ClientSessionStore();
        _clientId = sessions.Create().Id;
        _ws = new WsHandler(_store, _desc, sessions);
    }

    [Given("an item {string} already in the set")]
    public void GivenItemInSet(string name)
    {
        // Seeded directly through the store (not the WS) so the scenario has a stable, KNOWN owner id to
        // address the nested addEntry at — the fix under test is the value-branch mint+link, not this setup.
        _parentId = _store.CreateObject("Item", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name),
        }));
        _store.AddToSet(NodePath.Root.Field("items"), _parentId);
    }

    // ── When ─────────────────────────────────────────────────────────────────────

    [When("the client addEntry a new item named {string} at path {string} over the WS")]
    public void WhenAddItem(string name, string path)
    {
        _versionBefore = _store.CurrentVersion;
        _reply = _ws.ProcessMessage(JsonSerializer.Serialize(new
        {
            op = "addEntry",
            clientId = _clientId,
            path,
            value = new { name },
        }, Opts));
        CaptureReportedId();
    }

    [When("the client addEntry a new child named {string} into the parent's children path over the WS")]
    public void WhenAddChild(string name)
    {
        _versionBefore = _store.CurrentVersion;
        _reply = _ws.ProcessMessage(JsonSerializer.Serialize(new
        {
            op = "addEntry",
            clientId = _clientId,
            path = $"/items/{_parentId}/children",
            value = new { name },
        }, Opts));
        CaptureReportedId();
    }

    private void CaptureReportedId()
    {
        using var doc = JsonDocument.Parse(_reply);
        if (doc.RootElement.TryGetProperty("key", out var k) && int.TryParse(k.GetString(), out var id))
            _reportedId = id;
    }

    // ── Then ─────────────────────────────────────────────────────────────────────
    // Own phrasing ("addEntry reply", not TransientIdSteps' "WS reply") — a Reqnroll [Binding] class is a
    // fresh instance per scenario (its own _reply field), so reusing another class's IDENTICAL step text
    // would read THAT class's (unset) field, not this one's — hence the distinct wording, not shared text.

    [Then("the WS addEntry reply is ok")]
    public async Task ThenReplyOk()
    {
        using var doc = JsonDocument.Parse(_reply);
        await Assert.That(doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()).IsTrue();
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsFalse();
    }

    [Then("the Item extent has exactly {int} object")]
    public async Task ThenItemExtentCount(int count) =>
        await Assert.That(_store.ReadExtent("Item").Count).IsEqualTo(count);

    [Then("the Child extent has exactly {int} object")]
    public async Task ThenChildExtentCount(int count) =>
        await Assert.That(_store.ReadExtent("Child").Count).IsEqualTo(count);

    [Then("the items set has exactly {int} member, the one addEntry reported")]
    public async Task ThenItemsSetMembers(int count)
    {
        var set = (SetValue)_store.ReadNode(NodePath.Root.Field("items"))!;
        await Assert.That(set.Members.Count).IsEqualTo(count);
        await Assert.That(set.Members.ContainsKey(_reportedId)).IsTrue();
    }

    [Then("the parent's children set has exactly {int} member, the one addEntry reported")]
    public async Task ThenChildrenSetMembers(int count)
    {
        var set = (SetValue)_store.ReadNode(NodePath.Root.Field("items").Key(_parentId.ToString()).Field("children"))!;
        await Assert.That(set.Members.Count).IsEqualTo(count);
        await Assert.That(set.Members.ContainsKey(_reportedId)).IsTrue();
    }

    [Then("the reported item's {string} is {string}")]
    public async Task ThenReportedField(string prop, string expected)
    {
        var hit = _store.ReadById(_reportedId);
        await Assert.That(hit).IsNotNull();
        await Assert.That((hit!.Value.Fields.Fields[prop] as TextValue)?.Text).IsEqualTo(expected);
    }

    [Then("the store version advanced by exactly {int}")]
    public async Task ThenVersionDelta(int delta) =>
        await Assert.That(_store.CurrentVersion - _versionBefore).IsEqualTo(delta);

    // ── teardown ─────────────────────────────────────────────────────────────────

    [AfterScenario]
    public void Cleanup()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
