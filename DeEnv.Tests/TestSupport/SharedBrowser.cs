using Microsoft.Playwright;
using TUnit.Core;

namespace DeEnv.Tests.TestSupport;

/// <summary>
/// One Playwright driver + one headless Chromium for the WHOLE test run. Spawning the driver (a Node
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

    private static async Task<IBrowser> BrowserAsync()
    {
        if (_browser != null) return _browser;
        await Gate.WaitAsync();
        try
        {
            _playwright ??= await Playwright.CreateAsync();
            _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            return _browser;
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// A fresh, isolated page (on its own context) on the shared browser, carrying the suite's
    /// fail-fast 5s timeouts — a genuinely stuck wait surfaces in seconds, not Playwright's 30s default.
    /// Close <c>page.Context</c> to tear the scenario down (it disposes the page with it).
    /// </summary>
    public static async Task<IPage> NewPageAsync(string? baseUrl = null)
    {
        var browser = await BrowserAsync();
        var context = await browser.NewContextAsync(
            baseUrl is null ? new BrowserNewContextOptions() : new BrowserNewContextOptions { BaseURL = baseUrl });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(5000);
        page.SetDefaultNavigationTimeout(5000);
        return page;
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
