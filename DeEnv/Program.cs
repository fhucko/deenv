using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Engine.Internal;
using GenHTTP.Modules.Layouting;
using GenHTTP.Modules.Practices;
using GenHTTP.Modules.Websockets;

// ── Instance description ──────────────────────────────────────────────────────

// Hardcoded for Milestone 1. No file-based loading yet.
const string HardcodedDescription = """
{
  "types": [
    { "name": "Db", "baseType": "bool" }
  ]
}
""";

var description = InstanceDescriptionLoader.Load(HardcodedDescription);

// ── Storage ───────────────────────────────────────────────────────────────────

var dataFile = Path.Combine(AppContext.BaseDirectory, "instance-data.json");
IInstanceStore store = new JsonFileInstanceStore(dataFile, description);

// ── HTTP layer (GenHTTP, pure-C# engine — no ASP.NET Core) ─────────────────────

var app = DeEnv.Http.InstanceApp.Build(store, description);

await Host.Create()
          .Handler(app)
          .Defaults()
          .Port(8080)
          .RunAsync();
