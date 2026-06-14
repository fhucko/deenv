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

    // ── When / Given (start) ────────────────────────────────────────────────────

    [When("the kernel starts")]
    [Given("the kernel has started")]
    public async Task WhenKernelStartsAsync()
    {
        var registry = RegistryReader.Read(Path.Combine(ctx.KernelDir!, "kernel.json"));
        ctx.Kernel = new KernelHost();
        await ctx.Kernel.StartAsync(KernelHost.SpecsFor(registry, ctx.KernelDir!));
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
