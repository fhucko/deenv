using System.Text.Json.Serialization;

namespace DeEnv.Code;

// The Code AST: a SolidJS-like reactive UI + logic language stored as a JSON tree.
// Ported and adapted from the app15 prototype. JSON-polymorphic with a "type"
// discriminator so a hand-written AST in instance.schema.json round-trips through
// System.Text.Json. Grouped into one file (mirrors Storage/NodeValue.cs).
//
// Three node families:
//   ICodeStatement — statements in a block / function body
//   ICodeValue     — expressions that produce a value (also valid as tag children)
//   ICodeTagChild  — anything that can sit inside a UI tag (values + tag control flow)

public interface ICodeElement;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CodeBlock), "block")]
[JsonDerivedType(typeof(CodeFunction), "fn")]
[JsonDerivedType(typeof(CodeReturn), "return")]
[JsonDerivedType(typeof(CodeVarDec), "varDec")]
[JsonDerivedType(typeof(CodeAssignment), "assign")]
[JsonDerivedType(typeof(CodeCall), "call")]
[JsonDerivedType(typeof(CodeIf), "if")]
[JsonDerivedType(typeof(CodeAmbient), "ambient")]
public interface ICodeStatement : ICodeElement;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CodeFunction), "fn")]
[JsonDerivedType(typeof(CodeTag), "tag")]
[JsonDerivedType(typeof(CodeObject), "object")]
[JsonDerivedType(typeof(CodeBool), "bool")]
[JsonDerivedType(typeof(CodeText), "text")]
[JsonDerivedType(typeof(CodeInfixOp), "infixOp")]
[JsonDerivedType(typeof(CodeSymbol), "symbol")]
[JsonDerivedType(typeof(CodeInt), "int")]
[JsonDerivedType(typeof(CodeCall), "call")]
[JsonDerivedType(typeof(CodeArray), "array")]
[JsonDerivedType(typeof(CodeNull), "null")]
[JsonDerivedType(typeof(CodeAssignment), "assign")]
public interface ICodeValue : ICodeTagChild;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CodeFunction), "fn")]
[JsonDerivedType(typeof(CodeTag), "tag")]
[JsonDerivedType(typeof(CodeObject), "object")]
[JsonDerivedType(typeof(CodeBool), "bool")]
[JsonDerivedType(typeof(CodeText), "text")]
[JsonDerivedType(typeof(CodeInfixOp), "infixOp")]
[JsonDerivedType(typeof(CodeSymbol), "symbol")]
[JsonDerivedType(typeof(CodeInt), "int")]
[JsonDerivedType(typeof(CodeCall), "call")]
[JsonDerivedType(typeof(CodeArray), "array")]
[JsonDerivedType(typeof(CodeNull), "null")]
[JsonDerivedType(typeof(CodeTagForEach), "foreach")]
[JsonDerivedType(typeof(CodeTagIf), "if")]
public interface ICodeTagChild : ICodeElement;

// ── statements ────────────────────────────────────────────────────────────────

public sealed class CodeBlock : ICodeStatement
{
    public required ICodeStatement[] Statements { get; set; }
}

public sealed class CodeFunction : ICodeStatement, ICodeValue
{
    public string? Name { get; set; }
    public required CodeFunctionParam[] Params { get; set; }
    public required CodeBlock Body { get; set; }
    public int Id { get; set; }

    // Marks a function that runs only on the server (never shipped to the client).
    // The missing-value path forces such a function server-side. Used Stage 4+ for
    // secret logic (e.g. password hashing); parsed/validated now.
    public bool ServerOnly { get; set; }
}

public sealed class CodeFunctionParam
{
    public required string Name { get; set; }
}

public sealed class CodeReturn : ICodeStatement
{
    public required ICodeValue Value { get; set; }
}

public sealed class CodeVarDec : ICodeStatement
{
    public required string Name { get; set; }
    public ICodeValue? Value { get; set; }
}

// `ambient name = value` — provide/override an ambient (dynamic-scope) var for the rest of
// the enclosing block and its callees. Resolved on a symbol read after a lexical miss, and
// popped when the block exits. Consume is implicit (just read the name).
public sealed class CodeAmbient : ICodeStatement
{
    public required string Name { get; set; }
    public required ICodeValue Value { get; set; }
}

public sealed class CodeAssignment : ICodeStatement, ICodeValue
{
    // A symbol (a var) or an object-prop access (`obj.field`) — the lvalue. A prop
    // lvalue writes through the same path as two-way binding (set + invalidate, persist
    // when the object is server-backed).
    public required ICodeValue Target { get; set; }
    public required ICodeValue Value { get; set; }
}

public sealed class CodeCall : ICodeStatement, ICodeValue
{
    public required ICodeValue Fn { get; set; }
    public required ICodeValue[] Params { get; set; }
}

public sealed class CodeIf : ICodeStatement
{
    public required ICodeValue Condition { get; set; }
    public required ICodeStatement Body { get; set; }
    public ICodeStatement? ElseBody { get; set; }
}

// ── values ──────────────────────────────────────────────────────────────────────

public sealed class CodeSymbol : ICodeValue
{
    public required string Name { get; set; }
}

public sealed class CodeInt : ICodeValue
{
    public required int Value { get; set; }
}

public sealed class CodeBool : ICodeValue
{
    public required bool Value { get; set; }
}

public sealed class CodeText : ICodeValue
{
    public required string Value { get; set; }
}

public sealed class CodeNull : ICodeValue;

public sealed class CodeArray : ICodeValue
{
    public required ICodeValue[] Items { get; set; }
}

public sealed class CodeObject : ICodeValue
{
    public required CodeObjectProp[] Props { get; set; }
}

public sealed class CodeObjectProp
{
    public required string Name { get; set; }
    public required ICodeValue Value { get; set; }
}

public sealed class CodeInfixOp : ICodeValue
{
    public required CodeInfixOpType Op { get; set; }
    public required ICodeValue Left { get; set; }
    public required ICodeValue Right { get; set; }
}

public enum CodeInfixOpType
{
    Add = 1,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Equals,
    NotEquals,
    MoreThan,
    MoreThanOrEqual,
    LessThan,
    LessThanOrEqual,
    And,
    Or,
    ObjectProp,
}

// ── UI tags ───────────────────────────────────────────────────────────────────

public sealed class CodeTag : ICodeValue
{
    public required string Name { get; set; }
    public required CodeTagAttribute[] Attributes { get; set; }
    public required ICodeTagChild[] Children { get; set; }
}

public sealed class CodeTagAttribute
{
    public required string Name { get; set; }
    public required ICodeValue Value { get; set; }
}

public sealed class CodeTagForEach : ICodeTagChild
{
    public required CodeSymbol Item { get; set; }
    public required ICodeValue Collection { get; set; }
    public required ICodeTagChild[] Body { get; set; }
}

public sealed class CodeTagIf : ICodeTagChild
{
    public required ICodeValue Condition { get; set; }
    public required ICodeTagChild[] Body { get; set; }
    public ICodeTagChild[] ElseBody { get; set; } = [];
}
