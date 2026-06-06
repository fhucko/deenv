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

    // Milestone 2 CRM-with-orders instance: objects, nested dictionaries,
    // every base type, and both auto (int) + manual (text) key generation.
    public static InstanceDescription CrmDb() =>
        InstanceDescriptionLoader.Load("""
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
        """);

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
