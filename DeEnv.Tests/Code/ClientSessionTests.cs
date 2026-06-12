using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// The per-client session lifecycle: a clientId minted at SSR survives only a short claim
// window unless the page's WS hello claims it; claimed sessions stay until idle; an
// expired session still serves a refetch (which re-renders from a fresh store load).
public sealed class ClientSessionTests
{
    [Test]
    public async Task An_unclaimed_session_expires_after_the_claim_window()
    {
        var store = new ClientSessionStore(claimWindow: TimeSpan.FromMilliseconds(50));
        var session = store.Create();

        await Task.Delay(150);
        await Assert.That(store.Get(session.Id)).IsNull();
    }

    [Test]
    public async Task A_claimed_session_survives_past_the_claim_window()
    {
        var store = new ClientSessionStore(claimWindow: TimeSpan.FromMilliseconds(50));
        var session = store.Create();

        await Assert.That(store.Get(session.Id)).IsNotNull(); // the hello — claims it
        await Task.Delay(150);
        await Assert.That(store.Get(session.Id)).IsNotNull(); // still there
    }

    [Test]
    public async Task A_claimed_session_expires_when_idle()
    {
        var store = new ClientSessionStore(
            claimWindow: TimeSpan.FromMilliseconds(50), idleTtl: TimeSpan.FromMilliseconds(100));
        var session = store.Create();

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

    // The point of the warm-graph cleanup: a refetch re-renders from a fresh store load,
    // so a change another session committed (here, a directly-added person) is reflected —
    // not hidden behind a stale per-client mirror.
    [Test]
    public async Task A_refetch_reflects_a_store_change_made_outside_the_session()
    {
        var desc = InstanceContext.RefetchUiDb();
        var dataPath = Path.GetTempFileName();
        var dataStore = new JsonFileInstanceStore(dataPath, desc);
        Seed(dataStore, "Ada", 999);
        var sessions = new ClientSessionStore();

        var html = new SsrRenderer(dataStore, desc, sessions).Render("/");
        var clientId = ClientIdOf(html);
        var ws = new WsHandler(dataStore, desc, sessions);
        ws.ProcessMessage($$"""{ "op": "hello", "clientId": "{{clientId}}" }""");

        // Another session adds a person directly to the store after this client's render.
        Seed(dataStore, "Zoe", 5);

        using var refetch = JsonDocument.Parse(
            ws.ProcessMessage($$"""{ "op": "refetch", "clientId": "{{clientId}}", "path": "/", "vars": {} }"""));
        var state = refetch.RootElement.GetProperty("state").GetRawText();
        await Assert.That(state).Contains("Zoe"); // the out-of-band change is reflected
        await Assert.That(state).Contains("Ada");

        try { File.Delete(dataPath); } catch { /* best-effort */ }
    }

    private static void Seed(IInstanceStore store, string name, int salary)
    {
        var id = store.CreateObject("Person", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name),
            ["salary"] = new IntValue(salary),
        }));
        store.AddToSet(NodePath.Root.Field("people"), id);
    }

    private static string ClientIdOf(string html)
    {
        const string marker = "window.initClientId=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return html[start..html.IndexOf('"', start)];
    }
}
