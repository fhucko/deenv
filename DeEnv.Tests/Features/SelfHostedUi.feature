Feature: Self-hosted generic UI (object forms)
  The self-hosted generic UI is the DEFAULT for any app without a custom `fn render()`.
  The generic object page is re-expressed in the Code language: an `objectForm` library
  renders a form from the type's schema (a Code value) using the `field(obj, name)`
  builtin for dynamic, two-way-bound access — plugged in as a synthesized per-type view.
  An object that holds a set self-hosts too: objectForm renders the set as an inline
  table whose member rows link to the nested member URL (path-walk); a dictionary renders
  inline as a `dictTable`, its route as a `dictTable` page, and each entry on its own page
  (an object entry via objectForm, a scalar entry via the shared leaf editor). A dict
  entry has no extent id, so its field edits persist path-addressed (the `write` op).

  @milestone-9 @single-user
  Scenario: An unrouted URL is a self-hosted 404
    Given the self-hosted form app is running
    When I request "/does-not-exist"
    Then the response status is 404
    And the response body contains "Not found"

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
    And the set row link points at "/notes/2"

  @milestone-9 @single-user
  Scenario: A nested member link from the root resolves to its self-hosted page
    Given the self-hosted form app is running
    When I open "/"
    And I follow the set row link
    Then the page is a code page
    And the page shows ".object-form"
    And the "title" field is a "text" input

  # ── dictionaries (slice: dicts self-host) ──────────────────────────────────

  @milestone-9 @single-user
  Scenario: A dictionary self-hosts as a table
    Given the self-hosted dict app is running
    When I open "/"
    Then the page is a code page
    And the page shows ".dict-table"

  @milestone-9 @single-user
  Scenario: Adding a dictionary entry persists and shows
    Given the self-hosted dict app is running
    When I open "/"
    And I fill the new key with "alpha"
    And I fill the new "value" with "Hello"
    And I add the dict entry
    Then a dict row eventually shows "alpha"
    And the store eventually has a "Setting" whose "value" is "Hello"

  @milestone-9 @single-user
  Scenario: Deleting a dictionary entry removes it
    Given the self-hosted dict app is running
    When I open "/"
    And I fill the new key with "beta"
    And I fill the new "value" with "Bye"
    And I add the dict entry
    And a dict row eventually shows "beta"
    And I remove the dict row "beta"
    Then no dict row eventually shows "beta"

  @milestone-9 @single-user
  Scenario: A scalar dictionary entry's own page edits its value (path-addressed)
    Given the self-hosted scalar dict app is running
    When I open "/"
    And I fill the new key with "lang"
    And I fill the new "value" with "en"
    And I add the dict entry
    And the dict entry "lang" eventually has value "en"
    And I open "/settings/lang"
    And I fill the "value" field with "fr"
    Then the dict entry "lang" eventually has value "fr"

  @milestone-9 @single-user
  Scenario: A scalar dictionary self-hosts and an entry persists
    Given the self-hosted scalar dict app is running
    When I open "/"
    Then the page is a code page
    And the page shows ".dict-table"
    When I fill the new key with "currency"
    And I fill the new "value" with "USD"
    And I add the dict entry
    Then a dict row eventually shows "currency"
    And the dict entry "currency" eventually has value "USD"

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

  # The generic object form is built entirely from the `sys` namespace builtins
  # (sys.humanize for the label, sys.field for the value) — a named proof that the
  # framework names self-host through `sys`, not only incidental coverage.
  @milestone-10 @single-user
  Scenario: A generic object form renders a humanized label and a field value through sys
    Given the self-hosted form app is running
    When I open "/notes/2"
    Then the "dueDate" label reads "Due date"
    And the "title" field shows "First"

  @milestone-9 @single-user
  Scenario: Editing a field autosaves over the WebSocket
    Given the self-hosted form app is running
    When I open "/notes/2"
    And I fill the "title" field with "Renamed"
    Then the store eventually has a "Note" whose "title" is "Renamed"

  # ── enum support (first slice) ─────────────────────────────────────────────

  @milestone-enum @single-user
  Scenario: An enum field renders as a select of its values and persists a choice
    Given the enum fixture app is running
    When I open "/orders/2"
    Then the page is a code page
    And the "status" field is a select with options "pending, shipped, delivered"
    And the "status" select displays options "Pending, Shipped, Delivered"
    When I choose "delivered" in the "status" select
    Then the store eventually has a "Order" whose "status" is "delivered"

  # ── component-local state (creation prototype) ─────────────────────────────

  @milestone-9 @single-user
  Scenario: A component holds creation state and resets after Create
    Given the component form app is running
    When I open "/"
    And I fill the draft title with "Buy milk"
    And I click create
    Then the note list eventually shows "Buy milk"
    And the draft title is empty

  # ── the public component library (milestone 11) ────────────────────────────
  # The generic-UI library (ObjectForm/RefEditor/…) is PUBLIC: a hand-written `fn render()`
  # composes <ObjectForm> directly — the same component the @milestone-9 object pages synthesize.
  # This proves it both RENDERS (humanized label + field value, built from the schema via
  # sys.schema) and is LIVE (an edit autosaves through the composed form), end-to-end.

  @milestone-11 @single-user
  Scenario: A hand-written render composes the public ObjectForm component
    Given the public-library form app is running
    When I open "/"
    Then the page is a code page
    And the page shows ".object-form"
    And the "dueDate" label reads "Due date"
    And the "title" field shows "First"
    When I fill the "title" field with "Renamed"
    Then the store eventually has a "Note" whose "title" is "Renamed"

  # ── reactive components: slot-path identity (milestone 11, slice 1) ─────────
  # A component invoked as a tag keys on its render-tree slot, not its arguments, so its
  # local state survives a re-render even when the argument is a fresh object each time.

  @milestone-11 @single-user
  Scenario: A component's draft survives a render even when its argument is rebuilt fresh
    Given the rebuilt-descriptor component app is running
    When I open "/"
    And I fill the draft title with "Buy mi"
    And I toggle the unrelated flag
    Then the draft title is still "Buy mi"
    When I click create
    Then the note list eventually shows "Buy mi"
    And the draft title is empty

  # A component used inside a foreach keys on the member's identity, so each row's local state
  # is independent and follows the object across a reorder (slice 2 — lists/keys).
  @milestone-11 @single-user
  Scenario: Per-row component state is independent and follows the row's identity across reorder
    Given the row-component list app is running
    When I open "/"
    And I type "X" into the scratch of the row titled "Alpha"
    And I type "Y" into the scratch of the row titled "Beta"
    Then the scratch of the row titled "Alpha" is "X"
    And the scratch of the row titled "Beta" is "Y"
    When I reorder the rows
    Then the first row is titled "Beta"
    And the scratch of the row titled "Alpha" is "X"
    And the scratch of the row titled "Beta" is "Y"

  # An explicit key on a component folds into its slot identity, so changing the key resets it
  # (fresh setup + state) — the opt-in "reset when X changes" escape hatch (slice 3).
  @milestone-11 @single-user
  Scenario: An explicit key resets a component when the key changes
    Given the keyed component app is running
    When I open "/"
    And I type "Z" into the box scratch
    Then the box scratch is "Z"
    When I rekey the component
    Then the box scratch is ""

  # A component returned directly by `fn render()` (value/root position, not a tag-child) is
  # recognized and slot-keyed, so its state survives a rebuilt-argument re-render (slice 4b).
  @milestone-11 @single-user
  Scenario: A root-position component's state survives a re-render with a rebuilt argument
    Given the root-component app is running
    When I open "/"
    And I fill the draft title with "Buy mi"
    And I toggle the unrelated flag
    Then the draft title is still "Buy mi"
    When I click create
    Then the note list eventually shows "Buy mi"
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

  # ── flag-gated create view (milestone 11) ──────────────────────────────────
  # The always-visible inline add row is replaced by a `+ New` button that reveals a
  # labeled create form (the same Field label+Input the edit page uses), swapping out
  # the table; Save commits + returns to the table, Cancel discards. Hidden until asked.

  @milestone-11 @single-user
  Scenario: A set table shows a New button, not an inline add form, on load
    Given the self-hosted form app is running
    When I open "/notes"
    Then the page shows ".set-table"
    And the page shows ".new-btn"
    And the page does not show ".set-new"

  @milestone-11 @single-user
  Scenario: Clicking New reveals a labeled create form in place of the set table
    Given the self-hosted form app is running
    When I open "/notes"
    And I click the new button
    Then the page shows ".create-form"
    And the create form has a labeled "title" field
    And the page does not show ".set-table"

  # The Note has a `dueDate date` prop, so the create form has a date field; fill it (an empty
  # date is rejected on add — a pre-existing model gap in empty-date handling, NOT specific to the
  # create form), proving the labeled multi-field create flow commits end-to-end.
  @milestone-11 @single-user
  Scenario: Saving the create form adds the member and returns to the table
    Given the self-hosted form app is running
    When I open "/notes"
    And I click the new button
    And I fill the new "title" with "Second"
    And I fill the new "dueDate" with "2026-02-02"
    And I add to the set
    Then a set row eventually shows "Second"
    And the store eventually has a "Note" whose "title" is "Second"
    And the page does not show ".create-form"

  @milestone-11 @single-user
  Scenario: Cancelling the create form returns to the table with no new member
    Given the self-hosted form app is running
    When I open "/notes"
    And I click the new button
    And I fill the new "title" with "Discarded"
    And I cancel the create form
    Then the page shows ".set-table"
    And the page does not show ".create-form"
    And no set row eventually shows "Discarded"

  @milestone-11 @single-user
  Scenario: A dictionary create form reveals labeled Key and value fields
    Given the self-hosted dict app is running
    When I open "/"
    And I click the new button
    Then the page shows ".create-form"
    And the create form has a labeled "dict-key" field
    And the create form has a labeled "value" field
    And the page does not show ".dict-table"

  @milestone-11 @single-user
  Scenario: A dictionary create form with a missing key shows a validation error and adds nothing
    Given the self-hosted dict app is running
    When I open "/"
    And I click the new button
    And I fill the new "value" with "Orphan"
    And I add the dict entry
    Then I see a create error
    And the page shows ".create-form"

  # ── navigable tables (milestone 11: the UI middle-ground) ───────────────────
  # A whole table row navigates to its entry via a stretched row-link anchor; the
  # header aligns with the body (one cell per body column, incl. the Remove column);
  # bool columns render a read-only ✓/✗ glyph; a per-row Remove deletes without
  # navigating (the handled click is consumed, not bubbled to the row link).

  @milestone-11 @single-user
  Scenario: Clicking a set member row navigates to that member's page
    Given the self-hosted form app is running
    When I open "/notes"
    And I click the set row titled "First"
    Then the URL path becomes "/notes/2"
    And the page shows ".object-form"
    And the "title" field shows "First"

  @milestone-11 @single-user
  Scenario: Clicking a row's Remove removes the member and does not navigate
    Given the self-hosted form app is running
    When I open "/notes"
    Then a set row shows "First"
    When I remove the set row titled "First"
    Then no set row eventually shows "First"
    And the URL path is still "/notes"

  @milestone-11 @single-user
  Scenario: A set table's header aligns with its body rows
    Given the self-hosted form app is running
    When I open "/notes"
    Then the page shows ".set-table"
    And the set table header has a trailing action column
    And the set table header column count equals the body row column count

  @milestone-11 @single-user
  Scenario: A bool column renders a read-only glyph, not the text true/false
    Given the self-hosted form app is running
    When I open "/notes"
    Then a set row shows "First"
    And the "First" row's "done" cell shows the bool glyph for false
    And no set row shows the text "false"

  @milestone-11 @single-user
  Scenario: A dictionary table's header aligns with its body rows
    Given the self-hosted dict app is running
    When I open "/"
    And I fill the new key with "alpha"
    And I add the dict entry
    Then a dict row eventually shows "alpha"
    And the dict table header has a trailing action column
    And the dict table header column count equals the body row column count

  # ── breadcrumbs on a collection/route page (milestone 11 bug fix) ───────────
  # The breadcrumb trail is the REQUEST url path, segment for segment — not the
  # view's argument-binding path (which, for an owner-bound set route, is the
  # PARENT object and so showed only "Db").

  @milestone-11 @single-user
  Scenario: The breadcrumb trail on a set route shows the full URL path
    Given the self-hosted form app is running
    When I open "/notes"
    Then the breadcrumbs read "Db / notes"

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
