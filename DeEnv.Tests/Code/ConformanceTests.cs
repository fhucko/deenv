using System.Text.Json;
using DeEnv.Code;
using DeEnv.Instance;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Runs the shared conformance suite (DeEnv/Code/conformance.json) through the C#
// interpreter. The same file is executed by the TypeScript interpreter in Stage 3,
// so any drift between the two evaluation cores fails here or there.
//
// This also proves the polymorphic JSON round-trip: each case's `expr` is the exact
// AST shape a hand-written instance.schema.json carries.
public sealed class ConformanceTests
{
    private static readonly JsonSerializerOptions JsonOpts = SchemaJson.Options;

    private sealed record Suite(Case[] Cases);
    public sealed record Case(string Name, JsonElement Expr, Expectation Expect);
    public sealed record Expectation(string Kind, JsonElement Value);

    public static IEnumerable<Func<Case>> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "conformance.json");
        var suite = JsonSerializer.Deserialize<Suite>(File.ReadAllText(path), JsonOpts)!;
        foreach (var c in suite.Cases) yield return () => c;
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Conformance_case_evaluates(Case c)
    {
        var expr = c.Expr.Deserialize<ICodeValue>(JsonOpts)!;
        var result = new CodeExecutor().ExecuteValue(expr, new ExecScope(), new ExecContext());

        switch (c.Expect.Kind)
        {
            case "int":
                await Assert.That(((ExecInt)result).Value).IsEqualTo(c.Expect.Value.GetInt32());
                break;
            case "text":
                await Assert.That(((ExecText)result).Value).IsEqualTo(c.Expect.Value.GetString());
                break;
            case "bool":
                await Assert.That(((ExecBool)result).Value).IsEqualTo(c.Expect.Value.GetBoolean());
                break;
            case "intList":
            {
                var got = ((ExecArray)result).Items.Select(i => ((ExecInt)i.Value).Value);
                var want = c.Expect.Value.EnumerateArray().Select(e => e.GetInt32());
                await Assert.That(string.Join(",", got)).IsEqualTo(string.Join(",", want));
                break;
            }
            case "nothing":
                await Assert.That(result is ExecNothing).IsTrue();
                break;
            default:
                throw new InvalidOperationException($"Unknown expect kind '{c.Expect.Kind}' in case '{c.Name}'.");
        }
    }
}
