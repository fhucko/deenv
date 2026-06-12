namespace DeEnv.Code.Parsing;

// The parsing core, ported from the app14 prototype (DeEnv/Parsing) with two upgrades:
//
//   • An offset CURSOR instead of substring slicing — the prototype re-allocated the
//     remaining input at every step (O(n²) garbage); the cursor is a (source, offset)
//     pair over one shared string.
//   • A failure HIGH-WATER MARK — the prototype's failed parse yielded nothing, with no
//     position. Every primitive records the furthest offset it failed at, so a failed
//     parse reports "line N, column M" with the offending line.
//
// A parser is a function from a cursor to a lazy sequence of results (each a value +
// the cursor after it). Multiple results = local ambiguity; callers backtrack through
// alternatives until the whole input is consumed (see Parse.Run).

public delegate IEnumerable<IResult<T>> Parser<out T>(Cursor input);

// An indentation-aware parser: closed over the exact indent string its block lives at.
public delegate Parser<T> IndentedParser<T>(string indent);

public readonly struct Cursor(string source, int offset, FailureMark failure)
{
    public string Source { get; } = source;
    public int Offset { get; } = offset;
    public FailureMark Failure { get; } = failure;

    public bool AtEnd => Offset >= Source.Length;
    public Cursor Advance(int by) => new(Source, Offset + by, Failure);

    // A primitive failed here: remember the furthest failure for the error message.
    public void MarkFailure() => Failure.Record(Offset);
}

// Shared mutable box: the furthest offset any primitive failed at during one parse.
public sealed class FailureMark
{
    public int Offset { get; private set; }
    public void Record(int offset) { if (offset > Offset) Offset = offset; }
}

// Covariant result interface so Parser<out T> stays covariant (a Parser<CodeTag> is a
// Parser<ICodeValue>); Result is the single implementation.
public interface IResult<out T>
{
    T Value { get; }
    Cursor Remainder { get; }
}

public sealed class Result<T>(T value, Cursor remainder) : IResult<T>
{
    public T Value { get; } = value;
    public Cursor Remainder { get; } = remainder;
}

// A text → AST failure, positioned. Raised by Parse.Run; the loader surfaces it as a
// schema validation error (a broken code file fails the load, like a broken document).
public sealed class CodeParseException(string message) : Exception(message);
