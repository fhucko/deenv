using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

[Binding]
public sealed class PresentationSteps(InstanceContext ctx)
{
    [Then(@"I see the label {string}")]
    public async Task ThenISeeLabelAsync(string text)
    {
        var count = await ctx.Page!.Locator($"label:text-is('{text}')").CountAsync();
        await Assert.That(count).IsGreaterThanOrEqualTo(1);
    }

    [When(@"I click the {string} list title")]
    public async Task WhenClickListTitleAsync(string title)
    {
        await ctx.Page!.Locator($".list-title a:text-is('{title}')").ClickAsync();
        await ctx.Page.WaitForLoadStateAsync();
    }

    [Then(@"the create form has a {string} field")]
    public async Task ThenCreateHasFieldAsync(string name)
    {
        var count = await ctx.Page!.Locator($"form.create-form input[name='{name}']").CountAsync();
        await Assert.That(count).IsGreaterThanOrEqualTo(1);
    }

    [Then(@"the create form has no {string} field")]
    public async Task ThenCreateHasNoFieldAsync(string name)
    {
        var count = await ctx.Page!.Locator($"form.create-form input[name='{name}']").CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }
}
