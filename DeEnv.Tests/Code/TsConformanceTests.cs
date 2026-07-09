using System.Text.Json;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Runs the shared conformance suite (conformance.json) through the TypeScript twin
// interpreter (DeEnv/Instance/codeExec.js), the mirror of ConformanceTests on the C#
// side. The same AST-in/value-out cases must evaluate identically on both cores, so
// any drift between the two interpreters fails here or in ConformanceTests.
public sealed class TsConformanceTests
{
    [Test]
    public async Task TypeScript_interpreter_matches_conformance_suite()
    {
        var suite = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance.json")));
        var interpreterJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codeExec.js"));

        // Reuse the shared browser (launched once for the whole run; see SharedBrowser).
        var page = await SharedBrowser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body></body></html>");
        await page.AddScriptTagAsync(new() { Content = interpreterJs });

        foreach (var c in suite.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString();
            var expect = c.GetProperty("expect");
            var kind = expect.GetProperty("kind").GetString();
            // Pass the WHOLE case (a single `expr`, or `setup` + `renders`) so the twin runs the
            // identical protocol; the C# side runs it in ConformanceTests.EvaluateCase.
            var resultJson = await page.EvaluateAsync<string>("e => runConformance(e)", c.GetRawText());
            var result = JsonDocument.Parse(resultJson).RootElement;

            await Assert.That(result.GetProperty("kind").GetString()).IsEqualTo(kind);
            switch (kind)
            {
                case "int":
                    await Assert.That(result.GetProperty("value").GetInt32()).IsEqualTo(expect.GetProperty("value").GetInt32());
                    break;
                case "text":
                    await Assert.That(result.GetProperty("value").GetString()).IsEqualTo(expect.GetProperty("value").GetString());
                    break;
                case "bool":
                    await Assert.That(result.GetProperty("value").GetBoolean()).IsEqualTo(expect.GetProperty("value").GetBoolean());
                    break;
                case "intList":
                {
                    var got = result.GetProperty("value").EnumerateArray().Select(e => e.GetInt32());
                    var want = expect.GetProperty("value").EnumerateArray().Select(e => e.GetInt32());
                    await Assert.That(string.Join(",", got)).IsEqualTo(string.Join(",", want));
                    break;
                }
                case "tag":
                    await Assert.That(result.GetProperty("value").GetString()).IsEqualTo(expect.GetProperty("value").GetString());
                    break;
                case "error":
                    // M12 FG: the case must THROW (a runaway-recursive fn past the call-depth guard);
                    // runConformance catches it and reports { kind: "error", value: <message> }.
                    await Assert.That(result.GetProperty("value").GetString()).IsEqualTo(expect.GetProperty("value").GetString());
                    break;
                case "nothing":
                case "null":
                    break; // the kind comparison above is the whole assertion
                default:
                    throw new InvalidOperationException($"Unknown expect kind '{kind}' in case '{name}'.");
            }
        }

        await page.Context.CloseAsync();
    }

    // The TS-side guard-trip leak proof (arch review): codeExec.ts's callDepth is a MODULE-level counter
    // (not per-context like C#'s ctx.CallDepth), so a leaked increment would be PERMANENT — eroding the
    // 256 ceiling by 1 per caught trip until legitimate shallow renders eventually throw spuriously. Runs
    // the SAME unbounded-recursion case TWICE on ONE page (one shared module scope, exactly the reuse
    // pattern that would surface a leak), reading the module-level counter through the test-only
    // `__callDepthForTest` hook (mirrors runConformance's own test-harness-only export) after each trip.
    // Twin of ConformanceTests.Tripping_the_guard_does_not_leak_call_depth.
    [Test]
    public async Task TypeScript_interpreter_does_not_leak_call_depth_across_repeated_guard_trips()
    {
        const string recursiveCase = """
            { "expr": { "type": "call", "params": [],
                "fn": { "type": "fn", "name": "Rec", "params": [], "body": { "type": "block", "statements": [
                    { "type": "return", "value": { "type": "call", "params": [], "fn": { "type": "symbol", "name": "Rec" } } }
                ] } } } }
            """;

        var interpreterJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codeExec.js"));
        var page = await SharedBrowser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body></body></html>");
        await page.AddScriptTagAsync(new() { Content = interpreterJs });

        var firstJson = await page.EvaluateAsync<string>("e => runConformance(e)", recursiveCase);
        var first = JsonDocument.Parse(firstJson).RootElement;
        await Assert.That(first.GetProperty("kind").GetString()).IsEqualTo("error");
        await Assert.That(first.GetProperty("value").GetString()).IsEqualTo("Call depth exceeded 256 — runaway recursion?");
        await Assert.That(await page.EvaluateAsync<int>("() => __callDepthForTest()")).IsEqualTo(0);

        // A second trip on the SAME page (same module-level callDepth): pre-fix this would occur one
        // call shallower than the first (the leaked +1 from the first trip); post-fix it's identical.
        var secondJson = await page.EvaluateAsync<string>("e => runConformance(e)", recursiveCase);
        var second = JsonDocument.Parse(secondJson).RootElement;
        await Assert.That(second.GetProperty("kind").GetString()).IsEqualTo("error");
        await Assert.That(second.GetProperty("value").GetString()).IsEqualTo(first.GetProperty("value").GetString());
        await Assert.That(await page.EvaluateAsync<int>("() => __callDepthForTest()")).IsEqualTo(0);

        await page.Context.CloseAsync();
    }
}
