using DeEnv.Kernel;
using DeEnv.Tests.TestSupport;
using DeEnv.Instance;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Split: core in this file, step methods in DesignerSteps.Steps.cs
// Categorized markers in other Designer*Steps.cs

[Binding]
public sealed partial class DesignerSteps(InstanceContext ctx)
{
    private const string DesignerAdminName = "admin";
    private const string DesignerAdminPassword = "hunter2";

    private HostedInstance _designer = null!;
    private string _newInstanceName = "";
    private string _urlBeforeClick = "";
    private float _scrollYBefore;
    private int _lastCreatedInstanceId;
    private string _justAddedTypeName = "";
    private int _todoTargetLogLinesAfterStaleness;

    [Given("the operator IDE is running on a kernel hosting instances {string} and {string}")]
    public async Task GivenIdeRunning(string firstLabel, string secondLabel)
    {
        // Kernel boot + first navigation + login can sit at the multi-hop ceiling; scenario body uses
        // DesignerActionMs for locator actions (see TestTimeouts).
        _designer = await ctx.StartKernelDesignerBrowserAsync((5, firstLabel), (6, secondLabel));
        ctx.Page!.SetDefaultTimeout(TestTimeouts.DesignerTestMs);
        ctx.Page!.SetDefaultNavigationTimeout(TestTimeouts.DesignerTestMs);
        SeedDesignerAdmin();
        await LoginDesignerAdminAsync();
        ApplyDesignerPageTimeouts();
    }

    [Given("the anonymous operator IDE is running on a kernel hosting instances {string} and {string}")]
    public async Task GivenAnonymousIdeRunning(string firstLabel, string secondLabel)
    {
        _designer = await ctx.StartKernelDesignerBrowserAsync((5, firstLabel), (6, secondLabel));
        SeedDesignerAdmin();
        // Anonymous path skips the login form in this harness.
        ApplyDesignerPageTimeouts();
    }

    private void ApplyDesignerPageTimeouts()
    {
        ctx.Page!.SetDefaultTimeout(TestTimeouts.DesignerActionMs);
        ctx.Page!.SetDefaultNavigationTimeout(TestTimeouts.DesignerTestMs);
    }

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
        // Wait for list content (any row) after login. Use classic selector (at-least-one) rather than
        // bare Locator to avoid strict-mode errors now that the list correctly shows 3 designs
        // (designer + the two hosted targets) thanks to working seeding.
        await page.WaitForSelectorAsync("main.ide-designs .set-row", new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    private Microsoft.Playwright.ILocator RowFor(string label) =>
        ctx.Page!.Locator("main.ide-list .set-row", new() {
            Has = ctx.Page.Locator("a.row-link", new() { HasTextString = label })
        });

    private Microsoft.Playwright.ILocator DesignRowFor(string label) =>
        ctx.Page!.Locator(".set-row", new() {
            Has = ctx.Page.Locator("a.row-link", new() { HasTextString = label })
        });

    private Microsoft.Playwright.ILocator TypeNameInput(string name) =>
        ctx.Page!.Locator("main.ide-design-edit .design-editor .type-card", new() {
            Has = ctx.Page.Locator($"input.type-name[value={CssString(name)}]")
        }).Locator("input.type-name");

    private Microsoft.Playwright.ILocator JustAddedTypeRow() =>
        ctx.Page!.Locator("main.ide-design-edit .design-editor .type-card", new() {
            Has = ctx.Page.Locator($"input.type-name[value={CssString(_justAddedTypeName)}]")
        });

    private Microsoft.Playwright.ILocator PropTypeSelect(string propName) =>
        ctx.Page!.Locator("main.ide-design-edit .design-editor .prop-row", new() {
            Has = ctx.Page.Locator($"input.prop-name[value={CssString(propName)}]")
        }).Locator("select.prop-type");

    private Microsoft.Playwright.ILocator PropCardinalitySelect(string propName) =>
        ctx.Page!.Locator("main.ide-design-edit .design-editor .prop-row", new() {
            Has = ctx.Page.Locator($"input.prop-name[value={CssString(propName)}]")
        }).Locator("select.prop-cardinality");

    private Microsoft.Playwright.ILocator PropKeytypeInput(string propName) =>
        ctx.Page!.Locator("main.ide-design-edit .design-editor .prop-row", new() {
            Has = ctx.Page.Locator($"input.prop-name[value={CssString(propName)}]")
        }).Locator("input.prop-keytype");

    private Microsoft.Playwright.ILocator PropMultilineInput(string propName) =>
        ctx.Page!.Locator("main.ide-design-edit .design-editor .prop-row", new() {
            Has = ctx.Page.Locator($"input.prop-name[value={CssString(propName)}]")
        }).Locator("input.prop-multiline");

    private static readonly Microsoft.Playwright.LocatorWaitForOptions Hidden =
        new() { State = Microsoft.Playwright.WaitForSelectorState.Hidden };

    private static Task EventuallyAsync(
        Func<bool> condition,
        int timeoutMs = TestTimeouts.DesignerTestMs,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string what = "")
        => Polling.EventuallyAsync(condition, what, timeoutMs);

    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static string CssString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
