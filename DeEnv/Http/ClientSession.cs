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

    // ── principal (M-auth) ───────────────────────────────────────────────────────
    //
    // The id of the `User` object this session is authenticated as, or null when anonymous. This is
    // the durable home of the session→principal bind: the renderer reads it to resolve the `currentUser`
    // system var and the access read floor. It is the seam the password-login slice will SET (on a WS
    // login/bind over this same connection — the bind happens on the connection, no reserved route).
    // ponytail: this slice is FLOOR-FIRST — the principal is bound by the test harness (passed straight
    // into SsrRenderer.Render), so this field is the not-yet-written destination of that later bind; no
    // password crypto, no login handshake here. It defaults to null (anonymous), the dormant common case.
    public int? PrincipalUserId { get; set; }

    // ── transient-id remap (a just-added object's negative id → its real one) ────
    //
    // The Code UI mints a just-added object with a transient NEGATIVE id and keeps addressing it by
    // that id — as the objectId of a field edit, the member id of a remove — until the arrayAdd
    // round-trip remaps it. The client fires those follow-ups immediately (it must not wait for the
    // round-trip), so the server reconciles instead: this per-client table records every negative→real
    // mapping the server assigned when it minted an added object, and ResolveId translates an inbound id
    // through it. Ordered WS delivery guarantees the minting arrayAdd is processed before any op that
    // references its tempId, so the mapping is always present when needed. Bounded by the session
    // lifetime (the store caps + idle-expires sessions); an int→int entry per add is negligible.
    private readonly Dictionary<int, int> _transientIds = new();

    // Record that the client's transient (negative) id now denotes the real (positive) extent id.
    public void MapTransientId(int transientId, int realId) => _transientIds[transientId] = realId;

    // Resolve a wire id through the remap: a known transient id → its real id; any other id (a real
    // positive id, or one never mapped) is returned unchanged.
    public int ResolveId(int id) => _transientIds.GetValueOrDefault(id, id);

    // Drop a mapping once the client acks it has applied the remap: from then on the client addresses the
    // object by its real id and never sends the transient one again, so the entry is dead. This keeps the
    // table to just the in-flight adds (the few not-yet-acked), with session expiry only the last-resort
    // backstop for an ack lost to a crash.
    public void DropTransientId(int transientId) => _transientIds.Remove(transientId);
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

    public ClientSession Create(int? principalUserId = null)
    {
        var session = new ClientSession { Id = Guid.NewGuid().ToString("N"), PrincipalUserId = principalUserId };
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
