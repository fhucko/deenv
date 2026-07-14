Feature: Designer - Commit, Publish, Branches, Merge


  @milestone-auth @single-user
  Scenario: The committed designer gates anonymous visitors with its own login form
    Given the anonymous operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designer designs route
    Then the committed designer login gate is shown


  # Apply is the deploy: picking a different design and applying records it on the instance (the
  # registry designId changes) AND projects the chosen design onto the instance's app document.
  @milestone-10 @single-user
  Scenario: Applying a different design records it and deploys it to the instance
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    And I open the instance "todo"
    And I pick the design "crm" in the dropdown
    And I apply the design
    Then the instance "todo" records the design "crm"
    And the "todo" instance's app document describes the type "Customer"


  # The end-to-end split: edit a design in /designs/<id> (rename a type + retype its reference), then
  # apply that design to its instance — the edited design is what gets deployed.
  @milestone-10 @single-user
  Scenario: Editing a design then applying it deploys the edit
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Widget"
    And I retype the prop "items" to "Widget"
    When I open the instance "todo"
    And I apply the design
    Then the "todo" instance's app document describes the type "Widget"


  # ──── the Commit-button UX slice (M13 versioning's last piece) ──────────────────────────────────────────────────────────
  #
  # sys.commitDesign(design, message, migration) is now wired lockstep into the AST scan / validator / both
  # interpreters (mirroring sys.publish's existing wiring exactly), and the design editor grows its
  # first versioning surface: a message input + a Commit button + a "Last commit:" confirmation line
  # (DesignCommit.feature is the full spec of the commit mechanism; this scenario is the UI's
  # end-to-end proof it is reachable from the editor, not a re-test of the mechanism itself).
  #
  # UX REVIEW FIX: the message input does NOT clear on click (a synchronous clear both faked "done"
  # before the server ack and destroyed the typed message on a rejected commit). Instead, the positive
  # confirmation is the "Last commit:" line — pure Code reading the design's main branch head — which
  # updates to the just-committed message once the success ack's refetch lands (ws.ts:947). The input
  # is left holding what was typed (retained by construction — nothing clears it either way).
  @milestone-13 @single-user
  Scenario: Committing a design from the editor shows the new commit as the confirmation line
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "first snapshot" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "first snapshot"
    When I open the commit history
    Then the commit history shows a commit with message "first snapshot"


  # The AST wiring guard: an app whose Code calls sys.commitDesign is detected by
  # HostActionScan.UsesHostActions exactly like the existing sys.publish/sys.delete detection
  # (Kernel.feature's "designer-shaped, uses host actions" scenario) — the wiring this slice adds, proven
  # at the same seam. A real seam is built (the AST wiring works), but the app declares no `sys` rule, so
  # the authority gate still rejects — the same shape-≠-authority proof Kernel.feature already makes for
  # sys.delete, now repeated for sys.commitDesign.
  @milestone-13 @single-user
  Scenario: An app whose Code calls sys.commitDesign is wired for host actions, and the sys rule still gates it
    Given a registry whose only instance is designer-shaped, calls sys.commitDesign, and has no sys rule
    And the kernel has started
    When I send a hostAction "commitDesign" for that instance's own id over its WebSocket
    Then the host action reply over the WebSocket is an error
    And the kernel still hosts that instance


  # Validator arity guard, mirroring sys.publish's existing 2-argument fixed arity: calling
  # sys.commitDesign with the wrong number of arguments fails to LOAD (a load-time schema error), not at
  # first paint — the same class Schema.feature's "loading is rejected" scenarios pin for other builtins.
  @milestone-13 @single-user
  Scenario: sys.commitDesign with the wrong number of arguments fails to load
    Given the app description:
      """
      types
          Db
              designs set of Design
          Design
              label text

      ui
          fn render()
              return <button onClick={() => sys.commitDesign(1)}>
                  "Commit"
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "commitDesign"


  # Empty-message commit: the designer's OTHER inputs (design label, rename) accept and persist an empty
  # value with no client-side guard — a commit message is the same, honest kind of free text, so an
  # empty message is ALLOWED, not silently blocked. Pinned so a future "helpfully" added required-field
  # guard is a deliberate decision, not an accident.
  @milestone-13 @single-user
  Scenario: Committing with an empty message is allowed, matching the editor's other free-text inputs
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I click Commit
    Then the last-commit line eventually shows "(no message)"
    When I open the commit history
    Then the commit history shows a commit with an empty message


  # UX REVIEW FIX 2: the history is newest-first (orderBy descending on logSeq, the honest total
  # order) — a daily glance finds the latest commit on top instead of buried under the boot-time
  # Adopted baselines.
  @milestone-13 @single-user
  Scenario: The commit history lists newest commits first
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "newest one" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "newest one"
    When I open the commit history
    Then the commit history's first row has message "newest one"


  # Review fix 5 — the textarea→commitDesign→detail round-trip has no rendered-UI proof and the
  # binding is load-bearing: open the Migration disclosure, type a valid migration, commit, then
  # confirm it rendered on the commit's detail page.
  @milestone-13 @single-user
  Scenario: Committing a migration from the editor renders it on the commit's detail page
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "with migration" into the commit message
    And I expand the Migration disclosure
    And I type a migration for "TodoItem" into the migration textarea
    And I click Commit
    Then the last-commit line eventually shows message "with migration"
    When I open the commit history
    And I open the commit "with migration" from the history
    Then the commit detail page shows the migration source for "TodoItem"


  # ──── Host-action success callback (docs/plans/host-action-success-signal.md) — the commit bar's
  # first consumer. sys.commitDesign's optional trailing fn arg runs ONLY on the ok reply, so a
  # successful commit clears BOTH the message and migration inputs (a committed message is done —
  # retaining it invites a stale re-commit); a rejected commit leaves both exactly as typed (the
  # callback never ran), matching the existing "keeps the typed message" proof but now over BOTH
  # inputs and asserting the CLEAR on the success leg the earlier UX-fix scenario deliberately left
  # unasserted (it predates the callback mechanism — the input was never cleared client-side at all).
  @milestone-13 @single-user
  Scenario: Committing successfully clears the message and migration inputs
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "cleared on success" into the commit message
    And I expand the Migration disclosure
    And I type a migration for "TodoItem" into the migration textarea
    And I click Commit
    Then the last-commit line eventually shows message "cleared on success"
    And the commit message input eventually holds ""
    And the migration textarea eventually holds ""


  # The rejection leg: an invalid migration (naming a type absent from the design — the same shape
  # DesignCommit.feature's "must name a committed type" scenario reproduces server-side) rejects with
  # the global error banner, and the callback never having run means BOTH inputs retain exactly what
  # was typed.
  @milestone-13 @single-user
  Scenario: Committing with an invalid migration keeps both inputs on rejection
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "should not clear" into the commit message
    And I expand the Migration disclosure
    And I type a migration for "Bogus" into the migration textarea
    And I click Commit
    Then the global error banner is shown mentioning "Bogus"
    And the commit message input still holds "should not clear"
    And the migration textarea still holds the migration for "Bogus"


  # B1 — the commit-detail page (/commits/<id>). The history table is LINKED again; clicking a row
  # navigates client-side to the detail page, which resolves the commit by route id and shows its fields
  # (message/at/design/parent/logSeq) + the cached canonical snapshot text, read-only. Back returns to
  # the history list. (Replaces the old "no dead self-link" scenario, which pinned linked={false} while
  # no detail page existed.)
  @milestone-13 @single-user
  Scenario: A commit history row opens the commit-detail page and Back returns
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "snapshot A" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "snapshot A"
    When I open the commit history
    And I open the commit "snapshot A" from the history
    Then the commit detail page shows message "snapshot A"
    And the commit detail page shows design "todo"
    When I navigate back
    Then the commit history shows a commit with message "snapshot A"


  @milestone-13 @single-user
  Scenario: A commit made by a logged-in operator shows its author on the detail page
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "authored snapshot" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "authored snapshot"
    When I open the commit history
    And I open the commit "authored snapshot" from the history
    Then the commit detail page shows author "admin"


  # B1 ride-along: with the history LINKED again, an empty-message commit would otherwise render an empty,
  # unclickable <a> (the phantom the old linked={false} avoided). The generic SetTable now renders a
  # "(no <humanized labelProp>)" placeholder for an empty label WHEN linked, so the row has visible,
  # clickable text that still routes to the detail page. The placeholder humanizes the prop name (matching
  # the library's own convention, e.g. the "Message" column header), so the cell reads "(no Message)" —
  # distinct from the design-editor's page-local last-commit line, which stays the lowercase "(no message)".
  @milestone-13 @single-user
  Scenario: An empty-message commit history row shows a placeholder label and still links
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I click Commit
    Then the last-commit line eventually shows "(no message)"
    When I open the commit history
    Then the commit history's first row link reads "(no Message)"


  # B2 — the "Changes since parent" diff on the commit-detail page. Commit a baseline, then rename a type
  # (retyping the referencing prop so the design stays valid) and commit again. Opening the second commit's
  # detail page shows the STRUCTURAL diff against its parent, computed server-side by sys.diffCommits and
  # shipped via the memo cache (like sys.schema/sys.canRead — no host action, no conformance). The payoff of
  # the identity diff: the type change renders as a RENAME ("TodoItem → Task"), never as a remove+add.
  @milestone-13 @single-user
  Scenario: The commit-detail page shows a rename as a rename in "Changes since parent"
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "baseline" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "baseline"
    When I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    And I type "rename TodoItem" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename TodoItem"
    When I open the commit history
    And I open the commit "rename TodoItem" from the history
    Then the changes-since-parent shows a rename from "TodoItem" to "Task"
    And the changes-since-parent shows no removal of "TodoItem"
    When I navigate back
    And I navigate back
    And I add a type to the design
    And I name the just-added type "Project"
    And I add a field "title" to the type "Project"
    And I type "add Project" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "add Project"
    When I open the commit detail for "add Project"
    Then the changes-since-parent shows an add of "Project"


  # ──── B3 — Publish + dry-run from the designer ──────────────────────────────────────────────────────────────────────────────────────────────────
  #
  # The design editor grows a Publish section: for each instance running this design, a toggle-gated
  # Preview (the dry-run PublishReport, computed server-side by sys.publishPreview — a server-backed READ
  # shipped via the memo cache like sys.diffCommits, NOT a host action, changing NOTHING) then an Apply
  # (sys.publish — the existing host action). The preview reaches the TARGET's data file read-only; the
  # apply carries data through renames. Three proofs: the dry-run is loud + inert, the apply drives the
  # real publish, and a rename carries data (leaning on Publish.feature's migration-engine proof).

  # 1) The dry-run surfaces a destructive change LOUDLY and changes NOTHING. Remove a leaf field
  # (TodoItem.checked) in the designer and commit, so the design's head diverges from the target's stamped
  # boot baseline by a REMOVAL. Previewing the "todo" instance shows the removal in a destructive (red)
  # class, and the target instance's stored schema STILL has the field — the preview wrote nothing.
  @milestone-13 @single-user
  Scenario: Previewing a publish surfaces a destructive change loudly and changes nothing
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I remove the field "checked" from the type "TodoItem"
    And I type "drop checked" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "drop checked"
    When I preview the publish for the instance "todo"
    Then the publish preview flags "TodoItem.checked" as removed loudly
    And the "todo" instance's app document still describes the field "checked"


  @milestone-13 @single-user
  Scenario: A drift-only publish preview tells the operator to commit instead of offering Apply
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    When I preview the publish for the instance "todo"
    Then the publish preview asks me to commit before publishing
    And the publish preview for the instance "todo" shows no Apply button


  # 2) The confirmed Apply drives the real publish. After a rename+commit, previewing shows the rename;
  # applying fires sys.publish (which stamps the target to the new head), and the target's app document then
  # carries the rename. Re-previewing reads "up to date" — the success signal the operator sees (the diff is
  # now empty). NB the host-action ack runs resetViewState, which closes the open preview toggle; the
  # operator re-opens Preview to confirm — so this step re-previews rather than expecting an auto-refresh.
  @milestone-13 @single-user
  Scenario: Applying a previewed publish deploys the design to the instance
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    And I type "rename for apply" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename for apply"
    When I preview the publish for the instance "todo"
    Then the publish preview shows a rename from "TodoItem" to "Task"
    When I apply the publish for the instance "todo"
    Then the "todo" instance's app document describes the type "Task"
    And the publish row for instance "todo" eventually shows "Published to todo"
    When I preview the publish for the instance "todo"
    Then the publish preview for the instance "todo" reads up to date


  # 3) A rename carries the target's DATA through the publish. The designer's Publish UI reaches the real
  # rename-safe publish (Publish.feature is the exhaustive proof of the migration engine itself — this proves
  # the UI drives it and the data survives, not a re-test of slice-4). Seed a TodoItem in the target, rename
  # the type in the designer + commit, apply via the UI, then read the target's carried-over data back.
  @milestone-13 @single-user
  Scenario: Applying a rename through the Publish UI carries the target's data
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    And the "todo" target holds a TodoItem with text "buy milk"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    And I type "rename carries data" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename carries data"
    When I preview the publish for the instance "todo"
    And I apply the publish for the instance "todo"
    Then the "todo" instance eventually holds a "Task" with text "buy milk"


  # 4) The preview→apply CONSISTENCY GUARD (addendum). Splitting preview from apply opens a TOCTOU window:
  # the operator approves a SPECIFIC plan (the preview), but an unguarded apply recomputes fresh and could
  # execute a DIFFERENT plan if the target moved in between. The Apply button always passes back the token
  # `sys.publishPreview` handed it (targetCommit + targetVersion); the server rejects a stale apply BEFORE
  # any write. Here the target's OWN data moves (a direct field write bumping its store version) after the
  # preview was taken but before Apply is clicked — the target is never actually published.
  #
  # The target holds a REAL TodoItem (review fix): a rename with NOTHING to migrate (an empty TodoItem
  # extent) never touches the boundary-apply's write path at all (ApplyPublishBoundary short-circuits when
  # there is no data of the affected type), so a stale-reject proof needs actual data at risk of being
  # migrated for the "no write happened" assertion to mean anything.
  @milestone-13 @single-user
  Scenario: Applying a stale preview is rejected and the target is not published
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    And the "todo" target holds a TodoItem with text "must not be migrated"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    And I type "rename then stale apply" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename then stale apply"
    When I preview the publish for the instance "todo"
    Then the publish preview shows a rename from "TodoItem" to "Task"
    And the "todo" target's data changes since the preview
    When I apply the publish for the instance "todo"
    Then the global error banner is shown mentioning "changed since the preview"
    And the "todo" instance's app document does not describe the type "Task"
    And the "todo" target's data is unchanged by the rejected apply


  # 5) The guard's OTHER leg: a CLEAN (non-stale) guarded apply on the VERSIONED path still succeeds
  # end-to-end and carries data — proving the guard rejects ONLY a genuinely stale token, never a fresh one.
  # (Scenario 3 above already proves the UI-driven rename+data-carry; this is the same shape but explicitly
  # on the guarded 4-arg sys.publish call, since scenario 3 predates the addendum and never exercised it.)
  @milestone-13 @single-user
  Scenario: A clean guarded apply on the versioned path still succeeds and carries data
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    And the "todo" target holds a TodoItem with text "guarded apply keeps me"
    # The target is already stamped at boot to the design's baseline (see EnsureMainBranches +
    # StampMatchingInstance). We change the design and do one guarded publish; it will be on the
    # versioned path (diff from the boot stamp) and exercises the 4-arg guarded form.
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    And I type "clean guarded apply" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "clean guarded apply"
    When I preview the publish for the instance "todo"
    Then the publish preview shows a rename from "TodoItem" to "Task"
    When I apply the publish for the instance "todo"
    Then the "todo" instance's app document describes the type "Task"
    And the "todo" instance eventually holds a "Task" with text "guarded apply keeps me"


  # ──── B4 — Branch UI + createBranch/mergeBranch from the designer ──────────────────────────────────────────────────────────────
  #
  # The design editor grows a Branches section: create a branch (sys.createBranch — a host action), see
  # the design's branches as links to their own /designs/<id> editors (a branch working copy is a Design
  # row at its own URL — switching branches is navigation), and merge a branch back in via a toggle-gated
  # sys.mergePreview (a server-backed READ shipped via the memo cache like sys.publishPreview, NOT a host
  # action, changing NOTHING) then an Apply (sys.mergeBranch — the host action). The merge machinery itself
  # (three-way merge, conflicts, resolutions, access-change surfacing) is exhaustively proven at the WS-op
  # level in DesignMerge.feature; these scenarios prove the UI drives it end-to-end.

  # 1) Create a branch: commit a baseline (so the branch has a head to clone), then create a branch named
  # "feature". It appears in the Branches section as a branch link (a Design row at its own URL).
  @milestone-13 @single-user
  Scenario: Creating a branch from the editor lists it as a branch link
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "baseline for branching" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "baseline for branching"
    When I create a branch named "feature"
    Then the Branches section lists a branch link "feature"


  # 2) A clean merge carries a disjoint change. Commit a baseline, branch, add a field on the branch and
  # commit it there, then merge the branch back into the main design — a clean merge (disjoint edit), and
  # the main design's type now carries the branch's new field. Proves the whole preview→apply UI path.
  @milestone-13 @single-user
  Scenario: Merging a branch cleanly carries its change into the target design
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "baseline for merge" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "baseline for merge"
    When I create a branch named "feature"
    And I open the branch "feature" from the Branches section
    And I add a field "priority" to the type "TodoItem"
    And I type "add priority on branch" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "add priority on branch"
    When I open the designs list
    And I edit the design "todo"
    And I preview the merge of branch "feature"
    Then the merge preview reports a clean merge
    When I apply the merge of branch "feature"
    Then the Branches section eventually shows "Merged feature into this design"
    And the design "todo" eventually has a stored prop named "priority" on "TodoItem"
    And the merge preview reports already up to date


  # 3) A conflict is shown, resolved by a per-conflict pick, then applied. Rename the same prop differently
  # on the branch and on main; the merge preview surfaces the conflict with its base/source/target values,
  # Apply stays gated until the conflict is resolved, and picking a side unlocks the merge.
  @milestone-13 @single-user
  Scenario: A merge conflict renders, is resolved by a pick, and then merges
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "baseline for conflict" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "baseline for conflict"
    When I create a branch named "feature"
    And I open the branch "feature" from the Branches section
    And I rename the prop "text" to "heading" on the type "TodoItem"
    And I type "rename to heading on branch" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename to heading on branch"
    When I open the designs list
    And I edit the design "todo"
    And I rename the prop "text" to "caption" on the type "TodoItem"
    And I type "rename to caption on main" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename to caption on main"
    When I preview the merge of branch "feature"
    Then the merge preview shows a conflict with source "heading" and target "caption"
    And the merge preview shows no Merge button
    When I take source for the first conflict
    And I apply the merge of branch "feature"
    Then the design "todo" eventually has a stored prop named "heading" on "TodoItem"


  # 4) The access-change must-see block. A merge that introduces an access-rule difference ALWAYS surfaces
  # it (never silently folded in — the settled security rule), even on an otherwise-clean merge. Grant a
  # read rule on the branch (reusing the slice-5 store-level access mutation), commit it there, then the
  # merge preview on main renders the loud AccessChanges block naming the rule.
  @milestone-13 @single-user
  Scenario: A merge that changes an access rule surfaces the must-see access block
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "baseline for access" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "baseline for access"
    When I create a branch named "feature"
    And I grant read on "TodoItem" to everyone on the branch "feature"
    And I open the branch "feature" from the Branches section
    And I type "grant read on branch" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "grant read on branch"
    When I open the designs list
    And I edit the design "todo"
    And I preview the merge of branch "feature"
    Then the merge preview's access block mentions "TodoItem"
