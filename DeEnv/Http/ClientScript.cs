using System.Reflection;

namespace DeEnv.Http;

// Reads the compiled TypeScript clients embedded in the assembly (.ts → .js build
// output → embedded resource). No wwwroot, no static files on disk — the JS rides
// inside DeEnv.dll.
//
//   Js   → instance.js, the generic auto-form client, served at /js.
//   UiJs → the code-owned UI bundle (codeExec + dt + ui + init, concatenated in load
//          order), served at /ui-js. The interpreter comes first; init.js runs last.
public static class ClientScript
{
    private static readonly Lazy<string> _js = new(() => Read("DeEnv.Instance.instance.js"));

    private static readonly Lazy<string> _uiJs = new(() => string.Join("\n;\n",
        Read("DeEnv.Instance.codeExec.js"),
        Read("DeEnv.Instance.dt.js"),
        Read("DeEnv.Instance.ws.js"),
        Read("DeEnv.Instance.ui.js"),
        Read("DeEnv.Instance.init.js")));

    public static string Js => _js.Value;
    public static string UiJs => _uiJs.Value;

    private static string Read(string resource)
    {
        var asm = typeof(ClientScript).Assembly;
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Embedded client script '{resource}' not found. " +
                $"Available resources: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
