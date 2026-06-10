using DeEnv.Storage;

namespace DeEnv.Code;

// Evaluates a filter expression against an ObjectValue member from a set.
// Returns true if the member matches the predicate, false otherwise.
//
// Comparing against null always returns false.
// Reference traversal loads the target object from the store; a null TargetId stops and returns null.
public static class ExpressionEvaluator
{
    public static bool Evaluate(ExpressionNode node, ObjectValue member, IInstanceStore store) =>
        EvaluateValue(node, member, store) is true;

    // Returns null when the expression evaluates to a non-boolean value.
    public static bool? EvaluateAsBool(ExpressionNode node, ObjectValue member, IInstanceStore store)
    {
        var result = EvaluateValue(node, member, store);
        return result is bool b ? b : (bool?)null;
    }

    private static object? EvaluateValue(ExpressionNode node, ObjectValue member, IInstanceStore store)
    {
        switch (node)
        {
            case LiteralNode lit:
                return lit.Value;

            case FieldNode field:
                return WalkPath(field.Path, member, store);

            case EqNode eq:
            {
                var l = EvaluateValue(eq.Left, member, store);
                var r = EvaluateValue(eq.Right, member, store);
                return l != null && r != null && Equals(l, r) ? (object)true : false;
            }
            case NeqNode neq:
            {
                var l = EvaluateValue(neq.Left, member, store);
                var r = EvaluateValue(neq.Right, member, store);
                return l != null && r != null && !Equals(l, r) ? (object)true : false;
            }
            case GtNode gt:
            {
                var c = OrdCompare(gt.Left, gt.Right, member, store);
                return c.HasValue && c.Value > 0 ? (object)true : false;
            }
            case LtNode lt:
            {
                var c = OrdCompare(lt.Left, lt.Right, member, store);
                return c.HasValue && c.Value < 0 ? (object)true : false;
            }
            case GteNode gte:
            {
                var c = OrdCompare(gte.Left, gte.Right, member, store);
                return c.HasValue && c.Value >= 0 ? (object)true : false;
            }
            case LteNode lte:
            {
                var c = OrdCompare(lte.Left, lte.Right, member, store);
                return c.HasValue && c.Value <= 0 ? (object)true : false;
            }
            case AndNode and:
                return EvaluateValue(and.Left, member, store) is true &&
                       EvaluateValue(and.Right, member, store) is true ? (object)true : false;

            case OrNode or:
                return EvaluateValue(or.Left, member, store) is true ||
                       EvaluateValue(or.Right, member, store) is true ? (object)true : false;

            case NotNode not:
                return EvaluateValue(not.Operand, member, store) is not true ? (object)true : false;

            default:
                return null;
        }
    }

    // Walk a dot-path through an ObjectValue, following ReferenceValues via the store.
    private static object? WalkPath(IReadOnlyList<string> path, ObjectValue start, IInstanceStore store)
    {
        ObjectValue? current = start;
        for (var i = 0; i < path.Count; i++)
        {
            if (!current!.Fields.TryGetValue(path[i], out var fieldVal))
                throw new InvalidOperationException($"Unknown field '{path[i]}'.");

            if (i == path.Count - 1)
                return NodeValueToObject(fieldVal);

            // Middle segment: need an object to continue navigating
            current = fieldVal switch
            {
                ObjectValue nested => nested,
                ReferenceValue r when r.TargetId.HasValue =>
                    store.ReadById(r.TargetId.Value) is { } hit ? hit.Fields : null,
                _ => null
            };
            if (current == null) return null;
        }
        return null; // empty path (should not occur with a valid AST)
    }

    // Unwrap a scalar NodeValue to its primitive CLR value for comparison.
    // Returns null for non-scalar values (references, sets, dictionaries).
    private static object? NodeValueToObject(NodeValue value) => value switch
    {
        BoolValue b      => (object?)b.Value,
        IntValue i       => (object?)i.Value,
        DecimalValue d   => (object?)d.Value,
        TextValue t      => (object?)t.Text,
        DateValue d      => (object?)d.Value.ToString("yyyy-MM-dd"),
        DateTimeValue dt => (object?)dt.Value.ToString("O"),
        _                => null
    };

    private static int? OrdCompare(ExpressionNode left, ExpressionNode right, ObjectValue member, IInstanceStore store)
    {
        var l = EvaluateValue(left, member, store);
        var r = EvaluateValue(right, member, store);
        if (l == null || r == null) return null;
        return (l, r) switch
        {
            (int li, int ri)         => li.CompareTo(ri),
            (int li, decimal rd)     => ((decimal)li).CompareTo(rd),
            (decimal ld, int ri)     => ld.CompareTo((decimal)ri),
            (decimal ld, decimal rd) => ld.CompareTo(rd),
            (string ls, string rs)   => string.Compare(ls, rs, StringComparison.Ordinal),
            _                        => null
        };
    }
}
