Feature: Schema bridge (self-hosted designer)
  The designer is the instance runtime running a hand-written meta-schema, so a
  schema is authored as ordinary data. The bridge projects that designer data
  into a canonical schema document — validated like any hand-written one — and
  the instance runs the result.

  @milestone-4 @single-user
  Scenario: The meta-schema document loads
    Given the meta-schema document
    When the meta-schema is loaded
    Then the meta-schema loads successfully
    And the meta-schema defines a type "MetaType"
    And the meta-schema defines a type "MetaProp"

  @milestone-4 @single-user
  Scenario: The bridge projects a design into a runnable document
    Given a designer instance
    And a designed type "Db" with base type "object"
    And the type "Db" has a prop "notes" of type "text"
    When the design is exported
    Then the exported document loads successfully
    And the exported type "Db" has a prop "notes"

  @milestone-4 @single-user
  Scenario: The bridge projects a set relationship to another type
    Given a designer instance
    And a designed type "Db" with base type "object"
    And a designed type "Item" with base type "object"
    And the type "Item" has a prop "label" of type "text"
    And the type "Db" has a set prop "items" of type "Item"
    When the design is exported
    Then the exported document loads successfully
    And the exported type "Db" has a set prop "items" of type "Item"

  @milestone-4 @single-user
  Scenario: Exporting an invalid design is rejected and writes nothing
    Given a designer instance
    And a designed type "Db" with base type "object"
    When the design is exported
    Then the export is rejected with an error mentioning "props"
    And the target schema file is unchanged

  @milestone-4 @single-user
  Scenario: The bridge emits props in their order, not creation order
    Given a designer instance
    And a designed type "Db" with base type "object"
    And the type "Db" has a prop "alpha" of type "text" with order 20
    And the type "Db" has a prop "beta" of type "text" with order 10
    When the design is exported
    Then the exported type "Db" lists prop "beta" before "alpha"

  @milestone-4 @single-user
  Scenario: A designed schema runs as an instance after export
    Given a designer instance
    And a designed type "Db" with base type "object"
    And the type "Db" has a prop "notes" of type "text"
    When the design is exported
    And an instance is started from the exported schema
    Then I see a form for "Db"
    And the "notes" field is present
