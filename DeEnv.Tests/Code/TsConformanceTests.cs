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
                case "nothing":
                case "null":
                    break; // the kind comparison above is the whole assertion
                default:
                    throw new InvalidOperationException($"Unknown expect kind '{kind}' in case '{name}'.");
            }
        }

        await page.Context.CloseAsync();
    }
}
