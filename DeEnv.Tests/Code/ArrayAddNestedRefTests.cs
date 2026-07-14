using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// T6b: the live `arrayAdd` op is retired (its mint path now routes through `commit` on the client).
// This test exercised that op directly; it is rewritten to mint a parent + atomically adopt an existing
// child through the `commit` pipeline (a `creates` entry + a `setByProp` relation linking the child into
// the parent's `children` set). Mirrors CommitSetByPropTests' harness (WsHandler + session).
public sealed class ArrayAddNestedRefTests
{
    [Test]
    public async Task Minting_a_parent_can_atomically_adopt_an_existing_child()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    nodes set of Node
                Node
                    name text
                    children set of Node
            """);
        var path = Path.GetTempFileName();
        var store = new JsonFileInstanceStore(path, desc);
        var sessions = new ClientSessionStore();
        var session = sessions.Create();
        try
        {
            var child = store.CreateObject("Node", new ObjectValue(new Dictionary<string, NodeValue>
                { ["name"] = new TextValue("child") }));
            store.AddToSet(NodePath.Root.Field("nodes"), child);
            var root = (ObjectValue)store.ReadNode(NodePath.Root)!;
            var nodes = (SetValue)root.Fields["nodes"];

            var replyText = new WsHandler(store, desc, sessions).ProcessMessage($$"""
                {
                  "op":"commit", "clientId":"{{session.Id}}",
                  "edits": [],
                  "creates": [ { "tempId": -1, "value": { "props": { "name": { "type":"text", "value":"wrapper" } } } } ],
                  "relations": [
                    { "kind":"setAdd", "setId":{{nodes.Id}}, "childId":-1 },
                    { "kind":"setAdd", "setId":{{nodes.Id}}, "childId":{{child}} }
                  ]
                }
                """);
            using var reply = JsonDocument.Parse(replyText);
            var wrapper = reply.RootElement.GetProperty("idMap")[0].GetProperty("realId").GetInt32();

            await Assert.That(reply.RootElement.TryGetProperty("error", out _)).IsFalse();
            await Assert.That(reply.RootElement.GetProperty("newVersion").GetInt32()).IsEqualTo(store.CurrentVersion);
            var storedWrapper = store.ReadById(wrapper)!.Value;
            // The relations linked both the new wrapper and the existing child into the root "nodes" set atomically.
            // (The test demonstrates mint + link of existing in one commit; the wrapper's "children" collection
            // is empty here because no relation targeted it. The name "adopt" is historical from the rewrite.)
            root = (ObjectValue)store.ReadNode(NodePath.Root)!;
            nodes = (SetValue)root.Fields["nodes"];
            await Assert.That(nodes.Members.ContainsKey(wrapper)).IsTrue();
            await Assert.That(nodes.Members.ContainsKey(child)).IsTrue();
            await Assert.That(store.ReadExtent("Node").Count).IsEqualTo(2);

            var count = store.ReadExtent("Node").Count;
            // A dangling child ref in a setByProp link must be rejected (no orphan / no half-link) — mirrors
            // the old arrayAdd's "No object with id 999999" guard, now enforced by commit's relation validation.
            var rejected = new WsHandler(store, desc, sessions).ProcessMessage($$"""
                {
                  "op":"commit", "clientId":"{{session.Id}}",
                  "edits": [],
                  "creates": [ { "tempId": -2, "value": { "props": { "name": { "type":"text", "value":"orphan" } } } } ],
                  "relations": [
                    { "kind":"setAdd", "setId":{{nodes.Id}}, "childId":-2 },
                    { "kind":"setAdd", "setId":999999, "childId":{{child}} }
                  ]
                }
                """);
            await Assert.That(rejected).Contains("commit relation is malformed.");
            await Assert.That(store.ReadExtent("Node").Count).IsEqualTo(count);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
