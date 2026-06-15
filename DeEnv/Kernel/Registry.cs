using System.Text.Json;

namespace DeEnv.Kernel;

// The instance registry — kernel-owned data naming which instances the kernel hosts and on
// which ports. It is a plain bootstrap file (kernel.json) the kernel reads WITHOUT the
// interpreter (the sanctioned bootstrap subset): the registry must exist before any instance
// runs — it is how the system is assembled — so it cannot itself be modeled inside an instance.
//
// Minimality (see DECISIONS "The self-hosted image → kernel-owned data — keep it minimal"): an
// entry carries the instance's identity (a stable unique `Id`) + a display name (`App`) + its port
// binding, and essentially nothing else. Storage is keyed by the ID: the schema + data files live
// under instances/<id>/ (AppPaths.SchemaPathForId/DataPathForId, via KernelHost.SpecsFor), NOT
// derived from a name — so `App` is a pure display LABEL, used for nothing functional, and every
// instance gets its own store by virtue of its distinct id (two instances with the same name still
// have separate stores).
//
// `Id` is that stable unique address: every hosted instance has one, and clone/delete/publish address
// an instance BY it (the old model — created = id-dir number, boot = 0 — couldn't tell two boot
// instances apart). An entry written without an id (Id == 0, the unassigned sentinel) gets one
// assigned deterministically on read, so an id-less hand-edited registry still ends up uniquely
// addressed — provided its app files already live under instances/<id>/ (resolution is purely by id).
public sealed record RegistryEntry(int Id, string App, int AppPort, int InfraPort);

public sealed record Registry(IReadOnlyList<RegistryEntry> Instances);

// The shared bootstrap JSON shape for kernel.json: camelCase + case-insensitive property names,
// with comments and trailing commas allowed when reading a hand-edited file. camelCase matches the
// rest of the project's JSON (see the serialization-style decision) without coupling to SchemaJson's
// options. Reader and writer share it so a round-tripped registry keeps its shape.
internal static class RegistryJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };
}

// Reads the registry from kernel.json. Deliberately a tiny, dependency-free reader (System.Text.Json
// only): the bootstrap floor under the interpreter, kept separate from the model/wire serialization
// (SchemaJson) so the kernel does not depend on the object model to find its instances.
public static class RegistryReader
{
    public static Registry Read(string path)
    {
        if (!File.Exists(path))
            throw new KernelConfigException($"Kernel registry '{path}' not found.");

        Registry? registry;
        try
        {
            registry = JsonSerializer.Deserialize<Registry>(File.ReadAllText(path), RegistryJson.Options);
        }
        catch (JsonException ex)
        {
            throw new KernelConfigException($"Kernel registry '{path}' is not valid JSON: {ex.Message}");
        }

        if (registry?.Instances is null || registry.Instances.Count == 0)
            throw new KernelConfigException($"Kernel registry '{path}' lists no instances.");

        // Forgive a missing id: assign a unique one to any entry with Id == 0 (the unassigned
        // sentinel), so an id-less hand-edited registry is still uniquely addressed. Number unassigned
        // entries deterministically by file order, AFTER the max explicit id, so they never collide
        // with an id the operator pinned. The committed kernel.json + fixtures carry explicit ids, so
        // in practice this is a no-op. (Resolution is purely by id, so such an entry's app files must
        // already live under instances/<id>/ — this fills the id, it doesn't relocate any files.)
        var maxId = registry.Instances.Where(e => e.Id > 0).Select(e => e.Id).DefaultIfEmpty(0).Max();
        return new Registry(
            registry.Instances.Select(e => e.Id > 0 ? e : e with { Id = ++maxId }).ToList());
    }
}

// Rewrites kernel.json — the sibling of RegistryReader, sharing its JSON shape so a created
// instance's entry reads back identically. Kept the dependency-free bootstrap seam (plain
// System.Text.Json), NOT routed through IInstanceStore: the registry is bootstrap config that must
// exist before any instance runs, not instance object-model data — promoting it to a real
// (restricted) kernel-instance is a deferred slice (see DECISIONS "`create` direction"). Single
// operator, so no write-locking (deferred with concurrent-write safety).
public static class RegistryWriter
{
    public static void Write(string path, Registry registry) =>
        File.WriteAllText(path, JsonSerializer.Serialize(registry, RegistryJson.Options));
}

// The registry could not be read — missing, malformed, or empty. Program.cs reports it and exits
// non-zero: the kernel cannot assemble the system without a registry.
public sealed class KernelConfigException(string message) : Exception(message);
