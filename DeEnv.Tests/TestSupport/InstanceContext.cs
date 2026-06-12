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

    // Raw document text under test, its sidecar code text (M7), the result of
    // loading them, and any error raised.
    public string? SchemaJson { get; set; }
    public string? CodeText { get; set; }
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
    // base type, and both auto (int) + manual (text) key generation. Now a test
    // fixture (crm.schema.json) — the committed default app became the todo app.
    public static InstanceDescription CrmDb() =>
        InstanceDescriptionLoader.LoadFile(
            Path.Combine(AppContext.BaseDirectory, "crm.schema.json"));

    // The committed default app (DeEnv/instance.schema.json): the todo app — the
    // Code milestone's end-to-end proof. Types + ui AST + initialData seed; tests
    // drive the real single source of truth.
    public static InstanceDescription TodoDb() =>
        InstanceDescriptionLoader.LoadFile(
            Path.Combine(AppContext.BaseDirectory, "instance.schema.json"));

    // Code milestone: a hand-written `ui` component over a Task set. The render fn
    // exercises element/text, a bound text field, a bound checkbox, foreach, if/else,
    // and where/orderBy collection functions — the full Stage-2 SSR surface.
    public static InstanceDescription TasksUiDb() =>
        InstanceDescriptionLoader.Load(TasksUiJson, TasksUiCode);

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
      ]
    }
    """;

    private const string TasksUiCode = """
    ui
        var path = "/"
        var title = "Tasks"

        fn render()
            return <main>
                <h1>
                    title
                <section id="all">
                    foreach t in db.tasks.orderBy((x) => x.priority)
                        <div class="task">
                            <input type="text" value={t.title}>
                            <input type="checkbox" checked={t.done}>
                            if t.done
                                <span class="status">
                                    "done"
                            else
                                <span class="status">
                                    "open"
                <section id="open">
                    foreach t in db.tasks.where((x) => x.done == false)
                        <span class="open-title">
                            t.title
    """;

    // Code milestone, Stage 3: a tiny interactive `ui` over an Item set, ordered by a
    // text field bound two-way (so typing reorders the list — exercising identity-keyed
    // reconciliation) plus a transient new-item form (a name var + add button).
    public static InstanceDescription InteractiveUiDb() =>
        InstanceDescriptionLoader.Load(InteractiveUiJson, InteractiveUiCode);

    private const string InteractiveUiJson = """
    {
      "types": [
        { "name": "Db", "baseType": "object",
          "props": [ { "name": "items", "type": "Item", "cardinality": "set" } ] },
        { "name": "Item", "baseType": "object",
          "props": [ { "name": "name", "type": "text" } ] }
      ]
    }
    """;

    private const string InteractiveUiCode = """
    ui
        var path = "/"
        var newName = ""

        fn addItem()
            db.items.add({ name: newName })
            newName = ""

        fn render()
            return <main>
                <input class="new-name" value={newName}>
                <button class="add" onClick={addItem}>
                    "Add"
                foreach i in db.items.orderBy((x) => x.name)
                    <div>
                        <input class="name" value={i.name}>
    """;

    // Code milestone, Stage 4: a private field (`salary`) by construction. `highEarners`
    // filters by salary; its result is the `rich` var (a memoized computation), so salary
    // is a dependency, never a leaf — the client gets the high earners' names but never any
    // salary, and never the non-earner rows (db.people is read only inside the computation).
    // No `sensitive` flag: "private" = "an input to a computation, never a rendered result".
    public static InstanceDescription SensitiveUiDb() =>
        InstanceDescriptionLoader.Load(SensitiveUiJson, SensitiveUiCode);

    private const string SensitiveUiJson = """
    {
      "types": [
        { "name": "Db", "baseType": "object",
          "props": [ { "name": "people", "type": "Person", "cardinality": "set" } ] },
        { "name": "Person", "baseType": "object",
          "props": [
            { "name": "name",   "type": "text" },
            { "name": "salary", "type": "int" }
          ] }
      ]
    }
    """;

    private const string SensitiveUiCode = """
    common
        fn highEarners(people)
            return people.where((p) => p.salary > 100)

    ui
        var path = "/"
        var rich = highEarners(db.people)

        fn render()
            return <main>
                foreach p in rich
                    <div class="earner">
                        p.name
    """;

    // Code milestone, Stage 4b: a hidden-dependency recompute. `people` is rendered by
    // name, but the earners list is `db.people.where(p => p.salary > 100)` — salary is
    // read only inside the predicate, so it is a dependency, never shipped. Adding a
    // person changes set membership (the client knows) but the client cannot re-filter
    // (the existing members' salaries are private), so it refetches: the server recomputes
    // over fresh storage and returns the authoritative earners list.
    public static InstanceDescription RefetchUiDb() =>
        InstanceDescriptionLoader.Load(RefetchUiJson, RefetchUiCode);

    private const string RefetchUiJson = """
    {
      "types": [
        { "name": "Db", "baseType": "object",
          "props": [ { "name": "people", "type": "Person", "cardinality": "set" } ] },
        { "name": "Person", "baseType": "object",
          "props": [
            { "name": "name",   "type": "text" },
            { "name": "salary", "type": "int" }
          ] }
      ]
    }
    """;

    private const string RefetchUiCode = """
    ui
        var path = "/"

        fn render()
            return <main>
                <button class="add-rich" onClick={() => db.people.add({ name: "Rich", salary: 200 })}>
                    "Add rich"
                <button class="add-poor" onClick={() => db.people.add({ name: "Poor", salary: 50 })}>
                    "Add poor"
                foreach p in db.people
                    <div class="person">
                        p.name
                foreach p in db.people.where((x) => x.salary > 100)
                    <div class="earner">
                        p.name
    """;

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
