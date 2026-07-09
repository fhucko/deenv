Feature: Host-side actions — sys.create / sys.publish / sys.clone / sys.delete / sys.setDesign (server-side instance ops)
  The host-side action primitives: Code hands a DESIGN (or instance ids) to a server-side action that
  operates on the kernel's instances. A `Design` is a WHOLE app — structured `types` plus the other
  app-document sections (`initialData`/`common`/`ui`) carried as source text — and the designer holds a
  set of them (`db.designs`). `sys.publish(design, targetId)` projects the WHOLE design onto an EXISTING
  instance (replacing its document + resetting its data — keeping its custom UI / seed / shared
  functions, not just its types); `sys.create(design, name)` projects the same design into a NEW
  instance with the given display NAME (served at /apps/<name> — addressing is by path, no ports);
  `sys.cloneInstance(sourceId)` copies a source instance's app doc AND data into a NEW instance (with a
  mount name derived from the source); `sys.delete(targetId)` removes an existing instance;
  `sys.setDesign(design, targetId)` is the IDE's "Apply" — it RECORDS (on the target's
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
    Then the host action reply is an error mentioning "fields"
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
  Scenario: Apply rebaselines history when it removes schema-shaped data
    Given a target instance holding an "Item" with label "Keep me" and note "scratch"
    And the target has log and genesis files
    And a designer instance holding a design with a type "Item" and a custom render
    When the operator applies that design to the target's id over the WS
    Then the host action reply is ok
    And the target still holds an "Item" labelled "Keep me"
    And the target has no log or genesis files

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

  # An unconvertible value retyped to decimal/date/datetime must reset to the canonical UNSET form
  # (the empty-text leaf) — never a fabricated 0/today/now, which would make an unconvertible cell
  # masquerade as a genuine (if wrong) value instead of visibly empty.
  @milestone-13 @single-user @persistence
  Scenario: Apply defaults an unconvertible value on a type change to decimal to unset, not zero
    Given a target instance whose "Item" has "price" of type "text" set to "not-a-number"
    And a designer instance holding a design with "Item" field "price" typed "decimal"
    When the designer publishes that design to the target's id over the WS
    Then the host action reply is ok
    And the target's "Item" reads "price" as "decimal" ""

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

  # A newly added decimal/date/datetime field has no typed "empty" value, so an existing row that
  # predates it must read the CANONICAL unset form (the empty-text leaf) — the same value a UI-cleared
  # field stores — never a fabricated 0/today/now. An old row must not look freshly created.
  @milestone-13 @single-user @persistence
  Scenario: Apply defaults a newly added date field to unset, not today
    Given a target instance holding an "Item" labelled "Keep me"
    And a designer instance holding a design that adds a "due" field of type "date" to "Item"
    When the designer publishes that design to the target's id over the WS
    Then the host action reply is ok
    And the target still holds an "Item" labelled "Keep me", with "due" defaulted to ""
    And the target instance was restarted

  # A field's CARDINALITY change reshapes the stored value (same-name — no identity needed): a single
  # object reference becomes a one-member set (one -> many, lossless). The stored ref is wrapped into a
  # fresh set rather than reseeded. (Rename, a NAME change, needs M13's identity diff and is deferred there.)
  @milestone-13 @single-user @persistence
  Scenario: Apply reshapes a single object reference into a set
    Given a target instance whose Db has a single "lead" referencing a "Person" named "Ada"
    And a designer instance holding a design with Db's "lead" as a set of "Person"
    When the designer publishes that design to the target's id over the WS
    Then the host action reply is ok
    And the target's Db "lead" set holds the "Person" named "Ada"
    And the target instance was restarted

  @milestone-10 @single-user
  Scenario: Create projects the passed design into a new named instance
    Given a designer instance holding a design with a type "Item" and a custom render
    When the designer creates an instance named "myapp" from that design over the WS
    Then the host action reply is ok
    And a new instance was created named "myapp"
    And the created instance has the name "myapp"
    And the created app document describes the designed type "Item"
    And the created app document contains the custom render

  @milestone-10 @single-user
  Scenario: Creating from an invalid design is rejected and spawns nothing
    Given a designer instance holding a design whose root is an object type with no props
    When the designer creates an instance named "myapp" from that design over the WS
    Then the host action reply is an error mentioning "fields"
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
  Scenario: Clone asks the kernel to copy the source instance
    When the operator clones instance id 7 over the WS
    Then the host action reply is ok
    And the kernel was asked to clone source id 7

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
    Then the host action reply is an error mentioning "fields"
    And the target app document is unchanged

  # rename(id, name): update an instance's display label in the registry.
  @milestone-10 @single-user
  Scenario: Rename updates an instance's display label
    When the operator renames instance id 7 to "renamed" over the WS
    Then the host action reply is ok
    And the kernel was asked to rename instance id 7 to "renamed"

  # ── sys.importRender: convert a design's text render to structured rows (M12 X2a) ──
  # importRender(design) is a SERVER-ONLY host action: on the DESIGN HOST, as an ADMIN, it converts a
  # design whose `ui` is a plain `fn render()` into structured MetaNode rows (Design.render) and clears
  # `ui`, atomically (SchemaBridge.ImportRender). Driven end-to-end through the WS hostAction path — the
  # wiring + the `sys` access floor — no browser.
  @m12 @single-user
  Scenario: Import converts a design's text render into structured rows
    Given a designer instance holding a design with a type "Item" and a custom render
    When the designer imports that design's render over the WS
    Then the host action reply is ok
    And the design's `ui` text is cleared
    And the design's `render` set now holds the imported tree

  # The security guarantee: importRender is a `sys` host action, so a NON-ADMIN session is rejected and
  # the design is NOT converted (the render stays as `ui` text). Mirrors the delete-authorization teeth —
  # the reject proves the `sys` floor blocked the action BEFORE it reached the seam.
  @m12 @single-user
  Scenario: A non-admin session cannot import a design's render
    Given a designer instance holding a design with a type "Item" and a custom render
    And a seeded admin operator and a seeded member operator
    And the current operator is the member
    When the operator imports that design's render over the WS
    Then the host action reply is an error
    And the design's render was not imported

  @m12 @single-user
  Scenario: An anonymous session cannot import a design's render
    Given a designer instance holding a design with a type "Item" and a custom render
    And the operator session is anonymous
    When the operator imports that design's render over the WS
    Then the host action reply is an error
    And the design's render was not imported

  # ── host-action AUTHORIZATION (the access section's `sys` subject) ────────────────
  # Host actions run with KERNEL authority, so authority is NOT "the seam was wired" — it is the app's
  # own access rule. The access section gains a `sys` subject; a host action is accepted ONLY when the
  # session's principal satisfies that rule. This is DENY-BY-DEFAULT and UNCONDITIONAL: no access
  # section, no `sys` rule, a false/erroring condition, or no logged-in principal ALL reject — even
  # though the app's DATA rules default open, kernel authority never does. The rule is evaluated with
  # the SAME kernel-floor condition evaluation the type rules use ({ currentUser }). Driven at the
  # WsHandler seam (the enforcement point), a real KernelHostActions wired in — so a reject proves the
  # rule blocked the action BEFORE the seam, not that the seam was absent.
  #
  # The four scenarios below all carry the same real host action (delete id 7); what varies is the
  # ruleset + the principal. The reject scenarios assert the delete never reached the kernel.

  @milestone-auth @single-user
  Scenario: An admin session may run a host action against the designer
    Given the designer's access grants sys to the admin role
    And a seeded admin operator
    And the current operator is the admin
    When the operator deletes instance id 7 over the WS
    Then the host action reply is ok
    And the kernel was asked to delete instance id 7

  @milestone-auth @single-user
  Scenario: A non-admin session cannot run a host action
    Given the designer's access grants sys to the admin role
    And a seeded admin operator and a seeded member operator
    And the current operator is the member
    When the operator deletes instance id 7 over the WS
    Then the host action reply is an error
    And the kernel was not asked to delete anything

  @milestone-auth @single-user
  Scenario: An anonymous session cannot run a host action
    Given the designer's access grants sys to the admin role
    And a seeded admin operator
    And the operator session is anonymous
    When the operator deletes instance id 7 over the WS
    Then the host action reply is an error
    And the kernel was not asked to delete anything

  # The shape-authority hole, closed: an instance whose schema HAS the designer shape (Db { designs set
  # of Design }) AND whose Code calls a host action, but that declares NO `sys` access rule, must reject
  # host actions for EVERYONE — schema shape is not authorization. (Under the old shape gate this
  # instance would have had full host-action authority.)
  @milestone-auth @single-user
  Scenario: A designer-shaped app with no sys rule rejects host actions for everyone
    Given the designer meta-schema declares no access section
    And the current operator is the admin
    When the operator deletes instance id 7 over the WS
    Then the host action reply is an error
    And the kernel was not asked to delete anything

  # An ordinary app (devlog-shaped: its own data rules, NO `sys` rule) rejects host actions even for its
  # own admin — kernel authority is not granted by data rules. (In production such an app also never
  # gets a real KernelHostActions, since its Code calls no host-action builtin; here the real seam is
  # wired to prove the access rule ALSO rejects, the second line of defence.)
  @milestone-auth @single-user
  Scenario: An ordinary app with data rules but no sys rule rejects host actions
    Given an ordinary app whose access rules gate its data but declare no sys subject
    And a seeded admin operator
    And the current operator is the admin
    When the operator deletes instance id 7 over the WS
    Then the host action reply is an error
    And the kernel was not asked to delete anything
