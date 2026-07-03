@milestone-13
Feature: Design branches — origin-keyed three-way structural merge
  A design gets git-like branches: `sys.createBranch(design, name)` clones a working copy's whole
  subgraph (Design + its MetaTypes + MetaProps) into a NEW Branch, with every clone's `origin` flattened
  to the ORIGINAL lineage anchor (N-deep branching never chains). `sys.mergeBranch(source, target,
  resolutions?)` computes a lineage-keyed three-way structural merge over the two branches' committed
  heads (base = the max-logSeq common ancestor): a clean merge applies to the target working copy and
  creates a two-parent merge Commit; any conflict makes NO writes and returns a structured MergeReport
  instead, re-run with `resolutions: [{id, take: "source"|"target"}]` to apply per-conflict picks. See
  DECISIONS.md "App versioning — the full design (M13 clump)" §1 and docs/plans/versioning-slices.md
  slice 5.

  Background:
    Given a branchable designer instance holding a design with a type "Item"
    And the design is committed as "baseline" for branching
    And a branch named "feature" is created from the design over the WS

  # ── branching: the clone, its origin flattening, its GC reachability ────────────────────────────
  Scenario: Creating a branch clones the working copy with flattened origins
    Then db.designs is unchanged by the branch creation
    And db.branches holds a branch named "feature" whose head is the design's baseline commit
    And the branch's cloned "Item" type has origin equal to the original "Item" type's id
    And the branch's working copy renders through the generic designer store without error

  Scenario: A commit on the branch advances only that branch's head
    Given the branch's "Item" prop "label" is renamed to "title"
    When the branch is committed with message "rename on the branch"
    Then the branch's head advanced to the new commit
    And the main branch's head is still the baseline commit
    And the main design's working copy is unchanged

  # ── clean merges: disjoint edits, reorders, access unions ───────────────────────────────────────
  Scenario: Disjoint edits merge cleanly with a two-parent merge commit
    Given the main design's "Item" prop "label" is renamed to "caption"
    And the main design is committed with message "rename on main"
    And the branch adds a "priority" field to "Item"
    And the branch is committed with message "add field on branch"
    When the branch "feature" is merged into "main" over the WS
    Then the merge report shows merged true
    And the merge commit has parent equal to main's pre-merge head and mergeParent equal to the branch's head
    And the main design's "Item" now has a prop named "caption"
    And the main design's "Item" now has a prop named "priority"
    And the "priority" prop's origin traces back to the branch's source row

  Scenario: Both branches reordering props merges clean with no conflict
    Given the main design adds a "note" field to "Item" after "label"
    And the main design is committed with message "reorder on main"
    And the branch adds a "priority" field to "Item" after "label"
    And the branch is committed with message "reorder on branch"
    When the branch "feature" is merged into "main" over the WS
    Then the merge report shows merged true
    And the merge report shows no conflicts

  Scenario: Both branches adding different access rules merges clean and the report lists both
    Given the main design's access grants read on "Item" to everyone
    And the main design is committed with message "add read rule on main"
    And the branch's access grants edit on "Item" to admins
    And the branch is committed with message "add edit rule on branch"
    When the branch "feature" is merged into "main" over the WS
    Then the merge report shows merged true
    And the merge report's access changes mention the "Item" read rule
    And the merge report's access changes mention the "Item" edit rule

  # ── conflicts: nothing written, then resolved ────────────────────────────────────────────────────
  Scenario: Rename-vs-rename on the same prop conflicts and blocks all writes
    Given the main design's "Item" prop "label" is renamed to "heading"
    And the main design is committed with message "rename to heading on main"
    And the branch's "Item" prop "label" is renamed to "title"
    And the branch is committed with message "rename to title on branch"
    When the branch "feature" is merged into "main" over the WS
    Then the merge report shows merged false
    And the merge report has a conflict for the "Item" prop rename
    And the main design's "Item" still has a prop named "heading"
    And the main design's "Item" has no prop named "title"
    When the branch "feature" is merged into "main" taking source for that conflict over the WS
    Then the merge report shows merged true
    And the main design's "Item" now has a prop named "title"

  Scenario: Delete on one side and modify on the other is an existence conflict resolved by taking target
    Given the main design's "Item" field "label" is retyped to "int"
    And the main design is committed with message "retype label on main"
    And the branch's "Item" field "label" is removed
    And the branch is committed with message "remove label on branch"
    When the branch "feature" is merged into "main" over the WS
    Then the merge report shows merged false
    And the merge report has an existence conflict for the "Item" prop "label"
    When the branch "feature" is merged into "main" taking target for that conflict over the WS
    Then the merge report shows merged true
    And the main design's "Item" still has a prop named "label" typed "int"

  Scenario: The same function edited differently on both branches is a whole-fn conflict resolved by taking source
    Given the main design's fn "greet" is edited to return "hello from main" on main
    And the main design is committed with message "edit greet on main"
    And the branch's fn "greet" is edited to return "hello from branch" on the branch
    And the branch is committed with message "edit greet on branch"
    When the branch "feature" is merged into "main" over the WS
    Then the merge report shows merged false
    And the merge report has a conflict for the "greet" function
    When the branch "feature" is merged into "main" taking source for that conflict over the WS
    Then the merge report shows merged true
    And the main design's "greet" function returns "hello from branch"

  # ── drift refusal: uncommitted edits block the merge entirely ───────────────────────────────────
  # The target's own uncommitted rename (label -> heading) stays; the merge writes nothing (no "title"
  # from the branch, which the branch never even authored here — the proof is the merge refused before
  # computing anything, so the target is exactly its own pre-merge working copy).
  Scenario: Uncommitted drift on the target refuses the merge with nothing written
    Given the main design's "Item" prop "label" is renamed to "heading" but left uncommitted
    When the branch "feature" is merged into "main" over the WS
    Then the merge report shows merged false
    And the merge report flags drift on "target"
    And the main design's "Item" still has a prop named "heading"

  # ── re-merge sanity: the LCA advances past an already-merged commit ─────────────────────────────
  Scenario: A fresh branch edit merges again cleanly after an earlier clean merge
    Given the main design's "Item" prop "label" is renamed to "caption"
    And the main design is committed with message "rename on main"
    And the branch adds a "priority" field to "Item"
    And the branch is committed with message "add field on branch"
    And the branch "feature" is merged into "main" over the WS
    When the branch adds a "notes" field to "Item"
    And the branch is committed with message "second add on branch"
    And the branch "feature" is merged into "main" over the WS
    Then the second merge report shows merged true
    And the second merge report shows no conflicts
    And the main design's "Item" now has a prop named "notes"
