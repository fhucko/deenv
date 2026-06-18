namespace DeEnv.Tests.TestSupport;

/// <summary>
/// Poll a condition until it holds, the deterministic replacement for a fixed <c>WaitForTimeoutAsync</c>
/// sleep. A fixed sleep both flakes (the async outcome — a WS round-trip, a server GC — can take longer
/// than the guess under load) and wastes time (it always waits the full guess even when the outcome is
/// already there). Polling waits EXACTLY as long as the outcome actually needs and returns the instant it
/// is satisfied, so it is both more robust and faster.
/// </summary>
public static class Polling
{
    public static async Task EventuallyAsync(Func<bool> condition, string what, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (Try(condition)) return;
            await Task.Delay(25);
        }
        if (!Try(condition))
            throw new TimeoutException($"Timed out after {timeoutMs}ms waiting for: {what}");
    }

    // An IOException is the test thread reading a store/app file mid-write — transient, retried.
    private static bool Try(Func<bool> condition)
    {
        try { return condition(); }
        catch (IOException) { return false; }
    }
}
