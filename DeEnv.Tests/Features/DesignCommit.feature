@milestone-13
Feature: Design commits — Commit/Branch rows, sys.commitDesign, the authority inversion
  Design history becomes ordinary data in the designer instance: `Commit` and `Branch` rows in root
  sets `db.commits`/`db.branches` (a set, not a hidden mechanism — so history renders for free as a
  generic SetTable, is reachability-GC'd, and is URL-linkable). `sys.commitDesign(design, message)`
  is a host action: it snapshots the SHARED working copy (Figma model — no per-user staging) at a log
  position marked BEFORE the commit's own writes, records it as an immutable Commit (message/at/
  design/parent/logSeq/text/idMap), and advances the design's `main` Branch head. Commit/Branch rows
  get NO write grants (a `create edit delete where false` access rule denies every client write),
  so `sys.commitDesign` — writing through the store seam directly, below the client write floor — is
  the only path that can ever create or move one. This is the M13 clump's authority inversion too:
  design-data + its commit history are now the source of truth; an app's `.deenv` FILE is a publish
  ARTIFACT (written BY publish), so the kernel's boot sync stops re-seeding an already-adopted design
  from its file, and stops truncating the designer's own log on every boot. See DECISIONS.md "App
  versioning — the full design (M13 clump)" + docs/plans/versioning-slices.md slice 3.

  Background:
    Given a designer instance holding a design with a type "Item" and a custom render

  Scenario: Committing a design creates an immutable snapshot commit
    When the designer commits that design with message "first cut" over the WS
    Then the host action reply is ok
    And db.commits holds a commit with message "first cut"
    And that commit has a timestamp
    And that commit's design reference is the committed design
    And that commit's parent is empty
    And that commit's logSeq equals the head version before the commit's own writes
    And that commit's text is the design's canonical printed document
    And that commit's idMap covers every type and prop in the design
    And the design's main branch head points at that commit

  Scenario: A second commit chains to the first
    Given the designer already committed that design with message "first cut"
    When the designer commits that design with message "second cut" over the WS
    Then the host action reply is ok
    And db.commits holds a commit with message "second cut"
    And that commit's parent is the "first cut" commit
    And the design's main branch head points at that commit
    And that commit's logSeq is strictly greater than the "first cut" commit's logSeq

  Scenario: A client cannot edit a commit or move a branch head
    Given the designer already committed that design with message "first cut"
    When a client-path write to the commit's message field is attempted
    Then the write is denied
    And the commit's message is unchanged
    When a client-path write to the branch's head field is attempted
    Then the write is denied
    And the branch's head is unchanged

  Scenario: Committing an invalid design fails cleanly
    Given a designer instance holding a design whose root is an object type with no props
    When the designer commits that design with message "broken" over the WS
    Then the host action reply is an error mentioning "props"
    And db.commits is empty
    And the design's main branch head is unset

  Scenario: sys.commitDesign is denied outside a design host or without the sys grant
    Given the designer's access grants sys to the admin role
    And a seeded admin operator and a seeded member operator
    And the current operator is the member
    When the operator commits design id 1 with message "nope" over the WS
    Then the host action reply is an error
    And the kernel was not asked to commit anything

  @multi-user
  Scenario: A committed design survives a kernel restart
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    And the todo design's "TodoItem" prop "text" is renamed to "title" through the designer store
    And the designer's own log line count is remembered before committing
    When the designer commits the todo design with message "rename text to title" over its own WS
    And the kernel restarts from its persisted registry
    Then the design-host's todo design's "TodoItem" has a prop named "title"
    And the design-host still holds the "rename text to title" commit
    And the designer's log was not truncated by the restart

  @multi-user
  Scenario: A new app file is adopted exactly once
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    When the kernel restarts from its persisted registry
    Then the design-host holds exactly one design labelled "todo"
    And the design-host still holds a design with id 13 labelled "todo"

  @multi-user
  Scenario: Instances keep mirroring the registry across boots
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    When the kernel restarts from its persisted registry
    Then the design-host's db.instances lists every hosted instance by name
