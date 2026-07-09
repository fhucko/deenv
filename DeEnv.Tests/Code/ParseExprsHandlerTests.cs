using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// The `parseExprs` WS op (M12 auto-live parse-op — DeEnv/Http/WsHandler.cs HandleParseExprs): pure,
// store-free, no session/floor needed (mirrors the handler's own doc — even an anonymous session on a
// public instance may reach it). Handler-level, no browser: same `ws.ProcessMessage` idiom
// ClientSessionTests already uses for a store-backed but browser-free WS round trip.
public sealed class ParseExprsHandlerTests
{
    // The security-posture cap (per-text length, review-promoted from an open question): an oversize text
    // must be skipped BEFORE ever reaching CodeParse.ParseExpression's recursive descent — proven here by
    // making the oversize text OTHERWISE VALID syntax (a long but legal `1+1+1+...` chain), so if the cap
    // were missing/broken this text would successfully parse and the assertion below would catch it. A
    // short, ordinary valid text in the SAME request must still come back — one oversize text must not
    // starve its siblings (the `continue`, not `break`, in HandleParseExprs).
    [Test]
    public async Task An_oversize_text_is_skipped_but_others_in_the_same_request_still_parse()
    {
        var desc = InstanceContext.BoolDb();
        var dataPath = Path.GetTempFileName();
        var store = new JsonFileInstanceStore(dataPath, desc);
        var ws = new WsHandler(store, desc);

        // 1 + 500 * "+1" = 1001 chars — over the 1_000-char per-text cap, and legitimately parseable if the
        // cap didn't intercept it (a long left-associative int-addition chain).
        var oversize = "1" + string.Concat(Enumerable.Repeat("+1", 500));
        await Assert.That(oversize.Length).IsGreaterThan(1000);

        var texts = JsonSerializer.Serialize(new[] { "true", oversize });
        using var reply = JsonDocument.Parse(
            ws.ProcessMessage($$"""{ "op": "parseExprs", "id": 1, "texts": {{texts}} }"""));

        await Assert.That(reply.RootElement.GetProperty("op").GetString()).IsEqualTo("parseExprsResult");
        var entries = reply.RootElement.GetProperty("entries");
        await Assert.That(entries.TryGetProperty("true", out _)).IsTrue();     // an ordinary sibling text: parsed
        await Assert.That(entries.TryGetProperty(oversize, out _)).IsFalse();  // oversize: skipped, never attempted

        try { File.Delete(dataPath); } catch { /* best-effort */ }
    }
}
