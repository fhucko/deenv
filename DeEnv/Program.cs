using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Engine.Internal;
using GenHTTP.Modules.Layouting;
using GenHTTP.Modules.Practices;
using GenHTTP.Modules.Websockets;

// ── Instance description ──────────────────────────────────────────────────────

// Milestone 3: the instance is defined by a validated JSON schema document on
// disk (copied next to the executable by the build), not a hardcoded string.
var schemaPath = Path.Combine(AppContext.BaseDirectory, "instance.schema.json");
var description = InstanceDescriptionLoader.LoadFile(schemaPath);

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
