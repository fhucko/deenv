using System.Collections.Concurrent;
using DeEnv.Code;

namespace DeEnv.Http;

// A live per-client view of a code-owned UI: the warm db object graph the client is
// looking at, minted at SSR and addressed by an opaque clientId shipped in the page.
// The WS reuses it so a recompute (refetch) needs no reload, keeping it in sync with
// each client mutation — the "server remembers what the client sees" seam that a later
// milestone builds cross-client change propagation on. Mutations also persist through
// the store (the durable truth); the warm graph mirrors them for in-memory recompute.
public sealed class ClientSession
{
    public required string Id { get; init; }

    // The warm db root (the same ExecObject graph the SSR render produced).
    public required ExecObject Db { get; init; }

    // By-id handles into the warm graph, for applying an identity-addressed mutation
    // without re-walking: object id → object, set id → set.
    public Dictionary<int, ExecObject> Objects { get; } = [];
    public Dictionary<int, ExecArray> Sets { get; } = [];
}

public sealed class ClientSessionStore
{
    // A soft cap so long-lived hosts don't accumulate sessions without bound; the oldest
    // are dropped first. (Connection-scoped eviction lands with the real-time milestone.)
    private const int Cap = 500;

    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
    private readonly ConcurrentQueue<string> _order = new();

    public ClientSession Create(ExecObject db)
    {
        var session = new ClientSession { Id = Guid.NewGuid().ToString("N"), Db = db };
        Index(session);
        _sessions[session.Id] = session;
        _order.Enqueue(session.Id);
        while (_sessions.Count > Cap && _order.TryDequeue(out var old))
            _sessions.TryRemove(old, out _);
        return session;
    }

    public ClientSession? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    // Walk the warm graph once, indexing every persisted object/set by its intrinsic id.
    private static void Index(ClientSession session)
    {
        var seen = new HashSet<int>();

        void Walk(IExecValue value)
        {
            switch (value)
            {
                case ExecObject o:
                    if (o.Id > 0)
                    {
                        if (!seen.Add(o.Id)) return; // a reference cycle — already indexed
                        session.Objects[o.Id] = o;
                    }
                    foreach (var p in o.Props.Values) Walk(p);
                    break;
                case ExecArray a:
                    if (a.Id > 0) session.Sets[a.Id] = a;
                    foreach (var item in a.Items) Walk(item.Value);
                    break;
            }
        }

        Walk(session.Db);
    }
}
