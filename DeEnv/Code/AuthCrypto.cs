using System.Security.Cryptography;
using System.Text;

namespace DeEnv.Code;

// Password hashing + verification for the `password` TYPE (M-auth — a User's `password`-typed credential
// field). KERNEL code — a sibling of AccessFloor, consulted by the WS write chokepoint (WsHandler hashes a
// plaintext before the store) and the login floor (verifies against the stored hash) — NOT callable from
// app Code: app authors never touch crypto, the framework handles it server-side. BCL-only (no NuGet):
// PBKDF2 via Rfc2898DeriveBytes, the .NET-built-in password-based KDF.
//
// The stored value is a SELF-DESCRIBING string so Verify needs no out-of-band parameters — it reads the
// algorithm, iteration count, and salt back out of the stored hash:
//
//     pbkdf2$sha256$<iterations>$<base64 salt>$<base64 derived key>
//
// 16-byte random salt, 210,000 iterations (OWASP's PBKDF2-SHA256 floor), a 32-byte derived key. The
// compare is constant-time (CryptographicOperations.FixedTimeEquals) so verification leaks no timing
// signal about how much of the hash matched. The format is self-describing on PURPOSE: the iteration
// count can be raised later without a migration — old hashes verify against their own stored count.
public static class AuthCrypto
{
    private const string Algorithm = "pbkdf2";
    private const string Prf = "sha256";          // the PBKDF2 pseudo-random function (HMAC-SHA256)
    private const int SaltBytes = 16;
    // Iteration count = the OWASP PBKDF2-SHA256 floor (210k) in production. Overridable ONCE via the
    // DEENV_PBKDF2_ITERATIONS env var so the TEST host can run a trivial count: nearly every test logs in
    // and PBKDF2 is deliberately CPU-heavy, so 210k across a parallel suite of login-doing tests saturates
    // the cores and slows the whole browser run enough to intermittently blow the view-swap waits (the
    // LoginViewSwap/LogoutViewSwap flake). Production never sets the var → the floor stands. Safe to mix
    // counts across environments: the hash is self-describing, so every hash verifies against ITS OWN
    // stored iteration count regardless of this value.
    private static readonly int Iterations =
        int.TryParse(Environment.GetEnvironmentVariable("DEENV_PBKDF2_ITERATIONS"), out var n) && n > 0
            ? n : 210_000;
    private const int KeyBytes = 32;
    private static readonly HashAlgorithmName HashName = HashAlgorithmName.SHA256;
    // A valid-format hash NO real password verifies against — verified on the login MISS branch so an
    // unknown username costs the same PBKDF2 as a known one (no username-enumeration timing signal). Computed
    // from the CURRENT Iterations (fixed salt) so the miss path matches the hit path's cost in EVERY
    // environment, including the test host's lowered count. The bytes are irrelevant — it exists only to be
    // hashed-against and fail.
    public static readonly string DummyHash = BuildDummyHash();

    private static string BuildDummyHash()
    {
        var salt = new byte[SaltBytes]; // fixed all-zero salt — a dummy that must never verify
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("\0"), salt, Iterations, HashName, KeyBytes);
        return string.Join('$', Algorithm, Prf, Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(key));
    }

    // Hash a plaintext password into the self-describing storage string (fresh random salt each call).
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashName, KeyBytes);
        return string.Join('$', Algorithm, Prf, Iterations,
            Convert.ToBase64String(salt), Convert.ToBase64String(key));
    }

    // Verify a plaintext password against a stored hash string. Reads the iteration count + salt back out
    // of `stored`, re-derives the key with the SAME parameters, and compares constant-time. Returns false
    // (never throws) for any unparsable/mismatched stored value — a malformed or empty hash simply fails
    // to verify, the safe default for an authentication check.
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 5 || parts[0] != Algorithm || parts[1] != Prf) return false;
        if (!int.TryParse(parts[2], out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false; // a non-base64 salt/key — treat as a non-verifying hash
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashName, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
