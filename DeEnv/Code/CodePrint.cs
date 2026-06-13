using System.Text;

namespace DeEnv.Code;

// Prints the Code AST back to canonical text (the inverse of CodeParse): four-space
// indentation, minimal parentheses by precedence, escaped text literals. The designer
// will use this to present stored code as editable text; the round-trip tests pin
// parse(print(ast)) ≡ ast.
public static class CodePrint
{
    // ── expressions ──────────────────────────────────────────────────────────────

    // Binding strength, mirroring the grammar's precedence chain. A child operand is
    // parenthesized when it binds more loosely than its position requires.
    private static int Precedence(ICodeValue v) => v switch
    {
        CodeInfixOp { Op: CodeInfixOpType.ObjectProp } => 90,
        CodeCall => 90,
        CodeInfixOp { Op: CodeInfixOpType.Multiply or CodeInfixOpType.Divide or CodeInfixOpType.Modulo } => 80,
        CodeInfixOp { Op: CodeInfixOpType.Add or CodeInfixOpType.Subtract } => 70,
        CodeInfixOp { Op: CodeInfixOpType.And } => 50,
        CodeInfixOp { Op: CodeInfixOpType.Or } => 40,
        CodeInfixOp => 60, // the comparisons
        CodeFunction or CodeAssignment => 10,
        _ => 100, // atoms
    };

    private static string OpToken(CodeInfixOpType op) => op switch
    {
        CodeInfixOpType.Add => "+",
        CodeInfixOpType.Subtract => "-",
        CodeInfixOpType.Multiply => "*",
        CodeInfixOpType.Divide => "/",
        CodeInfixOpType.Modulo => "%",
        CodeInfixOpType.Equals => "==",
        CodeInfixOpType.NotEquals => "!=",
        CodeInfixOpType.MoreThan => ">",
        CodeInfixOpType.MoreThanOrEqual => ">=",
        CodeInfixOpType.LessThan => "<",
        CodeInfixOpType.LessThanOrEqual => "<=",
        CodeInfixOpType.And => "&&",
        CodeInfixOpType.Or => "||",
        _ => throw new InvalidOperationException($"No token for {op}."),
    };

    // An inline (single-line) expression. A multi-statement lambda has no inline form —
    // it only exists in multiline positions, which the statement printer handles.
    public static string Value(ICodeValue value) => value switch
    {
        CodeInt i => i.Value.ToString(),
        CodeBool b => b.Value ? "true" : "false",
        CodeNull => "null",
        CodeText t => Quote(t.Value),
        CodeSymbol s => s.Name,
        CodeArray a => a.Items.Length == 0 ? "[]" : "[" + string.Join(", ", a.Items.Select(Value)) + "]",
        CodeObject o => o.Props.Length == 0 ? "{}"
            : "{ " + string.Join(", ", o.Props.Select(p => $"{p.Name}: {Value(p.Value)}")) + " }",
        CodeInfixOp { Op: CodeInfixOpType.ObjectProp } prop =>
            Operand(prop.Left, 90) + "." + ((CodeSymbol)prop.Right).Name,
        CodeInfixOp op =>
            Operand(op.Left, Precedence(op)) + " " + OpToken(op.Op) + " " + Operand(op.Right, Precedence(op) + 1),
        CodeCall call => Operand(call.Fn, 90) + "(" + string.Join(", ", call.Params.Select(Value)) + ")",
        CodeAssignment assign => Value(assign.Target) + " = " + Value(assign.Value),
        CodeFunction fn => InlineLambda(fn),
        _ => throw new InvalidOperationException($"No inline text form for {value.GetType().Name}."),
    };

    private static string Operand(ICodeValue v, int minPrecedence) =>
        Precedence(v) < minPrecedence ? "(" + Value(v) + ")" : Value(v);

    private static string InlineLambda(CodeFunction fn)
    {
        if (fn.Body.Statements is not [CodeReturn ret])
            throw new InvalidOperationException(
                "A multi-statement lambda has no inline form (use a named function).");
        return LambdaParams(fn) + " => " + Value(ret.Value);
    }

    // Named functions always parenthesize; a lambda's single parameter prints bare.
    private static string Params(CodeFunction fn) =>
        "(" + string.Join(", ", fn.Params.Select(p => p.Name)) + ")";

