using System.Text.Json;
using DeEnv.Code;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// M-auth login sub-slice 1a — the password mechanism + the WS login/logout bind. Modeled on
// AccessSteps.BoundWs, but the session starts ANONYMOUS and the principal is set by SENDING a `login`
// action over the WS (not by setting PrincipalUserId directly) — the same way the floor was proven
// before login existed. The bind is asserted by reading the session's PrincipalUserId back. Drives the
// login/logout scenarios and the two privacy assertions (now testable with a real, login-bound principal).
//
// Steps already defined in AccessSteps (the Background, `the page state is rendered for …`, `the shipped
// data includes …`, `the current user is …`, `the app has no access rules`) are REUSED here — Reqnroll
// shares step definitions across [Binding] classes by phrase, so only the NEW login/privacy phrases live
// here. The InstanceContext (ctx) carries the store/description; the session + reply are scenario-local.
[Binding]
public sealed class LoginSteps(InstanceContext ctx)
{
    private ClientSessionStore? _sessions;
    private ClientSession? _session;
    private string _reply = "";
    private string _seededHash = "";
    private string _setPasswordReply = "";
    private int _verifyCalls;

    // ── seed ────────────────────────────────────────────────────────────────────

    // Seed the admin's password: hash the plaintext with the kernel helper and write it RAW into Ada's
    // `password` field through the store seam (the store keeps the real hash; only the SHIPPED value is
    // blanked). The seed is in friendly scalar form, so the hash is not in initialData — it is written
    // here, AFTER any fixture rebuild a scenario did, against the live store. This is the legitimate
    // pre-hashed write (like AdminSeed) that bypasses the WS hash, so it must NOT be double-hashed.
    [Given("the admin user has the password {string}")]
    public async Task GivenAdminPassword(string password)
    {
        _seededHash = AuthCrypto.Hash(password);
        ctx.Store!.WriteField(InstanceContext.AccessAdminId, InstanceContext.AccessPasswordField, new TextValue(_seededHash));

        // Confirm it landed (and recover the exact stored string for the privacy assertion).
        var stored = ctx.Store!.ReadById(InstanceContext.AccessAdminId)!.Value.Fields
            .Fields[InstanceContext.AccessPasswordField];
        await Assert.That(stored).IsEqualTo((NodeValue)new TextValue(_seededHash));
    }

    // Seed the MEMBER's (Bob's, id 4) password the same way (login 1b) — so a denied setPassword can be
    // proven to have left the ORIGINAL hash intact (the original still verifies, the attempted-new does
    // not). Written into the live store after any fixture rebuild a scenario did.
    [Given("the member user has the password {string}")]
    public async Task GivenMemberPassword(string password)
    {
        _seededHash = AuthCrypto.Hash(password); // recover the exact stored string for a no-leak assertion
        ctx.Store!.WriteField(InstanceContext.AccessMemberId, InstanceContext.AccessPasswordField, new TextValue(_seededHash));
        var stored = ctx.Store!.ReadById(InstanceContext.AccessMemberId)!.Value.Fields
            .Fields[InstanceContext.AccessPasswordField];
        await Assert.That(stored).IsEqualTo((NodeValue)new TextValue(_seededHash));
    }

    // ── the User access rule (login 1b: setPassword is gated as a `User edit`) ───

    // Install a `User` access rule (e.g. `User edit where currentUser.role == "Admin"`) so setPassword is
    // gated. The rule line is the FULL "User <verbs> [where <cond>]"; the leading "User " is stripped and
    // accumulated, then the fixture is rebuilt from BOTH the Milestone lines (Background) and the User
    // lines — parsed by AppParse exactly as the app's own `access` section would be — and the rule asserted
    // present. The store is rebuilt over the SAME seed (identical types ⇒ the seeded users still fit).
    [Given("the User access rule {string}")]
    public async Task GivenUserAccessRule(string rule)
    {
        rule = rule.Replace("\\\"", "\"");
        const string prefix = "User ";
        await Assert.That(rule.StartsWith(prefix)).IsTrue();
        ctx.UserAccessRuleLines.Add(rule[prefix.Length..]);

        ctx.Description = InstanceContext.AccessFixtureWithRules(
            ctx.AccessRuleLines.ToArray(), ctx.UserAccessRuleLines.ToArray());
        ctx.Store = new JsonFileInstanceStore(ctx.DataFilePath, ctx.Description);

        // Sanity: a User rule is now active (any verb — edit for setPassword/management, create for the
        // create-control gating). Asserting Type only keeps this step reusable across User rule verbs.
        await Assert.That(ctx.Description!.Rules!.Any(r => r.Type == "User")).IsTrue();
    }

