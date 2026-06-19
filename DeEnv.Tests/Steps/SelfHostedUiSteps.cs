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

    // The committed shop (instances/4) — the fully-auto generic UI over a customer set. Loaded
    // from its real id-dir document so the staged-edit scenarios drive the single source of truth.
    [Given("the shop app is running")]
    public async Task GivenShopAppRunning()
    {
        ctx.Description = InstanceDescriptionLoader.LoadFile(InstanceContext.AppFixture(4));
        await ctx.EnsureServerAndBrowserAsync();
    }

    // ── staged edits + Save/Discard (milestone 11) ──────────────────────────────────
    // The generic ObjectForm now stages scalar edits in a local draft and commits them on Save
    // (autosave OFF by default). These drive that form-level flow.

    // Commit the staged scalar edits: the ObjectForm's Save button (.object-form button.save) writes
    // the draft's scalars back onto the live object via sys.setFields (id-addressed objectPropChange).
    [When("I save the form")]
    public async Task WhenSaveTheForm()
    {
        await ctx.Page!.WaitHydratedAsync();
        await ctx.Page!.Locator(".object-form button.save").First.ClickAsync();
    }

    // Discard the staged edits: the Discard button copies the live object's scalars back ONTO the
    // draft in place (sys.setFields(state.draft, obj)), so the bound inputs re-render to the stored
    // values (the draft keeps its identity, so the slot-keyed Fields re-read it).
    [When("I discard the form")]
    public async Task WhenDiscardTheForm()
    {
        await ctx.Page!.WaitHydratedAsync();
        await ctx.Page!.Locator(".object-form button.discard").First.ClickAsync();
    }

    // The autosave-mode ObjectForm (autosave={true}) shows NO Save button — edits persist live.
    [Then("the form has no Save button")]
    public async Task ThenNoSaveButton() =>
        await Assert.That(await ctx.Page!.Locator(".object-form button.save").CountAsync()).IsEqualTo(0);

    // A staged edit must NOT reach the store: the named type's stored object STILL holds the
    // original value. A direct read (not "eventually") — a staged edit fires no WS op, so the store
    // cannot have changed by the time the awaited fill/discard completed.
    [Then("the store still has a {string} whose {string} is {string}")]
    public async Task ThenStoreStillHas(string typeName, string field, string expected) =>
        await Assert.That(ctx.Store!.ReadExtent(typeName).Values
            .Any(o => o.Fields.TryGetValue(field, out var v) && v is TextValue t && t.Text == expected)).IsTrue();

    // objectForm gives each field input (and its label) the class of its prop name
    // (class={p.name}).
    [When("I fill the {string} field with {string}")]
    public async Task WhenFillField(string field, string value)
    {
        await ctx.Page!.WaitHydratedAsync(); // the bound input's handler must be attached before we type
        await ctx.Page!.Locator($"input.{field}").FillAsync(value);
    }

    // Clear a field to empty. FillAsync("") sets .value and fires `input` for a text input but NOT
    // reliably for an <input type="date">, so the staged draft's `oninput` can miss the clear — and
    // Save would then replay the OLD value. Dispatch a native `input` explicitly so the draft records
    // the empty value as it does for a real keystroke (idempotent for a text input).
    [When("I clear the {string} field")]
    public async Task WhenClearField(string field)
    {
        await ctx.Page!.WaitHydratedAsync();
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

    // The exact pathname after a navigation lands (the row link pushed history) — stricter than the
    // substring "the URL is", so a wrong navigation to /notes/2 can't satisfy a "/notes" assertion.
    [Then("the URL path becomes {string}")]
    public async Task ThenUrlPathBecomes(string expected) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => new URL(location.href).pathname === {JsString(expected)}");

    // The pathname is STILL exactly this (a handled Remove must not have navigated). The preceding
    // step already settled the removal, so this reads the now-stable URL.
    [Then("the URL path is still {string}")]
    public async Task ThenUrlPathStill(string expected)
    {
        var pathname = new Uri(ctx.Page!.Url).AbsolutePath;
        await Assert.That(pathname).IsEqualTo(expected);
    }

    // ── flag-gated create view (milestone 11) ───────────────────────────────────────
    // The always-visible inline add row (.set-new/.dict-new) is replaced by a `+ New` button that
    // reveals a labeled create form (.create-form), swapping out the table; Save commits + returns to
    // the table, Cancel discards. These steps drive that swap directly (the (a)-(e) coverage).

    // A selector is ABSENT (the inline add form is gone; the table is replaced while creating; the
    // create form is gone after Save/Cancel). WaitForFunction so a still-reconciling DOM settles.
    [Then("the page does not show {string}")]
    public async Task ThenPageDoesNotShow(string selector) =>
        await ctx.Page!.WaitForFunctionAsync($"() => document.querySelector({JsString(selector)}) === null");

    // Click the `+ New` button to reveal the create form (the table → create-form swap). Hydration
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

    // A create-form field is LABELED: a <label> with the prop's class sits in the form (the same
    // label+Input composite the edit/object page uses), proving the form is the labeled edit form, not
    // a row of bare inputs.
    [Then("the create form has a labeled {string} field")]
    public async Task ThenCreateFormLabeledField(string field) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.create-form label.{field}') !== null " +
            $"&& document.querySelector('.create-form input.{field}, .create-form select.{field}') !== null");

    // Milestone 11: a hand-written `fn render()` that composes the PUBLIC <ObjectForm> library
    // component — proving the generic-UI library is reachable + usable from userspace.
    [Given("the public-library form app is running")]
    public async Task GivenPublicLibraryFormAppRunning()
    {
        ctx.Description = InstanceContext.PublicLibraryFormDb();
        await ctx.EnsureServerAndBrowserAsync();
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
    // scoped to the section). Replays the draft's scalars onto the live object via objectPropChange.
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

    // The object of `typeName` whose `title` matches (the test data has unique titles), eventually.
    private async Task<ObjectValue> ReminderTitledAsync(string typeName, string title)
    {
        ObjectValue? found = null;
        await EventuallyAsync(() =>
        {
            found = ctx.Store!.ReadExtent(typeName).Values
                .FirstOrDefault(o => o.Fields.TryGetValue("title", out var v) && v is TextValue t && t.Text == title);
            return found != null;
        });
        return found!;
    }

    // An optional date/decimal/datetime left empty means UNSET: it round-trips as the empty leaf
    // (TextValue "") — the server must not force-parse "". A never-set seed field is absent; an
    // explicitly-emptied field is the empty text leaf. Both read as unset.
    [Then("the store has a {string} titled {string} whose {string} is unset")]
    public async Task ThenStoreFieldUnset(string typeName, string title, string field)
    {
        var obj = await ReminderTitledAsync(typeName, title);
        var unset = !obj.Fields.TryGetValue(field, out var v) || v is TextValue { Text: "" };
        await Assert.That(unset).IsTrue();
    }

    // A non-empty optional leaf still parses + persists. The Code runtime projects a date/decimal
    // to its text form (DbBridge.ScalarToExec), so the stored value reads back as that text.
    [Then("the store has a {string} titled {string} whose {string} is {string}")]
    public async Task ThenStoreFieldText(string typeName, string title, string field, string expected)
    {
        var obj = await ReminderTitledAsync(typeName, title);
        var actual = obj.Fields.TryGetValue(field, out var v) ? StoredLeafText(v) : null;
        await Assert.That(actual).IsEqualTo(expected);
    }

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

    // The set/dict create form (flag-gated: revealed by `+ New`) and the reference create-new form
    // class their inputs by prop name. For a set/dict, reveal the create form first (idempotent), then
    // fill its labeled .create-form field; the reference editor keeps its always-visible .ref-new form.
    [When("I fill the new {string} with {string}")]
    public async Task WhenFillNewField(string field, string value)
    {
        // A reference route/field has a .ref-new form with no `+ New`; a set/dict route has the gated
        // create form. Reveal the create form when one is gated (a .new-btn is present).
        if (await ctx.Page!.Locator(".new-btn").CountAsync() > 0)
            await ctx.Page!.RevealCreateFormAsync();
        var input = ctx.Page!.Locator($".create-form input.{field}, .ref-new input.{field}").First;
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

    [When("I create the new object")]
    public async Task WhenCreateNewObject() =>
        await ctx.Page!.Locator("button.ref-create").First.ClickAsync();

    // The set create form's Save button (commits the new member) keeps the .set-add class (primary look).
    [When("I add to the set")]
    public async Task WhenAddToSet() =>
        await ctx.Page!.Locator("button.set-add").First.ClickAsync();

    [Then("a set row shows {string}")]
    [Then("a set row eventually shows {string}")]
    public async Task ThenSetRowShows(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.set-row')].some(e => e.textContent.includes({JsString(text)}))");

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
