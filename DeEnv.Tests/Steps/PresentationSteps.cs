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
        // Wait for the navigation's HTML to parse, NOT the full Load (the /js bundle) — see PageNav.
        await ctx.Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.DOMContentLoaded);
    }

    // The "create form" is the self-hosted, flag-gated create form (.create-form, revealed by `+ New`):
    // a labeled .field per scalar prop, its input classed by prop name. A set/dict prop is omitted
    // (collections are added on the entry's own page after it exists — the create-then-populate model).
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
        await ctx.Page!.Locator($".create-form input.{name}").CountAsync();
}
