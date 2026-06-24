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
