using DeEnv.Code;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Code;

// M-auth login UI sub-slice 1e-1 — the DETERMINISTIC client-runtime proof of the post-login VIEW SWAP.
// This is the PRIMARY proof of the bug the prior attempt got stuck on: after a successful login the
// synthesized generic `fn render()` correctly stops gating (currentUser is now non-null) and returns the
// resolved DATA view (<ObjectForm> for the Db root) — but the DOM kept showing the <LoginForm>.
//
// ROOT CAUSE (a CLIENT issue, hence proven here, not in a browser-flow Gherkin): a component memoizes by
// its render-tree SLOT, and BOTH the gate's `return <LoginForm>` and the post-login `return <ObjectForm>`
// are returned in VALUE position from `render` (not tag-children), so both key on the SAME (empty/root)
// slot path "comp:". A client-side NAVIGATION already drops the `comp:` slot-cache (resetViewState) so a
// root-view swap across URLs works — but LOGIN IS A STATE CHANGE AT THE SAME URL (login is a state, not a
// route), and the login→refetch reply deliberately PRESERVES `comp:` entries (component state). So the
// stale <LoginForm> view sat under the root slot and `renderUi` handed it back for the <ObjectForm> call.
//
// The FIX (ws.ts login reply): treat the login flip as the wholesale render-tree rebuild it is — drop the
// `comp:` slot-cache (resetViewState, the SAME helper a navigation uses) before the refetch, so the data
// view re-runs fresh at the root slot and the DOM reconciler swaps <main.login-form> → <div.object-form>.
//
// DETERMINISM: this drives the REAL client over a REAL WS login (the production path: fill the bound
// inputs → Submit → sys.login → WS `login` op → ok reply → refetch → mergeState → renderUi), but every
// wait POLLS the real DOM/URL outcome (Playwright auto-waiting + WaitForFunction) — NO fixed sleeps. It
// gates on `data-ready` (WS open + session claimed) before submitting, so the login op acts on an
// established connection. The assertion ("object-form appears AND login-form is gone") is the swap itself;
// BEFORE the fix it FAILS (the object-form never appears — the stale login view persists).
public sealed class LoginViewSwapTests
{
    [Test]
    public async Task After_login_the_root_view_swaps_from_login_form_to_the_data_view()
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
            // claimed) — the login op needs an established connection for its reply to drive the refetch.
            await page.GotoContentAsync("/");
            await page.WaitReadyAsync();

            // The gate is in play: the login form is shown INSIDE #app, and the ruled data ("Gate #3") is
            // denied to the anonymous read floor, so the data view is absent. This is the pre-login state
            // the swap must move AWAY from.
            await Assert.That(await page.Locator("#app .login-form").CountAsync()).IsEqualTo(1);
            await Assert.That(await page.Locator("#app .object-form").CountAsync()).IsEqualTo(0);
            await Assert.That(await page.ContentAsync()).DoesNotContain("Gate #3");

            // Log in through the real form (fill the two-way-bound inputs + click Submit → sys.login).
            await page.Locator("#app .login-form input.name").FillAsync("Ada");
            await page.Locator("#app .login-form input.password").FillAsync("hunter2");
            await page.Locator("#app .login-form button.login-submit").ClickAsync();

            // THE SWAP (the assertion that fails before the fix): the login reply refetches as the bound
            // admin, mergeState flips currentUser non-null, and the re-render returns <ObjectForm> at the
            // root slot. Poll the live DOM (the WS round-trip is async): the data view appears AND the login
            // form is gone. Before the fix the stale `comp:` LoginForm persists, so .object-form never shows.
            try
            {
                await page.WaitForFunctionAsync(
                    "() => !!document.querySelector('#app .object-form') && !document.querySelector('#app .login-form')",
                    null, new PageWaitForFunctionOptions { Timeout = TestTimeouts.ActionMs });
            }
            catch (TimeoutException)
            {
                var app = await page.Locator("#app").InnerHTMLAsync();
                throw new Exception("View did not swap from login-form to object-form. Console:\n" +
                    string.Join("\n", logs) + "\n--- #app (first 1200) ---\n" + app[..Math.Min(1200, app.Length)]);
            }

            // The ruled data is now readable (the refetch ran as the admin), confirming the swap landed on
            // the real authoritative view, not an empty shell.
            await page.WaitForFunctionAsync(
                "() => document.body.innerText.includes('Gate #3')",
                null, new PageWaitForFunctionOptions { Timeout = TestTimeouts.ActionMs });
        }
        finally
        {
            await page.Context.CloseAsync();
            try { File.Delete(dataPath); } catch { /* best-effort */ }
        }
    }
}
