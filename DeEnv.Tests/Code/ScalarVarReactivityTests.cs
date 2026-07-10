using System.Text.Json;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Unit-tests (client-side TS only — the reactivity itself is browser-pinned, per AGENTS' Code
// execution model: the memo cache's read-back invalidation loop is a CLIENT phenomenon, the server
// renders once) the component-local scalar-var reactivity fix: a bare `var count = 0` in a
// component's setup, reassigned by a handler (`count = count + 1`), must invalidate and recompute the
// component's memoized VIEW — the gap SelfHostedUiSteps.cs's own comment on ReactiveCounterConvertibleRender
// documents (every other click-driven fixture had to use the `var state = { count: 0 }` OBJECT idiom to
// work around it). Drives codeExec.js directly (the TsConformanceTests/WorkbenchDeepCopyTests idiom): a
// bare page with only codeExec.js loaded, calling executeComponentValue/executeStatement straight, so the
// test exercises the SAME memoize/invalidateProp machinery a live page's ui.ts uses, with no DOM/WS noise.
public sealed class ScalarVarReactivityTests
{
    // A stateful component whose setup declares a BARE scalar `count` and a `views` counter (a second
    // bare var, incremented every time the VIEW body actually runs — the observable "did this recompute"
    // signal, since a cache HIT never re-executes the view at all). Returns [count, views] as an ARRAY,
    // deliberately NOT an object literal: memoize's own "identity-creating computation" guard never
    // caches a freshly-minted OBJECT result (it would hand every caller the same mutable instance — see
    // its doc comment), which would make the view re-run on EVERY call regardless of staleness and
    // silently defeat this whole test. An array result has no such exclusion (a where/orderBy result is
    // exactly this shape and stays cacheable), so a genuine cache HIT is observable here.
    private const string CounterAst = """
        {
          "type": "fn", "name": "Counter", "params": [],
          "body": { "type": "block", "statements": [
            { "type": "varDec", "name": "count", "value": { "type": "int", "value": 0 } },
            { "type": "varDec", "name": "views", "value": { "type": "int", "value": 0 } },
            { "type": "fn", "name": "view", "params": [], "body": { "type": "block", "statements": [
              { "type": "assign", "target": { "type": "symbol", "name": "views" }, "value": {
                "type": "infixOp", "op": "add",
                "left": { "type": "symbol", "name": "views" }, "right": { "type": "int", "value": 1 } } },
              { "type": "return", "value": { "type": "array", "items": [
                { "type": "symbol", "name": "count" }, { "type": "symbol", "name": "views" }
              ] } }
            ] } },
            { "type": "return", "value": { "type": "symbol", "name": "view" } }
          ] }
        }
        """;

    // The handler AST a real onClick would run: `count = count + 1`, executed against the render
    // closure's OWN captured scope (a child frame, mirroring how a real handler closure's call scope
    // chains up to the setup's captured locals) — never touching executeComponentValue's memoize path
    // directly, exactly like a live click.
    private const string IncrementCountStatement = """
        { "type": "assign", "target": { "type": "symbol", "name": "count" }, "value": {
          "type": "infixOp", "op": "add",
          "left": { "type": "symbol", "name": "count" }, "right": { "type": "int", "value": 1 } } }
        """;

    [Test]
    public async Task A_handler_write_to_a_bare_scalar_var_invalidates_and_recomputes_the_memoized_view()
    {
        var codeExecJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codeExec.js"));
        var page = await SharedBrowser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body></body></html>");
        await page.AddScriptTagAsync(new() { Content = codeExecJs });

        var resultJson = await page.EvaluateAsync<string>(
            """
            ([counterAst, incrementStmt]) => {
                setMemoCache(new Map());
                resetSlotPath();
                const scope = { items: {}, parent: null };
                const context = { lastId: { value: 0 } };
                executeStatement(JSON.parse(counterAst), scope, context);

                const component = tryResolveComponent("Counter", scope);
                const tag = { type: "tag", name: "Counter", attributes: [], children: [] };

                // First mount: setup runs, view runs once (views=1), count starts at 0.
                const view1 = executeComponentValue(tag, component, scope, context);

                // The render closure setup minted — its scope holds `count`/`views`, the SAME storage
                // cells a real onClick handler closure would close over via the scope chain.
                const setupEntry = memoCache.get("comp:");
                const renderClosure = setupEntry.result;
                const handlerScope = { items: {}, parent: renderClosure.scope };

                // Simulate the handler dispatch (real handlers run under memoBypass — side-effecting).
                runWithMemoBypass(() => { executeStatement(JSON.parse(incrementStmt), handlerScope, context); });

                // Re-render the SAME slot: with the fix, the view memo entry went stale and recomputes.
                const view2 = executeComponentValue(tag, component, scope, context);

                return JSON.stringify({
                    count1: view1.items[0].value.value, views1: view1.items[1].value.value,
                    count2: view2.items[0].value.value, views2: view2.items[1].value.value,
                });
            }
            """, new[] { CounterAst, IncrementCountStatement });
        var result = JsonDocument.Parse(resultJson).RootElement;

        await Assert.That(result.GetProperty("count1").GetInt32()).IsEqualTo(0);
        await Assert.That(result.GetProperty("views1").GetInt32()).IsEqualTo(1);
        // RED before the fix: the view memo entry never goes stale, so view2 is the SAME cached result as
        // view1 — count2 stays 0 and views2 stays 1 (no recompute). GREEN after: the write invalidates the
        // (item.id, "value") dep the view's read recorded, so the view recomputes.
        await Assert.That(result.GetProperty("count2").GetInt32()).IsEqualTo(1);
        await Assert.That(result.GetProperty("views2").GetInt32()).IsEqualTo(2);

        await page.Context.CloseAsync();
    }

    // The trap the naive (name-keyed) fix falls into, made observable: TWO Counter instances at DISTINCT
    // slots each declare their own `count`. Writing instance A's count must invalidate ONLY A's view —
    // never B's, which would happen if the dep were keyed by the var's NAME ("count") instead of the
    // scope ITEM's own minted identity (both instances declare a var literally named "count").
    [Test]
    public async Task A_write_to_one_component_instances_var_never_invalidates_a_sibling_instances_same_named_var()
    {
        var codeExecJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codeExec.js"));
        var page = await SharedBrowser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body></body></html>");
        await page.AddScriptTagAsync(new() { Content = codeExecJs });

        var resultJson = await page.EvaluateAsync<string>(
            """
            ([counterAst, incrementStmt]) => {
                setMemoCache(new Map());
                resetSlotPath();
                const scope = { items: {}, parent: null };
                const context = { lastId: { value: 0 } };
                executeStatement(JSON.parse(counterAst), scope, context);
                const component = tryResolveComponent("Counter", scope);

                function mount(slotSegment) {
                    slotPath.length = 0;
                    slotPath.push(slotSegment);
                    const tag = { type: "tag", name: "Counter", attributes: [], children: [] };
                    const view = executeComponentValue(tag, component, scope, context);
                    slotPath.length = 0;
                    return view;
                }
                function rerender(slotSegment) { return mount(slotSegment); } // same slot key -> memoize hits/misses correctly

                // Two sibling instances, at distinct render-tree slots (mirrors two rows of a foreach).
                const a1 = mount("rowA");
                const b1 = mount("rowB");

                // Write ONLY instance A's `count`, via ITS OWN setup scope (comp:rowA).
                const aSetupEntry = memoCache.get("comp:rowA");
                const aHandlerScope = { items: {}, parent: aSetupEntry.result.scope };
                runWithMemoBypass(() => { executeStatement(JSON.parse(incrementStmt), aHandlerScope, context); });

                const a2 = rerender("rowA");
                const b2 = rerender("rowB");

                return JSON.stringify({
                    aCount1: a1.items[0].value.value, aViews1: a1.items[1].value.value,
                    bCount1: b1.items[0].value.value, bViews1: b1.items[1].value.value,
                    aCount2: a2.items[0].value.value, aViews2: a2.items[1].value.value,
                    bCount2: b2.items[0].value.value, bViews2: b2.items[1].value.value,
                });
            }
            """, new[] { CounterAst, IncrementCountStatement });
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Both instances mount independently (views=1 each, count=0 each).
        await Assert.That(result.GetProperty("aCount1").GetInt32()).IsEqualTo(0);
        await Assert.That(result.GetProperty("aViews1").GetInt32()).IsEqualTo(1);
        await Assert.That(result.GetProperty("bCount1").GetInt32()).IsEqualTo(0);
        await Assert.That(result.GetProperty("bViews1").GetInt32()).IsEqualTo(1);

        // A's write repaints ONLY A: A recomputes (views 1→2, count 0→1); B's cached view is UNTOUCHED
        // (views stays 1 — proof it never re-ran — and count stays 0).
        await Assert.That(result.GetProperty("aCount2").GetInt32()).IsEqualTo(1);
        await Assert.That(result.GetProperty("aViews2").GetInt32()).IsEqualTo(2);
        await Assert.That(result.GetProperty("bCount2").GetInt32()).IsEqualTo(0);
        await Assert.That(result.GetProperty("bViews2").GetInt32()).IsEqualTo(1);

        await page.Context.CloseAsync();
    }
}
