using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for Designer.feature — the hand-rolled designer (DeEnv/designer.app), an operator-facing
// custom `fn render()` over its own meta-schema, driven end-to-end through a real browser like the
// todo app: SSR first paint, client hydration, then hand-rolled reactive editing (add/edit type +
// prop via clicks/inputs). There is no kernel under TestInstanceServer, so `sys.instances` renders
// empty and the create control's spawn effect is NOT exercised — the scenario asserts the control
// is present, never clicks it (a click would hit NoHostActions).
[Binding]
public sealed class DesignerSteps(InstanceContext ctx)
{
    [Given("the designer app is running")]
    public async Task GivenDesignerAppRunning()
    {
        ctx.Description = InstanceContext.DesignerDb();
        await ctx.EnsureServerAndBrowserAsync();
        await ctx.Page!.GotoAsync("/");
        // Hydration checkpoint: the custom render's static root is in the SSR paint AND the client
        // bundle has bootstrapped (window.initUi set by init()), so the hand-rolled onClick handlers
        // are attached before we interact. The type list starts empty (no initialData), so there is
        // no foreach data-key to wait on — the static root is the stable signal.
        await ctx.Page.WaitForSelectorAsync("main.designer button.add-type");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I add a type")]
    public async Task WhenAddType()
    {
        await ctx.Page!.Locator("button.add-type").ClickAsync();
        await ctx.Page.WaitForSelectorAsync(".type-row"); // reactive add landed
    }

    [When("I name the first type {string}")]
    public async Task WhenNameFirstType(string name) =>
        await ctx.Page!.Locator(".type-row input.type-name").First.FillAsync(name);

    [When("I add a prop to the first type")]
    public async Task WhenAddProp()
    {
        await ctx.Page!.Locator(".type-row button.add-prop").First.ClickAsync();
        await ctx.Page.WaitForSelectorAsync(".prop-row"); // reactive add landed
    }

    [When("I name the first prop {string}")]
    public async Task WhenNameFirstProp(string name) =>
        await ctx.Page!.Locator(".prop-row input.prop-name").First.FillAsync(name);

    [Then("the designer shows a type named {string} with a prop named {string}")]
    public async Task ThenShowsTypeWithProp(string typeName, string propName)
    {
        // Read the live (post-hydration, reactive) input values: the bound type/prop name fields
        // reflect the edited model after re-render.
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.type-row input.type-name')].some(e => e.value === {JsString(typeName)})");
        await ctx.Page.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.prop-row input.prop-name')].some(e => e.value === {JsString(propName)})");
    }

    [Then("the page shows a create-instance control")]
    public async Task ThenShowsCreateControl() =>
        await ctx.Page!.WaitForSelectorAsync("button.create-instance");

    [When("I set the app port to {string}")]
    public async Task WhenSetAppPort(string value) =>
        await ctx.Page!.Locator("input.app-port").FillAsync(value);

    // The app-port var is an int (`var appPort = 9100`). A bound input writes a string, so without
    // coercion the var would become text "007" and sys.create's port arg would arrive as a string
    // (rejected server-side). Coercion parses it to the int 7, which re-renders as "7" — text would
    // have kept "007". So "007" -> "7" proves the bound input preserved the int type.
    [Then("the app port input shows {string}")]
    public async Task ThenAppPortShows(string value) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('input.app-port')?.value === {JsString(value)}");

    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
