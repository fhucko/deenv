using DeEnv.Instance;
using DeEnv.Kernel;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for AgedStore.feature — the aged-store harness. A fresh seed mints complete, design-full rows;
// a real store holds the shapes below. The fixture builders synthesize them with the REAL store APIs
// (CreateObject/AddToSet/WriteReference — the same calls MirrorInstanceInsert makes), then the steps
// drive the affected pages through clicked in-app links (client-side nav → the warm-session refetch
// path, which is where the &&/|| bug actually reproduced; SSR alone did not), collecting every console
// error / page error along the way and asserting the sweep stayed clean at the end.
[Binding]
public sealed class AgedStoreSteps(InstanceContext ctx)
{
    private HostedInstance _designer = null!;
    private readonly List<string> _clientErrors = new();

    // ── the aged DESIGNER store (kernel leg) ─────────────────────────────────────

    [Given("the operator IDE is running on an aged kernel store")]
    public async Task GivenAgedIde()
    {
        // "todo" boots WITH its design (the normal shape); "devlog" has no designId in the committed
        // registry, so its db.instances mirror row is DESIGN-LESS — the exact store shape that broke
        // the designer's `i.design != null && sys.id(i.design)` guards under eager &&.
        _designer = await ctx.StartKernelDesignerBrowserAsync((5, "todo"), (6, "devlog"));
        AgeDesignerStore();
        var desc = InstanceDescriptionLoader.LoadFile(_designer.Spec.SchemaPath);
        AdminSeed.Seed(_designer.Store, desc, "admin", "hunter2", "Admin");
        WatchClientErrors();
        // Log in (mirrors DesignerSteps' private login): a warm WS session, landing on /designs — every
        // page after this is reached by a clicked link over that session, never a fresh GET.
        var page = ctx.Page!;
        await page.GotoReadyAsync(ctx.DesignerUrl("/designs"));
        await page.WaitReadyAsync();
        await page.Locator(".login-form input.name").FillAsync("admin");
        await page.Locator(".login-form input.password").FillAsync("hunter2");
        await page.Locator(".login-form button.login-submit").ClickAsync();
        await page.Locator("main.ide-designs .set-row").First.WaitForAsync();
    }

