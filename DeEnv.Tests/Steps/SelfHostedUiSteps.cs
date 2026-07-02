using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for SelfHostedUi.feature — the self-hosted generic UI (milestone 9). The fixtures
// have no custom `fn render()`, so they fall to the default self-hosted generic UI: their
// object pages are rendered by the Code `objectForm` library. Page-kind and navigation
// steps ("I open", "the page is a code page", "the page shows", "the store eventually
// has …") are reused from the other step bindings.
[Binding]
public sealed class SelfHostedUiSteps(InstanceContext ctx)
{
    [Given("the self-hosted form app is running")]
    public async Task GivenSelfHostedFormAppRunning()
    {
        ctx.Description = InstanceContext.SelfHostedFormDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // The three-objects-deep generic-UI app (Db → milestones → Milestone → slices → Slice). Drives the
    // deep-route HYDRATED breadcrumb/title scenario: a deep-link's SSR labels the intermediate "4" crumb,
    // and (with the ancestor-label leaf shipped) the client's post-hydration re-resolve keeps it byte-
    // identical instead of flipping to the raw id.
    [Given("the deep-nav app is running")]
    public async Task GivenDeepNavAppRunning()
    {
        ctx.Description = InstanceContext.DeepNavDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // A Db with an EMPTY set (notes) — drives the collection empty-state scenario.
    [Given("the empty-collection app is running")]
    public async Task GivenEmptyCollectionAppRunning()
    {
        ctx.Description = InstanceContext.EmptyCollectionDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // The committed shop (instances/4) — the fully-auto generic UI over a customer set. Loaded
    // from its real id-dir document so the staged-edit scenarios drive the single source of truth.
    [Given("the shop app is running")]
    public async Task GivenShopAppRunning()
    {
        ctx.Description = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(4));
        await ctx.EnsureServerAndBrowserAsync();
    }

    // ── staged edits + Save/Discard (milestone 11) ──────────────────────────────────
    // The generic ObjectForm stages scalar edits in a data-context (`ctx`) overlay and commits them on
    // Save (autosave OFF by default). These drive that form-level flow.

    // Commit the staged scalar edits: the ObjectForm's Save button (.object-form button.save) flushes
    // the ctx overlay back onto the live object via ctx.commit() (one atomic `commit` WS message with
    // all staged fields in an `edits` array — all-or-none). The commit is an async WS round-trip, so gate on it landing in the persisted store
    // before the scenario reads it (or navigates and re-renders from it) — the pending edits recorded by
    // the fill steps. (A non-emptying assertion that follows — "the store eventually has …" — would also
    // poll, but a save→navigate flow has no such gate, so awaiting here makes every Save path safe.)
    [When("I save the form")]
    public async Task WhenSaveTheForm()
    {
        await ctx.Page!.WaitReadyAsync(); // the Save commits over the WS — wait for the socket to be fully settled
        await ctx.Page!.Locator(".object-form button.save").First.ClickAsync();
        await ctx.AwaitPendingEditsAsync();
    }

    // Discard the staged edits: the Discard button drops the ctx overlay (ctx.discard()) and
    // invalidates the staged props, so the bound inputs re-render to the stored values. Drop the
    // pending edits too — a discard abandons them, so a later Save in the same scenario must not wait
    // for a value that will never persist.
    [When("I discard the form")]
    public async Task WhenDiscardTheForm()
    {
        await ctx.Page!.WaitHydratedAsync();
        await ctx.Page!.Locator(".object-form button.discard").First.ClickAsync();
        ctx.PendingEditValues.Clear();
    }

    // The autosave-mode ObjectForm (autosave={true}) shows NO Save button — edits persist live.
    [Then("the form has no Save button")]
    public async Task ThenNoSaveButton() =>
        await Assert.That(await ctx.Page!.Locator(".object-form button.save").CountAsync()).IsEqualTo(0);

    // The inline Save-status indicator (.save-status, next to the Save/Discard buttons) renders the
    // form's commit lifecycle: "Saving…" then "Saved" on the ack, "Couldn't save" on a reject. The
    // settled text is the proof of feedback; polled because the ack→render is an async WS round-trip.
    [Then("the form save status eventually reads {string}")]
    public async Task ThenSaveStatusEventuallyReads(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.object-form .save-status')?.textContent.trim() === {JsString(text)}");

    // A staged edit must NOT reach the store: the named type's stored object STILL holds the
    // original value. A direct read (not "eventually") — a staged edit fires no WS op, so the store
    // cannot have changed by the time the awaited fill/discard completed.
    [Then("the store still has a {string} whose {string} is {string}")]
    public async Task ThenStoreStillHas(string typeName, string field, string expected) =>
        await Assert.That(ctx.Store!.ReadExtent(typeName).Values
            .Any(o => o.Fields.TryGetValue(field, out var v) && v is TextValue t && t.Text == expected)).IsTrue();

    // ── Save must not persist a SET prop (regression) ───────────────────────────────
    // Capture every WS frame the page SENDS, so a later assertion can prove a staged Save never tried
    // to persist a collection prop. The discriminator is the SENT frame, not a server reply or a
    // console message: the frame is emitted synchronously when Save runs (before any round-trip), so
    // asserting on it is deterministic and race-free. (A staged Save now sends a single `commit` with
    // an `edits` array; SET props must not appear in that array — the server would reject the whole
    // batch and the stored set stays untouched; the sent frame is the robust signal.)
    // Must be wired BEFORE the navigation opens the WS, so it observes the connection from the start.
    private readonly List<string> _sentWsFrames = new();

    [When("I watch the websocket")]
    public Task WhenWatchWebSocket()
    {
        ctx.Page!.WebSocket += (_, ws) =>
            ws.FrameSent += (_, frame) => { lock (_sentWsFrames) _sentWsFrames.Add(frame.Text ?? ""); };
        return Task.CompletedTask;
    }

    // The named-by-scalar object still holds a set prop with the expected member count — proving Save
    // left the live SET untouched (the staged draft is scalar-only; the set binds to the live object).
    // Eventually, since Save's scalar commit is async; once it lands the set is read from fresh storage.
    [Then("the {string} whose {string} is {string} still has {int} {word}")]
    public async Task ThenObjectStillHasSet(string typeName, string field, string value, int count, string setProp) =>
        await EventuallyAsync(() =>
        {
            var obj = ctx.Store!.ReadExtent(typeName).Values
                .FirstOrDefault(o => o.Fields.TryGetValue(field, out var v) && v is TextValue t && t.Text == value);
            return obj != null && obj.Fields.TryGetValue(setProp, out var sv)
                && sv is SetValue set && set.Members.Count == count;
        });

    // No WS message for the named (collection) prop was ever SENT — proving the staged Save persisted
    // only scalars and left the set live. After the atomic-commit change, a staged Save sends one
    // `commit` with an `edits` array; before it sent individual `objectPropChange` frames. Either way,
    // the set prop (`orders`) must not appear. The preceding "eventually has…" + "still has N orders"
    // steps already waited for the WS round-trip, so the buffer is settled; this reads it without sleep.
    [Then("no commit was sent for {string}")]
    public async Task ThenNoPropChangeSentFor(string prop)
    {
        string[] offending;
        lock (_sentWsFrames)
            offending = _sentWsFrames
                .Where(f => (f.Contains("\"op\":\"objectPropChange\"") && f.Contains($"\"prop\":\"{prop}\""))
                         || (f.Contains("\"op\":\"commit\"") && f.Contains($"\"prop\":\"{prop}\"")))
                .ToArray();
        await Assert.That(offending).IsEmpty();
    }

    // objectForm gives each field input (and its label) the class of its prop name
    // (class={p.name}). Record the value as a pending edit so a following Save ("I save"/"I save the
    // form") gates on it reaching the store before the scenario reads it or navigates — the same
    // tracking the "I set the … field to" path uses, unified on the shared context so it works
    // regardless of which fill/save bindings a scenario mixes.
    [When("I fill the {string} field with {string}")]
    public async Task WhenFillField(string field, string value)
    {
        // WaitReadyAsync, not just WaitHydratedAsync: an autosave form persists this edit over the WS as we
        // type, so the socket must be fully settled (open + claimed) — a hydrated-but-not-ready page would
        // ride the connecting-window outbox and could lose the edit under load. (A staged form only writes
        // the draft here, but waiting for ready is uniform and harmless.)
        await ctx.Page!.WaitReadyAsync();
        await ctx.Page!.Locator($"input.{field}").FillAsync(value);
        ctx.PendingEditValues.Add(value);
    }

    // Clear a field to empty. FillAsync("") sets .value and fires `input` for a text input but NOT
    // reliably for an <input type="date">, so the staged draft's `oninput` can miss the clear — and
    // Save would then replay the OLD value. Dispatch a native `input` explicitly so the draft records
    // the empty value as it does for a real keystroke (idempotent for a text input).
    [When("I clear the {string} field")]
    public async Task WhenClearField(string field)
    {
        await ctx.Page!.WaitReadyAsync(); // an autosave form persists the clear over the WS — wait for the settled socket
        var input = ctx.Page!.Locator($"input.{field}");
        await input.FillAsync("");
        await input.DispatchEventAsync("input");
    }

    [Then("the {string} field is a {string} input")]
    public async Task ThenFieldInputKind(string field, string kind)
    {
        var type = await ctx.Page!.Locator($"input.{field}").GetAttributeAsync("type");
        await Assert.That(type).IsEqualTo(kind);
    }

    [Then("the {string} label reads {string}")]
    public async Task ThenLabelReads(string field, string text)
    {
        var actual = await ctx.Page!.Locator($"label.{field}").InnerTextAsync();
        await Assert.That(actual.Trim()).IsEqualTo(text);
    }

    // A set row navigates via a stretched row-link anchor (a.row-link wrapping the identity
    // value), addressed by the member's nested URL (path-walk), e.g. /notes/2 — not /~/<id>.
    [Then("the set row link points at {string}")]
    public async Task ThenSetRowLink(string href) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.set-row a.row-link')].some(e => e.getAttribute('href') === {JsString(href)})");

    // Click the row-link and follow it. End-to-end parity: the link string is built by `nest`
    // on the server (SSR) AND re-built by the client on hydrate (from location.pathname) —
    // following it confirms both agree and that the nested URL resolves to a self-hosted page.
    [When("I follow the set row link")]
    public async Task WhenFollowSetRow()
    {
        await ctx.Page!.Locator(".set-row a.row-link").First.ClickAsync();
        // Wait for the member page nav to land before the next step interacts, so its hydration check
        // sees the NEW page's marker, not the set page's.
        await ctx.Page!.WaitForUrlContentAsync(new System.Text.RegularExpressions.Regex(@"/[0-9]+$"));
    }

    // ── navigable tables (milestone 11) ─────────────────────────────────────────────

    // Click the row's stretched anchor (its accessible name is the identity value). Clicking the
    // real link is robust under Playwright actionability; the overlay covering the rest of the row
    // is confirmed visually (the `::after { inset:0 }` rule).
    [When("I click the set row titled {string}")]
    public async Task WhenClickSetRow(string title) =>
        await ctx.Page!.Locator(".set-row", new() { HasTextString = title })
            .Locator("a.row-link").First.ClickAsync();

    // Click a just-CREATED member's row-link and follow it into its member page. The member was minted
    // client-side (sys.new) then arrayAdd-remapped to its real id; the preceding "store eventually has"
    // step gates on that remap, so the row-link now carries the positive id. Waits for the member URL to
    // land (a trailing numeric segment) so the next assertion sees the member page, not the set view.
    [When("I open the just-created member titled {string}")]
    public async Task WhenOpenCreatedMember(string title)
    {
        await ctx.Page!.Locator(".set-row", new() { HasTextString = title })
            .Locator("a.row-link").First.ClickAsync();
        await ctx.Page!.WaitForUrlContentAsync(new System.Text.RegularExpressions.Regex(@"/[0-9]+$"));
    }

    // Per-row Remove (.set-remove), z-raised above the row-link overlay. With the stopPropagation
    // wiring, clicking it removes the member WITHOUT bubbling to the row link (no navigation).
    [When("I remove the set row titled {string}")]
    public async Task WhenRemoveSetRow(string title) =>
        await ctx.Page!.Locator(".set-row", new() { HasTextString = title })
            .Locator("button.set-remove").First.ClickAsync();

    [Then("no set row eventually shows {string}")]
    public async Task ThenNoSetRow(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => ![...document.querySelectorAll('.set-row')].some(e => e.textContent.includes({JsString(text)}))");

    // A set/dict row never prints the literal text "true"/"false" (a bool renders as a glyph).
    [Then("no set row shows the text {string}")]
    public async Task ThenNoSetRowText(string text) =>
        await Assert.That(await ctx.Page!.Locator(".set-row").AllInnerTextsAsync())
            .DoesNotContain(t => t.Contains(text));

    // The trailing action (Remove) column header: the last header cell, present and empty so the
    // header aligns with the body's Remove cell (the #1 bug — header had N cells, body had N+2).
    [Then("the set table header has a trailing action column")]
    public async Task ThenSetHeaderTrailingColumn() =>
        await ctx.Page!.WaitForFunctionAsync(
            "() => { const th = [...document.querySelectorAll('.set-head th')]; " +
            "return th.length > 0 && th[th.length - 1].textContent.trim() === ''; }");

    // A SetTable column header (.set-head <th>) with the given humanized text IS present — a plain
    // scalar prop is a column.
    [Then("the set table has a {string} column")]
    public async Task ThenSetTableHasColumn(string header) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.set-head th')].some(th => th.textContent.trim() === {JsString(header)})");

    // No SetTable column header has the given text — proving a `multiline` text prop is DROPPED from the
    // auto columns (long-form content lives on the member page, not a scannable list).
    [Then("the set table has no {string} column")]
    public async Task ThenSetTableHasNoColumn(string header) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.set-head th')].every(th => th.textContent.trim() !== {JsString(header)})");

    [Then("the set table header column count equals the body row column count")]
    public async Task ThenSetHeaderAligns() =>
        await ctx.Page!.WaitForFunctionAsync(
            "() => { const h = document.querySelectorAll('.set-head th').length; " +
            "const row = document.querySelector('.set-row'); " +
            "return row != null && h > 0 && row.querySelectorAll('td').length === h; }");

    [Then("the dict table header has a trailing action column")]
    public async Task ThenDictHeaderTrailingColumn() =>
        await ctx.Page!.WaitForFunctionAsync(
            "() => { const th = [...document.querySelectorAll('.dict-head th')]; " +
            "return th.length > 0 && th[th.length - 1].textContent.trim() === ''; }");

    [Then("the dict table header column count equals the body row column count")]
    public async Task ThenDictHeaderAligns() =>
        await ctx.Page!.WaitForFunctionAsync(
            "() => { const h = document.querySelectorAll('.dict-head th').length; " +
            "const row = document.querySelector('.dict-row'); " +
            "return row != null && h > 0 && row.querySelectorAll('td').length === h; }");

    // A bool cell renders a read-only glyph: ✓ for true, ✗ for false (never the text "true"/"false").
    [Then("the {string} row's {string} cell shows the bool glyph for false")]
    public async Task ThenBoolGlyphFalse(string rowTitle, string _) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const r = [...document.querySelectorAll('.set-row')].find(e => e.textContent.includes({JsString(rowTitle)})); " +
            "return r != null && r.querySelector('.bool-cell')?.textContent.trim() === '\\u2717'; }");

    // A reference column renders the referent's label (or blank when unset). The data cell is the
    // row's <td> that is neither the title link (.row-id) nor the trailing Remove cell (.row-action);
    // Note has exactly one such cell (author), so its trimmed text is the rendered reference value.
    [Then("the set row titled {string} shows {string} in its reference cell")]
    public async Task ThenRefCellShows(string rowTitle, string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const r = [...document.querySelectorAll('.set-row')].find(e => e.textContent.includes({JsString(rowTitle)})); " +
            $"const c = r?.querySelector('td:not(.row-id):not(.row-action)'); " +
            $"return c != null && c.textContent.trim() === {JsString(expected)}; }}");

    // The row's single non-label, non-action data cell (same addressing as the reference cell).
    // Used for an enum column: the cell text is the rendered value, which must be the HUMANIZED
    // form (sys.humanize) — e.g. "Shipped", not the raw "shipped".
    [Then("the set row titled {string} shows {string} in its data cell")]
    public async Task ThenDataCellShows(string rowTitle, string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const r = [...document.querySelectorAll('.set-row')].find(e => e.textContent.includes({JsString(rowTitle)})); " +
            $"const c = r?.querySelector('td:not(.row-id):not(.row-action)'); " +
            $"return c != null && c.textContent.trim() === {JsString(expected)}; }}");

    // The exact pathname after a navigation lands (the row link pushed history) — stricter than the
    // substring "the URL is", so a wrong navigation to /notes/2 can't satisfy a "/notes" assertion.
    [Then("the URL path becomes {string}")]
    public async Task ThenUrlPathBecomes(string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => new URL(location.href).pathname === {JsString(expected)}");

    // The URL is a member page under `base` — `base` then a single numeric segment (a created member's
    // id is server-assigned, so the exact value is unknown; this asserts the shape, e.g. /tasks/8).
    [Then("the URL path matches a {string} member")]
    public async Task ThenUrlPathMatchesMember(string @base) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => new RegExp('^' + {JsString(@base)} + '/[0-9]+$').test(new URL(location.href).pathname)");

    // The pathname is STILL exactly this (a handled Remove must not have navigated). The preceding
    // step already settled the removal, so this reads the now-stable URL.
    [Then("the URL path is still {string}")]
    public async Task ThenUrlPathStill(string expected)
    {
        var pathname = new Uri(ctx.Page!.Url).AbsolutePath;
        await Assert.That(pathname).IsEqualTo(expected);
    }

    // ── client-side (SPA) navigation (milestone 11) ─────────────────────────────────
    // The unique per-load token stamped on the window by "I mark the live page" — captured here so a
    // later step can prove the EXACT same token is still present (no reload AND no re-hydration).
    private string _pageToken = "";

    // Mark the LIVE document with a window flag and a unique token. A full page reload re-creates the
    // window (wiping both); a re-hydration re-runs init() (which a reload is the only trigger for here).
    // So if the EXACT token survives a navigation, that navigation neither reloaded nor re-hydrated the
    // page. Wait for readiness first so the marker is set on the fully-settled page (and the same
    // connection a client-side nav reuses).
    [When("I mark the live page")]
    public async Task WhenMarkLivePage()
    {
        await ctx.Page!.WaitReadyAsync();
        _pageToken = "tok-" + Guid.NewGuid().ToString("N");
        await ctx.Page!.EvaluateAsync(
            "(t) => { window.__spa = true; window.__pageToken = t; }", _pageToken);
    }

    // The window flag set by "I mark the live page" is STILL present — proof the navigation did not
    // trigger a full document reload (which would have wiped window.__spa).
    [Then("the live page mark survives")]
    public async Task ThenLivePageMarkSurvives() =>
        await Assert.That(await ctx.Page!.EvaluateAsync<bool>("() => window.__spa === true")).IsTrue();

    // Same hydrated session: the page is still hydrated (data-hydrated present) AND it carries the
    // EXACT token stamped before the nav. The window survives only if there was no reload; init() runs
    // only on the initial document load, so the token being byte-for-byte the same proves the client
    // never re-hydrated — the warm session (and its WebSocket) carried straight through the navigation.
    [Then("the page is still the same hydrated session")]
    public async Task ThenSameHydratedSession()
    {
        await ctx.Page!.WaitHydratedAsync();
        var sameToken = await ctx.Page!.EvaluateAsync<bool>(
            "(t) => window.__pageToken === t", _pageToken);
        await Assert.That(sameToken).IsTrue();
    }

    // The browser tab title (document.title), polled — on a client-side (SPA) navigation it is updated
    // during the breadcrumb sync (commitRender → syncBreadcrumbs), which paints only after the target's
    // render settles, so a one-shot read could race the re-render. Proves the generic title both UPDATES
    // on SPA nav (the desync fix) and shows the LABELED trail (humanized props + object labels).
    [Then("the browser tab title eventually is {string}")]
    public async Task ThenBrowserTitleEventuallyIs(string expected) =>
        await ctx.Page!.WaitForFunctionAsync($"() => document.title === {JsString(expected)}");

    // The breadcrumb text after HYDRATION settles, polled — proves the CLIENT trail is byte-identical to
    // the server's. On a fresh deep-link the SSR already shows the labeled trail; once the client hydrates,
    // syncBreadcrumbs re-resolves every segment over the SHIPPED graph and rebuilds the nav only if its
    // recomputed trail differs. So if an ancestor's label leaf did NOT ship, syncBreadcrumbs flips that
    // INTERMEDIATE crumb from the label to the humanized raw id — and this polled equality catches it
    // (a one-shot read could race that flip). Equal here ⇒ the server and the hydrated client agree.
    [Then("the breadcrumbs eventually read {string}")]
    public async Task ThenBreadcrumbsEventuallyRead(string expected)
    {
        await ctx.Page!.WaitHydratedAsync();
        await ctx.Page!.WaitForFunctionAsync(
            "(e) => document.querySelector('nav.breadcrumbs') != null && " +
            "document.querySelector('nav.breadcrumbs').innerText.trim().replace(/\\s+/g, ' ') === e",
            expected);
    }

    // Browser Back: pops the history entry the client-side nav pushed. The popstate handler (init.ts)
    // writes the base-stripped location back into the `path` var and re-renders over the warm session —
    // no reload, so NO Load event fires. WaitUntil=Commit resolves as soon as the history change is
    // committed (waiting for Load would hang on a same-document pop); the following URL/content steps
    // poll the live DOM/URL for the settled outcome.
    [When("I navigate back")]
    public async Task WhenNavigateBack() =>
        await ctx.Page!.GoBackAsync(new() { WaitUntil = Microsoft.Playwright.WaitUntilState.Commit });

    // ── SPA-nav flash guard (milestone 11) ──────────────────────────────────────────
    // Following a link to an UN-SHIPPED object (sys.resolve → target:null) must NOT optimistically paint
    // the router's NotFound branch and then re-render the real page (a "Not found" flash). These steps
    // arm a flash DETECTOR, drive a real in-app link click, and assert NotFound never rendered.

    [Given("the flash-nav app is running")]
    public async Task GivenFlashNavAppRunning()
    {
        ctx.Description = InstanceContext.FlashNavDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // ── SPA nav to a collection view over un-shipped data (regression) ───────────────
    // The demo app's shape: object collections (a tasks set whose member nests a set + ref, configs/
    // settings dicts). A deep-link to a scalar member ships only that member, so navigating to a
    // collection view speculatively renders over un-shipped data — the path that threw a non-VNA error
    // before this fix.

    [Given("the demo collections app is running")]
    public async Task GivenDemoCollectionsAppRunning()
    {
        ctx.Description = InstanceContext.DemoCollectionsDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // Capture every uncaught page error (window 'error' / unhandled rejection) Playwright surfaces as
    // `PageError`, so a later assertion can prove the SPA navigation raised NONE. This is the decisive
    // bug signal: the buggy speculative render's non-VNA throw escapes navigateClientSide and surfaces
    // here as a pageerror (console.log capture is unreliable — see project memory — so this hooks the
    // structured PageError event, which carries the exact message). Wired BEFORE the navigation so it
    // observes the page from the start.
    private readonly List<string> _pageErrors = new();

    [When("I watch for page errors")]
    public Task WhenWatchForPageErrors()
    {
        ctx.Page!.PageError += (_, error) => { lock (_pageErrors) _pageErrors.Add(error); };
        return Task.CompletedTask;
    }

    // No uncaught page error fired during the navigation. The preceding content assertions (the target
    // table/form actually rendering) already waited for the navigation to settle onto the target view,
    // so by now any synchronous throw from the speculative render would have been captured. On the buggy
    // code the speculative SetTable/ObjectForm render throws a non-VNA error that escapes to here AND the
    // view freezes — so this (with the content steps above) is what makes the scenario fail before the fix.
    [Then("no page error occurred")]
    public async Task ThenNoPageError()
    {
        string[] errors;
        lock (_pageErrors) errors = _pageErrors.ToArray();
        await Assert.That(errors).IsEmpty();
    }

    // Arm a MutationObserver that flips window.__sawNotFound the instant a `.not-found` element ever
    // appears anywhere under #app — so a NotFound that renders and is then replaced is still caught (a
    // post-hoc "is .not-found present now" check would miss a transient flash). Records the CURRENT state
    // too (in case a synchronous paint beat the observer). Wait for readiness first so the detector is
    // armed on the fully-settled page, before the link click that triggers the client-side navigation.
    [When("I arm the not-found detector")]
    public async Task WhenArmNotFoundDetector()
    {
        await ctx.Page!.WaitReadyAsync();
        await ctx.Page!.EvaluateAsync(
            """
            () => {
                window.__sawNotFound = document.querySelector('#app .not-found') != null;
                const obs = new MutationObserver(() => {
                    if (document.querySelector('#app .not-found') != null) window.__sawNotFound = true;
                });
                obs.observe(document.getElementById('app'), { childList: true, subtree: true });
            }
            """);
    }

    // Inject a real same-origin in-app anchor and click it: a plain left-click on an <a href> bubbles to
    // init.ts's delegated `interceptNavigation`, which (the page being the generic UI) takes it over as a
    // client-side navigation — exactly the path a framework-emitted link click follows. The anchor's
    // origin is the test page's own, so it is in-mount and same-origin. Clicking a link the test injected
    // is no less a real interception than clicking a rendered one (the handler is delegated on document
    // and does not care who created the anchor).
    [When("I navigate via an in-app link to {string}")]
    public async Task WhenNavigateViaInAppLink(string path)
    {
        await ctx.Page!.EvaluateAsync(
            """
            (p) => {
                const a = document.createElement('a');
                a.setAttribute('href', p);
                a.id = '__spa_link';
                a.textContent = 'go';
                document.body.appendChild(a);
            }
            """, path);
        await ctx.Page!.Locator("#__spa_link").ClickAsync();
    }

    // The target object form has SETTLED on the expected title — polled (the target paints only after the
    // refetch returns, so a one-shot read could race the re-render). Reaching this guarantees the
    // navigation completed onto the real target view, so the not-found detector below reads a final state.
    [Then("the target title field eventually shows {string}")]
    public async Task ThenTargetTitleEventuallyShows(string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.object-form input.title')?.value === {JsString(expected)}");

    // NotFound NEVER rendered during the navigation: the detector flag stayed false. The preceding
    // content assertions (.object-form + the settled target title) already waited for the target view to
    // appear, so by now any transient NotFound flash would have been observed. This is the decisive flash
    // assertion — without the guard, the optimistic paint renders NotFound here and the flag is true.
    [Then("the not-found view never appeared during the navigation")]
    public async Task ThenNotFoundNeverAppeared() =>
        await Assert.That(await ctx.Page!.EvaluateAsync<bool>("() => window.__sawNotFound === true")).IsFalse();

    // A `refetch` frame was sent — proving the navigation's target was genuinely un-shipped (the flash
    // guard held the view and asked the server), so this scenario actually exercises the guard. Reads the
    // settled sent-frame buffer ("I watch the websocket"); the target-rendered assertions above already
    // waited for the refetch reply, so every frame the nav emits is captured by now.
    [Then("a refetch was sent")]
    public async Task ThenRefetchWasSent()
    {
        bool any;
        lock (_sentWsFrames) any = _sentWsFrames.Any(f => f.Contains("\"op\":\"refetch\""));
        await Assert.That(any).IsTrue();
    }

    // ── flag-gated create view (milestone 11) ───────────────────────────────────────
    // The always-visible inline add row (.set-new/.dict-new) is replaced by a `+ New` button that
    // reveals a labeled create form (.create-form) BELOW the still-visible read-only table; Save commits
    // + returns to the New button, Cancel discards. These steps drive that flow directly.

    // A selector is ABSENT (the inline add form is gone; the create form is gone after Save/Cancel).
    // WaitForFunction so a still-reconciling DOM settles.
    [Then("the page does not show {string}")]
    public async Task ThenPageDoesNotShow(string selector) =>
        await ctx.Page!.WaitForFunctionAsync($"() => document.querySelector({JsString(selector)}) === null");

    // Click the `+ New` button to reveal the create form (below the still-visible table). Hydration
    // is awaited because the click runs a JS handler that flips the component's `creating` state.
    [When("I click the new button")]
    public async Task WhenClickNewButton()
    {
        await ctx.Page!.WaitHydratedAsync();
        await ctx.Page!.Locator(".new-btn").First.ClickAsync();
        await ctx.Page!.Locator(".create-form").First.WaitForAsync();
    }

    // Cancel the create form (discard the draft, return to the table without committing).
    [When("I cancel the create form")]
    public async Task WhenCancelCreateForm() =>
        await ctx.Page!.Locator(".create-form button.cancel").First.ClickAsync();

    // The reopened create form's input for the named prop is BLANK — proving "New" after a Save shows a
    // FRESH draft (state.draft = sys.new), not the just-saved values. Before the M11 fix the create-form's
    // <Field obj={state.draft}> was a slot-stable component whose cached output stayed bound to the OLD
    // (now-added) draft object, so reopening showed the prior values. WaitForFunction so a still-reconciling
    // DOM settles to the fresh draft.
    [Then("the new {string} field is empty")]
    public async Task ThenNewFieldEmpty(string field) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const e = document.querySelector('.create-form input.{field}'); return e != null && e.value === ''; }}");

    // A create-form field is LABELED: a <label> with the prop's class sits in the form (the same
    // label+Input composite the edit/object page uses), proving the form is the labeled edit form, not
    // a row of bare inputs.
    [Then("the create form has a labeled {string} field")]
    public async Task ThenCreateFormLabeledField(string field) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.create-form label.{field}') !== null " +
            $"&& document.querySelector('.create-form input.{field}, .create-form select.{field}') !== null");

    // The just-opened create form takes focus on its first field (the focusNewCreateForm one-shot in
    // ui.ts), so on a long list / small screen the New click visibly does something and the operator can
    // type immediately. WaitForFunction so the focus (set in the same commitRender that mounts the form)
    // is observed once the DOM settles.
    [Then("the create form's first field is focused")]
    public async Task ThenCreateFormFirstFieldFocused() =>
        await ctx.Page!.WaitForFunctionAsync(
            "() => { const f = document.querySelector('.create-form'); const a = document.activeElement; " +
            "return f !== null && a !== null && f.contains(a) " +
            "&& (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA' || a.tagName === 'SELECT'); }");

    // Milestone 11: a hand-written `fn render()` that composes the PUBLIC <ObjectForm> library
    // component — proving the generic-UI library is reachable + usable from userspace.
    [Given("the public-library form app is running")]
    public async Task GivenPublicLibraryFormAppRunning()
    {
        ctx.Description = InstanceContext.PublicLibraryFormDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // Milestone 11 (custom-render collection-link gating): the same public-library composition, but
    // over an object with a set prop, to prove the list-title label is INERT (no <a>) under a custom
    // `fn render()`.
    [Given("the public-library set-form app is running")]
    public async Task GivenPublicLibrarySetFormAppRunning()
    {
        ctx.Description = InstanceContext.PublicLibrarySetFormDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    [Then("the {string} list title is not a link")]
    public async Task ThenListTitleIsNotALink(string title)
    {
        await ctx.Page!.WaitForSelectorAsync($".list-title:text-is('{title}')");
        var linkCount = await ctx.Page.Locator($"a.list-title:text-is('{title}')").CountAsync();
        await Assert.That(linkCount).IsEqualTo(0);
    }

    [Then("the {string} row title is not a link")]
    public async Task ThenRowTitleIsNotALink(string title)
    {
        await ctx.Page!.WaitForSelectorAsync($".row-link:text-is('{title}')");
        var linkCount = await ctx.Page.Locator($"a.row-link:text-is('{title}')").CountAsync();
        await Assert.That(linkCount).IsEqualTo(0);
    }

    // Milestone 11: a hand-written `fn render()` composing TWO staged <ObjectForm>s over the SAME
    // object. Proves "same object, two independent editing contexts" on the EXISTING per-form overlay —
    // each form, a distinct render-tree slot, gets its own draft; a staged edit in one doesn't touch the
    // other or the store until Save (single-client, last-write-wins).
    [Given("the two-contexts form app is running")]
    public async Task GivenTwoContextsFormAppRunning()
    {
        ctx.Description = InstanceContext.TwoContextsDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // The title input of the ObjectForm inside the named context (.context-a / .context-b). The form
    // classes each scalar input by prop name (input.title), so scoping under the section marker
    // addresses exactly one of the two forms.
    [When("I fill the title field in context {string} with {string}")]
    public async Task WhenFillTitleInContext(string context, string value)
    {
        await ctx.Page!.WaitHydratedAsync(); // the bound input's handler must be attached before we type
        await ContextLocator(context).Locator("input.title").FillAsync(value);
    }

    // The title input in the named context still shows the given value — proves the two drafts are
    // INDEPENDENT (editing the other context didn't write this one's draft).
    [Then("the title field in context {string} shows {string}")]
    public async Task ThenTitleInContextShows(string context, string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            "([sel, val]) => document.querySelector(sel + ' input.title')?.value === val",
            new[] { ContextSelector(context), expected });

    // Commit the named context's staged edits: that form's Save button (.object-form button.save,
    // scoped to the section). Sends one atomic `commit` message with all staged scalars.
    [When("I save context {string}")]
    public async Task WhenSaveContext(string context)
    {
        await ctx.Page!.WaitHydratedAsync();
        await ContextLocator(context).Locator(".object-form button.save").First.ClickAsync();
    }

    private Microsoft.Playwright.ILocator ContextLocator(string context) => ctx.Page!.Locator(ContextSelector(context));

    private static string ContextSelector(string context) => ".context-" + context.ToLowerInvariant();

    [Given("the self-hosted dict app is running")]
    public async Task GivenSelfHostedDictAppRunning()
    {
        ctx.Description = InstanceContext.SelfHostedDictDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // ── sys.resolve probe (milestone 11, collapse increment 1) ──────────────────────
    // A hand-written `fn render()` calls sys.resolve(path) and renders its fields into spans. The
    // server resolves for first paint (over the schema's TypeResolver); the client RE-resolves on
    // hydrate (over the SHIPPED descriptors) and reconciles the SAME spans — so each assertion below
    // reads the POST-HYDRATE value (WaitForFunction observes the live DOM after the client render),
    // proving BOTH twins resolve the URL identically (a divergence would change the span text).

    [Given("the resolve-probe app is running")]
    public async Task GivenResolveProbeAppRunning()
    {
        ctx.Description = InstanceContext.ResolveProbeDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    [Then("the resolve probe kind is {string}")]
    public async Task ThenResolveKind(string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.kind')?.textContent.trim() === {JsString(expected)}");

    [Then("the resolve probe prop is {string}")]
    public async Task ThenResolveProp(string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.prop')?.textContent.trim() === {JsString(expected)}");

    [Then("the resolve probe type name is {string}")]
    public async Task ThenResolveTypeName(string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.type-name')?.textContent.trim() === {JsString(expected)}");

    [Then("the resolve probe parent type is {string}")]
    public async Task ThenResolveParentType(string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.parent-type')?.textContent.trim() === {JsString(expected)}");

    [Then("the resolve probe target title is {string}")]
    public async Task ThenResolveTargetTitle(string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.target-title')?.textContent.trim() === {JsString(expected)}");

    // The generic-UI collapse: a default app ships a SINGLE render (the framework-synthesized
    // generic router, set as initUi.ui.render) with NO per-type view binding — the old
    // `initUi.view` ViewInfo (kind/index/objectId) that drove the deleted per-URL dispatch is
    // gone. Reads the live window.initUi the SSR page injected. Before the collapse this object
    // page shipped `view.kind === "type"` and a per-type view index; after, render is present and
    // `view` is absent — proving routing now lives in the one Code render, not C#.
    [Then("the page routes through a single code render with no per-type view binding")]
    public async Task ThenSingleCodeRender()
    {
        var hasRender = await ctx.Page!.EvaluateAsync<bool>("() => !!(window.initUi && window.initUi.ui && window.initUi.ui.render)");
        var hasViewInfo = await ctx.Page!.EvaluateAsync<bool>("() => !!(window.initUi && window.initUi.view)");
        await Assert.That(hasRender).IsTrue();
        await Assert.That(hasViewInfo).IsFalse();
    }

    // ── optional date/decimal/datetime left empty (pre-existing bug fix) ─────────

    [Given("the optional-leaves app is running")]
    public async Task GivenOptionalLeavesAppRunning()
    {
        ctx.Description = InstanceContext.OptionalLeavesDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // Poll until the object of `typeName` whose `title` matches (the test data has unique titles)
    // exists AND its `field` satisfies `fieldOk`. Gating on the FIELD, not just the object's
    // existence, is the fix for root-cause A here: a clear-then-Save edits a field of an
    // ALREADY-SEEDED object (which is present instantly), so a poll that only waited for the object
    // would read the field before the async WS write landed and assert the stale value. Polling the
    // field condition returns the instant the edit is on disk (same Polling budget, just the right
    // predicate).
    private async Task EventuallyFieldAsync(string typeName, string title, string field, Func<NodeValue?, bool> fieldOk) =>
        await EventuallyAsync(() =>
        {
            var obj = ctx.Store!.ReadExtent(typeName).Values
                .FirstOrDefault(o => o.Fields.TryGetValue("title", out var v) && v is TextValue t && t.Text == title);
            if (obj == null) return false;
            obj.Fields.TryGetValue(field, out var fv);
            return fieldOk(fv);
        });

    // An optional date/decimal/datetime left empty means UNSET: it round-trips as the empty leaf
    // (TextValue "") — the server must not force-parse "". A never-set seed field is absent; an
    // explicitly-emptied field is the empty text leaf. Both read as unset. Polls until the field is
    // unset (not just until the object exists), so a clear→Save that empties a previously-set field
    // is awaited rather than read stale.
    [Then("the store has a {string} titled {string} whose {string} is unset")]
    public async Task ThenStoreFieldUnset(string typeName, string title, string field) =>
        await EventuallyFieldAsync(typeName, title, field, v => v is null or TextValue { Text: "" });

    // A non-empty optional leaf still parses + persists. The Code runtime projects a date/decimal
    // to its text form (DbBridge.ScalarToExec), so the stored value reads back as that text. Polls
    // until the field equals the expected value (the create's WS write is async).
    [Then("the store has a {string} titled {string} whose {string} is {string}")]
    public async Task ThenStoreFieldText(string typeName, string title, string field, string expected) =>
        await EventuallyFieldAsync(typeName, title, field, v => v is not null && StoredLeafText(v) == expected);

    // The text projection of a stored scalar leaf (matches DbBridge.ScalarToExec / the wire form),
    // so a date persists+reads as "yyyy-MM-dd" and a decimal as its invariant string.
    private static string? StoredLeafText(NodeValue v) => v switch
    {
        TextValue t => t.Text,
        DecimalValue d => d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DateValue d => d.Value.ToString("yyyy-MM-dd"),
        DateTimeValue dt => dt.Value.ToString("O"),
        IntValue i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        BoolValue b => b.Value ? "true" : "false",
        _ => null,
    };

    // ── enum support (first slice) ──────────────────────────────────────────────

    [Given("the enum fixture app is running")]
    public async Task GivenEnumFixtureAppRunning()
    {
        ctx.Description = InstanceContext.EnumFixtureDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // objectForm renders an enum scalar prop as <select class={p.name}> with an <option> per
    // value (plus a leading empty option). The options' value attributes must be exactly the
    // enum's values, in order.
    [Then("the {string} field is a select with options {string}")]
    public async Task ThenSelectOptions(string field, string commaList)
    {
        var expected = commaList.Split(',').Select(s => s.Trim()).ToArray();
        var values = await ctx.Page!.Locator($"select.{field} option").EvaluateAllAsync<string[]>(
            "els => els.map(e => e.getAttribute('value'))");
        // The empty placeholder option ("") comes first; the enum's values follow it.
        await Assert.That(values).IsEquivalentTo(new[] { "" }.Concat(expected).ToArray());
    }

    // The DISPLAYED option labels (textContent) — humanized, while the value attributes stay the
    // bare names (asserted above). Proves the generic UI shows `sys.humanize(value)`, not the raw name.
    [Then("the {string} select displays options {string}")]
    public async Task ThenSelectDisplays(string field, string commaList)
    {
        var expected = commaList.Split(',').Select(s => s.Trim()).ToArray();
        var texts = await ctx.Page!.Locator($"select.{field} option").EvaluateAllAsync<string[]>(
            "els => els.map(e => e.textContent.trim())");
        // The first option is the empty/unset placeholder ("(none)"); the humanized values follow.
        await Assert.That(texts).IsEquivalentTo(new[] { "(none)" }.Concat(expected).ToArray());
    }

    // Choose an option: SelectOptionAsync fires the <select>'s change event, whose binding
    // writes the chosen value back through sys.field's setValue (autosave → objectPropChange).
    [When("I choose {string} in the {string} select")]
    public async Task WhenChooseInSelect(string value, string field) =>
        await ctx.Page!.Locator($"select.{field}").SelectOptionAsync(value);

    // ── multiline text presentation attribute ──────────────────────────────────────

    [Given("the multiline fixture app is running")]
    public async Task GivenMultilineFixtureAppRunning()
    {
        ctx.Description = InstanceContext.MultilineFixtureDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // A `text multiline` prop renders as a <textarea class={p.name}> (not an <input>), and its bound
    // value is its TEXT CONTENT (browsers ignore `value` on a textarea — both twins set .value). Read
    // .value, the same property the client binds, so this works pre- and post-hydration.
    [Then("the {string} field is a textarea showing {string}")]
    public async Task ThenTextareaShows(string field, string expected)
    {
        var el = ctx.Page!.Locator($"textarea.{field}");
        await Assert.That(await el.CountAsync()).IsEqualTo(1);
        await Assert.That(await el.InputValueAsync()).IsEqualTo(expected);
    }

    // Fill the textarea with a genuinely multi-line value (the two lines joined by a real newline),
    // proving the textarea holds real multi-line text. NOT registered as a pending edit: the newline
    // is escaped on disk (\n), so the substring gate would miss it — the dedicated store assertion
    // below polls the PARSED store value (a real newline) instead.
    [When("I fill the {string} textarea with two lines {string} and {string}")]
    public async Task WhenFillTextareaTwoLines(string field, string line1, string line2)
    {
        await ctx.Page!.WaitReadyAsync(); // a staged edit still rides the WS on Save — wait for the settled socket
        await ctx.Page!.Locator($"textarea.{field}").FillAsync(line1 + "\n" + line2);
    }

    // The stored leaf is the two lines joined by a real newline — the value survived the round-trip
    // unchanged (multiline is presentation only; the value is and stays plain text). Polls the parsed
    // store (ReadExtent), so the comparison is over the real newline, not the on-disk \n escape.
    [Then("the store eventually has a {string} whose {string} is the two lines {string} and {string}")]
    public async Task ThenStoreHasTwoLines(string typeName, string field, string line1, string line2) =>
        await EventuallyAsync(() => ctx.Store!.ReadExtent(typeName).Values
            .Any(o => o.Fields.TryGetValue(field, out var v) && v is TextValue t && t.Text == line1 + "\n" + line2));

    [Given("the self-hosted scalar dict app is running")]
    public async Task GivenSelfHostedScalarDictAppRunning()
    {
        ctx.Description = InstanceContext.SelfHostedScalarDictDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // ── direct HTTP (status codes) ──────────────────────────────────────────────────

    private int _lastStatus;
    private string _lastBody = "";

    [When("I request {string}")]
    public async Task WhenRequest(string path)
    {
        using var http = new System.Net.Http.HttpClient();
        var r = await http.GetAsync(ctx.BaseUrl + path);
        _lastStatus = (int)r.StatusCode;
        _lastBody = await r.Content.ReadAsStringAsync();
    }

    [Then("the response status is {int}")]
    public async Task ThenResponseStatus(int code) => await Assert.That(_lastStatus).IsEqualTo(code);

    [Then("the response body contains {string}")]
    public async Task ThenResponseBody(string text) => await Assert.That(_lastBody).Contains(text);

    // A scalar dictionary entry's own page (/<dict>/<key>) — the shared leaf editor.
    [Then("the entry value shows {string}")]
    public async Task ThenEntryValueShows(string expected)
    {
        var v = await ctx.Page!.Locator(".leaf-form input.value").First.GetAttributeAsync("value") ?? "";
        await Assert.That(v).IsEqualTo(expected);
    }

    // A scalar dictionary entry's value, read at its path (/<dict>/<key>).
    [When("the dict entry {string} eventually has value {string}")]
    [Then("the dict entry {string} eventually has value {string}")]
    public async Task ThenDictEntryHasValue(string key, string value) =>
        await EventuallyAsync(() =>
            ctx.Store!.ReadNode(NodePath.FromSegments(["settings", key])) is TextValue t && t.Text == value);

    // ── component-local state (creation prototype) ─────────────────────────────────

    [Given("the component form app is running")]
    public async Task GivenComponentFormAppRunning()
    {
        ctx.Description = InstanceContext.ComponentFormDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    [When("I fill the draft title with {string}")]
    public async Task WhenFillDraftTitle(string value) =>
        await ctx.Page!.Locator("input.draft-title").FillAsync(value);

    [When("I click create")]
    public async Task WhenClickCreate() =>
        await ctx.Page!.Locator("button.create").ClickAsync();

    [Then("the note list eventually shows {string}")]
    public async Task ThenNoteListShows(string title) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.note-row')].some(e => e.textContent.includes({JsString(title)}))");

    [Then("the draft title is empty")]
    public async Task ThenDraftTitleEmpty() =>
        await ctx.Page!.WaitForFunctionAsync("() => document.querySelector('input.draft-title')?.value === ''");

    // ── reactive components: slot-path identity (milestone 11) ──────────────────────

    [Given("the rebuilt-descriptor component app is running")]
    public async Task GivenRebuiltDescriptorComponentAppRunning()
    {
        ctx.Description = InstanceContext.ComponentFormRebuiltDescDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // A `fn render()` that returns a component directly (value/root position) — slice 4b.
    [Given("the root-component app is running")]
    public async Task GivenRootComponentAppRunning()
    {
        ctx.Description = InstanceContext.RootComponentDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // Bumps an unrelated page-level counter, forcing the page to re-render and rebuild the
    // component's descriptor argument — the re-render the slot identity must survive.
    [When("I toggle the unrelated flag")]
    public async Task WhenToggleUnrelatedFlag() =>
        await ctx.Page!.Locator("button.toggle").ClickAsync();

    [Then("the draft title is still {string}")]
    public async Task ThenDraftTitleStill(string value) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('input.draft-title')?.value === {JsString(value)}");

    // ── reactive components in a list: per-row slot identity (milestone 11, slice 2) ──

    [Given("the row-component list app is running")]
    public async Task GivenRowComponentListAppRunning()
    {
        ctx.Description = InstanceContext.RowComponentListDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    [When("I type {string} into the scratch of the row titled {string}")]
    public async Task WhenTypeScratchOfRow(string value, string title) =>
        await ctx.Page!.Locator(".note-row").Filter(new() { HasText = title })
            .Locator("input.scratch").FillAsync(value);

    // Locate the row by its title text (robust across a reorder, which moves rows in the DOM).
    [Then("the scratch of the row titled {string} is {string}")]
    public async Task ThenScratchOfRowIs(string title, string value) =>
        await ctx.Page!.WaitForFunctionAsync(
            "() => { const r = [...document.querySelectorAll('.note-row')]" +
            $".find(e => e.querySelector('.row-title')?.textContent.includes({JsString(title)}));" +
            $" return r?.querySelector('input.scratch')?.value === {JsString(value)}; }}");

    [When("I reorder the rows")]
    public async Task WhenReorderRows() =>
        await ctx.Page!.Locator("button.reorder").ClickAsync();

    [Then("the first row is titled {string}")]
    public async Task ThenFirstRowTitled(string title) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.note-row .row-title')?.textContent.includes({JsString(title)})");

    // ── explicit per-call key: opt-in reset (milestone 11, slice 3) ─────────────────

    [Given("the keyed component app is running")]
    public async Task GivenKeyedComponentAppRunning()
    {
        ctx.Description = InstanceContext.KeyedComponentDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    [When("I type {string} into the box scratch")]
    public async Task WhenTypeBoxScratch(string value) =>
        await ctx.Page!.Locator(".box input.scratch").FillAsync(value);

    [Then("the box scratch is {string}")]
    public async Task ThenBoxScratchIs(string value) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.box input.scratch')?.value === {JsString(value)}");

    // Flips the key bound to the component, which must reset it (new slot identity → fresh state).
    [When("I rekey the component")]
    public async Task WhenRekeyComponent() =>
        await ctx.Page!.Locator("button.rekey").ClickAsync();

    // ── references (slice 2) ───────────────────────────────────────────────────────

    [Given("the self-hosted reference app is running")]
    public async Task GivenSelfHostedRefAppRunning()
    {
        ctx.Description = InstanceContext.SelfHostedRefDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // Candidates are the options of the `select.ref-pick` dropdown (the picker scales past a
    // handful of objects, unlike the old button-per-candidate list).
    [Then("a reference candidate {string} is offered")]
    public async Task ThenCandidateOffered(string label) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('select.ref-pick option')].some(e => e.textContent.trim() === {JsString(label)})");

    // Pick = choose the candidate in the dropdown, then commit with Set (applyPick → sys.setRef).
    [When("I pick the reference candidate {string}")]
    public async Task WhenPickCandidate(string label)
    {
        await ctx.Page!.Locator("select.ref-pick").First.SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Label = label });
        await ctx.Page.Locator("button.ref-set").First.ClickAsync();
    }

    [When("I clear the reference")]
    public async Task WhenClearReference() =>
        await ctx.Page!.Locator("button.ref-clear").First.ClickAsync();

    // The set/dict create form AND the reference create-new form are now both flag-gated (revealed by
    // `+ New` — the B1 collapse made RefEditor's create a nested create-mode ObjectForm behind the same
    // toggle as SetTable). They class their inputs by prop name. Reveal the create form first
    // (idempotent), then fill its labeled .create-form field.
    [When("I fill the new {string} with {string}")]
    public async Task WhenFillNewField(string field, string value)
    {
        // Reveal the gated create form when one is present (a .new-btn) — true for set/dict AND ref now.
        if (await ctx.Page!.Locator(".new-btn").CountAsync() > 0)
            await ctx.Page!.RevealCreateFormAsync();
        var input = ctx.Page!.Locator($".create-form input.{field}").First;
        await input.FillAsync(value);
        // FillAsync sets .value and fires `input` for a text input, but NOT reliably for an
        // <input type="date"> — so the two-way binding's `oninput` (which writes the draft) can miss
        // a date fill. Dispatch a native `input` explicitly so the draft updates as it does for a real
        // user's keystroke (idempotent for a text input: the binding just re-reads the same value).
        await input.DispatchEventAsync("input");
    }

    // ── dictionaries ───────────────────────────────────────────────────────────────

    [When("I fill the new key with {string}")]
    public async Task WhenFillNewKey(string key)
    {
        await ctx.Page!.RevealCreateFormAsync();
        await ctx.Page!.Locator(".create-form input.dict-key").First.FillAsync(key);
    }

    // The dict create form's Save button (commits the entry) keeps the .dict-add class (primary look).
    [When("I add the dict entry")]
    public async Task WhenAddDictEntry() =>
        await ctx.Page!.Locator("button.dict-add").First.ClickAsync();

    [When("a dict row eventually shows {string}")]
    [Then("a dict row shows {string}")]
    [Then("a dict row eventually shows {string}")]
    public async Task ThenDictRowShows(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.dict-row')].some(e => e.textContent.includes({JsString(text)}))");

    [When("I remove the dict row {string}")]
    public async Task WhenRemoveDictRow(string key) =>
        await ctx.Page!.Locator(".dict-row", new() { HasTextString = key })
            .Locator("button.dict-remove").First.ClickAsync();

    [Then("no dict row eventually shows {string}")]
    public async Task ThenNoDictRow(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => ![...document.querySelectorAll('.dict-row')].some(e => e.textContent.includes({JsString(text)}))");

    // ponytail: was `button.ref-create`; RefEditor's create-new form is now a nested create-mode
    // ObjectForm (B1 collapse), revealed by the same `+ New` toggle as SetTable; its Save button is the
    // join-agnostic `.create-save` (shared by every create-mode ObjectForm — set OR ref). The prior
    // fill step reveals the form via .new-btn, so the button is present by the time we click it.
    [When("I create the new object")]
    public async Task WhenCreateNewObject() =>
        await ctx.Page!.Locator("button.create-save").First.ClickAsync();

    // The set create form's Save button (commits the new member) is the create-mode ObjectForm's
    // join-agnostic .create-save (primary green look).
    [When("I add to the set")]
    public async Task WhenAddToSet() =>
        await ctx.Page!.Locator("button.create-save").First.ClickAsync();

    [Then("a set row shows {string}")]
    [Then("a set row eventually shows {string}")]
    public async Task ThenSetRowShows(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.set-row')].some(e => e.textContent.includes({JsString(text)}))");

    // The zero-member empty-state line a set/dict table renders under its header (.set-empty / .dict-empty),
    // instead of a bare header that reads as broken. WaitForFunction so SSR→hydrate settles to the text.
    [Then("the empty state reads {string}")]
    public async Task ThenEmptyStateReads(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.set-empty, .dict-empty')?.textContent.includes({JsString(text)})");

    [Then("the current reference is {string}")]
    public async Task ThenCurrentReference(string label) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.ref-current')?.textContent.includes({JsString(label)})");

    // Reading a set single-reference route follows the reference and returns the target
    // object (ReadNode resolves it), so a points-at check is a text field on that object.
    [Then("the {string} reference eventually points at {string}")]
    public async Task ThenReferencePointsAt(string path, string label) =>
        await EventuallyAsync(() =>
        {
            var segs = path.Trim('/').Split('/', System.StringSplitOptions.RemoveEmptyEntries);
            return ctx.Store!.ReadNode(NodePath.FromSegments(segs)) is ObjectValue ov
                && ov.Fields.Values.OfType<TextValue>().Any(t => t.Text == label);
        });

    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static async Task EventuallyAsync(System.Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = System.DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (System.DateTime.UtcNow < deadline)
        {
            try { if (condition()) return; }
            catch (System.IO.IOException) { /* store mid-write — retry */ }
            await Task.Delay(50);
        }
        bool final;
        try { final = condition(); } catch (System.IO.IOException) { final = false; }
        await Assert.That(final).IsTrue();
    }
}
