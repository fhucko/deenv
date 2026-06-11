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

    // Milestone 5 object-graph instance: one extent type (Person), a set of
    // references into it (people), and a single object-typed reference (lead).
    // `set` cardinality and the single-object-prop-as-reference are exactly what
    // this milestone introduces.
    public static InstanceDescription ObjectGraphDb() =>
        InstanceDescriptionLoader.Load("""
        {
          "types": [
            {
              "name": "Db",
              "baseType": "object",
              "props": [
                { "name": "people", "type": "Person", "cardinality": "set" },
                { "name": "lead",   "type": "Person" }
              ]
            },
            {
              "name": "Person",
              "baseType": "object",
              "props": [
                { "name": "name", "type": "text" }
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

    // Code milestone: a hand-written `ui` component over a Task set. The render fn
    // exercises element/text, a bound text field, a bound checkbox, foreach, if/else,
    // and where/orderBy collection functions — the full Stage-2 SSR surface.
    public static InstanceDescription TasksUiDb() =>
        InstanceDescriptionLoader.Load(TasksUiJson);

    // The rendered HTML from the code-owned UI (Stage 2 SSR), under test.
    public string? RenderedHtml { get; set; }

    private const string TasksUiJson = """
    {
      "types": [
        { "name": "Db", "baseType": "object",
          "props": [ { "name": "tasks", "type": "Task", "cardinality": "set" } ] },
        { "name": "Task", "baseType": "object",
          "props": [
            { "name": "title",    "type": "text" },
            { "name": "done",     "type": "bool" },
            { "name": "priority", "type": "int"  }
          ] }
      ],
      "ui": {
        "vars": [
          { "name": "path",  "value": { "type": "text", "value": "/" } },
          { "name": "title", "value": { "type": "text", "value": "Tasks" } }
        ],
        "render": {
          "type": "fn",
          "params": [],
          "body": { "type": "block", "statements": [ { "type": "return", "value":
            { "type": "tag", "name": "main", "attributes": [], "children": [
              { "type": "tag", "name": "h1", "attributes": [],
                "children": [ { "type": "symbol", "name": "title" } ] },

              { "type": "tag", "name": "section",
                "attributes": [ { "name": "id", "value": { "type": "text", "value": "all" } } ],
                "children": [
                  { "type": "foreach",
                    "item": { "name": "t" },
                    "collection": { "type": "call",
                      "fn": { "type": "infixOp", "op": "objectProp",
                        "left": { "type": "infixOp", "op": "objectProp",
                          "left": { "type": "symbol", "name": "db" },
                          "right": { "type": "symbol", "name": "tasks" } },
                        "right": { "type": "symbol", "name": "orderBy" } },
                      "params": [ { "type": "fn", "params": [ { "name": "x" } ],
                        "body": { "type": "block", "statements": [ { "type": "return", "value":
                          { "type": "infixOp", "op": "objectProp",
                            "left": { "type": "symbol", "name": "x" },
                            "right": { "type": "symbol", "name": "priority" } } } ] } } ] },
                    "body": [
                      { "type": "tag", "name": "div",
                        "attributes": [ { "name": "class", "value": { "type": "text", "value": "task" } } ],
                        "children": [
                          { "type": "tag", "name": "input",
                            "attributes": [
                              { "name": "type",  "value": { "type": "text", "value": "text" } },
                              { "name": "value", "value": { "type": "infixOp", "op": "objectProp",
                                "left": { "type": "symbol", "name": "t" },
                                "right": { "type": "symbol", "name": "title" } } }
                            ], "children": [] },
                          { "type": "tag", "name": "input",
                            "attributes": [
                              { "name": "type",    "value": { "type": "text", "value": "checkbox" } },
                              { "name": "checked", "value": { "type": "infixOp", "op": "objectProp",
                                "left": { "type": "symbol", "name": "t" },
                                "right": { "type": "symbol", "name": "done" } } }
                            ], "children": [] },
                          { "type": "if",
                            "condition": { "type": "infixOp", "op": "objectProp",
                              "left": { "type": "symbol", "name": "t" },
                              "right": { "type": "symbol", "name": "done" } },
                            "body": [ { "type": "tag", "name": "span",
                              "attributes": [ { "name": "class", "value": { "type": "text", "value": "status" } } ],
                              "children": [ { "type": "text", "value": "done" } ] } ],
                            "elseBody": [ { "type": "tag", "name": "span",
                              "attributes": [ { "name": "class", "value": { "type": "text", "value": "status" } } ],
                              "children": [ { "type": "text", "value": "open" } ] } ] }
                        ] }
                    ] }
                ] },

              { "type": "tag", "name": "section",
                "attributes": [ { "name": "id", "value": { "type": "text", "value": "open" } } ],
                "children": [
                  { "type": "foreach",
                    "item": { "name": "t" },
                    "collection": { "type": "call",
                      "fn": { "type": "infixOp", "op": "objectProp",
                        "left": { "type": "call",
                          "fn": { "type": "infixOp", "op": "objectProp",
                            "left": { "type": "infixOp", "op": "objectProp",
                              "left": { "type": "symbol", "name": "db" },
                              "right": { "type": "symbol", "name": "tasks" } },
                            "right": { "type": "symbol", "name": "where" } },
                          "params": [ { "type": "fn", "params": [ { "name": "x" } ],
                            "body": { "type": "block", "statements": [ { "type": "return", "value":
                              { "type": "infixOp", "op": "equals",
                                "left": { "type": "infixOp", "op": "objectProp",
                                  "left": { "type": "symbol", "name": "x" },
                                  "right": { "type": "symbol", "name": "done" } },
                                "right": { "type": "bool", "value": false } } } ] } } ] },
                        "right": { "type": "symbol", "name": "orderBy" } },
                      "params": [ { "type": "fn", "params": [ { "name": "x" } ],
                        "body": { "type": "block", "statements": [ { "type": "return", "value":
                          { "type": "infixOp", "op": "objectProp",
                            "left": { "type": "symbol", "name": "x" },
                            "right": { "type": "symbol", "name": "priority" } } } ] } } ] },
                    "body": [
                      { "type": "tag", "name": "span",
                        "attributes": [ { "name": "class", "value": { "type": "text", "value": "open-title" } } ],
                        "children": [ { "type": "infixOp", "op": "objectProp",
                          "left": { "type": "symbol", "name": "t" },
                          "right": { "type": "symbol", "name": "title" } } ] }
                    ] }
                ] }
            ] } } ] }
        }
      }
    }
    """;

    // Code milestone, Stage 3: a tiny interactive `ui` over an Item set, ordered by a
    // text field bound two-way (so typing reorders the list — exercising identity-keyed
    // reconciliation) plus a transient new-item form (a name var + add button).
    public static InstanceDescription InteractiveUiDb() =>
        InstanceDescriptionLoader.Load(InteractiveUiJson);

    private const string InteractiveUiJson = """
    {
      "types": [
        { "name": "Db", "baseType": "object",
          "props": [ { "name": "items", "type": "Item", "cardinality": "set" } ] },
        { "name": "Item", "baseType": "object",
          "props": [ { "name": "name", "type": "text" } ] }
      ],
      "ui": {
        "vars": [
          { "name": "path",    "value": { "type": "text", "value": "/" } },
          { "name": "newName", "value": { "type": "text", "value": "" } }
        ],
        "render": {
          "type": "fn", "params": [],
          "body": { "type": "block", "statements": [ { "type": "return", "value":
            { "type": "tag", "name": "main", "attributes": [], "children": [
              { "type": "tag", "name": "input", "attributes": [
                { "name": "class", "value": { "type": "text", "value": "new-name" } },
                { "name": "value", "value": { "type": "symbol", "name": "newName" } }
              ], "children": [] },
              { "type": "tag", "name": "button", "attributes": [
                { "name": "class", "value": { "type": "text", "value": "add" } },
                { "name": "onClick", "value": { "type": "fn", "params": [],
                  "body": { "type": "block", "statements": [
                    { "type": "call",
                      "fn": { "type": "infixOp", "op": "objectProp",
                        "left": { "type": "infixOp", "op": "objectProp",
                          "left": { "type": "symbol", "name": "db" },
                          "right": { "type": "symbol", "name": "items" } },
                        "right": { "type": "symbol", "name": "add" } },
                      "params": [ { "type": "object", "props": [
                        { "name": "name", "value": { "type": "symbol", "name": "newName" } }
                      ] } ] },
                    { "type": "assign", "target": { "type": "symbol", "name": "newName" },
                      "value": { "type": "text", "value": "" } }
                  ] } } }
              ], "children": [ { "type": "text", "value": "Add" } ] },
              { "type": "foreach", "item": { "name": "i" },
                "collection": { "type": "call",
                  "fn": { "type": "infixOp", "op": "objectProp",
                    "left": { "type": "infixOp", "op": "objectProp",
                      "left": { "type": "symbol", "name": "db" },
                      "right": { "type": "symbol", "name": "items" } },
                    "right": { "type": "symbol", "name": "orderBy" } },
                  "params": [ { "type": "fn", "params": [ { "name": "x" } ],
                    "body": { "type": "block", "statements": [ { "type": "return", "value":
                      { "type": "infixOp", "op": "objectProp",
                        "left": { "type": "symbol", "name": "x" },
                        "right": { "type": "symbol", "name": "name" } } } ] } } ] },
                "body": [
                  { "type": "tag", "name": "div", "attributes": [], "children": [
                    { "type": "tag", "name": "input", "attributes": [
                      { "name": "class", "value": { "type": "text", "value": "name" } },
                      { "name": "value", "value": { "type": "infixOp", "op": "objectProp",
                        "left": { "type": "symbol", "name": "i" },
                        "right": { "type": "symbol", "name": "name" } } }
                    ], "children": [] }
                  ] }
                ] }
            ] } } ] }
        }
      }
    }
    """;

    // Code milestone, Stage 4a: a sensitive field (`salary`) and a server-only
    // derivation. `highEarners` (server-only) filters by salary; its result is
    // materialised into the `rich` var; render shows only the resulting names. So the
    // client receives the high earners' names but never any salary, and never the
    // non-earner rows (db.people is read only on the server).
    public static InstanceDescription SensitiveUiDb() =>
        InstanceDescriptionLoader.Load(SensitiveUiJson);

    private const string SensitiveUiJson = """
    {
      "types": [
        { "name": "Db", "baseType": "object",
          "props": [ { "name": "people", "type": "Person", "cardinality": "set" } ] },
        { "name": "Person", "baseType": "object",
          "props": [
            { "name": "name",   "type": "text" },
            { "name": "salary", "type": "int", "sensitive": true }
          ] }
      ],
      "common": {
        "functions": [
          { "type": "fn", "name": "highEarners", "serverOnly": true,
            "params": [ { "name": "people" } ],
            "body": { "type": "block", "statements": [ { "type": "return", "value":
              { "type": "call",
                "fn": { "type": "infixOp", "op": "objectProp",
                  "left": { "type": "symbol", "name": "people" },
                  "right": { "type": "symbol", "name": "where" } },
                "params": [ { "type": "fn", "params": [ { "name": "p" } ],
                  "body": { "type": "block", "statements": [ { "type": "return", "value":
                    { "type": "infixOp", "op": "moreThan",
                      "left": { "type": "infixOp", "op": "objectProp",
                        "left": { "type": "symbol", "name": "p" },
                        "right": { "type": "symbol", "name": "salary" } },
                      "right": { "type": "int", "value": 100 } } } ] } } ] } } ] } }
        ]
      },
      "ui": {
        "vars": [
          { "name": "path", "value": { "type": "text", "value": "/" } },
          { "name": "rich", "value": { "type": "call",
            "fn": { "type": "symbol", "name": "highEarners" },
            "params": [ { "type": "infixOp", "op": "objectProp",
              "left": { "type": "symbol", "name": "db" },
              "right": { "type": "symbol", "name": "people" } } ] } }
        ],
        "render": {
          "type": "fn", "params": [],
          "body": { "type": "block", "statements": [ { "type": "return", "value":
            { "type": "tag", "name": "main", "attributes": [], "children": [
              { "type": "foreach", "item": { "name": "p" },
                "collection": { "type": "symbol", "name": "rich" },
                "body": [
                  { "type": "tag", "name": "div",
                    "attributes": [ { "name": "class", "value": { "type": "text", "value": "earner" } } ],
                    "children": [ { "type": "infixOp", "op": "objectProp",
                      "left": { "type": "symbol", "name": "p" },
                      "right": { "type": "symbol", "name": "name" } } ] }
                ] }
            ] } } ] }
        }
      }
    }
    """;

    // Same types as SensitiveUiDb, but the render reads the sensitive `salary`
    // directly in client-run code — the data-transfer boundary rejects it at render.
    public static InstanceDescription SensitiveLeakUiDb() =>
        InstanceDescriptionLoader.Load("""
        {
          "types": [
            { "name": "Db", "baseType": "object",
              "props": [ { "name": "people", "type": "Person", "cardinality": "set" } ] },
            { "name": "Person", "baseType": "object",
              "props": [
                { "name": "name",   "type": "text" },
                { "name": "salary", "type": "int", "sensitive": true }
              ] }
          ],
          "ui": {
            "render": { "type": "fn", "params": [], "body": { "type": "block", "statements": [
              { "type": "return", "value":
                { "type": "tag", "name": "main", "attributes": [], "children": [
                  { "type": "foreach", "item": { "name": "p" },
                    "collection": { "type": "infixOp", "op": "objectProp",
                      "left": { "type": "symbol", "name": "db" },
                      "right": { "type": "symbol", "name": "people" } },
                    "body": [
                      { "type": "tag", "name": "div", "attributes": [], "children": [
                        { "type": "infixOp", "op": "objectProp",
                          "left": { "type": "symbol", "name": "p" },
                          "right": { "type": "symbol", "name": "salary" } }
                      ] }
                    ] }
                ] } }
            ] } }
          }
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

    // Lazily start the in-process server and a headless browser. Idempotent, so
    // any step that drives the page can call it (not just "I navigate to …").
    public async Task EnsureServerAndBrowserAsync()
    {
        if (Server == null)
        {
            Server = new TestInstanceServer();
            await Server.StartAsync(Description!, DataFilePath);
            Store = Server.Store;
        }

        if (Browser == null)
        {
            Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            Page = await Browser.NewPageAsync(new BrowserNewPageOptions { BaseURL = BaseUrl });
        }
    }
}
