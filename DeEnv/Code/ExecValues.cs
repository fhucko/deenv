using DeEnv.Storage;

namespace DeEnv.Code;

// Runtime values produced by the interpreter. Ported from the app15 prototype and
// adapted onto the M5 object model: objects carry their TypeName, and db-backed
// collections carry the NodePath of their set so add/remove can persist through
// IInstanceStore. `IsInDb == false` marks a transient (not-yet-persisted) value.

public interface IExecTagChild;

public interface IExecValue : IExecTagChild
{
    // Scalars expose their primitive; objects/arrays expose themselves (for identity).
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

public sealed class ExecArray : IExecValue
{
    public required List<ExecArrayItem> Items { get; set; }
    public required int Id { get; set; }
    public bool IsInDb { get; set; }
    // Persistence binding (db-backed sets only; null for transient/derived collections).
    public NodePath? Path { get; set; }
    public string? ElementTypeName { get; set; }
    object IExecValue.Value => this;
}

public sealed class ExecArrayItem
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
    public required ExecArray Target { get; set; }
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

// ── execution context (data-access tracking for partial transfer; id minting) ────

public sealed class LastId
{
    public int Value { get; set; }
}

public sealed class ExecContext
{
    public LastId LastId { get; set; } = new();
    public HashSet<(ExecObject, string?)> AccessedObjectProps { get; set; } = [];
    public HashSet<(ExecArray, ExecArrayItem?)> AccessedArrayItems { get; set; } = [];
    public bool AccessedDb { get; set; }
    public List<ExecArray> CreatedArrays { get; set; } = [];
    public List<ExecObject> CreatedObjects { get; set; } = [];

    // ── memoization (Stage 4) ────────────────────────────────────────────────────
    // Computation results captured while rendering, keyed by (function, args), for
    // transfer; and the dependency stack — one Deps per in-flight computation, top is
    // the running one. See MemoCache.cs / MEMO_CACHE_DESIGN.md.
    public Dictionary<string, CacheEntry> Memo { get; } = [];
    public Stack<Deps> DepStack { get; } = new();
}
