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

// Reads the registry from kernel.json. Deliberately a tiny, dependency-free reader (System.Text.Json
// only): the bootstrap floor under the interpreter, kept separate from the model/wire serialization
// (SchemaJson) so the kernel does not depend on the object model to find its instances.
public static class RegistryReader
{
    // Forgiving toward a hand-edited config file: camelCase + case-insensitive property names,
    // with comments and trailing commas allowed. camelCase matches the rest of the project's JSON
    // (see the serialization-style decision) without coupling to SchemaJson's options.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Registry Read(string path)
    {
        if (!File.Exists(path))
            throw new KernelConfigException($"Kernel registry '{path}' not found.");

        Registry? registry;
        try
        {
            registry = JsonSerializer.Deserialize<Registry>(File.ReadAllText(path), Options);
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

// The registry could not be read — missing, malformed, or empty. Program.cs reports it and exits
// non-zero: the kernel cannot assemble the system without a registry.
public sealed class KernelConfigException(string message) : Exception(message);
