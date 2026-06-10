namespace DeEnv.Code;

// AST node hierarchy for the filter expression language.
// JSON shape (shared contract with TypeScript):
//   { "type": "eq", "left": { "type": "field", "path": ["done"] }, "right": { "type": "literal", "value": false } }

public abstract record ExpressionNode;

public sealed record LiteralNode(object? Value) : ExpressionNode;
public sealed record FieldNode(IReadOnlyList<string> Path) : ExpressionNode;

public sealed record EqNode(ExpressionNode Left, ExpressionNode Right) : ExpressionNode;
public sealed record NeqNode(ExpressionNode Left, ExpressionNode Right) : ExpressionNode;
public sealed record GtNode(ExpressionNode Left, ExpressionNode Right) : ExpressionNode;
public sealed record LtNode(ExpressionNode Left, ExpressionNode Right) : ExpressionNode;
public sealed record GteNode(ExpressionNode Left, ExpressionNode Right) : ExpressionNode;
public sealed record LteNode(ExpressionNode Left, ExpressionNode Right) : ExpressionNode;

public sealed record AndNode(ExpressionNode Left, ExpressionNode Right) : ExpressionNode;
public sealed record OrNode(ExpressionNode Left, ExpressionNode Right) : ExpressionNode;
public sealed record NotNode(ExpressionNode Operand) : ExpressionNode;
