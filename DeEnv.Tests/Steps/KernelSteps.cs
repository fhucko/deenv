using System.Net;
using System.Net.Sockets;
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
    // self-hosted generic UI. Two copies under different file names are two DIFFERENT app
    // documents → two different derived data files (alpha-data.json / beta-data.json), so the
    // stores cannot alias — the data-sovereignty point of the slice.
    private const string BoolApp = """
    types
        Db
            ready bool
    """;

    // A fully-custom app that renders the kernel's `sys.instances` global — the read-only list of
    // hosted instances (app + ports) provided under the framework `sys` namespace. A custom fn
    // render() runs in the app scope (parent = system), so `sys` resolves by walking up and
    // `sys.instances` reads its prop. Proves the read path end-to-end through the real scope chain.
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
                        <span class="port">
                            i.port
                        <span class="assets">
                            i.assetsPort
    """;

    private HostedInstance Alpha => ctx.Kernel!.Instances[0];
    private HostedInstance Beta => ctx.Kernel!.Instances[1];

    // Captures the error raised while resolving a deliberately-invalid registry.
    private Exception? _configError;

    // The `list` scenario: the served console page, and the tokens it must contain — every app
    // name plus the ports that ONLY the registry global could have rendered (each instance's app
    // port, and the OTHER instances' assets ports; the console's own assets port is skipped because
    // it also appears in the page's window.initInfraPort bootstrap, so it wouldn't prove anything).
    private string _consoleHtml = "";
    private string[] _expectedApps = [];
    private int[] _expectedPorts = [];

    // The `create` scenarios: the instance produced by CreateAsync and the port pair it was assigned
    // (kept so a post-restart instance can be re-found by its persisted port).
    private HostedInstance? _created;
    private int _createdAppPort;
    private int _createdInfraPort;

    // The `delete` scenarios: the created instance's id-dir, captured before deletion so we can
    // assert the whole store directory is gone afterwards.
    private string _createdIdDir = "";

    // The `clone` scenarios: the instance produced by CloneAsync and the port pair it was assigned
    // (kept so a post-restart clone can be re-found by its persisted port).
    private HostedInstance? _cloned;
    private int _clonedAppPort;
    private int _clonedInfraPort;

    // The `switch` scenarios: the new port pair the instance was switched to, and the old app port
    // it was switched away from (kept so we can assert the new binding serves and the old does not),
    // plus the error a rejected switch raises.
    private int _switchedAppPort;
    private int _switchedInfraPort;
    private int _oldAppPort;
    private Exception? _switchError;

    // ── Given ─────────────────────────────────────────────────────────────────

    [Given("a registry of two instances on distinct port pairs")]
    public void GivenRegistry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deenv-kernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ctx.KernelDir = dir;

        // Storage is id-based: each instance lives under instances/<id>/app.app, resolved purely by
        // its id. The `app` field is a display name label only. Two distinct ids → two distinct
        // id-dirs → two distinct stores (the data-sovereignty point of the slice).
        WriteApp(dir, 1, BoolApp);
        WriteApp(dir, 2, BoolApp);

        int aApp = FreePort(), aInfra = FreePort(), bApp = FreePort(), bInfra = FreePort();
        File.WriteAllText(Path.Combine(dir, "kernel.json"), $$"""
        {
          "instances": [
            { "id": 1, "app": "alpha", "appPort": {{aApp}}, "infraPort": {{aInfra}} },
            { "id": 2, "app": "beta",  "appPort": {{bApp}}, "infraPort": {{bInfra}} }
          ]
        }
        """);
    }

    [Given("a registry of one instance")]
    public void GivenRegistryOfOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deenv-kernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ctx.KernelDir = dir;

        WriteApp(dir, 1, BoolApp);

        int aApp = FreePort(), aInfra = FreePort();
        File.WriteAllText(Path.Combine(dir, "kernel.json"), $$"""
        {
          "instances": [
            { "id": 1, "app": "alpha", "appPort": {{aApp}}, "infraPort": {{aInfra}} }
          ]
        }
        """);
    }

    [Given("a registry whose only instance is a console app that lists the instances")]
    public void GivenConsoleRegistryOfOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deenv-kernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ctx.KernelDir = dir;

        WriteApp(dir, 1, ConsoleApp);
        int cApp = FreePort(), cInfra = FreePort();
        File.WriteAllText(Path.Combine(dir, "kernel.json"), $$"""
        {
          "instances": [
            { "id": 1, "app": "console", "appPort": {{cApp}}, "infraPort": {{cInfra}} }
          ]
        }
        """);
    }

    // ── When / Given (start) ────────────────────────────────────────────────────

    [When("the kernel starts")]
    [Given("the kernel has started")]
    public async Task WhenKernelStartsAsync()
    {
        var registry = RegistryReader.Read(Path.Combine(ctx.KernelDir!, "kernel.json"));
        ctx.Kernel = new KernelHost(ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"));
        await ctx.Kernel.StartAsync(KernelHost.SpecsFor(registry, ctx.KernelDir!));
    }

    // ── create: add an instance to a running kernel ──────────────────────────────

    [When("the operator creates an instance from a bool app on a free port pair")]
    [Given("the operator creates an instance from a bool app on a free port pair")]
    public async Task WhenOperatorCreatesInstanceAsync()
    {
        _createdAppPort = FreePort();
        _createdInfraPort = FreePort();
        _created = await ctx.Kernel!.CreateAsync(
            BoolApp, "app", _createdAppPort, _createdInfraPort,
            ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"));
        // Its id-dir (<KernelDir>/instances/<id>) is the directory holding the written app document,
        // captured now so a later delete assertion can prove the whole store directory is removed.
        _createdIdDir = Path.GetDirectoryName(_created.Spec.SchemaPath)!;
    }

    // ── ghost id-dir: a new instance must never reuse an orphaned directory ───────

    private int _orphanId;

    [Given("an orphaned instance directory {string} exists")]
    public void GivenOrphanedInstanceDir(string id)
    {
        // Simulate a "ghost": an instances/<n>/ dir left on disk by a create whose registry write
        // failed, so it is NOT in kernel.json (and not in the live set). The next create must mint an
        // id PAST it — never reuse the dir — or it would adopt the orphan's stale data.
        _orphanId = int.Parse(id);
        Directory.CreateDirectory(Path.Combine(ctx.KernelDir!, "instances", id));
    }

    [Then("the created instance's id is past the orphaned directory")]
    public async Task ThenCreatedIdPastOrphanAsync() =>
        await Assert.That(_created!.Spec.Id).IsGreaterThan(_orphanId);

    // ── delete: remove a created instance from a running kernel ───────────────────

    [When("the operator deletes the created instance")]
    [Given("the operator deletes the created instance")]
    public async Task WhenOperatorDeletesInstanceAsync()
    {
        await ctx.Kernel!.DeleteAsync(_created!, Path.Combine(ctx.KernelDir!, "kernel.json"));
    }

    // Delete a REGISTRY ("boot") instance — the second of two — by addressing it by its unique id,
    // proving delete is uniform now (no boot refusal). Capture its id-dir first so a later assertion
    // can prove the whole store directory is removed.
    [When("the operator deletes the second instance by its id")]
    public async Task WhenOperatorDeletesSecondAsync()
    {
        _createdIdDir = Path.GetDirectoryName(Beta.Spec.SchemaPath)!;
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.Id == Beta.Spec.Id);
        await ctx.Kernel.DeleteAsync(target, Path.Combine(ctx.KernelDir!, "kernel.json"));
    }

    [Then("the kernel hosts only the first instance")]
    public async Task ThenHostsOnlyFirstAsync()
    {
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(1);
    }

    [Then("the second instance's store directory is gone")]
    public async Task ThenSecondStoreDirGoneAsync()
    {
        await Assert.That(Directory.Exists(_createdIdDir)).IsFalse();
    }

    [Then("the deleted instance no longer serves its root")]
    public async Task ThenDeletedNoLongerServesAsync()
    {
        // Its app host is stopped, so the port no longer accepts connections — the GET fails.
        await Assert.That(await ServesRootAsync(_createdAppPort)).IsFalse();
    }

    [Then("the kernel hosts only the original instance")]
    public async Task ThenHostsOnlyOriginalAsync()
    {
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(1);
    }

    [Then("the created instance's store directory is gone")]
    public async Task ThenStoreDirGoneAsync()
    {
        await Assert.That(Directory.Exists(_createdIdDir)).IsFalse();
    }

    [Then("the console instance's page no longer lists the created instance")]
    public async Task ThenConsoleDoesNotListCreatedAsync()
    {
        // The created instance's app port could only ever appear on the console page via the LIVE
        // `instances` global; after delete the kernel hosts only the console, so it is gone.
        using var http = new HttpClient();
        var html = await http.GetStringAsync($"http://localhost:{ctx.Kernel!.Instances[0].AppPort}/");
        await Assert.That(html).DoesNotContain(_createdAppPort.ToString());
    }

    // ── clone: copy a created instance (app doc + data) onto a new port pair ───────

    [When("the operator clones the created instance onto a free port pair")]
    [Given("the operator clones the created instance onto a free port pair")]
    public async Task WhenOperatorClonesInstanceAsync()
    {
        _clonedAppPort = FreePort();
        _clonedInfraPort = FreePort();
        _cloned = await ctx.Kernel!.CloneAsync(
            _created!, _clonedAppPort, _clonedInfraPort,
            ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"));
    }

    [Then("the clone serves its root on its assigned port")]
    public async Task ThenCloneServesRootAsync()
    {
        // After a restart _cloned is disposed; re-find the live instance by its persisted port.
        var instance = ctx.Kernel!.Instances.Single(i => i.AppPort == _clonedAppPort);
        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://localhost:{instance.AppPort}/");
        await Assert.That((int)resp.StatusCode).IsEqualTo(200);
        await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("input type=\"checkbox\"");
    }

    [Then("the clone's data matches the source")]
    public async Task ThenCloneDataMatchesSourceAsync()
    {
        // The source's bool was toggled to true before the clone; CloneAsync copied the DATA file, so
        // the clone's sovereign store carries the same value (true) — a true copy, not an empty store.
        var instance = ctx.Kernel!.Instances.Single(i => i.AppPort == _clonedAppPort);
        await Assert.That(instance.Store.ReadNode(NodePath.Root.Field("ready"))).IsEqualTo(new BoolValue(true));
        using var http = new HttpClient();
        await Assert.That(await http.GetStringAsync($"http://localhost:{instance.AppPort}/")).Contains(" checked");
    }

    [Then("the kernel now hosts three instances")]
    public async Task ThenHostsThreeAsync()
    {
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(3);
    }

    // ── clone a BOOT instance by its unique id (the wrong-clone fix) ───────────────

    [Given("the second instance's data changes")]
    public void WhenSecondDataChanges()
    {
        // Toggle the SECOND boot instance's bool to true through its own store. Only the second one
        // changes — the first stays false — so a later assertion on the clone's data proves the RIGHT
        // boot was cloned (the old all-boots-share-id-0 model would have cloned the first).
        Beta.Store.WriteObject(
            NodePath.Root,
            new ObjectValue(new Dictionary<string, NodeValue> { ["ready"] = new BoolValue(true) }));
    }

    [When("the operator clones the second instance by its id onto a free port pair")]
    public async Task WhenCloneSecondByIdAsync()
    {
        _clonedAppPort = FreePort();
        _clonedInfraPort = FreePort();
        // Address the boot instance BY ITS UNIQUE ID (resolved from the live set) — the same lookup
        // the host-action clone path uses — proving a boot instance is individually addressable now.
        var bootId = Beta.Spec.Id;
        var source = ctx.Kernel!.Instances.Single(i => i.Spec.Id == bootId);
        _cloned = await ctx.Kernel.CloneAsync(
            source, _clonedAppPort, _clonedInfraPort,
            ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"));
    }

    [Then("the clone's data matches the second instance")]
    public async Task ThenCloneDataMatchesSecondAsync()
    {
        // The clone carries the SECOND boot's data (ready = true), not the first's (false) — so the
        // right boot was addressed and copied. (The first boot stays false, asserted implicitly: a
        // wrong clone would have copied false here.)
        var instance = ctx.Kernel!.Instances.Single(i => i.AppPort == _clonedAppPort);
        await Assert.That(instance.Store.ReadNode(NodePath.Root.Field("ready"))).IsEqualTo(new BoolValue(true));
        using var http = new HttpClient();
        await Assert.That(await http.GetStringAsync($"http://localhost:{instance.AppPort}/")).Contains(" checked");
    }

    // ── switch: re-bind an instance to a new port pair ────────────────────────────

    [When("the operator switches the original instance to a free port pair")]
    [Given("the operator switches the original instance to a free port pair")]
    public async Task WhenOperatorSwitchesInstanceAsync()
    {
        _oldAppPort = Alpha.AppPort;
        _switchedAppPort = FreePort();
        _switchedInfraPort = FreePort();
        await ctx.Kernel!.SwitchAsync(
            Alpha, _switchedAppPort, _switchedInfraPort, Path.Combine(ctx.KernelDir!, "kernel.json"));
    }

    [When("the operator switches the first instance onto the second instance's port")]
    public async Task WhenOperatorSwitchesOntoUsedPortAsync()
    {
        _oldAppPort = Alpha.AppPort;
        try
        {
            // Target Beta's live app port — already bound, so the guard must reject before any stop.
            await ctx.Kernel!.SwitchAsync(
                Alpha, Beta.AppPort, FreePort(), Path.Combine(ctx.KernelDir!, "kernel.json"));
        }
        catch (Exception ex)
        {
            _switchError = ex;
        }
    }

    [Then("the original instance serves its root on the new port")]
    public async Task ThenServesOnNewPortAsync()
    {
        // After a restart Alpha is a fresh handle; re-find the live instance by its persisted new port.
        var instance = ctx.Kernel!.Instances.Single(i => i.AppPort == _switchedAppPort);
        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://localhost:{instance.AppPort}/");
        await Assert.That((int)resp.StatusCode).IsEqualTo(200);
        await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("input type=\"checkbox\"");
    }

    [Then("the original instance no longer serves its root on the old port")]
    public async Task ThenNoLongerServesOnOldPortAsync()
    {
        await Assert.That(await ServesRootAsync(_oldAppPort)).IsFalse();
    }

    [Then("the switch is rejected with a clear kernel-config error")]
    public async Task ThenSwitchRejectedAsync()
    {
        await Assert.That(_switchError).IsNotNull();
        await Assert.That(_switchError is KernelConfigException).IsTrue();
        await Assert.That(_switchError!.Message).Contains("Port");
    }

    [Then("both instances still serve their roots on their original ports")]
    public async Task ThenBothStillServeAsync()
    {
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(2);
        using var http = new HttpClient();
        foreach (var instance in ctx.Kernel.Instances)
        {
            var resp = await http.GetAsync($"http://localhost:{instance.AppPort}/");
            await Assert.That((int)resp.StatusCode).IsEqualTo(200);
            await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("input type=\"checkbox\"");
        }
    }

    [Then("the created instance serves its root on its assigned port")]
    public async Task ThenCreatedServesRootAsync()
    {
        // After a restart _created is disposed; re-find the live instance by its persisted port.
        var instance = ctx.Kernel!.Instances.Single(i => i.AppPort == _createdAppPort);
        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://localhost:{instance.AppPort}/");
        await Assert.That((int)resp.StatusCode).IsEqualTo(200);
        await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("input type=\"checkbox\"");
    }

    [Then("the kernel now hosts both instances")]
    public async Task ThenHostsBothAsync()
    {
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(2);
    }

    [Then("the console instance's page lists the created instance")]
    public async Task ThenConsoleListsCreatedAsync()
    {
        // The original console instance ([0]) was already running when the create happened. Its page
        // must now show the created instance's app port — which can ONLY appear via the LIVE
        // `instances` global (a frozen boot snapshot would have listed only the console itself).
        using var http = new HttpClient();
        var html = await http.GetStringAsync($"http://localhost:{ctx.Kernel!.Instances[0].AppPort}/");
        await Assert.That(html).Contains(_createdAppPort.ToString());
    }

    [When("the kernel restarts from its persisted registry")]
    public async Task WhenKernelRestartsAsync()
    {
        await ctx.Kernel!.DisposeAsync();
        var registry = RegistryReader.Read(Path.Combine(ctx.KernelDir!, "kernel.json"));
        ctx.Kernel = new KernelHost(ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"));
        await ctx.Kernel.StartAsync(KernelHost.SpecsFor(registry, ctx.KernelDir!));
    }

    [When("the created instance's data changes")]
    [Given("the created instance's data changes")]
    public void WhenCreatedDataChanges()
    {
        _created!.Store.WriteObject(
            NodePath.Root,
            new ObjectValue(new Dictionary<string, NodeValue> { ["ready"] = new BoolValue(true) }));
    }

    [Then("the original instance is unchanged")]
    public async Task ThenOriginalUnchangedAsync()
    {
        await Assert.That(Alpha.Store.ReadNode(NodePath.Root.Field("ready"))).IsEqualTo(new BoolValue(false));
    }

    // ── Then ──────────────────────────────────────────────────────────────────

    [Then("each instance serves its root on its own port")]
    public async Task ThenEachServesRootAsync()
    {
        await Assert.That(ctx.Kernel!.Instances.Count).IsEqualTo(2);
        await Assert.That(Alpha.AppPort).IsNotEqualTo(Beta.AppPort);

        using var http = new HttpClient();
        foreach (var instance in ctx.Kernel.Instances)
        {
            var resp = await http.GetAsync($"http://localhost:{instance.AppPort}/");
            await Assert.That((int)resp.StatusCode).IsEqualTo(200);
            await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("input type=\"checkbox\"");
        }
    }

    // ── data sovereignty ────────────────────────────────────────────────────────

    [When("one instance's data changes")]
    public void WhenOneChanges()
    {
        // Toggle the first instance's bool through its OWN store (each instance is sovereign).
        Alpha.Store.WriteObject(
            NodePath.Root,
            new ObjectValue(new Dictionary<string, NodeValue> { ["ready"] = new BoolValue(true) }));
    }

    [Then("that instance reflects the change")]
    public async Task ThenChangedReflectsAsync()
    {
        await Assert.That(Alpha.Store.ReadNode(NodePath.Root.Field("ready"))).IsEqualTo(new BoolValue(true));
        // A rendered true bool attribute is " checked" (leading space; SsrRenderer omits a false
        // one). The embedded UI AST carries the attribute NAME as "checked" (quote-prefixed), so
        // the leading space is what distinguishes a checked checkbox from the always-present AST.
        using var http = new HttpClient();
        await Assert.That(await http.GetStringAsync($"http://localhost:{Alpha.AppPort}/")).Contains(" checked");
    }

    [Then("the other instance is unchanged")]
    public async Task ThenOtherUnchangedAsync()
    {
        await Assert.That(Beta.Store.ReadNode(NodePath.Root.Field("ready"))).IsEqualTo(new BoolValue(false));
        using var http = new HttpClient();
        await Assert.That(await http.GetStringAsync($"http://localhost:{Beta.AppPort}/")).DoesNotContain(" checked");
    }

    // ── list: the registry as a read-only Code global ───────────────────────────

    [Given("a registry whose first instance is a console app that lists the instances")]
    public void GivenConsoleRegistry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deenv-kernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ctx.KernelDir = dir;

        WriteApp(dir, 1, ConsoleApp);
        WriteApp(dir, 2, BoolApp);
        WriteApp(dir, 3, BoolApp);

        int cApp = FreePort(), cAssets = FreePort(),
            aApp = FreePort(), aAssets = FreePort(),
            bApp = FreePort(), bAssets = FreePort();

        // The rendered app names are the registry `app` labels (a display name only — storage is by id).
        _expectedApps = ["console", "alpha", "beta"];
        // Every app port, plus the non-console assets ports — all of which can ONLY appear on the
        // page via the `instances` global (cAssets is excluded: it's also in window.initInfraPort).
        _expectedPorts = [cApp, aApp, bApp, aAssets, bAssets];

        File.WriteAllText(Path.Combine(dir, "kernel.json"), $$"""
        {
          "instances": [
            { "id": 1, "app": "console", "appPort": {{cApp}}, "infraPort": {{cAssets}} },
            { "id": 2, "app": "alpha",   "appPort": {{aApp}}, "infraPort": {{aAssets}} },
            { "id": 3, "app": "beta",    "appPort": {{bApp}}, "infraPort": {{bAssets}} }
          ]
        }
        """);
    }

    [When("I request the console instance's root")]
    public async Task WhenRequestConsoleRootAsync()
    {
        using var http = new HttpClient();
        _consoleHtml = await http.GetStringAsync($"http://localhost:{ctx.Kernel!.Instances[0].AppPort}/");
    }

    [Then("the page lists every hosted instance's app and ports")]
    public async Task ThenPageListsInstancesAsync()
    {
        foreach (var app in _expectedApps)
            await Assert.That(_consoleHtml).Contains(app);
        foreach (var port in _expectedPorts)
            await Assert.That(_consoleHtml).Contains(port.ToString());
    }

    // ── registry validation (fail loudly on aliased stores) ─────────────────────

    [Given("a registry of two instances that resolve to the same data file")]
    public void GivenAliasedRegistry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deenv-kernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ctx.KernelDir = dir;

        // Two entries with the SAME id → the same id-dir (instances/1/) → the same data file, which
        // would silently break sovereignty if the kernel didn't reject it. (Storage is id-based, so
        // a duplicate id — not a duplicate name — is what aliases a store now.)
        WriteApp(dir, 1, BoolApp);
        int p1 = FreePort(), p2 = FreePort(), p3 = FreePort(), p4 = FreePort();
        File.WriteAllText(Path.Combine(dir, "kernel.json"), $$"""
        {
          "instances": [
            { "id": 1, "app": "alpha", "appPort": {{p1}}, "infraPort": {{p2}} },
            { "id": 1, "app": "beta",  "appPort": {{p3}}, "infraPort": {{p4}} }
          ]
        }
        """);
    }

    [When("the kernel registry is resolved")]
    public void WhenRegistryResolved()
    {
        try
        {
            var registry = RegistryReader.Read(Path.Combine(ctx.KernelDir!, "kernel.json"));
            KernelHost.SpecsFor(registry, ctx.KernelDir!);
        }
        catch (Exception ex)
        {
            _configError = ex;
        }
    }

    [Then("it is rejected with a clear kernel-config error")]
    public async Task ThenRejectedAsync()
    {
        await Assert.That(_configError).IsNotNull();
        await Assert.That(_configError is KernelConfigException).IsTrue();
        await Assert.That(_configError!.Message).Contains("same data file");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // Write a fixture app document to its id-dir (<dir>/instances/<id>/app.app), the layout the
    // kernel resolves PURELY by id (AppPaths.SchemaPathForId). The id, not a file name, is what
    // distinguishes one instance's store from another.
    private static void WriteApp(string dir, int id, string appDoc)
    {
        var idDir = AppPaths.IdDirFor(dir, id);
        Directory.CreateDirectory(idDir);
        File.WriteAllText(Path.Combine(idDir, "app.app"), appDoc);
    }

    // True if an instance is serving its root on this app port, false if the port refuses the
    // connection (the host has been stopped). A short timeout keeps a refused connection from hanging.
    private static async Task<bool> ServesRootAsync(int appPort)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var resp = await http.GetAsync($"http://localhost:{appPort}/");
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // Grab a free TCP port by binding to :0, reading the assigned port, then releasing it —
    // the same approach TestInstanceServer uses for its in-process hosts.
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
