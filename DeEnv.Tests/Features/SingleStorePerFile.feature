@milestone-13
Feature: One store instance per data file within a kernel process
  A JsonFileInstanceStore assumes it is the ONLY writer to its file: its `_sync` lock, its in-memory
  `_doc`/version, and the slice-1 WAL seq counter are all per-INSTANCE. The kernel violated that for the
  DESIGN HOST's file — the boot-cached `_designHostStore` (used by every create/clone/delete/rename mirror
  write), the LIVE hosted designer instance's own store, and the fresh-per-call stores KernelHostActions
  opened for commitDesign/publish/merge/branch could all coexist over instances/1/app-data.json in one
  process. A commit made through a fresh store, followed by a mirror write from the stale boot-cached
  store's `_doc`, silently CLOBBERED the commit out of the snapshot; a live-session edit did the same AND
  collided WAL seqs (fsck/boot-reconcile violations). The fix: the design host's ONE store is THE store —
  shared by boot sync, the live hosted instance, the mirror writes, era resolution, and every
  KernelHostActions call over that file. This feature pins the data-loss class shut.

  Background:
    Given a versioned designer instance and a target instance, both hosted by a single-store kernel

  # ── THE REPRO: a fresh-store commit survives two subsequent mirror-writing clones ─────────────────
  # boot → commitDesign (writes Commit + advances the main Branch head) → clone → clone again. Under the
  # old boot-cached _designHostStore, the SECOND clone's MirrorInstanceInsert persisted the boot-time
  # (pre-commit) snapshot and deleted the commit + branch head. With one shared store the commit and
  # branch survive; the designer's log fsck holds.
  Scenario: A commit survives two subsequent mirror-writing clones
    Given the designer commits the design with message "keep me"
    And the design's committed count is remembered
    When the operator clones the target
    And the operator clones the target again
    Then the design still has its committed count of commits
    And the design's main branch still has a head commit
    And the designer's log fsck holds for single store

  # ── THE SIBLING: a fresh-store commit survives a subsequent LIVE designer-session edit ────────────
  # boot → commitDesign (fresh store) → an edit through the LIVE hosted designer instance's OWN store
  # (its in-memory _doc predated the commit). Old behavior: the live store's Save() rewrote the snapshot
  # WITHOUT the commit rows, AND its stale _doc.Version minted a WAL seq colliding with the fresh store's
  # already-appended entry — snapshot clobber + WAL corruption. With one shared store the live edit sees
  # the commit, the commit survives, there are no duplicate log seqs, and fsck holds.
  Scenario: A commit survives a subsequent live designer-session edit
    Given the designer commits the design with message "keep me too"
    And the design's committed count is remembered
    When a label is edited through the live designer session's own store
    Then the design still has its committed count of commits
    And the design's main branch still has a head commit
    And the designer's log has no duplicate seqs
    And the designer's log fsck holds for single store

  # ── mirror writes are visible in the live session's OWN store without a reboot ────────────────────
  # After a clone host action, the designer's own store (the one the WS session serves renders from) holds
  # the new Instance row immediately — the shared store makes the mirror write live, not boot-cached.
  Scenario: A clone's mirror row is visible in the live designer store
    Given the live designer store's Instance-row count is remembered
    When the operator clones the target
    Then the live designer store holds one more Instance row than before
