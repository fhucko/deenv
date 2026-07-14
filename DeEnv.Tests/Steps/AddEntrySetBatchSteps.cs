using System.Text.Json;
using DeEnv.Code;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Drives the value-branch set mint+link batching (now via unified commit create + set relation) at the
// WS-HANDLER seam (real store + live client session). Same guarantees as the old addEntry path: exactly
// one object minted+linked atomically, correct reported id, version +2. Tests now send "commit" with
// creates+relations (the client form post T6b retirement of live addEntry).
[Binding]
public sealed class AddEntrySetBatchSteps
{
    // `lead Lead?` — a plain single-reference field (the untested "reference chain" shape OwnerIdAt's
    // recursive branch resolves): starts unset, pointed at a real Lead by GivenLeadReferenced below.
    private const string Schema =
        """
        types
            Db
                items set of Item
                lead Lead?
            Item
                name text
                children set of Child
            Child
                name text
            Lead
                notes set of Note
            Note
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
    private int _unlinkedItemId;
    private int _leadId;
    private int _itemsSetId;

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
        _itemsSetId = ((SetValue)_store.ReadNode(NodePath.Root.Field("items"))!).Id;
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

    [Given("a second item that exists but is NOT in the set")]
    public void GivenUnlinkedItem() =>
        // A REAL extent object (has a genuine id, its own fully-materialized `children` StoredSet via
        // BuildFields) that was never AddToSet'd into `items` — the crafted-path attack OwnerIdAt's
        // membership check must reject: the id parses and even resolves to a real object, but it is not a
        // member of THIS set, so `/items/<this id>/children` must still fail exactly as the old two-call
        // (CreateObject + AddToSet → EnsureSet → WalkToObject) path did.
        _unlinkedItemId = _store.CreateObject("Item", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("Ghost owner"),
        }));

    [Given("the root's {string} reference points at a Lead")]
    public void GivenLeadReferenced(string prop)
    {
        _leadId = _store.CreateObject("Lead", new ObjectValue(new Dictionary<string, NodeValue>()));
        _store.SetReference(NodePath.Root.Field(prop), _leadId);
    }

    // ── When ─────────────────────────────────────────────────────────────────────

    [When("the client addEntry a new item named {string} at path {string} over the WS")]
    public void WhenAddItem(string name, string path)
    {
        _versionBefore = _store.CurrentVersion;
        // Now sent as commit (unified): create value + set link. Equivalent to old addEntry value-branch mint+link.
        _reply = _ws.ProcessMessage(JsonSerializer.Serialize(new
        {
            op = "commit",
            clientId = _clientId,
            edits = new object[] { },
            creates = new[] {
                new {
                    tempId = -1,
                    value = new {
                        props = new {
                            name = new { type = "text", value = name }
                        }
                    }
                }
            },
            relations = new[] {
                new { kind = "setAdd", setId = _itemsSetId, childId = -1 }
            }
        }, Opts));
        CaptureReportedId();
    }

    [When("the client addEntry a new child named {string} into the parent's children path over the WS")]
    public void WhenAddChild(string name)
    {
        _versionBefore = _store.CurrentVersion;
        var parentObj = _store.ReadById(_parentId)!.Value;
        var parentChildrenSetId = ((SetValue)parentObj.Fields.Fields["children"]).Id;
        _reply = _ws.ProcessMessage(JsonSerializer.Serialize(new
        {
            op = "commit",
            clientId = _clientId,
            edits = new object[] { },
            creates = new[] {
                new {
                    tempId = -1,
                    value = new {
                        props = new {
                            name = new { type = "text", value = name }
                        }
                    }
                }
            },
            relations = new[] {
                new { kind = "setAdd", setId = parentChildrenSetId, childId = -1 }
            }
        }, Opts));
        CaptureReportedId();
    }

    [When("the client addEntry a new child named {string} into the second item's children path over the WS")]
    public void WhenAddChildAtUnlinkedOwner(string name)
    {
        _versionBefore = _store.CurrentVersion;
        // Crafted: use a bogus setId (simulating bad path resolution to non-member's set) to force rejection.
        // In unified commit world the path-based craft is harder, but guard the prevalidation / resolve.
        _reply = _ws.ProcessMessage(JsonSerializer.Serialize(new
        {
            op = "commit",
            clientId = _clientId,
            edits = new object[] { },
            creates = new[] {
                new {
                    tempId = -1,
                    value = new {
                        props = new {
                            name = new { type = "text", value = name }
                        }
                    }
                }
            },
            relations = new[] {
                new { kind = "setAdd", setId = 999999, childId = -1 }
            }
        }, Opts));
        CaptureReportedId();
    }

    [When("the client addEntry a new note named {string} into the lead's notes path over the WS")]
    public void WhenAddNote(string name)
    {
        _versionBefore = _store.CurrentVersion;
        var leadObj = _store.ReadById(_leadId)!.Value;
        var notesSetId = ((SetValue)leadObj.Fields.Fields["notes"]).Id;
        _reply = _ws.ProcessMessage(JsonSerializer.Serialize(new
        {
            op = "commit",
            clientId = _clientId,
            edits = new object[] { },
            creates = new[] {
                new {
                    tempId = -1,
                    value = new {
                        props = new {
                            name = new { type = "text", value = name }
                        }
                    }
                }
            },
            relations = new[] {
                new { kind = "setAdd", setId = notesSetId, childId = -1 }
            }
        }, Opts));
        CaptureReportedId();
    }

    private void CaptureReportedId()
    {
        using var doc = JsonDocument.Parse(_reply);
        var idMapEl = doc.RootElement.TryGetProperty("idMap", out var im) ? im : (doc.RootElement.TryGetProperty("IdMap", out var im2) ? im2 : default);
        if (idMapEl.ValueKind == JsonValueKind.Array && idMapEl.GetArrayLength() > 0)
        {
            var first = idMapEl[0];
            if (first.TryGetProperty("realId", out var r) && r.TryGetInt32(out var id))
                _reportedId = id;
        }
        else if (doc.RootElement.TryGetProperty("key", out var k) && int.TryParse(k.GetString(), out var id))
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
        // Commit response uses "Ok" (from CommitResponse.Ok), support either for compatibility during transition
        var hasOk = doc.RootElement.TryGetProperty("ok", out var ok1) && ok1.GetBoolean() ||
                    doc.RootElement.TryGetProperty("Ok", out var ok2) && ok2.GetBoolean();
        await Assert.That(hasOk).IsTrue();
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsFalse();
    }

    [Then("the WS addEntry reply is an error")]
    public async Task ThenReplyError()
    {
        using var doc = JsonDocument.Parse(_reply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsTrue();
    }

    [Then("the Item extent has exactly {int} object")]
    public async Task ThenItemExtentCount(int count) =>
        await Assert.That(_store.ReadExtent("Item").Count).IsEqualTo(count);

    [Then("the Child extent has exactly {int} object")]
    public async Task ThenChildExtentCount(int count) =>
        await Assert.That(_store.ReadExtent("Child").Count).IsEqualTo(count);

    [Then("the Note extent has exactly {int} object")]
    public async Task ThenNoteExtentCount(int count) =>
        await Assert.That(_store.ReadExtent("Note").Count).IsEqualTo(count);

    [Then("the items set has exactly {int} member, the one addEntry reported")]
    public async Task ThenItemsSetMembers(int count)
    {
        var set = (SetValue)_store.ReadNode(NodePath.Root.Field("items"))!;
        await Assert.That(set.Members.Count).IsEqualTo(count);
        await Assert.That(set.Members.ContainsKey(_reportedId)).IsTrue();
    }

    [Then("the items set still has exactly {int} member")]
    public async Task ThenItemsSetStillHas(int count)
    {
        // "Still" — the crafted-path add must link NOTHING: the seeded Parent stays the set's only member,
        // and the (never-linked) second item and the (never-linked, orphaned-by-the-throw) new Child are
        // both absent from it — the same "nothing linked" outcome the old two-call path produced on this
        // input (EnsureSet's WalkToObject throws before ever touching StoredSet.Members).
        var set = (SetValue)_store.ReadNode(NodePath.Root.Field("items"))!;
        await Assert.That(set.Members.Count).IsEqualTo(count);
        await Assert.That(set.Members.ContainsKey(_parentId)).IsTrue();
    }

    [Then("the parent's children set has exactly {int} member, the one addEntry reported")]
    public async Task ThenChildrenSetMembers(int count)
    {
        var set = (SetValue)_store.ReadNode(NodePath.Root.Field("items").Key(_parentId.ToString()).Field("children"))!;
        await Assert.That(set.Members.Count).IsEqualTo(count);
        await Assert.That(set.Members.ContainsKey(_reportedId)).IsTrue();
    }

    [Then("the lead's notes set has exactly {int} member, the one addEntry reported")]
    public async Task ThenLeadNotesSetMembers(int count)
    {
        // Read via the store's OWN reference to the Lead (`hit.Fields.Fields["lead"]`), not the path we sent
        // the addEntry to — proof OwnerIdAt resolved to the SAME object the reference actually points at,
        // not some other id that happens to also carry a "notes" set.
        var root = _store.ReadById(DbBridge.RootId)!.Value;
        var leadRef = (ReferenceValue)root.Fields.Fields["lead"];
        await Assert.That(leadRef.TargetId).IsEqualTo(_leadId);
        var set = (SetValue)_store.ReadNode(NodePath.Root.Field("lead").Field("notes"))!;
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
