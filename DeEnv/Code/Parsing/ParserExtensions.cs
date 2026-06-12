using static DeEnv.Code.Parsing.Parse;

namespace DeEnv.Code.Parsing;

public static class ParserExtensions
{
    public static Parser<U> ConvertTo<T, U>(this Parser<T> parse, Func<T, U> convert) =>
        input => parse(input)
            .Select(result => (IResult<U>)new Result<U>(convert(result.Value), result.Remainder));

    public static Parser<T> DefaultIfEmpty<T>(this Parser<T> parse, Func<T> createDefault)
    {
        IEnumerable<IResult<T>> ParseWithDefault(Cursor input)
        {
            var yielded = false;
            foreach (var result in parse(input))
            {
                yield return result;
                yielded = true;
            }
            if (!yielded)
                yield return new Result<T>(createDefault(), input);
        }
        return ParseWithDefault;
    }

    public static Parser<T> Filter<T>(this Parser<T> parse, Func<T, bool> predicate) =>
        input => parse(input).Where(result => predicate(result.Value));

    public static Parser<T> SkipEmptyLinesBefore<T>(this Parser<T> parse) =>
        Seq(Many0(Seq(Ws0, Nl, (_, nl) => nl)), parse, (_, p) => p);

    public static Parser<T> SkipEmptyLinesAfter<T>(this Parser<T> parse) =>
        Seq(parse, Many0(Seq(Ws0, Nl, (_, nl) => nl)), (p, _) => p);
}
