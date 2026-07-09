using System.Text.Json;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Unit-tests the ONE piece of M12 W1a that is pure client-side TS with no server twin (the design doc's
// own scope: "the graph copy is client-side TS; test through whatever client-harness exists, else through
// the browser pins") — workbench.ts's deep-copy re-mint, the mechanism that gives every mounted preview
// instance its OWN mutable seed graph. Loaded the same way TsConformanceTests loads codeExec.js: a bare
// page, the compiled JS attached directly, no DeEnv server involved.
public sealed class WorkbenchDeepCopyTests
{
    [Test]
    public async Task DeepCopySeed_remints_fake_positive_ids_and_preserves_shared_and_cyclic_structure()
    {
        var codeExecJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codeExec.js"));
        var workbenchJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workbench.js"));

        var page = await SharedBrowser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body></body></html>");
        await page.AddScriptTagAsync(new() { Content = codeExecJs });
        await page.AddScriptTagAsync(new() { Content = workbenchJs });

        // A root object with a SHARED child (reachable via two different props — the same reference) and a
        // CYCLIC self-reference — built as real JS object references on the page (a graph like this cannot
        // round-trip through JSON.stringify, so it must be constructed in-page, not passed in as an arg).
        var resultJson = await page.EvaluateAsync<string>("""
            () => {
                const shared = { type: 'object', props: { title: { type: 'text', value: 'Alpha' } }, id: 2 };
                const root = { type: 'object', props: {}, id: 1 };
                root.props['a'] = shared;
                root.props['b'] = shared;
                root.props['self'] = root;
                const copy = deepCopySeed(root, new Map());
                return JSON.stringify({
                    rootId: copy.id,
                    aId: copy.props['a'].id,
                    bId: copy.props['b'].id,
                    selfId: copy.props['self'].id,
                    sharedIsSameRef: copy.props['a'] === copy.props['b'],
                    selfIsRootRef: copy.props['self'] === copy,
                    rootIsFakePositive: copy.id >= 1000000000,
                    sharedIsFakePositive: copy.props['a'].id >= 1000000000,
                    titleSurvived: copy.props['a'].props['title'].value === 'Alpha',
                });
            }
            """);
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Shared structure preserved: BOTH props still point at the SAME copy (not two independent copies).
        await Assert.That(result.GetProperty("sharedIsSameRef").GetBoolean()).IsTrue();
        // Cyclic structure preserved: the self-reference copy IS the root copy (no infinite recursion, no
        // divergent second copy of the "same" node).
        await Assert.That(result.GetProperty("selfIsRootRef").GetBoolean()).IsTrue();
        // Every re-minted id is out-of-range positive (the user-settled fake-positive scheme).
        await Assert.That(result.GetProperty("rootIsFakePositive").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("sharedIsFakePositive").GetBoolean()).IsTrue();
        // The root and the shared child got DISTINCT new ids (a re-mint, not a collapse to one id).
        await Assert.That(result.GetProperty("rootId").GetInt32()).IsNotEqualTo(result.GetProperty("aId").GetInt32());
        // Both paths to the shared child agree on its NEW id too.
        await Assert.That(result.GetProperty("aId").GetInt32()).IsEqualTo(result.GetProperty("bId").GetInt32());
        // The self-reference's id matches the root copy's own (same object).
        await Assert.That(result.GetProperty("selfId").GetInt32()).IsEqualTo(result.GetProperty("rootId").GetInt32());
        // Plain data survives the copy untouched.
        await Assert.That(result.GetProperty("titleSurvived").GetBoolean()).IsTrue();

        await page.Context.CloseAsync();
    }
}
