@milestone-13
Feature: Append-only changeset log behind the store (durable data history)
  Every hosted instance's store keeps a durable, append-only log of changesets beside its
  snapshot: one entry per store commit, recording each write's OLD and NEW value, the
  correlation msgId, who made it, when, and the seq (the same monotonic number the store's
  HEAD version already carries). The log rides the store's existing lock in the fixed crash
  order append-then-snapshot; boot replays the tail when a crash left the snapshot behind;
  and the log is self-checking: replaying genesis to head must reproduce the live snapshot.
  This is DECISIONS.md "App versioning — the full design (M13 clump)": variant-C storage
  (log + genesis + head + WAL + fsck), the never-derivable artifact the whole M13 clump sits
  on. Server-only, invisible to the app: no wire field, no client, no UI — it is proven only
  through the files it writes and the fsck invariant.

  Background:
    Given a fresh instance store over the Db-with-Note fixture

  Scenario: A field write appends one changeset entry recording its old and new value
    Given the store is seeded with a note "n" whose title is "First"
    When the title of note "n" is written to "Second"
    Then the log's last entry has seq equal to the store's current version
    And the log's last entry records a write of note "n" title from "First" to "Second"

  Scenario: A batch commit appends exactly one entry carrying all its writes
    Given the store is seeded with a note "n" whose title is "First" and count is 0
    When a single commit writes note "n" title to "Batched" and count to 9
    Then the log grew by exactly one entry
    And that entry's writes include title "First" to "Batched" and count 0 to 9
    And that entry's seq equals the store's current version

  Scenario: An empty commit appends no entry and does not advance the log
    Given the store is seeded with a note "n"
    When an empty commit is applied
    Then the log did not grow
    And the store's version is unchanged

  Scenario: A snapshot left behind a crash is repaired from the log on boot
    Given the store is seeded with a note "n" whose title is "First"
    And the title of note "n" is written to "Committed"
    When the snapshot on disk is rolled back to before that write while the log keeps it
    And a new store is opened over the same files
    Then the reopened store reads note "n" title as "Committed"
    And the snapshot on disk again matches the log head

  Scenario: Genesis is frozen at the first write and stays fixed as the log grows
    Given the store is seeded with a note "n" whose title is "First"
    When the title of note "n" is written to "Second"
    And the title of note "n" is written to "Third"
    Then genesis on disk still holds note "n" title as "First"
    And genesis records the seq the log began from

  Scenario: Replaying the log from genesis reproduces the current data exactly
    Given the store is seeded with several notes
    And a mix of edits, a create, a set link, a set removal, and a reference set are committed
    When the log is replayed from genesis to head
    Then the replayed data equals the live snapshot on disk

  Scenario: The log seq and the store version are the same monotonic number
    Given the store is seeded with a note "n"
    When three separate writes are committed
    Then each new log entry's seq is one greater than the previous
    And the final entry's seq equals the store's current version

  # The boot rebuild of the baseVersion guard's per-object map must restore a member's version advanced by
  # an interleaved FIELD write across a restart — so a stale SAME-FIELD commit after the restart is still
  # caught. Under M13 slice 6's field-level analysis, a same-field collision is rejected as a CONFLICT
  # (the {base,mine,theirs}-carrying ConflictException, a subclass of StaleBaseException) — the reject the
  # boot rebuild must preserve across a restart, or a missed clobber slips through. (The former set-link
  # variant of this scenario auto-merges now — see "A member changed only by a set link auto-merges"
  # below: a set-link membership change is DISJOINT from a field edit, so it never conflicts; the design's
  # commute rule. So the durable per-object attribution the rebuild proves is exercised via a field write,
  # the case that actually still rejects.)
  Scenario: A member whose version advanced by a field write stays conflict-guarded across a restart
    Given the store is seeded with a note "n"
    And the store version is remembered as a stale base
    When note "n" title is changed by a batch
    And a new store is opened over the same files
    Then a commit editing note "n" title at the remembered stale base is rejected as a conflict

  # The design's commute rule (§0 / §2), post-restart: a member whose only interleaved change was a SET
  # LINK (a membership change, DISJOINT from any field) does NOT conflict with a later field edit — it
  # AUTO-MERGES (applies). The former "stays stale-guarded" outcome is superseded by field-level analysis:
  # set add/remove commute, so a set-link never collides with a field write.
  Scenario: A member changed only by a set link auto-merges a later field edit across a restart
    Given the store is seeded with a note "n"
    And the store version is remembered as a stale base
    When note "n" is linked into its set by a batch
    And a new store is opened over the same files
    Then a commit editing note "n" title at the remembered stale base is accepted
