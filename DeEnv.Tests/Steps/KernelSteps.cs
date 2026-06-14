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
            ready: bool
    """;

    // A fully-custom app that renders the kernel's `instances` global — the read-only list of
    // hosted instances (app + ports) provided in the system scope. A custom fn render() runs in
    // the app scope (parent = system), so `instances` resolves by walking up. Proves the read path
    // end-to-end through the real scope chain.
    private const string ConsoleApp = """
    types
        Db
            name: text

    ui
        fn render()
            return <main>
                foreach i in instances
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

    // ── Given ─────────────────────────────────────────────────────────────────

    [Given("a registry of two instances on distinct port pairs")]
    public void GivenRegistry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deenv-kernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ctx.KernelDir = dir;

        File.WriteAllText(Path.Combine(dir, "alpha.app"), BoolApp);
        File.WriteAllText(Path.Combine(dir, "beta.app"), BoolApp);

        int aApp = FreePort(), aInfra = FreePort(), bApp = FreePort(), bInfra = FreePort();
        File.WriteAllText(Path.Combine(dir, "kernel.json"), $$"""
        {
          "instances": [
            { "app": "alpha.app", "appPort": {{aApp}}, "infraPort": {{aInfra}} },
            { "app": "beta.app",  "appPort": {{bApp}}, "infraPort": {{bInfra}} }
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

        File.WriteAllText(Path.Combine(dir, "alpha.app"), BoolApp);

        int aApp = FreePort(), aInfra = FreePort();
        File.WriteAllText(Path.Combine(dir, "kernel.json"), $$"""
        {
          "instances": [
            { "app": "alpha.app", "appPort": {{aApp}}, "infraPort": {{aInfra}} }
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

        File.WriteAllText(Path.Combine(dir, "console.app"), ConsoleApp);
        int cApp = FreePort(), cInfra = FreePort();
        File.WriteAllText(Path.Combine(dir, "kernel.json"), $$"""
        {
          "instances": [
            { "app": "console.app", "appPort": {{cApp}}, "infraPort": {{cInfra}} }
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
        ctx.Kernel = new KernelHost();
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
            BoolApp, _createdAppPort, _createdInfraPort,
            ctx.KernelDir!, Path.Combine(ctx.KernelDir!, "kernel.json"));
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
        ctx.Kernel = new KernelHost();
        await ctx.Kernel.StartAsync(KernelHost.SpecsFor(registry, ctx.KernelDir!));
    }

    [When("the created instance's data changes")]
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

        File.WriteAllText(Path.Combine(dir, "console.app"), ConsoleApp);
        File.WriteAllText(Path.Combine(dir, "alpha.app"), BoolApp);
        File.WriteAllText(Path.Combine(dir, "beta.app"), BoolApp);

        int cApp = FreePort(), cAssets = FreePort(),
            aApp = FreePort(), aAssets = FreePort(),
            bApp = FreePort(), bAssets = FreePort();

        _expectedApps = ["console.app", "alpha.app", "beta.app"];
        // Every app port, plus the non-console assets ports — all of which can ONLY appear on the
        // page via the `instances` global (cAssets is excluded: it's also in window.initInfraPort).
        _expectedPorts = [cApp, aApp, bApp, aAssets, bAssets];

        File.WriteAllText(Path.Combine(dir, "kernel.json"), $$"""
        {
          "instances": [
            { "app": "console.app", "appPort": {{cApp}}, "infraPort": {{cAssets}} },
            { "app": "alpha.app",   "appPort": {{aApp}}, "infraPort": {{aAssets}} },
            { "app": "beta.app",    "appPort": {{bApp}}, "infraPort": {{bAssets}} }
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

        // Two entries naming the SAME app document → the same derived data file (alpha-data.json),
        // which would silently break sovereignty if the kernel didn't reject it.
        File.WriteAllText(Path.Combine(dir, "alpha.app"), BoolApp);
        int p1 = FreePort(), p2 = FreePort(), p3 = FreePort(), p4 = FreePort();
        File.WriteAllText(Path.Combine(dir, "kernel.json"), $$"""
        {
          "instances": [
            { "app": "alpha.app", "appPort": {{p1}}, "infraPort": {{p2}} },
            { "app": "alpha.app", "appPort": {{p3}}, "infraPort": {{p4}} }
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
