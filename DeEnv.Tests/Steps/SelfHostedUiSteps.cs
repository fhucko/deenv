using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for SelfHostedUi.feature — the self-hosted generic UI (milestone 9, slice 1).
// The fixture opts in with `generic` and has no hand-written views, so the all-scalar
// Note object page is rendered by the Code `objectForm` library. Page-kind and
// navigation steps ("I open", "the page is a code page", "… auto-form", "the page
// shows", "the store eventually has …") are reused from the other step bindings.
[Binding]
public sealed class SelfHostedUiSteps(InstanceContext ctx)
{
    [Given("the self-hosted form app is running")]
    public async Task GivenSelfHostedFormAppRunning()
    {
        ctx.Description = InstanceContext.SelfHostedFormDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // objectForm gives each field input (and its label) the class of its prop name
    // (class={p.name}).
    [When("I fill the {string} field with {string}")]
    public async Task WhenFillField(string field, string value) =>
        await ctx.Page!.Locator($"input.{field}").FillAsync(value);

    [When("I save the form")]
    public async Task WhenSaveForm() =>
        await ctx.Page!.Locator("button.save").ClickAsync();

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
}
