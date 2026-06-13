Feature: Self-hosted generic UI (object forms)
  An app opts into the self-hosted generic UI with `generic` in its `ui` section.
  The generic object page is then re-expressed in the Code language: an `objectForm`
  library renders a form from the type's schema (a Code value) using the `field(obj,
  name)` builtin for dynamic, two-way-bound access — plugged in at the lowest view
  precedence as a synthesized per-type view. An object that holds a set self-hosts too:
  objectForm renders the set as an inline table whose member rows link to the nested
  member URL (path-walk). Only a type holding an arbitrary-key dictionary still falls to
  the C# auto-form (no dicts in the Code runtime yet).

  @milestone-9 @single-user
  Scenario: An all-scalar object page is rendered by the self-hosted form
    Given the self-hosted form app is running
    When I open "/notes/2"
    Then the page is a code page
    And the page shows ".object-form"

  @milestone-9 @single-user
  Scenario: The Db root self-hosts and renders its set inline with nested links
    Given the self-hosted form app is running
    When I open "/"
    Then the page is a code page
    And the page shows ".object-form"
    And the page shows ".set-table"
    And a set row shows "First"
    And a set member open link points at "/notes/2"

  @milestone-9 @single-user
  Scenario: A nested member link from the root resolves to its self-hosted page
    Given the self-hosted form app is running
    When I open "/"
    And I follow the set member open link
    Then the page is a code page
    And the page shows ".object-form"
    And the "title" field is a "text" input

  @milestone-9 @single-user
  Scenario: A type holding a dictionary stays on the generic auto-form
    Given the self-hosted dict app is running
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
  Scenario: Editing a field autosaves over the WebSocket
    Given the self-hosted form app is running
    When I open "/notes/2"
    And I fill the "title" field with "Renamed"
    Then the store eventually has a "Note" whose "title" is "Renamed"

  # ── component-local state (creation prototype) ─────────────────────────────

  @milestone-9 @single-user
  Scenario: A component holds creation state and resets after Create
    Given the component form app is running
    When I open "/"
    And I fill the draft title with "Buy milk"
    And I click create
    Then the note list eventually shows "Buy milk"
    And the draft title is empty

  # ── set tables (slice 3) ───────────────────────────────────────────────────

  @milestone-9 @single-user
  Scenario: A set route renders a self-hosted table and adds a member
    Given the self-hosted reference app is running
    When I open "/notes"
    Then the page is a code page
    And the page shows ".set-table"
    And a set row shows "First note"
    When I fill the new "title" with "Second note"
    And I add to the set
    Then a set row eventually shows "Second note"
    And the store eventually has a "Note" whose "title" is "Second note"

  # ── references: the self-hosted pick-or-clear editor (slice 2) ──────────────

  @milestone-9 @single-user
  Scenario: A reference route renders the self-hosted editor
    Given the self-hosted reference app is running
    When I open "/lead"
    Then the page is a code page
    And the page shows ".ref-editor"
    And a reference candidate "Ada" is offered
    And a reference candidate "Grace" is offered

  @milestone-9 @single-user
  Scenario: Picking an existing object on a reference route persists
    Given the self-hosted reference app is running
    When I open "/lead"
    And I pick the reference candidate "Ada"
    Then the current reference is "Ada"
    And the "/lead" reference eventually points at "Ada"

  @milestone-9 @single-user
  Scenario: Creating a new object through a reference mints and points at it
    Given the self-hosted reference app is running
    When I open "/lead"
    And I fill the new "name" with "Carol"
    And I create the new object
    Then the current reference is "Carol"
    And the "/lead" reference eventually points at "Carol"

  @milestone-9 @single-user
  Scenario: Clearing a reference unsets it
    Given the self-hosted reference app is running
    When I open "/lead"
    And I pick the reference candidate "Ada"
    And I clear the reference
    Then the current reference is "(none)"

  @milestone-9 @single-user
  Scenario: A reference field inside an object form is a self-hosted picker
    Given the self-hosted reference app is running
    When I open "/notes/4"
    Then the page shows ".object-form"
    And the page shows ".ref-editor"
    When I pick the reference candidate "Grace"
    Then the current reference is "Grace"
