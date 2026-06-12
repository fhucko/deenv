using System.Text.RegularExpressions;

namespace DeEnv.Code.Parsing;

// The combinators, ported from the app14 prototype. Semantics are kept identical —
// notably Many1 yields SHORTER matches first and OneOf tries alternatives in order;
// the grammar was written against exactly that backtracking behaviour.
public static class Parse
{
    // ── primitives ───────────────────────────────────────────────────────────────

    public static Parser<string> Text(string text)
    {
        IEnumerable<IResult<string>> ParseText(Cursor input)
        {
            if (input.Source.AsSpan(input.Offset).StartsWith(text))
                yield return new Result<string>(text, input.Advance(text.Length));
            else
                input.MarkFailure();
        }
        return ParseText;
    }

    public static Parser<string> Regex(string pattern)
    {
        // \G anchors the match at the cursor offset (^ would anchor at the string start).
        var regex = new Regex(@"\G(?:" + pattern + ")", RegexOptions.Compiled);
        IEnumerable<IResult<string>> ParseRegex(Cursor input)
        {
            var match = regex.Match(input.Source, input.Offset);
            if (match.Success)
                yield return new Result<string>(match.Value, input.Advance(match.Length));
            else
                input.MarkFailure();
        }
        return ParseRegex;
    }

    public static Parser<string> Ws0 => Regex(@"[ \t]*");
    public static Parser<string> Ws1 => Regex(@"[ \t]+");
    public static Parser<string> Nl => Regex(@"(\r\n|\n\r|\n)");
    public static Parser<string> End => input => input.AtEnd
        ? [new Result<string>("", input)]
        : Fail<string>(input);
    public static Parser<string> NlOrEnd => OneOf(Nl, End);

    private static IEnumerable<IResult<T>> Fail<T>(Cursor input)
    {
        input.MarkFailure();
        return [];
    }

    // ── sequencing (combine overloads; arity per grammar need) ─────────────────────

    public static Parser<R> Seq<R1, R2, R>(Parser<R1> p1, Parser<R2> p2, Func<R1, R2, R> combine) =>
        input => p1(input)
            .SelectMany(r1 => p2(r1.Remainder)
            .Select(r2 => new Result<R>(combine(r1.Value, r2.Value), r2.Remainder)));

    public static Parser<R> Seq<R1, R2, R3, R>(Parser<R1> p1, Parser<R2> p2, Parser<R3> p3, Func<R1, R2, R3, R> combine) =>
        input => p1(input)
            .SelectMany(r1 => p2(r1.Remainder)
            .SelectMany(r2 => p3(r2.Remainder)
            .Select(r3 => new Result<R>(combine(r1.Value, r2.Value, r3.Value), r3.Remainder))));

    public static Parser<R> Seq<R1, R2, R3, R4, R>(
        Parser<R1> p1, Parser<R2> p2, Parser<R3> p3, Parser<R4> p4,
        Func<R1, R2, R3, R4, R> combine) =>
        input => p1(input)
            .SelectMany(r1 => p2(r1.Remainder)
            .SelectMany(r2 => p3(r2.Remainder)
            .SelectMany(r3 => p4(r3.Remainder)
            .Select(r4 => new Result<R>(combine(r1.Value, r2.Value, r3.Value, r4.Value), r4.Remainder)))));

    public static Parser<R> Seq<R1, R2, R3, R4, R5, R>(
        Parser<R1> p1, Parser<R2> p2, Parser<R3> p3, Parser<R4> p4, Parser<R5> p5,
        Func<R1, R2, R3, R4, R5, R> combine) =>
        input => p1(input)
            .SelectMany(r1 => p2(r1.Remainder)
            .SelectMany(r2 => p3(r2.Remainder)
            .SelectMany(r3 => p4(r3.Remainder)
            .SelectMany(r4 => p5(r4.Remainder)
            .Select(r5 => new Result<R>(
                combine(r1.Value, r2.Value, r3.Value, r4.Value, r5.Value), r5.Remainder))))));

