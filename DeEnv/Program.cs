using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Engine.Internal;
using GenHTTP.Modules.Layouting;
using GenHTTP.Modules.Practices;
using GenHTTP.Modules.Websockets;

// ── Instance description ──────────────────────────────────────────────────────

// Hardcoded for Milestone 2 (no designer/file loading yet): a small CRM with
// orders. Exercises objects, nested dictionaries, every base type, and both
// auto (int) and manual (text) dictionary key generation.
const string HardcodedDescription = """
{
  "types": [
    {
      "name": "Db",
      "baseType": "object",
      "props": [
        { "name": "companyName", "type": "text" },
        { "name": "settings",  "type": "text",     "cardinality": "dictionary", "keyType": "text", "keyGeneration": "manual" },
        { "name": "customers", "type": "Customer", "cardinality": "dictionary", "keyType": "int",  "keyGeneration": "auto" }
      ]
    },
    {
      "name": "Customer",
      "baseType": "object",
      "props": [
        { "name": "name",   "type": "text" },
        { "name": "email",  "type": "text" },
        { "name": "active", "type": "bool" },
        { "name": "orders", "type": "Order", "cardinality": "dictionary", "keyType": "int", "keyGeneration": "auto" }
      ]
    },
    {
      "name": "Order",
      "baseType": "object",
      "props": [
        { "name": "date",    "type": "date" },
        { "name": "total",   "type": "decimal" },
        { "name": "shipped", "type": "bool" }
      ]
    }
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
          // Plain HTTP: no HTTPS endpoint, so don't upgrade/redirect.
          .Defaults(secureUpgrade: false, strictTransport: false)
          .Port(8080)
          .RunAsync();
