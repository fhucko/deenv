using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Code;
using DeEnv.Designer;
using DeEnv.Instance;
using DeEnv.Storage;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Slice 2 — durable list core: parse/print, mint, seed, load order + duplicate refs,
// GC edges, thin publish reshape, password blanking per slot, ordinal ExecItem keys.
public sealed class DurableListCoreTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new StoredValueConverter() },
    };

    [Test]
    public async Task List_of_T_round_trips_in_the_app_document()
    {
        const string app = """
            types
                Db
                    tasks list of Task
                    tags list of text
                    scores list of int
                Task
                    title text
            """;
        var first = AppParse.Parse(app);
        var printed = AppPrint.Print(first);
        var second = AppParse.Parse(printed);

        var a = JsonSerializer.SerializeToNode(first, SchemaJson.Options)!;
        var b = JsonSerializer.SerializeToNode(second, SchemaJson.Options)!;
        if (!JsonNode.DeepEquals(a, b))
            await Assert.That(b.ToJsonString()).IsEqualTo(a.ToJsonString());

        await Assert.That(AppPrint.Print(second)).IsEqualTo(printed);
        await Assert.That(printed).Contains("tasks list of Task");
        await Assert.That(printed).Contains("tags list of text");
        await Assert.That(printed).Contains("scores list of int");

        var desc = InstanceDescriptionLoader.Load(app);
        var db = desc.FindType("Db")!;
        await Assert.That(db.Props!.Single(p => p.Name == "tasks").Cardinality).IsEqualTo(Cardinality.List);
        await Assert.That(db.Props!.Single(p => p.Name == "tags").Cardinality).IsEqualTo(Cardinality.List);
    }

    [Test]
    public async Task Empty_list_is_minted_on_create_with_stable_positive_id()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    items list of Item
                Item
                    title text
            """);
        var path = TempPath();
        try
        {
            var store = new JsonFileInstanceStore(path, desc);
            // Empty list is minted on the Db root (BuildFields).
            var root = store.ReadById(1)!.Value;
            await Assert.That(root.Fields.Fields["items"]).IsTypeOf<ListValue>();
            var list = (ListValue)root.Fields.Fields["items"];
            await Assert.That(list.Id).IsGreaterThan(0);
            await Assert.That(list.Items.Count).IsEqualTo(0);

            // Reload preserves the empty list id.
            var store2 = new JsonFileInstanceStore(path, desc);
            var list2 = (ListValue)store2.ReadById(1)!.Value.Fields.Fields["items"];
            await Assert.That(list2.Id).IsEqualTo(list.Id);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Seeded_list_preserves_order_and_duplicate_object_refs_on_reload()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    tasks list of Task
                Task
                    title text

            initialData
                Db 1
                    tasks: [3, 2, 3]
                Task 2
                    title: "B"
                Task 3
                    title: "A"
            """);
        var path = TempPath();
        try
        {
            var store = new JsonFileInstanceStore(path, desc);
            var list = (ListValue)store.ReadById(1)!.Value.Fields.Fields["tasks"];
            await Assert.That(list.Id).IsGreaterThan(0);
            await Assert.That(list.Items.Count).IsEqualTo(3);
            await Assert.That(((ReferenceValue)list.Items[0]).TargetId).IsEqualTo(3);
            await Assert.That(((ReferenceValue)list.Items[1]).TargetId).IsEqualTo(2);
            await Assert.That(((ReferenceValue)list.Items[2]).TargetId).IsEqualTo(3);

            // DbBridge load: ordinal keys, shared ExecObject for the duplicate id.
            var ctx = new ExecContext();
            var root = DbBridge.LoadRoot(store, desc, ctx);
            var execList = (ExecList)root.Props["tasks"];
            await Assert.That(execList.Id).IsEqualTo(list.Id);
            await Assert.That(execList.Items.Count).IsEqualTo(3);
            await Assert.That(execList.Items[0].Key).IsEqualTo(0);
            await Assert.That(execList.Items[1].Key).IsEqualTo(1);
            await Assert.That(execList.Items[2].Key).IsEqualTo(2);
            var a0 = (ExecObject)execList.Items[0].Value;
            var b1 = (ExecObject)execList.Items[1].Value;
            var a2 = (ExecObject)execList.Items[2].Value;
            await Assert.That(a0.Id).IsEqualTo(3);
            await Assert.That(b1.Id).IsEqualTo(2);
            await Assert.That(a2.Id).IsEqualTo(3);
            await Assert.That(ReferenceEquals(a0, a2)).IsTrue(); // shared instance

            // Reload from disk.
            var store2 = new JsonFileInstanceStore(path, desc);
            var list2 = (ListValue)store2.ReadById(1)!.Value.Fields.Fields["tasks"];
            await Assert.That(list2.Items.Select(i => ((ReferenceValue)i).TargetId).ToArray())
                .IsEquivalentTo(new int?[] { 3, 2, 3 });
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Foreach_over_list_with_duplicate_object_uses_ordinal_slot_keys()
    {
        // Duplicate object id must NOT collapse to one row key (plan: list uses ordinal item.Key).
        // Mirrors CodeExecutor.ExecuteTagForEach / codeExec executeTagForEach rule.
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    tasks list of Task
                Task
                    title text

            initialData
                Db 1
                    tasks: [3, 2, 3]
                Task 2
                    title: "B"
                Task 3
                    title: "A"
            """);
        var path = TempPath();
        try
        {
            var store = new JsonFileInstanceStore(path, desc);
            var list = (ExecList)DbBridge.LoadRoot(store, desc, new ExecContext()).Props["tasks"];
            await Assert.That(list.Items.Select(i => i.Key).ToArray()).IsEquivalentTo(new[] { 0, 1, 2 });
            static int RowKey(IExecCollection coll, ExecItem item) =>
                coll is ExecList ? item.Key : item.Value is ExecObject o ? o.Id : item.Key;
            var keys = list.Items.Select(i => RowKey(list, i)).ToArray();
            await Assert.That(keys).IsEquivalentTo(new[] { 0, 1, 2 });
            // Identity-only keying would collapse both id=3 slots to one key:
            var identityKeys = list.Items.Select(i => ((ExecObject)i.Value).Id).Distinct().Count();
            await Assert.That(identityKeys).IsEqualTo(2);
            await Assert.That(keys.Distinct().Count()).IsEqualTo(3);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Create_ack_collections_include_kind_for_list_and_set()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    bags set of Bag
                Bag
                    title text
                    items list of Item
                    peers set of Item
                Item
                    title text
            """);
        var path = TempPath();
        try
        {
            var store = new JsonFileInstanceStore(path, desc);
            var result = store.CommitBatch(
                [new CommitCreate(-10, "Bag", new ObjectValue(new Dictionary<string, NodeValue>()))],
                []);
            await Assert.That(result.Creates.Count).IsEqualTo(1);
            var cols = result.Creates[0].Collections;
            await Assert.That(cols.ContainsKey("items")).IsTrue();
            await Assert.That(cols.ContainsKey("peers")).IsTrue();
            await Assert.That(cols["items"].Kind).IsEqualTo("list");
            await Assert.That(cols["peers"].Kind).IsEqualTo("set");
            await Assert.That(cols["items"].Id).IsGreaterThan(0);
            await Assert.That(cols["peers"].Id).IsGreaterThan(0);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Seeded_scalar_list_preserves_order()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    tags list of text

            initialData
                Db 1
                    tags: ["c", "a", "c"]
            """);
        var path = TempPath();
        try
        {
            var store = new JsonFileInstanceStore(path, desc);
            var list = (ListValue)store.ReadById(1)!.Value.Fields.Fields["tags"];
            await Assert.That(list.Items.Count).IsEqualTo(3);
            await Assert.That(((TextValue)list.Items[0]).Text).IsEqualTo("c");
            await Assert.That(((TextValue)list.Items[1]).Text).IsEqualTo("a");
            await Assert.That(((TextValue)list.Items[2]).Text).IsEqualTo("c");
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Gc_treats_list_slots_as_edges()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    tasks list of Task
                    bag set of Task
                Task
                    title text
            """);
        var path = TempPath();
        try
        {
            // Task 5 only via list (incl. a duplicate slot); Task 6 only via set; Task 7 orphan.
            var seedDoc = new Db
            {
                NextId = 10,
                Root = new StoredRef("Db", 1),
                Extents =
                {
                    ["Db"] = new()
                    {
                        [1] = new StoredObject("Db", 1, new()
                        {
                            ["tasks"] = new StoredList(2,
                            [
                                new StoredRef("Task", 5),
                                new StoredRef("Task", 5),
                            ]),
                            ["bag"] = new StoredSet(3, new() { [6] = new StoredRef("Task", 6) }),
                        }),
                    },
                    ["Task"] = new()
                    {
                        [5] = new StoredObject("Task", 5, new() { ["title"] = new StoredLeaf(new TextValue("via-list")) }),
                        [6] = new StoredObject("Task", 6, new() { ["title"] = new StoredLeaf(new TextValue("via-set")) }),
                        [7] = new StoredObject("Task", 7, new() { ["title"] = new StoredLeaf(new TextValue("orphan")) }),
                    },
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(seedDoc, Opts));

            var store = new JsonFileInstanceStore(path, desc);
            // RemoveFromSet is the public mutation that runs CollectGarbage.
            store.RemoveFromSet(3, 6);

            var onDisk = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var pool = onDisk["extents"]!["Task"]!.AsObject();
            await Assert.That(pool.ContainsKey("5")).IsTrue();  // list edge keeps it (even after set GC)
            await Assert.That(pool.ContainsKey("6")).IsFalse(); // unlinked set member collected
            await Assert.That(pool.ContainsKey("7")).IsFalse(); // orphan swept
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task StoredList_round_trips_in_store_json()
    {
        var list = new StoredList(12,
        [
            new StoredRef("Task", 3),
            new StoredLeaf(new TextValue("hi")),
            new StoredRef("Task", 3),
        ]);
        var json = JsonSerializer.Serialize<StoredValue>(list, Opts);
        var back = JsonSerializer.Deserialize<StoredValue>(json, Opts)!;
        await Assert.That(back).IsTypeOf<StoredList>();
        var loaded = (StoredList)back;
        await Assert.That(loaded.Id).IsEqualTo(12);
        await Assert.That(loaded.Items.Count).IsEqualTo(3);
        await Assert.That(((StoredRef)loaded.Items[0]).Id).IsEqualTo(3);
        await Assert.That(((TextValue)((StoredLeaf)loaded.Items[1]).Scalar).Text).IsEqualTo("hi");
        await Assert.That(((StoredRef)loaded.Items[2]).Id).IsEqualTo(3);

        var node = JsonNode.Parse(json)!.AsObject();
        await Assert.That(node["type"]!.GetValue<string>()).IsEqualTo("list");
        await Assert.That(node["items"]!.AsArray().Count).IsEqualTo(3);
    }

    [Test]
    public async Task Thin_publish_reshapes_single_set_list()
    {
        // single → list, set → list (order-by-id), list → set (dedupe).
        var targetDesc = InstanceDescriptionLoader.Load("""
            types
                Db
                    lead list of Task
                    bag list of Task
                    queue set of Task
                Task
                    title text
            """);
        var db = new Db
        {
            NextId = 20,
            Root = new StoredRef("Db", 1),
            Extents =
            {
                ["Db"] = new()
                {
                    [1] = new StoredObject("Db", 1, new()
                    {
                        ["lead"] = new StoredRef("Task", 5),
                        ["bag"] = new StoredSet(2, new()
                        {
                            [7] = new StoredRef("Task", 7),
                            [5] = new StoredRef("Task", 5),
                        }),
                        ["queue"] = new StoredList(3,
                        [
                            new StoredRef("Task", 5),
                            new StoredRef("Task", 7),
                            new StoredRef("Task", 5),
                        ]),
                    }),
                },
                ["Task"] = new()
                {
                    [5] = new StoredObject("Task", 5, new() { ["title"] = new StoredLeaf(new TextValue("A")) }),
                    [7] = new StoredObject("Task", 7, new() { ["title"] = new StoredLeaf(new TextValue("B")) }),
                },
            },
        };

        var diff = new DesignDiff(
            TypeRenames: [], PropRenames: [], TypeAdds: [], Adds: [], Removes: [], TypeRemoves: [],
            Conversions: [],
            CardinalityChanges:
            [
                new CardinalityChange("Db", "lead", Cardinality.Single, Cardinality.List),
                new CardinalityChange("Db", "bag", Cardinality.Set, Cardinality.List),
                new CardinalityChange("Db", "queue", Cardinality.List, Cardinality.Set),
            ]);
        var writes = new List<LogWrite>();
        var result = JsonFileInstanceStore.TransformDb(db, diff, targetDesc, writes);

        var root = db.Extents["Db"][1];
        var lead = (StoredList)root.Fields["lead"];
        await Assert.That(lead.Items.Count).IsEqualTo(1);
        await Assert.That(((StoredRef)lead.Items[0]).Id).IsEqualTo(5);

        var bag = (StoredList)root.Fields["bag"];
        // order-by-id: 5 then 7
        await Assert.That(bag.Items.Select(i => ((StoredRef)i).Id).ToArray()).IsEquivalentTo(new[] { 5, 7 });

        var queue = (StoredSet)root.Fields["queue"];
        await Assert.That(queue.Members.Count).IsEqualTo(2); // deduped
        await Assert.That(queue.Members.ContainsKey(5)).IsTrue();
        await Assert.That(queue.Members.ContainsKey(7)).IsTrue();

        await Assert.That(result.UnsupportedReshapes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Password_list_slots_blank_on_load()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    secrets list of password
            """);
        var path = TempPath();
        try
        {
            var seedDoc = new Db
            {
                NextId = 5,
                Root = new StoredRef("Db", 1),
                Extents =
                {
                    ["Db"] = new()
                    {
                        [1] = new StoredObject("Db", 1, new()
                        {
                            ["secrets"] = new StoredList(2,
                            [
                                new StoredLeaf(new TextValue("hash-one")),
                                new StoredLeaf(new TextValue("hash-two")),
                            ]),
                        }),
                    },
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(seedDoc, Opts));
            var store = new JsonFileInstanceStore(path, desc);
            var root = DbBridge.LoadRoot(store, desc, new ExecContext());
            var list = (ExecList)root.Props["secrets"];
            await Assert.That(list.Items.Count).IsEqualTo(2);
            await Assert.That(((ExecText)list.Items[0].Value).Value).IsEqualTo("");
            await Assert.That(((ExecText)list.Items[1].Value).Value).IsEqualTo("");
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task TypeResolver_list_member_is_object_id_never_index()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    tasks list of Task
                    tags list of text
                Task
                    title text
            """);
        var resolver = new TypeResolver(desc);

        var member = resolver.ResolveType(NodePath.FromSegments(["tasks", "7"]));
        await Assert.That(member).IsNotNull();
        await Assert.That(member!.Type.Name).IsEqualTo("Task");
        await Assert.That(member.Cardinality).IsEqualTo(Cardinality.Single);

        // Scalar list: type-walk still descends for schema purposes, but store refuses child URLs.
        var tag = resolver.ResolveType(NodePath.FromSegments(["tags", "0"]));
        await Assert.That(tag).IsNotNull();
        await Assert.That(tag!.Type.Name).IsEqualTo("text");
    }

    [Test]
    public async Task Validator_accepts_duplicate_list_refs_and_rejects_wrong_kind()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    tasks list of Task
                Task
                    title text
            """);
        var good = new Db
        {
            NextId = 10,
            Root = new StoredRef("Db", 1),
            Extents =
            {
                ["Db"] = new()
                {
                    [1] = new StoredObject("Db", 1, new()
                    {
                        ["tasks"] = new StoredList(2,
                        [
                            new StoredRef("Task", 5),
                            new StoredRef("Task", 5),
                        ]),
                    }),
                },
                ["Task"] = new()
                {
                    [5] = new StoredObject("Task", 5, new() { ["title"] = new StoredLeaf(new TextValue("A")) }),
                },
            },
        };
        StoredDataValidator.Validate(good, desc, "good.json"); // no throw

        var bad = new Db
        {
            NextId = 10,
            Root = new StoredRef("Db", 1),
            Extents =
            {
                ["Db"] = new()
                {
                    [1] = new StoredObject("Db", 1, new()
                    {
                        ["tasks"] = new StoredSet(2, new() { [5] = new StoredRef("Task", 5) }),
                    }),
                },
                ["Task"] = new()
                {
                    [5] = new StoredObject("Task", 5, new() { ["title"] = new StoredLeaf(new TextValue("A")) }),
                },
            },
        };
        await Assert.That(() => StoredDataValidator.Validate(bad, desc, "bad.json"))
            .Throws<StoredDataException>();
    }

    [Test]
    public async Task Sys_schema_and_new_mint_empty_ExecList_for_list_props()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    tasks list of Task
                Task
                    title text
                    steps list of text
            """);
        var (_, _, _, descriptors) = GenericUi.Effective(desc);
        var executor = new CodeExecutor(store: null, descriptors: descriptors);
        var ctx = new ExecContext();
        executor.PrewarmDescriptors(ctx);

        var stepsDesc = (ExecObject)executor.ExecuteValue(
            CodeParse.ParseExpression("sys.schema(\"Task\", \"steps\")"), new ExecScope(), ctx);
        await Assert.That(((ExecText)stepsDesc.Props["baseType"]).Value).IsEqualTo("list");
        await Assert.That(((ExecText)stepsDesc.Props["element"]).Value).IsEqualTo("text");

        var draft = (ExecObject)executor.ExecuteValue(
            CodeParse.ParseExpression("sys.new(sys.schema(\"Task\"))"), new ExecScope(), ctx);
        await Assert.That(draft.Props["steps"]).IsTypeOf<ExecList>();
        var steps = (ExecList)draft.Props["steps"];
        await Assert.That(steps.Items.Count).IsEqualTo(0);
        await Assert.That(steps.ElementTypeName).IsEqualTo("text");
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "deenv-list-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var logPath = AppPaths.LogPathForDataPath(path);
        var genesisPath = AppPaths.GenesisPathForDataPath(path);
        if (File.Exists(logPath)) File.Delete(logPath);
        if (File.Exists(genesisPath)) File.Delete(genesisPath);
    }
}
