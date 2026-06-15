Feature: Host-side action — sys.publish (server-side schema export)
  The first host-side action primitive: a Code action `sys.publish(targetId)` runs the
  M4 schema export SERVER-SIDE, projecting the calling (designer) instance's data into a
  TARGET instance's app document and resetting the target's data. It is a server-only
  builtin (no client mutation), carried over a new server-authoritative `hostAction` WS op
  and a per-instance IHostActions seam that resolves the target id to its app+data paths.
  Driven at the WS-handler seam (like the schema bridge) — no browser.

  @milestone-10 @single-user
  Scenario: Publish runs the schema export server-side onto the target
    Given a designer instance with a designed type "Item" with a "label" prop
    And a target instance addressed by an id
    When the designer publishes to the target's id over the WS
    Then the host action reply is ok
    And the target app document describes the designed type "Item"
    And the target instance's data is reset

  @milestone-10 @single-user
  Scenario: Publishing an invalid design is rejected and writes nothing
    Given a designer instance whose design is an object type with no props
    And a target instance addressed by an id
    When the designer publishes to the target's id over the WS
    Then the host action reply is an error mentioning "props"
    And the target app document is unchanged

  @milestone-10 @single-user
  Scenario: Publishing to an unknown id is rejected and writes nothing
    Given a designer instance with a designed type "Item" with a "label" prop
    And a target instance addressed by an id
    When the designer publishes to an unknown id over the WS
    Then the host action reply is an error
    And the target app document is unchanged
