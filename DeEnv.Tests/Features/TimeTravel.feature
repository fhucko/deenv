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

  # ── the sharpest scenario: era-schema resolution across a MULTI-COMMIT publish gap ────────────────
  # A first "baseline" publish (C1, unrenamed) STAMPS the target — a target's very FIRST publish always
  # takes the pre-slice-4 name-match fallback and writes NO boundary marker at all (see Publish.feature);
  # only a SECOND, STAMPED publish is identity-diffed and writes the real boundary entry this scenario
  # needs. Between C1 and the eventual publish, the design gets a SECOND commit (C2, adding an unrelated
  # "note" field) that is COMMITTED BUT NEVER PUBLISHED on its own — then a THIRD commit (C3, the rename)
  # IS published. So the target's stamped base (C1) and the published head (C3) are TWO commits apart —
  # publish diffs across that whole gap in one identity-diff (KernelHostActions.Publish), and the boundary
  # records BaseCommitId=C1 directly (not C3.parent, which is C2). Cloning at "before" (a seq predating the
  # C3 publish entirely) must therefore run C1's era schema — proven by the presence/absence of C2's own
  # "note" field: C1 lacks it (never committed at C1) and C3 kept it (the rename commit is BUILT ON TOP of
  # C2's addition, so `note` still exists at C3) — asserting its ABSENCE at "before" is what actually
  # distinguishes C1 from C2/C3, since title-vs-heading alone cannot tell C1 from C2 apart. Under the
  # superseded parent-walk (C3.parent = C2), this scenario would incorrectly serve C2's schema for the
  # "before" clone — i.e. show "note" present when it must be absent.
  #
  # ONE clone per scenario (deliberately, not two in sequence): a separate, PRE-EXISTING kernel bug this
  # slice's testing surfaced — KernelHost's Mirror*/`_designHostStore` write path (used by every
  # create/clone/delete/rename to keep db.instances in lockstep) operates on the SAME boot-cached, never-
  # refreshed store reference `_designHostStore` names (see ResolveEraDoc's own doc on why era-resolution
  # reads bypass it) — so a SECOND clone/create in one kernel session, after commits were made through
  # fresh stores, silently CLOBBERS the designer's db.designs/db.commits/db.branches back to their
  # boot-time (pre-commit) contents. Flagged to the coordinator as a newly-discovered, out-of-scope,
  # pre-existing bug (not introduced by this slice, not one of the three named fixes) — NOT fixed here;
  # this scenario is split across TWO independent Background-fresh scenarios below to avoid tripping it,
  # so both proofs stay genuinely isolated from that unrelated bug.
  Scenario: A clone at a pre-publish seq across a multi-commit gap runs the pre-gap era schema
    Given the target holds an "Item" titled "Keep me"
    And the time-travel design is committed with message "baseline"
    And the time-travel designer publishes the design's head commit to the target over the WS
    And the current seq is remembered as "before"
    And the design adds a "note" field to "Item" for time travel
    And the time-travel design is committed with message "add note (never published alone)"
    And the design's "Item" prop "title" is renamed to "heading" for time travel
    And the time-travel design is committed with message "rename title to heading"
    And the time-travel designer publishes the design's head commit to the target over the WS
    When the operator clones the target at the remembered "before" seq
    Then that clone's app document declares "Item" prop "title"
    And that clone's app document does not declare "Item" prop "heading"
    And that clone's app document does not declare "Item" prop "note"
    And that clone's "Item" reads "title" as "Keep me"

  Scenario: A clone at a post-publish seq across a multi-commit gap runs the post-gap era schema
    Given the target holds an "Item" titled "Keep me"
    And the time-travel design is committed with message "baseline"
    And the time-travel designer publishes the design's head commit to the target over the WS
    And the design adds a "note" field to "Item" for time travel
    And the time-travel design is committed with message "add note (never published alone)"
    And the design's "Item" prop "title" is renamed to "heading" for time travel
    And the time-travel design is committed with message "rename title to heading"
    And the time-travel designer publishes the design's head commit to the target over the WS
    And the current seq is remembered as "after"
    When the operator clones the target at the remembered "after" seq
    Then that clone's app document declares "Item" prop "heading"
    And that clone's app document does not declare "Item" prop "title"
    And that clone's app document declares "Item" prop "note"
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

  # ── invalid atSeq fails loudly, nothing created — not even an orphan instances/<id>/ directory ─────
  Scenario: An invalid atSeq fails loudly and creates nothing
    Given the target's "Item" title is written to "First"
    And the target's "Item" title is written to "Second"
    And the target's "Item" title is written to "Third"
    And the kernel's hosted instance count is remembered
    And the next instance id is remembered
    When the operator clones the target at a seq far past the head
    Then the clone attempt fails
    And the kernel's hosted instance count is unchanged
    And no new instance directory was created

  Scenario: A negative atSeq fails loudly and creates nothing
    Given the target's "Item" title is written to "First"
    And the target's "Item" title is written to "Second"
    And the target's "Item" title is written to "Third"
    And the kernel's hosted instance count is remembered
    And the next instance id is remembered
    When the operator clones the target at seq -1
    Then the clone attempt fails
    And the kernel's hosted instance count is unchanged
    And no new instance directory was created

  # ── the materializer ties into the slice-1 fsck invariant ────────────────────────────────────────
  Scenario: The materializer agrees with fsck
    Given the target's "Item" title is written to "First"
    And the target's "Item" title is written to "Second"
    And the target's "Item" title is written to "Third"
    When the operator clones the target at its current head seq
    Then the clone's "Item" title reads "Third"
    And the target's own log fsck holds for time travel

  # ── unresolvable era commit fails loudly, never silently falls back to the current doc ────────────
  # A boundary that NAMES a commit (a real publish happened) but that commit no longer resolves (simulated
  # here by corrupting the boundary to point at a bogus id — the same observable shape a torn/rolled-back
  # design-host store would produce) must be told apart from "no boundary at all" (case (a), where current
  # app.deenv legitimately IS the era doc) — silently falling back there would serve the WRONG schema
  # indistinguishably from a correct resolution. The clone attempt fails loudly instead; nothing created.
  Scenario: A boundary naming an unresolvable commit fails loudly and creates nothing
    Given the target holds an "Item" titled "Keep me"
    And the time-travel design is committed with message "baseline"
    And the time-travel designer publishes the design's head commit to the target over the WS
    And the design's "Item" prop "title" is renamed to "heading" for time travel
    And the time-travel design is committed with message "rename title to heading"
    And the time-travel designer publishes the design's head commit to the target over the WS
    And the target's newest boundary marker is corrupted to point at a nonexistent commit
    And the kernel's hosted instance count is remembered
    And the next instance id is remembered
    When the operator clones the target at its current head seq
    Then the clone attempt fails
    And the kernel's hosted instance count is unchanged
    And no new instance directory was created
