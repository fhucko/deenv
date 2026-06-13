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

    // The self-hosted object form renders a collection prop's label as a navigable
    // <a class="list-title"> to its route; the retiring C# table nested the link inside
    // an <h3 class="list-title">. Match either.
    [When(@"I click the {string} list title")]
    public async Task WhenClickListTitleAsync(string title)
    {
        await ctx.Page!.Locator($"a.list-title:text-is('{title}'), .list-title a:text-is('{title}')")
            .First.ClickAsync();
        await ctx.Page.WaitForLoadStateAsync();
    }

    // The "create form" is the self-hosted inline add form (.set-new / .dict-new, inputs
    // classed by prop name) or the retiring C# create form (inputs keyed by name=).
    [Then(@"the create form has a {string} field")]
    public async Task ThenCreateHasFieldAsync(string name)
    {
        var count = await CreateFieldCountAsync(name);
        await Assert.That(count).IsGreaterThanOrEqualTo(1);
    }

    [Then(@"the create form has no {string} field")]
    public async Task ThenCreateHasNoFieldAsync(string name)
    {
        var count = await CreateFieldCountAsync(name);
        await Assert.That(count).IsEqualTo(0);
    }

    private async Task<int> CreateFieldCountAsync(string name) =>
        await ctx.Page!.Locator(
            $".set-new input.{name}, .dict-new input.{name}, form.create-form input[name='{name}']")
            .CountAsync();
}
