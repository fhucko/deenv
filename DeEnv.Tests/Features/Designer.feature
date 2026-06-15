Feature: The hand-rolled designer (custom UI)
  The designer is an operator-facing surface authored as an EXPLICIT custom `fn render()`
  over its own meta-schema (Db { types: set of MetaType }) — NOT the auto generic UI, and
  NOT a hidden callable designer. Its editor is hand-rolled Code, like the todo app: a
  type/prop editor over `db.types`, a list of the kernel's running instances
  (`sys.instances`), and a create-instance control wired to `sys.create`. Milestone 10.

  @milestone-10 @single-user
  Scenario: The hand-rolled designer edits the schema by hand
    Given the designer app is running
    When I add a type
    And I name the first type "Item"
    And I add a prop to the first type
    And I name the first prop "label"
    Then the designer shows a type named "Item" with a prop named "label"
    And the page shows a create-instance control

  @milestone-10 @single-user
  Scenario: Editing a port input keeps it an integer for sys.create
    Given the designer app is running
    When I set the app port to "007"
    Then the app port input shows "7"