    private static string LambdaParams(CodeFunction fn) =>
        fn.Params is [var single] ? single.Name : Params(fn);

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\t' => "\\t", _ => c.ToString() });
        return sb.Append('"').ToString();
    }

    // ── statements & blocks ──────────────────────────────────────────────────────

    private const string Step = "    ";

    public static void Function(StringBuilder sb, CodeFunction fn, string indent)
    {
        sb.Append(indent);
        if (fn.ServerOnly) sb.Append("server ");
        sb.Append("fn ").Append(fn.Name).Append(Params(fn)).Append('\n');
        Block(sb, fn.Body, indent + Step);
    }

    private static void Block(StringBuilder sb, CodeBlock block, string indent)
    {
        foreach (var statement in block.Statements)
            Statement(sb, statement, indent);
    }

    private static void Statement(StringBuilder sb, ICodeStatement statement, string indent)
    {
        switch (statement)
        {
            case CodeVarDec v:
                sb.Append(indent).Append("var ").Append(v.Name);
                if (v.Value != null) sb.Append(" = ").Append(Value(v.Value));
                sb.Append('\n');
                break;
            case CodeAssignment a:
                sb.Append(indent).Append(Value(a)).Append('\n');
                break;
            case CodeCall c:
                sb.Append(indent).Append(Value(c)).Append('\n');
                break;
            case CodeReturn r:
                sb.Append(indent).Append("return ");
                ValueWithNl(sb, r.Value, indent);
                break;
            case CodeIf i:
                If(sb, i, indent);
                break;
            case CodeFunction fn:
                Function(sb, fn, indent);
                break;
            case CodeBlock b:
                Block(sb, b, indent); // only as an if-branch body; same indent
                break;
            default:
                throw new InvalidOperationException($"No text form for statement {statement.GetType().Name}.");
        }
    }

    private static void If(StringBuilder sb, CodeIf i, string indent)
    {
        sb.Append(indent).Append("if ").Append(Value(i.Condition)).Append('\n');
        Statement(sb, i.Body, indent + Step);
        switch (i.ElseBody)
        {
            case null:
                break;
            case CodeIf elseIf:
                sb.Append(indent).Append("else ");
                // Splice the nested if onto the `else ` line (else-if chain).
                var nested = new StringBuilder();
                If(nested, elseIf, indent);
                sb.Append(nested.ToString(indent.Length, nested.Length - indent.Length));
                break;
            default:
                sb.Append(indent).Append("else\n");
                Statement(sb, i.ElseBody, indent + Step);
                break;
        }
    }

    // A multiline value position (return / tag child): a tag, a multi-statement
    // lambda, or an inline expression ending the line.
    private static void ValueWithNl(StringBuilder sb, ICodeValue value, string indent)
    {
        switch (value)
        {
            case CodeTag tag:
                TagHead(sb, tag);
                TagChildren(sb, tag.Children, indent + Step);
                break;
            case CodeFunction fn when fn.Body.Statements is not [CodeReturn]:
                // A multi-statement lambda: only expressible in multiline positions.
                sb.Append(LambdaParams(fn)).Append(" =>\n");
                Block(sb, fn.Body, indent + Step);
                break;
            default:
                sb.Append(Value(value)).Append('\n');
                break;
        }
    }

    // ── tags ─────────────────────────────────────────────────────────────────────

    private static void TagHead(StringBuilder sb, CodeTag tag)
    {
        sb.Append('<').Append(tag.Name);
        foreach (var attr in tag.Attributes)
        {
            sb.Append(' ').Append(attr.Name).Append('=');
            if (attr.Value is CodeText text) sb.Append(Quote(text.Value));
            else sb.Append('{').Append(Value(attr.Value)).Append('}');
        }
        sb.Append(">\n");
    }

    private static void TagChildren(StringBuilder sb, IEnumerable<ICodeTagChild> children, string indent)
    {
        foreach (var child in children)
            TagChild(sb, child, indent);
    }

    private static void TagChild(StringBuilder sb, ICodeTagChild child, string indent)
    {
        switch (child)
        {
            case CodeTag tag:
                sb.Append(indent);
                TagHead(sb, tag);
                TagChildren(sb, tag.Children, indent + Step);
                break;
            case CodeTagForEach fe:
                sb.Append(indent).Append("foreach ").Append(fe.Item.Name).Append(" in ")
                  .Append(Value(fe.Collection)).Append('\n');
                TagChildren(sb, fe.Body, indent + Step);
                break;
            case CodeTagIf tagIf:
                TagIf(sb, tagIf, indent);
                break;
            case ICodeValue value:
                sb.Append(indent);
                ValueWithNl(sb, value, indent);
                break;
            default:
                throw new InvalidOperationException($"No text form for tag child {child.GetType().Name}.");
        }
    }

    private static void TagIf(StringBuilder sb, CodeTagIf tagIf, string indent)
    {
        sb.Append(indent).Append("if ").Append(Value(tagIf.Condition)).Append('\n');
        TagChildren(sb, tagIf.Body, indent + Step);
        if (tagIf.ElseBody.Length == 0) return;
        if (tagIf.ElseBody is [CodeTagIf elseIf])
        {
            sb.Append(indent).Append("else ");
            var nested = new StringBuilder();
            TagIf(nested, elseIf, indent);
            sb.Append(nested.ToString(indent.Length, nested.Length - indent.Length));
            return;
        }
        sb.Append(indent).Append("else\n");
        TagChildren(sb, tagIf.ElseBody, indent + Step);
    }
}
