using DeEnv.Storage;

namespace DeEnv.Code;

// Runtime values produced by the interpreter. Adapted onto the M5 object model:
// objects carry their TypeName and an intrinsic Id; a db-backed set (ExecSet) is a
// mutable, persistent, identity-keyed collection, distinct from a transient ordered
// list (ExecList — a where/orderBy result or an array literal). `ExecObject.IsInDb`
// marks an object as persisted vs a not-yet-saved transient.

public interface IExecTagChild;

public interface IExecValue : IExecTagChild
{
    // Scalars expose their primitive; objects/collections expose themselves (identity).
    object? Value { get; }
}

public sealed class ExecInt : IExecValue
{
    public required int Value { get; set; }
    object IExecValue.Value => Value;
}

public sealed class ExecBool : IExecValue
{
    public required bool Value { get; set; }
    object IExecValue.Value => Value;
}

public sealed class ExecText : IExecValue
{
    public required string Value { get; set; }
    object IExecValue.Value => Value;
}

public sealed class ExecNull : IExecValue
{
    object? IExecValue.Value => null;
}

// The result of a statement-shaped call that yields nothing usable.
public sealed class ExecNothing : IExecValue
{
    object? IExecValue.Value => null;
}

public sealed class ExecObject : IExecValue
{
    public required Dictionary<string, IExecValue> Props { get; set; }
    public required int Id { get; set; }
    public string? TypeName { get; set; }
    public bool IsInDb { get; set; }
    object IExecValue.Value => this;
}

// A runtime collection of items: a db-backed set or a transient ordered list. The two
// share iteration (foreach, where/orderBy) but differ in identity and persistence.
public interface IExecCollection : IExecValue
{
    int Id { get; }
    List<ExecItem> Items { get; }
}

// A db-backed set: identity-keyed, persistent, with a stable intrinsic Id. add/remove
// write through IInstanceStore at Path.
public sealed class ExecSet : IExecCollection
{
    public required int Id { get; set; }
    public required List<ExecItem> Items { get; set; }
    public NodePath? Path { get; set; }
    public string? ElementTypeName { get; set; }
    object IExecValue.Value => this;
}

// A transient ordered list: a where/orderBy result or an array literal. Not persisted;
// its Id is a render-local (negative) id.
public sealed class ExecList : IExecCollection
{
    public required int Id { get; set; }
    public required List<ExecItem> Items { get; set; }
    object IExecValue.Value => this;
}

public sealed class ExecItem
{
    public required int Id { get; set; }
    public required IExecValue Value { get; set; }
}

public sealed class ExecFunction : IExecValue
{
    public required CodeFunction Function { get; set; }
    public required ExecScope Scope { get; set; }
    object IExecValue.Value => this;
}

// A built-in collection method (add / remove / where / orderBy) bound to its target.
public sealed class ExecSysFunction : IExecValue
{
    public required IExecCollection Target { get; set; }
    public required string Method { get; set; }
    object IExecValue.Value => this;
}

public sealed class ExecTag : IExecValue
{
    public required string Name { get; set; }
    public required Dictionary<string, IExecValue> Attributes { get; set; }
    public required IExecTagChild[] Children { get; set; }
    object IExecValue.Value => this;
}

// ── scope ───────────────────────────────────────────────────────────────────────

public sealed class ExecScope
{
    public Dictionary<string, ExecScopeItem> Items { get; set; } = [];
    public ExecScope? Parent { get; set; }
}

public sealed class ExecScopeItem
{
    public required IExecValue Value { get; set; }
    public required bool IsReadOnly { get; set; }
}

// ── execution context ─────────────────────────────────────────────────────────────

public sealed class LastId
{
    public int Value { get; set; }
}

public sealed class ExecContext
{
    public LastId LastId { get; set; } = new();

    // Leaf accesses (made in output position — DepStack empty): the displayed data
    // shipped to the client. Inside a computation, reads become dependencies instead.
    public HashSet<(ExecObject, string?)> AccessedObjectProps { get; set; } = [];
    public HashSet<(IExecCollection, ExecItem?)> AccessedItems { get; set; } = [];

    // ── memoization (Stage 4) ────────────────────────────────────────────────────
    // Computation results captured while rendering, keyed by (function, args), for
    // transfer; and the dependency stack — one Deps per in-flight computation, top is
    // the running one. See MemoCache.cs / MEMO_CACHE_DESIGN.md.
    public Dictionary<string, CacheEntry> Memo { get; } = [];
    public Stack<Deps> DepStack { get; } = new();
}