    public static Parser<R> Seq<R1, R2, R3, R4, R5, R6, R>(
        Parser<R1> p1, Parser<R2> p2, Parser<R3> p3, Parser<R4> p4, Parser<R5> p5, Parser<R6> p6,
        Func<R1, R2, R3, R4, R5, R6, R> combine) =>
        input => p1(input)
            .SelectMany(r1 => p2(r1.Remainder)
            .SelectMany(r2 => p3(r2.Remainder)
            .SelectMany(r3 => p4(r3.Remainder)
            .SelectMany(r4 => p5(r4.Remainder)
            .SelectMany(r5 => p6(r5.Remainder)
            .Select(r6 => new Result<R>(
                combine(r1.Value, r2.Value, r3.Value, r4.Value, r5.Value, r6.Value), r6.Remainder)))))));

    public static Parser<R> Seq<R1, R2, R3, R4, R5, R6, R7, R>(
        Parser<R1> p1, Parser<R2> p2, Parser<R3> p3, Parser<R4> p4, Parser<R5> p5, Parser<R6> p6, Parser<R7> p7,
        Func<R1, R2, R3, R4, R5, R6, R7, R> combine) =>
        input => p1(input)
            .SelectMany(r1 => p2(r1.Remainder)
            .SelectMany(r2 => p3(r2.Remainder)
            .SelectMany(r3 => p4(r3.Remainder)
            .SelectMany(r4 => p5(r4.Remainder)
            .SelectMany(r5 => p6(r5.Remainder)
            .SelectMany(r6 => p7(r6.Remainder)
            .Select(r7 => new Result<R>(
                combine(r1.Value, r2.Value, r3.Value, r4.Value, r5.Value, r6.Value, r7.Value), r7.Remainder))))))));

    public static Parser<R> Seq<R1, R2, R3, R4, R5, R6, R7, R8, R>(
        Parser<R1> p1, Parser<R2> p2, Parser<R3> p3, Parser<R4> p4, Parser<R5> p5, Parser<R6> p6, Parser<R7> p7, Parser<R8> p8,
        Func<R1, R2, R3, R4, R5, R6, R7, R8, R> combine) =>
        input => p1(input)
            .SelectMany(r1 => p2(r1.Remainder)
            .SelectMany(r2 => p3(r2.Remainder)
            .SelectMany(r3 => p4(r3.Remainder)
            .SelectMany(r4 => p5(r4.Remainder)
            .SelectMany(r5 => p6(r5.Remainder)
            .SelectMany(r6 => p7(r6.Remainder)
            .SelectMany(r7 => p8(r7.Remainder)
            .Select(r8 => new Result<R>(
                combine(r1.Value, r2.Value, r3.Value, r4.Value, r5.Value, r6.Value, r7.Value, r8.Value), r8.Remainder)))))))));

    public static Parser<R> Seq<R1, R2, R3, R4, R5, R6, R7, R8, R9, R>(
        Parser<R1> p1, Parser<R2> p2, Parser<R3> p3, Parser<R4> p4, Parser<R5> p5, Parser<R6> p6, Parser<R7> p7, Parser<R8> p8, Parser<R9> p9,
        Func<R1, R2, R3, R4, R5, R6, R7, R8, R9, R> combine) =>
        input => p1(input)
            .SelectMany(r1 => p2(r1.Remainder)
            .SelectMany(r2 => p3(r2.Remainder)
            .SelectMany(r3 => p4(r3.Remainder)
            .SelectMany(r4 => p5(r4.Remainder)
            .SelectMany(r5 => p6(r5.Remainder)
            .SelectMany(r6 => p7(r6.Remainder)
            .SelectMany(r7 => p8(r7.Remainder)
            .SelectMany(r8 => p9(r8.Remainder)
            .Select(r9 => new Result<R>(
                combine(r1.Value, r2.Value, r3.Value, r4.Value, r5.Value, r6.Value, r7.Value, r8.Value, r9.Value), r9.Remainder))))))))));

