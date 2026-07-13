using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// T6a.1: HandleCommit must recognize the `dict` commit relation — the wire-accepted counterpart of
// DictWriteMutation (formerly server-only). A `dict` relation writes ONE scalar dict entry on the owner's
// (owner, prop) dictionary field; a `dictRemove` drops it. Both floor on an `edit` of the owner, matching
// HandleWrite's dict-entry gate. Mirrors CommitSetByPropTests' wiring (WsHandler + session harness; wire
// keys reuse ParseRelation's names — owner/prop/key/value — so the wire stays consistent).
public sealed class CommitDictTests
{
    [Test]
    public async Task A_commit_with_a_dict_relation_writes_the_entry()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    meta dict of text
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
                  "creates": [],
                  "relations": [
                    { "kind": "dict", "owner": 1, "prop": "meta", "key": "theme", "value": { "type": "text", "value": "dark" } }
                  ]
                }
                """);
            using var reply = JsonDocument.Parse(replyText);
            await Assert.That(reply.RootElement.TryGetProperty("error", out _)).IsFalse();

            var dict = (DictionaryValue)store.ReadById(1).Value.Fields.Fields["meta"];
            await Assert.That(dict.Entries.TryGetValue(new TextValue("theme"), out var v)).IsTrue();
            await Assert.That(v).IsEqualTo((NodeValue)new TextValue("dark"));
        }
        finally
        {
            if (File.Exists(dataPath)) File.Delete(dataPath);
            CleanupLogGenesis(dataPath);
        }
    }

    [Test]
    public async Task A_commit_with_dict_then_dictRemove_round_trips_the_entry()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    meta dict of text
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            var sessions = new ClientSessionStore();
            var session = sessions.Create();
            var ws = new WsHandler(store, desc, sessions);

            ws.ProcessMessage($$"""
                {
                  "op": "commit", "clientId": "{{session.Id}}", "edits": [], "creates": [],
                  "relations": [ { "kind": "dict", "owner": 1, "prop": "meta", "key": "theme", "value": { "type": "text", "value": "dark" } } ]
                }
                """);

            var before = (DictionaryValue)store.ReadById(1).Value.Fields.Fields["meta"];
            await Assert.That(before.Entries.ContainsKey(new TextValue("theme"))).IsTrue();

            var removeReply = ws.ProcessMessage($$"""
                {
                  "op": "commit", "clientId": "{{session.Id}}", "edits": [], "creates": [],
                  "relations": [ { "kind": "dictRemove", "owner": 1, "prop": "meta", "key": "theme" } ]
                }
                """);
            using var removeJson = JsonDocument.Parse(removeReply);
            await Assert.That(removeJson.RootElement.TryGetProperty("error", out _)).IsFalse();

            var after = (DictionaryValue)store.ReadById(1).Value.Fields.Fields["meta"];
            await Assert.That(after.Entries.ContainsKey(new TextValue("theme"))).IsFalse();
        }
        finally
        {
            if (File.Exists(dataPath)) File.Delete(dataPath);
            CleanupLogGenesis(dataPath);
        }
    }

    [Test]
    public async Task A_dict_relation_on_a_non_dictionary_field_is_rejected()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    title text
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
                  "op": "commit", "clientId": "{{session.Id}}", "edits": [], "creates": [],
                  "relations": [ { "kind": "dict", "owner": 1, "prop": "title", "key": "k", "value": { "type": "text", "value": "v" } } ]
                }
                """);
            using var reply = JsonDocument.Parse(replyText);
            await Assert.That(reply.RootElement.TryGetProperty("error", out _)).IsTrue();
        }
        finally
        {
            if (File.Exists(dataPath)) File.Delete(dataPath);
            CleanupLogGenesis(dataPath);
        }
    }

    [Test]
    public async Task A_dict_write_is_rejected_when_the_owner_edit_floor_denies_it()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    meta dict of text
            access
                Db
                    edit where false
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            var sessions = new ClientSessionStore();
            var session = sessions.Create(); // anonymous — no principal to satisfy any condition
            var ws = new WsHandler(store, desc, sessions);

            var replyText = ws.ProcessMessage($$"""
                {
                  "op": "commit", "clientId": "{{session.Id}}", "edits": [], "creates": [],
                  "relations": [ { "kind": "dict", "owner": 1, "prop": "meta", "key": "theme", "value": { "type": "text", "value": "dark" } } ]
                }
                """);
            using var reply = JsonDocument.Parse(replyText);
            await Assert.That(reply.RootElement.TryGetProperty("error", out _)).IsTrue();

            // Nothing was applied: the owner's dict is still empty.
            var dict = (DictionaryValue)store.ReadById(1).Value.Fields.Fields["meta"];
            await Assert.That(dict.Entries.ContainsKey(new TextValue("theme"))).IsFalse();
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
