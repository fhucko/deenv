using System.Reflection;

namespace DeEnv.Http;

// Reads the compiled TypeScript client embedded in the assembly (.ts → .js build
// output → embedded resource). No wwwroot, no static files on disk — the JS rides
// inside DeEnv.dll.
//
//   UiJs → the code-owned UI bundle (codeExec + dt + ws + ui + workbench + init, concatenated
//          in load order), served at /js on the infra port. The interpreter comes first;
//          init.js runs last.
public static class ClientScript
{
    private static readonly Lazy<string> _uiJs = new(() => string.Join("\n;\n",
        Read("DeEnv.Instance.codeExec.js"),
        Read("DeEnv.Instance.dt.js"),
        Read("DeEnv.Instance.ws.js"),
        Read("DeEnv.Instance.ui.js"),
        // M12 W1a — the component-workbench live-instance driver (workbench.ts): a sibling module built
        // strictly from ui.ts/codeExec.ts's own exported primitives, loaded after both.
        Read("DeEnv.Instance.workbench.js"),
        Read("DeEnv.Instance.init.js")));

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
