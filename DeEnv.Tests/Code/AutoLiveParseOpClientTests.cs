using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// M12 auto-live parse-op — the CLIENT dispatch half (WsHandler-level parse/cap behavior is pinned
// separately in ParseExprsHandlerTests.cs). One cheap, precise pin driven straight against the real
// client bundle over a live WS session (the CodeClientTests.cs idiom — a TestInstanceServer + a page,
// no full designer/kernel apparatus needed): a parseExprsResult reply must never fall through to the
// mutating-ack path in onWsMessage.
public sealed class AutoLiveParseOpClientTests
{
    // Review fix: parseExprsResult self-correlates (via parseExprsRequests) but carries a numeric `id`
    // like any mutating ack, so before the fix it fell through the SAME onWsMessage dispatch a
    // commit/write/etc. ack uses — landing in the "ANY successful mutating ack clears a prior rejection"
    // branch (ws.ts). A read-only parse reply arriving while the "your edits were NOT saved… reload"
    // safety banner (#__error, uiStatic.lastError) is up would then silently DISMISS it. Proven directly:
    // set lastError (materializing the real banner, exactly as a genuine rejected mutation would), feed
    // the client a parseExprsResult reply whose id matches NOTHING in-flight (applyParseExprsResult itself
    // no-ops on it — the point is only that dispatch never reaches the ack block at all), and assert the
    // banner survives untouched.
    [Test]
    public async Task A_parseExprsResult_reply_never_clears_the_rejection_banner()
    {
        var desc = InstanceContext.TasksUiDb();
        var dataPath = Path.GetTempFileName();
        await using var server = new TestInstanceServer();
        await server.StartAsync(desc, dataPath);
        SeedTask(server.Store!, "Alpha", priority: 1);

        var page = await SharedBrowser.NewPageAsync(server.BaseUrl);
        try
        {
            await page.GotoContentAsync("/");
            await page.WaitForSelectorAsync("[data-key]"); // hydrated

            await page.EvaluateAsync("() => { uiStatic.lastError = 'boom'; refreshErrorBanner(); }");
            await Assert.That(await page.Locator("#__error").CountAsync()).IsEqualTo(1);

            await page.EvaluateAsync("() => onWsMessage({ op: 'parseExprsResult', id: 999999, entries: {} })");

            await Assert.That(await page.EvaluateAsync<string?>("() => uiStatic.lastError")).IsEqualTo("boom");
            await Assert.That(await page.Locator("#__error").CountAsync()).IsEqualTo(1);
        }
        finally
        {
            await page.Context.CloseAsync();
            try { File.Delete(dataPath); } catch { /* best-effort */ }
        }
    }

    private static void SeedTask(IInstanceStore store, string title, int priority)
    {
        var id = store.CreateObject("Task", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["title"] = new TextValue(title),
            ["done"] = new BoolValue(false),
            ["priority"] = new IntValue(priority),
        }));
        store.AddToSet(NodePath.Root.Field("tasks"), id);
    }
}
