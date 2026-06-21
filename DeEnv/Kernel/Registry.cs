using System.Text.Json;

namespace DeEnv.Kernel;

// The instance registry — kernel-owned data naming which instances the kernel hosts. It is a plain
// bootstrap file (kernel.json) the kernel reads WITHOUT the interpreter (the sanctioned bootstrap
// subset): the registry must exist before any instance runs — it is how the system is assembled — so
// it cannot itself be modeled inside an instance.
//
// ADDRESSING IS BY PATH: every instance is served under the kernel's single app port + single asset
// port, addressed by `/apps/<name>`. So the two kernel-level ports live on the registry's `Kernel`
// header (NOT per-instance), and a per-instance entry carries NO ports at all.
//
// Minimality (see DECISIONS "The self-hosted image → kernel-owned data — keep it minimal"): an entry
// carries the instance's identity (a stable unique `Id`) + a display name (`App`) and essentially
// nothing else. Storage is keyed by the ID: the schema + data files live under instances/<id>/
// (AppPaths.SchemaPathForId/DataPathForId, via KernelHost.SpecsFor), NOT derived from a name — so
// `App` is the display LABEL that ALSO determines the mount path (`/apps/<App>`); every instance gets
// its own store by virtue of its distinct id. (Two instances may NOT share a name now — they would
// collide on the mount path — but they still have separate stores by id.)
//
// `Id` is that stable unique address: every hosted instance has one, and clone/delete/publish address
// an instance BY it. An entry written without an id (Id == 0, the unassigned sentinel) gets one
// assigned deterministically on read, so an id-less hand-edited registry still ends up uniquely
// addressed — provided its app files already live under instances/<id>/ (resolution is purely by id).
//
// `DesignId` is the EXPLICIT reference to which design (a member of the operator IDE's `db.designs`
// set) this instance currently runs — recorded by the IDE's Apply action (sys.setDesign) and read
// back to pre-select the design dropdown. null means "no design chosen", and is omitted from
// kernel.json (see RegistryJson.Options — WhenWritingNull).
public sealed record RegistryEntry(int Id, string App, int? DesignId = null);

// The whole registry: the kernel-level shared ports (the app port + the asset port — addressing is by
// path, so these are kernel-wide, not per-instance) plus the instances. `AppPort`/`AssetPort` default
// to the committed local-dev pair (8080/8081) so a registry that omits them still boots; a deployment
// sets them explicitly. The reader fills the default when the header is absent (an id-less / port-less
// hand-edited registry still works).
public sealed record Registry(IReadOnlyList<RegistryEntry> Instances, int AppPort = 8080, int AssetPort = 8081);

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
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
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
        // Rebuild the instances (filling missing ids) but PRESERVE the kernel-level ports via `with`
        // (a bare `new Registry(instances)` would reset them to the 8080/8081 defaults — addressing is
        // by path, so the two shared ports are the only ports there are; dropping them was the bug).
        return registry with
        {
            Instances = registry.Instances.Select(e => e.Id > 0 ? e : e with { Id = ++maxId }).ToList(),
        };
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
