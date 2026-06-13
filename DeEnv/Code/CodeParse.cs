using System.Text;
using DeEnv.Code.Parsing;
using DeEnv.Instance;
using static DeEnv.Code.Parsing.Parse;

namespace DeEnv.Code;

// The Code text grammar: app.txt-style source → the Code AST (the same AST the JSON
// form declares — CodeValidator, CodeIds, both interpreters and the wire are unchanged).
// Ported from the app14 prototype's CodeParse and adapted to this AST, with four fixes
// the prototype lacked: `0` is a valid int literal, text literals support escapes
// (\" \\ \n \t), parenthesized expressions exist, and postfix chaining is a real layer
// (so `db.tasks.where(p).orderBy(k)` and `(n => n + 1)(41)` parse).
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

    // A lambda's parameter list: a single parameter needs no parentheses
    // (`x => x.done`); zero or many keep them (`() => …`, `(a, b) => …`).
    public static Parser<CodeFunctionParam[]> LambdaParams => OneOf(
        FunctionParams,
        Symbol.ConvertTo(s => new[] { new CodeFunctionParam { Name = s.Name } }));

    // x => expr — sugar for a one-statement body returning the expression.
    public static Parser<CodeFunction> InlineLambda => Seq(
        LambdaParams, Ws0, Text("=>"), Ws0, Value,
        (parameters, _, _, _, body) => new CodeFunction
        {
            Name = null,
            Params = parameters,
            Body = new CodeBlock { Statements = [new CodeReturn { Value = body }] },
        });

    // An assignment lvalue: a bare symbol (a var) or a `.member` chain (`obj.field`).
    // Calls are not lvalues, so this is symbol + dotted members only.
    public static Parser<ICodeValue> Lvalue => Seq(
        Symbol, Many0(Seq(Ws0, Text("."), Ws0, Symbol, (_, _, _, m) => m)),
        (head, members) => members.Aggregate((ICodeValue)head,
            (left, m) => new CodeInfixOp { Op = CodeInfixOpType.ObjectProp, Left = left, Right = m }));

    public static Parser<CodeAssignment> AssignValue => Seq(
        Lvalue, Ws0, Text("="), Ws0, Value,
        (target, _, _, _, value) => new CodeAssignment { Target = target, Value = value });

    // ── the expression entry point ───────────────────────────────────────────────

    public static Parser<ICodeValue> Value => Lazy(() => OneOf<ICodeValue>(
        Or,           // the full precedence chain (bottoms out at Primary/Postfix)
        InlineLambda,
        AssignValue));

    // Parse a single expression that must consume the whole text.
    public static ICodeValue ParseExpression(string text) => Run(Value, text);

    // ── statements & indentation blocks ──────────────────────────────────────────
    // A block's indent is discovered from its first line (IndentLookahead) and every
    // line of the block must sit at exactly that indent; a shallower line ends it.

    public static IndentedParser<CodeAssignment> AssignStatement => _ =>
        Seq(AssignValue, NlOrEnd, (assign, _) => assign);

    public static IndentedParser<CodeReturn> Return => indent =>
        Seq(Text("return"), Ws1, ValueWithNl(indent), (_, _, value) => new CodeReturn { Value = value });

    public static IndentedParser<CodeVarDec> VarDec => _ =>
        Seq(Text("var"), Ws1, Symbol,
            Optional(Seq(Ws0, Text("="), Ws0, Value, (_, _, _, v) => v)), NlOrEnd,
            (_, _, name, value, _) => new CodeVarDec { Name = name.Name, Value = value });

    // A named function: `fn name(params)` + an indented body. `server fn` marks it
    // server-only (never shipped to the client).
    public static IndentedParser<CodeFunction> NamedFunction => indent =>
        Seq(Optional(Seq(Text("server"), Ws1, (s, _) => s)),
            Text("fn"), Ws1, Symbol, Ws0, FunctionParams, NlOrEnd, IndentedBlock(indent),
            (serverOnly, _, _, name, _, parameters, _, body) => new CodeFunction
            {
                Name = name.Name,
                Params = parameters,
                Body = body,
                ServerOnly = serverOnly != null,
            });

    public static IndentedParser<CodeCall> CallStatement => _ =>
        Seq(Postfix.Filter(v => v is CodeCall), NlOrEnd, (call, _) => (CodeCall)call);

    public static IndentedParser<CodeIf> If => indent =>
        Seq(Text("if"), Ws1, Value, NlOrEnd, IndentedBlock(indent),
            Optional(OneOf<ICodeStatement>(
                Seq(Text(indent), Text("else"), Ws1, Lazy(() => If(indent)), (_, _, _, nested) => nested),
                Seq(Text(indent), Text("else"), NlOrEnd, IndentedBlock(indent), (_, _, _, body) => body))),
            (_, _, condition, _, body, elseBody) => new CodeIf
            {
                Condition = condition,
                Body = body,
                ElseBody = elseBody,
            });

    public static IndentedParser<ICodeStatement> Statement => indent =>
        Seq(Text(indent), OneOf<ICodeStatement>(
            AssignStatement(indent),
            If(indent),
            Return(indent),
            NamedFunction(indent),
            CallStatement(indent),
            VarDec(indent)),
            (_, statement) => statement);

    public static IndentedParser<CodeBlock> Block => indent =>
        Many1(Statement(indent).SkipEmptyLinesBefore())
            .ConvertTo(statements => new CodeBlock { Statements = statements });

    public static IndentedParser<CodeBlock> IndentedBlock => indent =>
        IndentLookahead(indent, Ws1, Block).SkipEmptyLinesBefore();

    // A multiline value position (a return / tag child): an inline value ending the
    // line, a multiline tag, or a multiline lambda.
    public static IndentedParser<ICodeValue> ValueWithNl => indent => Lazy(() => OneOf<ICodeValue>(
        Seq(Value, NlOrEnd, (value, _) => value),
        TagMultiline(indent),
        MultilineLambda(indent)));

    // x => / (a, b) => with an indented statement body.
    public static IndentedParser<CodeFunction> MultilineLambda => indent =>
        Seq(LambdaParams, Ws0, Text("=>"), NlOrEnd, IndentedBlock(indent),
            (parameters, _, _, _, body) => new CodeFunction
            {
                Name = null,
                Params = parameters,
                Body = body,
            });

    // ── tags (JSX-like; children are an indented block, no closing tag) ──────────

    public static IndentedParser<CodeTagAttribute> TagAttribute => _ =>
        Seq(Ws1, Symbol, Ws0, Text("="), Ws0,
            OneOf<ICodeValue>(
                Seq(Text("{"), Ws0, Value, Ws0, Text("}"), (_, _, value, _, _) => value),
                TextLiteral),
            (_, name, _, _, _, value) => new CodeTagAttribute { Name = name.Name, Value = value });

    public static IndentedParser<CodeTag> TagMultiline => indent =>
        Seq(Text("<"), Symbol, Many0(TagAttribute(indent)), Ws0, Text(">"), NlOrEnd,
            Optional(IndentedTagChildren(indent)),
            (_, name, attributes, _, _, _, children) => new CodeTag
            {
                Name = name.Name,
                Attributes = attributes,
                Children = children ?? [],
            });

    public static IndentedParser<CodeTagIf> TagIf => indent =>
        Seq(Text("if"), Ws1, Value, NlOrEnd, IndentedTagChildren(indent),
            Optional(OneOf(
                Seq(Text(indent), Text("else"), Ws1, Lazy(() => TagIf(indent)),
                    (_, _, _, nested) => new ICodeTagChild[] { nested }),
                Seq(Text(indent), Text("else"), NlOrEnd, IndentedTagChildren(indent),
                    (_, _, _, body) => body))),
            (_, _, condition, _, body, elseBody) => new CodeTagIf
            {
                Condition = condition,
                Body = body,
                ElseBody = elseBody ?? [],
            });

    public static IndentedParser<CodeTagForEach> TagForEach => indent =>
        Seq(Text("foreach"), Ws1, Symbol, Ws1, Text("in"), Ws1, Value, NlOrEnd, IndentedTagChildren(indent),
            (_, _, item, _, _, _, collection, _, body) => new CodeTagForEach
            {
                Item = item,
                Collection = collection,
                Body = body,
            });

    public static IndentedParser<ICodeTagChild> TagChild => indent =>
        Seq(Text(indent), OneOf<ICodeTagChild>(
            TagIf(indent),
            TagForEach(indent),
            ValueWithNl(indent)),
            (_, child) => child);

    public static IndentedParser<ICodeTagChild[]> IndentedTagChildren => indent =>
        IndentLookahead(indent, Ws1, i => Many1(TagChild(i).SkipEmptyLinesBefore()))
            .SkipEmptyLinesBefore();

    // ── the document: `common` + `ui` sections ──────────────────────────────────
    // Top-level items are named functions, (in ui) vars, and (in ui) views;
    // `fn render()` is the optional whole-app render fn (the root path view).

    // A view declaration riding through SectionItems until MapUi consumes it. Never
    // serialized (not in the ICodeStatement JsonDerivedType list) — it exists only
    // between the parser and the section mapping. `view` is a CONTEXTUAL keyword:
    // it introduces a section item but stays usable as an ordinary identifier.
    internal sealed class CodeViewDec : ICodeStatement
    {
        public string? Type { get; init; }
        public string? Path { get; init; }
        public required CodeFunction Fn { get; init; }
    }

    // The `generic` opt-in marker: a ui section item, consumed by MapUi into
    // InstanceUi.Generic. A CONTEXTUAL keyword (still usable as an identifier).
    internal sealed class CodeGenericMarker : ICodeStatement;

    // `generic` on its own line. Backtracks (so `genericFoo`, `generic = …` fall through
    // to the function/var parsers) because NlOrEnd must follow immediately.
    public static IndentedParser<ICodeStatement> GenericMarker => _ =>
        Seq(Text("generic"), NlOrEnd, (_, _) => (ICodeStatement)new CodeGenericMarker());

    // `view Customer(customer)` (type target) / `view "/reports"(path)` (path target).
    public static IndentedParser<ICodeStatement> ViewDec => indent =>
        Seq(Text("view"), Ws1,
            OneOf<object>(Symbol, TextLiteral),
            Ws0, FunctionParams, NlOrEnd, IndentedBlock(indent),
            (_, _, target, _, parameters, _, body) => (ICodeStatement)new CodeViewDec
            {
                Type = (target as CodeSymbol)?.Name,
                Path = (target as CodeText)?.Value,
                Fn = new CodeFunction { Name = null, Params = parameters, Body = body },
            });

    // SkipEmptyLinesBefore on the LOOKAHEAD itself: a blank line between the section
    // header and its first item (the printer's canonical spacing) must not break the
    // indent probe.
    public static Parser<ICodeStatement[]> SectionItems => IndentLookahead("", Ws1,
        indent => Many1(
            Seq(Text(indent), OneOf<ICodeStatement>(
                ViewDec(indent),
                GenericMarker(indent),
                NamedFunction(indent),
                VarDec(indent)),
                (_, item) => item)
            .SkipEmptyLinesBefore()))
        .SkipEmptyLinesBefore();

    // Public: the app document (AppParse) composes these sections after its own
    // `types`/`initialData` sections.
    public static Parser<ICodeStatement[]> Section(string keyword) =>
        Seq(Text(keyword), NlOrEnd, SectionItems, (_, _, items) => items)
            .SkipEmptyLinesBefore();

    // Raw sections only — mapping (and its errors) happens AFTER Run has chosen the
    // parse that consumes the whole input. Mapping inside the combine would throw on
    // partial candidates mid-backtracking (Many1 yields shorter matches first).
    private static Parser<(ICodeStatement[]? Common, ICodeStatement[] Ui)> Document =>
        Seq(Optional(Section("common")), Section("ui"), (common, ui) => (common, ui))
            .SkipEmptyLinesAfter();

    // Parse a whole code file into the sections the schema's JSON form used to carry.
    public static (InstanceCommon? Common, InstanceUi Ui) ParseDocument(string source)
    {
        var (common, ui) = Run(Seq(Document, Ws0, (doc, _) => doc), source);
        return (MapCommon(common), MapUi(ui));
    }

    public static InstanceCommon? MapCommon(ICodeStatement[]? items)
    {
        if (items == null) return null;
        var functions = new List<CodeFunction>();
        foreach (var item in items)
            functions.Add(item as CodeFunction
                ?? throw new CodeParseException("The 'common' section may only contain functions."));
        return new InstanceCommon(functions);
    }

    public static InstanceUi MapUi(ICodeStatement[] items)
    {
        var vars = new List<UiVar>();
        var functions = new List<CodeFunction>();
        var views = new List<UiView>();
        CodeFunction? render = null;
        var generic = false;
        foreach (var item in items)
            switch (item)
            {
                case CodeVarDec v:
                    vars.Add(new UiVar(v.Name, v.Value));
                    break;
                case CodeFunction { Name: "render" } fn:
                    render = fn;
                    break;
                case CodeFunction fn:
                    functions.Add(fn);
                    break;
                case CodeViewDec view:
                    views.Add(new UiView(view.Type, view.Path, view.Fn));
                    break;
                case CodeGenericMarker:
                    generic = true;
                    break;
            }
        // render is optional: with only views (or the `generic` opt-in), the app
        // customizes parts of the generic UI. (A ui with none is rejected by CodeValidator.)
        return new InstanceUi(vars, functions, render, views, generic);
    }
}
