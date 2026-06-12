using System.Text;
using DeEnv.Code.Parsing;
using static DeEnv.Code.Parsing.Parse;

namespace DeEnv.Code;

// The Code text grammar: app.txt-style source → the Code AST (the same AST the JSON
// form declares — CodeValidator, CodeIds, both interpreters and the wire are unchanged).
// Ported from the app14 prototype's CodeParse and adapted to this AST, with four fixes
// the prototype lacked: `0` is a valid int literal, text literals support escapes
// (\" \\ \n \t), parenthesized expressions exist, and postfix chaining is a real layer
// (so `db.tasks.where(p).orderBy(k)` and `((n) => n + 1)(41)` parse).
//
// Expression precedence (loosest binds last):
//   postfix (.member, (args))  →  * / %  →  + -  →  comparisons  →  &&  →  ||
public static class CodeParse
{
    public static readonly string[] Keywords =
        ["fn", "var", "if", "else", "foreach", "in", "return", "true", "false", "null", "common", "ui", "server"];

    // ── literals & atoms ─────────────────────────────────────────────────────────

    public static Parser<CodeSymbol> Symbol => Regex("[a-zA-Z_][a-zA-Z_0-9]*")
        .Filter(p => !Keywords.Contains(p))
        .ConvertTo(p => new CodeSymbol { Name = p });

    public static Parser<CodeInt> Int => Regex("-?(0|[1-9][0-9]*)")
        .ConvertTo(p => new CodeInt { Value = int.Parse(p) });

    public static Parser<CodeBool> Bool => OneOf(
        Text("true").ConvertTo(_ => new CodeBool { Value = true }),
        Text("false").ConvertTo(_ => new CodeBool { Value = false }));

    public static Parser<CodeNull> Null => Text("null").ConvertTo(_ => new CodeNull());

