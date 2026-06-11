namespace DeEnv.Code;

// The memoization cache (Stage 4). Each computation boundary — a user-function call or
// a where/orderBy derived collection — is memoized by (function, arguments) with its
// result and the dependencies it read, AS REFERENCES (object/collection ids + prop
// names), never the values. The transfer layer (ClientState) ships these entries so a
// computation's inputs stay on the server; the client reuses results and invalidates
// them by dependency. See MEMO_CACHE_DESIGN.md.

// A read of a scalar/object prop: changing owner.prop may change a dependent result.
public readonly record struct PropDep(int ObjectId, string Prop);

// An observation of a collection's membership (a where/orderBy iterating it): an
// add/remove to that collection may change a dependent result. Keyed by the
// collection's runtime array id (shipped in leaves, so server and client agree).
public readonly record struct MemberDep(int CollectionId);

public sealed class Deps
{
    public HashSet<PropDep> Props { get; } = [];
    public HashSet<MemberDep> Members { get; } = [];

    // A caller transitively depends on what its callees read.
    public void Merge(Deps other)
    {
        Props.UnionWith(other.Props);
        Members.UnionWith(other.Members);
    }
}

public sealed class CacheEntry
{
    public required string Key { get; init; }
    public required IExecValue Result { get; init; }
    public required Deps Deps { get; init; }
}
