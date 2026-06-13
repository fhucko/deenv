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

    // The simplest valid instance: an object Db with a single bool field. (The root must
    // be an object type — a base-typed `Db: bool` is rejected at load.) Renders as one
    // checkbox via the C# auto-form.
    public static InstanceDescription BoolDb() =>
        InstanceDescriptionLoader.Load("""
        types
            Db
                ready: bool
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

    // Milestone 9 (self-hosted generic UI): an app with no `ui` section, so the self-hosted
    // generic UI (the default) renders it. The all-scalar `Note` object page is rendered by
    // the self-hosted `objectForm` library; the Db root (`/`) self-hosts too, rendering its
    // `notes` set as an inline table whose member rows link to the nested member URL
    // (/notes/2). Drives SelfHostedUi.feature.
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
    """;

    // Milestone 9: an app whose Db holds an arbitrary-key DICTIONARY, rendered by the default
    // self-hosted generic UI. The Db root self-hosts, rendering the dictionary inline via the
    // `dictTable` library component. `Setting` (all-scalar) self-hosts too.
    public static InstanceDescription SelfHostedDictDb() =>
        InstanceDescriptionLoader.Load(SelfHostedDictApp);

    private const string SelfHostedDictApp = """
    types
        Db
            settings: dict of Setting by text
        Setting
            value: text

    initialData
        Db 1
    """;

    // Milestone 9: an app whose Db holds a SCALAR dictionary (text→text), rendered by the
    // default self-hosted generic UI. The self-hosted dictTable shows a Key + Value column;
    // entries persist via the path-addressed addEntry/removeEntry ops, like the object dict.
    public static InstanceDescription SelfHostedScalarDictDb() =>
        InstanceDescriptionLoader.Load(SelfHostedScalarDictApp);

    private const string SelfHostedScalarDictApp = """
    types
        Db
            settings: dict of text by text

    initialData
        Db 1
    """;

    // Milestone 9 (slice 2: references). Rendered by the default self-hosted generic UI.
    // `Db.lead: Person` is a reference ROUTE (/lead → the self-hosted reference editor);
    // `Note` (title scalar + `author: Person` reference) so /notes/{id} renders an objectForm
    // with an embedded author picker. Two people seed the Person extent for "pick existing".
    public static InstanceDescription SelfHostedRefDb() =>
        InstanceDescriptionLoader.Load(SelfHostedRefApp);

    private const string SelfHostedRefApp = """
    types
        Db
            people: set of Person
            lead: Person
            notes: set of Note
        Person
            name: text
        Note
            title: text
            author: Person

    initialData
        Db 1
            people: [2, 3]
            notes: [4]
        Person 2
            name: "Ada"
        Person 3
            name: "Grace"
        Note 4
            title: "First note"
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

    // Milestone 9 prototype: the component pattern for creation. `newForm` runs its body
    // ONCE (init — a wrapper `state` holding a draft), returns a render fn (the reactive
    // part), and a `create` handler that mints the draft then resets `state.draft` via an
    // object-field assignment. Verifies init-once + reactive render + create + reset hold
    // on the memo cache. Hand-authored (full-takeover `fn render()`), not the generic UI.
    public static InstanceDescription ComponentFormDb() =>
        InstanceDescriptionLoader.Load(ComponentFormApp);

    private const string ComponentFormApp = """
    types
        Db
            notes: set of Note
        Note
            title: text

    ui
        var path = "/"

        fn getNewNote()
            return { title: "" }

        fn newForm()
            var state = { draft: getNewNote() }
            fn create()
                db.notes.add(state.draft)
                state.draft = getNewNote()
            fn render()
                return <div class="new-form">
                    <input class="draft-title" value={state.draft.title}>
                    <button class="create" onClick={create}>
                        "Create"
            return render

        fn render()
            return <main>
                newForm()()
                <h2>
                    "Notes"
                foreach n in db.notes
                    <div class="note-row">
                        n.title
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
