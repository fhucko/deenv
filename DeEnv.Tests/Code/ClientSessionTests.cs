using System.Text.Json;
using DeEnv.Code;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Stage 4b warm-session lifecycle: a session minted at SSR survives only a short claim
// window unless the page's WS hello claims it; claimed sessions stay; an expired session
// degrades a refetch to a full re-render from storage (never an error).
public sealed class ClientSessionTests
{
    private static ExecObject Db() => new() { Props = [], Id = 1, TypeName = "Db" };

    [Test]
    public async Task An_unclaimed_session_expires_after_the_claim_window()
    {
        var store = new ClientSessionStore(claimWindow: TimeSpan.FromMilliseconds(50));
        var session = store.Create(Db());

        await Task.Delay(150);
        await Assert.That(store.Get(session.Id)).IsNull();
    }

    [Test]
    public async Task A_claimed_session_survives_past_the_claim_window()
    {
        var store = new ClientSessionStore(claimWindow: TimeSpan.FromMilliseconds(50));
        var session = store.Create(Db());

        await Assert.That(store.Get(session.Id)).IsNotNull(); // the hello — claims it
        await Task.Delay(150);
        await Assert.That(store.Get(session.Id)).IsNotNull(); // still there
    }

    [Test]
    public async Task A_claimed_session_expires_when_idle()
    {
        var store = new ClientSessionStore(
            claimWindow: TimeSpan.FromMilliseconds(50), idleTtl: TimeSpan.FromMilliseconds(100));
        var session = store.Create(Db());

        await Assert.That(store.Get(session.Id)).IsNotNull(); // claimed (activity)
        await Task.Delay(300);
        await Assert.That(store.Get(session.Id)).IsNull();    // idle past the TTL → released
    }

    [Test]
    public async Task A_late_hello_reports_the_session_gone_but_refetch_still_serves_state()
    {
        var desc = InstanceContext.RefetchUiDb();
        var dataPath = Path.GetTempFileName();
        var dataStore = new JsonFileInstanceStore(dataPath, desc);
        var sessions = new ClientSessionStore(claimWindow: TimeSpan.FromMilliseconds(50));

        // SSR mints the session; the client's hello arrives after the window.
        var html = new SsrRenderer(dataStore, desc, sessions).Render("/");
        var clientId = ClientIdOf(html);
        await Task.Delay(150);

        var ws = new WsHandler(dataStore, desc, sessions);
        using var hello = JsonDocument.Parse(
            ws.ProcessMessage($$"""{ "op": "hello", "clientId": "{{clientId}}" }"""));
        await Assert.That(hello.RootElement.GetProperty("sessionAlive").GetBoolean()).IsFalse();

        // The refetch falls back to a full re-render from storage — state, not an error.
        using var refetch = JsonDocument.Parse(
            ws.ProcessMessage($$"""{ "op": "refetch", "clientId": "{{clientId}}", "path": "/", "vars": {} }"""));
        await Assert.That(refetch.RootElement.TryGetProperty("state", out var state)).IsTrue();
        await Assert.That(state.TryGetProperty("leaves", out _)).IsTrue();

        try { File.Delete(dataPath); } catch { /* best-effort */ }
    }

    private static string ClientIdOf(string html)
    {
        const string marker = "window.initClientId=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return html[start..html.IndexOf('"', start)];
    }
}
