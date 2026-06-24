using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Code;
using DeEnv.Instance;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Stage 4 of the text-syntax milestone: the printer. parse(print(desc)) must equal
// desc (structural equality over the serialized description), and the canonical
// printed form is a fixpoint — printing it again reproduces it byte for byte.
public sealed class AppPrintTests
{
    private static async Task AssertRoundTrips(string appText)
    {
        var first = AppParse.Parse(appText);
        var printed = AppPrint.Print(first);
        var second = AppParse.Parse(printed);

        var a = JsonSerializer.SerializeToNode(first, SchemaJson.Options)!;
        var b = JsonSerializer.SerializeToNode(second, SchemaJson.Options)!;
        if (!JsonNode.DeepEquals(a, b))
            await Assert.That(b.ToJsonString()).IsEqualTo(a.ToJsonString());

        await Assert.That(AppPrint.Print(second)).IsEqualTo(printed);
    }

    [Test]
    public async Task The_todo_app_round_trips()
    {
        await AssertRoundTrips(File.ReadAllText(InstanceContext.AppFixture(2)));
    }

    [Test]
    public async Task The_code_fixtures_round_trip()
    {
        foreach (var app in InstanceContext.CodeFixtureApps)
            await AssertRoundTrips(app);
    }

    [Test]
    public async Task The_crm_and_designer_documents_round_trip()
    {
        await AssertRoundTrips(File.ReadAllText(InstanceContext.AppFixture(3)));
        await AssertRoundTrips(File.ReadAllText(InstanceContext.AppFixture(1)));
    }

    // An enum type (`Name: enum` + an indented value list) round-trips: parse∘print is the
    // identity and the printed form is a fixpoint, with the values in declared order.
    [Test]
    public async Task The_enum_fixture_round_trips()
    {
        await AssertRoundTrips(InstanceContext.EnumFixtureApp);
    }

    // A `text multiline` prop (the presentation attribute) round-trips: the trailing `multiline`
    // keyword is parsed onto the prop and printed back after the type, so parse∘print is the
    // identity and the printed form is a fixpoint. A plain `text`/`int` prop prints with no keyword.
    [Test]
    public async Task The_multiline_fixture_round_trips()
    {
        await AssertRoundTrips(InstanceContext.MultilineFixtureApp);
    }

    // ── expression printing: minimal parentheses ────────────────────────────────

    [Test]
    public async Task Parentheses_print_only_where_precedence_requires_them()
    {
        await AssertPrints("2 * (3 + 4)", "2 * (3 + 4)");
        await AssertPrints("2 * 3 + 4", "2 * 3 + 4");
        await AssertPrints("(2 + 3) * 4", "(2 + 3) * 4");
        await AssertPrints("a - b - c", "a - b - c");          // left-assoc: no parens
        await AssertPrints("a - (b - c)", "a - (b - c)");      // right nesting kept
        await AssertPrints("db.tasks.where(x => x.done == false)",
                           "db.tasks.where(x => x.done == false)");
        // The parenthesized single-param form normalizes to the bare form.
        await AssertPrints("db.tasks.where((x) => x.done == false)",
                           "db.tasks.where(x => x.done == false)");
        await AssertPrints("((n) => n + 1)(41)", "(n => n + 1)(41)");
        // Unary `!`: tighter than the binary ops (no parens on a member/comparison operand),
        // but it parenthesizes a lower-precedence operand; `!!x` nests bare.
        await AssertPrints("!a", "!a");
        await AssertPrints("!a.b", "!a.b");
        await AssertPrints("!a == b", "!a == b");
        await AssertPrints("!(a == b)", "!(a == b)");
        await AssertPrints("!!a", "!!a");
    }

    private static async Task AssertPrints(string source, string expected)
    {
        var printed = CodePrint.Value(CodeParse.ParseExpression(source));
        await Assert.That(printed).IsEqualTo(expected);
        // The printed form parses back to the same tree.
        var a = JsonSerializer.SerializeToNode<ICodeValue>(CodeParse.ParseExpression(source), SchemaJson.Options)!;
        var b = JsonSerializer.SerializeToNode<ICodeValue>(CodeParse.ParseExpression(printed), SchemaJson.Options)!;
        await Assert.That(JsonNode.DeepEquals(a, b)).IsTrue();
    }
}
