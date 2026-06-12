using DeEnv.Code.Parsing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using static DeEnv.Code.Parsing.Parse;

namespace DeEnv.Tests.Code;

// Stage 0 of the text-syntax milestone: the ported parser-combinator core. These pin
// the behaviours the grammar depends on — match/advance, choice order, backtracking
// through Many1, full-input runs, and positioned errors from the failure high-water mark.
public sealed class ParsingTests
{
    [Test]
    public async Task Text_matches_and_advances()
    {
        var result = Run(Seq(Text("ab"), Text("c"), (a, b) => a + b), "abc");
        await Assert.That(result).IsEqualTo("abc");
    }

    [Test]
    public async Task Regex_anchors_at_the_cursor()
    {
        // The digits must match at the cursor, not anywhere later in the input.
        var result = Run(Seq(Text("x"), Regex("[0-9]+"), (_, d) => d), "x42");
        await Assert.That(result).IsEqualTo("42");
    }

    [Test]
    public async Task OneOf_tries_alternatives_in_order()
    {
        var result = Run(OneOf(Text("a").ConvertTo(_ => "first"), Text("a").ConvertTo(_ => "second")), "a");
        await Assert.That(result).IsEqualTo("first");
    }

    [Test]
    public async Task Many1_backtracks_until_the_whole_input_parses()
    {
        // Greedy-naive "as" would swallow the final 'a' needed by the tail; the run
        // must backtrack through shorter Many1 matches to find the complete parse.
        var parser = Seq(Many1(Text("a")), Text("ab"), (many, _) => many.Length);
        var result = Run(parser, "aaab");
        await Assert.That(result).IsEqualTo(2);
    }

    [Test]
    public async Task Many0Separated_parses_a_comma_list()
    {
        var result = Run(Many0Separated(Text(","), Regex("[a-z]+")), "a,bb,ccc");
        await Assert.That(string.Join("|", result)).IsEqualTo("a|bb|ccc");
    }

    [Test]
    public async Task Optional_yields_default_when_absent()
    {
        var result = Run(Seq(Optional(Text("x")), Text("y"), (x, _) => x ?? "none"), "y");
        await Assert.That(result).IsEqualTo("none");
    }

    [Test]
    public async Task Not_succeeds_without_consuming_when_the_parser_fails()
    {
        var result = Run(Seq(Not(Text("z")), Text("a"), (_, a) => a), "a");
        await Assert.That(result).IsEqualTo("a");
    }

    [Test]
    public async Task A_partial_match_is_not_a_successful_run()
    {
        // Text("a") matches, but the input has trailing content → no complete parse.
        await Assert.That(() => Run(Text("a"), "ab")).Throws<CodeParseException>();
    }

    [Test]
    public async Task A_failed_run_reports_the_furthest_failure_position()
    {
        // Line 1 parses; line 2 fails at "oops" (line 2, column 5).
        var line = Seq(Regex("[a-z]+"), Text(" = "), Regex("[0-9]+"), NlOrEnd, (n, _, v, _) => n + v);
        var ex = await Assert.That(() => Run(Many1(line), "a = 1\nb = oops\n")).Throws<CodeParseException>();
        await Assert.That(ex!.Message).Contains("line 2");
        await Assert.That(ex.Message).Contains("b = oops");
    }

    [Test]
    public async Task IndentLookahead_fixes_the_block_indent_from_its_first_line()
    {
        // A block = lines at the discovered indent; a deeper or shallower line ends it.
        IndentedParser<string[]> block = indent =>
            Many1(Seq(Text(indent), Regex("[a-z]+"), NlOrEnd, (_, w, _) => w));
        var parser = Seq(Regex("top"), Nl, IndentLookahead("", Ws1, block), (_, _, b) => b);
        var result = Run(parser, "top\n  aa\n  bb\n");
        await Assert.That(string.Join("|", result)).IsEqualTo("aa|bb");
    }
}
