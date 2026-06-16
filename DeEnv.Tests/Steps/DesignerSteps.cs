using DeEnv.Kernel;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for Designer.feature — the operator IDE (the REAL DeEnv/instances/4/app.app), a URL-routed
// multi-instance designer driven end-to-end through a real browser. Unlike the rest of the
// browser-driven suite it runs against a REAL KernelHost (InstanceContext.StartKernelDesignerBrowserAsync):
// the IDE renders `sys.instances` (the kernel's hosted set), which is empty under the kernel-less
// TestInstanceServer, so the designer can only be exercised against a live kernel. The kernel hosts the
// designer (id 4) plus the named target instances; the browser is pointed at the designer's app port.
//
// The IDE's surfaces are SEPARATE: `/designs` (the design library) + `/designs/<designId>` (the design
// EDITOR — type editor + code areas, no publish), and `/instances` (instances + their current design)
// + `/instances/<id>` (a design SELECTOR — a <select> dropdown + Apply). An instance references its
// design by an EXPLICIT `designId` (seeded by the fixture so the dropdowns start correct).
//
// Cross-route navigation is a fresh-SSR <a href> per route (the no-load model — each route's page needs
// different store data, so a server render from the store is exactly right), not a client-side path
// reassignment; the steps click the links and let the browser navigate.
[Binding]
public sealed class DesignerSteps(InstanceContext ctx)
{
    // The kernel-hosted designer instance (id 4); its targets are reached via ctx.Kernel.Instances by
    // their registry label (Spec.App).
    private HostedInstance _designer = null!;

    // ── Given ───────────────────────────────────────────────────────────────────

    [Given("the operator IDE is running on a kernel hosting instances {string} and {string}")]
    public async Task GivenIdeRunning(string firstLabel, string secondLabel)
    {
        // Boot a kernel hosting the real designer (id 4) + two target instances labelled to match the
        // designer's two seeded designs (the fixture seeds each target's designId to the matching
        // design), then point the browser at the designer's app port.
        _designer = await ctx.StartKernelDesignerBrowserAsync((5, firstLabel), (6, secondLabel));
    }

    // ── When: navigation ─────────────────────────────────────────────────────────

    [When("I open the designs list")]
    public async Task WhenOpenDesignsList()
    {
        await ctx.Page!.GotoAsync("/designs");
        await ctx.Page.WaitForSelectorAsync("main.ide-designs .design-row");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I open the instances list")]
    public async Task WhenOpenList()
    {
        await ctx.Page!.GotoAsync("/instances");
        // Hydration checkpoint: the SSR instance rows are present AND the client bundle has bootstrapped
        // (window.initUi set), so the hand-rolled links/handlers are attached before we interact.
        await ctx.Page.WaitForSelectorAsync("main.ide-list .instance-row");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I edit the design {string}")]
    public async Task WhenEditDesign(string label)
    {
        // The Edit link is a fresh-SSR <a href="/designs/<designId>"> on the matching design row; clicking
        // it navigates the browser, so the editor page is a full server render with the design's data.
        await DesignRowFor(label).Locator("a.edit-design").ClickAsync();
        await ctx.Page!.WaitForSelectorAsync("main.ide-design-edit .design-editor .type-name");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I open the instance {string}")]
    public async Task WhenOpenInstance(string label)
    {
        // The Open link is a fresh-SSR <a href="/instances/<id>"> on the matching instance row. Reaching
        // the instance page can start from the instances list OR directly (after editing a design) — go
        // to the list first so the row's Open link is present, then click it.
        if (await ctx.Page!.Locator($".instance-row").CountAsync() == 0)
            await WhenOpenList();
        await RowFor(label).Locator("a.open-instance").ClickAsync();
        await ctx.Page!.WaitForSelectorAsync("main.ide-instance select.design-pick");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    // ── When: editing a design (on /designs/<id>) ────────────────────────────────

    [When("I rename the type {string} to {string}")]
    public async Task WhenRename(string from, string to)
    {
        await TypeNameInput(from).FillAsync(to);
        // The bound input re-renders the model name to the new value (the client edit landed)…
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.type-row input.type-name')].some(e => e.value === {JsString(to)})");
        // …then wait for the autosave (objectPropChange) to reach the designer's sovereign store, so a
        // later apply projects the renamed design (the apply reads the store fresh).
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaType").Values
            .Any(o => o.Fields.TryGetValue("name", out var v)
                && v is DeEnv.Storage.TextValue t && t.Text == to));
    }

    [When("I retype the prop {string} to {string}")]
    public async Task WhenRetypeProp(string propName, string newType)
    {
        // Renaming a referenced type requires retyping the prop that points at it, so the projected app
        // stays valid (a prop whose `type` names a missing type is rejected at deploy). The prop's type
        // input is the `.prop-type` in the `.prop-row` whose `.prop-name` currently holds `propName`.
        await PropTypeInput(propName).FillAsync(newType);
        // The bound input re-renders the new value (the client edit landed)…
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.prop-row input.prop-type')].some(e => e.value === {JsString(newType)})");
        // …then wait for the autosave to reach the designer's store, so a later apply projects the
        // retyped prop (the apply reads the store fresh).
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values
            .Any(o => o.Fields.TryGetValue("type", out var v)
                && v is DeEnv.Storage.TextValue t && t.Text == newType));
    }

    // ── When: the instance selector (on /instances/<id>) ─────────────────────────

    [When("I pick the design {string} in the dropdown")]
    public async Task WhenPickDesign(string designLabel)
    {
        // Pick the option whose visible label is the design's name (its value is the design id). The
        // onchange binding writes the picked id back to the selector's state and re-renders, so the Apply
        // button below resolves to the newly-picked design.
        await ctx.Page!.Locator("select.design-pick").SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Label = designLabel });
        // The selection lands (the bound state reflects the new pick) before we apply.
        await ctx.Page.WaitForFunctionAsync(
            $"() => {{ const s = document.querySelector('select.design-pick'); return s != null && s.options[s.selectedIndex] != null && s.options[s.selectedIndex].textContent.trim() === {JsString(designLabel)}; }}");
    }

