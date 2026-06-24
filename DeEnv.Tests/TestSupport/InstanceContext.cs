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

    // Milestone 9 (labeled breadcrumbs, deep-route twin invariant): the self-hosted generic UI over a
    // THREE-objects-deep graph (Db → milestones → Milestone → slices → Slice), modelled on the committed
    // devlog app's shape. A route like /milestones/4/slices/5 has an INTERMEDIATE object segment ("4",
    // the Milestone) whose label is NOT rendered by the leaf page (the Slice "5" form) — it is shown only
    // in the breadcrumb. So the only thing that ships the intermediate's labelProp leaf is the breadcrumb
    // trail's resolve recording it (the blocker fix); without that, the server labels "4" while the client
    // humanizes the raw id → the crumb flips after hydration. The labelProp is the text `name`. Drives the
    // deep-route SSR scenario AND the byte-identical hydrated browser scenario.
    public static InstanceDescription DeepNavDb() =>
        InstanceDescriptionLoader.Load(DeepNavApp);

    private const string DeepNavApp = """
    types
        Db
            milestones set of Milestone
        Milestone
            name text
            slices set of Slice
        Slice
            name text

    initialData
        Db 1
            milestones: [4]
        Milestone 4
            name: "Gate #3 - dogfood a real app"
            slices: [5]
        Slice 5
            name: "Dev tracker v1 (this app)"
    """;

    // Milestone 11 (SPA-nav flash guard): the self-hosted generic UI over a notes set with TWO members.
    // An OBJECT route (/notes/2) ships only the routed member of the set (FindTarget records just that
    // item) — so the SIBLING (Note 9) is ABSENT from the client graph after a deep-link to /notes/2.
    // Navigating client-side to /notes/9 therefore resolves to target:null (a VALID route whose object
    // was not shipped — byte-identical to a genuinely-missing node), which is exactly the case the flash
    // guard must handle: don't paint NotFound optimistically, hold the current view, refetch, then paint
    // the real Note 9 page. Drives the "navigating to an un-shipped target never flashes Not found"
    // scenario. (No custom `fn render()` → the default self-hosted generic UI, so SPA nav is in play.)
    public static InstanceDescription FlashNavDb() =>
        InstanceDescriptionLoader.Load(FlashNavApp);

    private const string FlashNavApp = """
    types
        Db
            notes set of Note
        Note
            title text

    initialData
        Db 1
            notes: [2, 9]
        Note 2
            title: "First"
        Note 9
            title: "Ninth"
    """;

    // SPA-nav object-form-with-reference regression — drives the REAL committed demo app (instances/6),
    // a generic-UI Db holding object collections: a `tasks` set whose Task element holds an `assignee`
    // reference (+ a nested `subtasks` set), plus object/scalar dicts. No `ui` section → the default
    // generic UI, so SPA nav is in play.
    //
    // The bug (fixed, commit 87889d0): the generic object form renders a RefEditor when its object holds a
    // reference, whose picker is `foreach c in sys.extent("Person")`. A start view that shipped the route's
    // descriptors (so the nav's first gate passes) but NOT the Person extent — the Db root (`/`) and the
    // `tasks` set view (`/tasks`) both qualify — left `extent:Person` un-shipped; a client memoize MISS
    // returned an empty `nothing` (a swallowed VNA, not a throw); `foreach c in nothing` then threw a
    // NON-VNA error that escaped navigateClientSide before the floor refetch (the URL had already changed)
    // → URL changed, console error, view FROZEN until reload. Navigating from `/` OR `/tasks` to the task
    // member `/tasks/4` (whose `assignee` RefEditor reads the un-shipped Person extent) reproduces it.
    // (A dict entry with an object value is the same render path; dict entries aren't seedable in
    // initialData — AppParse.SeedValue takes only scalars/ref-id arrays/bare ids — so a `tasks` member is
    // its deterministic deep-link analogue.)
    public static InstanceDescription DemoCollectionsDb() =>
        InstanceDescriptionLoader.Load(File.ReadAllText(AppFixture(6)));

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
    // run-once setup mints its OWN draft (staged through its own ctx); M11 keys a
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

    // A `text multiline` presentation-attribute fixture (the generic-UI <textarea> slice).
    // `Note.body` is marked `multiline`, `Note.title` is a plain text prop, and `count` is an int —
    // so the member edit page (/notes/2) proves the THREE renderings side by side: body → <textarea>,
    // title → single-line <input type=text>, count → <input type=number>. The value stays text either
    // way (multiline is presentation only). A dedicated fixture (NOT a committed app, like the enum
    // fixture) — it isolates the mechanism and avoids dragging the designer seed / SchemaBridge in.
    public static InstanceDescription MultilineFixtureDb() =>
        InstanceDescriptionLoader.Load(MultilineFixtureApp);

    public const string MultilineFixtureApp = """
    types
        Db
            notes set of Note
        Note
            title text
            body text multiline
            count int

    initialData
        Db 1
            notes: [2]
        Note 2
            title: "First"
            body: "line one"
            count: 3
    """;

    // An object with OPTIONAL leaf fields of every non-empty-parseable kind: `due` (date),
    // `amount` (decimal), `at` (datetime), plus a required `title`. Rendered by the default
    // self-hosted generic UI (no `ui` section). The Db holds a `reminders` set, so `/reminders`
    // is the set route (create form) and `/reminders/2` the member page (edit form). One reminder
    // is seeded with `due` set so the clear-then-empty edit scenario has a value to clear; its
    // `amount`/`at` are left out of the seed (unset). Proves an EMPTY date/decimal/datetime means
    // UNSET — the server round-trips it as the empty leaf instead of force-parsing "" (the bug).
    // A dedicated fixture (NOT a committed app, like the enum fixture) — it isolates the empty-leaf
    // behavior and avoids dragging the designer seed / SchemaBridge into the change.
    public static InstanceDescription OptionalLeavesDb() =>
        InstanceDescriptionLoader.Load(OptionalLeavesApp);

    private const string OptionalLeavesApp = """
    types
        Db
            reminders set of Reminder
        Reminder
            title text
            due date
            amount decimal
            at datetime

    initialData
        Db 1
            reminders: [2]
        Reminder 2
            title: "Seeded"
            due: "2026-01-01"
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
    // The `notes` SET seeds two members so its set table at /notes shows the reference column
    // both ways: Note 4 has no author (→ "(none)") and Note 5 references Ada (id 2, → "2").
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
            notes: [4, 5]
        Person 2
            name: "Ada"
        Person 3
            name: "Grace"
        Note 4
            title: "First note"
        Note 5
            title: "Authored note"
            author: 2
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

    // Code milestone, Stage 4 / Milestone 11: the privacy invariant when a filtered collection is
    // nested inside a MINTED object that itself SHIPS. `var box = { rows: db.people.where(p => p.salary
    // > 100) }` is a top-scope var, so ClientState serializes it; its `rows` array holds the matching
    // rows (Ada 999, Cleo 500). But the render DISPLAYS only the > 600 subset (Ada), so only Ada is an
    // accessed item. The minted box must ship `rows` ACCESS-SCOPED — only the displayed Ada — never the
    // undisplayed-but-matching Cleo's membership. Pins the ship-whole boundary: only a provably-constant
    // sys.schema descriptor ships its full interior; a minted object wrapping a where-result does not.
    // (With the broad "ship any negative-id array nested in a complete object" rule, `rows` shipped its
    // FULL membership — Cleo's object entry leaked even though she was never displayed.)
    public static InstanceDescription NestedFilterPrivacyDb() =>
        InstanceDescriptionLoader.Load(NestedFilterPrivacyApp);

    private const string NestedFilterPrivacyApp = """
    types
        Db
            people set of Person
        Person
            name text
            salary int

    ui
        var box = { rows: db.people.where(p => p.salary > 100) }

        fn render()
            return <main>
                foreach p in box.rows.where(q => q.salary > 600)
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

    // Milestone 11 (generic-UI collapse, increment 1): a HAND-WRITTEN `fn render()` that calls the
    // new `sys.resolve(path)` builtin and renders its fields as text — the probe that proves
    // resolve binds a URL to its view-kind + objects IDENTICALLY on both twins (server resolves for
    // first paint over the schema's TypeResolver; client resolves on hydrate over the SHIPPED
    // descriptors). A custom render owns the whole URL space, so the probe runs at EVERY URL and the
    // generic per-type views are not synthesized. The schema covers the owner-bound outcomes a single
    // page exercises: a `notes` object set (set route + member object page), a `lead` reference
    // (single-reference route), and a scalar `settings` dict (dict route + scalar-entry leaf). One
    // note is seeded (title "First", id 2) so the object route binds a real target; settings is left
    // empty (the leaf outcome is schema-decided, target binding is incidental). A test fixture, not a
    // committed app — so no designer-seed regen.
    public static InstanceDescription ResolveProbeDb() =>
        InstanceDescriptionLoader.Load(ResolveProbeApp);

    private const string ResolveProbeApp = """
    types
        Db
            notes set of Note
            lead Person
            settings dict of text by text
        Note
            title text
        Person
            name text

    initialData
        Db 1
            notes: [2]
        Note 2
            title: "First"

    ui

        fn render()
            var r = sys.resolve(path)
            return <main>
                <span class="kind">
                    r.kind
                <span class="prop">
                    r.prop
                <span class="type-name">
                    r.typeName
                <span class="parent-type">
                    r.parentType
                if r.kind == "object"
                    <span class="target-title">
                        sys.field(r.target, "title")
    """;

    // M-auth (the access read floor): a devlog-shaped app — a `Milestone` set + a `User` with a `role`
    // enum (Admin, Member) — carrying ONE type-level read rule that gates `Milestone` to admins. No `ui`
    // section, so the default self-hosted generic UI renders it (the Db root shows the milestones set).
    // A dedicated fixture (NOT a committed app, like the enum fixture) — it isolates the floor and avoids
    // dragging the designer seed / SchemaBridge into the change. `WithAccessRule = false` drops the
    // `access` section so the same shape proves the DORMANT (allow-all) case. The principal is bound
    // floor-first (the harness passes it into SsrRenderer.Render); no password login this slice.
    public static InstanceDescription AccessFixtureDb(bool withAccessRule = true) =>
        InstanceDescriptionLoader.Load(withAccessRule ? AccessFixtureApp : AccessFixtureAppNoRule);

    private const string AccessFixtureTypes = """
    types
        Role enum
            Admin
            Member
        Db
            milestones set of Milestone
            users set of User
        Milestone
            title text
        User
            name text
            role Role
            passwordHash text
    """;

    private const string AccessFixtureSeed = """

    initialData
        Db 1
            milestones: [2]
            users: [3, 4]
        Milestone 2
            title: "Gate #3"
        User 3
            name: "Ada"
            role: "Admin"
        User 4
            name: "Bob"
            role: "Member"
    """;

    // Public so the AppPrint round-trip test can prove the `access` section parses∘prints to identity.
    public const string AccessFixtureApp = AccessFixtureTypes + AccessFixtureSeed + """

    access
        Milestone
            read where currentUser.role == "Admin"
    """;

    private const string AccessFixtureAppNoRule = AccessFixtureTypes + AccessFixtureSeed;

    // The same fixture (types + seed) but carrying an EXPLICIT set of `Milestone` rule lines — so the
    // write-enforcement scenarios can install a single-verb rule (e.g. `edit where currentUser.role ==
    // "Admin"`) and have it take real effect (the rule is PARSED by AppParse exactly as the app's own
    // would be, not hand-built). Each `ruleLine` is a rule line under the `Milestone` type block — its
    // verb list + optional `where` condition. The same deny-by-default ruleset gates reads AND the
    // mutation seam, so a single-verb rule here activates the floor for that verb only.
    public static InstanceDescription AccessFixtureWithRules(params string[] ruleLines) =>
        InstanceDescriptionLoader.Load(
            AccessFixtureTypes + AccessFixtureSeed + "\n\naccess\n    Milestone\n" +
            string.Concat(ruleLines.Select(l => "        " + l + "\n")));

    // The seeded principal ids in AccessFixtureDb (the admin Ada, the member Bob), so a step can bind the
    // current user by role without re-deriving ids. These mirror the initialData seed above.
    public const int AccessAdminId = 3;
    public const int AccessMemberId = 4;

    // The admin's plaintext password (M-auth login). The seed step hashes it (AuthCrypto.Hash) into Ada's
    // `passwordHash` so a `login` action can verify against the real PBKDF2 hash. NOT in initialData —
    // the seed is in friendly scalar form, so the hash is written into the store directly by the step.
    public const string AccessAdminPassword = "hunter2";

    // The seeded "Gate #3" milestone's id (a set member of Db.milestones) — the write scenarios edit /
    // delete it by id and assert the store directly. Mirrors the initialData seed above.
    public const int AccessMilestoneId = 2;

    // The principal bound for the next render (M-auth) — the id of the `User` the request acts as, or null
    // (anonymous). A step sets it; the render step passes it into SsrRenderer.Render. This is the
    // floor-first harness hook the locked slice calls for (no WS login/bind handshake).
    public int? PrincipalUserId { get; set; }

    // The `Milestone` access rule lines installed via the "Given the access rule" step (accumulated across
    // the Background read rule + a write scenario's verb rule). Rebuilding from the full set keeps every
    // declared rule active at once — an app realistically carries read AND write rules — and each is parsed
    // by AppParse exactly as the app's own would be.
    public readonly List<string> AccessRuleLines = new();

    // ── storage ───────────────────────────────────────────────────────────────

    public string DataFilePath { get; set; } = Path.GetTempFileName();
    public IInstanceStore? Store { get; set; }

    // Field values typed since the last Save — the persistence gate shared by ALL fill paths and
    // BOTH Save steps. A fill step ("I set the … field to" / "I fill the … field with") records the
    // value here; a Save step ("I save" / "I save the form") then polls the persisted store for each,
    // so the edit is provably on disk before the scenario reads it (or navigates and re-renders from
    // it). The gate lives on the shared context, not in one step class, so it covers a scenario that
    // fills via one binding and saves via another (e.g. ObjectModel's fill → CRM's save → navigate).
    public readonly List<string> PendingEditValues = new();

    // Block until every pending edit has reached the persisted store, then clear them. Polls the
    // store FILE (the WS commit rewrites it atomically) via the blessed Polling.EventuallyAsync — the
    // instant all values are present it returns. A no-op when nothing was filled (a Save with no prior
    // fill — e.g. a checkbox toggle — gates on its own assertion instead). Empty/cleared fields are
    // not gated here (an empty string is always "contained"); their assertions poll the field itself.
    // The persist can be slow to LAND under peak full-suite load (not lost — fill and save both gate on
    // WaitReadyAsync upstream); the wide Polling default absorbs that spike.
    public async Task AwaitPendingEditsAsync()
    {
        foreach (var value in PendingEditValues)
            if (value.Length > 0)
                await Polling.EventuallyAsync(
                    () => File.ReadAllText(DataFilePath).Contains(value),
                    $"the edit '{value}' to persist");
        PendingEditValues.Clear();
    }

    // ── server ────────────────────────────────────────────────────────────────

    public TestInstanceServer? Server { get; set; }
    public string BaseUrl => Server?.BaseUrl ?? "";

    // ── kernel host (milestone 10) ──────────────────────────────────────────────

    // A running multi-instance kernel and the temp directory holding its app fixtures,
    // registry (kernel.json), and derived data files — all cleaned up in Hooks.
    public KernelHost? Kernel { get; set; }
    public string? KernelDir { get; set; }

    // The designer's mount base (`/apps/designer`) when a kernel-backed browser is pointed at it —
    // addressing is by PATH now, so a step navigating to a designer route prefixes this (the `<a href>`
    // links the designer emits already carry it via the SSR/client edge). Empty until set.
    public string DesignerBase { get; private set; } = "";

    // A designer route URL: the designer's mount base + the root-relative route (e.g. "/instances" →
    // "/apps/designer/instances"). Used by DesignerSteps' explicit navigations (clicked links carry the
    // prefix already).
    public string DesignerUrl(string route) => DesignerBase + route;

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

        // Each target hosts the REAL committed app whose label it carries (todo → instances/2's app, crm
        // → instances/3's, …), so the kernel's first-boot seed reverse-projects each into its REAL Design.
        // Each references its design by an EXPLICIT designId — the committed kernel.json's designId for
        // that label — so the dropdowns start correct. The designer itself (id 1) carries a designId too
        // (its OWN "designer" self-design). Addressing is by PATH now, so there are NO per-instance ports —
        // the two kernel-level ports go on the registry header, and each instance is served at /apps/<name>.
        var appPort = FreePort();
        var assetPort = FreePort();
        var designIds = DesignIdsByLabel();
        var entries = new List<string> { RegistryEntryJson(1, "designer", designIds["designer"]) };
        foreach (var (id, label) in targets)
        {
            WriteIdApp(dir, id, File.ReadAllText(CommittedAppForLabel(label)));
            entries.Add(RegistryEntryJson(id, label, designIds[label]));
        }

        File.WriteAllText(Path.Combine(dir, "kernel.json"),
            "{\n" +
            $"  \"appPort\": {appPort},\n  \"assetPort\": {assetPort},\n" +
            "  \"instances\": [\n    " + string.Join(",\n    ", entries) + "\n  ]\n}");

        var registry = RegistryReader.Read(Path.Combine(dir, "kernel.json"));
        Kernel = new KernelHost(dir, Path.Combine(dir, "kernel.json"), appPort, assetPort);
        await Kernel.StartAsync(KernelHost.SpecsFor(registry, dir));

        var designer = Kernel.Instances.Single(i => i.Spec.Id == 1);

        // The designer is mounted at /apps/designer; the browser BaseURL is the app port, and steps
        // navigate to DesignerUrl(route) (the designer's emitted links already carry the mount prefix).
        DesignerBase = HostedInstance.MountBaseFor(designer.Spec.App);
        Page = await SharedBrowser.NewPageAsync($"http://localhost:{appPort}");
        return designer;
    }

    // The committed app document file for a label (e.g. "todo" → instances/2/app.app): the SAME app the
    // production kernel hosts for that label, located by reading the committed kernel.json (app label →
    // its instance id) and resolving its id-dir. The kernel reverse-projects this file into the design,
    // so hosting the real app here gives the designer the real design (real types + real ui).
    private static string CommittedAppForLabel(string label)
    {
        var registry = RegistryReader.Read(
            Path.Combine(AppContext.BaseDirectory, "kernel.json"));
        var entry = registry.Instances.FirstOrDefault(e => e.App == label)
            ?? throw new InvalidOperationException($"No committed instance labelled '{label}' in kernel.json.");
        return AppFixture(entry.Id);
    }

    private static void WriteIdApp(string dir, int id, string appDoc)
    {
        var idDir = AppPaths.IdDirFor(dir, id);
        Directory.CreateDirectory(idDir);
        File.WriteAllText(Path.Combine(idDir, "app.app"), appDoc);
    }

    private static string RegistryEntryJson(int id, string label, int? designId = null)
    {
        var did = designId.HasValue ? $", \"designId\": {designId.Value}" : "";
        return $"{{ \"id\": {id}, \"app\": \"{label}\"{did} }}";
    }

    // The seeded design id for a label (e.g. "crm") — so a step can assert an instance now records that
    // design's id after Apply. Reads the SAME source of truth the kernel seeds from: kernel.json maps an
    // instance's display label to its designId, and the kernel's first-boot seed mints each Design AT its
    // instance's designId. The designer no longer embeds the design library in its initialData, so this
    // reads the registry, not instances/1's (now-empty) initialData.
    public int DesignIdForLabel(string label) => DesignIdsByLabel()[label];

    // Map each design's label → its id, read from the committed kernel.json (the registry). The kernel
    // seeds the design-host with one Design per registered instance that has a `designId`, at id ==
    // designId, labelled with the instance's `app` name — so a target labelled "todo" resolves to the
    // design id its kernel.json entry references, the same value the fixture writes into its own
    // kernel.json. Reading the registry (not instances/1's initialData) matches the new single-source
    // model: each app's own app.app is its design, kernel.json holds the link.
    private static Dictionary<string, int> DesignIdsByLabel()
    {
        var registry = RegistryReader.Read(
            Path.Combine(AppContext.BaseDirectory, "kernel.json"));
        var map = new Dictionary<string, int>();
        foreach (var entry in registry.Instances)
            if (entry.DesignId is { } designId)
                map[entry.App] = designId;
        return map;
    }

    // A genuinely free TCP port, never handed out twice this run (see PortAllocator) — so two parallel
    // scenarios can't be given the same port and have a browser reach the wrong instance. Public so a step
    // that fills the create-instance form's port inputs can pick free ports (the kernel rejects a port
    // collision, so a hard-coded pair would flake against the other in-process hosts).
    public static int FreePort() => PortAllocator.Next();
}
