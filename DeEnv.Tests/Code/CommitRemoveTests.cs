using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// T3: HandleCommit must recognize the `dictRemove` commit relation kind —
//   dictRemove → DictRemoveMutation(owner,prop,key)  (drop ONE dictionary entry — mirrors dict.Remove(k))
// — commit-internal (routed through CommitBatch), never a live wire op. Mirrors CommitSetByPropTests'
// WsHandler+session harness.
//
// NOTE: the user removed the bulk `remove` (detach-from-every-edge) op from the union — in C#/JS you drop
// individual references (RefLink→null / SetUnlink / DictRemove) and GC collects the orphan; there is no
// single "detach X from everything" primitive. So only `dictRemove` ships here.
public sealed class CommitRemoveTests
{
    [Test]
    public async Task A_commit_with_dictRemove_drops_the_dictionary_entry()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    tags dict of text by text
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            var sessions = new ClientSessionStore();
            var session = sessions.Create();
            var ws = new WsHandler(store, desc, sessions);

            // Seed two dict entries on the Db root (id 1).
            store.CreateEntry(NodePath.Root.Field("tags"), new TextValue("k1"), new TextValue("v1"));
            store.CreateEntry(NodePath.Root.Field("tags"), new TextValue("k2"), new TextValue("v2"));

            var replyText = ws.ProcessMessage($$"""
                {
                  "op": "commit",
                  "clientId": "{{session.Id}}",
                  "edits": [],
                  "creates": [],
                  "relations": [
                    { "kind": "dictRemove", "owner": 1, "prop": "tags", "key": "k1" }
                  ]
                }
                """);

            using var reply = JsonDocument.Parse(replyText);
            await Assert.That(reply.RootElement.TryGetProperty("error", out _)).IsFalse();

            // k1 is gone; k2 remains.
            var tags = (DictionaryValue)store.ReadNode(NodePath.Root.Field("tags"))!;
            await Assert.That(tags.Entries.Any(e => ((TextValue)e.Key).Text == "k1")).IsFalse();
            await Assert.That(tags.Entries.Any(e => ((TextValue)e.Key).Text == "k2")).IsTrue();
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
