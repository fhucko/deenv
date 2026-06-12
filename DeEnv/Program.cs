using DeEnv.Designer;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Engine.Internal;
using GenHTTP.Modules.Layouting;
using GenHTTP.Modules.Practices;
using GenHTTP.Modules.Websockets;

// ── Run mode ───────────────────────────────────────────────────────────────────
//
// Selected by a `--mode` arg (set per Visual Studio launch profile — see
// Properties/launchSettings.json). The instance runtime is untouched: modes only
// pick which schema + data files it runs on.
//
//   instance  → run instance.app + instance-data.json  (the built app)
//   designer  → run meta.app     + designer-data.json  (author schemas as data)
//   export    → project designer data into instance.app, then exit (the bridge)

var baseDir = AppContext.BaseDirectory;
var instanceSchema = Path.Combine(baseDir, "instance.app");
var instanceData   = Path.Combine(baseDir, "instance-data.json");
var metaSchema     = Path.Combine(baseDir, "meta.app");
var designerData   = Path.Combine(baseDir, "designer-data.json");

var mode = ModeArg(args);

if (mode == "export")
{
    SchemaBridge.Export(metaSchema, designerData, instanceSchema, instanceData);
    Console.WriteLine($"Exported designer schema → {instanceSchema} (instance data reset).");
    return;
}

var (schemaPath, dataPath) = mode == "designer"
    ? (metaSchema, designerData)
    : (instanceSchema, instanceData);

// TEMPORARY (testing scaffolding — remove later): first run of Designer mode opens
// on the current instance schema instead of a blank slate (no-op once the designer
// has its own types).
if (mode == "designer")
    SchemaBridge.SeedDesignerData(metaSchema, designerData, instanceSchema);

// ── Host the instance runtime on the selected schema + data ────────────────────

var description = InstanceDescriptionLoader.LoadFile(schemaPath);
IInstanceStore store = new JsonFileInstanceStore(dataPath, description);
var app = InstanceApp.Build(store, description);

Console.WriteLine($"Running in '{mode}' mode on {schemaPath}.");

await Host.Create()
          .Handler(app)
          // Plain HTTP: no HTTPS endpoint, so don't upgrade/redirect.
          .Defaults(secureUpgrade: false, strictTransport: false)
          .Port(8080)
          .RunAsync();

// Parse `--mode <value>`; defaults to "instance". Unknown values fall through to
// the instance branch above.
static string ModeArg(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] is "--mode" or "-m")
            return args[i + 1].ToLowerInvariant();
    return "instance";
}
