namespace DeEnv.Code;

// Runtime values produced by the interpreter. Adapted onto the M5 object model:
// objects carry their TypeName and an intrinsic Id; collections are a single
// kind-tagged ExecArray — the *Code array* — used byte-for-byte the same on the
// server, the wire, and the client (no per-side collection type, no transform).
//
// Identity is the id sign: a positive Id means persisted (a db extent/set id),
// a negative Id means transient (a render-local literal/result, not yet saved).
// So "is this in the db?" is simply `Id > 0` — there is no separate flag.

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

    // For a dictionary ENTRY (an inline value with no extent id, so Id is a negative
    // stable hash), the entry's own node path — so a bound field edit persists via the
    // PATH-addressed `write` op instead of the id-addressed objectPropChange. ScalarEntry
    // marks a scalar dict entry, whose single value lives AT SourcePath (no field suffix);
    // an object entry's fields write to SourcePath + "/" + prop.
    public string? SourcePath { get; set; }
    public bool ScalarEntry { get; set; }

    // A provably-constant, user-data-FREE value (a sys.schema descriptor: built from schema
    // metadata only, in a fresh empty scope, so it cannot reference db). ClientState ships a
    // Constant object/array WHOLE — every prop, every item, recursively — because a consumer
    // (sys.new / a generic-UI walk) reads its full interior and there is nothing private inside.
    // Default false: an ordinary minted object/array stays ACCESS-SCOPED (ships only what was
    // displayed), so a where/orderBy/literal collection never spills its undisplayed membership.
    public bool Constant { get; set; }

    object IExecValue.Value => this;
}

// How a Code array behaves: a db-backed identity-keyed set, a keyed dictionary, or a
// transient ordered list (a where/orderBy result or an array literal). All three are
// the same ExecArray shape; only iteration/mutation semantics differ by kind.
public enum ArrayKind { Set, Dict, List }

// A Code array: one collection type for every kind. A db set/dict has a positive,
// persistent Id (its stable intrinsic identity — a single set may be reached by many
// reference paths but has one Id); a list has a render-local negative Id. add/remove
// on a set write through IInstanceStore by Id; ElementTypeName is the member type
// (set/dict only) used when persisting a freshly-created member.
public sealed class ExecArray : IExecValue
{
    public required int Id { get; set; }
    public required ArrayKind Kind { get; set; }
    public required List<ExecItem> Items { get; set; }
    public string? ElementTypeName { get; set; }

    // A dictionary's source URL path (e.g. "/settings"). Dictionaries persist through the
    // PATH-addressed store/WS ops (addEntry/removeEntry) — a dict entry's address IS its
    // key under a path (M5), unlike a set member reached by its own identity. Set/list are
    // null. Ships to the client so its add/remove sends carry the path.
    public string? SourcePath { get; set; }

    // Part of a provably-constant, user-data-free value (a sys.schema descriptor's nested array,
    // e.g. `props`/`values`/`valueProps`). ClientState ships a Constant array WHOLE — every item —
    // since the descriptor's consumer walks all of it and there is nothing private inside. Default
    // false: a where/orderBy result or an array literal stays ACCESS-SCOPED (ships only displayed
    // items), so an undisplayed row's membership never leaks. See ExecObject.Constant.
    public bool Constant { get; set; }

    object IExecValue.Value => this;
}

// One (key, value) pair in a Code array. Key is the item's identity within the array:
// a set member's intrinsic object id, a dict key, or a list ordinal.
public sealed class ExecItem
{
    public required int Key { get; set; }
    public required IExecValue Value { get; set; }
}

public sealed class ExecFunction : IExecValue
{
    public required CodeFunction Function { get; set; }
    public required ExecScope Scope { get; set; }
    // The ambient bindings at the closure's creation — restored when it's invoked, so a deferred
    // callback (e.g. an onClick) resolves ambient vars from its birthplace, not the call site. Null
    // for a top-level fn (created outside any ambient), which then flows down to the live ambient.
    public AmbientFrame? CapturedAmbient { get; set; }
    object IExecValue.Value => this;
}

// A built-in collection method (add / remove / where / orderBy) bound to its target.
public sealed class ExecSysFunction : IExecValue
{
    public required ExecArray Target { get; set; }
    public required string Method { get; set; }
    object IExecValue.Value => this;
}

// A data context: an overlay of staged field writes over a parent context, read-through. Live
// marks the root (writes go straight to the live object); a sub-context (`ctx.new`) stages
// instead, until `commit` flushes to its parent. Staged is keyed by object reference.
public sealed class ExecCtx : IExecValue
{
    public Dictionary<ExecObject, Dictionary<string, IExecValue>> Staged { get; } = [];
    public ExecCtx? Parent { get; set; }
    public bool Live { get; set; }
    object IExecValue.Value => this;
}

// `ctx.new` / `ctx.commit` / `ctx.discard` bound to their context — invoked via a call.
public sealed class ExecCtxMethod : IExecValue
{
    public required ExecCtx Ctx { get; set; }
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

    // A persistent top-level scope (the system scope holding db/path, or the app scope
    // holding the ui vars/functions) — as opposed to a transient local scope (a function
    // call, a block, a foreach item). A writable var in a top scope is reactive: read in a
    // computation it is a dependency; assigned it invalidates the memo cache.
    public bool IsTop { get; set; }
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

// Reads made inside an in-flight computation, kept by instance. If the computation's
// result turns out to be a tag tree (a page fn — its result cannot ship, the client
// re-renders it), these reads ARE displayed data and get promoted to leaves; if the
// result is a value (it ships), they stay dependencies only — private.
public sealed class LeafFrame
{
    public HashSet<(ExecObject, string?)> Props { get; } = [];
    public HashSet<(ExecArray, ExecItem?)> Items { get; } = [];
}

public sealed class ExecContext
{
    public LastId LastId { get; set; } = new();

    // Leaf accesses (made in output position — DepStack empty): the displayed data
    // shipped to the client. Inside a computation, reads become dependencies instead
    // (plus pending leaves — see LeafFrame).
    public HashSet<(ExecObject, string?)> AccessedObjectProps { get; set; } = [];
    public HashSet<(ExecArray, ExecItem?)> AccessedItems { get; set; } = [];

    // ── memoization (Stage 4) ────────────────────────────────────────────────────
    // Computation results captured while rendering, keyed by (function, args), for
    // transfer; and the dependency stack — one Deps per in-flight computation, top is
    // the running one (LeafStack moves in lockstep). See MemoCache.cs / MEMO_CACHE_DESIGN.md.
    public Dictionary<string, CacheEntry> Memo { get; } = [];
    public Stack<Deps> DepStack { get; } = new();
    public Stack<LeafFrame> LeafStack { get; } = new();

    // ── component slot path (Milestone 11) ───────────────────────────────────────
    // The render-tree position of the node currently being rendered. ExecuteTagChildren
    // pushes each child's static AST index; a tag-invoked component keys its run-once setup
    // on this path (not on its argument identities), so its local state survives a re-render
    // — even when an argument object is rebuilt fresh every render. Push/pop is balanced, so
    // it returns to empty between renders.
    public List<string> SlotPath { get; } = [];

    // Ambient (dynamic-scope) bindings for the current dynamic extent: `ambient name = value`
    // pushes a frame, a block save/restores this (popping provides on exit), and a symbol read
    // falls back to it after a lexical miss.
    public AmbientFrame? Ambient { get; set; }
}

// One immutable frame in the ambient chain — dynamic scoping over the call/render extent.
public sealed record AmbientFrame(string Name, IExecValue Value, AmbientFrame? Parent);
