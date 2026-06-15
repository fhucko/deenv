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
          "instances": [
            { "app": "alpha", "appPort": 8080, "infraPort": 8081 },
            { "app": "beta",  "appPort": 8082, "infraPort": 8083 },
            { "app": "gamma", "appPort": 8084, "infraPort": 8085 }
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
          "instances": [
            { "id": 5, "app": "alpha", "appPort": 8080, "infraPort": 8081 },
            { "app": "beta",  "appPort": 8082, "infraPort": 8083 },
            { "id": 2, "app": "gamma", "appPort": 8084, "infraPort": 8085 }
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

    private static Registry ReadInline(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "deenv-registry-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        try { return RegistryReader.Read(path); }
        finally { File.Delete(path); }
    }
}
