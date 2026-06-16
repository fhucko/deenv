Feature: The operator IDE (designs library + instance design selector)
  The designer (instance 4) is a URL-routed multi-instance IDE, authored as an explicit custom
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
