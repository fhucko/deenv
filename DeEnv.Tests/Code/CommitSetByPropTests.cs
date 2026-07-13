using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// T2.2: HandleCommit must recognize the three new commit relation kinds —
//   setByProp    → SetLinkByPropMutation  (link a member into an owner's SET addressed by (owner, prop))
//   setUnlink    → SetUnlinkMutation       (unlink a member from a set addressed by raw setId)
//   setUnlinkByProp → SetUnlinkByPropMutation (unlink a member from an owner's SET addressed by (owner, prop))
// — and emit the matching CommitMutation (ids resolved through the session, like every other addressed id).
// Mirrors CommitSessionRemapTests' WsHandler+session harness. The wire keys deliberately reuse ParseRelation's
// existing names (childId / setId / parentId / prop) so the wire stays consistent.
public sealed class CommitSetByPropTests
{
    [Test]
    public async Task A_commit_with_setByProp_setUnlink_and_setUnlinkByProp_applies_them_all()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    children set of Node
                Node
                    label text
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            var sessions = new ClientSessionStore();
            var session = sessions.Create();
            var ws = new WsHandler(store, desc, sessions);

            // A pre-existing owner (Db root, id 1) with a `children` SET, plus two pre-existing members.
            var childrenSetId = ((SetValue)store.ReadNode(NodePath.Root.Field("children"))!).Id;
            store.CommitBatch(
                [
                    new CommitCreate(-1, "Node", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("memberA") })),
                    new CommitCreate(-2, "Node", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("memberB") })),
                ],
                [
                    new SetLinkMutation(childrenSetId, -1),
                    new SetLinkMutation(childrenSetId, -2),
                ]);
            var aId = store.ReadExtent("Node").First(kv => ((TextValue)kv.Value.Fields["label"]).Text == "memberA").Key;
            var bId = store.ReadExtent("Node").First(kv => ((TextValue)kv.Value.Fields["label"]).Text == "memberB").Key;

            var replyText = ws.ProcessMessage($$"""
                {
                  "op": "commit",
                  "clientId": "{{session.Id}}",
                  "edits": [],
                  "creates": [
                    { "tempId": -10, "value": { "props": { "label": { "type": "text", "value": "childViaByProp" } } } }
                  ],
                  "relations": [
                    { "kind": "setByProp", "parentId": 1, "prop": "children", "childId": -10 },
                    { "kind": "setUnlink", "setId": {{childrenSetId}}, "childId": {{aId}} },
                    { "kind": "setUnlinkByProp", "parentId": 1, "prop": "children", "childId": {{bId}} }
                  ]
                }
                """);

            using var reply = JsonDocument.Parse(replyText);
            await Assert.That(reply.RootElement.TryGetProperty("error", out _)).IsFalse();
            await Assert.That(reply.RootElement.TryGetProperty("idMap", out _)).IsTrue();

            // setByProp linked the freshly-created child (-10) into Db.children.
            var realChild = reply.RootElement.GetProperty("idMap")[0].GetProperty("realId").GetInt32();
            var children = (SetValue)store.ReadNode(NodePath.Root.Field("children"))!;
            await Assert.That(children.Members.ContainsKey(realChild)).IsTrue();

            // setUnlink removed memberA from the set addressed by raw setId.
            await Assert.That(children.Members.ContainsKey(aId)).IsFalse();

            // setUnlinkByProp removed memberB from Db's `children` set.
            await Assert.That(children.Members.ContainsKey(bId)).IsFalse();

            // The freshly created child is the ONLY remaining member (A and B were both unlinked).
            await Assert.That(children.Members.Count).IsEqualTo(1);
        }
        finally
        {
            if (File.Exists(dataPath)) File.Delete(dataPath);
            CleanupLogGenesis(dataPath);
        }
    }

    [Test]
    public async Task A_setByProp_missing_its_prop_is_rejected()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    children set of Node
                Node
                    label text
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            var sessions = new ClientSessionStore();
            var session = sessions.Create();
            var ws = new WsHandler(store, desc, sessions);

            var replyText = ws.ProcessMessage($$"""
                {
                  "op": "commit",
                  "clientId": "{{session.Id}}",
                  "edits": [],
                  "creates": [
                    { "tempId": -10, "value": { "props": { "label": { "type": "text", "value": "x" } } } }
                  ],
                  "relations": [
                    { "kind": "setByProp", "parentId": 1, "childId": -10 }
                  ]
                }
                """);

            using var reply = JsonDocument.Parse(replyText);
            await Assert.That(reply.RootElement.TryGetProperty("error", out var err)).IsTrue();
            await Assert.That(err.GetString()).IsEqualTo("commit relation is malformed.");
        }
        finally
        {
            if (File.Exists(dataPath)) File.Delete(dataPath);
            CleanupLogGenesis(dataPath);
        }
    }

    // T2.3: the set link/unlink commit ops must honor the access floor. A real (positive-id) member being
    // linked{or detached} is floor-checked as a `create` — so an UNREADABLE (write-denied) member is
    // REJECTED (the whole commit returns an {error}, nothing applied), exactly as the first-pass set/ref
    // link. Here we rule Node's `create` to false and prove every op that touches an existing Node member
    // is denied, while the floor stays quiet for the unruled owner (Db) edit.
    [Test]
    public async Task A_setByProp_or_unlink_link_of_an_unreadable_member_is_rejected()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    children set of Node
                Node
                    label text
            access
                Node
                    create where false
                    delete where false
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            var sessions = new ClientSessionStore();
            var session = sessions.Create(); // anonymous — no principal to satisfy any condition
            var ws = new WsHandler(store, desc, sessions);

            var childrenSetId = ((SetValue)store.ReadNode(NodePath.Root.Field("children"))!).Id;
            store.CommitBatch(
                [
                    new CommitCreate(-1, "Node", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("memberA") })),
                ],
                [ new SetLinkMutation(childrenSetId, -1) ]);
            var aId = store.ReadExtent("Node").First(kv => ((TextValue)kv.Value.Fields["label"]).Text == "memberA").Key;

            // setByProp link of the existing (unreadable) member → denied (member `create` floor fails).
            var linkReply = ws.ProcessMessage($$"""
                {
                  "op": "commit",
                  "clientId": "{{session.Id}}",
                  "edits": [],
                  "creates": [],
                  "relations": [
                    { "kind": "setByProp", "parentId": 1, "prop": "children", "childId": {{aId}} }
                  ]
                }
                """);
            using var linkJson = JsonDocument.Parse(linkReply);
            await Assert.That(linkJson.RootElement.TryGetProperty("error", out _)).IsTrue();

            // setUnlink of the existing member → denied (member `delete` floor fails; no owner handle —
            // mirrors the former live `arrayRemove`, which floored on `delete` of the member).
            var unlinkReply = ws.ProcessMessage($$"""
                {
                  "op": "commit",
                  "clientId": "{{session.Id}}",
                  "edits": [],
                  "creates": [],
                  "relations": [
                    { "kind": "setUnlink", "setId": {{childrenSetId}}, "childId": {{aId}} }
                  ]
                }
                """);
            using var unlinkJson = JsonDocument.Parse(unlinkReply);
            await Assert.That(unlinkJson.RootElement.TryGetProperty("error", out _)).IsTrue();

            // setUnlinkByProp of the existing member → denied (member `delete` floor fails; owner edit of
            // the unruled Db passes, but the member gate still rejects the whole commit).
            var unlinkByPropReply = ws.ProcessMessage($$"""
                {
                  "op": "commit",
                  "clientId": "{{session.Id}}",
                  "edits": [],
                  "creates": [],
                  "relations": [
                    { "kind": "setUnlinkByProp", "parentId": 1, "prop": "children", "childId": {{aId}} }
                  ]
                }
                """);
            using var unlinkByPropJson = JsonDocument.Parse(unlinkByPropReply);
            await Assert.That(unlinkByPropJson.RootElement.TryGetProperty("error", out _)).IsTrue();

            // Nothing was applied: memberA is STILL linked in Db.children.
            var children = (SetValue)store.ReadNode(NodePath.Root.Field("children"))!;
            await Assert.That(children.Members.ContainsKey(aId)).IsTrue();
        }
        finally
        {
            if (File.Exists(dataPath)) File.Delete(dataPath);
            CleanupLogGenesis(dataPath);
        }
    }

    [Test]
    public async Task A_setByProp_link_of_a_readable_existing_member_is_allowed()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    children set of Node
                Node
                    label text
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            var sessions = new ClientSessionStore();
            var session = sessions.Create();
            var ws = new WsHandler(store, desc, sessions);

            var childrenSetId = ((SetValue)store.ReadNode(NodePath.Root.Field("children"))!).Id;
            store.CommitBatch(
                [
                    new CommitCreate(-1, "Node", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("memberA") })),
                ],
                [ new SetLinkMutation(childrenSetId, -1) ]);
            var aId = store.ReadExtent("Node").First(kv => ((TextValue)kv.Value.Fields["label"]).Text == "memberA").Key;

            // Dormant floor (no rules): linking an existing, readable member via setByProp succeeds.
            var replyText = ws.ProcessMessage($$"""
                {
                  "op": "commit",
                  "clientId": "{{session.Id}}",
                  "edits": [],
                  "creates": [],
                  "relations": [
                    { "kind": "setByProp", "parentId": 1, "prop": "children", "childId": {{aId}} }
                  ]
                }
                """);

            using var reply = JsonDocument.Parse(replyText);
            await Assert.That(reply.RootElement.TryGetProperty("error", out _)).IsFalse();
        }
        finally
        {
            if (File.Exists(dataPath)) File.Delete(dataPath);
            CleanupLogGenesis(dataPath);
        }
    }

    // T2.5: end-to-end proof that a commit can create a FRESH owner (a NEGATIVE temp id) AND link an
    // EXISTING object into that owner's SET property via a `setByProp` relation whose `ownerId` is the
    // negative temp id — passed through VERBATIM (HandleCommit does NOT resolve a negative owner server-side;
    // the store's apply arm resolves it against the in-batch `creates`). This exercises the exact path the
    // designer re-parent flow relies on: a freshly-minted wrapper receives an existing node as a child within
    // the SAME commit. Mirrors the T1 StoreConcurrencyTests "wrap" (atomic set move) scenario, but driven
    // through WsHandler/HandleCommit so the wire → session → store hop is covered, not just the store batch.
    [Test]
    public async Task A_commit_with_a_negative_owner_setByProp_links_an_existing_member_into_a_fresh_wrapper()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    nodes set of Item
                Item
                    label text
                    children set of Item
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            var sessions = new ClientSessionStore();
            var session = sessions.Create();
            var ws = new WsHandler(store, desc, sessions);

            // Seed an EXISTING member N into Db.nodes (so it has a real id AND a previous parent set).
            var nodesSetId = ((SetValue)store.ReadNode(NodePath.Root.Field("nodes"))!).Id;
            store.CommitBatch(
                [ new CommitCreate(-1, "Item", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("existingChild") })) ],
                [ new SetLinkMutation(nodesSetId, -1) ]);
            var nId = store.ReadExtent("Item").Single().Key;

            // ONE commit through the WS handler:
            //   • creates a FRESH wrapper `w` (temp id -1, type Item), typed by the `set` link into Db.nodes;
            //   • setByProp with ownerId:-1 (the FRESH wrapper, a NEGATIVE temp id) links N into w.children;
            //   • setUnlink pulls N out of its previous parent (Db.nodes) so the re-parent is observable.
            var replyText = ws.ProcessMessage($$"""
                {
                  "op": "commit",
                  "clientId": "{{session.Id}}",
                  "edits": [],
                  "creates": [
                    { "tempId": -1, "value": { "props": { "label": { "type": "text", "value": "wrapper" } } } }
                  ],
                  "relations": [
                    { "kind": "setByProp", "parentId": -1, "prop": "children", "childId": {{nId}} },
                    { "kind": "setUnlink", "setId": {{nodesSetId}}, "childId": {{nId}} },
                    { "kind": "set", "setId": {{nodesSetId}}, "childId": -1 }
                  ]
                }
                """);

            using var reply = JsonDocument.Parse(replyText);
            await Assert.That(reply.RootElement.TryGetProperty("error", out _)).IsFalse();

            // The FRESH wrapper resolved to a real (positive) id and is the only other Item besides N.
            var wrapperId = store.ReadExtent("Item")
                .Single(kv => ((TextValue)kv.Value.Fields["label"]).Text == "wrapper").Key;
            await Assert.That(wrapperId).IsGreaterThan(0); // temp id -1 was resolved to a real id

            // N is reachable via the FRESH wrapper's `children` set — the NEGATIVE owner ref worked end-to-end.
            var wrapperChildren = (SetValue)store.ReadExtent("Item")[wrapperId].Fields["children"];
            await Assert.That(wrapperChildren.Members.ContainsKey(nId)).IsTrue();

            // N is NO LONGER in its previous parent (Db.nodes); the wrapper took its place there.
            var nodesAfter = (SetValue)store.ReadNode(NodePath.Root.Field("nodes"))!;
            await Assert.That(nodesAfter.Members.ContainsKey(nId)).IsFalse();
            await Assert.That(nodesAfter.Members.ContainsKey(wrapperId)).IsTrue();

            // The whole re-parent is one consistent changeset.
            await Assert.That(((JsonFileInstanceStore)store).Fsck()).IsTrue();
        }
        finally
        {
            if (File.Exists(dataPath)) File.Delete(dataPath);
            CleanupLogGenesis(dataPath);
        }
    }

    private static void CleanupLogGenesis(string dataPath)
    {
        var logPath = AppPaths.LogPathForDataPath(dataPath);
        var genesisPath = AppPaths.GenesisPathForDataPath(dataPath);
        if (File.Exists(logPath)) File.Delete(logPath);
        if (File.Exists(genesisPath)) File.Delete(genesisPath);
    }
}
