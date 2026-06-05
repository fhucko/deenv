Feature: Boolean persistence
  The single boolean is stored in a real ACID database (SQLite or Postgres).
  These scenarios spec the storage spine directly, below the UI.

  @milestone-1 @single-user @persistence
  Scenario: Writing the value stores it
    Given a single-boolean instance with value unchecked
    When the value is set to checked
    Then reading the stored value returns checked

  @milestone-1 @single-user @persistence
  Scenario: The value is durable across an instance restart
    Given a single-boolean instance with value checked
    When the instance is stopped and started again
    Then reading the stored value returns checked

  @milestone-future @durability
  Scenario: A write either fully happens or not at all
    Given a single-boolean instance with value unchecked
    When a write of checked is interrupted before it completes
    Then reading the stored value returns unchecked
