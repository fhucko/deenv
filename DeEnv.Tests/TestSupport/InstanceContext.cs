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

    // Raw app document text under test, the result of loading it, and any error raised.
    public string? SchemaJson { get; set; }
    public InstanceDescription? LoadedDescription { get; set; }
    public Exception? LoadError { get; set; }
    public string? SchemaFilePath { get; set; }

    public static InstanceDescription BoolDb() =>
        InstanceDescriptionLoader.Load("""
        types
            Db: bool
        """);

    public static InstanceDescription ShopDb() =>
        InstanceDescriptionLoader.Load("""
        types
            Db
                customers: dict of Customer by text
            Customer
                name: text
                active: bool
        """);

    // Milestone 5 object-graph instance: one extent type (Person), a set of
    // references into it (people), and a single object-typed reference (lead).
    // `set` cardinality and the single-object-prop-as-reference are exactly what
    // this milestone introduces.
    public static InstanceDescription ObjectGraphDb() =>
        InstanceDescriptionLoader.Load("""
        types
            Db
                people: set of Person
                lead: Person
            Person
                name: text
        """);

    // Milestone 2 CRM-with-orders instance: objects, nested dictionaries, every
    // base type. Now a test fixture (crm.app) — the committed default app is todo.
    public static InstanceDescription CrmDb() =>
        InstanceDescriptionLoader.LoadFile(
            Path.Combine(AppContext.BaseDirectory, "crm.app"));

    // The committed default app (DeEnv/instance.app): the todo app — types,
    // initialData seed, and ui code in one text document; tests drive the real
    // single source of truth.
    public static InstanceDescription TodoDb() =>
        InstanceDescriptionLoader.LoadFile(
            Path.Combine(AppContext.BaseDirectory, "instance.app"));

    // Code milestone: a hand-written `ui` component over a Task set. The render fn
    // exercises element/text, a bound text field, a bound checkbox, foreach, if/else,
    // and where/orderBy collection functions — the full Stage-2 SSR surface.
    public static InstanceDescription TasksUiDb() =>
        InstanceDescriptionLoader.Load(TasksUiApp);

    // The rendered HTML from the code-owned UI (Stage 2 SSR), under test.
    public string? RenderedHtml { get; set; }

    // Milestone 8: the shop app — a TYPE view (Customer) + a PATH view (/dashboard)
    // over a generic remainder, no `fn render()`. The committed DeEnv/shop.app and
    // this text are the same document. Drives UiCustomization.feature.
    public static InstanceDescription ViewsUiDb() =>
        InstanceDescriptionLoader.LoadFile(
            Path.Combine(AppContext.BaseDirectory, "shop.app"));

    // Milestone 9 (self-hosted generic UI, slice 1): an app that opts into the generic
    // Code UI (`generic`) with no hand-written views. The all-scalar `Note` object page
    // is rendered by the self-hosted `objectForm` library; `/` and `/notes` (the Db root
    // and the set) stay on the C# auto-form. Drives SelfHostedUi.feature.
    public static InstanceDescription SelfHostedFormDb() =>
        InstanceDescriptionLoader.Load(SelfHostedFormApp);

    private const string SelfHostedFormApp = """
    types
        Db
            notes: set of Note
        Note
            title: text
            done: bool
            count: int
            dueDate: date

    initialData
        Db 1
            notes: [2]
        Note 2
            title: "First"
            done: false
            count: 3
            dueDate: "2026-01-01"

    ui
        generic
    """;

    // The code-bearing fixture documents, for the printer round-trip tests.
    public static IReadOnlyList<string> CodeFixtureApps =>
        [TasksUiApp, InteractiveUiApp, SensitiveUiApp, RefetchUiApp,
         File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "shop.app"))];

    private const string TasksUiApp = """
    types
        Db
            tasks: set of Task
        Task
            title: text
            done: bool
            priority: int

    ui
        var path = "/"
        var title = "Tasks"

        fn render()
            return <main>
                <h1>
                    title
                <section id="all">
                    foreach t in db.tasks.orderBy(x => x.priority)
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
                    foreach t in db.tasks.where(x => x.done == false)
                        <span class="open-title">
                            t.title
    """;

    // Code milestone, Stage 3: a tiny interactive `ui` over an Item set, ordered by a
    // text field bound two-way (so typing reorders the list — exercising identity-keyed
    // reconciliation) plus a transient new-item form (a name var + add button).
    public static InstanceDescription InteractiveUiDb() =>
        InstanceDescriptionLoader.Load(InteractiveUiApp);

    private const string InteractiveUiApp = """
    types
        Db
            items: set of Item
        Item
            name: text

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
                foreach i in db.items.orderBy(x => x.name)
                    <div>
                        <input class="name" value={i.name}>
    """;

    // Code milestone, Stage 4: a private field (`salary`) by construction. `highEarners`
    // filters by salary; its result is the `rich` var (a memoized computation), so salary
    // is a dependency, never a leaf — the client gets the high earners' names but never any
    // salary, and never the non-earner rows (db.people is read only inside the computation).
    // No `sensitive` flag: "private" = "an input to a computation, never a rendered result".
    public static InstanceDescription SensitiveUiDb() =>
        InstanceDescriptionLoader.Load(SensitiveUiApp);

    private const string SensitiveUiApp = """
    types
        Db
            people: set of Person
        Person
            name: text
            salary: int

    common
        fn highEarners(people)
            return people.where(p => p.salary > 100)

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
        InstanceDescriptionLoader.Load(RefetchUiApp);

    private const string RefetchUiApp = """
    types
        Db
            people: set of Person
        Person
            name: text
            salary: int

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
                foreach p in db.people.where(x => x.salary > 100)
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
