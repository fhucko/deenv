using System.Runtime.CompilerServices;

namespace DeEnv.Tests.TestSupport;

// Test-suite bootstrap: runs ONCE at test-assembly load, before any test — and, crucially, before
// AuthCrypto's static init reads the same env var (AuthCrypto lives in the DeEnv assembly and is first
// touched inside a test, well after this module initializer).
//
// Lowers the PBKDF2 iteration count to a trivial value for the whole run. Nearly every test logs in (all
// ~48 designer scenarios seed+verify an admin, plus the Login/Access suites), and PBKDF2 at the production
// 210k OWASP floor is DELIBERATELY CPU-heavy — so a parallel suite of login-doing tests saturates the cores
// and slows the whole browser run enough to intermittently blow the 10s view-swap waits (the
// LoginViewSwap/LogoutViewSwapTests flake). Tests exercise the login PATH, never crypto STRENGTH, so a
// count of 1 is correct here. Production never sets this var → the 210k floor stands (see AuthCrypto).
internal static class TestBootstrap
{
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("DEENV_PBKDF2_ITERATIONS", "1");
}
