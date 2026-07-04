@milestone-auth
Feature: The access floor (read enforcement by principal)
  Access is a deny-by-default ruleset over the object model, enforced at the kernel
  floor below Code: a rule's condition is a Code expression the existing interpreter
  evaluates over { currentUser, object }. Objects a reader is not allowed to read
  never enter the db graph that ships to the client. With no rules the app is dormant
  (allow-all, today's behavior).

  Background:
    Given an app whose Db holds a set of "Milestone" and a "User" with a "role" enum (Admin, Member)
    And the access rule "Milestone read where currentUser.role == \"Admin\""
    And a seeded admin user and a seeded member user
    And one seeded "Milestone" titled "Gate #3"

  Scenario: An admin principal can read the ruled type
    Given the current user is the admin
    When the page state is rendered for "/"
    Then the shipped data includes a "Milestone" titled "Gate #3"

  Scenario: A non-admin principal is denied the ruled type
    Given the current user is the member
    When the page state is rendered for "/"
    Then the shipped data includes no "Milestone"

  Scenario: An anonymous principal is denied (the condition fails closed, never errors)
    Given there is no current user
    When the page state is rendered for "/"
    Then the render does not error
    And the shipped data includes no "Milestone"

  Scenario: With no access rules the app is dormant and everything loads
    Given the app has no access rules
    And the current user is the member
    When the page state is rendered for "/"
    Then the shipped data includes a "Milestone" titled "Gate #3"

  # ── write enforcement (the mutation seam) ──────────────────────────────────
  # The same ruleset gates mutations. With a verb rule active, a create/edit/delete is
  # accepted only when a matching-verb rule's condition holds for the principal + target;
  # otherwise it is rejected (the client's existing rollback restores state) and the store
  # is left UNCHANGED. Driven at the WsHandler level (the mutation floor is server-side).

  Scenario: An admin may edit a ruled object and the change persists
    Given the access rule "Milestone edit where currentUser.role == \"Admin\""
    And the current user is the admin
    When the admin edits the "Milestone" titled "Gate #3" to set "title" to "Gate #3 - done"
    Then the mutation is accepted
    And the stored "Milestone" 2 has "title" equal to "Gate #3 - done"

  Scenario: A non-admin's edit is rejected and the store is unchanged
    Given the access rule "Milestone edit where currentUser.role == \"Admin\""
    And the current user is the member
    When the member edits the "Milestone" titled "Gate #3" to set "title" to "Hacked"
    Then the mutation is rejected
    And the stored "Milestone" 2 has "title" equal to "Gate #3"

  Scenario: An admin may create a ruled object and it is added
    Given the access rule "Milestone create where currentUser.role == \"Admin\""
    And the current user is the admin
    When the current user adds a "Milestone" titled "Gate #4" to the milestones set
    Then the mutation is accepted
    And the milestones set contains a "Milestone" titled "Gate #4"

  Scenario: A non-admin's create is rejected and nothing is added
    Given the access rule "Milestone create where currentUser.role == \"Admin\""
    And the current user is the member
    When the current user adds a "Milestone" titled "Sneaky" to the milestones set
    Then the mutation is rejected
    And the milestones set contains no "Milestone" titled "Sneaky"

  Scenario: An admin may delete a ruled object and it is removed
    Given the access rule "Milestone delete where currentUser.role == \"Admin\""
    And the current user is the admin
    When the current user removes "Milestone" 2 from the milestones set
    Then the mutation is accepted
    And the milestones set contains no "Milestone" 2

  Scenario: A non-admin's delete is rejected and the object remains
    Given the access rule "Milestone delete where currentUser.role == \"Admin\""
    And the current user is the member
    When the current user removes "Milestone" 2 from the milestones set
    Then the mutation is rejected
    And the milestones set still contains "Milestone" 2

  # ── locked (M13 sugar for `create edit delete where false`) ──────────────────
  # `locked` is a spelling upgrade over the write-denial idiom the designer already uses to make
  # Commit/Branch immutable (`create edit delete where false`) — same floor decision, same
  # AccessFloor, no new mechanism. Unlike the admin-gated rules above, `locked` has NO condition to
  # satisfy at all — even the admin (who passes every OTHER rule in this file) is denied every
  # write, because the rule's condition is unconditionally false, not role-conditioned. Reads are
  # UNTOUCHED (no `read` rule is installed, so Milestone stays unruled-for-read ⇒ open), matching
  # the task's "write-locked, not secret" requirement — the history SetTable stays visible. Uses a
  # dedicated step (not "the access rule") because `locked` must be the subject's ONLY rule —
  # replacing, not joining, the Background's `Milestone read where currentUser.role == "Admin"`.
  @milestone-13
  Scenario: A locked type rejects every client write while staying readable
    Given the only access rule for Milestone is "locked"
    And the current user is the admin
    When the page state is rendered for "/"
    Then the shipped data includes a "Milestone" titled "Gate #3"
    When the current user adds a "Milestone" titled "Sneaky" to the milestones set
    Then the mutation is rejected
    And the milestones set contains no "Milestone" titled "Sneaky"
    When the admin edits the "Milestone" titled "Gate #3" to set "title" to "Hacked"
    Then the mutation is rejected
    And the stored "Milestone" 2 has "title" equal to "Gate #3"
    When the current user removes "Milestone" 2 from the milestones set
    Then the mutation is rejected
    And the milestones set still contains "Milestone" 2

  # ── password login (the session→principal bind over the WS) ─────────────────
  # Login is a STATE bound over the EXISTING WS connection (no reserved route): a `login`
  # action looks up the User by name, verifies the plaintext against the stored PBKDF2 hash,
  # and on success SETS the session's principal. The session starts ANONYMOUS; the bind is
  # driven THROUGH the action (not by setting the principal directly) and asserted by reading
  # the session's PrincipalUserId back. Wrong password and unknown user are the SAME negative
  # result (no user-enumeration) and are NOT errors. Tested by sending the action directly —
  # the floor was proven this way before login existed; no login-as-state UI this slice.

  Scenario: A correct password binds the session to the admin principal
    Given the admin user has the password "hunter2"
    And an anonymous WS session
    When the session logs in as "Ada" with password "hunter2"
    Then the login succeeds as the admin
    And the session principal is the admin

  Scenario: A wrong password leaves the session anonymous
    Given the admin user has the password "hunter2"
    And an anonymous WS session
    When the session logs in as "Ada" with password "wrong"
    Then the login fails
    And the session principal is anonymous
    And the password verifier ran once

  Scenario: An unknown user leaves the session anonymous (same reply as a wrong password)
    Given the admin user has the password "hunter2"
    And an anonymous WS session
    When the session logs in as "Nobody" with password "hunter2"
    Then the login fails
    And the session principal is anonymous
    And the password verifier ran once

  Scenario: After logging in, a render shows the bound admin can read the ruled type
    Given the admin user has the password "hunter2"
    And an anonymous WS session
    When the session logs in as "Ada" with password "hunter2"
    And the page is rendered for "/" as the logged-in session
    Then the shipped data includes a "Milestone" titled "Gate #3"

  Scenario: Logging out clears the session to anonymous
    Given the admin user has the password "hunter2"
    And an anonymous WS session
    And the session logs in as "Ada" with password "hunter2"
    When the session logs out
    Then the logout succeeds
    And the session principal is anonymous

  Scenario: Login persists across a fresh page load
    Given the access-fixture app is served with the admin password "hunter2"
    And an anonymous visitor opens "/"
    When the visitor logs in through the form as "Ada" with password "hunter2"
    Then "Gate #3" eventually appears
    When the visitor opens a fresh page at "/"
    Then "Gate #3" eventually appears

  Scenario: Logout clears the persisted login
    Given the access-fixture app is served with the admin password "hunter2"
    And an anonymous visitor opens "/"
    When the visitor logs in through the form as "Ada" with password "hunter2"
    Then "Gate #3" eventually appears
    When the visitor logs out through the user menu
    And the visitor opens a fresh page at "/"
    Then the login form is shown and "Gate #3" is not

  # ── privacy (now testable with a real, login-bound principal) ────────────────
  # The two assertions the floor slice deferred because they needed a real principal:
  # (a) passwordHash NEVER enters the shipped graph — RULE-INDEPENDENT (asserted under the
  #     ruled fixture AND the dormant no-rules fixture); (b) the currentUser scope ships
  #     fields-less — the principal's `role`, read by the access condition, never leaks.

  Scenario: The password hash never enters the shipped document (ruled app)
    Given the admin user has the password "hunter2"
    And the current user is the admin
    When the page state is rendered for "/"
    Then the rendered document does not contain the admin's password hash
    And the rendered document does not contain the "pbkdf2" marker

  Scenario: The password hash never enters the shipped document (dormant app, no rules)
    Given the app has no access rules
    And the admin user has the password "hunter2"
    And the current user is the admin
    When the page state is rendered for "/"
    Then the rendered document does not contain the admin's password hash
    And the rendered document does not contain the "pbkdf2" marker

  # The READ chokepoint on the user's OWN object page — where the `password`-typed field IS displayed (its
  # masked <input> reads sys.field(obj,"password"), unlike /milestones which never touches it). The field is
  # ACCESSED, so it ships — but BLANK ("") never the stored hash. Proves the masked control binds to "" and the
  # secret stays server-side even on the page that renders the field.
  Scenario: A user's password field ships blank on its own object page (never the hash)
    Given the member user has the password "original"
    And the current user is the admin
    When the page state is rendered for "/users/4"
    Then the user 4's password field ships blank in the graph

  # The SetTable default columns EXCLUDE the password field — the users list shows no "Password" column of
  # blank cells (a secret is not a scannable list value; it belongs on the member page). This is the
  # SetTable companion of the Scalars/labelProp exclusion the descriptor already applies.
  Scenario: The users list shows no password column
    Given the member user has the password "original"
    And the current user is the admin
    When the page state is rendered for "/users"
    Then the rendered users list has no password column

  # Rendered at the milestone member page (/milestones/2) — which has NO users table — so the only way
  # the principal's role "Admin" could appear is via the currentUser scope. The access condition DOES
  # read currentUser.role (to admit the milestone), but over a throwaway context, so it never ships.
  Scenario: The currentUser scope ships fields-less (the principal's role is not exposed)
    Given the admin user has the password "hunter2"
    And the current user is the admin
    When the page state is rendered for "/milestones/2"
    Then the shipped data includes a "Milestone" titled "Gate #3"
    And the rendered document does not expose the current user's role "Admin"

  # ── set-password-as-a-field (the M-auth `password` type) ─────────────────────
  # Setting a User's password is now just an EDIT of its `password`-typed field — an objectPropChange
  # gated by the SAME write floor (an ordinary `User edit where currentUser.role == "Admin"` rule decides
  # who may do it; no bespoke setPassword op). The WS layer PBKDF2-hashes the plaintext before the store
  # (the write chokepoint); the stored hash is blanked to "" on the way out (the read chokepoint), so it is
  # write-only from the client's view. An admin's edit is accepted; a member's is the `{ error }` reply
  # (the floor throw → client rollback), leaving the original hash intact.

  Scenario: An admin sets a member's password and the member can then log in with it
    Given the User access rule "User edit where currentUser.role == \"Admin\""
    And the current user is the admin
    When the admin sets user 4's password to "freshpass"
    Then the setPassword succeeds
    When the member logs in with password "freshpass"
    Then the login succeeds as the member

  Scenario: A non-admin's set-password edit is rejected and the original password still works
    Given the User access rule "User edit where currentUser.role == \"Admin\""
    And the member user has the password "original"
    And the current user is the member
    When the member sets user 4's password to "hacked"
    Then the setPassword is rejected
    And the member can log in with password "original"
    And the member cannot log in with password "hacked"

  # ── floor-hardening (three review-found bypasses) ───────────────────────────
  # The read floor gated the db GRAPH but not the extent LISTING; the write floor gated the
  # identity-addressed edits but not the path `write` onto a set member's scalar field; a
  # throwing condition crashed the render. Each scenario below FAILS on the unhardened floor
  # (the denied row leaks / the ungated write persists / the render errors) and PASSES after.

  # Fix 1 — sys.extent must obey the READ floor. A custom render (or the generic ref picker)
  # lists a type's rows via sys.extent(...), which bypassed CanRead. The denied member must see
  # NO candidate of the ruled type; the admin sees them.
  Scenario: An admin sees the ruled type's rows in an extent listing
    Given an app that lists the Milestone extent via sys.extent in a custom render
    And the current user is the admin
    When the page state is rendered for "/"
    Then the extent listing includes a row titled "Gate #3"

  Scenario: A non-admin sees none of the ruled type's rows in an extent listing
    Given an app that lists the Milestone extent via sys.extent in a custom render
    And the current user is the member
    When the page state is rendered for "/"
    Then the extent listing includes no row titled "Gate #3"

  Scenario: An anonymous reader sees none of the ruled type's rows in an extent listing
    Given an app that lists the Milestone extent via sys.extent in a custom render
    And there is no current user
    When the page state is rendered for "/"
    Then the render does not error
    And the extent listing includes no row titled "Gate #3"

  # Fix 2 — the path `write` op onto a set member's scalar field must obey the WRITE floor. It
  # is the SAME mutation objectPropChange performs (and gates), but routed through the leaf-path
  # seam, which was ungated. A non-admin's write is rejected and the store is byte-unchanged; an
  # admin's write persists.
  Scenario: A non-admin's path write to a ruled object's field is rejected and the store is unchanged
    Given the access rule "Milestone edit where currentUser.role == \"Admin\""
    And the current user is the member
    When the member writes "title" of "Milestone" 2 to "Hacked" via the path write op
    Then the mutation is rejected
    And the stored "Milestone" 2 has "title" equal to "Gate #3"

  Scenario: An admin's path write to a ruled object's field is accepted and persists
    Given the access rule "Milestone edit where currentUser.role == \"Admin\""
    And the current user is the admin
    When the admin writes "title" of "Milestone" 2 to "Gate #3 - done" via the path write op
    Then the mutation is accepted
    And the stored "Milestone" 2 has "title" equal to "Gate #3 - done"

  # Fix 3 — a throwing condition must DENY (fail closed), not crash the render. A condition that
  # divides by zero throws DivideByZeroException (not a CodeRuntimeException), which escaped the
  # floor's narrow catch and crashed the SSR render. It must now deny and the render must succeed.
  Scenario: A condition that divides by zero denies access without crashing the render
    Given the only access rule's condition divides by zero
    And the current user is the admin
    When the page state is rendered for "/"
    Then the render does not error
    And the shipped data includes no "Milestone"

  # ── login-as-state from the UI (the client half, sub-slice 1e-1) ─────────────
  # An auto-mode app where anonymous can read NOTHING (every read rule needs a non-null
  # currentUser) is `anonymousLockedOut`: the synthesized generic render gates an anonymous
  # request to a <LoginForm> instead of an empty page. Logging in through that form binds the
  # session's principal over the WS and refetches, so the same URL re-renders as the bound user
  # and the ruled data appears — login is a STATE, not a route (the URL never changes). End-to-end
  # in a real browser: the failing-before proof is that without the gate + form an anonymous user
  # has no way to log in (the page is empty / the data is denied and stays denied).
  Scenario: An anonymous visitor logs in through the auto-mode gate and the ruled data appears
    Given the access-fixture app is served with the admin password "hunter2"
    And an anonymous visitor opens "/"
    Then the login form is shown and "Gate #3" is not
    When the visitor logs in through the form as "Ada" with password "hunter2"
    Then "Gate #3" eventually appears
    And the URL is still "/"

  # ── logout from the UI (sub-slice 1e-2) ──────────────────────────────────────
  # The mirror of login. Once logged in, the synthesized generic render also shows a <UserMenu>
  # (the user's name + a Log out button). Clicking Log out fires sys.logout over the WS; the reply
  # refetches as anonymous, so the same URL re-renders with the ruled data DENIED again and the
  # login gate back — logout is a STATE, not a route (the URL never changes).
  Scenario: A logged-in visitor logs out through the user menu and returns to the gate
    Given the access-fixture app is served with the admin password "hunter2"
    And an anonymous visitor opens "/"
    When the visitor logs in through the form as "Ada" with password "hunter2"
    Then "Gate #3" eventually appears
    When the visitor logs out through the user menu
    Then the login form is shown and "Gate #3" is not
    And the URL is still "/"

  # ── public-roadmap policy + the sign-in affordance (the devlog dogfood) ───────
  # A PUBLIC app grants anonymous reads with a bare `read` rule while gating writes to an admin — a public
  # roadmap anyone can read and only the operator can edit. Because a public app is NOT anonymousLockedOut,
  # the auto login-gate never fires, so the synthesized render instead offers an always-present sign-in
  # control (a `<SignInBar>` — login-as-state, no reserved URL). It is gated on `accessActive` (the app has
  # rules), so a DORMANT no-auth app shows no stray sign-in button. (Deterministic at the SsrRenderer/WsHandler
  # level; the sign-in click→login flow reuses the proven LoginForm path.)

  Scenario: A bare read rule makes the data public to an anonymous visitor
    Given the access rule "Milestone read"
    And there is no current user
    When the page state is rendered for "/"
    Then the shipped data includes a "Milestone" titled "Gate #3"

  Scenario: A public-read app still denies an anonymous write
    Given the access rule "Milestone read"
    And the access rule "Milestone edit where currentUser.role == \"Admin\""
    And there is no current user
    When the anonymous edits the "Milestone" titled "Gate #3" to set "title" to "Hacked"
    Then the mutation is rejected
    And the stored "Milestone" 2 has "title" equal to "Gate #3"

  Scenario: A public auth app offers an anonymous visitor a sign-in control
    Given the access rule "Milestone read"
    And there is no current user
    When the page state is rendered for "/"
    Then the rendered document includes a sign-in control

  Scenario: A dormant no-auth app shows no sign-in control
    Given the app has no access rules
    And there is no current user
    When the page state is rendered for "/"
    Then the rendered document includes no sign-in control

  # End-to-end in a real browser: the full sign-in/sign-out loop of a PUBLIC generic app. The data is
  # public, so the visitor sees it AND a sign-in control (no auto-gate); opening that control reveals the
  # login form in place (login-as-state, no navigation); logging in swaps to the user menu; logging out
  # returns to the sign-in control with the data still readable — all at the same URL.
  Scenario: A visitor signs in and out of a public generic app
    Given the access-fixture app is served as a public roadmap with admin password "hunter2"
    And an anonymous visitor opens "/"
    Then "Gate #3" eventually appears
    And a sign-in control is shown
    And no create control is shown
    When the visitor opens the sign-in form
    And the visitor logs in through the form as "Ada" with password "hunter2"
    Then the user menu is shown
    And a create control is shown
    And "Gate #3" eventually appears
    When the visitor logs out through the user menu
    Then a sign-in control is shown
    And no create control is shown
    And "Gate #3" eventually appears
    And the URL is still "/"

  # ── user management: the "Users" link in <UserMenu> ──────────────────────────
  # Multi-user management is reached from the user menu's "Users" link, which navigates to the generic
  # User list (`/users`); set-password lives on each User's own object page (`/users/<id>`) — no popup,
  # no reserved URL. The link's visibility is gated on a derived `canManageUsers` capability (the floor's
  # User `edit`), NOT on the principal's role — so the role stays private while the admin-only control
  # still gates correctly.

  Scenario: An admin sees the user-management control
    Given the access rule "Milestone read where currentUser.role == \"Admin\""
    And the User access rule "User edit where currentUser.role == \"Admin\""
    And the current user is the admin
    When the page state is rendered for "/"
    Then the rendered document includes a user-management control

  Scenario: A non-admin sees no user-management control
    Given the access rule "Milestone read where currentUser.role == \"Admin\""
    And the User access rule "User edit where currentUser.role == \"Admin\""
    And the current user is the member
    When the page state is rendered for "/"
    Then the rendered document includes no user-management control

  # The "Users" link resolves the User set BY TYPE, not by the literal name "users": an app whose root names
  # its `set of User` prop `members` (not `users`) must have the menu link point at the RESOLVED `/members`,
  # not a dead `/users`. The link is computed in Code from the schema descriptor (the root's set-of-principal
  # prop via the `isPrincipal` flag), mirroring how ObjectForm builds a set's list-title link.
  Scenario: The user-management link resolves the User set by type, not the name "users"
    Given an app whose root User set is named "members"
    And the current user is the admin
    When the page state is rendered for "/"
    Then the user-management link points at "/members"

  # ── component-state seed (client data layer, slice 1a) ───────────────────────
  # The server can reproduce the CLIENT's exact component view-state. The fixture's whole UI is one
  # stateful root component `<panel>` whose view reveals the (admin-ruled) milestone rows only when its
  # `state.open` is true — the `<UserAdmin>`-behind-`if state.managing` footgun in miniature. Rendering
  # as the admin with the panel's slot UNSEEDED leaves open:false (the setup default), so nothing reads
  # `db.milestones` and "Gate #3" is never harvested — it is absent from the shipped document (the exact
  # empty-popup bug). SEEDING the panel's slot `state = { open: true }` makes the server render the SAME
  # tree the client has: the rows render, `db.milestones` is read, and structural privacy harvests
  # "Gate #3" into the shipped document. The harvest stays floor-gated (the Milestone read rule). This is
  # the seed-CONSUMPTION proof; the client SHIP of state + the refetch threading are later slices, so the
  # seed is injected directly here.
  Scenario: A seeded component reproduces the client's view-state and ships its demanded data
    Given the access-seed app whose panel reveals the milestones only when its slot is open
    And the current user is the admin
    When the page state is rendered for "/"
    Then the shipped data includes no "Milestone"
    When the "panel" slot is seeded "open" = true
    And the page state is rendered for "/"
    Then the shipped data includes a "Milestone" titled "Gate #3"

  # ── client data layer, slice 1b — the CLIENT SHIP + server reconstruct round-trip (real browser) ──
  # 1a proved the server CONSUMING a seed (injected directly). 1b proves the full loop: the CLIENT ships its
  # live component view-state, the server reproduces the exact render and ships the demanded data, the client
  # merges it. The fixture's whole UI is one stateful root <panel> with a SCALAR `var open = false` and a
  # "Show" button; the milestone rows read `db.milestones` ONLY when open, and NOTHING else server-side reads
  # it — so the rows can populate ONLY via the ship→seed round-trip (the empty-popup footgun, controlled). The
  # data is absent while the panel is closed (structural privacy ships only what the closed render touched);
  # a click flips `open` client-side → the re-render's swallowed VNA fires a refetch carrying the new
  # slotState → the server seeds open:true, reproduces the open panel, harvests "Gate #3", ships it → the rows
  # appear. Reverting either the ws.ts ship or the HandleRefetch reconstruct leaves the panel empty forever.
  @milestone-client-data
  Scenario: A client-toggled component's demanded data is shipped via the state round-trip
    Given the access-toggle app is served
    And a visitor opens the toggle app at "/"
    Then no gated row is shown
    When the visitor clicks the panel's Show control
    Then a gated row titled "Gate #3" eventually appears

  # ── client data layer, slice 4 — the ACTION-MISS round-trip (real browser) ──────────────────
  # 1b shipped the data a RENDER demands; slice 4 does the same for the data a CLICK HANDLER demands. The
  # fixture SHOWS the counter's value `a` (= 0, shipped) but NEVER reads its self-`link` reference on first
  # paint, so `c.link` is un-shipped. The Bump handler increments THROUGH that link: `c.link.a = c.link.a +
  # 1`. On click the handler reads `c.link` -> "Value not available"; TODAY (slice 3) that VNA flushes the
  # pre-throw sends and re-throws -- the action SILENTLY DIES, the value stays 0. After slice 4 the handler
  # transaction CATCHES the VNA, ABORTS atomically (zero partial writes -- slice 3's rollback), RECORDS the
  # pending action (its handler fn-id + render-slot + view-state), and FETCHES: the server reproduces the
  # exact render, locates that handler closure by (slot, fn-id), INVOKES it READ-ONLY (its writes stage into
  # the throwaway in-memory graph that is discarded), and HARVESTS its reads (`c.link`) through the same
  # structural-privacy + access floor. The client merges the now-present data and RE-INVOKES the handler
  # over it -- atomically, in a fresh transaction -- so the increment lands on the visible `a` (0 -> 1) and
  # persists. Reverting the slice-4 wiring leaves the action dead (the counter never increments).
  @milestone-client-data
  Scenario: A handler that reads un-shipped data completes its action via the action-miss round-trip
    Given the action-miss app is served
    And a visitor opens the action-miss app at "/"
    Then the counter reads 0
    When the visitor clicks the counter's Bump control
    Then the counter eventually reads 1
    And the stored counter value is 1

  # ── REGRESSION (component slot-keying — Surface 2, the KebabMenu(body) render-prop in a foreach) ──
  # Each row of `db.rows` renders a `<menu>` (the KebabMenu shape) and passes a FRESH per-row render-prop
  # `rowBody(r)` (a `fn inner(close)` closing over the row, returning `<div class={"row-action " + r.name}>`).
  # Opening a row's menu invokes `body(close)` to fill the popup; the popup must show that ROW's name as a
  # class (`.row-action.Alpha` / `.row-action.Beta`). On main it does NOT: every row's `inner` is a separate
  # closure but shares one fn AST id, and `menu` invokes it through the function-call memo (key = fn-id +
  # args; the `close` arg is a function → argKey "?"), with NO per-row/slot segment — so every row's
  # `body(close)` collides on ONE key. The FIRST menu opened caches its body under that key; opening a LATER
  # row's menu cache-hits the first and renders the FIRST row's content. (Both menus start CLOSED, so
  # `body(close)` runs only on a click — the collision needs two opens to race; opening Alpha then Beta is
  # the minimal trigger.) The symptom is CLIENT-only (the C# twin's memo is write-only, so it re-runs each
  # call and SSR renders both rows correctly); the fix folds the invocation slot/row into the render-prop's
  # identity so each row's body renders independently.
  @milestone-client-data
  Scenario: Each foreach row's menu shows its own render-prop content, not the first row's
    Given the menu-keying app is served
    And a visitor opens the menu-keying app at "/"
    When the visitor opens the menu for row "Alpha"
    And the visitor opens the menu for row "Beta"
    Then the open menu for row "Alpha" shows its own action ".row-action.Alpha"
    And the open menu for row "Beta" shows its own action ".row-action.Beta"

  # ── read-only affordances: write controls hidden when the principal cannot write ──
  # The generic UI gates Save (form-actions), New (new-btn), and Remove (set-remove) on sys.canWrite(type,
  # verb) — server-resolved from the floor, shipped like sys.extent. So a read-only principal (e.g. an
  # anonymous visitor of a public-read app) sees the data but NOT controls the floor would reject; an admin
  # sees them. The floor still RE-decides every real write — this governs only what the UI OFFERS.

  Scenario: A read-only visitor sees no edit control on a ruled object
    Given the access rule "Milestone read"
    And the access rule "Milestone edit where currentUser.role == \"Admin\""
    And there is no current user
    When the page state is rendered for "/milestones/2"
    Then the rendered body shows no "form-actions" marker

  Scenario: An admin sees the edit control on a ruled object
    Given the access rule "Milestone read"
    And the access rule "Milestone edit where currentUser.role == \"Admin\""
    And the current user is the admin
    When the page state is rendered for "/milestones/2"
    Then the rendered body shows a "form-actions" marker

  # Both collections on the Db root (milestones + the baked-in users set) are create-ruled, so a read-only
  # visitor sees NO "New …" anywhere. (An unruled type's create stays allowed — the floor only restricts
  # what is ruled — so a partial ruleset would correctly still offer New for the unruled type.)
  Scenario: A read-only visitor sees no create control on a ruled collection
    Given the access rule "Milestone read"
    And the access rule "Milestone create where currentUser.role == \"Admin\""
    And the User access rule "User create where currentUser.role == \"Admin\""
    And there is no current user
    When the page state is rendered for "/"
    Then the rendered body shows no "new-btn" marker

  Scenario: An admin sees the create control on a ruled collection
    Given the access rule "Milestone read"
    And the access rule "Milestone create where currentUser.role == \"Admin\""
    And the User access rule "User create where currentUser.role == \"Admin\""
    And the current user is the admin
    When the page state is rendered for "/"
    Then the rendered body shows a "new-btn" marker

  # ── unreadable collections hidden (sys.canRead): an admin-only set is hidden from / and its route 404s ──
  # A collection whose element type the principal cannot read at all is hidden — the ObjectForm omits the
  # field and the route 404s (hide-existence). The `users` set is admin-only, so an anonymous visitor of a
  # public app sees no users table on the root and cannot reach /users; an admin sees both. canRead errs
  # toward READABLE (a public or partially-readable collection is never hidden).

  Scenario: A read-only visitor does not see an admin-only collection on the root
    Given the access rule "Milestone read"
    And the User access rule "User read where currentUser.role == \"Admin\""
    And there is no current user
    When the page state is rendered for "/"
    Then the rendered body shows no "/users" marker
    And the shipped data includes a "Milestone" titled "Gate #3"

  Scenario: An admin sees the admin-only collection on the root
    Given the access rule "Milestone read"
    And the User access rule "User read where currentUser.role == \"Admin\""
    And the current user is the admin
    When the page state is rendered for "/"
    Then the rendered body shows a "/users" marker

  Scenario: A read-only visitor cannot reach the admin-only collection's route
    Given the access rule "Milestone read"
    And the User access rule "User read where currentUser.role == \"Admin\""
    And there is no current user
    When the page state is rendered for "/users"
    Then the rendered body shows a "not-found" marker

  Scenario: An admin can reach the admin-only collection's route
    Given the access rule "Milestone read"
    And the User access rule "User read where currentUser.role == \"Admin\""
    And the current user is the admin
    When the page state is rendered for "/users"
    Then the rendered body shows no "not-found" marker

  # ── read-only fields: a principal who cannot edit gets read-only inputs (not just a hidden Save) ──
  Scenario: A read-only visitor's fields are read-only inputs
    Given the access rule "Milestone read"
    And the access rule "Milestone edit where currentUser.role == \"Admin\""
    And there is no current user
    When the page state is rendered for "/milestones/2"
    Then the rendered body shows a "readonly" marker

  Scenario: An admin's fields are editable inputs
    Given the access rule "Milestone read"
    And the access rule "Milestone edit where currentUser.role == \"Admin\""
    And the current user is the admin
    When the page state is rendered for "/milestones/2"
    Then the rendered body shows no "readonly" marker

  # The root object (Db) holds only collections — no scalar fields to stage — so its form shows no Save,
  # even for an admin who could edit it (there is simply nothing to save).
  Scenario: A form for an object with only collections shows no Save
    Given the access rule "Milestone read"
    And the current user is the admin
    When the page state is rendered for "/"
    Then the rendered body shows no "form-actions" marker

  # End-to-end (the M-auth `password` type, EDIT path): an admin creates a user on the generic User list
  # (reached via the menu's "Users" link) and sets that user's password on its own object page by EDITING
  # the masked password FIELD and clicking Save (no bespoke control — set-password is a field edit). The new
  # user can then log in — the full multi-user thread (create on /users → edit-password on /users/<id> →
  # re-login) in a real browser. The password field's value is BLANK on the wire throughout (the read
  # chokepoint), so the admin types into an empty masked input and the hash is computed server-side.
  Scenario: An admin creates a user and sets a password, and the new user can log in
    Given the access-fixture app is served as a public roadmap with admin password "hunter2"
    And an anonymous visitor opens "/"
    When the visitor opens the sign-in form
    And the visitor logs in through the form as "Ada" with password "hunter2"
    Then the user menu is shown
    When the admin opens user management
    And the admin creates a user "Cleo" with role "Member"
    And the admin sets "Cleo"'s password to "cleopw"
    And the visitor logs out through the user menu
    And the visitor opens the sign-in form
    And the visitor logs in through the form as "Cleo" with password "cleopw"
    Then the user menu is shown

  # End-to-end (the M-auth `password` type, CREATE path): the password is a FIELD, so it can be set at
  # CREATION — the admin fills name + role + password in ONE create-form submit, and the new user can log in.
  # The WS layer hashes the password from the create payload (the write chokepoint); no separate set step.
  Scenario: An admin creates a user with a password in one form, and that user can log in
    Given the access-fixture app is served as a public roadmap with admin password "hunter2"
    And an anonymous visitor opens "/"
    When the visitor opens the sign-in form
    And the visitor logs in through the form as "Ada" with password "hunter2"
    Then the user menu is shown
    When the admin opens user management
    And the admin creates a user "Dani" with role "Member" and password "danipw"
    And the visitor logs out through the user menu
    And the visitor opens the sign-in form
    And the visitor logs in through the form as "Dani" with password "danipw"
    Then the user menu is shown
