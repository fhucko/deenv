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

  # The generic edit form now STAGES scalar edits and commits on a Save button (autosave is
  # OFF by default — the synthesized object view omits the `autosave` arg → falsy → staged).
  # Editing the field does not touch the store until Save; Save persists it.
  @milestone-11 @single-user
  Scenario: Editing a scalar field stages until Save, then persists
    Given the self-hosted form app is running
    When I open "/notes/2"
    And I fill the "title" field with "Renamed"
    Then the store still has a "Note" whose "title" is "First"
    When I save the form
    Then the store eventually has a "Note" whose "title" is "Renamed"

  # The committed shop (instances/4, fully-auto generic UI) on a real customer page. The full
  # staged flow on three distinct outcomes: a staged edit leaves the store unchanged; Save commits
  # it; a Discard after an edit reverts the input to the stored value and leaves the store unchanged.
  @milestone-11 @single-user
  Scenario: A staged edit on a customer page does not persist until Save
    Given the shop app is running
    When I open "/customers/2"
    And I fill the "name" field with "Ada L."
    Then the store still has a "Customer" whose "name" is "Ada Lovelace"
    When I save the form
    Then the store eventually has a "Customer" whose "name" is "Ada L."

  @milestone-11 @single-user
  Scenario: Discarding a staged edit reverts the field and leaves the store unchanged
    Given the shop app is running
    When I open "/customers/2"
    And I fill the "name" field with "Throwaway"
    And I discard the form
    Then the "name" field shows "Ada Lovelace"
    And the store still has a "Customer" whose "name" is "Ada Lovelace"

  # Regression: Saving a staged ObjectForm for an object that HAS a SET prop (a customer with
  # orders) must NOT try to persist the set. The staged draft is scalar-only by design (the form
  # binds collection props to the LIVE object, never the draft), so Save (sys.setFields(obj, draft))
  # must copy/persist only scalars. Before the fix, the edit-init sys.setFields(state.draft, obj)
  # copied EVERY prop of the live customer into the draft — including `orders` — so Save fired an
  # objectPropChange for the set, which the server rejected ("Field 'orders' on 'Customer' is not a
  # scalar field") and the journal rolled back, logging a console error on every Save (the scalar
  # still persisted, masking the failure). The edited scalar persists, the orders set keeps its
  # members, and the set's objectPropChange is never even sent.
  @milestone-11 @single-user
  Scenario: Saving an object with a set prop persists the scalar and leaves the set untouched
    Given the shop app is running
    When I watch the websocket
    And I open "/customers/2"
    And I fill the "name" field with "Ada L."
    And I save the form
    Then the store eventually has a "Customer" whose "name" is "Ada L."
    And the "Customer" whose "name" is "Ada L." still has 2 orders
    And no objectPropChange was sent for "orders"

  # ── enum support (first slice) ─────────────────────────────────────────────

  @milestone-enum @single-user
  Scenario: An enum field renders as a select of its values and persists a choice
    Given the enum fixture app is running
    When I open "/orders/2"
    Then the page is a code page
    And the "status" field is a select with options "pending, shipped, delivered"
    And the "status" select displays options "Pending, Shipped, Delivered"
    When I choose "delivered" in the "status" select
    And I save the form
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
  # This fixture passes autosave={true}, the opt-in that keeps today's LIVE per-keystroke save (no
  # Save/Discard buttons). It proves the form RENDERS (humanized label + field value, built from the
  # schema via sys.schema), is LIVE (an edit autosaves through the composed form), and that the
  # autosave mode shows NO Save/Discard buttons — end-to-end.

  @milestone-11 @single-user
  Scenario: A hand-written render composes the public ObjectForm component with autosave
    Given the public-library form app is running
    When I open "/"
    Then the page is a code page
    And the page shows ".object-form"
    And the "dueDate" label reads "Due date"
    And the "title" field shows "First"
    And the form has no Save button
    When I fill the "title" field with "Renamed"
    Then the store eventually has a "Note" whose "title" is "Renamed"

  # ── same object, two independent editing contexts (milestone 11) ────────────
  # Two STAGED ObjectForms compose over the SAME object (db.note). Each is a component, so it keys on
  # its render-tree SLOT, not its arguments — the two distinct positions get two INDEPENDENT drafts.
  # Editing one form's field stages into that form's draft only (the other input is untouched) and the
  # store is untouched until a Save; Saving each commits its own draft (single-client, last-write-wins).
  # No cross-context observation/merge/conflict handling — that is the deferred real-time/multi-user
  # pillar. Pure composition of the existing per-form overlay: no new builtin or mechanism.

  @milestone-11 @single-user
  Scenario: Two editing contexts over the same object stage independently and commit last-write-wins
    Given the two-contexts form app is running
    When I open "/"
    And I fill the title field in context "A" with "Left edit"
    And I fill the title field in context "B" with "Right edit"
    Then the store still has a "Note" whose "title" is "First"
    And the title field in context "A" shows "Left edit"
    And the title field in context "B" shows "Right edit"
    When I save context "A"
    Then the store eventually has a "Note" whose "title" is "Left edit"
    When I save context "B"
    Then the store eventually has a "Note" whose "title" is "Right edit"

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

  # The generic set table shows every non-collection prop as a column, including a single
  # reference: a ref column renders the referent's LABEL (sys.field(ref, target.labelProp)),
  # or blank when unset. Note 5 references Ada → its author cell shows "Ada"; Note 4 has no
  # author → blank. Tables stay read-only (no cell inputs); editing happens on the member page.
  @milestone-11 @single-user
  Scenario: A set table renders a single reference as a label column
    Given the self-hosted reference app is running
    When I open "/notes"
    Then the page shows ".set-table"
    And the set row titled "Authored note" shows "Ada" in its reference cell
    And the set row titled "First note" shows "" in its reference cell

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

  # ── sys.resolve: URL → view-kind + bound objects (milestone 11, collapse increment 1) ──
  # `sys.resolve(path)` is the Code-level twin of the C# SsrRenderer.ResolveView dispatch: it
  # resolves a URL to { kind, target, parent, prop, typeName, parentType } — the six outcomes
  # (object / set / ref / dict / leaf / notFound) with the bound object(s). The builtin runs on
  # BOTH twins (server resolves for first paint over the schema's TypeResolver; client resolves on
  # hydrate over the SHIPPED descriptors), so a probe `fn render()` that renders the resolution as
  # text must show the SAME result before AND after hydrate — a divergence would change the DOM.
  # This proves the binding end-to-end on both interpreters; increment 2 rewrites the generic UI's
  # per-URL dispatch as a Code `fn render()` composing this builtin.

  @milestone-11 @single-user
  Scenario: sys.resolve binds an object page route
    Given the resolve-probe app is running
    When I open "/notes/2"
    Then the resolve probe kind is "object"
    And the resolve probe target title is "First"
    And the resolve probe type name is "Note"

  @milestone-11 @single-user
  Scenario: sys.resolve binds a set route to its owner and prop
    Given the resolve-probe app is running
    When I open "/notes"
    Then the resolve probe kind is "set"
    And the resolve probe prop is "notes"
    And the resolve probe type name is "Note"

  @milestone-11 @single-user
  Scenario: sys.resolve binds a single-reference route to its owner
    Given the resolve-probe app is running
    When I open "/lead"
    Then the resolve probe kind is "ref"
    And the resolve probe prop is "lead"
    And the resolve probe type name is "Person"

  @milestone-11 @single-user
  Scenario: sys.resolve binds a dictionary route to its owner and prop
    Given the resolve-probe app is running
    When I open "/settings"
    Then the resolve probe kind is "dict"
    And the resolve probe prop is "settings"
    And the resolve probe parent type is "Db"

  @milestone-11 @single-user
  Scenario: sys.resolve binds a scalar dictionary entry as a leaf
    Given the resolve-probe app is running
    When I open "/settings/lang"
    Then the resolve probe kind is "leaf"

  @milestone-11 @single-user
  Scenario: sys.resolve reports an unrouted URL as notFound
    Given the resolve-probe app is running
    When I open "/does-not-exist"
    Then the resolve probe kind is "notFound"

  # ── the generic-UI collapse: one synthesized Code render (milestone 11, increment 2+3) ──
  # The per-URL C# view dispatch is GONE. A default (no-custom-render) app now renders an object
  # page through a SINGLE framework-synthesized `fn render()` — the same custom-render path a
  # hand-written app uses — that routes via sys.resolve + composes the library. Observable proof:
  # the page still self-hosts the object form, and the shipped client UI carries a `render` (the
  # one render) with NO per-type view binding (the old `view` ViewInfo is gone). This is the
  # defining assertion of the collapse; the ~44 generic-UI scenarios above are its behavior net.

  @milestone-11 @single-user
  Scenario: A default app routes through a single synthesized Code render, not a per-type view
    Given the self-hosted form app is running
    When I open "/notes/2"
    Then the page is a code page
    And the page shows ".object-form"
    And the page routes through a single code render with no per-type view binding

  # The synthesized generic render's notFound branch self-hosts the 404 page through the SAME one
  # render, on the CLIENT too: `I open` waits for `data-hydrated`, which init() sets only AFTER the
  # client render completes — so if the synth render's `else` branch (`status = 404` + return
  # NotFoundForm()) threw on the client, this step would time out. It passing proves the collapse's
  # NotFound path runs client-side (not only the SSR 404 the "I request" scenario above checks); the
  # `.not-found` selector confirms the right branch rendered. (No data-key "code page" assertion: the
  # not-found tree has no foreach, hence no keyed element.)
  @milestone-11 @single-user
  Scenario: An unrouted URL self-hosts the not-found page through the single render
    Given the self-hosted form app is running
    When I open "/does-not-exist"
    Then the page shows ".not-found"

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

  # ── optional date/decimal/datetime left empty (pre-existing bug fix) ─────────
  # An optional `date`/`decimal`/`datetime` field left EMPTY means UNSET: the server must
  # NOT force-parse "" (which threw "String '' was not recognized as a valid DateOnly").
  # An unset optional round-trips as the empty leaf — it persists without error, the field
  # shows blank, and it reads back empty. The fixture's Reminder has all three optional
  # leaf kinds plus a required `title`, so a create that fills only the title and an edit
  # that clears a set field both exercise the empty path.

  @milestone-11 @single-user
  Scenario: Creating an object with empty date/decimal/datetime fields succeeds and round-trips empty
    Given the optional-leaves app is running
    When I open "/reminders"
    And I click the new button
    And I fill the new "title" with "Call dentist"
    And I add to the set
    Then a set row eventually shows "Call dentist"
    And the store eventually has a "Reminder" whose "title" is "Call dentist"
    And the store has a "Reminder" titled "Call dentist" whose "due" is unset
    And the store has a "Reminder" titled "Call dentist" whose "amount" is unset
    And the store has a "Reminder" titled "Call dentist" whose "at" is unset

  @milestone-11 @single-user
  Scenario: Creating an object with non-empty optional leaf values still parses and persists
    Given the optional-leaves app is running
    When I open "/reminders"
    And I click the new button
    And I fill the new "title" with "Pay rent"
    And I fill the new "due" with "2026-03-01"
    And I fill the new "amount" with "12.50"
    And I add to the set
    Then a set row eventually shows "Pay rent"
    And the store has a "Reminder" titled "Pay rent" whose "due" is "2026-03-01"
    And the store has a "Reminder" titled "Pay rent" whose "amount" is "12.50"

  @milestone-11 @single-user
  Scenario: Clearing a set date field on an existing object persists as empty
    Given the optional-leaves app is running
    When I open "/reminders/2"
    Then the "due" field shows "2026-01-01"
    When I clear the "due" field
    And I save the form
    Then the store has a "Reminder" titled "Seeded" whose "due" is unset
    When I open "/reminders/2"
    Then the "due" field shows ""
