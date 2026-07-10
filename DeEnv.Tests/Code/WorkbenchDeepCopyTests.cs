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

    // Regression pin (2026-07-09, prompted by a false-alarm investigation — see the commit message): the
    // mount hook must be a PROVABLE no-op on a page with zero workbench containers — it must never touch
    // page render state (uiStatic), never call renderUi/commitRender, never invalidate anything. Proven
    // structurally, not by inference: this page loads ONLY codeExec.js + workbench.js — deliberately NOT
    // ui.js/init.js, so `uiStatic` (and every other page-render global ui.ts defines) is genuinely
    // UNDEFINED here. mountOneWorkbenchInstance (the only path that reads uiStatic) is called exclusively
    // from inside the `document.querySelectorAll("[instancemount]")` forEach — with zero matching elements,
    // that callback never runs, so calling mountWorkbenchInstances() on an empty page must complete without
    // ever referencing the undefined `uiStatic` — a ReferenceError there is the structural proof the hook
    // touched page state it shouldn't have on a zero-container pass.
    [Test]
    public async Task MountWorkbenchInstances_never_touches_page_state_on_a_zero_container_page()
    {
        var codeExecJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codeExec.js"));
        var workbenchJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workbench.js"));

        var page = await SharedBrowser.NewPageAsync();
        // No [instancemount] elements anywhere on this page.
        await page.SetContentAsync("<!doctype html><html><body><div>plain content</div></body></html>");
        await page.AddScriptTagAsync(new() { Content = codeExecJs });
        await page.AddScriptTagAsync(new() { Content = workbenchJs });

        var resultJson = await page.EvaluateAsync<string>("""
            () => {
                let err = null;
                try { mountWorkbenchInstances(); } catch (e) { err = (e && e.message) || String(e); }
                return JSON.stringify({ err, uiStaticDefined: typeof uiStatic !== 'undefined' });
            }
            """);
        var result = JsonDocument.Parse(resultJson).RootElement;

        // uiStatic genuinely never got defined on this page (ui.js/init.js were never loaded) — the
        // precondition that makes `err == null` a real structural proof, not a coincidence.
        await Assert.That(result.GetProperty("uiStaticDefined").GetBoolean()).IsFalse();
        await Assert.That(result.GetProperty("err").ValueKind).IsEqualTo(JsonValueKind.Null);

        await page.Context.CloseAsync();
    }

    // Regression pin (M12 W1a review, arch fix 1): a component whose view is legitimately empty — a bare
    // `if` with no `else`, condition false, so runBody's own `?? {type:"nothing"}` fallback fires with NO
    // store-backed builtin anywhere involved — must render an EMPTY card, not a fabricated "Value not
    // available" error. Before the fix, EVERY `nothing` was labeled an error unconditionally; this is the
    // preview≠live divergence the fix closes (the live page renders nothing for the same code).
    [Test]
    public async Task RenderWorkbenchInstance_shows_no_error_for_a_component_whose_view_is_legitimately_empty()
    {
        var codeExecJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codeExec.js"));
        var workbenchJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workbench.js"));

        var page = await SharedBrowser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body></body></html>");
        await page.AddScriptTagAsync(new() { Content = codeExecJs });
        await page.AddScriptTagAsync(new() { Content = workbenchJs });

        var resultJson = await page.EvaluateAsync<string>("""
            () => {
                setMemoCache(new Map()); // matches production: the driver always installs a real private cache
                // fn Empty() { if false { return <div>"never"</div> } } — the taken path (condition false)
                // never reaches a `return`, so runBody yields {type:"nothing"} — no VNA anywhere.
                const emptyFnAst = {
                    type: 'fn', name: 'Empty', params: [],
                    body: { type: 'block', statements: [
                        { type: 'if', condition: { type: 'bool', value: false },
                          body: { type: 'return', value: { type: 'tag', name: 'div', attributes: [], children: [ { type: 'text', value: 'never' } ] } },
                          elseBody: null },
                    ] },
                };
                const ctx = {
                    type: 'object', id: 1,
                    props: {
                        db: { type: 'object', id: 2, props: {} },
                        fns: { type: 'object', id: 3, props: { Empty: { type: 'object', id: 4, props: { ast: { type: 'text', value: JSON.stringify(emptyFnAst) } } } } },
                        exprs: { type: 'object', id: 5, props: {} },
                    },
                };
                const fn = { type: 'object', id: 6, props: { name: { type: 'text', value: 'Empty' } } };
                const use = { type: 'object', id: 7, props: { args: { type: 'array', kind: 'set', items: [], id: 0 } } };
                const result = renderWorkbenchInstance(fn, use, ctx);
                return JSON.stringify({ errorMessage: result.errorMessage, tagCount: result.tags.length });
            }
            """);
        var result = JsonDocument.Parse(resultJson).RootElement;

        // No error — an honest empty view, exactly what the live page shows for the same code (isRenderable
        // drops a `nothing` just like it drops anything else non-tag/text/int/bool).
        await Assert.That(result.GetProperty("errorMessage").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(result.GetProperty("tagCount").GetInt32()).IsEqualTo(0);

        await page.Context.CloseAsync();
    }

    // M12 W1b — the dispatch bracket's restore-on-throw (the W1a bracket test idiom, extended from render
    // time to HANDLER time — component-workbench.md's "grill's core fix"). A handler that throws must leave
    // every global the bracket touches (memoCache, slotPath, needsServerData, callDepth, wsHooks,
    // memoBypass) restored to whatever the ENCLOSING page render had installed — never the sandbox's own
    // values, and never some hardcoded default. Proven by installing distinctive PAGE-posture sentinels
    // before the call and asserting they — the exact same references/values, not just "something sane" —
    // are back in place after runInstanceHandler returns from a throwing body.
    //
    // This harness deliberately loads ONLY codeExec.js + workbench.js (the regression-pin precedent above),
    // so ui.ts's `updateChildren` — the one thing runInstanceHandler's error path calls — does not exist
    // here; a trivial stub stands in for it (the bracket-restore assertion below needs nothing from ui.ts;
    // the REAL error-card rendering is proven end-to-end by the browser scenario in Designer.feature).
    [Test]
    public async Task RunInstanceHandler_restores_every_bracket_global_when_the_handler_throws()
    {
        var codeExecJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codeExec.js"));
        var workbenchJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workbench.js"));

        var page = await SharedBrowser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body><section id=\"c\"></section></body></html>");
        await page.AddScriptTagAsync(new() { Content = codeExecJs });
        await page.AddScriptTagAsync(new() { Content = workbenchJs });

        var resultJson = await page.EvaluateAsync<string>("""
            () => {
                window.updateChildren = () => {}; // stand-in for ui.ts's reconciler — not loaded in this harness

                // Distinctive PAGE-posture sentinels — nothing the sandbox bracket would ever install itself,
                // so finding them back afterward proves a genuine RESTORE, not a coincidental match.
                const pageCache = new Map();
                setMemoCache(pageCache);
                slotPath.length = 0; slotPath.push('page-slot');
                needsServerData = true;
                callDepth = 7;
                wsHooks = { marker: 'PAGE_HOOKS' };
                memoBypass = false;

                const useId = 42;
                workbenchInstances.set(useId, { argsSignature: '', ctxKey: '', cache: new Map(), lastId: { value: 0 } });
                const container = document.getElementById('c');

                runInstanceHandler(useId, container, () => { throw new Error('boom'); });

                return JSON.stringify({
                    cacheRestored: memoCache === pageCache,
                    slotPathRestored: JSON.stringify(slotPath) === JSON.stringify(['page-slot']),
                    needsServerDataRestored: needsServerData === true,
                    callDepthRestored: callDepth === 7,
                    wsHooksRestored: wsHooks != null && wsHooks.marker === 'PAGE_HOOKS',
                    memoBypassRestored: memoBypass === false,
                });
            }
            """);
        var result = JsonDocument.Parse(resultJson).RootElement;

        await Assert.That(result.GetProperty("cacheRestored").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("slotPathRestored").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("needsServerDataRestored").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("callDepthRestored").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("wsHooksRestored").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("memoBypassRestored").GetBoolean()).IsTrue();

        await page.Context.CloseAsync();
    }
}
