using DeEnv.Kernel;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

[Binding]
public sealed class KernelSteps(InstanceContext ctx)
{
    // The simplest instance: an object Db with one bool, rendered as a single checkbox by the
    // self-hosted generic UI. Distinct ids → distinct id-dirs → distinct stores; distinct NAMES →
    // distinct /apps/<name> mounts. Addressing is by PATH now (no per-instance ports).
    private const string BoolApp = """
    types
        Db
            ready bool
    """;

    // A fully-custom app that renders the kernel's `sys.instances` global — the read-only list of
    // hosted instances (name + mount path) under the framework `sys` namespace. Proves the read path
    // end-to-end through the real scope chain. The instances are addressed by PATH now, so each row
    // shows i.app (the name) + i.path (/apps/<name>) as TEXT — i.path is a cross-instance ABSOLUTE
    // path (it points at ANOTHER instance's mount), so it is informational data, not the current app's
    // own intra-mount navigation (which is what the base seam prefixes).
    private const string ConsoleApp = """
    types
        Db
            name text

    ui
        fn render()
            return <main>
                foreach i in sys.instances
                    <div class="instance-row">
                        <span class="app">
                            i.app
                        <span class="path">
                            i.path
    """;

    // A generic-UI app (no custom render): its Db root self-hosts with breadcrumbs + a nested set
    // link, so a path-mounted render exercises the base seam's link/breadcrumb prefixing. Seeded with
    // one note so the set table + nested member link render.
    private const string MountApp = """
    types
        Db
            notes set of Note
        Note
            title text

    initialData
        Db 1
            notes: [2]
        Note 2
            title: "First"
    """;

    private HostedInstance Alpha => ctx.Kernel!.Instances[0];
    private HostedInstance Beta => ctx.Kernel!.Instances[1];

    // The kernel's two shared ports (chosen per scenario; addressing is by path, not per-instance).
    private int _appPort;
    private int _assetPort;

    private Exception? _configError;

    // The console list scenario: the served console page + the tokens it must contain.
    private string _consoleHtml = "";
    private string[] _expectedNames = [];
    private string[] _expectedPaths = [];

    // The create scenarios.
    private HostedInstance? _created;
    private string _createdName = "";
    private string _createdIdDir = "";

    // The clone scenario.
    private HostedInstance? _cloned;
    private string _clonedName = "";

    // The rename scenario.
    private string _oldName = "";
    private Exception? _renameError;

    // ── Given ─────────────────────────────────────────────────────────────────

    [Given("a registry of two named instances")]
    public void GivenRegistry()
    {
        var dir = NewDir();
        WriteApp(dir, 1, BoolApp);
        WriteApp(dir, 2, BoolApp);
        WriteRegistry(dir, ("alpha", 1), ("beta", 2));
    }

    [Given("a registry of one instance")]
    public void GivenRegistryOfOne()
    {
        var dir = NewDir();
        WriteApp(dir, 1, BoolApp);
        WriteRegistry(dir, ("alpha", 1));
    }

    [Given("a registry whose only instance is a console app that lists the instances")]
    public void GivenConsoleRegistryOfOne()
    {
        var dir = NewDir();
        WriteApp(dir, 1, ConsoleApp);
        WriteRegistry(dir, ("console", 1));
    }

    [Given("a registry of one generic-UI instance named {string}")]
    public void GivenGenericUiInstance(string name)
    {
        var dir = NewDir();
        WriteApp(dir, 1, MountApp);
        WriteRegistry(dir, (name, 1));
    }

    // ── When / Given (start) ────────────────────────────────────────────────────

