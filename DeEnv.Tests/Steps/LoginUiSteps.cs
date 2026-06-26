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

    // The PUBLIC-ROADMAP variant: serve an app where the data is publicly readable (bare `read`) but writes
    // are admin-gated. The app is NOT anonymousLockedOut, so the auto login-gate never fires — instead an
    // anonymous visitor sees the data AND a <SignInBar> to reach login. Seeds Ada's password like the locked
    // served-Given so the same login flow runs on top of the public policy.
    [Given("the access-fixture app is served as a public roadmap with admin password {string}")]
    public async Task GivenServedPublicWithPassword(string password)
    {
        ctx.Description = InstanceContext.AccessPublicFixtureDb();
        ctx.Server = new TestInstanceServer();
        await ctx.Server.StartAsync(ctx.Description, ctx.DataFilePath);
        ctx.Store = ctx.Server.Store;
        ctx.Store!.WriteField(InstanceContext.AccessAdminId, UserConvention.PasswordHashField,
            new TextValue(AuthCrypto.Hash(password)));
    }

    // A public app shows an anonymous visitor a <SignInBar> (the collapsed sign-in control) — the way to
    // reach login WITHOUT a reserved URL — and NO user menu (not logged in). Waits for it (the post-logout
    // refetch is async), so this also asserts the return-to-anonymous state after a logout.
    [Then("a sign-in control is shown")]
    public async Task ThenSignInControlShown()
    {
        await ctx.Page!.Locator(".sign-in-bar").WaitForAsync();
        await Assert.That(await ctx.Page.Locator(".user-menu").CountAsync()).IsEqualTo(0);
    }

    // Open the sign-in form: click the SignInBar's "Sign in" button, which flips its local state to render
    // the <LoginForm> in place (login-as-state — no navigation). The subsequent login step's locators
    // auto-wait for the revealed form.
    [When("the visitor opens the sign-in form")]
    public async Task WhenOpensSignInForm()
    {
        await ctx.Page!.Locator(".sign-in-bar button.sign-in").ClickAsync();
    }

    // Once logged in, the generic render shows the <UserMenu> (name + Log out, plus admin controls). Waits
    // for it (the login refetch is async) — proving the post-login chrome replaced the sign-in control.
    [Then("the user menu is shown")]
    public async Task ThenUserMenuShown()
    {
        await ctx.Page!.Locator(".user-menu").WaitForAsync();
        await Assert.That(await ctx.Page.Locator(".sign-in-bar").CountAsync()).IsEqualTo(0);
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

    // Log out THROUGH the UserMenu (sub-slice 1e-2): click the Log out button the generic render shows once
    // logged in. The button fires sys.logout → a `logout` WS op whose reply refetches as anonymous, so the
    // same URL re-renders with the ruled data denied and the gate back.
    //
    // Then WAIT for the logout to FULLY SETTLE (data-ready = the logout's refetch returned, refetchInFlight
    // cleared) before returning. A scenario that LOGS BACK IN right after (the create-user e2e: logout →
    // open sign-in → log in as the new user) must not fire the next login's refetch while the logout's
    // refetch is still in flight — the two coalesce, and the login's bound state is lost under the logout's
    // anonymous merge, so the user menu never reappears (a 30s flake, intermittent under load). Gating on
    // the settle makes the re-login act on a quiescent session; for a scenario that just asserts the
    // anonymous gate afterward, the wait is harmless (that state is already what data-ready settles to).
    [When("the visitor logs out through the user menu")]
    public async Task WhenLogsOut()
    {
        await ctx.Page!.Locator(".user-menu button.logout").ClickAsync();
        // Wait for the logout's view swap to LAND (the user menu gone — the anonymous re-render ran) and
        // THEN for the session to settle (data-ready, refetch no longer in flight). The disappearance gate
        // is essential: data-ready is still set from the logged-in state in the window between the click
        // and the logout reply, so waiting on it alone could return before the logout even starts its
        // refetch. Together they guarantee the logout is fully processed before the next interaction.
        await ctx.Page!.Locator(".user-menu").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await ctx.Page!.WaitReadyAsync();
    }

    // Read-only affordances (M-auth, sys.canWrite): a principal who cannot create sees no "New …" control.
    // Proves the gating CLIENT-side (the generic UI reads the shipped sys.canWrite cache after hydration).
    [Then("no create control is shown")]
    public async Task ThenNoCreateControl() =>
        await Assert.That(await ctx.Page!.Locator(".new-btn").CountAsync()).IsEqualTo(0);

    // After logging in as an admin the create control appears (sys.canWrite flips on the login refetch).
    [Then("a create control is shown")]
    public async Task ThenCreateControlShown() =>
        await ctx.Page!.Locator(".new-btn").First.WaitForAsync();

    // ── user administration (M-auth: the "Users" link in <UserMenu> → the generic User list /users; ──
    // ── set-password lives on each User's own object page /users/<id>) ──

    // An admin clicks the "Users" link in the user menu — gated on the canManageUsers capability (NOT the
    // shipped role) — which navigates to the generic User list at /users. Waits for the list's create
    // control (its SetTable `+ New` button), proving the page rendered.
    [When("the admin opens user management")]
    public async Task WhenOpensUserManagement()
    {
        await ctx.Page!.Locator(".user-menu a.manage-users").ClickAsync();
        await ctx.Page.Locator("button.new-btn").First.WaitForAsync();
    }

    // Create a User through the /users list's generic create-form (name + role; passwordHash is hidden, so
    // it is not in the form): open it, fill the name, pick the role, save. Waits for the new row to appear
    // AND its negative→real id remap to land (a POSITIVE data-key), not just the row's appearance: the next
    // step NAVIGATES to this user's page by its real id, so until the optimistic add is remapped the link
    // would point at a transient id. (The same remap gate the TodoApp add-flows use.)
    [When("the admin creates a user {string} with role {string}")]
    public async Task WhenCreatesUser(string name, string role)
    {
        await ctx.Page!.Locator("button.new-btn").First.ClickAsync();
        await ctx.Page.Locator(".create-form input.name").FillAsync(name);
        await ctx.Page.Locator(".create-form select.role").SelectOptionAsync(role);
        await ctx.Page.Locator(".create-form button.set-add").ClickAsync();
        await ctx.Page.Locator($".set-row:has-text(\"{name}\")").WaitForAsync();
        await ctx.Page.WaitForFunctionAsync(
            "name => { const r = [...document.querySelectorAll('.set-row')]" +
            ".find(e => e.textContent.includes(name)); return r != null && +r.getAttribute('data-key') > 0; }",
            name);
    }

    // Set a named user's password on that user's OWN object page (/users/<id>): navigate there via the
    // row's member link, then fill the page-level set-password control and submit (sys.setPassword).
    //
    // GATE on the hash actually PERSISTING before returning. setPassword is an admin-gated write
    // (User edit where currentUser.role == "Admin"); the scenario LOGS OUT right after, which flips the
    // session anonymous. Under peak load the logout can win the race, so the write lands against an
    // already-anonymous session and the floor REJECTS it — the hash is never stored and the later
    // re-login as this user silently fails (a wrong/empty password is not an error → the user menu never
    // appears, the 30s flake). Polling the store until the user has a non-empty passwordHash proves the
    // admin-gated write committed while still admin, so the subsequent login can verify against it. Polls
    // by name (the row text), reading the live store (the same seam LoginSteps writes the seed hash to).
    [When("the admin sets {string}'s password to {string}")]
    public async Task WhenSetsUserPassword(string name, string password)
    {
        // Navigate to the user's object page by clicking its row link, then wait for the page's
        // set-password control to render. (`div.set-password` disambiguates the control container from the
        // `button.set-password` submit it contains — both carry the `set-password` class.)
        await ctx.Page!.Locator($".set-row:has-text(\"{name}\") a.row-link").ClickAsync();
        var control = ctx.Page.Locator("div.set-password");
        await control.WaitForAsync();
        await control.Locator("input.new-password").FillAsync(password);
        await control.Locator("button.set-password").ClickAsync();
        await Polling.EventuallyAsync(() => ctx.Store!.ReadExtent("User").Values.Any(u =>
            u.Fields.TryGetValue("name", out var n) && n is TextValue { Text: var nm } && nm == name
            && u.Fields.TryGetValue(UserConvention.PasswordHashField, out var h)
            && h is TextValue { Text.Length: > 0 }), $"{name}'s password to persist");
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
