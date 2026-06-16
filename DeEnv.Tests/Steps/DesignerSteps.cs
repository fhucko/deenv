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
        // designer's two seeded designs, then point the browser at the designer's app port.
        _designer = await ctx.StartKernelDesignerBrowserAsync((5, firstLabel), (6, secondLabel));
    }

    // ── When ────────────────────────────────────────────────────────────────────

    [When("I open the instances list")]
    public async Task WhenOpenList()
    {
        await ctx.Page!.GotoAsync("/instances");
        // Hydration checkpoint: the SSR instance rows are present AND the client bundle has bootstrapped
        // (window.initUi set), so the hand-rolled links/handlers are attached before we interact.
        await ctx.Page.WaitForSelectorAsync("main.ide-list .instance-row");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I edit the instance {string}")]
    public async Task WhenEdit(string label)
    {
        // The Edit link is a fresh-SSR <a href="/instances/<id>"> on the matching instance row; clicking
        // it navigates the browser, so the edit page is a full server render with the design's data.
        await RowFor(label).Locator("a.edit-instance").ClickAsync();
        await ctx.Page!.WaitForSelectorAsync("main.ide-edit .design-editor .type-name");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I rename the type {string} to {string}")]
    public async Task WhenRename(string from, string to)
    {
        await TypeNameInput(from).FillAsync(to);
        // The bound input re-renders the model name to the new value (the client edit landed)…
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.type-row input.type-name')].some(e => e.value === {JsString(to)})");
        // …then wait for the autosave (objectPropChange) to reach the designer's sovereign store, so the
        // subsequent publish projects the renamed design (the publish reads the store fresh).
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaType").Values
            .Any(o => o.Fields.TryGetValue("name", out var v)
                && v is DeEnv.Storage.TextValue t && t.Text == to));
    }

    [When("I publish the design")]
    public async Task WhenPublish() =>
        await ctx.Page!.Locator("button.publish-design").ClickAsync();

    // ── Then ────────────────────────────────────────────────────────────────────

    [Then("the list shows the instance {string} with design {string}")]
    public async Task ThenListShows(string label, string designLabel)
    {
        var row = RowFor(label);
        await Assert.That(await row.CountAsync()).IsEqualTo(1);
        // The matched design's label is rendered inside the same row (a row with no matched design shows
        // no .design-label), so this asserts the label-matching resolved.
        await Assert.That(await row.Locator($".design-label:text-is({CssString(designLabel)})").CountAsync())
            .IsGreaterThanOrEqualTo(1);
    }

    [Then("the design editor shows a type named {string}")]
    public async Task ThenEditorShowsType(string name) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.design-editor .type-row input.type-name')].some(e => e.value === {JsString(name)})");

    [Then("the design editor shows the design's UI text in a textarea")]
    public async Task ThenEditorShowsUiText() =>
        // The design's `ui` section text is bound into the code-area <textarea> (a real multi-line
        // editor); the seeded design's UI is a custom `fn render()`, so its text contains "fn render".
        // A non-empty textarea carrying it proves the whole-app design (not just types) is loaded into
        // the editor AND that the textarea's value (its text content) round-trips through SSR + hydration.
        await ctx.Page!.WaitForFunctionAsync(
            "() => { const e = document.querySelector('.design-editor textarea.design-ui'); return e != null && e.value.includes('fn render'); }");

    [When("I replace the design's UI with a render returning {string}")]
    public async Task WhenSetUiText(string marker)
    {
        // Build a complete, VALID multi-line `ui` section (keyword + indentation, exactly the verbatim
        // form the text field carries) whose render returns the marker text — multi-line, so it exercises
        // a real textarea, and valid, so the publish's whole-document validation accepts it. \n are real
        // newlines: FillAsync types them as line breaks into the textarea.
        var ui = $"ui\n    fn render()\n        return <main>\n            \"{marker}\"\n";
        // FillAsync clears + types into the <textarea> and fires the input event the binding listens for,
        // so the bound `ui` field updates and re-renders through the same path a user would drive.
        var area = ctx.Page!.Locator(".design-editor textarea.design-ui");
        await area.FillAsync(ui);
        // The bound textarea reflects the new multi-line value (the client edit landed — proving caret
        // stability didn't drop characters and the value/oninput wiring round-tripped). The expected
        // value is passed as a function ARGUMENT (Playwright serialises it safely), so its newlines need
        // no JS-string escaping.
        await ctx.Page.WaitForFunctionAsync(
            "expected => { const e = document.querySelector('.design-editor textarea.design-ui'); return e != null && e.value === expected; }",
            ui);
        // …then wait for the autosave (objectPropChange) to reach the designer's sovereign store, so the
        // subsequent publish projects the edited design (the publish reads the store fresh).
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values
            .Any(o => o.Fields.TryGetValue("ui", out var v)
                && v is DeEnv.Storage.TextValue t && t.Text == ui));
    }

    [Then("the {string} instance's app document renders {string}")]
    public async Task ThenTargetContainsUi(string label, string marker)
    {
        // The publish wrote the projected app document (including the edited, validated `ui` section)
        // onto the target instance's sovereign app doc; poll it (the WS host-action + file write is
        // async) until the new render's marker text appears in the document.
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await EventuallyAsync(() => File.Exists(target.Spec.SchemaPath)
            && File.ReadAllText(target.Spec.SchemaPath).Contains($"\"{marker}\""));
    }

    [Then("the {string} instance's app document describes the type {string}")]
    public async Task ThenTargetDescribesType(string label, string typeName)
    {
        // The publish wrote the projected app document onto the target instance's app doc (its own
        // sovereign id-dir). Poll it (the WS host-action + file write is async) until the renamed type
        // appears — the same on-disk app-doc assertion the HostAction publish scenario makes.
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await EventuallyAsync(() => File.Exists(target.Spec.SchemaPath)
            && File.ReadAllText(target.Spec.SchemaPath).Contains(typeName));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    // The list row for an instance, located by its app-name cell (exact match, so "instance" never
    // matches "designer"/"crm").
    private Microsoft.Playwright.ILocator RowFor(string label) =>
        ctx.Page!.Locator($".instance-row:has(.instance-app:text-is({CssString(label)}))");

    // A design-editor type-name input currently holding `name` (the type being renamed).
    private Microsoft.Playwright.ILocator TypeNameInput(string name) =>
        ctx.Page!.Locator($".design-editor .type-row input.type-name[value={CssString(name)}]");

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