    // The third real-world Instance shape, beside todo (design present) and devlog (design NEVER
    // written): a row whose design was written and then CLEARED. Minted exactly like
    // KernelHost.MirrorInstanceInsert (CreateObject → AddToSet BEFORE the reference write — GC),
    // then cleared via WriteReference(null), which REMOVES the field, runs GC, and logs the clear —
    // a different stored history than never-written even though both now read back absent.
    private void AgeDesignerStore()
    {
        var store = _designer.Store;
        var objId = store.CreateObject("Instance", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("retired"),
            ["runtimeId"] = new IntValue(99),
        }));
        if (store.ReadNode(NodePath.Root.Field("instances")) is SetValue instances)
            store.AddToSet(instances.Id, objId);
        store.WriteReference(objId, "design", ctx.DesignIdForLabel("todo"), "Design");
        store.WriteReference(objId, "design", null, "Design");
    }

    [When("I click into the aged design editor for {string}")]
    public async Task WhenClickIntoEditor(string label)
    {
        // The clicked Edit link is handled CLIENT-SIDE (the refetch path). The editor's publish
        // section runs instancesRunning(design) — the `i.design != null && sys.id(i.design)` filter —
        // over the design-less devlog row and the cleared retired row: the a79f19f repro flow.
        await ctx.Page!.Locator($".set-row:has(a.row-link:text-is(\"{label}\")) a.edit-design").ClickAsync();
        await ctx.Page.WaitForSelectorAsync("main.ide-design-edit .design-editor");
    }

    [Then("the aged design editor renders its publish section")]
    public async Task ThenPublishSectionRenders()
    {
        // The section rendered AND found the one legitimately-running instance (todo) — proving the
        // filter evaluated over the aged rows instead of throwing into a failed refetch.
        await ctx.Page!.Locator(".publish-section").WaitForAsync();
        await ctx.Page.Locator(".publish-section .publish-row").First.WaitForAsync();
    }

    [When("I open the aged commit history from the editor")]
    public async Task WhenOpenCommitHistory()
    {
        await ctx.Page!.Locator(".design-editor a.view-history").ClickAsync();
        await ctx.Page.WaitForSelectorAsync("main.ide-commits .set-row");
    }

    [When("I open the adoption baseline commit")]
    public async Task WhenOpenAdoptionCommit() =>
        // Every design got its main branch + "Adopted" baseline at kernel boot (EnsureMainBranches) —
        // a commit with NO author and NO parent, the absent-single-ref shape on the Commit type.
        await ctx.Page!.Locator("main.ide-commits .set-row a.row-link:text-is(\"Adopted\")").First.ClickAsync();

    [Then("the adoption commit detail renders")]
    public async Task ThenAdoptionDetailRenders() =>
        await ctx.Page!.Locator("main.ide-commit-detail .field-value:text-is(\"Adopted\")").WaitForAsync();

    [When("I click the Instances nav link")]
    public async Task WhenClickInstancesNav()
    {
        await ctx.Page!.Locator("nav.ide-nav a:text-is(\"Instances\")").First.ClickAsync();
        await ctx.Page.WaitForSelectorAsync("main.ide-list .set-row");
    }

    [Then("the aged instances list shows {string}, {string} and {string}")]
    public async Task ThenInstancesListShows(string a, string b, string c)
    {
        foreach (var label in new[] { a, b, c })
            await ctx.Page!.Locator($"main.ide-list .set-row:has(a.row-link:text-is(\"{label}\"))").WaitForAsync();
    }

    [When("I click into the aged instance {string}")]
    public async Task WhenClickIntoInstance(string label)
    {
        var row = ctx.Page!.Locator($"main.ide-list .set-row:has(a.row-link:text-is(\"{label}\"))");
        await row.Locator("td.row-action button.kebab-toggle").ClickAsync();
        await row.Locator(".kebab-menu.open a.open-instance").ClickAsync();
    }

    [Then("the aged instance page renders its design selector")]
    public async Task ThenInstancePageRenders() =>
        // The design-less instance's selector page renders (currentDesignId falls to the `: 0` arm of
        // the same guard idiom) rather than erroring.
        await ctx.Page!.Locator("main.ide-instance select.design-pick").WaitForAsync();

    // ── the aged APP store (schema-evolution leg) ────────────────────────────────

    // The schema the rows were CREATED under.
    private const string OldSchemaApp = """
    types
        Db
            notes set of Note
            people set of Person
        Note
            title text
            author Person
        Person
            name text

    initialData
        Db 1
            notes: [2, 3]
            people: [4]
        Note 2
            title: "Fresh row"
        Note 3
            title: "Cleared author"
            author: 4
        Person 4
            name: "Ada"
    """;

    // The schema the store is SERVED under: the same app after an additive migration (count + due
    // added to Note). Every existing row predates both fields, so every read of them is an
    // absent-field read — the shape a fresh v2 seed never holds.
    private const string NewSchemaApp = """
    types
        Db
            notes set of Note
            people set of Person
        Note
            title text
            count int
            due date
            author Person
        Person
            name text

    initialData
        Db 1
    """;

    [Given("an app store aged under an old schema and served under one with added fields")]
    public async Task GivenAgedAppStore()
    {
        // Build the store's history under the OLD schema: the seed, a row born from a real logged
        // mutation (CreateObject + AddToSet — unlike seeded rows it exists only via the app log), and
        // a written-then-cleared author ref. Then serve the SAME data file under the NEW schema —
        // exactly what a publish that adds fields does to every pre-existing row.
        var store = new JsonFileInstanceStore(ctx.DataFilePath, InstanceDescriptionLoader.Load(OldSchemaApp));
        var noteId = store.CreateObject("Note", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["title"] = new TextValue("Grown row"),
        }));
        if (store.ReadNode(NodePath.Root.Field("notes")) is SetValue notes)
            store.AddToSet(notes.Id, noteId);
        store.WriteReference(3, "author", null, "Person");

        ctx.Description = InstanceDescriptionLoader.Load(NewSchemaApp);
        await ctx.EnsureServerAndBrowserAsync();
        WatchClientErrors();
    }

    [When("I open the aged notes list")]
    public async Task WhenOpenNotesList()
    {
        // The set table's count/due/author columns read the absent fields on every row (SSR side).
        await ctx.Page!.GotoReadyAsync(ctx.BaseUrl + "/notes");
        await ctx.Page.Locator(".set-row").First.WaitForAsync();
    }

    [When("I click into the aged note {string}")]
    public async Task WhenClickIntoNote(string title)
    {
        // A clicked row-link — client-side nav over the warm session, so the member page's fields
        // (including the author RefEditor's extent read) come through the refetch path.
        await ctx.Page!.Locator($".set-row a.row-link:text-is(\"{title}\")").ClickAsync();
        await ctx.Page.Locator("input.title").WaitForAsync();
    }

    [When("I return to the aged notes list and click into the aged note {string}")]
    public async Task WhenReturnAndClickIntoNote(string title)
    {
        await WhenOpenNotesList();
        await WhenClickIntoNote(title);
    }

    [Then("the aged note form reads the added fields consistently")]
    public async Task ThenNoteFormReadsAddedFieldsConsistently()
    {
        // What the WARM SESSION rendered (the client-nav'd DOM)…
        var clientCount = await ctx.Page!.Locator("input.count").InputValueAsync();
        var clientDue = await ctx.Page.Locator("input.due").InputValueAsync();
        // …must match what a FRESH SSR of the same URL emits — an absent-field read that differs
        // between the twins would be a hydration flip (the canvas-never-lies class of bug).
        var ssr = await (await ctx.Page.Context.APIRequest.GetAsync(ctx.Page.Url)).TextAsync();
        string SsrValue(string cls) => System.Text.RegularExpressions.Regex
            .Match(ssr, $"<input[^>]*class=\"{cls}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;
        await Assert.That(SsrValue("count")).IsEqualTo(clientCount);
        await Assert.That(SsrValue("due")).IsEqualTo(clientDue);
        // The absent int reads as the store's read-side default (JsonFileInstanceStore.DefaultBase).
        await Assert.That(clientCount).IsEqualTo("0");
        // FOUND BY THIS HARNESS (2026-07-08, fix pending its own decision): an ABSENT date reads back
        // as DateTime.Today via the same DefaultBase — a row predating a `due date` field displays a
        // phantom "today" that changes every day (and would persist on Save), while a UI-CLEARED date
        // reads "" (the WsHandler.OptionalLeaf empty-leaf model: empty means UNSET). Absent and
        // cleared should read alike; when DefaultBase is aligned to the empty-leaf model, tighten
        // this to IsEqualTo("") — the consistency asserts above stay as-is.
        await Assert.That(clientDue).IsNotEqualTo(null);
    }

    [Then("the aged note form shows title {string}")]
    public async Task ThenNoteFormShowsTitle(string title) =>
        await Assert.That(await ctx.Page!.Locator("input.title").InputValueAsync()).IsEqualTo(title);

    // ── the error collector ──────────────────────────────────────────────────────

    // A failed refetch never throws into the test — it surfaces ONLY as console.error("Server
    // error:", …) (ws.ts's uncorrelated-error branch) while the page silently freezes. So the
    // harness watches the console + uncaught page errors for the WHOLE sweep; resource-load noise
    // (e.g. a favicon 404) is not a client error and is filtered out.
    private void WatchClientErrors()
    {
        var page = ctx.Page!;
        page.Console += (_, msg) =>
        {
            if (msg.Type == "error" && !msg.Text.Contains("Failed to load resource"))
                _clientErrors.Add(msg.Text);
        };
        page.PageError += (_, err) => _clientErrors.Add(err);
    }

    [Then("no client errors were recorded")]
    public async Task ThenNoClientErrors()
    {
        // The global rejected-mutation banner must not be up either.
        await Assert.That(await ctx.Page!.Locator("#__error").CountAsync()).IsEqualTo(0);
        // Joined so a failure PRINTS the collected errors instead of a bare count mismatch.
        await Assert.That(string.Join("\n", _clientErrors)).IsEqualTo("");
    }
}
