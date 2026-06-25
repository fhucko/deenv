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
    // same URL re-renders with the ruled data denied and the gate back. (No extra readiness gate: the WS was
    // already established for login and stays open; the button only exists after the post-login refetch ran.)
    [When("the visitor logs out through the user menu")]
    public async Task WhenLogsOut()
    {
        await ctx.Page!.Locator(".user-menu button.logout").ClickAsync();
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

    // ── user administration (M-auth, the library <UserAdmin> reached from <UserMenu>) ──

    // An admin clicks "Manage users" in the user menu — gated on the canManageUsers capability (NOT the
    // shipped role) — which toggles the <UserAdmin> panel into view. Waits for the panel.
    [When("the admin opens user management")]
    public async Task WhenOpensUserManagement()
    {
        await ctx.Page!.Locator(".user-menu button.manage-users").ClickAsync();
        await ctx.Page.Locator(".user-admin").WaitForAsync();
    }

    // Create a User through the panel's generic create-form (name + role; passwordHash is hidden, so it is
    // not in the form): open it, fill the name, pick the role, save. Waits for the new row to appear (the
    // optimistic add) before returning, so a following set-password addresses a present row.
    [When("the admin creates a user {string} with role {string}")]
    public async Task WhenCreatesUser(string name, string role)
    {
        var panel = ctx.Page!.Locator(".user-admin");
        await panel.Locator("button.new-btn").ClickAsync();
        await panel.Locator(".create-form input.name").FillAsync(name);
        await panel.Locator(".create-form select.role").SelectOptionAsync(role);
        await panel.Locator(".create-form button.set-add").ClickAsync();
        await panel.Locator($".set-row:has-text(\"{name}\")").WaitForAsync();
    }

    // Set a named user's password via that row's set-password control (sys.setPassword). The just-created
    // user may still carry its transient negative id, but the server resolves it through the session remap
    // (the arrayAdd was sent first, WS messages are ordered) — so no wait-for-remap is needed.
    [When("the admin sets {string}'s password to {string}")]
    public async Task WhenSetsUserPassword(string name, string password)
    {
        var row = ctx.Page!.Locator($".user-admin .set-row:has-text(\"{name}\")");
        await row.Locator("input.new-password").FillAsync(password);
        await row.Locator("button.set-password").ClickAsync();
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
