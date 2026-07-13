using System.Text.Json;
using DeEnv.Code;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Drives the transient-id remap at the WS-HANDLER seam (real store + a live client session), the same
// level HostActionSteps drives the host actions — no browser. The Code UI adds an object to a set with a
// transient NEGATIVE id and keeps addressing it by that id until the arrayAdd round-trip remaps it; the
// server resolves that id through a per-session table, so a field edit / remove that arrives still
// addressing the transient id lands on the real object. We send the exact wire messages ws.ts sends and
// assert against the real store.
[Binding]
public sealed class TransientIdSteps
{
    private const string Schema =
        """
        types
            Db
                items set of Item
            Item
                name text
        """;

    // camelCase so the serialized messages are the wire bytes WsRequest deserializes (mirrors WsHandler).
    private static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deenv-transientid-" + Guid.NewGuid().ToString("N"));
    private InstanceDescription _desc = null!;
    private IInstanceStore _store = null!;
    private WsHandler _ws = null!;
    private string _clientId = "";
    private string _reply = "";
    private int _addedRealId;

    // ── Given ────────────────────────────────────────────────────────────────────

    [Given("a Code instance with a set of items and a live client session")]
    public void GivenInstance()
    {
        Directory.CreateDirectory(_dir);
        var appPath = Path.Combine(_dir, "app.app");
        File.WriteAllText(appPath, Schema);
        _desc = InstanceDescriptionLoader.LoadFile(appPath);
        _store = new JsonFileInstanceStore(Path.Combine(_dir, "app-data.json"), _desc);

        // A real session store + a claimed session, exactly as the SSR mints one and the client claims it
        // by clientId — the home of the per-client transient-id remap the handler reconciles through.
        var sessions = new ClientSessionStore();
        _clientId = sessions.Create().Id;
        _ws = new WsHandler(_store, _desc, sessions);
    }

    // ── When ─────────────────────────────────────────────────────────────────────

    [When("the client adds an item with transient id {int} over the WS")]
    public void WhenAddItem(int tempId)
    {
        _reply = _ws.ProcessMessage(JsonSerializer.Serialize(new
        {
            op = "arrayAdd",
            clientId = _clientId,
            setId = ItemsSetId(),
            tempId,
            typeName = "Item",
            value = new { props = new { name = new { type = "text", value = "" } } },
        }, Opts));
        // The reply carries the real extent id the server minted (newId) — the id the client would remap to.
        using var doc = JsonDocument.Parse(_reply);
        if (doc.RootElement.TryGetProperty("newId", out var n)) _addedRealId = n.GetInt32();
    }

    [When("the client sets prop {string} on object {int} to {string} over the WS")]
    public void WhenSetPropOnTransient(string prop, int objectId, string value) => SetProp(prop, objectId, value);

    [When("the client sets prop {string} on the added member's real id to {string} over the WS")]
    public void WhenSetPropOnReal(string prop, string value) => SetProp(prop, _addedRealId, value);

    [When("the client acks the new id for transient id {int} over the WS")]
    public void WhenAck(int tempId) =>
        _reply = _ws.ProcessMessage(JsonSerializer.Serialize(new
        {
            op = "ackRemap",
            clientId = _clientId,
            tempId,
        }, Opts));

    [When("the client removes object {int} from the set over the WS")]
    public void WhenRemove(int objectId) =>
        _reply = _ws.ProcessMessage($$"""{ "op": "commit", "clientId": "{{_clientId}}", "edits": [], "creates": [], "relations": [ { "kind": "setUnlink", "setId": {{ItemsSetId()}}, "childId": {{objectId}} } ] }""");

    private void SetProp(string prop, int objectId, string value) =>
        _reply = _ws.ProcessMessage($$"""{ "op": "commit", "clientId": "{{_clientId}}", "edits": [ { "objectId": {{objectId}}, "prop": "{{prop}}", "value": { "type": "text", "value": "{{value}}" } } ], "creates": [], "relations": [] }""");

    // ── Then ─────────────────────────────────────────────────────────────────────

    [Then("the WS reply is ok")]
    public async Task ThenReplyOk()
    {
        using var doc = JsonDocument.Parse(_reply);
        await Assert.That(doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()).IsTrue();
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsFalse();
    }

    [Then("the WS reply is an error")]
    public async Task ThenReplyError()
    {
        using var doc = JsonDocument.Parse(_reply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsTrue();
    }

    [Then("the added member has name {string}")]
    public async Task ThenAddedMemberName(string expected)
    {
        // Resolve the real object the transient id denoted; its persisted name must be the edited value —
        // proof the field edit, sent against the negative id, landed on the real object.
        var hit = _store.ReadById(_addedRealId);
        await Assert.That(hit).IsNotNull();
        await Assert.That((hit!.Value.Fields.Fields["name"] as TextValue)?.Text).IsEqualTo(expected);
    }

    [Then("the set has no members")]
    public async Task ThenSetEmpty()
    {
        // Reload the root fresh: the remove (sent against the negative id) resolved to the real member, so
        // the set is empty and the now-unreferenced object was collected.
        var db = DbBridge.LoadRoot(_store, _desc, new ExecContext());
        await Assert.That(((ExecArray)db.Props["items"]).Items.Count).IsEqualTo(0);
    }

    // ── helpers / teardown ────────────────────────────────────────────────────────

    // The intrinsic id of the root's `items` set — discovered the way the renderer does (load the root
    // through DbBridge, read the array's id), so the arrayAdd targets the real set.
    private int ItemsSetId() =>
        ((ExecArray)DbBridge.LoadRoot(_store, _desc, new ExecContext()).Props["items"]).Id;

    [AfterScenario]
    public void Cleanup()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
