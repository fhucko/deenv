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

  @milestone-5 @single-user
  Scenario: The bridge projects a single reference to another type
    Given a designer instance
    And a designed type "Db" with base type "object"
    And a designed type "Person" with base type "object"
    And the type "Person" has a prop "name" of type "text"
    And the type "Db" has a prop "lead" of type "Person"
    When the design is exported
    Then the exported document loads successfully
    And the exported type "Db" has a single reference prop "lead" of type "Person"

  @milestone-5 @single-user
  Scenario: A designed schema with a reference runs as an instance
    Given a designer instance
    And a designed type "Db" with base type "object"
    And a designed type "Person" with base type "object"
    And the type "Person" has a prop "name" of type "text"
    And the type "Db" has a prop "lead" of type "Person"
    When the design is exported
    And an instance is started from the exported schema
    Then I see a form for "Db"
    And a reference link "lead" is present

  @milestone-4 @single-user
  Scenario: A designed schema runs as an instance after export
    Given a designer instance
    And a designed type "Db" with base type "object"
    And the type "Db" has a prop "notes" of type "text"
    When the design is exported
    And an instance is started from the exported schema
    Then I see a form for "Db"
    And the "notes" field is present

  # An enum type carries a comma-separated value list (the always-rendered designer field), which the
  # bridge projects into BaseType.Enum + the ordered, trimmed Values — so the designer can author an
  # enum's value list, not just a type name + baseType. The exported document declares the enum in the
  # canonical `Name enum` + indented-values form.
  @milestone-10 @single-user
  Scenario: The bridge projects an enum type's value list
    Given a designer instance
    And a designed type "Db" with base type "object"
    And the type "Db" has a prop "status" of type "Status"
    And a designed enum type "Status" with values "open, doing, done"
    When the design is exported
    Then the exported document loads successfully
    And the exported type "Status" is an enum with values "open, doing, done"
    And the exported document declares the enum "Status" with values "open, doing, done"

  # A single text prop's `multiline` presentation flag (the designer's checkbox, commit 678eb6d) flows
  # through projection onto the PropDefinition (Multiline = true) and prints back in the canonical
  # `name text multiline` form — so the designer authors a textarea-rendered prop end to end. A stale flag
  # carried on a NON-text prop (a prop retyped after it was toggled) is DROPPED by projection — the
  # designer never deploys an invalid design (the loader also rejects multiline off a text prop).
  @milestone-multiline @single-user
  Scenario: The bridge projects multiline onto a text prop and drops it from a non-text prop
    Given a designer instance
    And a designed type "Db" with base type "object"
    And the type "Db" has a prop "body" of type "text" marked multiline
    And the type "Db" has a prop "owner" of type "User" marked multiline
    And a designed type "User" with base type "object"
    And the type "User" has a prop "name" of type "text"
    When the design is exported
    Then the exported document loads successfully
    And the exported type "Db" has a multiline prop "body"
    And the exported type "Db" has a prop "owner" that is not multiline
    And the exported document declares the multiline prop "body" of type "text"
