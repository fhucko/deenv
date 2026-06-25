using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Client data layer, slice 1b — the CLIENT SHIP + server reconstruct ROUND-TRIP, proven END-TO-END in a
// real browser (the only place the new client code — ws.ts slotState — runs; the production bundle is TS
// compiled to JS that runs in the browser, so there is no deterministic Node harness for it). The server
// reconstruct (WsHandler.SlotStateFromWire → RenderState seed) runs in C# in the same loop.
//
// The fixture (InstanceContext.AccessToggleFixtureDb) is the empty-popup footgun CONTROLLED: its whole UI is
// one stateful root <panel> with a SCALAR `var open = false` + a "Show" button; the milestone rows read
// `db.milestones` ONLY when open, and NOTHING else server-side reads it. So the rows can populate ONLY via
// the round-trip: a click flips `open` client-side → the re-render reads the un-shipped `db.milestones` →
// the swallowed VNA fires a refetch carrying the new slotState ({ "comp:" → { open: true } }) → the server
// seeds open:true, reproduces the open panel, harvests "Gate #3", ships it → the client merges + repaints.
// Reverting either half of the wiring leaves the panel empty forever (the fail-before).
//
// De-flake playbook (the project rule): hydration/readiness gates (data-hydrated / data-ready) + Playwright
// auto-waiting locators / WaitForFunction — NO fixed sleeps.
[Binding]
public sealed class ClientDataLayerSteps(InstanceContext ctx)
{
    // Serve the toggle fixture over the production handler tree (TestInstanceServer + the shared browser).
    // Publicly readable, so the round-trip needs no login — 1b isolates the ship/reconstruct mechanism (the
    // floor-gated harvest through a refetch is already proven by 1a).
    [Given("the access-toggle app is served")]
    public async Task GivenToggleServed()
    {
        ctx.Description = InstanceContext.AccessToggleFixtureDb();
        ctx.Server = new TestInstanceServer();
        await ctx.Server.StartAsync(ctx.Description, ctx.DataFilePath);
        ctx.Store = ctx.Server.Store;
    }

    // Open the app and wait for FULL readiness (data-ready: hydrated + WS open + session claimed + the
    // connect-time refetch applied) — the Show click fires a refetch whose reply must drive the merge, so it
    // needs an established, claimed connection (the same gate every mutating/refetching step uses).
    [Given("a visitor opens the toggle app at {string}")]
    public async Task GivenVisitorOpens(string path)
    {
        ctx.Page = await SharedBrowser.NewPageAsync(ctx.BaseUrl);
        await ctx.Page.GotoReadyAsync(path);
        await ctx.Page.WaitReadyAsync();
    }

    // The panel is closed (open:false), so its `if open` branch never ran on the server first paint — nothing
    // read `db.milestones`, structural privacy shipped no Milestone, and no `.gated-row` is present anywhere
    // (neither in the DOM nor leaked through window.initData). This is the pre-toggle state the round-trip
    // moves away from; it also confirms the data is genuinely un-shipped (so the post-click rows can ONLY have
    // arrived via the ship→seed loop).
    [Then("no gated row is shown")]
    public async Task ThenNoGatedRow()
    {
        await ctx.Page!.Locator(".panel button.show").WaitForAsync(); // the panel rendered (closed)
        await Assert.That(await ctx.Page.Locator(".gated-row").CountAsync()).IsEqualTo(0);
        await Assert.That(await ctx.Page.ContentAsync()).DoesNotContain("Gate #3"); // not leaked via initData either
    }

    // Click "Show" — flips the panel's scalar `open` to true CLIENT-side. The re-render now reads the
    // un-shipped `db.milestones`; the swallowed VNA sets needsServerData and maybeRefetch fires, carrying the
    // new slotState. (Gated on data-ready upstream so the refetch acts on a claimed connection.)
    [When("the visitor clicks the panel's Show control")]
    public async Task WhenClicksShow()
    {
        await ctx.Page!.Locator(".panel button.show").ClickAsync();
    }

    // THE ROUND-TRIP LANDED (the assertion that fails before the wiring): the refetch shipped slotState
    // open:true → the server seeded it, reproduced the open panel, harvested "Gate #3", and shipped it → the
    // client merged + repainted the rows. Poll the live DOM (the WS round-trip is async) for the gated row.
    // Before the ship OR the reconstruct exists, the server renders the panel CLOSED (open:false), harvests
    // nothing, and the row never appears.
    [Then("a gated row titled {string} eventually appears")]
    public async Task ThenGatedRowAppears(string title)
    {
        try
        {
            await ctx.Page!.WaitForFunctionAsync(
                "t => [...document.querySelectorAll('.gated-row')].some(e => e.textContent.includes(t))",
                title, new PageWaitForFunctionOptions { Timeout = 10000 });
        }
        catch (TimeoutException)
        {
            var app = await ctx.Page!.Locator("#app").InnerHTMLAsync();
            throw new Exception("The gated row never appeared — the state round-trip did not deliver the data. " +
                "#app (first 1200):\n" + app[..Math.Min(1200, app.Length)]);
        }
    }