    [When("I apply the design")]
    public async Task WhenApply() =>
        await ctx.Page!.Locator("button.apply-design").ClickAsync();

    // ── Then ────────────────────────────────────────────────────────────────────

    [Then("the designs list shows a design {string}")]
    public async Task ThenDesignsListShows(string label) =>
        await Assert.That(await ctx.Page!.Locator($".design-row .design-label:text-is({CssString(label)})").CountAsync())
            .IsGreaterThanOrEqualTo(1);

    [Then("the design editor shows a type named {string}")]
    public async Task ThenEditorShowsType(string name) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.design-editor .type-row input.type-name')].some(e => e.value === {JsString(name)})");

    [Then("the design editor shows the design's UI text in a textarea")]
    public async Task ThenEditorShowsUiText() =>
        // The design's `ui` section text is bound into the code-area <textarea> (a real multi-line
        // editor); the seeded design's UI is a custom `fn render()`, so its text contains "fn render".
        await ctx.Page!.WaitForFunctionAsync(
            "() => { const e = document.querySelector('.design-editor textarea.design-ui'); return e != null && e.value.includes('fn render'); }");

    [Then("the instances list shows the instance {string} running design {string}")]
    public async Task ThenListShows(string label, string designLabel)
    {
        var row = RowFor(label);
        await Assert.That(await row.CountAsync()).IsEqualTo(1);
        // The current design's label is rendered inside the same row, resolved by the explicit designId
        // reference (a row whose designId matches no design shows no .design-label).
        await Assert.That(await row.Locator($".design-label:text-is({CssString(designLabel)})").CountAsync())
            .IsGreaterThanOrEqualTo(1);
    }

    [Then("the design dropdown has the design {string} selected")]
    public async Task ThenDropdownSelected(string designLabel) =>
        // The <select>'s pre-selected option (driven by the instance's designId through the <select>
        // binding) is the instance's current design — its visible text is the design's label.
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const s = document.querySelector('select.design-pick'); return s != null && s.options[s.selectedIndex] != null && s.options[s.selectedIndex].textContent.trim() === {JsString(designLabel)}; }}");

    [Then("the instance {string} records the design {string}")]
    public async Task ThenInstanceRecordsDesign(string instanceLabel, string designLabel)
    {
        // Apply (setDesign) records the chosen design on the target's registry entry — the kernel updates
        // the live spec's DesignId (and rewrites kernel.json). Poll the live spec (the WS host-action is
        // async) until it carries the picked design's id.
        var designId = ctx.DesignIdForLabel(designLabel);
        await EventuallyAsync(() =>
            ctx.Kernel!.Instances.Single(i => i.Spec.App == instanceLabel).Spec.DesignId == designId);
    }

    [Then("the {string} instance's app document describes the type {string}")]
    public async Task ThenTargetDescribesType(string label, string typeName)
    {
        // Apply also deployed: it wrote the projected app document onto the target instance's app doc (its
        // own sovereign id-dir). Poll it (the WS host-action + file write is async) until the type appears.
        // The deploy projects the WHOLE app and resets the target store, so it can run long at the tail of
        // a saturated full suite — a wider window than the 8s default keeps it deterministic.
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await EventuallyAsync(() => File.Exists(target.Spec.SchemaPath)
            && File.ReadAllText(target.Spec.SchemaPath).Contains(typeName), timeoutMs: 15000);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    // The instances-list row for an instance, located by its app-name cell (exact match, so "instance"
    // never matches "designer"/"crm").
    private Microsoft.Playwright.ILocator RowFor(string label) =>
        ctx.Page!.Locator($".instance-row:has(.instance-app:text-is({CssString(label)}))");

    // The designs-list row for a design, located by its label cell (exact match).
    private Microsoft.Playwright.ILocator DesignRowFor(string label) =>
        ctx.Page!.Locator($".design-row:has(.design-label:text-is({CssString(label)}))");

    // A design-editor type-name input currently holding `name` (the type being renamed).
    private Microsoft.Playwright.ILocator TypeNameInput(string name) =>
        ctx.Page!.Locator($".design-editor .type-row input.type-name[value={CssString(name)}]");

    // The prop-type input of the `.prop-row` whose `.prop-name` currently holds `propName` (the prop
    // being retyped). Scoped to that row so it targets the right prop across all the types' prop rows.
    private Microsoft.Playwright.ILocator PropTypeInput(string propName) =>
        ctx.Page!.Locator($".design-editor .prop-row:has(input.prop-name[value={CssString(propName)}]) input.prop-type");

    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    // A double-quoted CSS/Playwright string argument with quotes/backslashes escaped.
    private static string CssString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // Polls a condition (a WS round-trip / file write is async). An IOException is the test thread
    // reading a store/app file mid-write — transient, retried. Mirrors TodoSteps.EventuallyAsync.
    private static async Task EventuallyAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return; }
            catch (IOException) { /* file mid-write — retry */ }
            await Task.Delay(50);
        }
        bool final;
        try { final = condition(); } catch (IOException) { final = false; }
        await Assert.That(final).IsTrue();
    }
}
