using System.Text.Json;

namespace DeEnv.Kernel;

// The instance registry — kernel-owned data naming which instances the kernel hosts and on
// which ports. It is a plain bootstrap file (kernel.json) the kernel reads WITHOUT the
// interpreter (the sanctioned bootstrap subset): the registry must exist before any instance
// runs — it is how the system is assembled — so it cannot itself be modeled inside an instance.
//
// Minimality (see DECISIONS "The self-hosted image → kernel-owned data — keep it minimal"): an
// entry carries instance identity (the app document) + its port binding, and essentially nothing
// else. The data file is DERIVED from the app stem (via AppPaths in KernelHost.SpecsFor), not
// stored here — so distinct apps get distinct stores, which is the slice's data-sovereignty
// guarantee. (A later slice that needs two instances of the SAME app with separate data grows an
// explicit data-file/name field THEN, when a slice needs it — not before.)
public sealed record RegistryEntry(string App, int AppPort, int InfraPort);

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
        return registry;
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
