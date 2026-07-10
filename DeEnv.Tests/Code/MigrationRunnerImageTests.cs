using DeEnv.Code;
using DeEnv.Instance;
using DeEnv.Storage;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Direct unit coverage for MigrationRunner.ScalarForDeclared's "image" arm (review batch item 8,
// docs/plans/assets-design.md §4) — a cheap, kernel/designer/WS-free alternative to Publish.feature's
// full end-to-end migration scenarios (which need a running kernel + designer + a real publish round
// trip to exercise a migration fn at all). Calls MigrationRunner.Run directly over a hand-built
// StoreDoc: `Db` itself is the migrated extent (JsonFileInstanceStore.BuildInitialDoc seeds Db into
// Extents too), so no nested object type/set/collection wiring is needed — the smallest document that
// can carry an `image` field through a migration.
public sealed class MigrationRunnerImageTests
{
    private const string Schema = """
        types
            Db
                photo image
        """;

    [Test]
    public async Task A_migration_writing_a_new_value_to_an_image_field_succeeds()
    {
        var desc = InstanceDescriptionLoader.Load(Schema);
        var oldDoc = DbWithPhoto("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png");
        var newDoc = DbWithPhoto("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png");
        var writes = new List<LogWrite>();

        // Pre-fix (ScalarForDeclared with no "image" arm) this threw "type image are not supported
        // yet." — the exact gap the design doc's §4 flags as a "shouldn't ship" cheap-gap.
        var report = MigrationRunner.Run(
            "fn Db(old)\n    new.photo = \"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.png\"",
            commitId: 1, message: "rehash photo", oldDoc, desc, newDoc, desc, writes);

        await Assert.That(report.ObjectsMigrated).IsEqualTo(1);
        var stored = newDoc.Extents["Db"][1].Fields["photo"];
        await Assert.That(stored).IsTypeOf<StoredLeaf>();
        var leaf = (StoredLeaf)stored;
        await Assert.That(leaf.Scalar).IsTypeOf<TextValue>();
        await Assert.That(((TextValue)leaf.Scalar).Text)
            .IsEqualTo("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.png");
        // The harvested log write carries the SAME text-shaped leaf a normal edit would — no bytes,
        // no special value-kind (assets-design.md's whole-image invariant).
        await Assert.That(writes.Count).IsEqualTo(1);
        var write = (FieldWrite)writes[0];
        await Assert.That(write.Prop).IsEqualTo("photo");
        await Assert.That(((TextValue)((StoredLeaf)write.New!).Scalar).Text)
            .IsEqualTo("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.png");
    }

    private static StoreDoc DbWithPhoto(string hash) => new()
    {
        NextId = 2,
        Root = new StoredRef("Db", 1),
        Extents = { ["Db"] = new() { [1] = new StoredObject("Db", 1, new() { ["photo"] = new StoredLeaf(new TextValue(hash)) }) } },
    };
}