    public static Parser<CodeText> TextLiteral => Seq(
        Text("\""), Regex(@"([^""\\\r\n]|\\.)*"), Text("\""),
        (_, body, _) => new CodeText { Value = Unescape(body) });

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            i++;
            sb.Append(s[i] switch
            {
                'n' => '\n',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => throw new CodeParseException($"Unknown escape '\\{s[i]}' in a text literal."),
            });
        }
        return sb.ToString();
    }

    public static Parser<CodeArray> ArrayLiteral => Seq(
        Text("["), Ws0,
        Many0Separated(Text(","), Seq(Ws0, Value, Ws0, (_, v, _) => v)),
        Text("]"),
        (_, _, items, _) => new CodeArray { Items = items });

    public static Parser<CodeObjectProp[]> ObjectProps => Many0Separated(Text(","),
        Seq(Ws0, Symbol, Ws0, Text(":"), Ws0, Value, Ws0,
            (_, name, _, _, _, value, _) => new CodeObjectProp { Name = name.Name, Value = value }));

    public static Parser<CodeObject> ObjectLiteral => Seq(
        Text("{"), Ws0, ObjectProps, Text("}"),
        (_, _, props, _) => new CodeObject { Props = props });

    public static Parser<ICodeValue> Parens => Seq(
        Text("("), Ws0, Value, Ws0, Text(")"),
        (_, _, value, _, _) => value);

    public static Parser<ICodeValue> Primary => Lazy(() => OneOf<ICodeValue>(
        TextLiteral, Int, Bool, Null, Symbol, ArrayLiteral, ObjectLiteral, Parens));

    // ── postfix: member access and calls, chainable ──────────────────────────────

    public static Parser<ICodeValue[]> CallParams => Seq(
        Text("("),
        Many0Separated(Text(","), Seq(Ws0, Value, Ws0, (_, v, _) => v)),
        Text(")"),
        (_, args, _) => args);

    private static Parser<Func<ICodeValue, ICodeValue>> PostfixOp => OneOf(
        Seq(Ws0, Text("."), Ws0, Symbol, (_, _, _, member) => (Func<ICodeValue, ICodeValue>)(left =>
            new CodeInfixOp { Op = CodeInfixOpType.ObjectProp, Left = left, Right = member })),
        Seq(Ws0, CallParams, (_, args) => (Func<ICodeValue, ICodeValue>)(left =>
            new CodeCall { Fn = left, Params = args })));

    public static Parser<ICodeValue> Postfix => Seq(Primary, Many0(PostfixOp),
        (left, ops) => ops.Aggregate(left, (value, apply) => apply(value)));

    // ── binary operators, by precedence ──────────────────────────────────────────

    private static Parser<ICodeValue> InfixLevel(Parser<ICodeValue> operand, Parser<CodeInfixOpType> op) =>
        Seq(operand, Many0(Seq(Ws0, op, Ws0, operand)),
            (left, rest) => rest.Aggregate(left, (l, r) =>
                (ICodeValue)new CodeInfixOp { Op = r.Item2, Left = l, Right = r.Item4 }));

    public static Parser<ICodeValue> MultiplyDivide => InfixLevel(Postfix, OneOf(
        Text("*").ConvertTo(_ => CodeInfixOpType.Multiply),
        Text("/").ConvertTo(_ => CodeInfixOpType.Divide),
        Text("%").ConvertTo(_ => CodeInfixOpType.Modulo)));

    public static Parser<ICodeValue> AddSubtract => InfixLevel(MultiplyDivide, OneOf(
        Text("+").ConvertTo(_ => CodeInfixOpType.Add),
        Text("-").ConvertTo(_ => CodeInfixOpType.Subtract)));

    public static Parser<ICodeValue> Comparison => InfixLevel(AddSubtract, OneOf(
        Text("==").ConvertTo(_ => CodeInfixOpType.Equals),
        Text("!=").ConvertTo(_ => CodeInfixOpType.NotEquals),
        Text(">=").ConvertTo(_ => CodeInfixOpType.MoreThanOrEqual),
        Text(">").ConvertTo(_ => CodeInfixOpType.MoreThan),
        Text("<=").ConvertTo(_ => CodeInfixOpType.LessThanOrEqual),
        Text("<").ConvertTo(_ => CodeInfixOpType.LessThan)));

    public static Parser<ICodeValue> And => InfixLevel(Comparison,
        Text("&&").ConvertTo(_ => CodeInfixOpType.And));

    public static Parser<ICodeValue> Or => InfixLevel(And,
        Text("||").ConvertTo(_ => CodeInfixOpType.Or));

    // ── lambdas & assignment as a value ──────────────────────────────────────────

    public static Parser<CodeFunctionParam[]> FunctionParams => Seq(
        Text("("),
        Many0Separated(Text(","), Seq(Ws0, Symbol, Ws0, (_, p, _) => p)),
        Text(")"),
        (_, names, _) => names.Select(p => new CodeFunctionParam { Name = p.Name }).ToArray());

    // (x) => expr — sugar for a one-statement body returning the expression.
    public static Parser<CodeFunction> InlineLambda => Seq(
        FunctionParams, Ws0, Text("=>"), Ws0, Value,
        (parameters, _, _, _, body) => new CodeFunction
        {
            Name = null,
            Params = parameters,
            Body = new CodeBlock { Statements = [new CodeReturn { Value = body }] },
        });

    public static Parser<CodeAssignment> AssignValue => Seq(
        Symbol, Ws0, Text("="), Ws0, Value,
        (target, _, _, _, value) => new CodeAssignment { Target = target, Value = value });

    // ── the expression entry point ───────────────────────────────────────────────

    public static Parser<ICodeValue> Value => Lazy(() => OneOf<ICodeValue>(
        Or,           // the full precedence chain (bottoms out at Primary/Postfix)
        InlineLambda,
        AssignValue));

    // Parse a single expression that must consume the whole text.
    public static ICodeValue ParseExpression(string text) => Run(Value, text);
}