    // Tuple form (used where a later step needs several pieces at once).
    public static Parser<(R1, R2, R3, R4)> Seq<R1, R2, R3, R4>(Parser<R1> p1, Parser<R2> p2, Parser<R3> p3, Parser<R4> p4) =>
        Seq(p1, p2, p3, p4, (r1, r2, r3, r4) => (r1, r2, r3, r4));

    // ── repetition ───────────────────────────────────────────────────────────────

    // NOTE: yields shorter matches first (the prototype's semantics); callers
    // backtrack into longer ones when a later combinator fails.
    public static Parser<T[]> Many1<T>(Parser<T> parser)
    {
        IEnumerable<IResult<T[]>> ParseMany(Cursor input)
        {
            var stack = new Stack<IResult<T[]>>(parser.ConvertTo(p => new[] { p })(input));
            while (stack.Count > 0)
            {
                var result = stack.Pop();
                yield return result;
                foreach (var next in parser.ConvertTo(p => result.Value.Append(p).ToArray())(result.Remainder))
                    stack.Push(next);
            }
        }
        return ParseMany;
    }

    public static Parser<T[]> Many0<T>(Parser<T> parser) =>
        Many1(parser).DefaultIfEmpty(() => []);

    public static Parser<T[]> Many1Separated<T, U>(Parser<T> parser, Parser<U> separator) =>
        Seq(parser, Many0(Seq(separator, parser, (_, p) => p)),
            (first, rest) => new[] { first }.Concat(rest).ToArray());

    public static Parser<T[]> Many0Separated<T, U>(Parser<U> separator, Parser<T> parser) =>
        Many1Separated(parser, separator).DefaultIfEmpty(() => []);

    // ── choice & co ──────────────────────────────────────────────────────────────

    public static Parser<T> OneOf<T>(params Parser<T>[] parsers)
    {
        IEnumerable<IResult<T>> ParseOneOf(Cursor input)
        {
            foreach (var parser in parsers)
                foreach (var result in parser(input))
                    yield return result;
        }
        return ParseOneOf;
    }

    // Succeeds (consuming nothing) iff `parser` does NOT match here.
    public static Parser<T?> Not<T>(Parser<T> parser) =>
        input => parser(input).Any()
            ? []
            : new IResult<T?>[] { new Result<T?>(default, input) };

    public static Parser<T?> Optional<T>(Parser<T> parser) =>
        parser.DefaultIfEmpty<T?>(() => default);

    public static Parser<T> Lazy<T>(Func<Parser<T>> parser) => input => parser()(input);

    // Peek the child block's indentation (base indent + one extra Ws1 level) without
    // consuming it, then run the block parser closed over that exact indent string.
    public static Parser<T> IndentLookahead<T>(string baseIndent, Parser<string> indentation, IndentedParser<T> parser) =>
        input => Seq(Text(baseIndent), indentation, (a, b) => a + b)(input)
            .SelectMany(indentResult => parser(indentResult.Value)(input));

    // ── running ──────────────────────────────────────────────────────────────────

    // Parse the whole source: the first alternative that consumes everything wins.
    // No complete parse → a positioned error at the furthest failure point.
    public static T Run<T>(Parser<T> parser, string source)
    {
        var failure = new FailureMark();
        var start = new Cursor(source, 0, failure);
        foreach (var result in parser(start))
            if (result.Remainder.AtEnd)
                return result.Value;

        var (line, column, text) = Locate(source, failure.Offset);
        throw new CodeParseException(
            $"Parse error at line {line}, column {column}:\n{text}\n{new string(' ', column - 1)}^");
    }

    private static (int Line, int Column, string Text) Locate(string source, int offset)
    {
        offset = Math.Min(offset, source.Length);
        var lineStart = source.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        var lineEnd = source.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = source.Length;
        var line = 1 + source.AsSpan(0, lineStart).Count('\n');
        return (line, offset - lineStart + 1, source[lineStart..lineEnd].TrimEnd('\r'));
    }
}
