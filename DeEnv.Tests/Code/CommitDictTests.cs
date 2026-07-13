using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// T6a.1: HandleCommit must recognize the `dictAdd` commit relation — the wire-accepted counterpart of
// DictWriteMutation (formerly server-only). A `dictAdd` relation writes ONE scalar dict entry on the owner's
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
                    { "kind": "dictAdd", "owner": 1, "prop": "meta", "key": "theme", "value": { "type": "text", "value": "dark" } }
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
                  "relations": [ { "kind": "dictAdd", "owner": 1, "prop": "meta", "key": "theme", "value": { "type": "text", "value": "dark" } } ]
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
                  "relations": [ { "kind": "dictAdd", "owner": 1, "prop": "title", "key": "k", "value": { "type": "text", "value": "v" } } ]
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
                  "relations": [ { "kind": "dictAdd", "owner": 1, "prop": "meta", "key": "theme", "value": { "type": "text", "value": "dark" } } ]
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

    [Test]
    public async Task A_commit_with_a_dict_relation_writes_an_object_entry_and_rewrites_it_whole()
    {
        // T6b-4a: object dictionary entries (dict of Config) route through the SAME dictAdd relation as
        // scalar entries — the wire value is the { props: {...} } shape a commit create ships. Editing a
        // field is a whole-entry rewrite (dictAdd re-issues the entry), mirroring the model (a dict entry IS
        // a value). dictRemove drops the entry.
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    configs dict of Config by text
                Config
                    name text
                    port int
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            var sessions = new ClientSessionStore();
            var session = sessions.Create();
            var ws = new WsHandler(store, desc, sessions);

            var addReply = ws.ProcessMessage($$"""
                {
                  "op": "commit", "clientId": "{{session.Id}}", "edits": [], "creates": [],
                  "relations": [
                    { "kind": "dictAdd", "owner": 1, "prop": "configs", "key": "api",
                      "value": { "props": { "name": { "type": "text", "value": "Api" }, "port": { "type": "int", "value": 8080 } } } }
                  ]
                }
                """);
            using (var add = JsonDocument.Parse(addReply))
                await Assert.That(add.RootElement.TryGetProperty("error", out _)).IsFalse();

            var dict = (DictionaryValue)store.ReadById(1).Value.Fields.Fields["configs"];
            await Assert.That(dict.Entries.TryGetValue(new TextValue("api"), out var apiVal)).IsTrue();
            var api = (ObjectValue)apiVal;
            await Assert.That(((TextValue)api.Fields["name"]).Text).IsEqualTo("Api");
            await Assert.That(((IntValue)api.Fields["port"]).Value).IsEqualTo(8080);

            // Whole-entry rewrite: re-issue the same key with new field values (name unchanged, port bumped).
            // The previous object is unreferenced and swept by the batch GC.
            var rewriteReply = ws.ProcessMessage($$"""
                {
                  "op": "commit", "clientId": "{{session.Id}}", "edits": [], "creates": [],
                  "relations": [
                    { "kind": "dictAdd", "owner": 1, "prop": "configs", "key": "api",
                      "value": { "props": { "name": { "type": "text", "value": "Api" }, "port": { "type": "int", "value": 9090 } } } }
                  ]
                }
                """);
            using (var rw = JsonDocument.Parse(rewriteReply))
                await Assert.That(rw.RootElement.TryGetProperty("error", out _)).IsFalse();

            var api2 = (ObjectValue)((DictionaryValue)store.ReadById(1).Value.Fields.Fields["configs"])
                .Entries[new TextValue("api")];
            await Assert.That(((IntValue)api2.Fields["port"]).Value).IsEqualTo(9090);
            // Exactly ONE entry remains under the "api" key (the rewrite replaced, not appended).
            await Assert.That(
                ((DictionaryValue)store.ReadById(1).Value.Fields.Fields["configs"]).Entries.Count).IsEqualTo(1);

            // dictRemove drops the entry.
            var rmReply = ws.ProcessMessage($$"""
                {
                  "op": "commit", "clientId": "{{session.Id}}", "edits": [], "creates": [],
                  "relations": [ { "kind": "dictRemove", "owner": 1, "prop": "configs", "key": "api" } ]
                }
                """);
            using (var rm = JsonDocument.Parse(rmReply))
                await Assert.That(rm.RootElement.TryGetProperty("error", out _)).IsFalse();
            var dictAfter = (DictionaryValue)store.ReadById(1).Value.Fields.Fields["configs"];
            await Assert.That(dictAfter.Entries.ContainsKey(new TextValue("api"))).IsFalse();
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
