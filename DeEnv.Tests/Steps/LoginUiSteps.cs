using System.Text.RegularExpressions;
using DeEnv.Code;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// M-auth login UI sub-slice 1e-1 — "log in from the UI and see ruled data appear" (the CLIENT half;
// the server login-as-state bind is already done — see LoginSteps). Driven END-TO-END in a real browser
// over the production handler tree (TestInstanceServer + the shared Playwright browser), because the
// behavior under test is client-side: the auto-mode gate renders a <LoginForm> for an anonymous visitor
// (the app is read-ruled to Admin, so `anonymousLockedOut` is true), the form's Submit fires sys.login
// over the WS, and the success reply refetches so the SAME URL re-renders as the bound principal and the
// ruled data appears.
//
// The failing-before proof: WITHOUT the gate branch + the <LoginForm> component, an anonymous visitor has
// no way to log in — the read floor denies every Milestone, so the page is empty ("Gate #3" never shows)
// and there is no login control to act on. The de-flake playbook applies: hydration/readiness gates
// (data-hydrated / data-ready) and Playwright auto-waiting locators / WaitForFunction — NO fixed sleeps.
[Binding]
public sealed class LoginUiSteps(InstanceContext ctx)
{
    // Serve the access-fixture app (read-ruled to Admin → anonymousLockedOut is true) and seed Ada's
    // password into the live store the SAME way LoginSteps does (AuthCrypto.Hash + WriteField) — AFTER the
    // server/store exists, so the hash lands on the running instance's store (it is not in initialData).
    [Given("the access-fixture app is served with the admin password {string}")]
    public async Task GivenServedWithPassword(string password)
    {
        ctx.Description = InstanceContext.AccessFixtureDb();
        ctx.Server = new TestInstanceServer();
        await ctx.Server.StartAsync(ctx.Description, ctx.DataFilePath);
        ctx.Store = ctx.Server.Store;

        // Hash ONCE (AuthCrypto.Hash salts randomly, so re-hashing yields a different string) and write it;
        // verify the stored hash authenticates the password — the property login depends on, salt-independent.
        ctx.Store!.WriteField(InstanceContext.AccessAdminId, UserConvention.PasswordHashField,
            new TextValue(AuthCrypto.Hash(password)));
        var stored = ctx.Store!.ReadById(InstanceContext.AccessAdminId)!.Value.Fields
            .Fields[UserConvention.PasswordHashField];
        await Assert.That(stored is TextValue tv && AuthCrypto.Verify(password, tv.Text)).IsTrue();
    }

    // Open the path as an ANONYMOUS visitor (a fresh page on the shared browser — no principal bound, the
    // default WS session). Waits for hydration so the form's handlers are attached before the test acts.
    [Given("an anonymous visitor opens {string}")]
    public async Task GivenAnonymousOpens(string path)
    {
        ctx.Page = await SharedBrowser.NewPageAsync(ctx.BaseUrl);
        await ctx.Page.GotoReadyAsync(path);
    }

    // The gate renders the login form for an anonymous visitor (anonymousLockedOut && currentUser == null),
    // and the ruled data is NOT present (the read floor denied it). Both halves prove the gate is in play:
    // the form is shown INSTEAD of an empty page, and the denied "Gate #3" is absent everywhere.
    [Then("the login form is shown and {string} is not")]
    public async Task ThenLoginFormShown(string deniedText)
    {
        await ctx.Page!.Locator(".login-form").WaitForAsync();
        await Assert.That(await ctx.Page.Locator(".login-form input.name").CountAsync()).IsEqualTo(1);
        await Assert.That(await ctx.Page.Locator(".login-form input.password").CountAsync()).IsEqualTo(1);
        await Assert.That(await ctx.Page.ContentAsync()).DoesNotContain(deniedText);
    }

    // Log in THROUGH the form: fill the bound inputs and click Submit (which calls sys.login). Gate on full
    // readiness first (data-ready) — the WS must be open + the session claimed before sys.login is sent and
    // its reply can drive the refetch (an interim mutation-readiness gate, like every other mutating step).
    [When("the visitor logs in through the form as {string} with password {string}")]
    public async Task WhenLogsInThroughForm(string name, string password)
    {
        var page = ctx.Page!;
        await page.WaitReadyAsync();
        await page.Locator(".login-form input.name").FillAsync(name);
        await page.Locator(".login-form input.password").FillAsync(password);
        await page.Locator(".login-form button.login-submit").ClickAsync();
    }

    // The login success refetches and the page re-renders as the bound admin: the ruled data appears. Poll
    // the live DOM (the WS round-trip + refetch is async) — no fixed sleep.
    [Then("{string} eventually appears")]
    public async Task ThenEventuallyAppears(string text)
    {
        await ctx.Page!.WaitForFunctionAsync(
            "t => document.body.innerText.includes(t)", text,
            new PageWaitForFunctionOptions { Timeout = 10000 });
    }

    // Login is a STATE, not a route: the URL never changed (no /login redirect). The visitor stayed on the
    // requested path; only the rendered content flipped.
    [Then("the URL is still {string}")]
    public async Task ThenUrlStill(string path)
    {
        var page = ctx.Page!;
        await page.WaitForUrlContentAsync(new Regex(Regex.Escape(path) + "$"));
        await Assert.That(new Uri(page.Url).AbsolutePath).IsEqualTo(path);
    }
}
