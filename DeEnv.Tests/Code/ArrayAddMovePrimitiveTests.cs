using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// The MOVE primitive (M12 S5c wrap/unwrap — grounded 2026-07-10/11, approved as "Option A"):
// HandleArrayAdd's `refId` branch links an EXISTING object into a set instead of minting a duplicate, so
// `coll.add(existingNode)` followed by `oldColl.remove(existingNode)` composes into a Code-level MOVE that
// preserves sys.id. This is the GC-safety pin: RemoveFromSet runs CollectGarbage() synchronously, so the
// safety of the move depends entirely on ORDERING (link into the new set before unlinking from the old one).
//
// Handler-level, no browser — the `ws.ProcessMessage` idiom ParseExprsHandlerTests/ClientSessionTests already
// use. This sends the EXACT wire shape ws.ts's arrayAdd hook now emits for an id>0 object (`refId`, no
// `tempId`/`value`/`typeName`), so it proves the real production seam rather than simulating it.
public sealed class ArrayAddMovePrimitiveTests
{
    // Two ROOT sets of the SAME element type — the minimal shape a move needs (wrap/unwrap's real shape,
    // a self-referencing render-tree `children` set, is structurally the same: two sets, one element type).
    private static InstanceDescription MoveFixtureDb() => InstanceDescriptionLoader.Load("""
        types
            Db
                itemsA set of Item
                itemsB set of Item
            Item
                name text
        """);

    private static int SetIdOf(IInstanceStore store, string prop) =>
        ((SetValue)((ObjectValue)store.ReadNode(NodePath.Root)!).Fields[prop]).Id;

    [Test]
    public async Task Linking_an_existing_object_into_a_second_set_reuses_its_identity_and_mints_nothing()
    {
        var desc = MoveFixtureDb();
        var dataPath = Path.GetTempFileName();
        var store = new JsonFileInstanceStore(dataPath, desc);

        var itemId = store.CreateObject("Item", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("widget"),
        }));
        store.AddToSet(NodePath.Root.Field("itemsA"), itemId);
        var itemsBSetId = SetIdOf(store, "itemsB");

        await Assert.That(store.ReadExtent("Item").Count).IsEqualTo(1); // one Item exists before the link

        var ws = new WsHandler(store, desc);
        using var reply = JsonDocument.Parse(
            ws.ProcessMessage(
                $$"""{ "op": "arrayAdd", "id": 1, "clientId": "c1", "setId": {{itemsBSetId}}, "refId": {{itemId}} }"""));

        // The SAME id came back — no new object minted (the identity pin), and no remap is ever needed
        // (the reply carries no tempId — ws.ts's arrayAdd-remap branch is gated on tempId being present).
        await Assert.That(reply.RootElement.GetProperty("newId").GetInt32()).IsEqualTo(itemId);
        await Assert.That(reply.RootElement.TryGetProperty("tempId", out _)).IsFalse();
        await Assert.That(store.ReadExtent("Item").Count).IsEqualTo(1);

        // Both memberships are live: reachable via itemsA (the original) AND itemsB (the new link).
        var root = (ObjectValue)store.ReadNode(NodePath.Root)!;
        await Assert.That(((SetValue)root.Fields["itemsA"]).Members.ContainsKey(itemId)).IsTrue();
        await Assert.That(((SetValue)root.Fields["itemsB"]).Members.ContainsKey(itemId)).IsTrue();

        try { File.Delete(dataPath); } catch { /* best-effort */ }
    }

    [Test]
    public async Task Removing_from_the_first_set_after_linking_the_second_survives_GC_with_fields_intact()
    {
        var desc = MoveFixtureDb();
        var dataPath = Path.GetTempFileName();
        var store = new JsonFileInstanceStore(dataPath, desc);

        var itemId = store.CreateObject("Item", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("widget"),
        }));
        store.AddToSet(NodePath.Root.Field("itemsA"), itemId);
        var itemsASetId = SetIdOf(store, "itemsA");
        var itemsBSetId = SetIdOf(store, "itemsB");

        var ws = new WsHandler(store, desc);
        // The move: link into itemsB FIRST (now reachable via both sets)...
        ws.ProcessMessage(
            $$"""{ "op": "arrayAdd", "id": 1, "clientId": "c1", "setId": {{itemsBSetId}}, "refId": {{itemId}} }""");
        // ...THEN unlink from itemsA. RemoveFromSet's CollectGarbage() must find the object reachable via
        // itemsB at the moment it marks the graph — this is the ordering the safety of the whole primitive
        // rests on.
        ws.ProcessMessage(
            $$"""{ "op": "arrayRemove", "id": 2, "clientId": "c1", "setId": {{itemsASetId}}, "objectId": {{itemId}} }""");

        await Assert.That(store.ReadExtent("Item").Count).IsEqualTo(1); // survived — not swept
        var hit = store.ReadById(itemId);
        await Assert.That(hit).IsNotNull();
        await Assert.That(hit!.Value.TypeName).IsEqualTo("Item");
        await Assert.That(((TextValue)hit.Value.Fields.Fields["name"]).Text).IsEqualTo("widget"); // fields intact

        var root = (ObjectValue)store.ReadNode(NodePath.Root)!;
        await Assert.That(((SetValue)root.Fields["itemsA"]).Members.ContainsKey(itemId)).IsFalse(); // unlinked
        await Assert.That(((SetValue)root.Fields["itemsB"]).Members.ContainsKey(itemId)).IsTrue();  // reachable

        try { File.Delete(dataPath); } catch { /* best-effort */ }
    }

    [Test]
    public async Task Removing_first_without_linking_elsewhere_still_sweeps_the_wrong_order_pin()
    {
        // The danger this primitive must never quietly enable: remove-then-add (the WRONG order) still
        // loses the object — proving the safety comes from ORDERING (link before unlink), not from the
        // refId link itself being some kind of GC-immune operation.
        var desc = MoveFixtureDb();
        var dataPath = Path.GetTempFileName();
        var store = new JsonFileInstanceStore(dataPath, desc);

        var itemId = store.CreateObject("Item", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("widget"),
        }));
        store.AddToSet(NodePath.Root.Field("itemsA"), itemId);
        var itemsASetId = SetIdOf(store, "itemsA");

        store.RemoveFromSet(itemsASetId, itemId); // no other membership yet — unreachable, swept immediately

        await Assert.That(store.ReadExtent("Item").Count).IsEqualTo(0);
        await Assert.That(store.ReadById(itemId)).IsNull();

        try { File.Delete(dataPath); } catch { /* best-effort */ }
    }
}
