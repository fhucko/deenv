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
}
