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
//   instance  → run an app document + its data file (the built app)
//   designer  → run meta.app + designer-data.json (author schemas as data)
//   export    → project designer data into instance.app, then exit (the bridge)
//
// `--app <file>` (instance mode) picks which app document to run — e.g.
// `--app crm.app`. Each app gets its own data file (<name>-data.json), so
// switching apps never mixes data. Defaults to instance.app (the todo app).

var baseDir = AppContext.BaseDirectory;
var appFile = AppArg(args);
var instanceSchema = AppPaths.SchemaPath(appFile, baseDir);
var instanceData   = AppPaths.DataPath(appFile, baseDir);
var metaSchema     = Path.Combine(baseDir, "meta.app");
var designerData   = Path.Combine(baseDir, "designer-data.json");

var mode = ModeArg(args);

IInstanceStore store;
InstanceDescription description;
try
{
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

    // ── Host the instance runtime on the selected schema + data ────────────────

    description = InstanceDescriptionLoader.LoadFile(schemaPath);
    store = new JsonFileInstanceStore(dataPath, description);
    Console.WriteLine($"Running in '{mode}' mode on {schemaPath}.");
}
catch (StoredDataException ex)
{
    // The startup guard tripped: a data file belongs to a different/older app.
    // Refuse to serve (mutations would silently never persist) — the message
    // names the file and the remedy.
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
    return;
}

// Two ports: the app port serves a clean data URL space (SSR only); the infra port
// serves /ws and /js. The page loads its bundle and opens its WebSocket against the
// infra port (injected by SsrRenderer as window.initInfraPort).
const ushort appPort = 8080;
const ushort infraPort = 8081;
var (appApp, infraApp) = InstanceApp.Build(store, description, infraPort);

await Host.Create()
          .Handler(infraApp)
          .Defaults(secureUpgrade: false, strictTransport: false)
          .Port(infraPort)
          .StartAsync();

await Host.Create()
          .Handler(appApp)
          // Plain HTTP: no HTTPS endpoint, so don't upgrade/redirect.
          .Defaults(secureUpgrade: false, strictTransport: false)
          .Port(appPort)
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

// Parse `--app <file>`; defaults to the committed todo app.
static string AppArg(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] is "--app" or "-a")
            return args[i + 1];
    return "instance.app";
}
