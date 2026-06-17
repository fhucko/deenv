using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Instance;
using DeEnv.Storage;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Code;

// Direct tests of the TYPED store model (StoreModel.cs) that replaced the raw JSON DOM
// the store used to manipulate. They pin the two properties the refactor must guarantee
// independently of the end-to-end suite:
//   1. the on-disk JSON shape is preserved (the old shape deserializes; round-trip is
//      structurally + shape-stable), so existing data files load with NO migration;
//   2. a generic walk (the GC) over a graph that holds a user field literally named
//      "type" is correct — the union + pattern match makes the old duck-typing bug
//      (reading that key as a structural tag) unrepresentable.
public sealed class StoreModelTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new StoredValueConverter() },
    };

    // A representative document exercising every value kind, including an object whose
    // `fields` carries a key literally named "type" (the value-shape that broke the old
    // GC): serialize -> deserialize is structurally equal, and the serialized shape
    // matches the documented on-disk format key-for-key.
    [Test]
    public async Task A_full_document_round_trips_with_every_value_kind()
    {
        var doc = SampleDoc();

        var json = JsonSerializer.Serialize(doc, Opts);
        var back = JsonSerializer.Deserialize<StoreDoc>(json, Opts)!;

        // Re-serializing the deserialized doc yields identical JSON (shape + keys + scalars).
        var json2 = JsonSerializer.Serialize(back, Opts);
        await Assert.That(json2).IsEqualTo(json);

        // And the documented on-disk shape is present: an extent entry is a tagged
        // "object" with typeName/id/fields; the "type"-named user field survives as a
        // leaf; the set/dict/ref forms are exactly as specified.
        var node = JsonNode.Parse(json)!.AsObject();
        var widget = node["extents"]!["Widget"]!["7"]!.AsObject();
        await Assert.That(widget["type"]!.GetValue<string>()).IsEqualTo("object");
        await Assert.That(widget["typeName"]!.GetValue<string>()).IsEqualTo("Widget");
        await Assert.That(widget["id"]!.GetValue<int>()).IsEqualTo(7);

        var typeField = widget["fields"]!["type"]!.AsObject();   // user field NAMED "type"
        await Assert.That(typeField["type"]!.GetValue<string>()).IsEqualTo("text");
        await Assert.That(typeField["value"]!.GetValue<string>()).IsEqualTo("gizmo");

        var root = node["root"]!.AsObject();
        await Assert.That(root["type"]!.GetValue<string>()).IsEqualTo("object");
        await Assert.That(node["nextId"]!.GetValue<int>()).IsEqualTo(42);
    }

    // An OLD-shape JSON literal (the exact bytes a prior version wrote) deserializes into
    // the typed model with the right kinds and scalar values — proving no migration is
    // needed for files already on disk.
    [Test]
    public async Task The_old_on_disk_json_shape_deserializes_correctly()
    {
        const string onDisk = """
        {
          "extents": {
            "Db": { "1": { "type": "object", "typeName": "Db", "id": 1, "fields": {
              "title": { "type": "text", "value": "Home" },
              "count": { "type": "int", "value": 3 },
              "items": { "type": "set", "id": 2, "members": {
                "5": { "type": "object", "typeName": "Item", "id": 5 } } },
              "byKey": { "type": "dictionary", "id": 3, "entries": {
                "alpha": { "type": "object", "typeName": "Item", "id": 5 },
                "note":  { "type": "text", "value": "hi" } } },
              "lead": { "type": "object", "typeName": "Item", "id": 5 } } } },
            "Item": { "5": { "type": "object", "typeName": "Item", "id": 5, "fields": {
              "type": { "type": "text", "value": "gizmo" } } } }
          },
          "root": { "type": "object", "typeName": "Db", "id": 1 },
          "nextId": 5
        }
        """;

        var doc = JsonSerializer.Deserialize<StoreDoc>(onDisk, Opts)!;

        await Assert.That(doc.NextId).IsEqualTo(5);
        await Assert.That(doc.Root).IsTypeOf<StoredRef>();
        await Assert.That(((StoredRef)doc.Root!).Id).IsEqualTo(1);

        var db = doc.Extents["Db"][1];
        await Assert.That(db.Fields["title"]).IsTypeOf<StoredLeaf>();
        await Assert.That(((TextValue)((StoredLeaf)db.Fields["title"]).Scalar).Text).IsEqualTo("Home");
        await Assert.That(((IntValue)((StoredLeaf)db.Fields["count"]).Scalar).Value).IsEqualTo(3);

        var items = (StoredSet)db.Fields["items"];
        await Assert.That(items.Id).IsEqualTo(2);
        await Assert.That(items.Members[5]).IsTypeOf<StoredRef>();

        var byKey = (StoredDict)db.Fields["byKey"];
        await Assert.That(byKey.Id).IsEqualTo(3);
        await Assert.That(byKey.Entries["alpha"]).IsTypeOf<StoredRef>();    // object entry
        await Assert.That(byKey.Entries["note"]).IsTypeOf<StoredLeaf>();    // scalar entry

        await Assert.That(db.Fields["lead"]).IsTypeOf<StoredRef>();

        // The Item carries a user field literally named "type" → it is a leaf, never a tag.
        var item = doc.Extents["Item"][5];
        await Assert.That(item.Fields["type"]).IsTypeOf<StoredLeaf>();
        await Assert.That(((TextValue)((StoredLeaf)item.Fields["type"]).Scalar).Text).IsEqualTo("gizmo");
    }

    // The GC (a typed mark-sweep through the store) over a graph whose reachable objects
    // include one with a "type"-named field collects correctly: an unreferenced object is
    // swept, every reachable one is kept, and the "type"-named field never derails the walk.
    [Test]
    public async Task Gc_keeps_reachable_objects_and_sweeps_unreachable_ones_past_a_type_named_field()
    {
        // Db.items (a set) holds Item 5; Item 7 is in the extent but referenced by nobody.
        // Item 5 carries a field NAMED "type" (a leaf) — the case that broke the old GC.
        var desc = Load("""
        types
            Db
                items set of Item
            Item
                type text
        """);

        var path = TempPath();
        try
        {
            var seedDoc = new StoreDoc
            {
                NextId = 9,
                Root = new StoredRef("Db", 1),
                Extents =
                {
                    ["Db"] = new()
                    {
                        [1] = new StoredObject("Db", 1, new()
                        {
                            ["items"] = new StoredSet(2, new() { [5] = new StoredRef("Item", 5) }),
                        }),
                    },
                    ["Item"] = new()
                    {
                        [5] = new StoredObject("Item", 5, new() { ["type"] = new StoredLeaf(new TextValue("gizmo")) }),
                        [7] = new StoredObject("Item", 7, new() { ["type"] = new StoredLeaf(new TextValue("orphan")) }),
                    },
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(seedDoc, Opts));

            var store = new JsonFileInstanceStore(path, desc);
            // Removing member 5 makes Item 5 unreachable; the remove runs the GC. Item 7
            // is already unreachable. The walk must pass Item 5's "type"-named field without
            // throwing (the regression) and sweep BOTH orphans, leaving an empty extent.
            store.RemoveFromSet(2, 5);

            var onDisk = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var itemPool = onDisk["extents"]!["Item"]!.AsObject();
            await Assert.That(itemPool.Count).IsEqualTo(0); // both 5 and 7 swept; no exception
        }
        finally { File.Delete(path); }
    }

    // GC keeps an object still reachable through a "type"-named field's neighbour: removing
    // one member leaves the other (and its "type"-named field) intact — the walk visits it
    // by VALUE, never by reading the "type" key.
    [Test]
    public async Task Gc_keeps_a_sibling_object_that_carries_a_type_named_field()
    {
        var desc = Load("""
        types
            Db
                items set of Item
            Item
                type text
        """);

        var path = TempPath();
        try
        {
            var seedDoc = new StoreDoc
            {
                NextId = 9,
                Root = new StoredRef("Db", 1),
                Extents =
                {
                    ["Db"] = new()
                    {
                        [1] = new StoredObject("Db", 1, new()
                        {
                            ["items"] = new StoredSet(2, new()
                            {
                                [5] = new StoredRef("Item", 5),
                                [6] = new StoredRef("Item", 6),
                            }),
                        }),
                    },
                    ["Item"] = new()
                    {
                        [5] = new StoredObject("Item", 5, new() { ["type"] = new StoredLeaf(new TextValue("gizmo")) }),
                        [6] = new StoredObject("Item", 6, new() { ["type"] = new StoredLeaf(new TextValue("widget")) }),
                    },
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(seedDoc, Opts));

            var store = new JsonFileInstanceStore(path, desc);
            store.RemoveFromSet(2, 5); // drop Item 5; Item 6 stays reachable

            var onDisk = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var itemPool = onDisk["extents"]!["Item"]!.AsObject();
            await Assert.That(itemPool.ContainsKey("5")).IsFalse();
            await Assert.That(itemPool.ContainsKey("6")).IsTrue();
            await Assert.That(itemPool["6"]!["fields"]!["type"]!["value"]!.GetValue<string>()).IsEqualTo("widget");
        }
        finally { File.Delete(path); }
    }

    // ── sample data + helpers ─────────────────────────────────────────────────────

    private static StoreDoc SampleDoc() => new()
    {
        NextId = 42,
        Root = new StoredRef("Db", 1),
        Extents =
        {
            ["Db"] = new()
            {
                [1] = new StoredObject("Db", 1, new()
                {
                    ["title"] = new StoredLeaf(new TextValue("Home")),
                    ["widgets"] = new StoredSet(2, new() { [7] = new StoredRef("Widget", 7) }),
                    ["byName"] = new StoredDict(3, new()
                    {
                        ["a"] = new StoredRef("Widget", 7),     // object entry
                        ["greeting"] = new StoredLeaf(new TextValue("hi")), // scalar entry
                    }),
                    ["lead"] = new StoredRef("Widget", 7),       // single reference
                }),
            },
            ["Widget"] = new()
            {
                [7] = new StoredObject("Widget", 7, new()
                {
                    // a user field literally named "type" — the value-shape the old GC misread
                    ["type"] = new StoredLeaf(new TextValue("gizmo")),
                    ["count"] = new StoredLeaf(new IntValue(5)),
                    ["price"] = new StoredLeaf(new DecimalValue(9.99m)),
                    ["active"] = new StoredLeaf(new BoolValue(true)),
                    ["due"] = new StoredLeaf(new DateValue(new DateOnly(2026, 6, 17))),
                    ["at"] = new StoredLeaf(new DateTimeValue(new DateTimeOffset(2026, 6, 17, 8, 30, 0, TimeSpan.Zero))),
                }),
            },
        },
    };

    private static InstanceDescription Load(string app) => AppParse.Parse(app);

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "deenv-storemodel-" + Guid.NewGuid().ToString("N") + ".json");
}
