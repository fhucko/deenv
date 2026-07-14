using DeEnv.Kernel;
using DeEnv.Tests.TestSupport;
using DeEnv.Instance;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for Designer.feature — SPLIT COMPLETE (see docs/plans/2026-07-14-designer-tests-relevance-and-split.md)
// Categorized into multiple files using partial class to keep ~3k lines manageable.
// Core + shared in this file. Logic distributed by area.

[Binding]
public sealed partial class DesignerSteps(InstanceContext ctx)
{
    private const string DesignerAdminName = "admin";
    private const string DesignerAdminPassword = "hunter2";

    private HostedInstance _designer = null!;
    private string _newInstanceName = "";
    private string _urlBeforeClick = "";
    private int _lastCreatedInstanceId;
    private string _justAddedTypeName = "";
    private int _todoTargetLogLinesAfterStaleness;

    // ── Given ───────────────────────────────────────────────────────────────────

    [Given("the operator IDE is running on a kernel hosting instances {string} and {string}")]
    public async Task GivenIdeRunning(string firstLabel, string secondLabel)
    {
        _designer = await ctx.StartKernelDesignerBrowserAsync((5, firstLabel), (6, secondLabel));
        SeedDesignerAdmin();
        await LoginDesignerAdminAsync();
        ctx.Page!.SetDefaultTimeout(TestTimeouts.ActionMs);
    }

    [Given("the anonymous operator IDE is running on a kernel hosting instances {string} and {string}")]
    public async Task GivenAnonymousIdeRunning(string firstLabel, string secondLabel)
    {
        _designer = await ctx.StartKernelDesignerBrowserAsync((5, firstLabel), (6, secondLabel));
        SeedDesignerAdmin();
        ctx.Page!.SetDefaultTimeout(TestTimeouts.ActionMs);
    }

    // All other [Given/When/Then] step defs are in the categorized partial files:
    // DesignerLibraryNavigationSteps.cs, DesignerTypePropEditorSteps.cs,
    // DesignerTreeCanvasSteps.cs, DesignerComponentsLiveSteps.cs
    // (and commit/publish in future).

    // ── helpers (shared) ─────────────────────────────────────────────────────────────────

    private void SeedDesignerAdmin()
    {
        var desc = InstanceDescriptionLoader.LoadFile(_designer.Spec.SchemaPath);
        AdminSeed.Seed(_designer.Store, desc, DesignerAdminName, DesignerAdminPassword, "Admin");
    }

    private async Task LoginDesignerAdminAsync()
    {
        var page = ctx.Page ?? throw new InvalidOperationException("Designer browser was not started.");
        await page.GotoReadyAsync(ctx.DesignerUrl("/designs"));
        await page.WaitReadyAsync();
        await page.Locator(".login-form input.name").FillAsync(DesignerAdminName);
        await page.Locator(".login-form input.password").FillAsync(DesignerAdminPassword);
        await page.Locator(".login-form button.login-submit").ClickAsync();
        await page.Locator("main.ide-designs .set-row").First.WaitForAsync();
    }

    private Microsoft.Playwright.ILocator RowFor(string label) =>
        ctx.Page!.Locator($"main.ide-list .set-row:has(a.row-link:text-is({CssString(label)}))");

    private Microsoft.Playwright.ILocator DesignRowFor(string label) =>
        ctx.Page!.Locator($".set-row:has(a.row-link:text-is({CssString(label)}))");

    private Microsoft.Playwright.ILocator TypeNameInput(string name) =>
        ctx.Page!.Locator($"main.ide-design-edit .design-editor .type-card input.type-name[value={CssString(name)}]");

    private Microsoft.Playwright.ILocator JustAddedTypeRow() =>
        ctx.Page!.Locator($"main.ide-design-edit .design-editor .type-card:has(input.type-name[value={CssString(_justAddedTypeName)}])");

    private Microsoft.Playwright.ILocator PropTypeSelect(string propName) =>
        ctx.Page!.Locator($"main.ide-design-edit .design-editor .prop-row:has(input.prop-name[value={CssString(propName)}]) select.prop-type");

    private Microsoft.Playwright.ILocator PropCardinalitySelect(string propName) =>
        ctx.Page!.Locator($"main.ide-design-edit .design-editor .prop-row:has(input.prop-name[value={CssString(propName)}]) select.prop-cardinality");

    private Microsoft.Playwright.ILocator PropKeytypeInput(string propName) =>
        ctx.Page!.Locator($"main.ide-design-edit .design-editor .prop-row:has(input.prop-name[value={CssString(propName)}]) input.prop-keytype");

    private Microsoft.Playwright.ILocator PropMultilineInput(string propName) =>
        ctx.Page!.Locator($"main.ide-design-edit .design-editor .prop-row:has(input.prop-name[value={CssString(propName)}]) input.prop-multiline");

    private static readonly Microsoft.Playwright.LocatorWaitForOptions Hidden =
        new() { State = Microsoft.Playwright.WaitForSelectorState.Hidden };

    private static Task EventuallyAsync(
        Func<bool> condition,
        int timeoutMs = TestTimeouts.ActionMs,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string what = "")
        => Polling.EventuallyAsync(condition, what, timeoutMs);

    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static string CssString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // Supporting privates needed by partials
    // Supporting methods like CreateDesignViaGenericNew and constants are in the steps partial.
}