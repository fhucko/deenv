using Microsoft.Playwright;
using TUnit.Core;

namespace DeEnv.Tests.TestSupport;


/// <summary>
/// One Playwright driver + one Chromium (headless except when debugging in VS) for the WHOLE test run. Spawning the driver (a Node
/// process) and a browser process costs hundreds of ms each; doing it per scenario dominated the suite.
/// Each scenario/test gets a fresh <see cref="IBrowserContext"/> instead — full cookie/storage/cache
/// isolation, a few ms to create — so there is no isolation regression (the server + store are already
/// per-scenario). Browsers are built for many concurrent contexts, so this is safe under TUnit's
/// parallel execution.
/// </summary>
public static class SharedBrowser
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    // ── browser-concurrency cap (the de-flake) ──────────────────────────────────────
    //
    // TUnit runs tests parallel to the core count (6 here). The NON-browser tests (conformance, parsing,
    // store, code-exec — the bulk of the suite) are CPU-cheap and short; letting them run wide is free.
    // A BROWSER test is different: each spins up an in-process GenHTTP server (or, for the operator-IDE
    // scenarios, a kernel hosting THREE instances = six hosts) PLUS a Chromium context driving SSR + a
    // live WebSocket round-trip. Once the suite grew (the M-auth login/logout/user-management browser
    // scenarios roughly doubled the browser population), letting ALL of them overlap 6-wide oversubscribed
    // the box: page loads that take <1s in isolation took 7s+ under peak load, and Playwright's own action
    // waits then tipped past 30s — the intermittent full-suite-only timeouts (a TodoApp add-list, an
    // auth re-login), plus the occasional cross-render "wrong content" read (a contended SPA refetch
    // lagging its URL change). Not a data race — a load race; the data is sound (every flake passed in
    // isolation), and ports are already collision-free (see PortAllocator).
    //
    // This bounds the number of browser CONTEXTS alive at once — the real contended resource — to a few,
    // so the load spike stays in check while the run stays parallel (no serial floor). It gates at the
    // single choke point every browser test funnels through (NewPageAsync — both the Reqnroll scenarios
    // and the plain [Test] auth classes), so a server-only scenario that never opens a page (e.g. most of
    // Access.feature renders the page-state directly) pays NOTHING. The permit is released on the
    // context's Close event — the one signal that fires no matter who tears it down (the AfterScenario
    // hook, or a [Test]'s finally) — and on any failure between acquire and hand-off, so a permit can
    // never leak. 4 is the empirical sweet spot on a 6-core box: it leaves headroom for the in-process
    // servers + the test host while removing the oversubscription, and the heaviest sub-population (the
    // operator-IDE scenarios) stays additionally capped at 2 by Designer.feature's own [ParallelLimiter].
    private const int MaxConcurrentPages = 4;
    private static readonly SemaphoreSlim PageGate = new(MaxConcurrentPages, MaxConcurrentPages);

    private static async Task<IBrowser> BrowserAsync()
    {
        if (_browser != null) return _browser;
        await Gate.WaitAsync();
        try
        {
            _playwright ??= await Playwright.CreateAsync();
            var headless = !System.Diagnostics.Debugger.IsAttached;
            _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                SlowMo = headless ? 0 : 100, // slow down for visibility when debugging in VS
            });
            return _browser;
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// A fresh, isolated page (on its own context) on the shared browser. Close <c>page.Context</c> to
    /// tear the scenario down (it disposes the page with it).
    /// </summary>
    /// <remarks>
    /// Acquires a slot from the browser-concurrency cap (see <see cref="PageGate"/>) before creating the
    /// context, and releases it when that context closes — so at most <see cref="MaxConcurrentPages"/>
    /// browser contexts are ever live at once, keeping the suite's peak load off the oversubscription
    /// cliff that produced the full-suite-only Playwright timeouts.
    ///
    /// The default wait is 10s — a backstop, not a tuning knob. Navigation is made load-independent by
    /// <see cref="PageNav"/> (it waits for DOMContentLoaded, not the /js bundle), so what this bounds is
    /// deterministic element/hydration waits. The old 5s was tuned for the serialized world; once the
    /// heavy operator-IDE scenarios overlap the pool (bounded, not serialized), hydration can briefly
    /// exceed it under load — and even 10s proved too tight under PEAK full-suite load (an SPA
    /// round-trip's clicks/waits occasionally exceeded it). This uses Playwright's native 30s default:
    /// honest headroom for load spikes, the browser-action parallel of the wide fill→save persist gate.
    /// </remarks>
    public static async Task<IPage> NewPageAsync(string? baseUrl = null)
    {
        var browser = await BrowserAsync();

        // Hold a browser-concurrency slot for the lifetime of this context. Acquire BEFORE creating it,
        // release on its Close event — exactly once (the guard), and also if context/page creation throws
        // after the acquire, so the permit is never stranded.
        await PageGate.WaitAsync();
        IBrowserContext context;
        try
        {
            context = await browser.NewContextAsync(
                baseUrl is null ? new BrowserNewContextOptions() : new BrowserNewContextOptions { BaseURL = baseUrl });
        }
        catch
        {
            PageGate.Release();
            throw;
        }

        var released = 0;
        context.Close += (_, _) =>
        {
            if (Interlocked.Exchange(ref released, 1) == 0) PageGate.Release();
        };

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(TestTimeouts.TestMs);
            page.SetDefaultNavigationTimeout(TestTimeouts.TestMs);
            return page;
        }
        catch
        {
            // NewPageAsync failed: close the context (fires Close → releases the slot via the handler).
            // Belt-and-braces release in case Close does not fire for a half-built context.
            try { await context.CloseAsync(); } catch { /* best-effort */ }
            if (Interlocked.Exchange(ref released, 1) == 0) PageGate.Release();
            throw;
        }
    }

    // Tear the shared browser + driver down once, after every test in the assembly has run, so no
    // chromium/node processes leak past the test host.
    [After(Assembly)]
    public static async Task ShutdownAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        _browser = null;
        _playwright = null;
    }
}