    // ── set-password-as-a-field (the M-auth `password` type) ─────────────────────

    // Set a User's password the SAME way the UI does now: an objectPropChange EDIT of the User's `password`
    // field (no bespoke setPassword op — setting a password is just editing a field). The WS layer hashes the
    // plaintext before the store (the write chokepoint); the EXISTING write floor gates it as a `User edit`.
    // Sent over a WS session bound to the currently-chosen principal (ctx.PrincipalUserId, set by "the current
    // user is …"), exactly the way AccessSteps binds the write-floor scenarios. The `{word}` (admin/member)
    // is the actor label; the principal decides. The reply is captured for the succeeds/rejected assertions.
    [When("the {word} sets user {int}'s password to {string}")]
    public void WhenSetsPassword(string _actor, int userId, string newPassword)
    {
        _sessions ??= new ClientSessionStore();
        var session = _sessions.Create();
        session.PrincipalUserId = ctx.PrincipalUserId;
        var ws = new WsHandler(ctx.Store!, ctx.Description!, _sessions);
        _setPasswordReply = ws.ProcessMessage(
            $$"""{ "op": "objectPropChange", "clientId": "{{session.Id}}", "objectId": {{userId}}, "prop": "{{InstanceContext.AccessPasswordField}}", "value": { "type": "text", "value": "{{newPassword}}" } }""");
    }

    // The edit was accepted (the floor allowed the `User edit`): an objectPropChange ok reply, no error. The
    // store now holds the PBKDF2 hash of the new plaintext (asserted end-to-end by the member re-login).
    [Then("the setPassword succeeds")]
    public async Task ThenSetPasswordSucceeds()
    {
        using var doc = JsonDocument.Parse(_setPasswordReply);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("op").GetString()).IsEqualTo("objectPropChange");
        await Assert.That(root.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(root.TryGetProperty("error", out _)).IsFalse();
    }

    // A denied edit IS the `{ error }` reply (the write floor throws → the client rolls back), exactly like
    // any other denied objectPropChange — the original hash is untouched (the member can still log in with it).
    [Then("the setPassword is rejected")]
    public async Task ThenSetPasswordRejected()
    {
        using var doc = JsonDocument.Parse(_setPasswordReply);
        var root = doc.RootElement;
        await Assert.That(root.TryGetProperty("error", out _)).IsTrue();
    }

    // ── the member logs in (proves a written/unchanged hash is verifiable) ───────

    // Log in as the member (Bob) by sending a `login` over a fresh anonymous session — the end-to-end proof
    // that a hash setPassword wrote (or left intact) actually authenticates. Returns the reply's `ok` so the
    // caller asserts success/failure. By name "Bob" (the seeded member), the SAME path a real login takes.
    private bool MemberLoginOk(string password)
    {
        var sessions = new ClientSessionStore();
        var session = sessions.Create();
        var ws = new WsHandler(ctx.Store!, ctx.Description!, sessions);
        _reply = ws.ProcessMessage(
            $$"""{ "op": "login", "clientId": "{{session.Id}}", "name": "Bob", "password": "{{password}}" }""");
        using var doc = JsonDocument.Parse(_reply);
        return doc.RootElement.GetProperty("ok").GetBoolean();
    }

    [When("the member logs in with password {string}")]
    public void WhenMemberLogsIn(string password) => MemberLoginOk(password);

