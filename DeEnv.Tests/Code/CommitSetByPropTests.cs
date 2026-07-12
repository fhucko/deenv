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

    private static void CleanupLogGenesis(string dataPath)
    {
        var logPath = AppPaths.LogPathForDataPath(dataPath);
        var genesisPath = AppPaths.GenesisPathForDataPath(dataPath);
        if (File.Exists(logPath)) File.Delete(logPath);
        if (File.Exists(genesisPath)) File.Delete(genesisPath);
    }
}
