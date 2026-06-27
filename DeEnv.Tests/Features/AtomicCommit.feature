@milestone-atomic-commit
Feature: Atomic ctx.commit (edits)
  ctx.commit() sends ONE `commit` message the server applies all-or-none. If any edit in the batch
  fails validation (unknown prop, access denial, bad enum), NO edit in the batch persists. Before
  this change, each field was sent as an independent `objectPropChange`; a partial batch left the
  successful edits persisted while the failed one was rejected — the commit was not the unit.

  Background:
    Given the two-field commit fixture app

  Scenario: A commit where all edits are valid persists every field
    When the commit sends title "Updated title" and count 7
    Then the commit is accepted
    And the stored "Item" 2 has "title" equal to "Updated title"
    And the stored "Item" 2 has "count" equal to 7

  Scenario: A commit where one edit names a non-existent field rolls back the whole batch
    When the commit sends title "Would persist" and an unknown field "noSuchProp"
    Then the commit is rejected
    And the stored "Item" 2 has "title" equal to "Seed title"

  Scenario: A commit denied by an access rule rolls back every edit atomically
    Given the Item access rule "Item edit where currentUser.role == \"Admin\""
    And the current user is the member
    When the member commits edits to both "title" and "count" of item 2
    Then the commit is rejected
    And the stored "Item" 2 has "title" equal to "Seed title"
    And the stored "Item" 2 has "count" equal to 0
