Feature: The operator IDE (routed multi-instance designer)
  The designer (instance 4) is a URL-routed multi-instance IDE, authored as an explicit custom
  `fn render()` over a `Db { designs: set of Design }` meta-schema. A `Design` is a WHOLE app —
  structured `types` plus the other app-document sections (initialData/common/ui) as source text. An
  instance references its design by the registry `app` LABEL (an instance's `app` == its design's
  `label`), and the IDE matches `sys.instances` rows to `db.designs` by it. `/instances` lists the
  hosted instances with their matched designs; `/instances/<id>` edits the design that instance
  references (a type/prop editor + code-area inputs for ui/common/initialData) and publishes it back.
  Driven against a REAL kernel host (the designer needs a non-empty `sys.instances`), through a browser.
  Milestone 10.

  @milestone-10 @single-user
  Scenario: The instances route lists the hosted instances with their designs
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the instances list
    Then the list shows the instance "instance" with design "instance"
    And the list shows the instance "crm" with design "crm"

  @milestone-10 @single-user
  Scenario: Editing an instance shows the design it references — its types and its UI text
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the instances list
    And I edit the instance "instance"
    Then the design editor shows a type named "Item"
    And the design editor shows the design's UI text

  @milestone-10 @single-user
  Scenario: Renaming a type and publishing updates the target instance's app document
    Given the operator IDE is running on a kernel hosting instances "instance" and "crm"
    When I open the instances list
    And I edit the instance "instance"
    And I rename the type "Item" to "Widget"
    And I publish the design
    Then the "instance" instance's app document describes the type "Widget"