    [When("the kernel starts")]
    [Given("the kernel has started")]
    public async Task WhenKernelStartsAsync()
    {
        var registry = RegistryReader.Read(Path.Combine(ctx.KernelDir!, "kernel.json"));
        ctx.Kernel = new KernelHost(ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"), _appPort, _assetPort);
        await ctx.Kernel.StartAsync(KernelHost.SpecsFor(registry, ctx.KernelDir!));
    }

    // ── create ──────────────────────────────────────────────────────────────────

    [When("the operator creates an instance named {string} from a bool app")]
    [Given("the operator creates an instance named {string} from a bool app")]
    public async Task WhenOperatorCreatesInstanceAsync(string name)
    {
        _createdName = name;
        _created = await ctx.Kernel!.CreateAsync(
            BoolApp, name, ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"));
        _createdIdDir = Path.GetDirectoryName(_created.Spec.SchemaPath)!;
    }

    private int _orphanId;

    [Given("an orphaned instance directory {string} exists")]
    public void GivenOrphanedInstanceDir(string id)
    {
        _orphanId = int.Parse(id);
        Directory.CreateDirectory(Path.Combine(ctx.KernelDir!, "instances", id));
    }

    [Then("the created instance's id is past the orphaned directory")]
    public async Task ThenCreatedIdPastOrphanAsync() =>
        await Assert.That(_created!.Spec.Id).IsGreaterThan(_orphanId);

    // ── delete ──────────────────────────────────────────────────────────────────

    [When("the operator deletes the created instance")]
    [Given("the operator deletes the created instance")]
    public async Task WhenOperatorDeletesInstanceAsync() =>
        await ctx.Kernel!.DeleteAsync(_created!, Path.Combine(ctx.KernelDir!, "kernel.json"));

    [When("the operator deletes the second instance by its id")]
    public async Task WhenOperatorDeletesSecondAsync()
    {
        _createdIdDir = Path.GetDirectoryName(Beta.Spec.SchemaPath)!;
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.Id == Beta.Spec.Id);
        await ctx.Kernel.DeleteAsync(target, Path.Combine(ctx.KernelDir!, "kernel.json"));
    }

    [Then("the kernel hosts only the first instance")]
    public async Task ThenHostsOnlyFirstAsync() =>
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(1);

    [Then("the second instance's store directory is gone")]
    public async Task ThenSecondStoreDirGoneAsync() =>
        await Assert.That(Directory.Exists(_createdIdDir)).IsFalse();

    [Then("the deleted instance no longer serves its root")]
    public async Task ThenDeletedNoLongerServesAsync() =>
        await Assert.That(await ServesAsync(MountPath(_createdName))).IsFalse();

    [Then("the kernel hosts only the original instance")]
    public async Task ThenHostsOnlyOriginalAsync() =>
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(1);

    [Then("the created instance's store directory is gone")]
    public async Task ThenStoreDirGoneAsync() =>
        await Assert.That(Directory.Exists(_createdIdDir)).IsFalse();

    [Then("the console instance's page no longer lists the created instance")]
    public async Task ThenConsoleDoesNotListCreatedAsync()
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(Url(MountPath("console")));
        await Assert.That(html).DoesNotContain(MountPath(_createdName));
    }

    // ── clone ─────────────────────────────────────────────────────────────────

