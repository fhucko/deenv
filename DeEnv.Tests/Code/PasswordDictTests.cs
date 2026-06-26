using System.Text.Json;
using DeEnv.Code;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// The M-auth `password` type, defense-in-depth: a `dict of password` SCALAR entry must ship BLANK ("")
// like every other password leaf — the last leaf-materialization the read chokepoint had to cover (the
// DbBridge dict-entry SCALAR branch). No committed app uses this shape; this closes the absolute "every
// leaf-materialization for a password field blanks" invariant. The masked-render half is the InputType
// `password` arm (a `dict of password` create input renders <input type="password">), pinned via the
// descriptor the DictTable reads.
public sealed class PasswordDictTests
{
    // A minimal app whose Db holds a `dict of password` (keyType text). The dict route renders each
    // entry's value in a cell; the read chokepoint must blank a password-typed entry value to "".
    private const string App = """
    types
        Db
            secrets dict of password
    """;

    [Test]
    public async Task A_dict_of_password_ships_the_entry_value_blank_never_the_hash()
    {
        var desc = InstanceDescriptionLoader.Load(App);
        var dataPath = Path.Combine(Path.GetTempPath(), $"pwdict_{Guid.NewGuid():N}.json");
        var store = new JsonFileInstanceStore(dataPath, desc);

        // Seed a dict entry whose value is a real PBKDF2 hash (as the WS write chokepoint would have stored
        // it), written RAW through the store seam — the store keeps the hash; only the SHIPPED value blanks.
        var hash = AuthCrypto.Hash("s3cret");
        store.WriteDictionaryEntry(NodePath.Root.Field("secrets"), new TextValue("api"), new TextValue(hash));

        // Render the dict route. The entry's value ships in the leaves; the chokepoint blanks it.
        var html = new SsrRenderer(store, desc).Render("/secrets").Html;

        // The raw hash / its self-describing marker appear NOWHERE in the shipped document.
        await Assert.That(html.Contains(hash)).IsFalse();
        await Assert.That(html.Contains("pbkdf2")).IsFalse();

        // And the entry object's shipped `value` prop is exactly "" (a present blank, never the hash). The
        // dict entry is a NEGATIVE-id object in the leaves (a stable key-hash); find the one carrying our key.
        var initData = ExtractInitData(html);
        using var doc = JsonDocument.Parse(initData);
        var objects = doc.RootElement.GetProperty("leaves").GetProperty("objects");
        var found = false;
        foreach (var o in objects.EnumerateObject())
        {
            var props = o.Value.GetProperty("props");
            if (!props.TryGetProperty("__key", out var k)) continue;
            if (LeafText(k) != "api") continue;
            found = true;
            // The scalar entry's value leaf, blanked by the read chokepoint.
            await Assert.That(props.TryGetProperty("value", out var v)).IsTrue();
            await Assert.That(LeafText(v)).IsEqualTo("");
        }
        await Assert.That(found).IsTrue();
    }

    // Read a shipped leaf's text value, tolerant of the wrapper shape ({ type:"simple", value:{ type, value } }
    // for an object prop, or a bare { type, value }).
    private static string LeafText(JsonElement leaf)
    {
        var node = leaf;
        if (node.TryGetProperty("type", out var t) && t.GetString() == "simple")
            node = node.GetProperty("value");
        return node.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static string ExtractInitData(string html)
    {
        const string start = "window.initData=";
        const string end = ";window.initUi=";
        var i = html.IndexOf(start, StringComparison.Ordinal) + start.Length;
        var j = html.IndexOf(end, i, StringComparison.Ordinal);
        return html.Substring(i, j - i).Replace("\\u003c", "<");
    }
}
