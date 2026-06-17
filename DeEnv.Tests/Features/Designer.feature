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
  the kernel runs (instance = the todo app, crm, shop). Driven against a REAL kernel host (the
  designer needs a non-empty `sys.instances`), through a browser. Milestone 10.

  @milestone-10 @single-user
  Scenario: The designs route lists the design library
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the designs list
    Then the designs list shows a design "instance"
    And the designs list shows a design "crm"

  # The "instance" design is the REAL todo app: its types include TodoItem and its UI is the real
  # custom `fn render()` — so the editor (now at /designs/<id>) shows that app's actual content.
  @milestone-10 @single-user
  Scenario: Editing a design shows its real types and its UI text
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the designs list
    And I edit the design "instance"
    Then the design editor shows a type named "TodoItem"
    And the design editor shows the design's UI text in a textarea

  # The instances list shows each instance alongside the design it currently runs, resolved by the
  # explicit designId reference (instance → its seeded "instance" design).
  @milestone-10 @single-user
  Scenario: The instances route lists the hosted instances with their current design
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the instances list
    Then the instances list shows the instance "instance" running design "instance"
    And the instances list shows the instance "crm" running design "crm"

  # /instances/<id> is ONLY a selector: a <select> dropdown of the designs, the instance's current
  # design pre-selected (the explicit reference read back through the <select> binding).
  @milestone-10 @single-user
  Scenario: The instance page is a design selector with the current design pre-selected
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the instances list
    And I open the instance "instance"
    Then the design dropdown has the design "instance" selected

  # Apply is the deploy: picking a different design and applying records it on the instance (the
  # registry designId changes) AND projects the chosen design onto the instance's app document.
  @milestone-10 @single-user
  Scenario: Applying a different design records it and deploys it to the instance
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the instances list
    And I open the instance "instance"
    And I pick the design "crm" in the dropdown
    And I apply the design
    Then the instance "instance" records the design "crm"
    And the "instance" instance's app document describes the type "Customer"

  # The end-to-end split: edit a design in /designs/<id> (rename a type + retype its reference), then
  # apply that design to its instance — the edited design is what gets deployed.
  @milestone-10 @single-user
  Scenario: Editing a design then applying it deploys the edit
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the designs list
    And I edit the design "instance"
    And I rename the type "TodoItem" to "Widget"
    And I retype the prop "items" to "Widget"
    When I open the instance "instance"
    And I apply the design
    Then the "instance" instance's app document describes the type "Widget"

  # Create a design: the inline "New design" form on /designs adds a fresh, empty, labelled design to
  # db.designs. It appears in the library via the client re-render (no nav — race-free), and opening it
  # shows the (empty) editor. An empty-types design is a valid LIBRARY entry; it is only invalid to
  # deploy until it gains types.
  @milestone-10 @single-user
  Scenario: Adding a design from the list form puts it in the library and it opens in the editor
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the designs list
    And I add a design named "blank"
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
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
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
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the designs list
    And I edit the design "instance"
    And I retype the prop "checked" to "TodoList"
    And I set the prop "checked" cardinality to "set"
    And I set the prop "text" cardinality to "dictionary"
    And I set the prop "text" key type to "text"
    When I open the instance "instance"
    And I apply the design
    Then the "instance" instance's app document declares "checked set of TodoList"
    And the "instance" instance's app document declares "text dict of text by text"

  # An enum type's value list is authorable in the designer, not just in the .app text. Adding a type,
  # setting its base type to "enum", and filling its (always-rendered, comma-separated) values input,
  # then applying, deploys the enum through the same projection — so the designer can author an enum's
  # values end-to-end. The deployed document declares the enum in the canonical `Name enum` + indented
  # values form.
  @milestone-10 @single-user
  Scenario: An enum type authored in the designer deploys
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the designs list
    And I edit the design "instance"
    And I add a type to the design
    And I name the just-added type "Status"
    And I set the just-added type's base type to "enum"
    And I set the just-added type's values to "open, doing, done"
    When I open the instance "instance"
    And I apply the design
    Then the "instance" instance's app document declares the enum "Status" with values "open, doing, done"

  # Removing a type from a design must actually delete it. The remove drives arrayRemove on the design's
  # (nested) types set, which runs the store's garbage collector -- and the GC walks the whole meta-schema
  # graph, including a MetaProp object whose `fields` carries a key literally named "type" (the prop's data
  # type) whose value is a tagged-value object. Regression: the GC read that as a scalar tag and threw "must
  # be of type 'JsonValue'" on EVERY nested-set remove, so the server rejected it and the client rolled the
  # row back -- no type (or prop, or design) could be deleted in the designer. The user-reported symptom was
  # "I can't remove a type I just added and haven't named", but it is name- and timing-independent.
  @milestone-10 @single-user
  Scenario: A type added to a design can be removed again
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the designs list
    And I edit the design "instance"
    And I add a type to the design
    And I remove the just-added unnamed type
    Then the design "instance" has no unnamed type
