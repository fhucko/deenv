using System.Reflection;

namespace DeEnv.Http;

// Reads the compiled TypeScript client (instance.js) embedded in the assembly.
// instance.ts → instance.js (TS build) → embedded as "DeEnv.Instance.instance.js".
// No wwwroot, no static files on disk — the JS rides inside DeEnv.dll.
public static class ClientScript
{
    public const string ResourceName = "DeEnv.Instance.instance.js";

    private static readonly Lazy<string> _js = new(Load);

    public static string Js => _js.Value;

    private static string Load()
    {
        var asm = typeof(ClientScript).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded client script '{ResourceName}' not found. " +
                $"Available resources: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
