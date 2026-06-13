Feature: Self-hosted generic UI (object forms)
  An app opts into the self-hosted generic UI with `generic` in its `ui` section.
  The generic object page is then re-expressed in the Code language: an `objectForm`
  library renders a form from the type's schema (a Code value) using the `field(obj,
  name)` builtin for dynamic, two-way-bound access — plugged in at the lowest view
  precedence as a synthesized per-type view. Pages that are not all-scalar object pages
  (the Db root, a set) stay on the C# auto-form. Slice 1: object forms only.

  @milestone-9 @single-user
  Scenario: An all-scalar object page is rendered by the self-hosted form
    Given the self-hosted form app is running
    When I open "/notes/2"
    Then the page is a code page
    And the page shows ".object-form"

  @milestone-9 @single-user
  Scenario: Pages without a self-hosted view stay on the generic auto-form
    Given the self-hosted form app is running
    When I open "/"
    Then the page is a generic auto-form

  @milestone-9 @single-user
  Scenario: Input kind follows the prop's base type
    Given the self-hosted form app is running
    When I open "/notes/2"
    Then the "title" field is a "text" input
    And the "count" field is a "number" input
    And the "done" field is a "checkbox" input
    And the "dueDate" field is a "date" input

  @milestone-9 @single-user
  Scenario: Field labels are humanized
    Given the self-hosted form app is running
    When I open "/notes/2"
    Then the "dueDate" label reads "Due date"

  @milestone-9 @single-user
  Scenario: Editing persists on Save over the WebSocket
    Given the self-hosted form app is running
    When I open "/notes/2"
    And I fill the "title" field with "Renamed"
    And I save the form
    Then the store eventually has a "Note" whose "title" is "Renamed"