    // After a successful member login the reply names the member principal (id 4) — proves the hash
    // setPassword wrote verifies AND binds the right user.
    [Then("the login succeeds as the member")]
    public async Task ThenLoginSucceedsAsMember()
    {
        using var doc = JsonDocument.Parse(_reply);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("op").GetString()).IsEqualTo("login");
        await Assert.That(root.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("userId").GetInt32()).IsEqualTo(InstanceContext.AccessMemberId);
    }

    [Then("the member can log in with password {string}")]
    public async Task ThenMemberCanLogIn(string password) =>
        await Assert.That(MemberLoginOk(password)).IsTrue();

    [Then("the member cannot log in with password {string}")]
    public async Task ThenMemberCannotLogIn(string password) =>
        await Assert.That(MemberLoginOk(password)).IsFalse();

    // ── the WS session (anonymous; the principal is set by `login`) ──────────────

    // Mint a fresh WS session with NO principal — login must set it. The clientId threads into each `login`/
    // `logout`/refetch op so the handler resolves this session; PrincipalUserId is read back to prove the bind.
    [Given("an anonymous WS session")]
    public async Task GivenAnonymousSession()
    {
        _sessions = new ClientSessionStore();
        _session = _sessions.Create();
        await Assert.That(_session.PrincipalUserId).IsNull();
    }

    // ── login / logout (driven through the WS action) ────────────────────────────

    [When("the session logs in as {string} with password {string}")]
    [Given("the session logs in as {string} with password {string}")]
    public void WhenLogsIn(string name, string password)
    {
        _verifyCalls = 0;
        var ws = new WsHandler(ctx.Store!, ctx.Description!, _sessions, verifyPassword: (plain, hash) =>
        {
            _verifyCalls++;
            return AuthCrypto.Verify(plain, hash);
        });
        _reply = ws.ProcessMessage(
            $$"""{ "op": "login", "clientId": "{{_session!.Id}}", "name": "{{name}}", "password": "{{password}}" }""");
    }

    [When("the session logs out")]
    public void WhenLogsOut()
    {
        var ws = new WsHandler(ctx.Store!, ctx.Description!, _sessions);
        _reply = ws.ProcessMessage($$"""{ "op": "logout", "clientId": "{{_session!.Id}}" }""");
    }

    // ── reply + session-principal assertions ─────────────────────────────────────

    [Then("the login succeeds as the admin")]
    public async Task ThenLoginSucceedsAsAdmin()
    {
        using var doc = JsonDocument.Parse(_reply);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("op").GetString()).IsEqualTo("login");
        await Assert.That(root.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("userId").GetInt32()).IsEqualTo(InstanceContext.AccessAdminId);
        // A success is NOT an error reply.
        await Assert.That(root.TryGetProperty("error", out _)).IsFalse();
    }

    // A failure is a NORMAL negative reply ({ ok:false }, no userId), NOT routed through the `{ error }`
    // rollback path — wrong-password and unknown-user produce the SAME reply (no user-enumeration signal).
    [Then("the login fails")]
    public async Task ThenLoginFails()
    {
        using var doc = JsonDocument.Parse(_reply);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("op").GetString()).IsEqualTo("login");
        await Assert.That(root.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(root.TryGetProperty("userId", out _)).IsFalse();
        await Assert.That(root.TryGetProperty("error", out _)).IsFalse();
    }

    [Then("the logout succeeds")]
    public async Task ThenLogoutSucceeds()
    {
        using var doc = JsonDocument.Parse(_reply);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("op").GetString()).IsEqualTo("logout");
        await Assert.That(root.GetProperty("ok").GetBoolean()).IsTrue();
    }

    // The bind is observed by reading the session's PrincipalUserId back (the durable home the renderer +
    // floors read) — proving `login` SET it, not the harness.
    [Then("the session principal is the admin")]
    public async Task ThenSessionPrincipalAdmin() =>
        await Assert.That(_session!.PrincipalUserId).IsEqualTo(InstanceContext.AccessAdminId);

    [Then("the session principal is anonymous")]
    public async Task ThenSessionPrincipalAnonymous() =>
        await Assert.That(_session!.PrincipalUserId).IsNull();

    [Then("the password verifier ran once")]
    public async Task ThenPasswordVerifierRanOnce() =>
        await Assert.That(_verifyCalls).IsEqualTo(1);

    // ── render as the logged-in session ──────────────────────────────────────────

    // Render at the path with the SESSION's bound principal (read back from PrincipalUserId) — so the
    // floor + currentUser reflect the real login, exactly as the production SSR/refetch path would. The
    // rendered document is captured on ctx.RenderedHtml so the reused `the shipped data includes …` /
    // privacy Then steps observe it.
    [When("the page is rendered for {string} as the logged-in session")]
    public void WhenRenderedAsSession(string path)
    {
        var renderer = new SsrRenderer(ctx.Store!, ctx.Description!);
        ctx.RenderedHtml = renderer.Render(path, principalUserId: _session!.PrincipalUserId).Html;
    }

    // ── privacy assertions (a real principal makes these testable) ───────────────

    // (a) the literal PBKDF2 hash string never appears in the whole shipped document — RULE-INDEPENDENT
    // (this Then is asserted under both the ruled fixture and the dormant no-rules fixture).
    [Then("the rendered document does not contain the admin's password hash")]
    public async Task ThenNoHash()
    {
        await Assert.That(_seededHash.Length).IsGreaterThan(0);
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(ctx.RenderedHtml!.Contains(_seededHash)).IsFalse();
    }

    // (a, continued) not even the self-describing `pbkdf2$…` marker leaks (a stronger check than the exact
    // string — catches any partial/transformed hash that still carries the algorithm prefix).
    [Then("the rendered document does not contain the {string} marker")]
    public async Task ThenNoMarker(string marker)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        await Assert.That(ctx.RenderedHtml!.Contains(marker)).IsFalse();
    }

    // (b) the principal's SENSITIVE fields never leak: the `role`, read by the access condition to admit the
    // milestone, is read over a THROWAWAY context and so never enters the shipped graph; the `password`-typed
    // credential is BLANKED to "" at the load chokepoint (the M-auth `password` type), so even when the
    // principal ships the field it carries "" not the hash. The principal SHIPS as a scope reference
    // (window.initData scope.currentUser → an object id); its serialized object carries the fields the render
    // legitimately DISPLAYED, and the role/hash are not among the values.
    //
    // 1e-2 NOTE: this used to assert the principal object is FIELDS-LESS (zero props). That was a proxy that
    // held only while NOTHING read currentUser's fields. The <UserMenu> (the logged-in chrome) now displays
    // currentUser.name, so the principal legitimately ships its `name` leaf — the user is allowed to see
    // their OWN name; it is not a privacy leak. So the assertion is narrowed to the REAL invariant the
    // scenario name promises: the principal ships NO `role` field, its `password` field (if present) is "" —
    // NEVER the hash — and no pbkdf2 marker appears anywhere in its serialized object. (`password` is now a
    // PRESENT "" rather than absent — the `password`-type caveat — so the check is value-based, not
    // key-absence.) The role-VALUE string check below is unchanged and still the strongest guard (a
    // whole-document "Admin" check would be WRONG — "Admin" legitimately appears as the `Role` enum value
    // name in the shipped schema descriptor; this drills into the principal's own object entry, immune to
    // that metadata). Rendered at /milestones/2 (no users table), so the principal id is not also a displayed
    // db.users row.
    [Then("the rendered document does not expose the current user's role {string}")]
    public async Task ThenNoRole(string role)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();

        var initData = ExtractInitData(ctx.RenderedHtml!);
        using var doc = JsonDocument.Parse(initData);
        var principal = doc.RootElement
            .GetProperty("leaves").GetProperty("objects")
            .GetProperty(InstanceContext.AccessAdminId.ToString())
            .GetProperty("props");
        // The principal's role (read only by the access condition, over a throwaway context) never enters the
        // shipped object as a key.
        var props = principal.EnumerateObject().Select(p => p.Name).ToList();
        await Assert.That(props).DoesNotContain("role");
        // The credential field, when shipped (the `password` type blanks it to a PRESENT ""), carries "" — never
        // the hash — and its self-describing pbkdf2 marker never appears in the principal's serialized object. A
        // shipped leaf is { type:"simple", value:{ type:"text", value:"" } }; at /milestones/2 nothing reads
        // currentUser.password, so it is typically not even accessed/shipped — but if it is, it must be blank.
        if (principal.TryGetProperty(InstanceContext.AccessPasswordField, out var pw))
            await Assert.That(pw.GetProperty("value").GetProperty("value").GetString() ?? "").IsEqualTo("");
        await Assert.That(principal.GetRawText().Contains("pbkdf2")).IsFalse();
        await Assert.That(principal.GetRawText().Contains(_seededHash)).IsFalse();
        // And the role value is nowhere inside the principal's serialized object (belt-and-suspenders).
        await Assert.That(principal.GetRawText().Contains(role)).IsFalse();
    }

    // The password-typed field on the rendered USER object ships BLANK ("") into the data graph — never the
    // stored hash (the read chokepoint of the `password` type). The user's object entry is in the leaves; its
    // `password` prop, when shipped, is { type:"text", value:"" }. Combined with the no-hash / no-pbkdf2
    // document checks, this proves a user's own object page (where the field IS displayed, unlike /milestones)
    // ships the masked field's value as "" and the secret never crosses the wire.
    [Then("the user {int}'s password field ships blank in the graph")]
    public async Task ThenUserPasswordBlank(int userId)
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        var initData = ExtractInitData(ctx.RenderedHtml!);
        using var doc = JsonDocument.Parse(initData);
        var props = doc.RootElement.GetProperty("leaves").GetProperty("objects")
            .GetProperty(userId.ToString()).GetProperty("props");
        // The field is shipped (the user page displays it) and it is blank — never the hash. A shipped leaf is
        // { type:"simple", value:{ type:"text", value:"" } }.
        await Assert.That(props.TryGetProperty(InstanceContext.AccessPasswordField, out var pw)).IsTrue();
        await Assert.That(pw.GetProperty("value").GetProperty("value").GetString() ?? "").IsEqualTo("");
        // And the seeded hash / its marker appear nowhere in the whole shipped document.
        await Assert.That(ctx.RenderedHtml!.Contains(_seededHash)).IsFalse();
        await Assert.That(ctx.RenderedHtml!.Contains("pbkdf2")).IsFalse();
    }

    // The SetTable's DEFAULT columns must EXCLUDE the `password`-typed field (a secret is not a scannable
    // list value — the same exclusion `Scalars`/labelProp already apply, now also on the SetTable
    // default-columns path). The users list's SSR-rendered column-header row (`<tr class="set-head">`) has
    // NO "Password" <th>; the data rows (`<tr class="set-row">`) carry no password cell. FAIL-BEFORE:
    // without excluding p.baseType == "password" from the SetTable header/row foreach, a spurious "Password"
    // <th> + blank cells render here.
    //
    // Scoped to the RENDERED ELEMENTS (the actual `class="set-head"`/`class="set-row"` tags), NOT a loose
    // "Password" search: the page's hydration island (window.initUi) legitimately carries the create-form's
    // "Password" label + masked input (you DO set a password when creating a user — the create form is right
    // to have the field), so a whole-document search would false-positive on that.
    [Then("the rendered users list has no password column")]
    public async Task ThenNoPasswordColumn()
    {
        await Assert.That(ctx.RenderedHtml).IsNotNull();
        var html = ctx.RenderedHtml!;
        // The column-header row: from `class="set-head">` to its `</tr>`. None of its <th>s is "Password".
        var head = ElementInner(html, "class=\"set-head\">", "</tr>");
        await Assert.That(head).IsNotNull();
        await Assert.That(head!.Contains("Password")).IsFalse();
        // No data row carries a password cell: each `<tr class="set-row">` (to its `</tr>`) has no "Password"
        // text and no masked input (the read-only table shows the label value + scalar cells, never a secret).
        var idx = 0;
        var sawRow = false;
        while ((idx = html.IndexOf("class=\"set-row\">", idx, StringComparison.Ordinal)) >= 0)
        {
            sawRow = true;
            var row = ElementInner(html, "class=\"set-row\">", "</tr>", idx)!;
            await Assert.That(row.Contains("type=\"password\"")).IsFalse();
            await Assert.That(row.Contains("Password")).IsFalse();
            idx += "class=\"set-row\">".Length;
        }
        await Assert.That(sawRow).IsTrue(); // the seeded users render rows (a real list, not empty)
    }

    // The inner text of the first element whose open-tag-fragment `openMarker` appears at/after `from`,
    // up to the next `closeMarker`. A small SSR-HTML slice helper for the column/row assertions.
    private static string? ElementInner(string html, string openMarker, string closeMarker, int from = 0)
    {
        var start = html.IndexOf(openMarker, from, StringComparison.Ordinal);
        if (start < 0) return null;
        start += openMarker.Length;
        var end = html.IndexOf(closeMarker, start, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }

    // Pull the window.initData JSON island out of the rendered page. It is emitted as
    // `window.initData=<json>;window.initUi=…` with `<` neutralized to `<` (ScriptSafe) — undo that
    // so the extracted text is parseable JSON.
    private static string ExtractInitData(string html)
    {
        const string start = "window.initData=";
        const string end = ";window.initUi=";
        var i = html.IndexOf(start, StringComparison.Ordinal) + start.Length;
        var j = html.IndexOf(end, i, StringComparison.Ordinal);
        return html.Substring(i, j - i).Replace("\\u003c", "<");
    }
}
