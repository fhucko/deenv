@milestone-13
Feature: Structural identity-diff + rename-safe forward publish
  Publish now diffs the target's STAMPED commit against the design's HEAD commit by endpoint IDENTITY
  (the M13 slice-2 idMap caches), not by name: a rename reads as a rename, so a renamed prop/type carries
  its data through a deploy instead of reseeding it under the new name (the pre-slice behaviour). The
  target's log gets exactly ONE new entry — a boundary-marked, materialized changeset — so its history is
  PRESERVED across the publish (not truncated/re-baselined, unlike the unversioned migrate path). See
  DECISIONS.md "App versioning — the full design (M13 clump)" §3-4 and docs/plans/versioning-slices.md
  slice 4.

  Background:
    Given a versioned designer instance holding a design with a type "Item" and a custom render
    And a target instance addressed by an id, stamped to the design's baseline commit

  # ── the headline: a rename carries data ──────────────────────────────────────────────────────────
  Scenario: Renaming a prop in the designer carries the target's data through a publish
    Given the target holds an "Item" labelled "Keep me"
    And the design's "Item" prop "label" is renamed to "title"
    And the design is committed with message "rename label to title"
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the target's published "Item" reads "title" as "text" "Keep me"
    And the target's "Item" has no stored "label" value
    # proves identity, not coincidence: an unrelated fresh reseed would show the DEFAULT, not the kept value
    And the kept value is not the schema's default for "title"

  # ── the design host is never its own publish target (single-writer guard) ────────────────────────
  Scenario: Publishing onto the design host itself is rejected
    Given the design's "Item" prop "label" is renamed to "title"
    And the design is committed with message "self-publish guard"
    When the designer attempts to publish the design onto the design host itself over the WS
    Then the publish reply is an error saying the design host cannot be its own publish target

  Scenario: Renaming a type carries its extent and every reference to it
    Given the target holds an "Item" labelled "Keep me"
    And the design's type "Item" is renamed to "Product"
    And the design is committed with message "rename Item to Product"
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the target's "Product" extent holds an object labelled "Keep me"
    And every reference to the renamed type in the target now points at "Product"

  # ── the boundary entry: exactly one, history preserved, replay reproduces head ───────────────────
  Scenario: Publish appends exactly one boundary-marked entry and replay reproduces the post-publish head
    Given the target holds an "Item" labelled "Keep me"
    And the target's own log line count is remembered
    And the design's "Item" prop "label" is renamed to "title"
    And the design is committed with message "rename for boundary proof"
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the target's log grew by exactly one entry
    And the target's newest log entry carries a boundary marker for that commit
    And the target's genesis is unchanged by the publish
    And the target's log fsck holds
    And replaying the target's log from genesis to head reproduces the post-publish snapshot

  # ── WAL law: a crash between the boundary entry append and the snapshot rewrite recovers ─────────
  # The boundary apply must append the log entry BEFORE rewriting the snapshot (the slice-1 WAL law,
  # same as the live Save). Proven BOTH DIRECTIONS: (a) the CORRECT-order crash — the entry is on the log
  # but the snapshot died at its pre-publish version — a fresh store REPLAYS the entry forward and serves
  # the post-publish (renamed) state; (b) the INVERSE (what the pre-fix append-after-snapshot order would
  # leave) — snapshot AHEAD of the log head — is REJECTED loudly by boot reconciliation, never silently
  # trusted. So a mid-publish crash can only ever recover-forward or fail loud, never brick or lie.
  Scenario: A crash between the boundary entry and the snapshot recovers the post-publish state
    Given the target holds an "Item" labelled "Keep me"
    And the design's "Item" prop "label" is renamed to "title"
    And the design is committed with message "rename before crash"
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    When the target's snapshot is rolled back to before the publish while the log keeps the boundary entry
    And a fresh store is opened over the target's files
    Then the reopened target reads its "Item" "title" as "Keep me"
    And the reopened target's log fsck holds
    When the target's log has its boundary entry removed while the snapshot stays at the post-publish version
    Then opening a store over the target's files is rejected as snapshot-ahead-of-log

  # ── unsupported cardinality reshape: drop-and-report, never brick the remount ────────────────────
  # A reshape this slice cannot carry (set -> single) must NOT leave the old-shaped value in place — the
  # new schema declares the new shape, so the remount's startup guard would 503 while the report claimed
  # success. Decided contract: drop the old value to the new shape's default so the store ALWAYS loads,
  # and flag the drop LOUDLY (unsupported + dropped). The dropped value survives in the boundary entry's
  # old-value log write (recoverable). Built on the SAME design identity as the Background (add a "leads"
  # set to Db, publish, seed a member, THEN reshape set -> single) so the reshape is a genuine same-id op.
  Scenario: An unsupported cardinality reshape drops the old value, still loads, and is flagged
    Given the design's Db gains a "leads" set of "Person"
    And the design is committed with message "add leads set"
    And the designer publishes the design's head commit to the target's id over the WS
    And the target's Db "leads" set is seeded with a "Person" named "Ada"
    And the design's Db "leads" prop is reshaped to a single reference
    And the design is committed with message "leads as a single"
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the publish report flags the "leads" reshape as unsupported and dropped
    And a fresh store opens over the target's files without error
    And the target's Db "leads" reads as an unset reference

  Scenario: An unsupported reshape over an empty field applies cleanly with nothing to drop
    Given the design's Db gains a "leads" set of "Person"
    And the design is committed with message "add empty leads set"
    And the designer publishes the design's head commit to the target's id over the WS
    And the design's Db "leads" prop is reshaped to a single reference
    And the design is committed with message "empty leads as a single"
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And a fresh store opens over the target's files without error
    And the target's Db "leads" reads as an unset reference

  # ── parity with the existing non-destructive apply (adds / removes / conversions / reshape) ──────
  # A same-id, NON-renamed add/remove is ALSO an identity-diffed op (Renames aside, the diff reports it in
  # Adds/Removes exactly the same way) — so this proves the versioned path still carries what the earlier
  # non-destructive-apply slices already proved, not just renames. The design first COMMITS a "note" field
  # (matching a value the target already holds, seeded directly under that intermediate schema), THEN
  # drops "note" and adds "motto" in a second commit — the removal/addition the publish under test carries.
  Scenario: A published add and a removed field both still carry/default data through the versioned path
    Given the target holds an "Item" labelled "Keep me"
    And the design adds a "note" field to "Item"
    And the design is committed with message "add note"
    And the designer publishes the design's head commit to the target's id over the WS
    And the target's "Item" has "note" set to "scratch"
    And the design adds a "motto" field to "Item"
    And the design's "Item" field "note" is removed
    And the design is committed with message "drop note, add motto"
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the target's published "Item" reads "motto" defaulted to ""
    And the target's "Item" has no stored "note" value
    And the target's "Item" still reads "label" as "text" "Keep me"

  # ── destructive ops are flagged loudly, but still applied ────────────────────────────────────────
  # Mirrors the add/remove parity scenario's shape: the design first COMMITS a "qty" field typed text
  # (matching a genuinely unconvertible value seeded directly under that schema), THEN retypes it to int
  # AND removes "label" in a second commit — the same-id conversion/removal the publish under test carries.
  Scenario: A removed prop and an unconvertible cell are flagged as destructive in the report
    Given the target holds an "Item" labelled "Keep me"
    And the design adds a "qty" field to "Item"
    And the design is committed with message "add qty as text"
    And the designer publishes the design's head commit to the target's id over the WS
    And the target's "Item" has "qty" set to "abc-not-a-number"
    And the design's "Item" field "qty" is retyped to "int"
    And the design's "Item" field "label" is removed
    And the design is committed with message "destructive changes"
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the publish report flags "label" as a removed field
    And the publish report flags the "qty" cell as unconvertible
    And the target's published "Item" reads "qty" as "int" "0"

  # ── dry-run changes nothing ───────────────────────────────────────────────────────────────────────
  Scenario: Dry-run reports the same plan and changes nothing
    Given the target holds an "Item" labelled "Keep me"
    And the target's own log line count is remembered
    And the design's "Item" prop "label" is renamed to "title"
    And the design is committed with message "dry run rename"
    When the designer dry-runs a publish of the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the dry-run reply reports a rename from "label" to "title"
    And the target app document was never republished
    And the target's log did not grow
    And the target's "Item" still reads "label" as "text" "Keep me"
    And the target was not stamped by the dry run
    And the target instance was not restarted

  # ── uncommitted drift is reported, and never itself part of what gets published ──────────────────
  # Publish always deploys the design's COMMITTED head, never its live working copy — so an uncommitted
  # edit is, by construction, never published. This proves the operator is TOLD about it (a report field),
  # not left to assume the publish silently included their unsaved change: here the design was committed
  # once (carrying an already-published rename), then edited again WITHOUT a second commit — publishing
  # deploys the FIRST commit's content (a no-op relative to the target, already published) while flagging
  # that the live working copy has drifted past it.
  Scenario: Uncommitted working-copy drift is reported alongside a normal publish of the committed head
    Given the target holds an "Item" labelled "Keep me"
    And the design's "Item" prop "label" is renamed to "title"
    And the design is committed with message "carries the rename"
    And the designer publishes the design's head commit to the target's id over the WS
    And the design's "Item" prop "title" is renamed to "heading" but left uncommitted
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the publish report flags uncommitted drift
    And the publish report shows no renames
    And the target's "Item" still reads "title" as "text" "Keep me"

  # ── unstamped (pre-versioning) instance: name-match fallback once, then stamped ───────────────────
  # First publish is a NAME MATCH (the design's field is still called "label", matching the target's
  # already-deployed field of the same name) — the fallback's by-name apply is lossless HERE because the
  # names agree; it stamps the target to that commit. THEN the design renames the field — the SECOND
  # publish is now identity-diffed against the fresh stamp, so it must carry "Keep me" through the rename
  # with no loss (the fallback itself is never rename-safe by construction — it is the ONE-TIME bridge
  # into the identity-diffed steady state, proven correct precisely because it never has to survive one).
  Scenario: An unstamped instance publishes via name-match fallback once, then is rename-safe on the next publish
    Given a target instance addressed by an id, holding an "Item" labelled "Keep me", never stamped
    And the design is committed with message "first publish after fallback"
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the publish report used the name-match fallback
    And the target was stamped to the design's head commit
    And the target's published "Item" reads "label" as "text" "Keep me"
    Given the design's "Item" prop "label" is renamed to "title"
    And the design is committed with message "second publish is rename-safe"
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the publish report did not use the name-match fallback
    And the target's published "Item" reads "title" as "text" "Keep me"

  # ── adoption mints a baseline commit; a matching instance is stamped to it ────────────────────────
  # Adoption reads a hosted instance's OWN app.deenv to build the Design (M13 slice 3); once adopted, that
  # SAME instance's app document canonically equals the design's freshly-minted baseline commit text — so
  # it is the natural, minimal "matching instance" the stamping half targets, with no extra fixture needed.
  Scenario: Adoption mints a baseline commit and stamps the instance it was adopted from
    Given a kernel booted from the committed designer and the committed todo app
    Then the todo design was adopted with a baseline commit whose parent is empty
    And the todo instance's registry entry is stamped to that baseline commit

  # ── a draft staged before a publish is rejected afterward ────────────────────────────────────────
  Scenario: A draft staged before a publish is rejected afterward with a clear reload message
    Given the target holds an "Item" labelled "Keep me"
    And a client loaded the target's "Item" and staged an edit at that base version
    And the design's "Item" prop "label" is renamed to "title"
    And the design is committed with message "publish while a draft is open"
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    When the stale client commits its staged edit to the target
    Then the target instance rejects the stale commit with a message mentioning "reload"
    And the target's published "Item" reads "title" as "text" "Keep me"

  Scenario: A migration function computes new data while a rename carries old data
    Given the target holds an "Item" labelled "Keep me"
    And the design adds an int field "net" to "Item"
    And the design adds an int field "tax" to "Item"
    And the design is committed with message "add invoice parts"
    And the designer publishes the design's head commit to the target's id over the WS
    And the target's "Item" has int field "net" set to 7
    And the target's "Item" has int field "tax" set to 3
    And the design's "Item" prop "label" is renamed to "title"
    And the design adds an int field "total" to "Item"
    And the design's "Item" field "net" is removed
    And the design's "Item" field "tax" is removed
    And the design is committed with message "compute total" and migration:
      """
      fn Item(old)
          new.total = old.net + old.tax
      """
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the target's published "Item" reads "title" as "text" "Keep me"
    And the target's published "Item" reads "total" as "int" "10"
    And the publish report includes a migration for "Item" over 1 object

  Scenario: A throwing migration aborts atomically
    Given the target holds an "Item" labelled "Keep me"
    And the target's own log line count is remembered
    And the design adds an int field "total" to "Item"
    And the design is committed with message "bad migration" and migration:
      """
      fn Item(old)
          new.total = missing.value
      """
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish reply is an error mentioning "migration"
    And the target app document was never republished
    And the target's log did not grow
    And the target was not stamped to the failed head commit

  Scenario: A wrong-typed migration write aborts loudly
    Given the target holds an "Item" labelled "Keep me"
    And the design adds an int field "total" to "Item"
    And the design is committed with message "wrong type" and migration:
      """
      fn Item(old)
          new.total = "ten"
      """
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish reply is an error mentioning "Migration wrote Text to Item.total, expected int"

  Scenario: Dry-run executes migrations without applying them
    Given the target holds an "Item" labelled "Keep me"
    And the target's own log line count is remembered
    And the design adds an int field "total" to "Item"
    And the design is committed with message "preview migration" and migration:
      """
      fn Item(old)
          new.total = 12
      """
    When the designer dry-runs a publish of the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the publish report includes a migration for "Item" over 1 object
    And the target app document was never republished
    And the target's log did not grow
    And the target was not stamped by the dry run

  Scenario: A merged migration is refused before publish work
    Given the target holds an "Item" labelled "Keep me"
    And the design has a merged side-branch commit with migration:
      """
      fn Item(old)
          new.label = "merged"
      """
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish reply is an error mentioning "merged migration"

  Scenario: A migration function writing a dictionary prop is refused
    Given the target holds an "Item" labelled "Keep me"
    And the design adds a text dictionary "notes" to "Item"
    And the design is committed with message "dict write" and migration:
      """
      fn Item(old)
          new.notes = "nope"
      """
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish reply is an error mentioning "dictionary migration not supported yet"

  Scenario: Whitespace migration text is treated as no migration
    Given the target holds an "Item" labelled "Keep me"
    And the design adds an int field "total" to "Item"
    And the design is committed with message "blank migration" and whitespace migration
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the publish report includes 0 migration steps
    And the target's published "Item" reads "total" as "int" "0"

  Scenario: Re-publish after a boundary-entry crash re-stamps without re-running migrations
    Given the target holds an "Item" labelled "Keep me"
    And the design adds an int field "total" to "Item"
    And the design is committed with message "crash guard" and migration:
      """
      fn Item(old)
          new.total = old.missing
      """
    And the target already has the publish boundary entry for the design's head commit but no registry stamp
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the target was stamped to the design's head commit

  Scenario: Multi-commit migration ranges collapse structural spans around migration steps
    Given the target holds an "Item" labelled "Keep me"
    And the target's own log line count is remembered
    And the design adds an int field "net" to "Item"
    And the design is committed with message "structural before first migration"
    And the design adds an int field "total" to "Item"
    And the design is committed with message "first migration" and migration:
      """
      fn Item(old)
          new.total = 1
      """
    And the design adds an int field "checksum" to "Item"
    And the design is committed with message "second migration" and migration:
      """
      fn Item(old)
          new.checksum = old.total + 1
      """
    When the designer publishes the design's head commit to the target's id over the WS
    Then the publish host action reply is ok
    And the target's log grew by exactly one entry
    And the target's published "Item" reads "total" as "int" "1"
    And the target's published "Item" reads "checksum" as "int" "2"
    And the publish report includes 2 migration steps
