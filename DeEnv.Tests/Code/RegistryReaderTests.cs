using DeEnv.Kernel;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Code;

// The migration-forgiveness seam in RegistryReader.Read: a hand-written / legacy kernel.json without
// `id` fields still works — every entry is read back with a UNIQUE assigned id (its address for
// clone/delete/publish). Cheap to test directly (no host start / port binding): write a registry
// file, read it, inspect the ids.
public sealed class RegistryReaderTests
{
    // A kernel.json with NO ids: every entry gets a unique, non-zero id, assigned deterministically
    // by file order (1, 2, 3) — so a legacy registry becomes individually addressable.
    [Test]
    public async Task A_registry_without_ids_is_read_back_with_unique_assigned_ids()
    {
        var registry = ReadInline("""
        {
          "appPort": 8080,
          "assetPort": 8081,
          "instances": [
            { "app": "alpha" },
            { "app": "beta" },
            { "app": "gamma" }
          ]
        }
        """);

        var ids = registry.Instances.Select(e => e.Id).ToList();
        // Assigned deterministically by file order: 1, 2, 3 (index access, so the claim is positional).
        await Assert.That(ids[0]).IsEqualTo(1);
        await Assert.That(ids[1]).IsEqualTo(2);
        await Assert.That(ids[2]).IsEqualTo(3);
        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count);     // all unique
        await Assert.That(ids).DoesNotContain(0);                           // none left unassigned
    }

    // A MIXED registry (some explicit ids, some missing): explicit ids are kept, and the missing one
    // is assigned AFTER the max explicit id (so it never collides with one the operator pinned).
    [Test]
    public async Task Missing_ids_are_assigned_after_the_max_explicit_id()
    {
        var registry = ReadInline("""
        {
          "appPort": 8080,
          "assetPort": 8081,
          "instances": [
            { "id": 5, "app": "alpha" },
            { "app": "beta" },
            { "id": 2, "app": "gamma" }
          ]
        }
        """);

        var ids = registry.Instances.Select(e => e.Id).ToList();
        // Explicit ids kept in place (5 at 0, 2 at 2); the missing middle one assigned past the max
        // explicit id (5) → 6, so it never collides with an id the operator pinned.
        await Assert.That(ids[0]).IsEqualTo(5);
        await Assert.That(ids[1]).IsEqualTo(6);
        await Assert.That(ids[2]).IsEqualTo(2);
        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count);
    }

    // The reader PRESERVES the kernel-level ports (the app port + asset port). Addressing is by path,
    // so these two shared ports are the only ports there are; the reader's id-forgiveness rebuild must
    // carry them through (a bare rebuild reset them to the 8080/8081 defaults — the production bug where
    // the kernel ignored kernel.json's ports and always bound 8080/8081).
    [Test]
    public async Task The_reader_preserves_the_kernel_ports()
    {
        var registry = ReadInline("""
        {
          "appPort": 18080,
          "assetPort": 18081,
          "instances": [
            { "id": 1, "app": "alpha" }
          ]
        }
        """);

        await Assert.That(registry.AppPort).IsEqualTo(18080);
        await Assert.That(registry.AssetPort).IsEqualTo(18081);
    }

    private static Registry ReadInline(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "deenv-registry-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        try { return RegistryReader.Read(path); }
        finally { File.Delete(path); }
    }
}
