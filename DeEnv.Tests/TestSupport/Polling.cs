namespace DeEnv.Tests.TestSupport;

public static class TestTimeouts
{
    /// <summary>Timeout (ms) for individual Playwright actions/waits (locator waits, WaitFor*, etc.).</summary>
    public const int ActionMs = 10_000;

    /// <summary>Timeout (ms) for the overall test (page default timeout etc.).</summary>
    public const int TestMs = 30_000;

    /// <summary>
    /// Designer (kernel + IDE) per-action Playwright ceiling. Higher than <see cref="ActionMs"/> because
    /// designer steps share a heavy parallel fleet (kernel boot, live preview, WS autosave).
    /// </summary>
    public const int DesignerActionMs = 30_000;

    /// <summary>
    /// Designer multi-hop / store-poll ceiling (EventuallyAsync default, convert→tree attach, deploy file
    /// polls). Higher than <see cref="TestMs"/> for the same peak-suite reasons as <see cref="DesignerActionMs"/>.
    /// </summary>
    public const int DesignerTestMs = 60_000;
}

/// <summary>
/// Poll a condition until it holds, the deterministic replacement for a fixed <c>WaitForTimeoutAsync</c>
/// sleep. A fixed sleep both flakes (the async outcome — a WS round-trip, a server GC — can take longer
/// than the guess under load) and wastes time (it always waits the full guess even when the outcome is
/// already there). Polling waits EXACTLY as long as the outcome actually needs and returns the instant it
/// is satisfied, so it is both more robust and faster.
/// </summary>
public static class Polling
{
    // The 30s default is sized for PEAK full-suite load, not the typical case: under CPU contention from
    // the bounded parallel browser fleet, a WS commit / GC / file rewrite can briefly exceed 10s. The poll
    // returns the instant the outcome holds, so green runs pay nothing — only a genuinely stuck wait pays
    // the ceiling. Callers needing a wider window (a whole-app deploy) still pass an explicit timeout.
    public static async Task EventuallyAsync(Func<bool> condition, string what, int timeoutMs = 30000)
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
