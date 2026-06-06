using DeEnv.Instance;
using DeEnv.Storage;
using Microsoft.Playwright;

namespace DeEnv.Tests.TestSupport;

/// <summary>
/// Per-scenario shared context injected into all step classes via Reqnroll DI.
/// </summary>
public class InstanceContext
{
    // ── description ───────────────────────────────────────────────────────────

    public InstanceDescription? Description { get; set; }

    // ── schema document loading (milestone 3) ──────────────────────────────────

    // Raw document text under test, the result of loading it, and any error raised.
    public string? SchemaJson { get; set; }
    public InstanceDescription? LoadedDescription { get; set; }
    public Exception? LoadError { get; set; }
    public string? SchemaFilePath { get; set; }

    public static InstanceDescription BoolDb() =>
        InstanceDescriptionLoader.Load("""{ "types": [{ "name": "Db", "baseType": "bool" }] }""");

    public static InstanceDescription ShopDb() =>
        InstanceDescriptionLoader.Load("""
        {
          "types": [
            {
              "name": "Db",
              "baseType": "object",
              "props": [
                { "name": "customers", "type": "Customer", "cardinality": "dictionary", "keyType": "text" }
              ]
            },
            {
              "name": "Customer",
              "baseType": "object",
              "props": [
                { "name": "name",   "type": "text" },
                { "name": "active", "type": "bool" }
              ]
            }
          ]
        }
        """);

    // Milestone 2 CRM-with-orders instance: objects, nested dictionaries, every
    // base type, and both auto (int) + manual (text) key generation. Loaded from
    // the committed schema document (the single source of truth), shipped to the
    // test output by the csproj — see DeEnv/instance.schema.json.
    public static InstanceDescription CrmDb() =>
        InstanceDescriptionLoader.LoadFile(
            Path.Combine(AppContext.BaseDirectory, "instance.schema.json"));

    // ── storage ───────────────────────────────────────────────────────────────

    public string DataFilePath { get; set; } = Path.GetTempFileName();
    public IInstanceStore? Store { get; set; }

    // ── server ────────────────────────────────────────────────────────────────

    public TestInstanceServer? Server { get; set; }
    public string BaseUrl => Server?.BaseUrl ?? "";

    // ── browser ───────────────────────────────────────────────────────────────

    public IPlaywright? Playwright { get; set; }
    public IBrowser? Browser { get; set; }
    public IPage? Page { get; set; }
}
