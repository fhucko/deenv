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
        ["fn", "var", "if", "else", "foreach", "in", "return", "true", "false", "null", "common", "ui", "server", "ambient"];

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

    // Unary prefix `!` (logical NOT). Binds tighter than the binary operators but looser than
    // postfix member/call, so `!a.b` is `!(a.b)` and `!a && b` is `(!a) && b`. Recursive: `!!x`.
    public static Parser<ICodeValue> Unary => OneOf(
        Seq(Text("!"), Ws0, Lazy(() => Unary), (_, _, operand) => (ICodeValue)new CodeNot { Operand = operand }),
        Postfix);

    public static Parser<ICodeValue> MultiplyDivide => InfixLevel(Unary, OneOf(
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

    // ── ternary (the lowest-precedence expression form) ──────────────────────────
    // `cond ? then : else`: try the full binary chain (Or), then optionally a `? value : value`
    // tail. Right-associative — the branches are full Values, so `a ? b : c ? d : e` nests the
    // trailing ternary into the else. `?` and `:` appear nowhere else in expression syntax, so
    // there is no ambiguity to resolve. When the tail is absent this is just `Or` (falls through).
    public static Parser<ICodeValue> Ternary => Lazy(() => Seq(
        Or,
        Optional(Seq(Ws0, Text("?"), Ws0, Value, Ws0, Text(":"), Ws0, Value,
            (_, _, _, then, _, _, _, els) => (ICodeValue)new CodeTernary { Condition = default!, Then = then, Else = els })),
        (cond, tail) => tail is CodeTernary t
            ? new CodeTernary { Condition = cond, Then = t.Then, Else = t.Else }
            : cond));

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

    // Block lambda: `(params) => { stmt; stmt }` — multiple statements inline, ";"-separated.
    // The primary use is a JSX `onClick={() => { a(); b() }}` that fires several effects. Only
    // call and assignment statements are allowed in a block (control flow needs the multiline
    // form); the body is a CodeBlock with no return, exactly like a multi-statement named fn.
    public static Parser<CodeFunction> BlockLambda => Lazy(() => Seq(
        LambdaParams, Ws0, Text("=>"), Ws0, Text("{"), Ws0,
        Many0Separated(Seq(Ws0, Text(";"), Ws0, (_, _, _) => 0),
            OneOf<ICodeStatement>(
                AssignValue.ConvertTo(a => (ICodeStatement)a),
                Postfix.Filter(v => v is CodeCall).ConvertTo(v => (ICodeStatement)(CodeCall)v))),
        Ws0, Text("}"),
        (parms, _, _, _, _, _, stmts, _, _) => new CodeFunction
        {
            Name = null,
            Params = parms,
            Body = new CodeBlock { Statements = stmts },
        }));

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
        Ternary,      // cond ? then : else — falls through to Or (the full precedence chain) if no `?`
        BlockLambda,  // (params) => { stmt; stmt } — tried before InlineLambda (which has no `{` body)
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

    // `ambient name = value` — provide/override an ambient (dynamic-scope) var for the rest of the
    // enclosing block. Value is required; consuming it is implicit (just read the name).
    public static IndentedParser<CodeAmbient> Ambient => _ =>
        Seq(Text("ambient"), Ws1, Symbol, Ws0, Text("="), Ws0, Value, NlOrEnd,
            (_, _, name, _, _, _, value, _) => new CodeAmbient { Name = name.Name, Value = value });

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
            VarDec(indent),
            Ambient(indent)),
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
    // Top-level items are named functions and (in ui) vars; `fn render()` is the
    // optional whole-app render fn (fully-custom UI). Without it the self-hosted generic
    // UI is the default — there is no partial-customization `view` feature (dropped; the
    // auto UI is a library the custom render will compose instead).

    // SkipEmptyLinesBefore on the LOOKAHEAD itself: a blank line between the section
    // header and its first item (the printer's canonical spacing) must not break the
    // indent probe.
    public static Parser<ICodeStatement[]> SectionItems => IndentLookahead("", Ws1,
        indent => Many1(
            Seq(Text(indent), OneOf<ICodeStatement>(
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

    // Parse a standalone `ui` section — its keyword line plus indented body (e.g.
    // "ui\n    fn render()\n        …"), tolerating trailing whitespace. Uses the SAME Section("ui") + MapUi
    // the whole-document parser uses, so a section parsed on its own is identical to one parsed in-document.
    // The inverse of AppPrint.PrintUi; used to canonicalize a designer's verbatim `ui` field before it is
    // assembled into an app document (M12 S0). Throws CodeParseException on an unparseable section.
    public static InstanceUi ParseUiSection(string source) =>
        MapUi(Run(Seq(Section("ui"), Ws0, (items, _) => items), source));

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
        CodeFunction? render = null;
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
            }
        // An app is fully custom (`fn render()`) or fully auto (the self-hosted generic UI,
        // the default when there is no render). The synthesized generic render is supplied at
        // render time by GenericUi.Effective when there is no custom render, never here.
        return new InstanceUi(vars, functions, render);
    }
}
