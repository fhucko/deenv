using System.Security.Cryptography;
using System.Text;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Http;

public sealed class TokenAuth
{
    public const int DefaultMaxAgeSeconds = 30 * 24 * 60 * 60;
    public const string CookiePrefix = "deenv_session_";

    private readonly byte[] _secret;

    public TokenAuth(byte[] secret) => _secret = secret;

    public static TokenAuth Ephemeral() => new(RandomNumberGenerator.GetBytes(32));

    public static TokenAuth ForDataHome(string dataHome)
    {
        Directory.CreateDirectory(dataHome);
        var path = Path.Combine(dataHome, "kernel-secret");
        if (!File.Exists(path))
            File.WriteAllText(path, Base64Url(RandomNumberGenerator.GetBytes(32)));
        return new TokenAuth(FromBase64Url(File.ReadAllText(path).Trim()));
    }

    public string CookieName(int instanceId) => CookiePrefix + instanceId;

    public string Mint(int instanceId, int userId, string passwordHash, DateTimeOffset now)
    {
        var exp = now.ToUnixTimeSeconds() + DefaultMaxAgeSeconds;
        var payload = string.Join('|', instanceId, userId, exp, Stamp(passwordHash));
        return payload + "|" + Sign(payload);
    }

    public int? Verify(string token, int instanceId, IInstanceStore store, InstanceDescription desc, DateTimeOffset now)
    {
        var parts = token.Split('|');
        if (parts.Length != 5) return null;
        var payload = string.Join('|', parts[..4]);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Sign(payload)),
                Encoding.UTF8.GetBytes(parts[4])))
            return null;
        if (!int.TryParse(parts[0], out var tokenInstanceId) || tokenInstanceId != instanceId) return null;
        if (!int.TryParse(parts[1], out var userId)) return null;
        if (!long.TryParse(parts[2], out var exp) || exp < now.ToUnixTimeSeconds()) return null;
        if (Code.UserConvention.PasswordFieldName(desc) is not { } passwordField) return null;
        var user = store.ReadById(userId);
        if (user is null || user.Value.TypeName != Code.UserConvention.TypeName) return null;
        if (user.Value.Fields.Fields.GetValueOrDefault(passwordField) is not TextValue { Text: var hash }) return null;
        return parts[3] == Stamp(hash) ? userId : null;
    }

    // The tag prefix on a MintTicket/VerifyTicket payload — distinguishes an upload ticket from a
    // session cookie token at parse time (both share this class's Sign machinery/secret, but a ticket
    // must never be replayable as a session cookie or vice versa — the tag makes the two payload shapes
    // mutually unambiguous even though both are 5 pipe-separated fields).
    private const string TicketTag = "ticket";

    // A short-lived HMAC upload ticket — (instanceId, userId, exp) — for the blob pool's upload edge
    // (docs/plans/assets-design.md §2). Reuses this class's EXISTING signing machinery (Sign/_secret,
    // the same per-data-home secret Mint/Verify use for the session cookie) — no new crypto. Deliberately
    // NOT stamped to the user's password hash the way Mint is: a ticket's entire safety margin is its
    // short TTL (default 60s — minted on demand, used immediately), not password-invalidation, so it
    // stays valid through the mint→upload round trip even if a password happens to change in between.
    public (string Ticket, long Exp) MintTicket(int instanceId, int userId, DateTimeOffset now, int ttlSeconds = 60)
    {
        var exp = now.ToUnixTimeSeconds() + ttlSeconds;
        var payload = string.Join('|', TicketTag, instanceId, userId, exp);
        return (payload + "|" + Sign(payload), exp);
    }

    // Verify an upload ticket presented to THIS instance's asset edge. Returns the ticket's userId when
    // the signature is intact, unexpired, and scoped to `instanceId` — else null. Missing, garbage,
    // tampered, expired, and wrong-instance tickets all collapse to the same null (fail closed, no
    // signal to an attacker about WHICH check failed — mirrors Verify's own shape).
    public int? VerifyTicket(string? ticket, int instanceId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        var parts = ticket.Split('|');
        if (parts.Length != 5 || parts[0] != TicketTag) return null;
        var payload = string.Join('|', parts[..4]);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Sign(payload)),
                Encoding.UTF8.GetBytes(parts[4])))
            return null;
        if (!int.TryParse(parts[1], out var tokenInstanceId) || tokenInstanceId != instanceId) return null;
        if (!int.TryParse(parts[2], out var userId)) return null;
        if (!long.TryParse(parts[3], out var exp) || exp < now.ToUnixTimeSeconds()) return null;
        return userId;
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(_secret);
        return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private string Stamp(string passwordHash)
    {
        using var hmac = new HMACSHA256(_secret);
        return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes("pw:" + passwordHash)))[..16];
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }
}
