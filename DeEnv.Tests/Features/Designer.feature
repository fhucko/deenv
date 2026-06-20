Feature: The operator IDE (designs library + instance design selector)
  The designer (instance 1) is a URL-routed multi-instance IDE, authored as an explicit custom
  `fn render()` over a `Db { designs: set of Design }` meta-schema. A `Design` is a WHOLE app —
  structured `types` plus the other app-document sections (initialData/common/ui) as source text. The
  surfaces are SEPARATE: `/designs` is the design LIBRARY (list + per-design edit/delete) and
  `/designs/<designId>` is the design EDITOR (type/prop editor + ui/common/initialData code areas, NO
  publish); `/instances` lists the hosted instances each showing its CURRENT design, and
  `/instances/<id>` is ONLY a design SELECTOR — a `<select>` dropdown of the designs with the
  instance's current one pre-selected + an Apply button that records the chosen design on the
  instance AND deploys it. The instance↔design link is an EXPLICIT reference: each instance stores a
  `designId` (the id of a design in the designer's `db.designs`), seeded so the dropdowns start
  correct and read back to pre-select. The seeded designs are FAITHFUL copies of the committed apps
  the kernel runs (todo, crm, shop). Driven against a REAL kernel host (the
  designer needs a non-empty `sys.instances`), through a browser. Milestone 10.

  @milestone-10 @single-user
  Scenario: The designs route lists the design library
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the designs list shows a design "todo"
    And the designs list shows a design "crm"

  # The designs list is rendered by the generic <SetTable> (label-only column) with a per-row action
  # cell carrying an Edit link (/designs/<id>) and a Delete button. Opening the list CLIENT-SIDE (the
  # step waits for window.initUi) proves the client now hydrates over the <SetTable>-rendered page —
  # the prior build crashed on hydration (a function-equality twin divergence) and timed out here.
  @milestone-10 @single-user
  Scenario: Each designs-list row shows its label, an Edit link, and a Delete button via SetTable
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the designs list shows a design "todo"
    And the design "todo" row has an Edit link and a Delete button
    And the design "crm" row has an Edit link and a Delete button

  # The designer (instance 1) is a managed instance like any other: no special-casing in the render.
  # It has its OWN design in db.designs (a bounded self-snapshot) and its instances-list row resolves
  # to that design through the same explicit designId reference every other row uses — so it appears in
  # BOTH lists uniformly. (The kernel always hosts the designer; the fixture seeds its designId.)
  @milestone-10 @single-user
  Scenario: The designer appears uniformly as a design and a managed instance
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the designs list shows a design "designer"
    When I open the instances list
    Then the instances list shows the instance "designer" running design "designer"

  # The "todo" design is the REAL todo app: its types include TodoItem and its UI is the real
  # custom `fn render()` — so the editor (now at /designs/<id>) shows that app's actual content.
  @milestone-10 @single-user
  Scenario: Editing a design shows its real types and its UI text
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    Then the design editor shows a type named "TodoItem"
    And the design editor shows the design's UI text in a textarea

  # The instances list shows each instance alongside the design it currently runs, resolved by the
  # explicit designId reference (todo → its seeded "todo" design).
  @milestone-10 @single-user
  Scenario: The instances route lists the hosted instances with their current design
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    Then the instances list shows the instance "todo" running design "todo"
    And the instances list shows the instance "crm" running design "crm"

  # /instances/<id> is ONLY a selector: a <select> dropdown of the designs, the instance's current
  # design pre-selected (the explicit reference read back through the <select> binding).
  @milestone-10 @single-user
  Scenario: The instance page is a design selector with the current design pre-selected
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    And I open the instance "todo"
    Then the design dropdown has the design "todo" selected

  # Apply is the deploy: picking a different design and applying records it on the instance (the
  # registry designId changes) AND projects the chosen design onto the instance's app document.
  @milestone-10 @single-user
  Scenario: Applying a different design records it and deploys it to the instance
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    And I open the instance "todo"
    And I pick the design "crm" in the dropdown
    And I apply the design
    Then the instance "todo" records the design "crm"
    And the "todo" instance's app document describes the type "Customer"

  # The end-to-end split: edit a design in /designs/<id> (rename a type + retype its reference), then
  # apply that design to its instance — the edited design is what gets deployed.
  @milestone-10 @single-user
  Scenario: Editing a design then applying it deploys the edit
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Widget"
    And I retype the prop "items" to "Widget"
    When I open the instance "todo"
    And I apply the design
    Then the "todo" instance's app document describes the type "Widget"

  # Create a design through the GENERIC create: the SetTable's own "New" button reveals its create form,
  # which the designs list customizes (a `createForm` slot) to a clean LABEL-ONLY field — NOT the default
  # all-scalars form (which for a Design would render ui/common/initialData as raw textareas). Save runs
  # the generic set.add(draft) and the row appears via the client re-render (no nav — race-free). Opening
  # it shows the (empty) editor with its label. An empty-types design is a valid LIBRARY entry; it is only
  # invalid to deploy until it gains types (added on the edit page).
  @milestone-10 @single-user
  Scenario: Creating a design via the generic New puts it in the library and it opens in the editor
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "blank"
    Then the designs list shows a design "blank"
    When I edit the design "blank"
    Then the design editor shows the design's label "blank"

  # Create an instance: the inline "New instance" form on /instances picks an existing design (a
  # <select>) + a display name + a free app/infra port pair, and Create spawns a new instance running
  # that design under that name. The kernel refreshes its live set, so the new instance shows in the list
  # (race-free) under the typed name — and its NEW registry entry carries the picked design's id, so
  # opening it pre-selects that design in the selector.
  @milestone-10 @single-user
  Scenario: Creating an instance from the list form spawns it running the picked design
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    And I create an instance named "myapp" from the design "crm" on a free port pair
    Then a new instance "myapp" running design "crm" appears in the instances list
    When I open that new instance
    Then the design dropdown has the design "crm" selected

  # A prop's cardinality (single / set / dictionary) -- and a dictionary's key type -- are editable in
  # the designer, not just authorable in the .app text. A set's element must be an object type; a
  # dictionary carries a key type. Picking them and applying deploys the collection-shaped props through
  # the same projection the seeds use, so the designer can author the whole data model (sets/dicts), not
  # only single-valued props.
  @milestone-10 @single-user
  Scenario: A prop's cardinality and key type are editable and deploy
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I retype the prop "checked" to "TodoList"
    And I set the prop "checked" cardinality to "set"
    And I set the prop "text" cardinality to "dictionary"
    And I set the prop "text" key type to "text"
    When I open the instance "todo"
    And I apply the design
    Then the "todo" instance's app document declares "checked set of TodoList"
    And the "todo" instance's app document declares "text dict of text by text"

  # An enum type's value list is authorable in the designer, not just in the .app text. Adding a type,
  # setting its base type to "enum", and filling its (always-rendered, comma-separated) values input,
  # then applying, deploys the enum through the same projection — so the designer can author an enum's
  # values end-to-end. The deployed document declares the enum in the canonical `Name enum` + indented
  # values form.
  @milestone-10 @single-user
  Scenario: An enum type authored in the designer deploys
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I add a type to the design
    And I name the just-added type "Status"
    And I set the just-added type's base type to "enum"
    And I set the just-added type's values to "open, doing, done"
    When I open the instance "todo"
    And I apply the design
    Then the "todo" instance's app document declares the enum "Status" with values "open, doing, done"

  # Removing a type from a design must actually delete it. The remove drives arrayRemove on the design's
  # (nested) types set, which runs the store's garbage collector -- and the GC walks the whole meta-schema
  # graph, including a MetaProp object whose `fields` carries a key literally named "type" (the prop's data
  # type) whose value is a tagged-value object. Regression: the GC read that as a scalar tag and threw "must
  # be of type 'JsonValue'" on EVERY nested-set remove, so the server rejected it and the client rolled the
  # row back -- no type (or prop, or design) could be deleted in the designer. The user-reported symptom was
  # "I can't remove a type I just added and haven't named", but it is name- and timing-independent.
  @milestone-10 @single-user
  Scenario: A type added to a design can be removed again
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I add a type to the design
    And I remove the just-added unnamed type
    Then the design "todo" has no unnamed type

  # Progressive disclosure: a field that is only meaningful in one shape is hidden until that shape is
  # chosen, so the editor is not a wall of permanently-blank inputs. The key-type field appears only for
  # a dictionary prop; a type's props editor shows for the object kind and its enum-values field for the
  # enum kind (never both). The kind is a dropdown sourced from the system vocabulary, not free text.
  # This also proves the conditional fields reconcile on the client when cardinality / kind change.
  @milestone-10 @single-user
  Scenario: Irrelevant fields are hidden until their shape is chosen
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    Then the prop "text" shows no key-type field
    When I set the prop "text" cardinality to "dictionary"
    Then the prop "text" shows a key-type field
    When I add a type to the design
    And I name the just-added type "Status"
    Then the just-added type shows a props editor
    And the just-added type shows no values field
    When I set the just-added type's base type to "enum"
    Then the just-added type shows a values field
    And the just-added type shows no props editor

  # The prop-type picker is a dropdown, not a free-text input: it offers the built-in scalar types AND
  # this design's own types, kept in SEPARATE groups so system vocabulary is not flatly intermixed with
  # user-defined types. Picking a type writes it through the <select> binding exactly as before.
  @milestone-10 @single-user
  Scenario: The prop-type picker offers built-in scalars and the design's own types, grouped
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    Then the prop "text" type picker offers the built-in type "text"
    And the prop "text" type picker offers the design type "TodoList"
    And the prop "text" type picker keeps built-in and design types in separate groups

  # The designs list now uses the GENERIC create — the SetTable's own "New" button is the SINGLE create
  # control (the old bespoke .new-design "Add" box is gone). The list customizes the SetTable's create
  # form (a `createForm` slot) to a clean LABEL-ONLY field, so revealing it shows NO raw ui/common/
  # initialData textareas (the default all-scalars form WOULD render those). So the page shows EXACTLY ONE
  # create affordance, and that affordance does not expose the code sections as raw text boxes.
  @milestone-10 @single-user
  Scenario: The designs list shows exactly one create control and it is the generic New
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the designs list shows the generic SetTable New as its only create control
    And the designs list does not show a bespoke Add box
    When I reveal the generic create form
    Then the create form has a labeled "label" field
    And the create form shows no code-section textareas

  # An action-managed SetTable (one given `rowActions`) does NOT sit under the whole-row click overlay
  # (the stretched a.row-link::after that the data-table path uses). So the per-row Edit link and Delete
  # button are directly clickable — no per-consumer z-index band-aid needed. This proves both render and
  # are hit-testable (a click reaches them, not the overlay).
  @milestone-10 @single-user
  Scenario: The designs-list Edit and Delete are clickable, not under a row overlay
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the design "todo" Edit link receives the click
    And the design "todo" Delete button receives the click

  # Deleting a design destroys a whole app (unrecoverable), so Delete is a deliberate two-step inline
  # confirm — the SAME pattern the instances list uses for inline rename. Clicking Delete does NOT remove
  # the row; it arms a confirm (Delete? [Yes] [Cancel]). Cancel restores the plain Delete; Yes removes
  # the design. This verifies the in-row confirm toggles and reconciles correctly on the client.
  @milestone-10 @single-user
  Scenario: Deleting a design asks for confirmation; Cancel restores, Yes removes
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I add a design named "scratch"
    And I click Delete on the design "scratch"
    Then the design "scratch" shows a delete confirmation
    And the design "scratch" is still listed
    When I cancel the delete of the design "scratch"
    Then the design "scratch" shows no delete confirmation
    And the design "scratch" is still listed
    When I click Delete on the design "scratch"
    And I confirm the delete of the design "scratch"
    Then the designs list eventually drops the design "scratch"

  # The IDE nav marks the current section (Instances / Designs) with an is-active class, so the operator
  # can see where they are. Zero new data — the render already computes the section from the path.
  @milestone-10 @single-user
  Scenario: The nav marks the active section
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the nav "Designs" link is active
    And the nav "Instances" link is not active
    When I open the instances list
    Then the nav "Instances" link is active
    And the nav "Designs" link is not active

  # A bad /designs/<id> (no such design) must not render a blank page under the heading — it shows a
  # "not found" message, matching the model's own not-found discipline, with the Back link still present.
  @milestone-10 @single-user
  Scenario: A non-existent design id shows a not-found message
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open a non-existent design
    Then the design editor shows a not-found message
    And the design editor keeps its Back link

  # The design's label is editable IN the editor: a two-way-bound <input> (was a read-only heading), so a
  # design can be renamed where it is edited. The rename is a journaled scalar autosave to the designer's
  # store, so it survives a fresh server render — reloading the editor shows the new label.
  @milestone-10 @single-user
  Scenario: The editor's design label is an editable input that persists a rename
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "draft1"
    And I edit the design "draft1"
    And I rename the design's label to "renamed1"
    And I reload the design editor
    Then the design editor's label input holds "renamed1"

  # The chosen create path is LABEL-ONLY: the generic create form sets the new design's label, then types
  # are added on the EDIT page (the create form cannot author nested types — a set.add carries only the
  # draft's scalar props over the wire, so a type added during create would be silently dropped on Save).
  # This proves the whole flow: create with a label, open the editor, add a type, and it persists to the
  # designer's store (positive ids — the nested type round-tripped through its own arrayAdd).
  @milestone-10 @single-user
  Scenario: Create sets the label, then the edit page adds a type that persists
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "withtype"
    And I edit the design "withtype"
    And I add a type to the design
    And I name the just-added type "Thing"
    Then the design "withtype" has a stored type named "Thing"
