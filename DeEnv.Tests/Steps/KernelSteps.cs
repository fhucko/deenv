using DeEnv.Kernel;
using DeEnv.Storage;
using DeEnv.Instance;
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

    // Assets (docs/plans/assets-design.md), a single `image` scalar on Db — a real /apps/<name>-mounted
    // instance to prove the blob pool's HTTP edges + sys.assetUrl/BlobBase resolve correctly under a
    // kernel PATH mount (Assets.feature's own scenarios only exercise the ROOT-mounted TestInstanceServer
    // shape) and that the boot-time StoredDataValidator arm survives a restart.
    private const string ImageApp = """
    types
        Db
            photo image
    """;

    // ── security regression: the host-action operator gate ──────────────────────
    //
    // A minimal DESIGN-HOST schema — a `Db` whose `designs` prop is a `set of Design` — the design-host
    // SHAPE (mirrors HostActionSteps.MetaSchema). It calls NO host action, so under the AST wiring it is
    // NOT wired a real KernelHostActions — the point being that SHAPE alone no longer confers authority.
    // Here it is only the delete TARGET; the "plain" app's WS is the sender under test.
    private const string DesignHostApp = """
    types
        Db
            designs set of Design
        Design
            label text
            initialData text
            common text
            ui text
            types set of MetaType
        MetaType
            name text
            baseType text
            order int
            props set of MetaProp
        MetaProp
            name text
            type text
            cardinality text
            keyType text
            order int
    """;

    // An ORDINARY app — the shape of a public app like devlog. It calls no host action, so its WS is
    // unwired (NoHostActions). Its WebSocket is the attack surface: before the fix, a `hostAction` frame
    // sent here ran with full kernel authority against ANY instance.
    private const string PlainApp = """
    types
        Db
            items set of Item
        Item
            label text
    """;

    // The SHAPE-≠-AUTHORITY fixture: designer-SHAPED (Db { designs set of Design }) AND its Code CALLS a
    // host action (a render that fires sys.delete) — so it AST-wires to a REAL KernelHostActions — but it
    // declares NO `sys` access rule. Under the old shape gate it had full kernel authority; now the
    // access-rule gate (WsHandler.HandleHostAction) must reject on its OWN WS, for everyone. The `ui`
    // render is minimal but real: a button whose onClick calls sys.delete, enough for HostActionScan to
    // wire the seam. (Anonymous session — a fresh page load — so no principal; a `sys` rule is absent
    // anyway, so it denies regardless.)
    private const string DesignShapedNoRuleApp = """
    types
        Db
            designs set of Design
        Design
            label text
            initialData text
            common text
            ui text
            types set of MetaType
        MetaType
            name text
            baseType text
            order int
            props set of MetaProp
        MetaProp
            name text
            type text
            cardinality text
            keyType text
            order int

    ui
        fn render()
            return <button class="danger" onClick={() => sys.delete(1)}>
                "Delete"
    """;

    // The M13 Commit-button variant of the same shape-≠-authority fixture: designer-SHAPED AND its Code
    // calls sys.commitDesign (not sys.delete) — proving HostActionScan's AST wiring recognizes the newly
    // added builtin exactly like it already recognizes delete/publish/etc. No Commit/Branch types are
    // needed: the `sys` floor rejects BEFORE KernelHostActions.Run ever dispatches to CommitDesign (see
    // WsHandler.HandleHostAction), so this fixture only needs the designer SHAPE (Db.designs) + a call site.
    private const string DesignShapedCommitDesignNoRuleApp = """
    types
        Db
            designs set of Design
        Design
            label text
            initialData text
            common text
            ui text
            types set of MetaType
        MetaType
            name text
            baseType text
            order int
            props set of MetaProp
        MetaProp
            name text
            type text
            cardinality text
            keyType text
            order int

    ui
        fn render()
            return <button class="danger" onClick={() => sys.commitDesign(1, "x", "")}>
                "Commit"
    """;

    // What the WS security-gate scenario recorded: the raw `hostAction` reply text (over a REAL
    // WebSocket to the plain instance's /ws) and the design-host's id it targeted.
    private string _hostActionWsReply = "";
    private int _designHostId;

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

    // The kernel-mounted image round-trip scenario (Assets, docs/plans/assets-design.md).
    private byte[] _uploadedImageBytes = [];
    private string _uploadedImageName = "";

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

    [Given("a registry of one image-capable instance named {string}")]
    public void GivenImageCapableInstance(string name)
    {
        var dir = NewDir();
        WriteApp(dir, 1, ImageApp);
        WriteRegistry(dir, (name, 1));
    }

    // The security-regression fixture: instance 1 is the design host (real host-action authority),
    // instance 2 is an ordinary app (must get NoHostActions). Distinct ids/names, like every other
    // registry fixture here.
    [Given("a registry of a design-host instance and a plain instance")]
    public void GivenDesignHostAndPlainRegistry()
    {
        var dir = NewDir();
        WriteApp(dir, 1, DesignHostApp);
        WriteApp(dir, 2, PlainApp);
        WriteRegistry(dir, ("designer", 1), ("plainapp", 2));
        _designHostId = 1;
    }

    // The shape-≠-authority fixture: ONE instance that is designer-shaped, uses host actions (its Code
    // calls sys.delete), and declares no `sys` rule — so it AST-wires a real seam yet the access gate
    // must still reject on its own WS.
    [Given("a registry whose only instance is designer-shaped, uses host actions, and has no sys rule")]
    public void GivenDesignShapedNoRuleRegistry()
    {
        var dir = NewDir();
        WriteApp(dir, 1, DesignShapedNoRuleApp);
        WriteRegistry(dir, ("shaped", 1));
        _designHostId = 1;
    }

    // The Commit-button slice's AST-wiring guard: same shape-≠-authority fixture, but the Code calls
    // sys.commitDesign instead of sys.delete — proving HostActionScan.UsesHostActions recognizes the
    // newly wired builtin (a real seam gets built) exactly as it already does for the other host actions.
    [Given("a registry whose only instance is designer-shaped, calls sys.commitDesign, and has no sys rule")]
    public void GivenDesignShapedCommitDesignNoRuleRegistry()
    {
        var dir = NewDir();
        WriteApp(dir, 1, DesignShapedCommitDesignNoRuleApp);
        WriteRegistry(dir, ("shaped", 1));
        _designHostId = 1;
    }

    // ── When / Given (start) ────────────────────────────────────────────────────

    [When("the kernel starts")]
    [Given("the kernel has started")]
    public async Task WhenKernelStartsAsync()
    {
        var registry = RegistryReader.Read(Path.Combine(ctx.KernelDir!, "kernel.json"));
        ctx.Kernel = new KernelHost(ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"), _appPort, _assetPort, bindLoopback: true);
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
        ctx.Kernel = new KernelHost(ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"), _appPort, _assetPort, bindLoopback: true);
        await ctx.Kernel.StartAsync(KernelHost.SpecsFor(registry, ctx.KernelDir!));
    }

    // ── Assets (docs/plans/assets-design.md): the kernel-mounted image round-trip ────────────────

    [When("I upload {int} random bytes as {string} to the {string} instance")]
    public async Task WhenUploadRandomBytesToInstanceAsync(int count, string contentType, string name)
    {
        _uploadedImageBytes = new byte[count];
        new Random(7).NextBytes(_uploadedImageBytes);
        using var http = new HttpClient();
        using var content = new ByteArrayContent(_uploadedImageBytes);
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        var response = await http.PostAsync($"http://localhost:{_assetPort}{MountPath(name)}/assets", content);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        _uploadedImageName = doc.RootElement.GetProperty("name").GetString() ?? "";
    }

    [When("I set the {string} instance's Db {string} field to the uploaded name")]
    public void WhenSetInstanceDbFieldToUploadedName(string name, string field) =>
        ctx.Kernel!.Instances.Single(i => i.Spec.App == name).Store
            .WriteField(1, field, new TextValue(_uploadedImageName));

    [Then("the kernel still hosts the {string} instance")]
    public async Task ThenKernelStillHostsNamedInstanceAsync(string name) =>
        await Assert.That(ctx.Kernel!.Instances.Any(i => i.Spec.App == name)).IsTrue();

    [Then("the {string} instance serves the uploaded image at its mount")]
    public async Task ThenInstanceServesUploadedImageAsync(string name)
    {
        using var http = new HttpClient();
        var response = await http.GetAsync($"http://localhost:{_assetPort}{MountPath(name)}/assets/{_uploadedImageName}");
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        await Assert.That(Convert.ToBase64String(bytes)).IsEqualTo(Convert.ToBase64String(_uploadedImageBytes));
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
        // the checkbox stages it; clicking Save commits it over the WS (one atomic `commit` message) —
        // the full round-trip over the path-mounted asset endpoint, which is what this scenario exercises.
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

    // ── security regression: the host-action operator gate ──────────────────────
    //
    // Opens a REAL WebSocket (System.Net.WebSockets.ClientWebSocket, not a hand-picked WsHandler) to the
    // PLAIN instance's /ws under the kernel's shared asset port, and sends a `hostAction` frame targeting
    // the design host's id. This must go through the real KernelHost wiring — HostedInstance.Start wires
    // each instance's WsHandler with KernelHost.HostActionsFor(spec), which is exactly the fix under
    // test — so a hand-constructed WsHandler (as HostActionSteps uses for the host-action ARGUMENT-
    // SHAPE scenarios) would bypass the gate and prove nothing.
    [When("I send a hostAction {string} for the design host's id over the plain instance's WebSocket")]
    public async Task WhenSendHostActionOverPlainWsAsync(string action)
    {
        var plain = ctx.Kernel!.Instances.Single(i => i.Spec.Id != _designHostId);
        var uri = new Uri($"ws://localhost:{_assetPort}{MountPath(plain.Spec.App)}/ws");

        using var socket = new System.Net.WebSockets.ClientWebSocket();
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(uri, connectCts.Token);

        var frame = $$"""{ "op": "hostAction", "action": "{{action}}", "args": [ { "type": "int", "value": {{_designHostId}} } ] }""";
        var sendBytes = System.Text.Encoding.UTF8.GetBytes(frame);
        using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.SendAsync(sendBytes, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, sendCts.Token);

        var buffer = new byte[8192];
        using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await socket.ReceiveAsync(buffer, receiveCts.Token);
        _hostActionWsReply = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
    }

    // The shape-≠-authority send: a `hostAction` frame over the SHAPED instance's OWN /ws targeting its
    // own id. It AST-wires a real KernelHostActions (its Code calls sys.delete), so the reject can only
    // come from the access-rule gate (no `sys` rule → deny) — the second line of defence through real
    // kernel wiring. An anonymous socket (a fresh connection, no login).
    [When("I send a hostAction {string} for that instance's own id over its WebSocket")]
    public async Task WhenSendHostActionOverOwnWsAsync(string action)
    {
        var only = ctx.Kernel!.Instances.Single();
        var uri = new Uri($"ws://localhost:{_assetPort}{MountPath(only.Spec.App)}/ws");

        using var socket = new System.Net.WebSockets.ClientWebSocket();
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(uri, connectCts.Token);

        var frame = $$"""{ "op": "hostAction", "action": "{{action}}", "args": [ { "type": "int", "value": {{only.Spec.Id}} } ] }""";
        var sendBytes = System.Text.Encoding.UTF8.GetBytes(frame);
        using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.SendAsync(sendBytes, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, sendCts.Token);

        var buffer = new byte[8192];
        using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await socket.ReceiveAsync(buffer, receiveCts.Token);
        _hostActionWsReply = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
    }

    [Then("the host action reply over the WebSocket is an error")]
    public async Task ThenHostActionWsReplyIsErrorAsync()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(_hostActionWsReply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsTrue();
        await Assert.That(doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()).IsFalse();
    }

    // The real teeth: the targeted (design-host) instance is STILL LIVE in the kernel's hosted set —
    // proving the delete was actually rejected, not merely that the reply happened to carry an error.
    [Then("the kernel still hosts the design-host instance")]
    public async Task ThenKernelStillHostsDesignHostAsync() =>
        await Assert.That(ctx.Kernel!.Instances.Any(i => i.Spec.Id == _designHostId)).IsTrue();

    // The shape-≠-authority teeth: the shaped instance is STILL hosted — its own host action was rejected.
    [Then("the kernel still hosts that instance")]
    public async Task ThenKernelStillHostsThatInstanceAsync() =>
        await Assert.That(ctx.Kernel!.Instances.Any(i => i.Spec.Id == _designHostId)).IsTrue();

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

    // ── per-instance boot isolation ───────────────────────────────────────────────

    [Given("the second instance's app document is unparseable")]
    public void GivenSecondAppUnparseable() =>
        File.WriteAllText(AppPaths.SchemaPathForId(ctx.KernelDir!, 2), ")))) this is not an app document ((((");

    [Given("a registry of the committed designer and a bool app named {string}")]
    public void GivenDesignerAndBoolRegistry(string name)
    {
        var dir = NewDir();
        WriteApp(dir, 1, File.ReadAllText(InstanceContext.AppFixture(1))); // the committed designer
        WriteApp(dir, 2, BoolApp);
        File.WriteAllText(Path.Combine(dir, "kernel.json"),
            RegistryJsonText([("designer", 1, (int?)60), (name, 2, (int?)null)]));
    }

    [Given("the designer's data file is corrupt")]
    public void GivenDesignerDataCorrupt() =>
        File.WriteAllText(AppPaths.DataPathForId(ctx.KernelDir!, 1), "{ this is not the designer's data");

    [Then("the instance named {string} still serves its root")]
    public async Task ThenNamedStillServesAsync(string name)
    {
        using var http = new HttpClient();
        var resp = await http.GetAsync(Url(MountPath(name)));
        await Assert.That((int)resp.StatusCode).IsEqualTo(200);
        await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("input type=\"checkbox\"");
    }

    [Then("the kernel reports instance {string} as failed to boot")]
    public async Task ThenReportsFailedAsync(string name) =>
        await Assert.That(ctx.Kernel!.FailedInstances.Select(f => f.Spec.App).ToList()).Contains(name);

    [Then("the kernel reports the design library as not reconciled")]
    public async Task ThenDesignSyncFailedAsync() =>
        await Assert.That(ctx.Kernel!.DesignSyncError).IsNotNull();

    private Exception? _createError;

    [When("the operator creates an instance named {string} expecting rejection")]
    public async Task WhenOperatorCreatesExpectingRejectionAsync(string name)
    {
        try
        {
            await ctx.Kernel!.CreateAsync(BoolApp, name, ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"));
        }
        catch (Exception ex) { _createError = ex; }
    }

    [Then("the create is rejected with a clear kernel-config error mentioning the name")]
    public async Task ThenCreateRejectedAsync()
    {
        await Assert.That(_createError).IsNotNull();
        await Assert.That(_createError is KernelConfigException).IsTrue();
        await Assert.That(_createError!.Message).Contains("mount name");
    }

    [Then("the mount {string} answers 503 naming the failure")]
    public async Task ThenMountAnswers503Async(string path)
    {
        using var http = new HttpClient();
        var resp = await http.GetAsync(Url(path));
        await Assert.That((int)resp.StatusCode).IsEqualTo(503);
        var body = await resp.Content.ReadAsStringAsync();
        await Assert.That(body).Contains(path.Split('/').Last()); // the instance name
        await Assert.That(body).Contains("failed");
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
        ctx.Kernel = new KernelHost(dir, Path.Combine(dir, "kernel.json"), _appPort, _assetPort, bindLoopback: true);
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
        var names = SeededDesignTypeNames(id);
        await Assert.That(names).Contains(typeName);
    }

    // M13 slice 3 — the authority-inversion regression guard (the sibling of ThenSeededDesignHasTypeAsync):
    // a hand-edited app file must NOT retroactively appear in an already-adopted design after a restart.
    [Then("the design-host's design {int} does not have a type named {string}")]
    public async Task ThenSeededDesignDoesNotHaveTypeAsync(int id, string typeName)
    {
        var names = SeededDesignTypeNames(id);
        await Assert.That(names).DoesNotContain(typeName);
    }

    private List<string> SeededDesignTypeNames(int id)
    {
        var store = ctx.Kernel!.Instances.Single(i => i.Spec.Id == 1).Store;
        var design = SeededDesigns()[id];
        return ListMemberObjects(store, design.Fields.GetValueOrDefault("types"))
            .Select(t => t.Fields.TryGetValue("name", out var v) && v is TextValue n ? n.Text : "")
            .ToList();
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

    [Then("the design-host holds exactly one design labelled {string}")]
    public async Task ThenDesignHostHoldsExactlyOneLabelAsync(string label) =>
        await Assert.That(SeededDesigns().Values.Select(LabelOf).Count(l => l == label)).IsEqualTo(1);

    private static string LabelOf(ObjectValue design) =>
        design.Fields.TryGetValue("label", out var v) && v is TextValue t ? t.Text : "";

    private static string ExtractInitClientId(string html)
    {
        const string marker = "window.initClientId=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Rendered page did not include initClientId.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        if (end < 0) throw new InvalidOperationException("Rendered page had a malformed initClientId.");
        return html[start..end];
    }

    // ── M13 slice 3: adoption-once + the log-preserving boundary (DesignCommit.feature) ──────────

    private int _logLinesBeforeCommit;

    // Rename ONE PROP (never a whole type — TodoItem/TodoList/User are all cross-referenced by name from
    // sibling MetaProp.type fields AND the todo app's own Code, so a TYPE rename here would leave the
    // design's REST invalid — "Prop 'items' on type 'TodoList' references unknown type 'TodoItem'." A
    // prop is referenced only by its OWNING type's Code, which the commit's structural validation never
    // inspects, so this is a clean, isolated, real edit) DIRECTLY on the design-host's LIVE store (the
    // same HostedInstance.Store the kernel's own WsHandler operates over — a real production write, not
    // a hand-built harness). Walks db.designs[13].types[typeName].props looking for the named prop.
    [Given("the todo design's {string} prop {string} is renamed to {string} through the designer store")]
    public void GivenTodoDesignPropRenamed(string typeName, string oldPropName, string newPropName)
    {
        var store = ctx.Kernel!.Instances.Single(i => i.Spec.Id == 1).Store;
        var todoDesignId = SeededDesigns().Single(d => LabelOf(d.Value) == "todo").Key;
        var typesPath = NodePath.Root.Field("designs").Key(todoDesignId.ToString()).Field("types");
        // Designer position-bearing collections are lists (Slice 4): ReadNode yields ReferenceValue slots.
        var typesList = (ListValue)store.ReadNode(typesPath)!;
        var typeId = typesList.Items.OfType<ReferenceValue>()
            .Select(r => r.TargetId!.Value)
            .Single(id => store.ReadById(id) is ("MetaType", var fields)
                && fields.Fields.GetValueOrDefault("name") is TextValue t && t.Text == typeName);
        var propsList = (ListValue)store.ReadNode(typesPath.Key(typeId.ToString()).Field("props"))!;
        var propId = propsList.Items.OfType<ReferenceValue>()
            .Select(r => r.TargetId!.Value)
            .Single(id => store.ReadById(id) is ("MetaProp", var fields)
                && fields.Fields.GetValueOrDefault("name") is TextValue t && t.Text == oldPropName);
        store.WriteField(propId, "name", new TextValue(newPropName));
    }

    // The designer's own on-disk changeset log line count (M13 slice 1's append-only log), captured
    // BEFORE a commitDesign call — the baseline "the designer's log was not truncated by the restart"
    // compares against. A boot-time InitialData/Reset (the OLD upsert-on-every-boot behavior) deletes
    // this file entirely (0 lines after); the new adopt-once/no-Reset invariant must leave it growing.
    [Given("the designer's own log line count is remembered before committing")]
    public void GivenLogLineCountRemembered()
    {
        var logPath = AppPaths.LogPathForId(ctx.KernelDir!, 1);
        _logLinesBeforeCommit = File.Exists(logPath) ? File.ReadAllLines(logPath).Length : 0;
    }

    // sys.commitDesign(design, message, migration) over a REAL WebSocket to the designer's OWN /ws — the kernel-
    // wired path (HostedInstance.Start → KernelHost.HostActionsFor), not a hand-built WsHandler, so this
    // proves the FULL boot→adopt→commit→restart path end-to-end. The committed designer gates `sys` to an
    // Admin principal, so this logs in through /session, then uses the authenticated SSR's clientId.
    [When("the designer commits the todo design with message {string} over its own WS")]
    public async Task WhenDesignerCommitsTodoDesignAsync(string message)
    {
        var designer = ctx.Kernel!.Instances.Single(i => i.Spec.Id == 1);
        var desc = InstanceDescriptionLoader.LoadFile(designer.Spec.SchemaPath);
        AdminSeed.Seed(designer.Store, desc, "admin", "hunter2", "Admin");

        var cookies = new System.Net.CookieContainer();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
        var login = new StringContent(
            """{"name":"admin","password":"hunter2"}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var loginResp = await http.PostAsync($"http://localhost:{_assetPort}{MountPath(designer.Spec.App)}/session", login);
        loginResp.EnsureSuccessStatusCode();

        var html = await http.GetStringAsync(Url($"{MountPath(designer.Spec.App)}/designs"));
        var clientId = ExtractInitClientId(html);
        var todoDesignId = SeededDesigns().Single(d => LabelOf(d.Value) == "todo").Key;
        var uri = new Uri($"ws://localhost:{_assetPort}{MountPath(designer.Spec.App)}/ws");

        using var socket = new System.Net.WebSockets.ClientWebSocket();
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(uri, connectCts.Token);

        var frame = $$"""
            { "op": "hostAction", "clientId": "{{clientId}}", "action": "commitDesign", "args": [
                { "type": "int", "value": {{todoDesignId}} }, { "type": "text", "value": "{{message}}" },
                { "type": "text", "value": "" }
            ] }
            """;
        var sendBytes = System.Text.Encoding.UTF8.GetBytes(frame);
        using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.SendAsync(sendBytes, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, sendCts.Token);

        var buffer = new byte[8192];
        using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await socket.ReceiveAsync(buffer, receiveCts.Token);
        _hostActionWsReply = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);

        using var doc = System.Text.Json.JsonDocument.Parse(_hostActionWsReply);
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"commitDesign over the WS failed: {_hostActionWsReply}");
    }

    [Then("the design-host's todo design's {string} has a prop named {string}")]
    public async Task ThenTodoDesignTypeHasPropAsync(string typeName, string propName)
    {
        var store = ctx.Kernel!.Instances.Single(i => i.Spec.Id == 1).Store;
        var todoDesign = SeededDesigns().Single(d => LabelOf(d.Value) == "todo").Value;
        var type = ListMemberObjects(store, todoDesign.Fields.GetValueOrDefault("types"))
            .Single(t => t.Fields.GetValueOrDefault("name") is TextValue n && n.Text == typeName);
        var propNames = ListMemberObjects(store, type.Fields.GetValueOrDefault("props"))
            .Select(p => p.Fields.TryGetValue("name", out var v) && v is TextValue n ? n.Text : "")
            .ToList();
        await Assert.That(propNames).Contains(propName);
    }

    [Then("the design-host still holds the {string} commit")]
    public async Task ThenDesignHostHoldsCommitAsync(string message)
    {
        var store = ctx.Kernel!.Instances.Single(i => i.Spec.Id == 1).Store;
        var commits = store.ReadExtent("Commit");
        await Assert.That(commits.Values.Any(c =>
            c.Fields.GetValueOrDefault("message") is TextValue t && t.Text == message)).IsTrue();
    }

    [Then("the designer's log was not truncated by the restart")]
    public async Task ThenDesignerLogNotTruncatedAsync()
    {
        var logPath = AppPaths.LogPathForId(ctx.KernelDir!, 1);
        await Assert.That(File.Exists(logPath)).IsTrue();
        var linesAfter = File.ReadAllLines(logPath).Length;
        // The commit + the restart together must have GROWN the log (the commitDesign write itself adds
        // entries), never shrunk/reset it to zero — a Reset() truncation is exactly 0 lines afterward.
        await Assert.That(linesAfter).IsGreaterThan(_logLinesBeforeCommit);
    }

    [Then("the design-host's db.instances lists every hosted instance by name")]
    public async Task ThenDbInstancesListsEveryHostedInstanceAsync()
    {
        var store = ctx.Kernel!.Instances.Single(i => i.Spec.Id == 1).Store;
        var instances = store.ReadExtent("Instance").Values
            .Select(i => i.Fields.GetValueOrDefault("name") is TextValue t ? t.Text : "")
            .ToList();
        var hostedNames = ctx.Kernel.Instances.Select(i => i.Spec.App).ToList();
        foreach (var name in hostedNames)
            await Assert.That(instances).Contains(name);
    }

    // ── review fix 2: adoption id-rewrite remaps the Instance.design reference ────

    // The kernel.json designId a new app is registered with — deliberately BELOW the designer store's
    // post-seed mint counter (the seeds go up to id 60+), so AdoptInto is forced to mint a DIFFERENT id
    // and the rewrite+remap path fires. Its instance id (5) and mount name are fixed for the assertions.
    private const int NewAppInstanceId = 5;
    private const int NewAppLowDesignId = 3; // < every seeded id → AdoptInto can never pin it
    private const string NewAppName = "widgets";

    // Register a genuinely-new app (its own app.deenv + a kernel.json entry) whose designId is below the
    // mint counter, so the NEXT boot (an EXISTING store — SyncDesignHost's log-preserving adopt path)
    // mints a fresh id for it and must rewrite the registry + remap the Instance.design reference.
    [Given("a new app instance is registered with a designId below the mint counter")]
    public void GivenNewAppRegisteredWithLowDesignId()
    {
        const string widgetApp = """
        types
            Db
                widgets set of Widget
            Widget
                label text
        """;
        WriteApp(ctx.KernelDir!, NewAppInstanceId, widgetApp);

        // Append the new entry to the EXISTING registry (keep the four already there), so the restart
        // reads all five. The new entry references its own design by the low id.
        var kernelJson = Path.Combine(ctx.KernelDir!, "kernel.json");
        var stored = RegistryReader.Read(kernelJson);
        RegistryWriter.Write(kernelJson, new Registry(
            [.. stored.Instances, new RegistryEntry(NewAppInstanceId, NewAppName, NewAppLowDesignId)],
            stored.AppPort, stored.AssetPort));
    }

    [Then("the design-host adopted the new app's design at a minted id, not its stale designId")]
    public async Task ThenNewAppAdoptedAtMintedIdAsync()
    {
        var designs = SeededDesigns();
        // The new app's design has a "Widget" type (its label is "" — AdoptInto doesn't set one), so find
        // it by that type name; its id must be a freshly-minted one (> the low kernel.json designId), and
        // NOTHING should exist at the stale low id.
        var adopted = designs.Single(d => DesignHasType(d.Value, "Widget"));
        await Assert.That(adopted.Key).IsGreaterThan(NewAppLowDesignId);
        await Assert.That(designs.ContainsKey(NewAppLowDesignId)).IsFalse();
        _adoptedNewDesignId = adopted.Key;
    }

    private int _adoptedNewDesignId;

    [Then("the new app instance's stored design reference resolves to the adopted design")]
    public async Task ThenNewInstanceDesignResolvesAsync()
    {
        var store = ctx.Kernel!.Instances.Single(i => i.Spec.Id == 1).Store;
        var instance = store.ReadExtent("Instance").Values
            .Single(i => i.Fields.GetValueOrDefault("name") is TextValue { Text: NewAppName });
        // The Instance.design reference must point at the MINTED design id (the remap), never the stale
        // kernel.json one — and that id must actually resolve to a real Design row.
        var designRef = instance.Fields.GetValueOrDefault("design") as ReferenceValue;
        await Assert.That(designRef?.TargetId).IsEqualTo(_adoptedNewDesignId);
        await Assert.That(SeededDesigns().ContainsKey(_adoptedNewDesignId)).IsTrue();
    }

    private bool DesignHasType(ObjectValue design, string typeName)
    {
        var store = ctx.Kernel!.Instances.Single(i => i.Spec.Id == 1).Store;
        return ListMemberObjects(store, design.Fields.GetValueOrDefault("types"))
            .Any(t => t.Fields.GetValueOrDefault("name") is TextValue n && n.Text == typeName);
    }

    // Designer trees are lists of ReferenceValue slots (BuildListValue); resolve for assertions.
    private static IEnumerable<ObjectValue> ListMemberObjects(IInstanceStore store, NodeValue? coll)
    {
        if (coll is ListValue lv)
        {
            foreach (var item in lv.Items)
                if (item is ReferenceValue { TargetId: int id } && store.ReadById(id) is (_, ObjectValue ov))
                    yield return ov;
                else if (item is ObjectValue nested)
                    yield return nested;
        }
        else if (coll is SetValue sv)
        {
            foreach (var v in sv.Members.Values.OfType<ObjectValue>())
                yield return v;
        }
    }

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
        File.WriteAllText(Path.Combine(idDir, "app.deenv"), appDoc);
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
