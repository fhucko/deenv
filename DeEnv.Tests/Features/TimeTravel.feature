@milestone-13
Feature: Time-travel clones — cloneInstance(id, atSeq)
  `sys.cloneInstance(sourceId, atSeq)` widens the existing clone host action with an OPTIONAL second
  argument (M13 slice 7 — the milestone's last core slice, DECISIONS.md "App versioning — the full
  design (M13 clump)" §0/§6): "the app as of Tuesday," a fresh fork, in one op. Omitted, it is
  byte-identical to today's clone (a plain file copy of the source's current head). Given, the clone's
  data is MATERIALIZED at that log seq (folding the source's own genesis→seq via the SAME AppLogReplay
  the fsck/boot-replay machinery already shares — never a second apply implementation) and runs the
  SCHEMA that was in force at that moment: the latest publish boundary marker at or before atSeq (M13
  slice 4's `LogEntry.Boundary`), resolved to that design commit's cached text — or, absent any boundary
  at or before atSeq, the source's CURRENT app document (covers pre-first-publish history and a
  never-published source alike). The clone is an ordinary fork: a fresh instance id, its own registry
  entry, and its OWN history starting genesis-less (no log/genesis copied from the source) — its first
  real mutation freezes its own genesis from the materialized state, exactly like any brand-new
  instance. The capability gate is the EXISTING `sys` host-action rule (cloneInstance is already
  sys-gated) — no new floor machinery for this slice; per-field floor-over-history rides the future
  history-browsing UI.

  Background:
    Given a versioned designer instance and a target instance, both hosted by a real kernel

  # ── the headline: materializing at a mid-seq seq serves that moment's values, source untouched ──
  Scenario: Cloning an instance at an earlier seq yields the app as of that moment
    Given the target's "Item" title is written to "First"
    And the target's "Item" title is written to "Second"
    And the current seq is remembered as "mid"
    And the target's "Item" title is written to "Third"
    When the operator clones the target at the remembered "mid" seq
    Then the clone's "Item" title reads "Second"
    And the source still reads "Item" title as "Third"

  # ── the sharpest scenario: era-schema resolution on BOTH sides of a publish boundary ─────────────
  # A first "baseline" publish (unrenamed) STAMPS the target — a target's very FIRST publish always takes
  # the pre-slice-4 name-match fallback and writes NO boundary marker at all (see Publish.feature); only a
  # SECOND, STAMPED publish is identity-diffed and writes the real boundary entry this scenario needs. So
  # the rename's publish (the one under test) is the target's second publish, landing a genuine boundary.
  Scenario: A clone at a pre-publish seq runs the era schema
    Given the target holds an "Item" titled "Keep me"
    And the time-travel design is committed with message "baseline"
    And the time-travel designer publishes the design's head commit to the target over the WS
    And the current seq is remembered as "before"
    And the design's "Item" prop "title" is renamed to "heading" for time travel
    And the time-travel design is committed with message "rename title to heading"
    And the time-travel designer publishes the design's head commit to the target over the WS
    And the current seq is remembered as "after"
    When the operator clones the target at the remembered "before" seq
    Then that clone's app document declares "Item" prop "title"
    And that clone's app document does not declare "Item" prop "heading"
    And that clone's "Item" reads "title" as "Keep me"
    When the operator clones the target at the remembered "after" seq
    Then that clone's app document declares "Item" prop "heading"
    And that clone's app document does not declare "Item" prop "title"
    And that clone's "Item" reads "heading" as "Keep me"

  # ── a time-travel clone is a genuine fork: its own fresh history, the source untouched ───────────
  Scenario: A time-travel clone is a fork with its own fresh history
    Given the target's "Item" title is written to "First"
    And the target's "Item" title is written to "Second"
    And the current seq is remembered as "mid"
    And the target's "Item" title is written to "Third"
    And the target's own log line count is remembered for time travel
    When the operator clones the target at the remembered "mid" seq
    Then the clone has no log or genesis files yet
    And the source's log did not grow
    And the source still reads "Item" title as "Third"
    When the clone's "Item" title is written to "Mutated in the clone"
    Then the clone's log holds exactly one entry
    And the clone's genesis is frozen at the remembered "mid" seq
    And the source still reads "Item" title as "Third"
    And the clone's "Item" id equals the source's "Item" id

  # ── regression: today's clone (no atSeq) is completely unaffected ────────────────────────────────
  Scenario: Cloning without atSeq behaves exactly as before
    Given the target holds an "Item" titled "Keep me"
    And the target's "Item" title is written to "First"
    And the target's "Item" title is written to "Second"
    And the target's "Item" title is written to "Third"
    When the operator clones the target with no seq given
    Then the clone's "Item" title reads "Third"
    And the clone's app document is byte-identical to the target's
    And the clone has no log or genesis files yet

  # ── invalid atSeq fails loudly, nothing created ───────────────────────────────────────────────────
  Scenario: An invalid atSeq fails loudly and creates nothing
    Given the target's "Item" title is written to "First"
    And the target's "Item" title is written to "Second"
    And the target's "Item" title is written to "Third"
    And the kernel's hosted instance count is remembered
    When the operator clones the target at a seq far past the head
    Then the clone attempt fails
    And the kernel's hosted instance count is unchanged

  Scenario: A negative atSeq fails loudly and creates nothing
    Given the target's "Item" title is written to "First"
    And the target's "Item" title is written to "Second"
    And the target's "Item" title is written to "Third"
    And the kernel's hosted instance count is remembered
    When the operator clones the target at seq -1
    Then the clone attempt fails
    And the kernel's hosted instance count is unchanged

  # ── the materializer ties into the slice-1 fsck invariant ────────────────────────────────────────
  Scenario: The materializer agrees with fsck
    Given the target's "Item" title is written to "First"
    And the target's "Item" title is written to "Second"
    And the target's "Item" title is written to "Third"
    When the operator clones the target at its current head seq
    Then the clone's "Item" title reads "Third"
    And the target's own log fsck holds for time travel
