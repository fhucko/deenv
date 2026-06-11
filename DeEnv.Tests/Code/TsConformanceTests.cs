using System.Text.Json;
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
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record Suite(Case[] Cases);
    private sealed record Case(string Name, JsonElement Expr, Expectation Expect);
    private sealed record Expectation(string Kind, JsonElement Value);

    [Test]
    public async Task TypeScript_interpreter_matches_conformance_suite()
    {
        var suite = JsonSerializer.Deserialize<Suite>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance.json")), JsonOpts)!;
        var interpreterJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codeExec.js"));

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body></body></html>");
        await page.AddScriptTagAsync(new() { Content = interpreterJs });

        foreach (var c in suite.Cases)
        {
            var resultJson = await page.EvaluateAsync<string>("e => runConformance(e)", c.Expr.GetRawText());
            var result = JsonDocument.Parse(resultJson).RootElement;

            await Assert.That(result.GetProperty("kind").GetString()).IsEqualTo(c.Expect.Kind);
            switch (c.Expect.Kind)
            {
                case "int":
                    await Assert.That(result.GetProperty("value").GetInt32()).IsEqualTo(c.Expect.Value.GetInt32());
                    break;
                case "text":
                    await Assert.That(result.GetProperty("value").GetString()).IsEqualTo(c.Expect.Value.GetString());
                    break;
                case "bool":
                    await Assert.That(result.GetProperty("value").GetBoolean()).IsEqualTo(c.Expect.Value.GetBoolean());
                    break;
                case "intList":
                {
                    var got = result.GetProperty("value").EnumerateArray().Select(e => e.GetInt32());
                    var want = c.Expect.Value.EnumerateArray().Select(e => e.GetInt32());
                    await Assert.That(string.Join(",", got)).IsEqualTo(string.Join(",", want));
                    break;
                }
                case "nothing":
                    break; // the kind comparison above is the whole assertion
                default:
                    throw new InvalidOperationException($"Unknown expect kind '{c.Expect.Kind}' in case '{c.Name}'.");
            }
        }
    }
}
