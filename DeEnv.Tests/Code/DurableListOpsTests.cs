using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Slice 3 — list ops + persistable assign: CommitBatch mutations, AppLog replay, OCC-free.
public sealed class DurableListOpsTests
{
    [Test]
    public async Task ListReplace_keeps_list_id_and_order()
    {
        var (path, desc, listId) = OpenWithSeededTasks();
        try
        {
            var store = new JsonFileInstanceStore(path, desc);
            store.CommitBatch([], [
                new ListReplaceMutation(listId, [
                    new StoredRef("Task", 3),
                    new StoredRef("Task", 2),
                    new StoredRef("Task", 3),
                ]),
            ]);
            var after = RootList(store, "tasks");
            await Assert.That(after.Id).IsEqualTo(listId);
            await Assert.That(Ids(after)).IsEquivalentTo(new[] { 3, 2, 3 });
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task ListInsert_RemoveAt_Move_work_without_list_version()
    {
        var (path, desc, listId) = OpenWithSeededTasks();
        try
        {
            var store = new JsonFileInstanceStore(path, desc);
            // Seed is [3,2,3]. Insert Task 2 at 1 → [3,2,2,3]
            store.CommitBatch([], [new ListInsertMutation(listId, 1, new StoredRef("Task", 2))]);
            await Assert.That(Ids(RootList(store, "tasks"))).IsEquivalentTo(new[] { 3, 2, 2, 3 });

            // Move 0→3: take first 3 to end → [2,2,3,3]
            store.CommitBatch([], [new ListMoveMutation(listId, 0, 3)]);
            await Assert.That(Ids(RootList(store, "tasks"))).IsEquivalentTo(new[] { 2, 2, 3, 3 });

            // RemoveAt 1 → [2,3,3]
            store.CommitBatch([], [new ListRemoveAtMutation(listId, 1)]);
            await Assert.That(Ids(RootList(store, "tasks"))).IsEquivalentTo(new[] { 2, 3, 3 });

            // Same list id throughout (no re-mint).
            await Assert.That(RootList(store, "tasks").Id).IsEqualTo(listId);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task List_mutate_replays_through_AppLog_after_restart()
    {
        var (path, desc, listId) = OpenWithSeededTasks();
        try
        {
            var store = new JsonFileInstanceStore(path, desc);
            store.CommitBatch([], [
                new ListInsertMutation(listId, 0, new StoredRef("Task", 2)),
                new ListMoveMutation(listId, 0, 2),
                new ListRemoveAtMutation(listId, 1),
            ]);
            var expected = Ids(RootList(store, "tasks"));
            var expectedId = RootList(store, "tasks").Id;

            // Fresh open rebuilds from genesis + log (replay).
            var store2 = new JsonFileInstanceStore(path, desc);
            var reloaded = RootList(store2, "tasks");
            await Assert.That(reloaded.Id).IsEqualTo(expectedId);
            await Assert.That(Ids(reloaded)).IsEquivalentTo(expected);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task ListReplace_of_scalar_list_and_password_slots()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    tags list of text
                    secrets list of password
            """);
        var path = TempPath();
        try
        {
            var store = new JsonFileInstanceStore(path, desc);
            var tagsId = ((ListValue)store.ReadById(1)!.Value.Fields.Fields["tags"]).Id;
            var secretsId = ((ListValue)store.ReadById(1)!.Value.Fields.Fields["secrets"]).Id;

            store.CommitBatch([], [
                new ListReplaceMutation(tagsId, [
                    new StoredLeaf(new TextValue("a")),
                    new StoredLeaf(new TextValue("b")),
                ]),
                new ListInsertMutation(secretsId, 0, new StoredLeaf(new TextValue("hashed-or-plain"))),
            ]);

            var tags = (ListValue)store.ReadById(1)!.Value.Fields.Fields["tags"];
            await Assert.That(tags.Id).IsEqualTo(tagsId);
            await Assert.That(tags.Items.Count).IsEqualTo(2);
            await Assert.That(((TextValue)tags.Items[0]).Text).IsEqualTo("a");
            await Assert.That(((TextValue)tags.Items[1]).Text).IsEqualTo("b");

            var secrets = (ListValue)store.ReadById(1)!.Value.Fields.Fields["secrets"];
            await Assert.That(secrets.Items.Count).IsEqualTo(1);

            var store2 = new JsonFileInstanceStore(path, desc);
            var tags2 = (ListValue)store2.ReadById(1)!.Value.Fields.Fields["tags"];
            await Assert.That(((TextValue)tags2.Items[0]).Text).IsEqualTo("a");
            await Assert.That(tags2.Id).IsEqualTo(tagsId);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Create_Design_with_nested_list_wire_shape_then_listInsert_MetaType()
    {
        // Regression: nested list props on create wire must be skipped (like set), or create fails.
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    designs set of Design
                Design
                    label text
                    types list of MetaType
                MetaType
                    name text
                    props list of MetaProp
                MetaProp
                    name text
            """);
        var path = TempPath();
        try
        {
            var store = new JsonFileInstanceStore(path, desc);
            var sessions = new ClientSessionStore();
            var session = sessions.Create();
            var ws = new WsHandler(store, desc, sessions);
            var designsSetId = ((SetValue)store.ReadNode(NodePath.Root.Field("designs"))!).Id;

            var createDesign = """
                {"op":"commit","clientId":"CLIENT","edits":[],"creates":[{
                  "tempId":-1,
                  "value":{"props":{
                    "label":{"type":"text","value":"pageorder"},
                    "types":{"type":"list","items":[]}
                  }}
                }],"relations":[{"kind":"setAdd","setId":SETID,"childId":-1}]}
                """.Replace("CLIENT", session.Id).Replace("SETID", designsSetId.ToString());
            var reply1 = ws.ProcessMessage(createDesign);
            using (var doc = JsonDocument.Parse(reply1))
            {
                if (doc.RootElement.TryGetProperty("error", out var err))
                    throw new Exception("create Design failed: " + err.GetString());
            }

            var designId = store.ReadExtent("Design").Keys.Max();
            var typesListId = ((ListValue)store.ReadById(designId)!.Value.Fields.Fields["types"]).Id;

            var createType = """
                {"op":"commit","clientId":"CLIENT","edits":[],"creates":[{
                  "tempId":-2,
                  "value":{"props":{
                    "name":{"type":"text","value":"Thing"},
                    "props":{"type":"list","items":[]}
                  }}
                }],"relations":[{"kind":"listInsert","listId":LISTID,"index":0,"value":{"type":"object","id":-2}}]}
                """.Replace("CLIENT", session.Id).Replace("LISTID", typesListId.ToString());
            var reply2 = ws.ProcessMessage(createType);
            using (var doc = JsonDocument.Parse(reply2))
            {
                if (doc.RootElement.TryGetProperty("error", out var err))
                    throw new Exception("listInsert MetaType failed: " + err.GetString());
            }

            await Assert.That(store.ReadExtent("MetaType").Values.Any(o =>
                o.Fields.GetValueOrDefault("name") is TextValue { Text: "Thing" })).IsTrue();
            await Assert.That(((ListValue)store.ReadById(designId)!.Value.Fields.Fields["types"]).Items.Count).IsEqualTo(1);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Create_plus_listInsert_in_one_commit_remaps_temp_id()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    tasks list of Task
                Task
                    title text
            """);
        var path = TempPath();
        try
        {
            var store = new JsonFileInstanceStore(path, desc);
            var listId = ((ListValue)store.ReadById(1)!.Value.Fields.Fields["tasks"]).Id;
            var result = store.CommitBatch(
                [new CommitCreate(-1, "Task", new ObjectValue(new Dictionary<string, NodeValue> {
                    ["title"] = new TextValue("fresh"),
                }))],
                [new ListInsertMutation(listId, 0, new StoredRef("Task", -1))]);

            await Assert.That(result.Creates.Count).IsEqualTo(1);
            var realId = result.Creates[0].RealId;
            await Assert.That(realId).IsGreaterThan(0);
            await Assert.That(Ids(RootList(store, "tasks"))).IsEquivalentTo(new[] { realId });
        }
        finally { Cleanup(path); }
    }

    private static (string Path, InstanceDescription Desc, int ListId) OpenWithSeededTasks()
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
        var store = new JsonFileInstanceStore(path, desc);
        var listId = ((ListValue)store.ReadById(1)!.Value.Fields.Fields["tasks"]).Id;
        return (path, desc, listId);
    }

    private static ListValue RootList(IInstanceStore store, string prop) =>
        (ListValue)store.ReadById(1)!.Value.Fields.Fields[prop];

    private static int[] Ids(ListValue list) =>
        list.Items.OfType<ReferenceValue>().Select(r => r.TargetId!.Value).ToArray();

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "deenv-list-ops-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var logPath = AppPaths.LogPathForDataPath(path);
        var genesisPath = AppPaths.GenesisPathForDataPath(path);
        if (File.Exists(logPath)) File.Delete(logPath);
        if (File.Exists(genesisPath)) File.Delete(genesisPath);
    }
}
