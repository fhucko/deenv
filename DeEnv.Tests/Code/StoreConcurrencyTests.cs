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
            if (File.Exists(dataFile)) File.Delete(dataFile);
        }
    }
}
