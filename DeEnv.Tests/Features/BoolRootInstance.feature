Feature: Bool-root instance
  The simplest valid instance: an object Db with a single bool field. It renders
  as one checkbox in the self-hosted generic UI's ObjectForm, which STAGES scalar
  edits and commits them on a Save button (autosave is off by default). This is
  the degenerate case of the instance model, not a stub.

  @milestone-1 @single-user
  Scenario: The root renders as a checkbox
    Given an instance whose Db is a bool with value false
    When I navigate to the root URL "/"
    Then I see a single checkbox
    And the checkbox is unchecked

  @milestone-11 @single-user
  Scenario: Checking the box stages the edit (the checkbox reflects it)
    Given an instance whose Db is a bool with value false
    When I navigate to the root URL "/"
    And I click the checkbox
    Then the checkbox is checked

  @milestone-11 @single-user @persistence
  Scenario: A staged edit committed on Save survives a reload
    Given an instance whose Db is a bool with value false
    When I navigate to the root URL "/"
    And I click the checkbox
    And I save the form
    Then the root bool eventually persists as checked
    When I reload
    Then the checkbox is checked
