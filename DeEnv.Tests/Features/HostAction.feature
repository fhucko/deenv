Feature: Host-side actions — sys.create / sys.publish / sys.clone / sys.delete / sys.setDesign (server-side instance ops)
  The host-side action primitives: Code hands a DESIGN (or instance ids) to a server-side action that
  operates on the kernel's instances. A `Design` is a WHOLE app — structured `types` plus the other
  app-document sections (`initialData`/`common`/`ui`) carried as source text — and the designer holds a
  set of them (`db.designs`). `sys.publish(design, targetId)` projects the WHOLE design onto an EXISTING
  instance (replacing its document + resetting its data — keeping its custom UI / seed / shared
  functions, not just its types); `sys.create(design, appPort, infraPort)` projects the same design into
  a NEW instance on the given ports; `sys.cloneInstance(sourceId, appPort, infraPort)` copies a source
  instance's app doc AND data into a NEW instance on the given ports; `sys.delete(targetId)` removes an
  existing instance; `sys.setDesign(design, targetId)` is the IDE's "Apply" — it RECORDS (on the target's
  registry entry) which design the instance now runs AND deploys it (publish + the registry write that
  makes the reference explicit). A design crosses the wire as the object's id (one member of the
  designer's `designs` set); instance ids are bare ints. All are server-only builtins (no client
  mutation), carried over the `hostAction` WS op and the per-instance IHostActions seam. Driven at the
  WS-handler seam (like the schema bridge) — no browser.

  @milestone-10 @single-user
  Scenario: Publish projects the WHOLE app — types AND the custom UI — onto the target
    Given a designer instance holding a design with a type "Item" and a custom render
    And a target instance addressed by an id
    When the designer publishes that design to the target's id over the WS
    Then the host action reply is ok
    And the target app document describes the designed type "Item"
    And the target app document contains the custom render
    And the target instance's data is reset

  @milestone-10 @single-user
  Scenario: Publishing an invalid design is rejected and writes nothing
    Given a designer instance holding a design whose root is an object type with no props
    And a target instance addressed by an id
    When the designer publishes that design to the target's id over the WS
    Then the host action reply is an error mentioning "props"
    And the target app document is unchanged

  @milestone-10 @single-user
  Scenario: Publishing an id that is not a design is rejected and writes nothing
    Given a designer instance holding a design with a type "Item" and a custom render
    And a target instance addressed by an id
    When the designer publishes a non-design id to the target's id over the WS
    Then the host action reply is an error
    And the target app document is unchanged

  @milestone-10 @single-user
  Scenario: Publishing to an unknown target id is rejected and writes nothing
    Given a designer instance holding a design with a type "Item" and a custom render
    And a target instance addressed by an id
    When the designer publishes that design to an unknown target id over the WS
    Then the host action reply is an error
    And the target app document is unchanged

  @milestone-10 @single-user
  Scenario: Create projects the passed design into a new instance on the given ports
    Given a designer instance holding a design with a type "Item" and a custom render
    When the designer creates an instance from that design on ports 9100 and 9101 over the WS
    Then the host action reply is ok
    And a new instance was created on ports 9100 and 9101
    And the created app document describes the designed type "Item"
    And the created app document contains the custom render

  @milestone-10 @single-user
  Scenario: Creating from an invalid design is rejected and spawns nothing
    Given a designer instance holding a design whose root is an object type with no props
    When the designer creates an instance from that design on ports 9100 and 9101 over the WS
    Then the host action reply is an error mentioning "props"
    And no instance was created

  @milestone-10 @single-user
  Scenario: Creating from an id that is not a design is rejected and spawns nothing
    Given a designer instance holding a design with a type "Item" and a custom render
    When the designer creates an instance from a non-design id over the WS
    Then the host action reply is an error
    And no instance was created

  @milestone-10 @single-user
  Scenario: Delete asks the kernel to remove the instance by id
    When the operator deletes instance id 7 over the WS
    Then the host action reply is ok
    And the kernel was asked to delete instance id 7

  @milestone-10 @single-user
  Scenario: Clone asks the kernel to copy the source instance onto the given ports
    When the operator clones instance id 7 onto ports 9100 and 9101 over the WS
    Then the host action reply is ok
    And the kernel was asked to clone source id 7 onto ports 9100 and 9101

  # Apply (setDesign) is publish + the registry write: it records the chosen design on the target's
  # registry entry AND deploys the whole app onto the target (replacing its document + resetting data).
  @milestone-10 @single-user
  Scenario: Apply records the design on the target and deploys it
    Given a designer instance holding a design with a type "Item" and a custom render
    And a target instance addressed by an id
    When the operator applies that design to the target's id over the WS
    Then the host action reply is ok
    And the kernel was asked to record that design on the target's id
    And the target app document describes the designed type "Item"
    And the target instance's data is reset

  # An invalid design is rejected before any registry write or document overwrite — Apply records
  # nothing and deploys nothing (the projection validates first).
  @milestone-10 @single-user
  Scenario: Applying an invalid design is rejected and changes nothing
    Given a designer instance holding a design whose root is an object type with no props
    And a target instance addressed by an id
    When the operator applies that design to the target's id over the WS
    Then the host action reply is an error mentioning "props"
    And the target app document is unchanged
