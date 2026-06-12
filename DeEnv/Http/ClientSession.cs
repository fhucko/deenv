using System.Collections.Concurrent;

namespace DeEnv.Http;

// A per-client handle for a code-owned UI: an opaque clientId minted at SSR and shipped
// in the page, claimed by the client's WS. It carries no data of its own — a refetch
// always re-renders over a fresh load from the store (the single source of truth), so
// there is no per-client warm graph to keep in sync (and none to go stale against a
// change made by another session). This is the seam the real-time milestone will hang
// per-client subscriptions on; for now it only tracks liveness.
public sealed class ClientSession
{
    public required string Id { get; init; }

    // A session is claimed by the client's WS (the `hello` on socket open). Until then
    // it only survives the claim window; an unclaimed session is a render whose client
    // never connected (crawler, closed tab, lost script) and is dropped. A claimed
    // session slides on activity (LastTouched) and expires when idle.
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastTouched { get; set; } = DateTime.UtcNow;
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

    // How long a CLAIMED session survives without any WS activity. An idle tab's warm
    // graph is released; its next interaction refetches from storage (graceful, just
    // slower). Eviction by the connection's own close lands with the real-time milestone.
    private static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromMinutes(30);

    private readonly TimeSpan _claimWindow;
    private readonly TimeSpan _idleTtl;
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
    private readonly ConcurrentQueue<string> _order = new();

    public ClientSessionStore(TimeSpan? claimWindow = null, TimeSpan? idleTtl = null)
    {
        _claimWindow = claimWindow ?? DefaultClaimWindow;
        _idleTtl = idleTtl ?? DefaultIdleTtl;
    }

    public ClientSession Create()
    {
        var session = new ClientSession { Id = Guid.NewGuid().ToString("N") };
        _sessions[session.Id] = session;
        _order.Enqueue(session.Id);
        SweepExpired();
        while (_sessions.Count > Cap && _order.TryDequeue(out var old))
            _sessions.TryRemove(old, out _);
        return session;
    }

    // Look up a session by id — and claim it, since only the client's own WS knows the
    // id. Expired (unclaimed past the claim window, or claimed but idle past the idle
    // TTL) → dropped, not returned. Activity slides LastTouched.
    public ClientSession? Get(string id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return null;
        if (IsExpired(s))
        {
            _sessions.TryRemove(id, out _);
            return null;
        }
        s.Claimed = true;
        s.LastTouched = DateTime.UtcNow;
        return s;
    }

    private bool IsExpired(ClientSession s) => s.Claimed
        ? DateTime.UtcNow - s.LastTouched > _idleTtl
        : DateTime.UtcNow - s.CreatedAt > _claimWindow;

    // Drop expired sessions from the front of the creation-order queue (the oldest
    // first; a live session stops the sweep — the cap still bounds those).
    private void SweepExpired()
    {
        while (_order.TryPeek(out var id))
        {
            if (!_sessions.TryGetValue(id, out var s))
            {
                _order.TryDequeue(out _); // already evicted by the cap
                continue;
            }
            if (!IsExpired(s)) return;
            _order.TryDequeue(out _);
            _sessions.TryRemove(id, out _);
        }
    }
}
