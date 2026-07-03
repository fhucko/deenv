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
