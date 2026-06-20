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
    And the target instance was restarted

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

  # ── non-destructive apply: data survives a schema change (the migration substrate) ──
  # Applying an EVOLVED schema PRESERVES the target's existing data — apply no longer wipes and
  # reseeds. Here the published design adds a field to a populated type; the stored row survives
  # and the new field reads its default. The storage layer already tolerates a declared-but-absent
  # prop (StoredData.feature), so the change is purely in the apply path: reconcile-and-keep
  # instead of delete-and-reset. Pulled ahead of M13 versioning per DECISIONS "Data must survive
  # schema changes".
  @milestone-13 @single-user @persistence
  Scenario: Apply preserves existing rows and defaults a newly added field
    Given a target instance holding an "Item" labelled "Keep me"
    And a designer instance holding a design that adds a "motto" field to "Item"
    When the designer publishes that design to the target's id over the WS
    Then the host action reply is ok
    And the target app document describes the designed type "Item"
    And the target still holds an "Item" labelled "Keep me", with "motto" defaulted to ""
    And the target instance was restarted

  # Removing a field from a populated type DROPS that stored value (not reseed): the row survives,
  # the orphaned value is pruned on apply. The startup guard stays strict — re-opening the preserved
  # data against the new (narrower) schema would REJECT a lingering undeclared field, so a clean open
  # proves the prune happened. (Slice 2 of the non-destructive-apply substrate.)
  @milestone-13 @single-user @persistence
  Scenario: Apply drops a removed field and preserves the rest of the row
    Given a target instance holding an "Item" with label "Keep me" and note "scratch"
    And a designer instance holding a design with a type "Item" and a custom render
    When the designer publishes that design to the target's id over the WS
    Then the host action reply is ok
    And the target still holds an "Item" labelled "Keep me"
    And the target instance was restarted

  # A field's TYPE change CONVERTS its stored values where possible (slice 3): int → text keeps the
  # value ("3"). An unconvertible value (text "abc" → int) resets that ONE field to its default and is
  # reported (server log) — never silent corruption; the rest of the data survives either way.
  @milestone-13 @single-user @persistence
  Scenario: Apply converts a scalar field's value when its type changes
    Given a target instance whose "Item" has "qty" of type "int" set to "3"
    And a designer instance holding a design with "Item" field "qty" typed "text"
    When the designer publishes that design to the target's id over the WS
    Then the host action reply is ok
    And the target's "Item" reads "qty" as "text" "3"
    And the target instance was restarted

  @milestone-13 @single-user @persistence
  Scenario: Apply defaults an unconvertible value on a type change
    Given a target instance whose "Item" has "code" of type "text" set to "abc"
    And a designer instance holding a design with "Item" field "code" typed "int"
    When the designer publishes that design to the target's id over the WS
    Then the host action reply is ok
    And the target's "Item" reads "code" as "int" "0"

  # An UNSET optional decimal/date/datetime is stored as the empty-text leaf (the canonical "unset"
  # form the validator accepts). It is NOT a value needing conversion — a republish/additive apply must
  # leave it empty, not clobber it to 0/today and falsely report it as unconvertible.
  @milestone-13 @single-user @persistence
  Scenario: Apply leaves an unset optional decimal untouched
    Given a target instance whose "Item" has an unset optional decimal "price"
    And a designer instance holding a design with "Item" field "price" typed "decimal"
    When the designer publishes that design to the target's id over the WS
    Then the host action reply is ok
    And the target's "Item" reads "price" as "decimal" ""

  @milestone-10 @single-user
  Scenario: Create projects the passed design into a new named instance on the given ports
    Given a designer instance holding a design with a type "Item" and a custom render
    When the designer creates an instance named "myapp" from that design on ports 9100 and 9101 over the WS
    Then the host action reply is ok
    And a new instance was created on ports 9100 and 9101
    And the created instance has the name "myapp"
    And the created app document describes the designed type "Item"
    And the created app document contains the custom render

  @milestone-10 @single-user
  Scenario: Creating from an invalid design is rejected and spawns nothing
    Given a designer instance holding a design whose root is an object type with no props
    When the designer creates an instance named "myapp" from that design on ports 9100 and 9101 over the WS
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
    And the target instance was restarted

  # An invalid design is rejected before any registry write or document overwrite — Apply records
  # nothing and deploys nothing (the projection validates first).
  @milestone-10 @single-user
  Scenario: Applying an invalid design is rejected and changes nothing
    Given a designer instance holding a design whose root is an object type with no props
    And a target instance addressed by an id
    When the operator applies that design to the target's id over the WS
    Then the host action reply is an error mentioning "props"
    And the target app document is unchanged

  # rename(id, name): update an instance's display label in the registry.
  @milestone-10 @single-user
  Scenario: Rename updates an instance's display label
    When the operator renames instance id 7 to "renamed" over the WS
    Then the host action reply is ok
    And the kernel was asked to rename instance id 7 to "renamed"
