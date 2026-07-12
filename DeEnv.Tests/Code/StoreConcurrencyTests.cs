using DeEnv.Instance;
using DeEnv.Storage;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Code;

// A regression guard for the "persist/action" flake (a WS mutation timing out under parallel load): the
// store's atomic write (write-temp-then-File.Move) must REPLACE the data file even while another handle is
// briefly open on it. On Windows a File.Move-with-overwrite throws "Access to the path is denied" when the
// destination is open without FileShare.Delete — exactly what the test harness does while polling the file
// for a value (File.ReadAllText). Under load that collision surfaced as a server-side write failure that
// rolled the user's edit back, never persisting it. SaveRaw now retries past the transient conflict.
public sealed class StoreConcurrencyTests
{
    [Test]
    public async Task A_store_write_rides_out_a_reader_holding_the_data_file()
    {
        var dataFile = Path.Combine(Path.GetTempPath(), "deenv-share-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var desc = InstanceDescriptionLoader.Load("""
                types
                    Db
                        title text

                initialData
                    Db 1
                        title: "First"
                """);
            var store = new JsonFileInstanceStore(dataFile, desc); // seeds the file with Db 1 = "First"

            // Hold the data file the way a polling File.ReadAllText does (FileShare.Read, NOT Delete) so the
            // store's atomic File.Move-replace cannot land, then release it: the write must RETRY past the
            // conflict rather than fail it. Without the retry, WriteField throws "Access to the path is
            // denied" the instant it collides; with it, it rides the brief conflict out and persists. (On
            // non-Windows File.Move over an open handle just succeeds, so this passes trivially there — a
            // valid no-op guard for the OS where the bug exists.)
            var held = new FileStream(dataFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var writeTask = Task.Run(() => store.WriteField(1, "title", new TextValue("Renamed")));
            await Task.Delay(100); // let the write collide and start retrying while the handle is held
            held.Dispose();        // release → the write's next retry replaces the file
            await writeTask;       // re-throws here if the write FAILED instead of retrying

            await Assert.That(File.ReadAllText(dataFile)).Contains("Renamed");
        }
        finally
        {
            // The M13 append-only log + genesis snapshot ride BESIDE the data file (AppPaths) — this test's
            // WriteField call creates them, so they must be cleaned up WITH it (this test's GUID-based
            // filename can never COLLIDE across runs the way Hooks.cs's fix addresses, but leaving orphans
            // in %TEMP% on every run is still worth avoiding).
            if (File.Exists(dataFile)) File.Delete(dataFile);
            var logPath = AppPaths.LogPathForDataPath(dataFile);
            var genesisPath = AppPaths.GenesisPathForDataPath(dataFile);
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(genesisPath)) File.Delete(genesisPath);
        }
    }

    // M13 slice 3 review fix 3: a CommitBatch carrying a DictWriteMutation (the server-side vocabulary
    // sys.commitDesign uses to fold a Commit's idMap into the SAME atomic batch as the Commit's creation)
    // applies ALL-OR-NONE and logs as EXACTLY ONE entry. Proven at the store level, independent of the
    // designer wiring: create an object with a dict prop + upsert two dict entries on it in one batch, and
    // assert (a) both entries persisted, (b) the append-only log grew by exactly one entry, (c) fsck holds
    // (replay(genesis→head) == snapshot — so the create + both dict writes are one consistent changeset).
    [Test]
    public async Task A_commit_batch_with_dict_writes_applies_atomically_and_logs_one_entry()
    {
        var dataFile = Path.Combine(Path.GetTempPath(), "deenv-dictbatch-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var desc = InstanceDescriptionLoader.Load("""
                types
                    Db
                        boxes set of Box
                    Box
                        tags dict of int by text
                """);
            var store = new JsonFileInstanceStore(dataFile, desc);
            var logPath = AppPaths.LogPathForDataPath(dataFile);
            var boxesSetId = ((SetValue)store.ReadNode(NodePath.Root.Field("boxes"))!).Id;

            var linesBefore = File.Exists(logPath) ? File.ReadAllLines(logPath).Length : 0;

            // ONE batch: mint a Box (tempId -1), link it into db.boxes, and upsert two `tags` dict entries
            // ON THAT FRESH CREATE (addressed by its tempId — the owner isn't reachable by any path yet).
            const int boxTemp = -1;
            store.CommitBatch(
                [new CommitCreate(boxTemp, "Box", new ObjectValue(new Dictionary<string, NodeValue>()))],
                [
                    new SetLinkMutation(boxesSetId, boxTemp),
                    new DictWriteMutation(boxTemp, "tags", new TextValue("a"), new IntValue(11)),
                    new DictWriteMutation(boxTemp, "tags", new TextValue("b"), new IntValue(22)),
                ]);

            // (a) both dict entries persisted on the created Box.
            var box = store.ReadExtent("Box").Values.Single();
            var tags = (DictionaryValue)box.Fields["tags"];
            await Assert.That(tags.Entries.Count).IsEqualTo(2);
            await Assert.That(((IntValue)tags.Entries[new TextValue("a")]).Value).IsEqualTo(11);
            await Assert.That(((IntValue)tags.Entries[new TextValue("b")]).Value).IsEqualTo(22);

            // (b) the whole batch is ONE log entry.
            await Assert.That(File.ReadAllLines(logPath).Length).IsEqualTo(linesBefore + 1);

            // (c) fsck: replay(genesis→head) reproduces the snapshot — the create + both dict writes form one
            // internally-consistent changeset (a fresh store over the same files re-checks it too).
            await Assert.That(((JsonFileInstanceStore)store).Fsck()).IsTrue();
            await Assert.That(new JsonFileInstanceStore(dataFile, desc).Fsck()).IsTrue();
        }
        finally
        {
            if (File.Exists(dataFile)) File.Delete(dataFile);
            var logPath = AppPaths.LogPathForDataPath(dataFile);
            var genesisPath = AppPaths.GenesisPathForDataPath(dataFile);
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(genesisPath)) File.Delete(genesisPath);
        }
    }

    // M12 X1: SetLinkByPropMutation — link a member into an owner's SET addressed by (owner, prop), so a
    // child can link into a JUST-CREATED parent's nested set within ONE batch (the parent's set id isn't
    // known until minted). Proven at the store level, resolving BOTH owner kinds: a tempId owner (the fresh
    // parent Node's `children` set) AND a real-id owner (the pre-existing root Db's `nodes` set). Read the
    // whole tree back to prove it persisted, and fsck to prove the batch is one consistent changeset.
    [Test]
    public async Task A_set_link_by_prop_links_into_a_fresh_parent_and_an_existing_owner()
    {
        var dataFile = Path.Combine(Path.GetTempPath(), "deenv-setlinkbyprop-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var desc = InstanceDescriptionLoader.Load("""
                types
                    Db
                        nodes set of Node
                    Node
                        label text
                        children set of Node
                """);
            var store = new JsonFileInstanceStore(dataFile, desc);

            // ONE batch: mint a parent Node (tempId -1) + a child Node (tempId -2); link the CHILD into the
            // PARENT's `children` set (owner = a tempId — the parent's nested set is minted in this same
            // batch) and the PARENT into the EXISTING Db root's `nodes` set (owner = the real Db id 1).
            const int parentTemp = -1, childTemp = -2;
            store.CommitBatch(
                [
                    new CommitCreate(parentTemp, "Node", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("parent") })),
                    new CommitCreate(childTemp, "Node", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("child") })),
                ],
                [
                    new SetLinkByPropMutation(parentTemp, "children", childTemp),
                    new SetLinkByPropMutation(1, "nodes", parentTemp), // real-id owner: the Db root
                ]);

            // Read the tree back: db.nodes holds exactly the parent, whose `children` holds exactly the child.
            var nodes = (SetValue)store.ReadNode(NodePath.Root.Field("nodes"))!;
            var parent = nodes.Members.Values.OfType<ObjectValue>().Single();
            await Assert.That(((TextValue)parent.Fields["label"]).Text).IsEqualTo("parent");
            var children = (SetValue)parent.Fields["children"];
            var child = children.Members.Values.OfType<ObjectValue>().Single();
            await Assert.That(((TextValue)child.Fields["label"]).Text).IsEqualTo("child");

            // fsck: replay(genesis→head) reproduces the snapshot — creates + both set links are one changeset.
            await Assert.That(((JsonFileInstanceStore)store).Fsck()).IsTrue();
            await Assert.That(new JsonFileInstanceStore(dataFile, desc).Fsck()).IsTrue();
        }
        finally
        {
            if (File.Exists(dataFile)) File.Delete(dataFile);
            var logPath = AppPaths.LogPathForDataPath(dataFile);
            var genesisPath = AppPaths.GenesisPathForDataPath(dataFile);
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(genesisPath)) File.Delete(genesisPath);
        }
    }

    // M12 X1: a SetLinkByPropMutation naming a prop that is NOT a set is rejected in PRE-VALIDATION, so the
    // whole batch is refused with the store UNTOUCHED — nothing minted. CommitBatch has no rollback; its
    // all-or-none guarantee rests on the pre-validation loop throwing BEFORE the apply loop mutates. This
    // guards that the new mutation's set-prop check can never fire mid-apply and leave half-minted state.
    [Test]
    public async Task A_set_link_by_prop_on_a_non_set_prop_is_rejected_with_nothing_minted()
    {
        var dataFile = Path.Combine(Path.GetTempPath(), "deenv-setlinkbadprop-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var desc = InstanceDescriptionLoader.Load("""
                types
                    Db
                        nodes set of Node
                    Node
                        label text
                        children set of Node
                """);
            var store = new JsonFileInstanceStore(dataFile, desc);

            // `label` is a TEXT prop, not a set — linking into it must throw, and mint nothing.
            const int parentTemp = -1, childTemp = -2;
            await Assert.That(() => store.CommitBatch(
                [
                    new CommitCreate(parentTemp, "Node", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("p") })),
                    new CommitCreate(childTemp, "Node", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("c") })),
                ],
                [new SetLinkByPropMutation(parentTemp, "label", childTemp)]))
                .Throws<InvalidOperationException>();

            // Store UNTOUCHED: no Node minted (the pre-validation threw before the create loop ran).
            await Assert.That(store.ReadExtent("Node").Count).IsEqualTo(0);
            await Assert.That(((SetValue)store.ReadNode(NodePath.Root.Field("nodes"))!).Members.Count).IsEqualTo(0);
        }
        finally
        {
            if (File.Exists(dataFile)) File.Delete(dataFile);
            var logPath = AppPaths.LogPathForDataPath(dataFile);
            var genesisPath = AppPaths.GenesisPathForDataPath(dataFile);
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(genesisPath)) File.Delete(genesisPath);
        }
    }

    // T1 (transparent-client-mutations.md): an atomic set MOVE via CommitBatch — link an existing member
    // into set B and unlink it from set A in ONE batch, with link-before-unlink ordering + a single GC at
    // the end. After a clean apply the member is reachable ONLY from B (the double-membership window is
    // gone); the whole move is ONE log entry; and a malformed later mutation (an unlink from a non-existent
    // set) leaves the store document + version UNCHANGED (CommitBatch's all-or-none pre-validation guard,
    // independent of the new unlink arms). Proven at the store level, no client change.
    [Test]
    public async Task A_commit_batch_atomic_set_move_links_into_b_and_unlinks_from_a_in_one_commit()
    {
        var dataFile = Path.Combine(Path.GetTempPath(), "deenv-setmove-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var desc = InstanceDescriptionLoader.Load("""
                types
                    Db
                        nodes set of Item
                    Item
                        label text
                        children set of Item
                """);
            var store = new JsonFileInstanceStore(dataFile, desc);
            var logPath = AppPaths.LogPathForDataPath(dataFile);
            var linesBefore = File.Exists(logPath) ? File.ReadAllLines(logPath).Length : 0;

            // Seed two items in db.nodes.
            var nodesSetId = ((SetValue)store.ReadNode(NodePath.Root.Field("nodes"))!).Id;
            store.CommitBatch(
                [
                    new CommitCreate(-1, "Item", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("a") })),
                    new CommitCreate(-2, "Item", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("b") })),
                ],
                [
                    new SetLinkMutation(nodesSetId, -1),
                    new SetLinkMutation(nodesSetId, -2),
                ]);

            var ids = store.ReadExtent("Item").Keys.OrderBy(k => k).ToList();
            var aId = ids[0];
            var bId = ids[1];

            // ── THE WRAP (one batch, mirroring the designer re-parent flow): create a wrapper Item,
            //    link it into db.nodes (so it is reachable + not GC'd), AND re-parent 'a' from db.nodes
            //    into the wrapper's `children` set (unlink from nodes + link into wrapper.children). All
            //    in ONE CommitBatch with link-before-unlink ordering + a single end-of-batch GC. ──
            store.CommitBatch(
                [new CommitCreate(-3, "Item", new ObjectValue(new Dictionary<string, NodeValue> { ["label"] = new TextValue("wrapper") }))],
                [
                    new SetLinkMutation(nodesSetId, -3),          // wrapper → db.nodes (reachable)
                    new SetUnlinkMutation(nodesSetId, aId),       // 'a' leaves db.nodes  (unlink, pass 2)
                    new SetLinkByPropMutation(-3, "children", aId), // 'a' enters wrapper.children (link, pass 1)
                ]);

            // Resolve the minted wrapper id + its `children` set id.
            var wrapperId = store.ReadExtent("Item").Keys.Single(id => id != aId && id != bId);
            var wrapperChildrenSetId = ((SetValue)store.ReadExtent("Item")[wrapperId].Fields["children"]).Id;

            // After: 'a' is reachable ONLY from wrapper.children, not from db.nodes; 'b' still in db.nodes;
            // the wrapper itself is in db.nodes. No transient double-membership of 'a'.
            var nodesAfter = (SetValue)store.ReadNode(NodePath.Root.Field("nodes"))!;
            await Assert.That(nodesAfter.Members.ContainsKey(aId)).IsFalse();
            await Assert.That(nodesAfter.Members.ContainsKey(bId)).IsTrue();
            await Assert.That(nodesAfter.Members.ContainsKey(wrapperId)).IsTrue();
            var wrapperChildrenAfter = (SetValue)store.ReadExtent("Item")[wrapperId].Fields["children"];
            await Assert.That(wrapperChildrenAfter.Members.ContainsKey(aId)).IsTrue();

            // The move is ONE log entry; fsck holds (replay(genesis→head) reproduces the snapshot).
            await Assert.That(File.ReadAllLines(logPath).Length).IsEqualTo(linesBefore + 2); // seed + wrap
            await Assert.That(((JsonFileInstanceStore)store).Fsck()).IsTrue();
            await Assert.That(new JsonFileInstanceStore(dataFile, desc).Fsck()).IsTrue();

            // ── malformed-followup guard: an unlink from a non-existent set throws, leaves store untouched ──
            var verAfterMove = store.CurrentVersion;
            await Assert.That(() => store.CommitBatch(
                [],
                [new SetUnlinkMutation(999999, aId)]))
                .Throws<InvalidOperationException>();
            await Assert.That(store.CurrentVersion).IsEqualTo(verAfterMove); // version unchanged
            await Assert.That(((SetValue)store.ReadNode(NodePath.Root.Field("nodes"))!).Members.ContainsKey(bId)).IsTrue();
        }
        finally
        {
            if (File.Exists(dataFile)) File.Delete(dataFile);
            var logPath = AppPaths.LogPathForDataPath(dataFile);
            var genesisPath = AppPaths.GenesisPathForDataPath(dataFile);
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(genesisPath)) File.Delete(genesisPath);
        }
    }
}
