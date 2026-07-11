using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

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
        try
        {
            var child = store.CreateObject("Node", new ObjectValue(new Dictionary<string, NodeValue>
                { ["name"] = new TextValue("child") }));
            store.AddToSet(NodePath.Root.Field("nodes"), child);
            var root = (ObjectValue)store.ReadNode(NodePath.Root)!;
            var nodes = (SetValue)root.Fields["nodes"];
            var replyText = new WsHandler(store, desc).ProcessMessage($$"""
                { "op":"arrayAdd", "setId":{{nodes.Id}}, "tempId":-1, "typeName":"Node",
                  "value":{ "props":{ "name":{ "type":"text", "value":"wrapper" },
                    "children":{ "type":"array", "items":[{ "refId":{{child}} }] } } } }
                """);
            using var reply = JsonDocument.Parse(replyText);
            var wrapper = reply.RootElement.GetProperty("newId").GetInt32();

            await Assert.That(reply.RootElement.GetProperty("newVersion").GetInt32()).IsEqualTo(store.CurrentVersion);
            var storedWrapper = store.ReadById(wrapper)!.Value;
            await Assert.That(((SetValue)storedWrapper.Fields.Fields["children"]).Members.ContainsKey(child)).IsTrue();
            await Assert.That(store.ReadExtent("Node").Count).IsEqualTo(2);

            var count = store.ReadExtent("Node").Count;
            var rejected = new WsHandler(store, desc).ProcessMessage($$"""
                { "op":"arrayAdd", "setId":{{nodes.Id}}, "tempId":-2, "typeName":"Node",
                  "value":{ "props":{ "name":{ "type":"text", "value":"orphan" },
                    "children":{ "type":"array", "items":[{ "refId":999999 }] } } } }
                """);
            await Assert.That(rejected).Contains("No object with id 999999");
            await Assert.That(store.ReadExtent("Node").Count).IsEqualTo(count);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
