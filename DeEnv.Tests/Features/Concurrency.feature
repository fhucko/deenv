@milestone-13 @multi-user
Feature: Optimistic-concurrency anti-clobber (baseVersion)
  Two sessions can silently clobber each other today: both load the SAME object, both edit it, both
  save — the later ctx.commit() overwrites the earlier one with no warning, and the earlier session's
  change is gone. This is DECISIONS.md "App versioning — the full design (M13 clump)" (§0's baseVersion
  bullet) pulled ahead of the milestone because the bare anti-clobber check fixes a current bug.

  DETECTION ONLY (per the design doc): a stale commit is REJECTED whole (the existing rejected-save
  path — the global error banner, the client draft kept intact) — no conflict UI, no field-level merge,
  no change log. The check is OBJECT-granular, not whole-store: a commit touching only objects unchanged
  since its base applies even while OTHER objects in the store have moved on (disjoint interleaved
  commits auto-merge — no whole-store rejection, no retry storm).

  # ── (a) the headline bug, proven end-to-end in two real browser sessions ──────────────────────────
  # Two tabs load the SAME Note. Both are editing FROM THE SAME starting data. Session 1 saves first —
  # it applies (nothing was stale under it yet). Session 2, still holding the pre-session-1 data, saves
  # second — its ctx's baseVersion predates session 1's change to the SAME object, so it is REJECTED:
  # the global error banner appears in session 2, and the store still holds SESSION 1's value (not
  # session 2's) — the clobber that happens on main today does not happen here.
  @milestone-13 @multi-user
  Scenario: A stale save to an object another session just changed is rejected, not silently applied
    Given the concurrency fixture app is served
    And session 1 opens the note at "/notes/2"
    And session 2 opens the note at "/notes/2"
    When session 1 changes the title to "From session 1" and saves
    Then session 1's save is accepted
    When session 2 changes the title to "From session 2" and saves
    Then session 2's save is rejected
    And session 2 shows the "Someone else changed this" error banner
    And the stored note 2 title is "From session 1"

  # ── (b) disjoint objects: both sessions loaded the SAME version, but edit DIFFERENT objects ────────
  # Object-granularity's whole point: session 2's base predates NOTHING it touches (it never touches
  # note 2), so it must NOT be rejected just because SOME other object in the store moved on. Driven at
  # the WsHandler level (in-process) — the observable is the store, not the browser.
  Scenario: Two sessions editing different objects from the same base both apply
    Given the concurrency fixture app
    And both sessions loaded the store at the current version
    When session 1 commits note 2's title to "Session 1's note"
    And session 2 commits note 3's title to "Session 2's note"
    Then both commits are accepted
    And the stored note 2 title is "Session 1's note"
    And the stored note 3 title is "Session 2's note"

  # ── (c) sequential saves from ONE session: its own base advances with its own commits ────────────
  # A session that saves, then edits again and saves again, must not be rejected by ITS OWN prior
  # commit — the base for the second save is the version the first save itself landed at.
  Scenario: A session's second save, based on its own first save, applies
    Given the concurrency fixture app
    And a session loaded the store at the current version
    When that session commits note 2's title to "First edit" using its loaded base
    Then that commit is accepted
    When that session commits note 2's count to 7 using the version its first commit landed at
    Then that commit is also accepted
    And the stored note 2 title is "First edit"
    And the stored note 2 count is 7
