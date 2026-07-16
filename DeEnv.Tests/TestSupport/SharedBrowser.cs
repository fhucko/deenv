using Microsoft.Playwright;
using TUnit.Core;

namespace DeEnv.Tests.TestSupport;


/// <summary>
/// One Playwright driver + one Chromium (headless except when debugging in VS) for the WHOLE test run. Spawning the driver (a Node
/// process) and a browser process costs hundreds of ms each; doing it per scenario dominated the suite.
/// Each scenario/test gets a fresh <see cref="IBrowserContext"/> instead â€” full cookie/storage/cache
/// isolation, a few ms to create â€” so there is no isolation regression (the server + store are already
/// per-scenario). Browsers are built for many concurrent contexts, so this is safe under TUnit's
/// parallel execution.
/// </summary>
public static class SharedBrowser
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    // Concurrent pages follow assembly ParallelLimiter (GlobalParallelLimit = 3). One shared
    // Chromium; Gate only serializes the one-time browser launch.

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
                SlowMo = headless ? 0 : 500, // slow down for visibility when debugging in VS
            });
            return _browser;
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// A fresh, isolated page (on its own context) on the shared browser. Close <c>page.Context</c> to
    /// tear the scenario down (it disposes the page with it).
    /// </summary>
    public static async Task<IPage> NewPageAsync(string? baseUrl = null)
    {
        var browser = await BrowserAsync();

        var context = await browser.NewContextAsync(
            baseUrl is null ? new BrowserNewContextOptions() : new BrowserNewContextOptions { BaseURL = baseUrl });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(TestTimeouts.TestMs);
            page.SetDefaultNavigationTimeout(TestTimeouts.TestMs);
            return page;
        }
        catch
        {
            try { await context.CloseAsync(); } catch { /* best-effort */ }
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

