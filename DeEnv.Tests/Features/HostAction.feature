Feature: Host-side actions — sys.create / sys.publish / sys.clone / sys.delete (server-side instance ops)
  The host-side action primitives: Code hands a SCHEMA OBJECT (or instance ids) to a server-side
  action that operates on the kernel's instances. `sys.publish(schema, targetId)` projects the
  schema onto an EXISTING instance (replacing its document + resetting its data); `sys.create(schema,
  appPort, infraPort)` projects the same schema into a NEW instance on the given ports;
  `sys.cloneInstance(sourceId, appPort, infraPort)` copies a source instance's app doc AND data into
  a NEW instance on the given ports; `sys.delete(targetId)` removes an existing instance. A schema
  crosses the wire as the object's id (the designer passes `db`, the root object); instance ids are
  bare ints. All are server-only builtins (no client mutation), carried over the `hostAction` WS op
  and the per-instance IHostActions seam. Driven at the WS-handler seam (like the schema bridge) —
  no browser.

  @milestone-10 @single-user
  Scenario: Publish projects the passed schema onto the target
    Given a designer instance with a designed type "Item" with a "label" prop
    And a target instance addressed by an id
    When the designer publishes the schema to the target's id over the WS
    Then the host action reply is ok
    And the target app document describes the designed type "Item"
    And the target instance's data is reset

  @milestone-10 @single-user
  Scenario: Publishing an invalid design is rejected and writes nothing
    Given a designer instance whose design is an object type with no props
    And a target instance addressed by an id
    When the designer publishes the schema to the target's id over the WS
    Then the host action reply is an error mentioning "props"
    And the target app document is unchanged

  @milestone-10 @single-user
  Scenario: Publishing to an unknown id is rejected and writes nothing
    Given a designer instance with a designed type "Item" with a "label" prop
    And a target instance addressed by an id
    When the designer publishes the schema to an unknown id over the WS
    Then the host action reply is an error
    And the target app document is unchanged

  @milestone-10 @single-user
  Scenario: Create projects the passed schema into a new instance on the given ports
    Given a designer instance with a designed type "Item" with a "label" prop
    When the designer creates an instance from the schema on ports 9100 and 9101 over the WS
    Then the host action reply is ok
    And a new instance was created on ports 9100 and 9101
    And the created app document describes the designed type "Item"

  @milestone-10 @single-user
  Scenario: Creating from an invalid design is rejected and spawns nothing
    Given a designer instance whose design is an object type with no props
    When the designer creates an instance from the schema on ports 9100 and 9101 over the WS
    Then the host action reply is an error mentioning "props"
    And no instance was created

  @milestone-10 @single-user
  Scenario: A non-root schema object is rejected and spawns nothing
    Given a designer instance with a designed type "Item" with a "label" prop
    When the designer creates an instance from a non-root schema object over the WS
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
