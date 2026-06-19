using System.Net;
using System.Net.Sockets;
using DeEnv.Instance;
using DeEnv.Kernel;
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
                ready bool
        """);

    public static InstanceDescription ShopDb() =>
        InstanceDescriptionLoader.Load("""
        types
            Db
                customers dict of Customer by text
            Customer
                name text
                active bool
        """);

    // Milestone 5 object-graph instance: one extent type (Person), a set of
    // references into it (people), and a single object-typed reference (lead).
    // `set` cardinality and the single-object-prop-as-reference are exactly what
    // this milestone introduces.
    public static InstanceDescription ObjectGraphDb() =>
        InstanceDescriptionLoader.Load("""
        types
            Db
                people set of Person
                lead Person
            Person
                name text
        """);

    // Milestone 2 CRM-with-orders instance: objects, nested dictionaries, every base type. A
    // committed fixture in its id-dir (instances/3/app.app).
    public static InstanceDescription CrmDb() =>
        InstanceDescriptionLoader.LoadFile(AppFixture(3));

    // The committed default app (DeEnv/instances/2/app.app): the todo app — types, initialData seed,
    // and ui code in one text document; tests drive the real single source of truth.
    public static InstanceDescription TodoDb() =>
        InstanceDescriptionLoader.LoadFile(AppFixture(2));

    // A committed app fixture, resolved by its id-dir (instances/<id>/app.app) under the test output
    // — the same id-based layout the kernel hosts. Storage is fully id-based; the file name ("app")
    // no longer carries the app's identity, the id-dir does.
    public static string AppFixture(int id) =>
        Path.Combine(AppContext.BaseDirectory, "instances", id.ToString(), "app.app");

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
            notes set of Note
        Note
            title text
            done bool
            count int
            dueDate date

    initialData
        Db 1
            notes: [2]
        Note 2
            title: "First"
            done: false
            count: 3
            dueDate: "2026-01-01"
    """;

    // Milestone 11 (public component library): a HAND-WRITTEN `fn render()` that composes the
    // public `<ObjectForm>` library component — the second consumer proving the generic-UI library
    // is reachable + usable from userspace (the first consumer is the @milestone-9 generic object
    // pages, which still synthesize the same components). The Db holds the Note as a single
    // reference (`note`); render binds it: `<ObjectForm obj={db.note} meta={sys.schema("Note")}
    // base="/" autosave={true}>`. A custom render now also gets the descriptor map, so
    // sys.schema("Note") resolves; and the library scope is its parent, so <ObjectForm> recognizes
    // as a component. `autosave={true}` is the OPT-IN that keeps today's live per-keystroke save (no
    // Save/Discard buttons) — the synthesized object pages omit it and stage instead. So edits
    // autosave through the composed form, proving the opt-in works from userspace.
    public static InstanceDescription PublicLibraryFormDb() =>
        InstanceDescriptionLoader.Load(PublicLibraryFormApp);

    private const string PublicLibraryFormApp = """
    types
        Db
            note Note
        Note
            title text
            done bool
            count int
            dueDate date

    initialData
        Db 1
            note: 2
        Note 2
            title: "First"
            done: false
            count: 3
            dueDate: "2026-01-01"

    ui

        fn render()
            return <main>
                <ObjectForm obj={db.note} meta={sys.schema("Note")} base="/" autosave={true}>
    """;

    // Milestone 11 (same object, two independent editing contexts): a HAND-WRITTEN `fn render()` that
    // composes TWO STAGED `<ObjectForm>`s over the SAME object (`db.note`). Pure composition of the
    // existing per-form overlay — no new builtin or mechanism. Each form is a COMPONENT, so its
    // run-once setup mints its OWN draft (`sys.new` + `sys.setFields(draft, obj)`); M11 keys a
    // component by its render-tree SLOT, not its arguments, so the two forms — distinct render-tree
    // positions — get distinct slots and therefore INDEPENDENT drafts. Editing one form's field stages
    // into that form's draft only; the other is untouched, and the store is untouched until a Save.
    // The two `<section>` markers (`.context-a`/`.context-b`) let a test address each form's title
    // input + Save button separately. autosave is omitted → the DEFAULT staged-edit + Save flow.
    // The seeded `dueDate` is non-empty so a Save commits cleanly (an empty date is rejected on
    // commit — a pre-existing model gap, NOT this slice's concern); the scenario edits/asserts `title`.
    public static InstanceDescription TwoContextsDb() =>
        InstanceDescriptionLoader.Load(TwoContextsApp);

    private const string TwoContextsApp = """
    types
        Db
            note Note
        Note
            title text
            done bool
            count int
            dueDate date

    initialData
        Db 1
            note: 2
        Note 2
            title: "First"
            done: false
            count: 3
            dueDate: "2026-01-01"

    ui

        fn render()
            return <main>
                <section class="context-a">
                    <h2>
                        "Context A"
                    <ObjectForm obj={db.note} meta={sys.schema("Note")} base="/">
                <section class="context-b">
                    <h2>
                        "Context B"
                    <ObjectForm obj={db.note} meta={sys.schema("Note")} base="/">
    """;

    // Enum support (first slice): an app whose Db holds a set of `Order`, where `Order.status`
    // is typed by the `OrderStatus` enum. No `ui` section, so the default self-hosted generic UI
    // renders it: the order page's status field is a <select> of the enum's values. Seeded with
    // one order whose status is set ("shipped") and one left unset (default empty). The dedicated
    // enum fixture (NOT a committed app — committed apps mirror into the designer seed, which
    // would drag in SchemaBridge enum-projection, deferred to a later slice).
    public static InstanceDescription EnumFixtureDb() =>
        InstanceDescriptionLoader.Load(EnumFixtureApp);

    public const string EnumFixtureApp = """
    types
        OrderStatus enum
            pending
            shipped
            delivered
        Db
            orders set of Order
        Order
            label text
            status OrderStatus

    initialData
        Db 1
            orders: [2, 3]
        Order 2
            label: "First"
            status: "shipped"
        Order 3
            label: "Second"
    """;

    // Milestone 9: an app whose Db holds an arbitrary-key DICTIONARY, rendered by the default
    // self-hosted generic UI. The Db root self-hosts, rendering the dictionary inline via the
    // `dictTable` library component. `Setting` (all-scalar) self-hosts too.
    public static InstanceDescription SelfHostedDictDb() =>
        InstanceDescriptionLoader.Load(SelfHostedDictApp);

    private const string SelfHostedDictApp = """
    types
        Db
            settings dict of Setting by text
        Setting
            value text

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
            settings dict of text by text

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
            people set of Person
            lead Person
            notes set of Note
        Person
            name text
        Note
            title text
            author Person

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

    // The code-bearing fixture documents, for the printer round-trip tests. The shop app is a
    // committed fixture in its id-dir (instances/4/app.app).
    public static IReadOnlyList<string> CodeFixtureApps =>
        [TasksUiApp, InteractiveUiApp, SensitiveUiApp, RefetchUiApp,
         File.ReadAllText(AppFixture(4))];

    private const string TasksUiApp = """
    types
        Db
            tasks set of Task
        Task
            title text
            done bool
            priority int

    ui
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
            items set of Item
        Item
            name text

    ui
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
            people set of Person
        Person
            name text
            salary int

    common
        fn highEarners(people)
            return people.where(p => p.salary > 100)

    ui
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
            people set of Person
        Person
            name text
            salary int

    ui

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
            notes set of Note
        Note
            title text

    ui

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

    // Milestone 11, slice 1: the rebuilt-descriptor component. `noteForm` is invoked as a
    // TAG (`<noteForm desc={...}>`) with a FRESH descriptor object every render, and an
    // unrelated `ticks` counter the toggle button bumps — forcing the page to re-render and
    // rebuild the descriptor. With slot-path component identity the form's setup runs once per
    // slot, so the draft survives the re-render; with the old argument-keyed memo the rebuilt
    // descriptor would re-run the body and reset the draft. Proves the foundation end-to-end.
    public static InstanceDescription ComponentFormRebuiltDescDb() =>
        InstanceDescriptionLoader.Load(ComponentFormRebuiltDescApp);

    private const string ComponentFormRebuiltDescApp = """
    types
        Db
            notes set of Note
        Note
            title text

    ui

        var ticks = 0

        fn getNewNote()
            return { title: "" }

        fn noteForm(desc)
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
                <noteForm desc={getNewNote()}>
                <button class="toggle" onClick={() => ticks = ticks + 1}>
                    "Toggle"
                <span class="tick-count">
                    ticks
                <h2>
                    "Notes"
                foreach n in db.notes
                    <div class="note-row">
                        n.title
    """;

    // Milestone 11, slice 2 (lists/keys): a list whose rows are COMPONENTS, each with its own
    // local `scratch` state (not persisted). `rowEditor` is invoked as a tag inside `foreach`, so
    // each row keys on its member's identity — the per-row state is independent (no bleed between
    // rows) and follows the object across a reorder (it does NOT stick to the row position). The
    // `sign` flip reverses the orderBy so the two rows swap places. Proves slot identity through
    // foreach.
    public static InstanceDescription RowComponentListDb() =>
        InstanceDescriptionLoader.Load(RowComponentListApp);

    private const string RowComponentListApp = """
    types
        Db
            notes set of Note
        Note
            title text
            rank int

    initialData
        Db 1
            notes: [2, 3]
        Note 2
            title: "Alpha"
            rank: 1
        Note 3
            title: "Beta"
            rank: 2

    ui

        var sign = 1

        fn rowEditor(note)
            var state = { scratch: "" }
            fn render()
                return <div class="note-row">
                    <span class="row-title">
                        note.title
                    <input class="scratch" value={state.scratch}>
            return render

        fn render()
            return <main>
                <button class="reorder" onClick={() => sign = -1}>
                    "Reorder"
                foreach n in db.notes.orderBy(o => o.rank * sign)
                    <rowEditor note={n}>
    """;

    // Milestone 11, slice 3 (explicit per-call key): `<box key={k}>` resets when `k` changes.
    // `key` is a reserved directive that folds into the component's slot identity, so flipping `k`
    // gives `box` a NEW slot — its run-once setup re-runs and the local `scratch` clears. The
    // common case (no key) keeps state; this is the opt-in "reset when X changes" escape hatch.
    public static InstanceDescription KeyedComponentDb() =>
        InstanceDescriptionLoader.Load(KeyedComponentApp);

    private const string KeyedComponentApp = """
    types
        Db
            notes set of Note
        Note
            title text

    ui

        var k = 1

        fn box()
            var state = { scratch: "" }
            fn render()
                return <div class="box">
                    <input class="scratch" value={state.scratch}>
            return render

        fn render()
            return <main>
                <button class="rekey" onClick={() => k = 2}>
                    "Rekey"
                <box key={k}>
    """;

    // Milestone 11, slice 4b (root/value-position recognition): the page's `fn render()` RETURNS a
    // component directly (`return <rootForm desc={…}>`) — a component in value/return position, not a
    // tag-child. It must be recognized and slot-keyed, so its draft survives a re-render that rebuilds
    // the descriptor (the root analogue of the slice-1 scenario). Mirrors ComponentFormRebuiltDescApp
    // but with the form at the ROOT; reuses the slice-1 draft/create/note-list steps.
    public static InstanceDescription RootComponentDb() =>
        InstanceDescriptionLoader.Load(RootComponentApp);

    private const string RootComponentApp = """
    types
        Db
            notes set of Note
        Note
            title text

    ui

        var ticks = 0

        fn getNewNote()
            return { title: "" }

        fn rootForm(desc)
            var state = { draft: getNewNote() }
            fn create()
                db.notes.add(state.draft)
                state.draft = getNewNote()
            fn render()
                return <div class="root-form">
                    <input class="draft-title" value={state.draft.title}>
                    <button class="create" onClick={create}>
                        "Create"
                    <button class="toggle" onClick={() => ticks = ticks + 1}>
                        "Toggle"
                    <span class="tick-count">
                        ticks
                    <h2>
                        "Notes"
                    foreach n in db.notes
                        <div class="note-row">
                            n.title
            return render

        fn render()
            return <rootForm desc={getNewNote()}>
    """;

    // ── storage ───────────────────────────────────────────────────────────────

    public string DataFilePath { get; set; } = Path.GetTempFileName();
    public IInstanceStore? Store { get; set; }

    // ── server ────────────────────────────────────────────────────────────────

    public TestInstanceServer? Server { get; set; }
    public string BaseUrl => Server?.BaseUrl ?? "";

    // ── kernel host (milestone 10) ──────────────────────────────────────────────

    // A running multi-instance kernel and the temp directory holding its app fixtures,
    // registry (kernel.json), and derived data files — all cleaned up in Hooks.
    public KernelHost? Kernel { get; set; }
    public string? KernelDir { get; set; }

    // ── browser ───────────────────────────────────────────────────────────────

    public IPage? Page { get; set; }

    // Lazily start the in-process server and a page on the shared headless browser. Idempotent, so
    // any step that drives the page can call it (not just "I navigate to …").
    public async Task EnsureServerAndBrowserAsync()
    {
        if (Server == null)
        {
            Server = new TestInstanceServer();
            await Server.StartAsync(Description!, DataFilePath);
            Store = Server.Store;
        }

        // A fresh isolated page on the shared browser (see SharedBrowser): the browser + driver are
        // launched once for the whole run; each scenario just gets its own context + page.
        Page ??= await SharedBrowser.NewPageAsync(BaseUrl);
    }

    // ── kernel-backed designer browser (milestone 10: the operator IDE) ─────────

    // The operator IDE (instances/1/app.app) renders `sys.instances` — the kernel's hosted set — so it
    // can only be driven against a REAL KernelHost (TestInstanceServer hosts a single instance with no
    // kernel, so `sys.instances` would be empty). This boots a kernel hosting the REAL designer as id 1
    // plus the given target instances (each a tiny bool app, labelled to match a seeded design), then
    // points Playwright at the DESIGNER instance's app port. Returns the designer's HostedInstance so a
    // step can reach its store; targets are reached via Kernel.Instances by their label (Spec.App).
    public async Task<HostedInstance> StartKernelDesignerBrowserAsync(params (int Id, string Label)[] targets)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deenv-ide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        KernelDir = dir;

        // The designer at id 1: the REAL committed instances/1/app.app (copied from the test output),
        // hosted by the kernel exactly as production would.
        WriteIdApp(dir, 1, File.ReadAllText(AppFixture(1)));

        // Each target references its seeded design by an EXPLICIT designId — the id of the design in the
        // committed designer seed whose label matches the target's label. Resolved from the designer's
        // initialData (the seed) so the dropdowns start correct and the instances list shows the design.
        // The designer itself (id 1) is uniform: it carries a designId too — the id of its OWN "designer"
        // design (a bounded self-snapshot in the seed) — so its instances-list row resolves to a design
        // like every other row, with no special-casing. (Mirrors the committed kernel.json.)
        var designIds = DesignIdsByLabel();
        var entries = new List<string>
        {
            RegistryEntryJson(1, "designer", FreePort(), FreePort(), designIds["designer"]),
        };
        foreach (var (id, label) in targets)
        {
            WriteIdApp(dir, id, TargetBoolApp);
            entries.Add(RegistryEntryJson(id, label, FreePort(), FreePort(), designIds[label]));
        }

        File.WriteAllText(Path.Combine(dir, "kernel.json"),
            "{\n  \"instances\": [\n    " + string.Join(",\n    ", entries) + "\n  ]\n}");

        var registry = RegistryReader.Read(Path.Combine(dir, "kernel.json"));
        Kernel = new KernelHost(dir, Path.Combine(dir, "kernel.json"));
        await Kernel.StartAsync(KernelHost.SpecsFor(registry, dir));

        var designer = Kernel.Instances.Single(i => i.Spec.Id == 1);

        Page = await SharedBrowser.NewPageAsync($"http://localhost:{designer.AppPort}");
        return designer;
    }

    // A minimal valid app for a publish/edit target: an object Db with one bool. Its content is
    // irrelevant beyond being hostable (a publish overwrites it) — the label, not the doc, ties it to
    // a design.
    private const string TargetBoolApp = """
    types
        Db
            ready bool
    """;

    private static void WriteIdApp(string dir, int id, string appDoc)
    {
        var idDir = AppPaths.IdDirFor(dir, id);
        Directory.CreateDirectory(idDir);
        File.WriteAllText(Path.Combine(idDir, "app.app"), appDoc);
    }

    private static string RegistryEntryJson(int id, string label, int appPort, int infraPort, int? designId = null)
    {
        var did = designId.HasValue ? $", \"designId\": {designId.Value}" : "";
        return $"{{ \"id\": {id}, \"app\": \"{label}\", \"appPort\": {appPort}, \"infraPort\": {infraPort}{did} }}";
    }

    // The seeded design id for a label (e.g. "crm") — so a step can assert an instance now records that
    // design's id after Apply. Reads the same committed designer seed the fixture seeds designIds from.
    public int DesignIdForLabel(string label) => DesignIdsByLabel()[label];

    // Map each seeded design's label → its id, read from the committed designer seed (instances/1's
    // initialData). The IDE's instance↔design link is the explicit designId, so a target labelled
    // "todo" gets the id of the design labelled "todo" — making its dropdown pre-select and its
    // instances-list row resolve to that design.
    private static Dictionary<string, int> DesignIdsByLabel()
    {
        var designer = InstanceDescriptionLoader.LoadFile(AppFixture(1));
        var designs = designer.InitialData?.Extents?.GetValueOrDefault("Design")
            ?? throw new InvalidOperationException("The designer seed has no Design extent.");
        var map = new Dictionary<string, int>();
        foreach (var (key, env) in designs)
            if (env.TryGetProperty("label", out var label) && label.ValueKind == System.Text.Json.JsonValueKind.String)
                map[label.GetString()!] = int.Parse(key);
        return map;
    }

    // A genuinely free TCP port, never handed out twice this run (see PortAllocator) — so two parallel
    // scenarios can't be given the same port and have a browser reach the wrong instance. Public so a step
    // that fills the create-instance form's port inputs can pick free ports (the kernel rejects a port
    // collision, so a hard-coded pair would flake against the other in-process hosts).
    public static int FreePort() => PortAllocator.Next();
}
