using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// T2.1: HandleCommit must record each created object's transient (negative) id → real id in the
// session's transient-id map, mirroring HandleArrayAdd. Without this, a follow-up op the client
// fires before the commit round-trip returns (addressed by the temp id) cannot resolve to the real id.
public sealed class CommitSessionRemapTests
{
    [Test]
    public async Task A_commit_that_creates_objects_remaps_their_temp_ids_in_the_session()
    {
        var desc = InstanceContext.AtomicChangesetFixtureDb();
        var dataPath = Path.GetTempFileName();
        var store = new JsonFileInstanceStore(dataPath, desc);
        var sessions = new ClientSessionStore();
        var session = sessions.Create();
        var ws = new WsHandler(store, desc, sessions);

        var tagsSetId = ((SetValue)store.ReadNode(NodePath.Root.Field("tags"))!).Id;
        var replyText = ws.ProcessMessage($$"""
            {
              "op": "commit",
              "clientId": "{{session.Id}}",
              "edits": [],
              "creates": [
                { "tempId": -1, "value": { "props": { "label": { "type": "text", "value": "alpha" } } } },
                { "tempId": -2, "value": { "props": { "label": { "type": "text", "value": "beta"  } } } }
              ],
              "relations": [
                { "kind": "set", "setId": {{tagsSetId}}, "childId": -1 },
                { "kind": "set", "setId": {{tagsSetId}}, "childId": -2 }
              ]
            }
            """);

        using var reply = JsonDocument.Parse(replyText);
        var idMap = reply.RootElement.GetProperty("idMap");
        var real1 = idMap[0].GetProperty("realId").GetInt32();
        var real2 = idMap[1].GetProperty("realId").GetInt32();
        await Assert.That(real1).IsGreaterThan(0);
        await Assert.That(real2).IsGreaterThan(0);

        // The session now maps each temp id → its real id. ResolveId walks the same remap
        // HandleObjectPropChange/arrayRemove use to translate an inbound temp id.
        var reclaimed = sessions.Get(session.Id)!;
        await Assert.That(reclaimed.ResolveId(-1)).IsEqualTo(real1);
        await Assert.That(reclaimed.ResolveId(-2)).IsEqualTo(real2);

        try { File.Delete(dataPath); } catch { /* best-effort */ }
    }
}
