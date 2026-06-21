using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace DeEnv.Tests.TestSupport;

/// <summary>
/// Navigation helpers that wait for the HTML to PARSE (DOMContentLoaded), not the full Load event.
///
/// A deenv page server-renders its content into the initial HTML, then fetches the /js bundle over the
/// (separate) infra port to hydrate. Playwright's GotoAsync/WaitForURLAsync default to waiting for `Load`
/// — which only fires AFTER that bundle fetch completes. But the bundle fetch is the slow, contended step
/// once the suite runs heavy scenarios concurrently, and almost every assertion reads SSR content that is
/// already present at DOMContentLoaded. Waiting for `Load` therefore coupled every navigation to a fetch
/// it does not need, producing nav-timeout flakes under load.
///
/// Waiting for DOMContentLoaded is load-independent: the SSR content is there, and any test that needs
/// the hydrated/dynamic UI already waits for its OWN signal (a [data-key], a .selected-user row, …) after
/// navigating — so dropping the Load wait removes the flake without weakening a single assertion.
/// </summary>
public static class PageNav
{
    public static Task GotoContentAsync(this IPage page, string url) =>
        page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

    /// <summary>
    /// Wait until the page's URL matches, by POLLING <c>page.Url</c> — not Playwright's WaitForURLAsync.
    /// A click-triggered navigation can finish BEFORE WaitForURLAsync registers its load-state waiter, so
    /// that API then hangs waiting for a load event that already fired (a real flake we hit). Polling the
    /// current URL has no such race, and works for same-document (pushState) navigations too.
    /// </summary>
    public static async Task WaitForUrlContentAsync(this IPage page, Regex url)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!url.IsMatch(page.Url))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"URL did not match /{url}/ within 10s (current: {page.Url}).");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Navigate AND wait for the client to HYDRATE — for a test that then interacts (clicks a JS handler,
    /// types into a bound input). Waits for DOMContentLoaded (the navigation) then the
    /// <c>data-hydrated</c> marker init.ts sets after its first render, so handlers are guaranteed
    /// attached before the test clicks. Read-only tests use <see cref="GotoContentAsync"/> instead — they
    /// need no hydration, so they pay none of its (contended /js bundle) latency.
    /// </summary>
    public static async Task GotoReadyAsync(this IPage page, string url)
    {
        await page.GotoContentAsync(url);
        await page.WaitHydratedAsync();
    }

    /// <summary>
    /// Wait until the client has hydrated (the <c>data-hydrated</c> marker). Call this at the start of an
    /// interaction step (a click/fill that needs a JS handler) when the navigation happened in a different,
    /// read-only step — so the interaction guarantees its own precondition regardless of how the page was
    /// reached, without coupling that read-only navigation to the /js bundle.
    /// </summary>
    public static Task WaitHydratedAsync(this IPage page) =>
        page.WaitForSelectorAsync("html[data-hydrated]", new() { State = WaitForSelectorState.Attached });

    /// <summary>
    /// Wait until the page is FULLY ready (the <c>data-ready</c> marker): hydration done AND the WebSocket
    /// open AND the session-claim acknowledged AND any connect-time refetch applied. Call this before a step
    /// that MUTATES (a fill that autosaves, a Save commit, a pick/clear) — <c>data-hydrated</c> alone fires
    /// before the socket has settled, so an edit staged in that gap rides the connecting-window outbox and
    /// can be delayed past this wait (or lost to an early disconnect) under load. Gating the mutation on
    /// <c>data-ready</c> guarantees it acts on an established, server-acknowledged connection.
    ///
    /// INTERIM: this WAITS for readiness; the proper fix is offline-resilient mutations that survive a
    /// not-ready/dropped connection regardless of timing (see ws.ts's data-ready note). Remove these waits
    /// when mutations become connection-state-independent.
    /// </summary>
    public static Task WaitReadyAsync(this IPage page) =>
        page.WaitForSelectorAsync("html[data-ready]", new() { State = WaitForSelectorState.Attached });

    /// <summary>
    /// Reveal a set/dict table's flag-gated create form (milestone 11): the inline add row was replaced
    /// with a <c>+ New</c> button that swaps the table for a labeled create form. Idempotent — clicks
    /// <c>.new-btn</c> only when the create form is not already shown — so an add-flow step that reveals
    /// and a later fill step that also calls this never double-toggle. Waits for hydration first (the
    /// reveal click runs a JS handler). Scoped to the first table on the page (every add-flow scenario is
    /// on a single-collection page).
    /// </summary>
    public static async Task RevealCreateFormAsync(this IPage page)
    {
        await page.WaitHydratedAsync();
        if (await page.Locator(".create-form").CountAsync() == 0)
            await page.Locator(".new-btn").First.ClickAsync();
        await page.Locator(".create-form").First.WaitForAsync();
    }
}
