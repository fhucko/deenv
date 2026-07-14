using DeEnv.Code;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Code;

// M-auth login UI sub-slice 1e-2 — the DETERMINISTIC client-runtime proof of the post-logout VIEW SWAP
// (the MIRROR of LoginViewSwapTests). After logging in, the generic page shows the <UserMenu> (with the
// Log out button) + the resolved DATA view (<ObjectForm> for the Db root). Clicking Log out fires
// sys.logout → a `logout` WS op; the reply must swap the root view BACK to the anonymous <LoginForm> gate
// at the SAME URL (logout is a state, not a route — the ruled data is now denied to the anonymous floor).
//
// ROOT CAUSE the fix addresses (a CLIENT issue, hence proven here, not in a browser-flow Gherkin): the
// logged-in root view (<UserMenu> + <ObjectForm>, wrapped) and the post-logout <LoginForm> are BOTH
// returned in VALUE position from the synthesized `fn render()`, so the root component keys on the same
// (empty/root) slot path "comp:". A `logout` refetch reply that did NOT drop the `comp:` slot-cache would
// leave the stale logged-in view under the root slot — renderUi would hand it back for the <LoginForm>
// call and the page would never return to the gate.
//
// The FIX (ws.ts logout reply, the SAME helper login uses): treat the logout flip as the wholesale
// render-tree rebuild it is — resetViewState() drops the `comp:` slot-cache before the refetch, so the
// gate re-runs fresh at the root slot and the DOM reconciler swaps the data view back to <main.login-form>.
//
// DETERMINISM: drives the REAL client over a REAL WS login THEN logout (the production path), but every
// wait POLLS the real DOM (Playwright auto-waiting + WaitForFunction) — NO fixed sleeps. BEFORE the logout
// wiring this FAILS (the login form never comes back — the stale logged-in view persists).
public sealed class LogoutViewSwapTests
{
    [Test]
    public async Task After_logout_the_root_view_swaps_back_from_the_data_view_to_the_login_form()
    {
        var desc = InstanceContext.AccessFixtureDb(); // Milestone read-ruled to Admin → anonymousLockedOut
        var dataPath = Path.GetTempFileName();
        await using var server = new TestInstanceServer();
        await server.StartAsync(desc, dataPath);

        // Seed Ada's (the admin's) password into the LIVE store the same way LoginSteps does — AFTER the
        // server/store exists (the hash is not in initialData). Login verifies against this hash.
        server.Store!.WriteField(InstanceContext.AccessAdminId, InstanceContext.AccessPasswordField,
            new TextValue(AuthCrypto.Hash("hunter2")));

        var page = await SharedBrowser.NewPageAsync(server.BaseUrl);
        var logs = new List<string>();
        page.Console += (_, m) => logs.Add($"[{m.Type}] {m.Text}");
        page.PageError += (_, e) => logs.Add($"[pageerror] {e}");
        try
        {
            // Open `/` as an ANONYMOUS visitor and wait for FULL readiness (hydration + WS open + session
            // claimed) — the login/logout ops need an established connection for their replies to refetch.
            await page.GotoContentAsync("/");
            await page.WaitReadyAsync();

            // Pre-login: the gate shows the login form; the ruled data is denied. Log in through the real
            // form (the production path, proven by LoginViewSwapTests) to reach the bound view.
            await page.Locator("#app .login-form input.name").FillAsync("Ada");
            await page.Locator("#app .login-form input.password").FillAsync("hunter2");
            await page.Locator("#app .login-form button.login-submit").ClickAsync();

            // Logged-in state: the data view appears AND the UserMenu (with the Log out button) is present.
            // This is the state the LOGOUT swap must move AWAY from. Poll the live DOM (the WS round-trip is
            // async); the Log out button is what the logout half of the slice adds.
            await page.WaitForFunctionAsync(
                "() => !!document.querySelector('#app .object-form') && !!document.querySelector('#app .user-menu button.logout')",
                null, new PageWaitForFunctionOptions { Timeout = TestTimeouts.ActionMs });
            await page.WaitForFunctionAsync(
                "() => document.body.innerText.includes('Gate #3')",
                null, new PageWaitForFunctionOptions { Timeout = TestTimeouts.ActionMs });

            // Log out through the UserMenu.
            await page.Locator("#app .user-menu button.logout").ClickAsync();

            // THE SWAP (the assertion that fails before the fix): the logout reply refetches as anonymous,
            // mergeState flips currentUser back to null, the access floor denies the ruled data, and the
            // re-render returns <LoginForm> at the root slot. Poll the live DOM: the login form is back AND
            // the data view is gone. Before the fix the stale `comp:` logged-in view persists, so the
            // login form never reappears.
            try
            {
                await page.WaitForFunctionAsync(
                    "() => !!document.querySelector('#app .login-form') && !document.querySelector('#app .object-form')",
                    null, new PageWaitForFunctionOptions { Timeout = TestTimeouts.ActionMs });
            }
            catch (TimeoutException)
            {
                var app = await page.Locator("#app").InnerHTMLAsync();
                throw new Exception("View did not swap back from the data view to the login-form. Console:\n" +
                    string.Join("\n", logs) + "\n--- #app (first 1200) ---\n" + app[..Math.Min(1200, app.Length)]);
            }

            // The ruled data is denied again (the refetch ran as anonymous), confirming the swap landed on
            // the real anonymous gate, not a stale shell still showing the admin's data.
            await page.WaitForFunctionAsync(
                "() => !document.body.innerText.includes('Gate #3')",
                null, new PageWaitForFunctionOptions { Timeout = TestTimeouts.ActionMs });
        }
        finally
        {
            await page.Context.CloseAsync();
            try { File.Delete(dataPath); } catch { /* best-effort */ }
        }
    }
}
