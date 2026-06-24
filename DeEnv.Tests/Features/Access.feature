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

  Scenario: An unknown user leaves the session anonymous (same reply as a wrong password)
    Given the admin user has the password "hunter2"
    And an anonymous WS session
    When the session logs in as "Nobody" with password "hunter2"
    Then the login fails
    And the session principal is anonymous

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

  # Rendered at the milestone member page (/milestones/2) — which has NO users table — so the only way
  # the principal's role "Admin" could appear is via the currentUser scope. The access condition DOES
  # read currentUser.role (to admit the milestone), but over a throwaway context, so it never ships.
  Scenario: The currentUser scope ships fields-less (the principal's role is not exposed)
    Given the admin user has the password "hunter2"
    And the current user is the admin
    When the page state is rendered for "/milestones/2"
    Then the shipped data includes a "Milestone" titled "Gate #3"
    And the rendered document does not expose the current user's role "Admin"