    [When("the operator clones the created instance")]
    [Given("the operator clones the created instance")]
    public async Task WhenOperatorClonesInstanceAsync()
    {
        _cloned = await ctx.Kernel!.CloneAsync(
            _created!, ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"));
        _clonedName = _cloned.Spec.App;
    }

    [Then("the clone serves its root at its own path")]
    public async Task ThenCloneServesRootAsync()
    {
        // After a restart _cloned is disposed; re-find the live instance by its persisted name.
        var instance = ctx.Kernel!.Instances.Single(i => i.Spec.App == _clonedName);
        using var http = new HttpClient();
        var resp = await http.GetAsync(Url(MountPath(instance.Spec.App)));
        await Assert.That((int)resp.StatusCode).IsEqualTo(200);
        await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("input type=\"checkbox\"");
    }

    [Then("the clone's data matches the source")]
    public async Task ThenCloneDataMatchesSourceAsync()
    {
        var instance = ctx.Kernel!.Instances.Single(i => i.Spec.App == _clonedName);
        await Assert.That(instance.Store.ReadNode(NodePath.Root.Field("ready"))).IsEqualTo(new BoolValue(true));
        using var http = new HttpClient();
        await Assert.That(await http.GetStringAsync(Url(MountPath(instance.Spec.App)))).Contains(" checked");
    }

    [Then("the kernel now hosts three instances")]
    public async Task ThenHostsThreeAsync() =>
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(3);

    [Given("the second instance's data changes")]
    public void WhenSecondDataChanges() =>
        Beta.Store.WriteObject(
            NodePath.Root,
            new ObjectValue(new Dictionary<string, NodeValue> { ["ready"] = new BoolValue(true) }));

    [When("the operator clones the second instance by its id")]
    public async Task WhenCloneSecondByIdAsync()
    {
        var bootId = Beta.Spec.Id;
        var source = ctx.Kernel!.Instances.Single(i => i.Spec.Id == bootId);
        _cloned = await ctx.Kernel.CloneAsync(source, ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"));
        _clonedName = _cloned.Spec.App;
    }

    [Then("the clone's data matches the second instance")]
    public async Task ThenCloneDataMatchesSecondAsync()
    {
        var instance = ctx.Kernel!.Instances.Single(i => i.Spec.App == _clonedName);
        await Assert.That(instance.Store.ReadNode(NodePath.Root.Field("ready"))).IsEqualTo(new BoolValue(true));
        using var http = new HttpClient();
        await Assert.That(await http.GetStringAsync(Url(MountPath(instance.Spec.App)))).Contains(" checked");
    }

    // ── rename (now a re-mount) ───────────────────────────────────────────────────

    [When("the operator renames the original instance to {string}")]
    [Given("the operator renames the original instance to {string}")]
    public async Task WhenOperatorRenamesAsync(string name)
    {
        _oldName = Alpha.Spec.App;
        await ctx.Kernel!.RenameAsync(Alpha.Spec.Id, name, Path.Combine(ctx.KernelDir!, "kernel.json"));
    }

    [When("the operator renames the first instance onto the second instance's name")]
    public async Task WhenOperatorRenamesOntoUsedAsync()
    {
        _oldName = Alpha.Spec.App;
        try
        {
            await ctx.Kernel!.RenameAsync(Alpha.Spec.Id, Beta.Spec.App, Path.Combine(ctx.KernelDir!, "kernel.json"));
        }
        catch (Exception ex) { _renameError = ex; }
    }

    [Then("the original instance no longer serves its root at its old path")]
    public async Task ThenNoLongerServesOldNameAsync() =>
        await Assert.That(await ServesAsync(MountPath(_oldName))).IsFalse();

    [Then("the rename is rejected with a clear kernel-config error")]
    public async Task ThenRenameRejectedAsync()
    {
        await Assert.That(_renameError).IsNotNull();
        await Assert.That(_renameError is KernelConfigException).IsTrue();
    }

    [Then("both instances still serve their roots at their own paths")]
    public async Task ThenBothStillServeAsync()
    {
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(2);
        using var http = new HttpClient();
        foreach (var instance in ctx.Kernel.Instances)
        {
            var resp = await http.GetAsync(Url(MountPath(instance.Spec.App)));
            await Assert.That((int)resp.StatusCode).IsEqualTo(200);
            await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("input type=\"checkbox\"");
        }
    }

    // ── serving at /apps/<name> ───────────────────────────────────────────────────

    [Then("the created instance serves its root at {string}")]
    public async Task ThenCreatedServesAtAsync(string path)
    {
        using var http = new HttpClient();
        var resp = await http.GetAsync(Url(path));
        await Assert.That((int)resp.StatusCode).IsEqualTo(200);
        await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("input type=\"checkbox\"");
    }

    [Then("the original instance serves its root at {string}")]
    public async Task ThenOriginalServesAtAsync(string path)
    {
        using var http = new HttpClient();
        var resp = await http.GetAsync(Url(path));
        await Assert.That((int)resp.StatusCode).IsEqualTo(200);
        await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("input type=\"checkbox\"");
    }

    [Then("the kernel now hosts both instances")]
    public async Task ThenHostsBothAsync() =>
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(2);

    [Then("the console instance's page lists the created instance")]
    public async Task ThenConsoleListsCreatedAsync()
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(Url(MountPath("console")));
        await Assert.That(html).Contains(MountPath(_createdName));
    }

    [When("the kernel restarts from its persisted registry")]
    public async Task WhenKernelRestartsAsync()
    {
        await ctx.Kernel!.DisposeAsync();
        var registry = RegistryReader.Read(Path.Combine(ctx.KernelDir!, "kernel.json"));
        ctx.Kernel = new KernelHost(ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"), _appPort, _assetPort);
        await ctx.Kernel.StartAsync(KernelHost.SpecsFor(registry, ctx.KernelDir!));
    }

    [When("the created instance's data changes")]
    [Given("the created instance's data changes")]
    public void WhenCreatedDataChanges() =>
        _created!.Store.WriteObject(
            NodePath.Root,
            new ObjectValue(new Dictionary<string, NodeValue> { ["ready"] = new BoolValue(true) }));

    [Then("the original instance is unchanged")]
    public async Task ThenOriginalUnchangedAsync() =>
        await Assert.That(Alpha.Store.ReadNode(NodePath.Root.Field("ready"))).IsEqualTo(new BoolValue(false));

    // ── Then: /apps/<name> serving ────────────────────────────────────────────────

    [Then("each instance serves its root at its mount path on the app port")]
    public async Task ThenEachServesAtPathAsync()
    {
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(2);
        using var http = new HttpClient();
        foreach (var instance in ctx.Kernel.Instances)
        {
            var resp = await http.GetAsync(Url(MountPath(instance.Spec.App)));
            await Assert.That((int)resp.StatusCode).IsEqualTo(200);
            await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("input type=\"checkbox\"");
        }
    }

    // ── mount-aware links + base ──────────────────────────────────────────────────

    [When("I request the console instance at its path")]
    public Task WhenRequestConsoleAtPathAsync() => RequestNamedAsync("console", prefix: null);

    [When("I request the {string} instance at its path")]
    public Task WhenRequestNamedAtPathAsync(string name) => RequestNamedAsync(name, prefix: null);

    [When("I request the {string} instance at its path with X-Forwarded-Prefix {string}")]
    public Task WhenRequestNamedWithPrefixAsync(string name, string prefix) => RequestNamedAsync(name, prefix);

    private async Task RequestNamedAsync(string name, string? prefix)
    {
        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, Url(MountPath(name)));
        if (prefix != null) req.Headers.Add("X-Forwarded-Prefix", prefix);
        var resp = await http.SendAsync(req);
        _consoleHtml = await resp.Content.ReadAsStringAsync();
    }

    [Then("the page's links carry the {string} prefix")]
    public async Task ThenLinksCarryPrefixAsync(string prefix)
    {
        // A generic-UI page's breadcrumb + nested set links are root-relative in app Code and
        // mount-prefixed at the SSR edge, so every emitted href starts with the mount prefix (e.g. the
        // breadcrumb "Db" → href="/apps/site", the nested notes link → href="/apps/site/notes").
        await Assert.That(_consoleHtml).Contains($"href=\"{prefix}");
        // And no UNPREFIXED root-relative app href slipped through (a bare href="/..." that the edge
        // failed to prefix). The bundle bootstrap uses base-relative JS, not an href, so this is scoped
        // to anchor/src hrefs the renderer emits.
        await Assert.That(_consoleHtml).DoesNotContain("href=\"/notes");
    }

    [Then("the page's injected base is {string}")]
    public async Task ThenInjectedBaseAsync(string expected) =>
        await Assert.That(_consoleHtml).Contains($"window.initBase=\"{expected}\"");

    [Then("the page's links are root-relative")]
    public async Task ThenLinksRootRelativeAsync()
    {
        // X-Forwarded-Prefix "" → no /apps/<name> prefix anywhere on the page (links + asset URL):
        // the instance is served at the domain root, the primitive nginx uses for a per-domain app.
        await Assert.That(_consoleHtml).DoesNotContain("/apps/");
    }

    // ── data sovereignty ──────────────────────────────────────────────────────────

    [When("one instance's data changes")]
    public void WhenOneChanges() =>
        Alpha.Store.WriteObject(
            NodePath.Root,
            new ObjectValue(new Dictionary<string, NodeValue> { ["ready"] = new BoolValue(true) }));

    [Then("that instance reflects the change")]
    public async Task ThenChangedReflectsAsync()
    {
        await Assert.That(Alpha.Store.ReadNode(NodePath.Root.Field("ready"))).IsEqualTo(new BoolValue(true));
        using var http = new HttpClient();
        await Assert.That(await http.GetStringAsync(Url(MountPath(Alpha.Spec.App)))).Contains(" checked");
    }

    [Then("the other instance is unchanged")]
    public async Task ThenOtherUnchangedAsync()
    {
        await Assert.That(Beta.Store.ReadNode(NodePath.Root.Field("ready"))).IsEqualTo(new BoolValue(false));
        using var http = new HttpClient();
        await Assert.That(await http.GetStringAsync(Url(MountPath(Beta.Spec.App)))).DoesNotContain(" checked");
    }

    // ── list: the registry as a read-only Code global ─────────────────────────────

    [Given("a registry whose first instance is a console app that lists the instances")]
    public void GivenConsoleRegistry()
    {
        var dir = NewDir();
        WriteApp(dir, 1, ConsoleApp);
        WriteApp(dir, 2, BoolApp);
        WriteApp(dir, 3, BoolApp);
        WriteRegistry(dir, ("console", 1), ("alpha", 2), ("beta", 3));
        _expectedNames = ["console", "alpha", "beta"];
        _expectedPaths = ["/apps/console", "/apps/alpha", "/apps/beta"];
    }

    [Then("the page lists every hosted instance's name and path")]
    public async Task ThenPageListsInstancesAsync()
    {
        foreach (var name in _expectedNames)
            await Assert.That(_consoleHtml).Contains(name);
        foreach (var path in _expectedPaths)
            await Assert.That(_consoleHtml).Contains(path);
    }

    // ── the shared asset port ─────────────────────────────────────────────────────

    [When("I request {string} under the original instance's mount on the asset port")]
    public async Task WhenRequestAssetUnderMountAsync(string assetPath)
    {
        using var http = new HttpClient();
        var url = $"http://localhost:{_assetPort}{MountPath(Alpha.Spec.App)}{assetPath}";
        _consoleHtml = await http.GetStringAsync(url);
    }

    [Then("the asset response is the client bundle")]
    public async Task ThenAssetIsBundleAsync() =>
        // The bundle defines the client runtime (init() + the reconciler); a distinctive token proves it.
        await Assert.That(_consoleHtml).Contains("function init(");

    // ── a path-mounted instance's WebSocket (browser) ─────────────────────────────

    [When("I open the original instance in a browser at its path")]
    public async Task WhenOpenOriginalInBrowserAsync()
    {
        ctx.Page = await SharedBrowser.NewPageAsync($"http://localhost:{_appPort}");
        await ctx.Page.GotoReadyAsync(MountPath(Alpha.Spec.App));
    }

    [When("I toggle its checkbox")]
    public async Task WhenToggleCheckboxAsync()
    {
        await ctx.Page!.WaitReadyAsync(); // the save commits over the WS — wait for the settled socket
        // The generic ObjectForm stages scalar edits in a draft (autosave off by default), so toggling
        // the checkbox stages it; clicking Save commits it over the WS (objectPropChange) — the full
        // round-trip over the path-mounted asset endpoint, which is what this scenario exercises.
        await ctx.Page!.Locator("input[type=checkbox]").First.CheckAsync();
        await ctx.Page!.Locator(".object-form button.save").First.ClickAsync();
    }

    // Open a named instance at its KERNEL MOUNT (/apps/<name>) in a browser — the production addressing,
    // so a subsequent SPA click exercises the mount-aware branch (ui.ts stripBase/in-mount guard). The
    // page BaseURL is the kernel's shared app port; GotoReadyAsync waits for hydration so the delegated
    // click listener is wired before the test clicks.
    [When("I open the {string} instance in a browser at its path")]
    public async Task WhenOpenNamedInBrowserAsync(string name)
    {
        ctx.Page = await SharedBrowser.NewPageAsync($"http://localhost:{_appPort}");
        await ctx.Page.GotoReadyAsync(MountPath(name));
    }

    // Click the generic-UI set row's stretched anchor (a.row-link). Under a mount its href is the
    // member's MOUNTED URL (/apps/site/notes/2), so the click drives the mount-aware client-side nav.
    [When("I click the set row link")]
    public async Task WhenClickSetRowLinkAsync() =>
        await ctx.Page!.Locator(".set-row a.row-link").First.ClickAsync();

    // The browser's URL pathname becomes the (MOUNTED) target — polled, since a client-side nav updates
    // location via pushState without a Load event. Distinct from the generic-UI "the URL path becomes"
    // step, which asserts the app's root-relative path; here we assert the full mounted browser path.
    [Then("the browser URL path becomes {string}")]
    public async Task ThenBrowserUrlPathBecomesAsync(string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => new URL(location.href).pathname === {JsString(expected)}");

    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    [Then("the original instance's store eventually has the bool set")]
    public async Task ThenStoreHasBoolSetAsync() =>
        await Polling.EventuallyAsync(
            () => Alpha.Store.ReadNode(NodePath.Root.Field("ready")) is BoolValue { Value: true },
            "the toggled bool to persist over the path-mounted WS");

    [Then("the page is fully ready")]
    public async Task ThenPageReadyAsync() =>
        await ctx.Page!.WaitReadyAsync();

    // ── registry validation ───────────────────────────────────────────────────────

    [Given("a registry of two instances that resolve to the same data file")]
    public void GivenAliasedRegistry()
    {
        var dir = NewDir();
        // Two entries with the SAME id → the same id-dir → the same data file.
        WriteApp(dir, 1, BoolApp);
        WriteRegistry(dir, ("alpha", 1), ("beta", 1));
    }

    [Given("a registry of two instances that share a mount name")]
    public void GivenSharedNameRegistry()
    {
        var dir = NewDir();
        // Distinct ids (distinct stores) but the SAME name → a /apps/<name> mount collision.
        WriteApp(dir, 1, BoolApp);
        WriteApp(dir, 2, BoolApp);
        WriteRegistry(dir, ("dup", 1), ("dup", 2));
    }

    [When("the kernel registry is resolved")]
    public void WhenRegistryResolved()
    {
        try
        {
            var registry = RegistryReader.Read(Path.Combine(ctx.KernelDir!, "kernel.json"));
            KernelHost.SpecsFor(registry, ctx.KernelDir!);
        }
        catch (Exception ex) { _configError = ex; }
    }

    [Then("it is rejected with a clear kernel-config error")]
    public async Task ThenRejectedAsync()
    {
        await Assert.That(_configError).IsNotNull();
        await Assert.That(_configError is KernelConfigException).IsTrue();
        await Assert.That(_configError!.Message).Contains("same data file");
    }

    [Then("it is rejected with a clear kernel-config error mentioning the name")]
    public async Task ThenRejectedNameAsync()
    {
        await Assert.That(_configError).IsNotNull();
        await Assert.That(_configError is KernelConfigException).IsTrue();
        await Assert.That(_configError!.Message).Contains("mount name");
    }

    // ── design-host first-boot seed (the designer's `db.designs`) ────────────────

    [Given("a kernel booted from the committed designer, todo and crm apps plus a no-design app")]
    public async Task GivenKernelFromCommittedAppsAsync()
    {
        var dir = NewDir();
        WriteApp(dir, 1, File.ReadAllText(InstanceContext.AppFixture(1))); // designer (db.designs)
        WriteApp(dir, 2, File.ReadAllText(InstanceContext.AppFixture(2))); // todo
        WriteApp(dir, 3, File.ReadAllText(InstanceContext.AppFixture(3))); // crm
        WriteApp(dir, 4, BoolApp);                                          // a no-design instance

        // designIds pin each design's id to its instance's reference (designer 60, todo 13, crm 27).
        var entries = new[]
        {
            ("designer", 1, (int?)60),
            ("todo", 2, (int?)13),
            ("crm", 3, (int?)27),
            ("nodesign", 4, (int?)null),
        };
        File.WriteAllText(Path.Combine(dir, "kernel.json"), RegistryJsonText(entries));

        var registry = RegistryReader.Read(Path.Combine(dir, "kernel.json"));
        ctx.Kernel = new KernelHost(dir, Path.Combine(dir, "kernel.json"), _appPort, _assetPort);
        await ctx.Kernel.StartAsync(KernelHost.SpecsFor(registry, dir));
    }

    private IReadOnlyDictionary<int, ObjectValue> SeededDesigns() =>
        ctx.Kernel!.Instances.Single(i => i.Spec.Id == 1).Store.ReadExtent("Design");

    [Then("the design-host holds a design with id {int} labelled {string}")]
    public async Task ThenDesignHostHoldsDesignAsync(int id, string label)
    {
        var designs = SeededDesigns();
        await Assert.That(designs.ContainsKey(id)).IsTrue();
        await Assert.That(LabelOf(designs[id])).IsEqualTo(label);
    }

    [Then("the seeded design {int} has a type named {string}")]
    [Then("the design-host's design {int} has a type named {string}")]
    public async Task ThenSeededDesignHasTypeAsync(int id, string typeName)
    {
        var design = SeededDesigns()[id];
        var types = (SetValue)design.Fields["types"];
        var names = types.Members.Values
            .OfType<ObjectValue>()
            .Select(t => t.Fields.TryGetValue("name", out var v) && v is TextValue n ? n.Text : "")
            .ToList();
        await Assert.That(names).Contains(typeName);
    }

    [Given("the todo app's document gains a new type {string}")]
    public void GivenTodoAppGainsType(string typeName)
    {
        var schemaPath = AppPaths.SchemaPathForId(ctx.KernelDir!, 2); // todo is instance id 2
        var doc = File.ReadAllText(schemaPath);
        var lines = doc.Replace("\r\n", "\n").Split('\n').ToList();
        var typesLine = lines.FindIndex(l => l == "types");
        lines.Insert(typesLine + 1, $"    {typeName}\n        label text");
        File.WriteAllText(schemaPath, string.Join("\n", lines));
    }

    [Given("the design-host's design {int} is removed from its store")]
    public void GivenDesignRemovedFromStore(int id)
    {
        var store = ctx.Kernel!.Instances.Single(i => i.Spec.Id == 1).Store;
        store.RemoveFromSet(NodePath.Root.Field("designs"), id);
    }

    [Then("the design-host still holds a design with id {int} labelled {string}")]
    public async Task ThenDesignHostStillHoldsDesignAsync(int id, string label)
    {
        var designs = SeededDesigns();
        await Assert.That(designs.ContainsKey(id)).IsTrue();
        await Assert.That(LabelOf(designs[id])).IsEqualTo(label);
    }

    [Then("the no-design app contributes no design")]
    public async Task ThenNoDesignAppContributesNothingAsync()
    {
        var designs = SeededDesigns();
        await Assert.That(designs.Count).IsEqualTo(3);
        await Assert.That(designs.Values.Select(LabelOf)).DoesNotContain("nodesign");
    }

    [Given("the operator adds a design labelled {string} to the design-host")]
    public void GivenOperatorAddsDesign(string label)
    {
        var store = ctx.Kernel!.Instances.Single(i => i.Spec.Id == 1).Store;
        var id = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["label"] = new TextValue(label),
            ["initialData"] = new TextValue(""),
            ["common"] = new TextValue(""),
            ["ui"] = new TextValue(""),
        }));
        store.AddToSet(NodePath.Root.Field("designs"), id);
    }

    [Then("the design-host holds a design labelled {string}")]
    public async Task ThenDesignHostHoldsLabelAsync(string label) =>
        await Assert.That(SeededDesigns().Values.Select(LabelOf)).Contains(label);

    private static string LabelOf(ObjectValue design) =>
        design.Fields.TryGetValue("label", out var v) && v is TextValue t ? t.Text : "";

    // ── helpers ───────────────────────────────────────────────────────────────

    // A fresh temp dir for a scenario's fixtures + registry, and the two shared kernel ports.
    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deenv-kernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ctx.KernelDir = dir;
        _appPort = FreePort();
        _assetPort = FreePort();
        return dir;
    }

    private static void WriteApp(string dir, int id, string appDoc)
    {
        var idDir = AppPaths.IdDirFor(dir, id);
        Directory.CreateDirectory(idDir);
        File.WriteAllText(Path.Combine(idDir, "app.app"), appDoc);
    }

    // Write kernel.json with the two kernel-level ports + the given (name, id) instances (no
    // per-instance ports — addressing is by path).
    private void WriteRegistry(string dir, params (string Name, int Id)[] instances) =>
        File.WriteAllText(Path.Combine(dir, "kernel.json"),
            RegistryJsonText(instances.Select(i => (i.Name, i.Id, (int?)null)).ToArray()));

    private string RegistryJsonText((string Name, int Id, int? DesignId)[] instances)
    {
        var rows = instances.Select(i =>
        {
            var did = i.DesignId.HasValue ? $", \"designId\": {i.DesignId.Value}" : "";
            return $"    {{ \"id\": {i.Id}, \"app\": \"{i.Name}\"{did} }}";
        });
        return "{\n" +
               $"  \"appPort\": {_appPort},\n" +
               $"  \"assetPort\": {_assetPort},\n" +
               "  \"instances\": [\n" + string.Join(",\n", rows) + "\n  ]\n}";
    }

    private string MountPath(string name) => "/apps/" + name;
    private string Url(string path) => $"http://localhost:{_appPort}{path}";

    // True if the app port serves a 2xx at this path, false if it 404s (the instance is not routed) or
    // the connection is refused. A refused/404 returns promptly; the timeout bounds a LIVE-but-slow page.
    private async Task<bool> ServesAsync(string path)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            var resp = await http.GetAsync(Url(path));
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    private static int FreePort() => PortAllocator.Next();
}
