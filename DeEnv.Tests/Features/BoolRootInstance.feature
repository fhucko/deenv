Feature: Bool-root instance
  The simplest valid instance: an object Db with a single bool field. It renders
  as one checkbox in the self-hosted generic UI, which autosaves each edit (no
  Save button). This is the degenerate case of the instance model, not a stub.

  @milestone-1 @single-user
  Scenario: The root renders as a checkbox
    Given an instance whose Db is a bool with value false
    When I navigate to the root URL "/"
    Then I see a single checkbox
    And the checkbox is unchecked

  @milestone-1 @single-user
  Scenario: Checking the box edits the data
    Given an instance whose Db is a bool with value false
    When I navigate to the root URL "/"
    And I click the checkbox
    Then the checkbox is checked

  @milestone-1 @single-user @persistence
  Scenario: An edit autosaves and survives a reload
    Given an instance whose Db is a bool with value false
    When I navigate to the root URL "/"
    And I click the checkbox
    And I reload
    Then the checkbox is checked
