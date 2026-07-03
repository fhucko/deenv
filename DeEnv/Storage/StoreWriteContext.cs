namespace DeEnv.Storage;

// Ambient "who is writing right now" for the append-only changeset log (M13 slice 1). The store builds a
// LogEntry from whatever this carries at the moment a mutating method commits — WITHOUT adding a
// who/msgId parameter to every IInstanceStore method (that would be a wire/interface-shape change for a
// concern only the WS layer and the log care about). WsHandler.ProcessMessage sets it right after parsing
// a request (who = the bound principal, msgId = the request's correlation id), then clears it once the
// dispatch returns — see WsHandler.cs.
//
// AsyncLocal, not a bare static field: concurrent WS connections (all on shared thread-pool threads) can
// interleave between one connection's Set and its call into the store, and a bare field would let one
// request's who/msgId leak into another's log entry. AsyncLocal flows with the logical call, not the
// thread, so each in-flight ProcessMessage sees only its own values. Every non-WS writer (host actions,
// seeds, tests) never sets this, so it reads (null, null) — logging nulls naturally, exactly the
// documented behavior for a non-WS writer.
public static class StoreWriteContext
{
    private static readonly AsyncLocal<(int? Who, int? MsgId)> Current = new();

    public static (int? Who, int? MsgId) Get() => Current.Value;

    // Scope-style set: the caller wraps its dispatch in `using StoreWriteContext.Scope(who, msgId);` (or
    // an explicit try/finally) so the ambient value is always cleared even if the dispatch throws — a
    // stuck value would otherwise leak into whatever unrelated store write runs next on a thread-pool
    // thread reused for another logical call.
    public static IDisposable Scope(int? who, int? msgId)
    {
        var previous = Current.Value;
        Current.Value = (who, msgId);
        return new Restore(previous);
    }

    private sealed class Restore((int? Who, int? MsgId) previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
