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

    // A session is claimed by the client's WS (the `hello` on socket open). Until then
    // it only survives the claim window; an unclaimed session is a render whose client
    // never connected (crawler, closed tab, lost script) and is dropped.
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public bool Claimed { get; set; }
}

public sealed class ClientSessionStore
{
    // A soft cap so long-lived hosts don't accumulate sessions without bound; the oldest
    // are dropped first. (Connection-scoped eviction lands with the real-time milestone.)
    private const int Cap = 500;

    // How long an UNCLAIMED session is kept: the gap between the SSR response and the
    // page's WS hello. If the hello arrives later (or never), the session is gone and a
    // refetch falls back to a full re-render from storage.
    private static readonly TimeSpan DefaultClaimWindow = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _claimWindow;
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
    private readonly ConcurrentQueue<string> _order = new();

    public ClientSessionStore(TimeSpan? claimWindow = null) =>
        _claimWindow = claimWindow ?? DefaultClaimWindow;

    public ClientSession Create(ExecObject db)
    {
        var session = new ClientSession { Id = Guid.NewGuid().ToString("N"), Db = db };
        Index(session);
        _sessions[session.Id] = session;
        _order.Enqueue(session.Id);
        SweepExpired();
        while (_sessions.Count > Cap && _order.TryDequeue(out var old))
            _sessions.TryRemove(old, out _);
        return session;
    }

    // Look up a session by id — and claim it, since only the client's own WS knows the
    // id. An unclaimed session past the claim window is expired: dropped, not returned.
    public ClientSession? Get(string id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return null;
        if (!s.Claimed && DateTime.UtcNow - s.CreatedAt > _claimWindow)
        {
            _sessions.TryRemove(id, out _);
            return null;
        }
        s.Claimed = true;
        return s;
    }

    // Drop expired unclaimed sessions from the front of the creation-order queue (the
    // oldest first; a claimed session stops the sweep — the cap still bounds those).
    private void SweepExpired()
    {
        while (_order.TryPeek(out var id))
        {
            if (!_sessions.TryGetValue(id, out var s))
            {
                _order.TryDequeue(out _); // already evicted by the cap
                continue;
            }
            if (s.Claimed || DateTime.UtcNow - s.CreatedAt <= _claimWindow) return;
            _order.TryDequeue(out _);
            _sessions.TryRemove(id, out _);
        }
    }

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
