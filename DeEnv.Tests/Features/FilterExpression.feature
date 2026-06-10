Feature: Filter expression — first language slice (Milestone 6)
  A transient filter expression narrows a set view in the browser.
  Scalar-only expressions (fields present in data-member) are evaluated
  client-side with no WebSocket round-trip. Expressions referencing
  cross-object fields (e.g. assignee.name) are routed to the server
  via the filterSet WebSocket op. Filter state is page-only — cleared on reload.

  The instance used here:
    Db   { tasks: set<Task> }
    Task { title: text, done: bool, priority: int, assignee: Person }
    Person { name: text }

  Background:
    Given a filter-task instance

  # ── scalar field filters (client-side) ─────────────────────────────────────

  @milestone-6 @single-user
  Scenario: Bool equality — done == false shows only incomplete tasks
    Given a task "Write code" with done false and priority 1
    And a task "Deploy" with done true and priority 2
    When I navigate to the tasks set
    And I type the filter "done == false"
    Then only the row "Write code" is visible

  @milestone-6 @single-user
  Scenario: Text equality — title == 'Write code' shows only matching task
    Given a task "Write code" with done false and priority 1
    And a task "Deploy" with done true and priority 2
    When I navigate to the tasks set
    And I type the filter "title == 'Write code'"
    Then only the row "Write code" is visible

  @milestone-6 @single-user
  Scenario: Int comparison — priority > 1 shows only high-priority tasks
    Given a task "Write code" with done false and priority 1
    And a task "Deploy" with done true and priority 2
    When I navigate to the tasks set
    And I type the filter "priority > 1"
    Then only the row "Deploy" is visible

  @milestone-6 @single-user
  Scenario: Logical AND — done == false && priority == 1 combines conditions
    Given a task "Write code" with done false and priority 1
    And a task "Deploy" with done true and priority 2
    And a task "Review" with done false and priority 2
    When I navigate to the tasks set
    And I type the filter "done == false && priority == 1"
    Then only the row "Write code" is visible

  @milestone-6 @single-user
  Scenario: Logical OR — done == true || priority == 1
    Given a task "Write code" with done false and priority 1
    And a task "Deploy" with done true and priority 2
    And a task "Review" with done false and priority 2
    When I navigate to the tasks set
    And I type the filter "done == true || priority == 1"
    Then 2 rows are visible

  @milestone-6 @single-user
  Scenario: NOT — !done shows only incomplete tasks
    Given a task "Write code" with done false and priority 1
    And a task "Deploy" with done true and priority 2
    When I navigate to the tasks set
    And I type the filter "!done"
    Then only the row "Write code" is visible

  # ── reference field filter (server-side) ────────────────────────────────────

  @milestone-6 @single-user
  Scenario: Nested field — assignee.name == 'Ada' routes to server and filters by reference
    Given a person "Ada"
    And a person "Grace"
    And a task "Write code" assigned to "Ada" with done false and priority 1
    And a task "Deploy" assigned to "Grace" with done true and priority 2
    And a task "Review" with done false and priority 1
    When I navigate to the tasks set
    And I type the filter "assignee.name == 'Ada'"
    Then only the row "Write code" is visible

  # ── invalid expressions → error state ──────────────────────────────────────

  @milestone-6 @single-user
  Scenario: Non-boolean result (bare text field) shows error state and leaves all rows visible
    Given a task "Write code" with done false and priority 1
    And a task "Deploy" with done true and priority 2
    When I navigate to the tasks set
    And I type the filter "title"
    Then the filter input shows an error
    And 2 rows are visible

  @milestone-6 @single-user
  Scenario: Non-existing field shows error state and leaves all rows visible
    Given a task "Write code" with done false and priority 1
    And a task "Deploy" with done true and priority 2
    When I navigate to the tasks set
    And I type the filter "foo"
    Then the filter input shows an error
    And 2 rows are visible

  # ── empty filter restores all members ───────────────────────────────────────

  @milestone-6 @single-user
  Scenario: Empty filter restores all members
    Given a task "Write code" with done false and priority 1
    And a task "Deploy" with done true and priority 2
    When I navigate to the tasks set
    And I type the filter "done == false"
    And I clear the filter
    Then 2 rows are visible