    // ── client data layer, slice 4 — the ACTION-MISS round-trip ─────────────────────────────────────────
    //
    // The fixture (InstanceContext.ActionMissFixtureDb) is the action-miss CONTROLLED: the counter's value
    // `a` is shown (so it ships) but its self-`link` is NEVER read on first paint (so `c.link` is un-shipped),
    // and the Bump handler increments THROUGH the link (`c.link.a = c.link.a + 1`). The first read on click is
    // `c.link` → "Value not available". TODAY the handler dies on the VNA; after slice 4 the action-miss
    // round-trip (catch → abort → record → fetch → server harvests `c.link` → merge → re-invoke) completes the
    // increment. There is no other path that touches `a`, so reverting the slice-4 wiring leaves it at 0.

    [Given("the action-miss app is served")]
    public async Task GivenActionMissServed()
    {
        // A FRESH, unique data file: this fixture declares its own (Counter-only) schema, so it must seed
        // from EMPTY — never inherit a sibling scenario's recycled temp file (whose other-shape extent would
        // trip the startup guard). The committed default `Path.GetTempFileName()` can recur under parallel
        // load; a per-serve GUID path is collision-proof.
        ctx.DataFilePath = Path.Combine(Path.GetTempPath(), "deenv-actionmiss-" + Guid.NewGuid().ToString("N") + ".json");
        ctx.Description = InstanceContext.ActionMissFixtureDb();
        ctx.Server = new TestInstanceServer();
        await ctx.Server.StartAsync(ctx.Description, ctx.DataFilePath);
        ctx.Store = ctx.Server.Store;
    }

    // Open + wait for FULL readiness (data-ready): the Bump click fires the action-miss refetch whose reply
    // drives the merge + re-invoke, so it needs an established, claimed connection (the same gate 1b uses).
    [Given("a visitor opens the action-miss app at {string}")]
    public async Task GivenVisitorOpensActionMiss(string path)
    {
        ctx.Page = await SharedBrowser.NewPageAsync(ctx.BaseUrl);
        await ctx.Page.GotoReadyAsync(path);
        await ctx.Page.WaitReadyAsync();
    }

    // The counter's value `a` IS shipped + shown on first paint (the render reads `c.a`), so it reads the
    // seeded 0 — the un-shipped thing is the `link` reference the handler dereferences, not `a` itself.
    [Then("the counter reads {int}")]
    public async Task ThenCounterReads(int value)
    {
        await Assert.That(await ctx.Page!.Locator(".counter-a").InnerTextAsync()).IsEqualTo(value.ToString());
    }

    // Click Bump — its handler's first read is `c.link`, un-shipped → VNA. After slice 4 this triggers the
    // action-miss round-trip (the handler completes via the fetched data + re-invoke).
    [When("the visitor clicks the counter's Bump control")]
    public async Task WhenClicksBump()
    {
        await ctx.Page!.Locator(".counter button.bump").ClickAsync();
    }

    // THE ACTION-MISS ROUND-TRIP LANDED (the assertion that fails before the wiring): the handler read
    // un-shipped `c.link`, the server harvested it, the client re-invoked the handler over the now-present
    // data, and the increment landed on the visible `a` (0 → 1). Poll the live DOM (the round-trip is async).
    // Before the wiring exists the handler dies on the VNA and `a` stays 0 forever.
    [Then("the counter eventually reads {int}")]
    public async Task ThenCounterEventuallyReads(int value)
    {
        try
        {
            await ctx.Page!.WaitForFunctionAsync(
                "v => document.querySelector('.counter-a')?.textContent === v",
                value.ToString(), new PageWaitForFunctionOptions { Timeout = 10000 });
        }
        catch (TimeoutException)
        {
            var app = await ctx.Page!.Locator("#app").InnerHTMLAsync();
            throw new Exception($"The counter never reached {value} — the action-miss round-trip did not " +
                "complete the handler. #app (first 1200):\n" + app[..Math.Min(1200, app.Length)]);
        }
    }

    // The increment also REACHED STORAGE: the re-invoked handler's `c.link.a = …` write persists via the
    // objectPropChange WS op (a positive-id object), so a fresh store read holds the new value. (Before the
    // wiring nothing was sent — the handler died — so the stored value would still be 0.)
    [Then("the stored counter value is {int}")]
    public async Task ThenStoredCounterValueIs(int value)
    {
        await Polling.EventuallyAsync(
            () => ctx.Store!.ReadExtent("Counter").Values.Single() is { } stored
                && stored.Fields.TryGetValue("a", out var v) && v is IntValue i && i.Value == value,
            "the stored counter value never became " + value);
    }
}
