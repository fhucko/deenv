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
    // committed fixture in its id-dir (instances/3/app.deenv).
    public static InstanceDescription CrmDb() =>
        InstanceDescriptionLoader.LoadFile(AppFixture(3));

    // The committed default app (DeEnv/instances/2/app.deenv): the todo app — types, initialData seed,
    // and ui code in one text document; tests drive the real single source of truth.
    public static InstanceDescription TodoDb() =>
        InstanceDescriptionLoader.LoadFile(AppFixture(2));

    // A committed app fixture, resolved by its id-dir (instances/<id>/app.deenv) under the test output
    // — the same id-based layout the kernel hosts. Storage is fully id-based; the file name ("app")
    // no longer carries the app's identity, the id-dir does.
    public static string AppFixture(int id) =>
        Path.Combine(AppContext.BaseDirectory, "instances", id.ToString(), "app.deenv");

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

    // Atomic-commit Step B: an OBJECT that holds a SET, reachable as an object PAGE. The Db's `orders` set
    // holds an Order (a scalar `title` + a nested `lines` set of Line). Navigating to /orders/2 renders the
    // Order's ObjectForm — a scalar field (so it HAS a Save) + an inline `lines` SetTable. Adding a Line
    // through that inline create form STAGES the new Line into the Order form's staging ctx (it does NOT
    // persist on the card's Add); the Order form's Save (ctx.commit) is what persists it — the generic-create
    // DEFERRAL. Contrast the top-level /notes route, whose SetTable sits under the LIVE page ctx (no Save), so
    // its creates persist on Add. The seeded Order is id 2.
    public static InstanceDescription NestedSetCreateDb() =>
        InstanceDescriptionLoader.Load(NestedSetCreateApp);

    private const string NestedSetCreateApp = """
    types
        Db
            orders set of Order
        Order
            title text
            lines set of Line
        Line
            label text

    initialData
        Db 1
            orders: [2]
        Order 2
            title: "First order"
    """;

    // A Db holding an EMPTY set: `notes` is unseeded, so it materializes as a zero-member set (SeededFields
    // still mints its StoredSet id, so adding a member works). Drives the empty-state scenario — a zero-member
    // set table renders a "No <Element> yet" line under the header instead of a bare header that reads as
    // broken; adding a member through the create form flips the empty-state to a data row (it is reactive to
    // membership). Note has a single `title text` prop so the create form is a one-field add (no empty-date gap).
    public static InstanceDescription EmptyCollectionDb() =>
        InstanceDescriptionLoader.Load(EmptyCollectionApp);

    private const string EmptyCollectionApp = """
    types
        Db
            notes set of Note
        Note
            title text

    initialData
        Db 1
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
    // committed fixture in its id-dir (instances/4/app.deenv).
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

    // Client data layer, slice 3 (+ a slice-4 discriminator case): throwing-handler shapes for the
    // atomic-rollback / VNA-branch proofs.
    //
    //  • `bumpThenThrow(c)` makes TWO real writes to a persisted (positive-id) db object and THEN throws
    //    (the trailing `c.name.add(c)` calls a collection method on a text value — a runtime "cannot read
    //    'add' on a non-object", which the validator passes but execution rejects). Each write is an
    //    objectPropChange mutation that, pre-slice-3, applied + SENT per statement — so a throw after them
    //    leaked both partial writes (locally and to the server). The atomic transaction must roll BOTH back.
    //
    //  • `setRefThenThrow(c)` performs a REFERENCE set (sys.setRef on a positive-id object) and THEN throws.
    //    setRef stages the prop in the journal but ALSO, outside the entry, sets needsServerData = true +
    //    invalidateExtents() (coarse-staling every extent: memo entry). The journal undo does NOT reverse
    //    those, so before the abort-state fix the rolled-back handler still left needsServerData set + the
    //    extent entries stale → the abort's renderUi → maybeRefetch SENT a refetch (a partial trace + a
    //    half-state: stale extent memos over a reverted model). The fix captures/restores that out-of-journal
    //    state on the non-VNA abort, so the rolled-back handler leaves ZERO trace and triggers NO refetch.
    //
    //  • `bumpThenSchemaMiss(c)` is the slice-4 DISCRIMINATOR case: a REAL write (c.a = 1 → an
    //    objectPropChange that BUFFERS a send) and THEN a `sys.schema("Counter")` read. A handler runs under
    //    memoBypass (ui.ts runWithMemoBypass), and under bypass the client's execSchema's memoize calls its
    //    compute DIRECTLY (no cache consult) — whose body unconditionally throws "Value not available". So
    //    this is a SPURIOUS post-write VNA: the descriptor IS shipped, but the bypass forces a re-compute that
    //    throws. runHandlerTransaction's VNA branch must tell it apart from a genuine action-miss by the
    //    `didWork` discriminator (a send was ALREADY buffered): a write-then-VNA must take the FLUSH +
    //    RE-THROW path (the real write persists, NO pendingAction armed, NO action-carrying refetch), NOT the
    //    action-miss path (which would abort the write + plan a server harvest that mis-reads client-only
    //    draft state). Proven by A_write_then_spurious_VNA_handler_flushes_and_does_not_arm_an_action.
    //
    // `link Counter` is the single object-typed (reference) prop setRef points at; two int fields so the
    // test can assert exact values in the store. The render iterates sys.extent("Counter") (rather than
    // db.items) so a real `extent:Counter` memo entry exists at click time — that is what setRef's
    // invalidateExtents() stales, so the setRef test can prove the stale-flag restore, not just the
    // needsServerData restore. (The single seeded Counter is in both the extent and items, so the
    // bumpThenThrow assertions — span.a/span.b, the name lookup, the store readback — are unaffected.)
    public static InstanceDescription AtomicHandlerUiDb() =>
        InstanceDescriptionLoader.Load(AtomicHandlerUiApp);

    private const string AtomicHandlerUiApp = """
    types
        Db
            items set of Counter
        Counter
            name text
            a int
            b int
            link Counter

    ui

        fn bumpThenThrow(c)
            c.a = 1
            c.b = 2
            c.name.add(c)

        fn setRefThenThrow(c)
            sys.setRef(c, "link", c)
            c.name.add(c)

        fn bumpThenSchemaMiss(c)
            c.a = 1
            sys.schema("Counter")

        fn render()
            return <main>
                foreach c in sys.extent("Counter")
                    <div>
                        <span class="name">
                            c.name
                        <span class="a">
                            c.a
                        <span class="b">
                            c.b
                        <button class="bump" onClick={() => bumpThenThrow(c)}>
                            "Bump"
                        <button class="setref" onClick={() => setRefThenThrow(c)}>
                            "SetRef"
                        <button class="schemamiss" onClick={() => bumpThenSchemaMiss(c)}>
                            "SchemaMiss"
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
            password password
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

    // The PUBLIC-ROADMAP variant (the sign-in affordance + user-management e2e): the same shape + seed, but
    // the Milestone is PUBLICLY readable (a bare `read` rule) with writes gated to the admin — so an
    // anonymous visitor reads the data AND is offered a <SignInBar> (the app is NOT anonymousLockedOut).
    // User is admin-only (no anonymous enumeration; the admin may read+write users for management). Mirrors
    // the devlog dogfood's policy.
    public static InstanceDescription AccessPublicFixtureDb() =>
        InstanceDescriptionLoader.Load(AccessPublicFixtureApp);

    public const string AccessPublicFixtureApp = AccessFixtureTypes + AccessFixtureSeed + """

    access
        Milestone
            read
            * where currentUser.role == "Admin"
        User
            * where currentUser.role == "Admin"
    """;

    // The user-management menu link resolves the User set BY TYPE, not by the name "users". This fixture
    // names the root's `set of User` prop `members` instead of `users` — everything else (the User type, the
    // role enum, the seed, the public-roadmap policy) matches AccessPublicFixtureDb. The framework finds the
    // principal set by type (AdminSeed.UsersSetPath filters on `p.Type == "User"`), so the seeded admin lands
    // in `members`; a `<UserMenu>` whose "Users" link hard-coded `/users` would 404. The link is computed in
    // Code from the schema descriptor (the root's set-of-`isPrincipal` prop), so it must point at `/members`.
    // A test fixture (not a committed app), so no designer-seed regen. The admin-only User rule makes
    // `canManageUsers` true for the admin, so the menu link renders.
    public static InstanceDescription AccessRenamedUserSetDb() =>
        InstanceDescriptionLoader.Load(AccessRenamedUserSetApp);

    private const string AccessRenamedUserSetApp = """
    types
        Role enum
            Admin
            Member
        Db
            milestones set of Milestone
            members set of User
        Milestone
            title text
        User
            name text
            role Role
            password password

    initialData
        Db 1
            milestones: [2]
            members: [3, 4]
        Milestone 2
            title: "Gate #3"
        User 3
            name: "Ada"
            role: "Admin"
        User 4
            name: "Bob"
            role: "Member"

    access
        Milestone
            read
            * where currentUser.role == "Admin"
        User
            * where currentUser.role == "Admin"
    """;

    // M-auth floor-hardening (Fix 1 — sys.extent gating): the SAME shape + the `Milestone read` rule, but
    // with a CUSTOM `fn render()` that LISTS the Milestone extent via `sys.extent("Milestone")` (the seam
    // the reference picker uses: `foreach c in sys.extent(target)`). This is the exposure the graph floor
    // does NOT cover — a custom render (or a ref picker) lists a read-denied type's rows directly. Each row
    // ships its title in a `.extent-row`, so a scenario asserts an admin sees "Gate #3" as a candidate and
    // a member sees none. (A plain `<main>`/`<div>` render — no tag name resolves to a component, avoiding
    // the name-resolution footgun.)
    public static InstanceDescription AccessExtentFixtureDb() =>
        InstanceDescriptionLoader.Load(AccessExtentFixtureApp);

    private const string AccessExtentFixtureApp = AccessFixtureTypes + AccessFixtureSeed + """

    access
        Milestone
            read where currentUser.role == "Admin"

    ui

        fn render()
            return <main>
                foreach m in sys.extent("Milestone")
                    <div class="extent-row">
                        m.title
    """;

    // Client data layer, slice 1a (component-state seed): the SAME devlog shape + seed + the admin-only
    // `Milestone read` rule, with a CUSTOM `fn render()` whose whole UI is ONE stateful root component
    // `<panel>`. The panel holds `var state = { open: false }`; its view renders the milestone rows
    // (each `m.title` in a `.gated-row`) ONLY when `state.open`. So with the panel's slot UNSEEDED
    // (open:false, the setup default) nothing reads `db.milestones` → "Gate #3" is never harvested →
    // absent from the shipped document; with the slot SEEDED `state = { open: true }` the rows render →
    // `db.milestones` is read → structural privacy harvests "Gate #3" → it ships. This is the
    // <UserAdmin>-behind-`if state.managing` footgun in miniature, at a STABLE slot: the panel is a
    // value-position root component, so its slot key is the empty-path `comp:`. A test fixture (not a
    // committed app), so no designer-seed regen. The Milestone rule means the harvest is floor-gated.
    public static InstanceDescription AccessSeedFixtureDb() =>
        InstanceDescriptionLoader.Load(AccessSeedFixtureApp);

    private const string AccessSeedFixtureApp = AccessFixtureTypes + AccessFixtureSeed + """

    access
        Milestone
            read where currentUser.role == "Admin"

    ui

        fn panel()
            var state = { open: false }
            fn render()
                return <div class="panel">
                    if state.open
                        foreach m in db.milestones
                            <div class="gated-row">
                                m.title
            return render

        fn render()
            return <panel>
    """;

    // The component's render-slot key in AccessSeedFixtureApp: the panel is a value-position root
    // component (returned directly from `fn render()`), so its slot path is empty and its key is the
    // bare "comp:". The seed step targets this; whole-object overwrite replaces the panel's `state`.
    public const string AccessSeedPanelSlot = "comp:";

    // Client data layer, slice 1b (the CLIENT SHIP + server reconstruct round-trip — proven in a real
    // browser): the SAME devlog shape + seed, with a CUSTOM `fn render()` whose whole UI is ONE stateful root
    // component `<panel>` carrying `var state = { open: false }` and a "Show" button that flips it open. Its
    // view renders the milestone rows (each `m.title` in a `.gated-row`) ONLY when `state.open`.
    //
    // THE ROUND-TRIP (why it proves 1b and nothing else does): the root render is JUST the panel, and the
    // panel reads `db.milestones` ONLY inside `if state.open` — so with open:false (first paint) NOTHING reads
    // it, and STRUCTURAL PRIVACY (the memo/dependency harvest, not the access floor) ships no Milestone →
    // "Gate #3" is absent. Clicking "Show" flips `state.open` CLIENT-side; the re-render now reads the
    // un-shipped `db.milestones`, the swallowed VNA sets needsServerData, and maybeRefetch fires — carrying
    // the new `slotState` ({ "comp:" → { state: { open: true } } }). The server seeds it (ApplySeed),
    // reproduces the open panel, reads `db.milestones`, harvests "Gate #3", and ships it; the client merges +
    // repaints the rows. So the rows appear ONLY via the ship→seed round-trip — there is no other server-side
    // reader of `db.milestones`, so reverting EITHER the ws.ts ship OR the HandleRefetch reconstruct leaves
    // the panel permanently empty (the empty-popup footgun, controlled).
    //
    // OBJECT state (`var state = { open }`) — REQUIRED, not incidental: a component-local var is non-top, and
    // ONLY an object-prop write (`state.open = …` → invalidateProp on the object's id) re-renders a component
    // (a bare scalar `var` in a component scope is NOT a tracked dep, so its assignment never re-renders). So
    // the universal component-state shape is an object, and the round-trip ships it BY VALUE (a transient,
    // negative-id object — see ws.ts slotState's ship-rule). This is pure VIEW-STATE (a popup flag), NOT a
    // draft that drives a query (the harvest depends on WHICH branch runs, never on the field VALUES — and
    // stays floor-gated), so it does not pull in the deferred draft-driven-query work (spec I3).
    //
    // PUBLIC read rule (anonymous may read) so the round-trip needs NO login: the access-FLOOR layer (the
    // harvest stays gated through a refetch) is already proven by 1a — 1b isolates the CLIENT-ship + SERVER-
    // reconstruct mechanism, where login would only add a flaky moving part. The custom render owns the whole
    // page (no generic-UI sign-in chrome), so an anonymous visitor exercises the loop directly. A test fixture
    // (not a committed app), so no designer-seed regen.
    public static InstanceDescription AccessToggleFixtureDb() =>
        InstanceDescriptionLoader.Load(AccessFixtureTypes + AccessFixtureSeed + """

    access
        Milestone
            read

    ui

        fn panel()
            var state = { open: false }
            fn show()
                state.open = true
            fn render()
                return <div class="panel">
                    <button class="show" onClick={show}>
                        "Show"
                    if state.open
                        foreach m in db.milestones
                            <div class="gated-row">
                                m.title
            return render

        fn render()
            return <panel>
    """);

    // Client data layer, slice 4 (the ACTION-MISS round-trip — proven in a real browser): a button handler
    // that READS data the first paint never shipped, then WRITES based on it. The whole UI is a foreach over
    // `db.counters`; each row SHOWS the counter's value `a` (so `a` is shipped) and a "Bump" button, but the
    // render NEVER reads the counter's self-`link` reference — so `c.link` is un-shipped (only ACCESSED props
    // ship, structural privacy). The Bump handler increments THROUGH the link: `c.link.a = c.link.a + 1`.
    //
    // THE ROUND-TRIP (why it proves slice 4 and nothing else does): on click the handler's first read is
    // `c.link` — un-shipped on the client → "Value not available". TODAY (slice 3) the handler transaction's
    // VNA branch flushes the pre-throw sends (none here) and re-throws, so the action DIES — `a` stays 0.
    // After slice 4 the VNA is CAUGHT → the handler ABORTS atomically (slice 3's rollback: it made no writes
    // before the miss, so nothing to undo) → a PENDING ACTION is recorded (the handler's fn-id + its render-
    // slot + the live view-state) → a refetch ships that intent → the server reproduces the exact render,
    // finds the handler closure at (slot, fn-id), invokes it READ-ONLY (the increment stages into the
    // throwaway loaded graph, discarded) and HARVESTS its reads (`c.link`, which is `c` itself) → ships them
    // → the client merges + RE-INVOKES the handler over the now-present `c.link`, so `c.link.a = 0 + 1` lands
    // on the visible `a` (0 → 1) and persists (objectPropChange on the positive-id object). There is NO other
    // path that increments `a`, so reverting the slice-4 wiring (catch/record/re-invoke OR the server harvest)
    // leaves the action dead — `a` never moves.
    //
    // `link` is a SELF reference (the seed points the counter's link at its own id) so the harvested target
    // IS the on-screen object — the increment lands on the SAME `a` the test observes, keeping the assertion
    // direct. PUBLIC (no access rules) so the loop needs no login — slice 4 isolates the action-miss
    // mechanism; the floor-gated harvest is already proven by 1a. A test fixture, so no designer-seed regen.
    public static InstanceDescription ActionMissFixtureDb() =>
        InstanceDescriptionLoader.Load(ActionMissFixtureApp);

    private const string ActionMissFixtureApp = """
    types
        Db
            counters set of Counter
        Counter
            a int
            link Counter

    initialData
        Db 1
            counters: [2]
        Counter 2
            a: 0
            link: 2

    ui

        fn bump(c)
            c.link.a = c.link.a + 1

        fn render()
            return <main>
                foreach c in db.counters
                    <div class="counter">
                        <span class="counter-a">
                            c.a
                        <button class="bump" onClick={() => bump(c)}>
                            "Bump"
    """;

    // M-auth floor-hardening (Fix 3 — a throwing condition must DENY, not crash the render): the SAME
    // shape + seed, but the ONLY access rule's condition divides by zero (`1 / 0 == 1`). Integer `/` by a
    // zero divisor throws DivideByZeroException (CodeExecutor.ExecuteInfixOp) — a .NET exception, NOT a
    // CodeRuntimeException. The read floor evaluates the condition while loading the Milestone set member;
    // before the fix the throw ESCAPED AccessFloor.EvaluateCondition's narrow catch and crashed the SSR
    // render (a render-time DoS). After the fix the broad catch denies (fail closed) — the milestone is
    // omitted and the page renders normally. Rebuilt over the same seed so the milestone/users persist.
    public static InstanceDescription AccessDivZeroFixtureDb() =>
        InstanceDescriptionLoader.Load(AccessFixtureTypes + AccessFixtureSeed + """

    access
        Milestone
            read where 1 / 0 == 1
    """);

    // M-auth login 1d (bootstrap): the SAME shape (Role enum + a `users set of User` + the access
    // rules) but with NO seeded users — the chicken-and-egg the seed-admin operation solves (an app
    // with rules and no Admin can never be logged into). Only the Db root is seeded so the `users` set
    // exists to link the new admin into; the seed mints the first User. Used to prove the kernel-side
    // AdminSeed creates a loginable admin from scratch.
    public static InstanceDescription AccessFixtureNoUsers() =>
        InstanceDescriptionLoader.Load(AccessFixtureTypes + AccessFixtureNoUsersSeed + AccessFixtureRules);

    // The bootstrap shape WITHOUT an access section — a User type + role enum but NO rules and no users.
    // Proves the boot bootstrap's rules guard (AdminSeed.SeedIfRuled): a dormant no-auth app is NOT
    // boot-seeded even with credentials present (the kernel must skip it, not seed a User into it).
    public static InstanceDescription AccessFixtureNoUsersNoRules() =>
        InstanceDescriptionLoader.Load(AccessFixtureTypes + AccessFixtureNoUsersSeed);

    private const string AccessFixtureNoUsersSeed = """

    initialData
        Db 1
            milestones: [2]
            users: []
        Milestone 2
            title: "Gate #3"
    """;

    private const string AccessFixtureRules = """

    access
        Milestone
            read where currentUser.role == "Admin"
    """;

    // The same fixture (types + seed) but carrying an EXPLICIT set of `Milestone` rule lines — so the
    // write-enforcement scenarios can install a single-verb rule (e.g. `edit where currentUser.role ==
    // "Admin"`) and have it take real effect (the rule is PARSED by AppParse exactly as the app's own
    // would be, not hand-built). Each `ruleLine` is a rule line under the `Milestone` type block — its
    // verb list + optional `where` condition. The same deny-by-default ruleset gates reads AND the
    // mutation seam, so a single-verb rule here activates the floor for that verb only.
    public static InstanceDescription AccessFixtureWithRules(params string[] ruleLines) =>
        AccessFixtureWithRules(ruleLines, []);

    // The same fixture carrying Milestone rule lines AND User rule lines (M-auth login 1b: setPassword is
    // gated as a `User edit`, so a scenario installs a `User edit where currentUser.role == "Admin"` rule
    // alongside the Background's `Milestone read`). Each block is emitted only when it has lines, so the
    // Milestone-only callers (the read/write scenarios) are unaffected. Both blocks parse through AppParse
    // exactly as the app's own `access` section would.
    public static InstanceDescription AccessFixtureWithRules(string[] milestoneRuleLines, string[] userRuleLines)
    {
        var access = "";
        if (milestoneRuleLines.Length > 0)
            access += "\n    Milestone\n" + string.Concat(milestoneRuleLines.Select(l => "        " + l + "\n"));
        if (userRuleLines.Length > 0)
            access += "\n    User\n" + string.Concat(userRuleLines.Select(l => "        " + l + "\n"));
        return InstanceDescriptionLoader.Load(
            AccessFixtureTypes + AccessFixtureSeed + (access.Length > 0 ? "\n\naccess" + access : ""));
    }

    // The seeded principal ids in AccessFixtureDb (the admin Ada, the member Bob), so a step can bind the
    // current user by role without re-deriving ids. These mirror the initialData seed above.
    public const int AccessAdminId = 3;
    public const int AccessMemberId = 4;

    // The admin's plaintext password (M-auth login). The seed step hashes it (AuthCrypto.Hash) into Ada's
    // `password` field so a `login` action can verify against the real PBKDF2 hash. NOT in initialData —
    // the seed is in friendly scalar form, so the hash is written into the store directly by the step.
    public const string AccessAdminPassword = "hunter2";

    // The User's credential field name in the access fixtures (the M-auth `password` type — `password
    // password` on User). The seed/privacy steps write/read the real hash on this field RAW through the
    // store seam (the store keeps the hash; only the SHIPPED value is blanked to ""), exactly the field
    // the kernel's UserConvention.PasswordFieldName resolves by type.
    public const string AccessPasswordField = "password";

    // ── atomic-commit fixture (AtomicCommit.feature) ─────────────────────────────────────────
    //
    // A minimal two-field app whose sole Item (id 2) has `title text` and `count int`. The `commit`
    // op is tested against this fixture: a batch of field edits is validated-all-then-applied-all-or-none.
    // Carries a Role enum + User set so the access-denial scenario can install an edit rule.
    //
    // A separate fixture (NOT reusing AccessFixtureTypes) so the type/seed shape is self-contained and
    // the count field is unambiguous (the milestone fixture has only `title`).
    public static InstanceDescription TwoFieldCommitFixtureDb(string? itemRuleLines = null) =>
        InstanceDescriptionLoader.Load(TwoFieldCommitApp(itemRuleLines));

    private static string TwoFieldCommitApp(string? itemRuleLines) =>
        TwoFieldCommitTypes + TwoFieldCommitSeed + (itemRuleLines == null ? "" : $"\n\naccess\n    Item\n        {itemRuleLines}");

    private const string TwoFieldCommitTypes = """
    types
        Role enum
            Admin
            Member
        Db
            items set of Item
            users set of User
        Item
            title text
            count int
        User
            name text
            role Role
            password password
    """;

    private const string TwoFieldCommitSeed = """

    initialData
        Db 1
            items: [2]
            users: [3, 4]
        Item 2
            title: "Seed title"
            count: 0
        User 3
            name: "Ada"
            role: "Admin"
        User 4
            name: "Bob"
            role: "Member"
    """;

    // Ids from the seed above. NOTE: the User ids deliberately mirror AccessAdminId (3) / AccessMemberId (4)
    // so the denial scenario can reuse AccessSteps' "the current user is the member" binding — keep them
    // aligned if either seed changes (the principal binds by AccessMemberId, not a TwoField* constant).
    public const int TwoFieldItemId = 2;

    // ── atomic-changeset fixture (AtomicCommit.feature, Step B) ──────────────────────────────────
    //
    // A two-type app for the HEADLINE atomic-changeset proof: an `Item` (id 2, the edit target) and a
    // SEPARATE, unrelated `Tag` type. The `commit` op stages, in ONE ctx, an EDIT to the Item + a CREATE of
    // a Tag + a RELATION (set Db.tags add the new tag) — committed all-or-none. Carries the Role + User set
    // so the denial scenario can install a `Tag create` rule and prove a denied change rolls the WHOLE
    // changeset back (no orphan Tag, the Item edit reverted). `optTagRuleLines` installs a Tag access block.
    public static InstanceDescription AtomicChangesetFixtureDb(string? tagRuleLines = null) =>
        InstanceDescriptionLoader.Load(AtomicChangesetApp(tagRuleLines));

    private static string AtomicChangesetApp(string? tagRuleLines) =>
        AtomicChangesetTypes + AtomicChangesetSeed + (tagRuleLines == null ? "" : $"\n\naccess\n    Tag\n        {tagRuleLines}");

    private const string AtomicChangesetTypes = """
    types
        Role enum
            Admin
            Member
        Db
            items set of Item
            tags set of Tag
            users set of User
        Item
            title text
            count int
        Tag
            label text
        User
            name text
            role Role
            password password
    """;

    private const string AtomicChangesetSeed = """

    initialData
        Db 1
            items: [2]
            users: [3, 4]
        Item 2
            title: "Seed title"
            count: 0
        User 3
            name: "Ada"
            role: "Admin"
        User 4
            name: "Bob"
            role: "Member"
    """;

    // The Item edit target + the Db's tags set id. The tags set is the Db root's (id 1) second collection
    // prop; the store mints collection ids from the counter starting above the seeded max id (4), so Db's
    // items/tags/users sets mint 5/6/7. tags is the 2nd → id 6. (Asserted indirectly: the changeset's set
    // relation names it; a mis-id would fail the commit, surfacing here.)
    public const int AtomicChangesetItemId = 2;
    public const int AtomicChangesetTagsSetId = 6;

    // The seeded "Gate #3" milestone's id (a set member of Db.milestones) — the write scenarios edit /
    // delete it by id and assert the store directly. Mirrors the initialData seed above.
    public const int AccessMilestoneId = 2;

    // The principal bound for the next render (M-auth) — the id of the `User` the request acts as, or null
    // (anonymous). A step sets it; the render step passes it into SsrRenderer.Render. This is the
    // floor-first harness hook the locked slice calls for (no WS login/bind handshake).
    public int? PrincipalUserId { get; set; }

    // The component-state seed for the next render (client data layer, slice 1a) — a map { slotKey →
    // { varName → value } } that reproduces a component's client view-state. A step sets it; the render
    // step passes it into SsrRenderer.Render (the seed-consumption seam; the client SHIP of state is a
    // later slice, so the test injects the seed directly). Null = the unseeded default render.
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, DeEnv.Code.IExecValue>>? Seed { get; set; }

    // The `Milestone` access rule lines installed via the "Given the access rule" step (accumulated across
    // the Background read rule + a write scenario's verb rule). Rebuilding from the full set keeps every
    // declared rule active at once — an app realistically carries read AND write rules — and each is parsed
    // by AppParse exactly as the app's own would be.
    public readonly List<string> AccessRuleLines = new();

    // The `User` access rule lines installed via the "Given the User access rule" step (M-auth login 1b:
    // the `User edit` rule that gates setPassword). Accumulated separately so a rebuild always carries BOTH
    // the Milestone block (Background) and the User block — order-independent across the two steps.
    public readonly List<string> UserAccessRuleLines = new();

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

    // The operator IDE (instances/1/app.deenv) renders `sys.instances` — the kernel's hosted set — so it
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

        // The designer at id 1: the REAL committed instances/1/app.deenv (copied from the test output),
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
        Kernel = new KernelHost(dir, Path.Combine(dir, "kernel.json"), appPort, assetPort, bindLoopback: true);
        await Kernel.StartAsync(KernelHost.SpecsFor(registry, dir));

        var designer = Kernel.Instances.Single(i => i.Spec.Id == 1);

        // The designer is mounted at /apps/designer; the browser BaseURL is the app port, and steps
        // navigate to DesignerUrl(route) (the designer's emitted links already carry the mount prefix).
        DesignerBase = HostedInstance.MountBaseFor(designer.Spec.App);
        Page = await SharedBrowser.NewPageAsync($"http://localhost:{appPort}");
        return designer;
    }

    // The committed app document file for a label (e.g. "todo" → instances/2/app.deenv): the SAME app the
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
        File.WriteAllText(Path.Combine(idDir, "app.deenv"), appDoc);
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
    // model: each app's own app.deenv is its design, kernel.json holds the link.
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
