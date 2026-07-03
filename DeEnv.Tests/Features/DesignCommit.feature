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

  # Review fix 3: the whole commit — Commit row + design/parent refs + db.commits link + every idMap
  # dict entry + the branch-head advance — is ONE atomic CommitBatch, so the designer's append-only
  # data log (slice 1) grows by EXACTLY ONE entry, and fsck/replay still holds (genesis→head == snapshot).
  Scenario: A design commit is a single atomic changeset in the data log
    Given the designer's own log line count is remembered before committing over the WS
    When the designer commits that design with message "atomic cut" over the WS
    Then the host action reply is ok
    And the designer's log grew by exactly one entry
    And the designer's log replays from genesis to the live snapshot

  Scenario: A client cannot edit a commit or move a branch head
    Given the designer already committed that design with message "first cut"
    When a client-path write to the commit's message field is attempted
    Then the write is denied
    And the commit's message is unchanged
    When a client-path write to the branch's head field is attempted
    Then the write is denied
    And the branch's head is unchanged
    # Review fix 3: the dict-write floor. A client addEntry / path-write into the immutable Commit's
    # idMap dictionary is ALSO denied (the deferred dict-write gap let this through before the fix).
    When a client addEntry into the commit's idMap is attempted
    Then the write is denied
    And the commit's idMap is unchanged
    When a client-path write into the commit's idMap is attempted
    Then the write is denied
    And the commit's idMap is unchanged

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

  # Review fix 2: adopting a genuinely-new app file into an EXISTING designer store whose mint counter
  # has advanced past the file's kernel.json designId. AdoptInto mints a DIFFERENT id (it cannot pin one
  # on a live store), so SyncDesignHost rewrites the registry AND remaps the Instance.design reference to
  # the minted id — otherwise the instance's design would dangle at the stale kernel.json id.
  @multi-user
  Scenario: Adopting a new app whose designId is below the mint counter remaps the instance reference
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    And a new app instance is registered with a designId below the mint counter
    When the kernel restarts from its persisted registry
    Then the design-host adopted the new app's design at a minted id, not its stale designId
    And the new app instance's stored design reference resolves to the adopted design

  @multi-user
  Scenario: Instances keep mirroring the registry across boots
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    When the kernel restarts from its persisted registry
    Then the design-host's db.instances lists every hosted instance by name
