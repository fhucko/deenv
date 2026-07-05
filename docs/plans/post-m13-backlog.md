# Post-M13 backlog — self-contained briefs for background tasks

Written 2026-07-04, immediately after M13 (core + UX surface) completed at suite 715/715
(`docs/plans/versioning-slices.md` is the milestone record; DECISIONS.md carries the entry).
Each task below is written to be handed to a FRESH session/agent with no other context.

**Standing process for every task** (from `.claude/skills/build/SKILL.md` — read it first):
isolated worktree off LOCAL main (never `origin/main`, never the shared checkout); Gherkin
first where behavior changes; full suite `dotnet test DeEnv.Tests -c Release` from PowerShell
with output captured to a file; review per the tier policy (builders sonnet at low/medium,
opus for subtle zones; reviewers opus, diff PINNED to the branch's merge-base); ff-merge;
docs+memory sync in the same landing. Never kill chrome by name (testhost / `*ms-playwright*`
only). (The LoginViewSwap/LogoutViewSwapTests parallel-load flake was root-caused + fixed
2026-07-04 — PBKDF2 CPU contention; the test host runs `DEENV_PBKDF2_ITERATIONS=1`.)
Two-tab browser scenarios are ParallelLimiter-capped (`TwoTabScenarioLimit`) — don't add
two-tab scenarios without joining that limiter.

Deploy to the box is deliberately SKIPPED (user decision 2026-07-04) — no task below touches
the box, and none should re-pitch deployment.

---

## Track A — correctness fast-follows (small, disjoint, parallel-safe)

### A1 — Lazy main-branch ensure on first commit — DONE 2026-07-04  [S; builder sonnet; review architecture]
**Bug (scenario-documented):** a design created at RUNTIME (designer's generic New) has no
`main` Branch row — `EnsureMainBranches` runs only at boot (`KernelHost.cs`) — so the first
`sys.commitDesign` on it fails with the branch-lookup error ("owning branch"). The
Commit-button slice's failure-leg scenario in `Designer.feature` currently PINS this wrong
behavior (it asserts the "owning branch" error as the rejection reason).
**Fix:** `KernelHostActions.CommitDesign` (and anything else that resolves a design's branch,
e.g. `CreateBranch`'s source resolution) lazily creates the `main` Branch when the design has
none — create-if-missing, idempotent, EXACTLY the semantics `EnsureMainBranches` applies at
boot (name "main", head empty, workingCopy = the design; go through the live store,
one atomic batch like the boot path). Do NOT remove the boot ensure (it still covers adopted
designs pre-first-commit for era resolution).
**Accept:** the Designer.feature failure-leg scenario flips to the honest NEW behavior (a
fresh design's first commit SUCCEEDS; find a different honest rejection for the
banner-retention leg — e.g. blank the design's type name first so schema validation rejects);
a new scenario: create design at runtime → commit → Commit row + main branch exist without a
reboot. Suite green from 715.
**Conflicts:** touches `KernelHostActions.cs` — do not run concurrently with B3/B4.

### A2 — dt.ts scalar-var refetch race — DONE 2026-07-04
[M; builder opus — client-reconcile subtle zone; review architecture]
Already fully specced in the pending task chip — if the chip was started, skip this entry.
**Bug:** `dt.ts` `mergeState` unconditionally overwrites plain scalar `ui var`s with a refetch
reply's echoed value; no carve-out for a value the user is typing (unlike the transient-draft
carve-out at dt.ts:89-90). Any two-way-bound text input on a page that triggers a refetch can
lose keystrokes. The Commit-button harness works around it (`DesignerSteps.cs`
`WhenTypeCommitMessage` waits for `!refetchInFlight`) — remove that wait if the fix makes it
unnecessary.
**Fix direction:** mirror the transient-draft carve-out semantics for scalars (don't clobber
a var whose client value differs from the last client-known value), or track dirty/focused
input state. BOTH twins' state handling may be involved — check the M6 memo-cache/reconcile
design in DECISIONS.md before changing merge semantics; conformance case if any twin-visible
evaluation behavior changes (likely client-only, ctx.status precedent).
**Accept:** a regression scenario — typing during an in-flight refetch survives the reply.
**Conflicts:** `dt.ts`/`ws.ts` — do not run concurrently with B5.

### A3 — V4 login-timing side channel — DONE 2026-07-04  [S; builder sonnet; review architecture (security)]
From `docs/plans/security-review-pre-public.md` follow-up #5: unknown-username login skips
PBKDF2 → response-time username enumeration. **Fix:** verify a fixed dummy hash on the
unknown-user branch so both branches cost the same. Find the login verification in the auth
path (M-auth, `docs/plans/m-auth.md`); note login-persistence (2026-07-04) may have moved it.
**Accept:** existing auth scenarios green; a unit-level check that both branches run the hash
(timing asserts are flaky — assert the CODE PATH, e.g. the dummy-verify is invoked, not wall
time). Update the security doc's follow-up list + the security memory.
**Conflicts:** auth files — check what the login-persistence session landed before starting;
coordinate if that session is still active.

### A4 — SchemaBridge.Export dead-code check — DONE 2026-07-04  [S; builder sonnet; review skip (trivial) or architecture note]
The single-store review (2026-07-04) found `SchemaBridge.Export` (SchemaBridge.cs:196-212)
has no production caller. **Task:** verify with a full grep (including tests + launch
profiles — the M4-era `--mode Export` VS profile may reach it via Program.cs; if it does, it
is NOT dead and this task closes as "keep, documented"); if genuinely dead, delete it and its
tests. Small, honest simplification-pass item (the <3× rule cuts both ways).

---

## Track B — versioning UX (all touch `DeEnv/instances/1/app.deenv` + DesignerSteps —
run SEQUENTIALLY among themselves, in this order; each is ui+ux-reviewed with screenshots)

### B1 — Commit-detail page  [DONE 2026-07-04 — suite 716/716; ui-arch + ux SHIP; slice 9 in versioning-slices.md]
`/commits/<id>` currently re-renders the list; the history SetTable has `linked={false}` to
suppress the dead link. **Build:** a commit-detail page (route branch in instances/1's
`render()`, same idiom as `/commits`): message, at, design, parent (+ mergeParent when set),
logSeq, and the cached canonical `text` rendered read-only (a `<pre>`-ish block via existing
idioms). Restore `linked` on the history table so rows navigate to it. Ride-along cosmetics
(from the ux ledger): "(no message)" placeholder for empty messages in the label cell
(generic-table level if trivial, else page-local), humanized datetime IF a cheap generic
approach exists (else leave — it's ledgered, not required).
**Accept:** browser scenario — click a history row → detail shows the right commit; back
works. Suite green.

### B2 — Diff view between commits  [DONE 2026-07-04 — implemented as a server-backed READ builtin `sys.diffCommits` (NOT a host action — less surface, renders inline); suite 708/708; arch + ui-arch + ux SHIP; slice 10 in versioning-slices.md]
The server already computes identity diffs (`DeEnv/Designer/DesignDiff.cs`, slice 4) over two
commits' {text, idMap}. **Build:** on the commit-detail page (B1), a "changes since parent"
section rendering the diff vs `parent` (renames/adds/removes/conversions/cardinality — the
same vocabulary PublishReport uses). Needs a small server surface to EXPOSE the diff to the
designer UI — prefer a read-only host action (e.g. `sys.diffCommits(a, b)` returning the
report shape on the reply, mirroring publish's report precedent; wire lockstep ONLY if called
from Code — it will be, so all five sites like commitDesign) — flag the exact surface in the
plan-approval message to the user BEFORE building (wire-adjacent: report-on-reply precedent
is approved, but a NEW host action is a surface addition).
**Accept:** browser scenario — a rename between two commits renders as a rename (not
remove+add) in the detail view.

### B3 — Publish + dry-run from the designer  [DONE 2026-07-04 — sys.publishPreview READ builtin returns the report (kernel-wired) + preview→apply consistency guard; suite 713/713; arch + ui-arch + ux SHIP; slice 11 in versioning-slices.md]
`sys.publish` is already AST-wired and returns the structured `PublishReport` (+ `dryRun`
arg) — there is NO UI. **Build:** on the design editor (or B1's detail page — builder judges
placement with the ux idioms): a Publish affordance targeting the design's instance(s)
(resolve via `db.instances` design refs), a dry-run preview rendering the report FIRST — the
settled design demands destructive ops (removes, unconvertible, dropped reshapes) surface
LOUDLY before apply — then confirm-apply showing the same report as the result. Client Code
reads the hostAction reply's `report` — verify how replies reach Code today; if reports are
not yet Code-visible, the minimal honest surface follows the ctx.status/client-only precedent
(flag before building if a new client surface is needed).
**Accept:** browser scenarios — dry-run shows a destructive remove loudly and changes
nothing; confirmed publish applies and the report renders; a rename publish carries data
(reuses slice-4 fixtures).
**Conflicts:** `KernelHostActions.cs` (A1 first), app.deenv.

### B4 — Branch UI + lockstep wiring  [DONE 2026-07-04 — sys.mergePreview READ builtin (self-built) + createBranch/mergeBranch wired lockstep + additive scalarOf structured-arg extension; suite 718/718; arch + ui-arch + ux SHIP; slice 12 in versioning-slices.md]
`sys.createBranch` / `sys.mergeBranch` exist (slice 5) WS-op-only — the recorded rule: wire
each into HostActionScan/CodeValidator/CodeExecutor/codeExec.ts TOGETHER with its UI (the
commitDesign precedent, all five sites). **Build:** minimal branch surface — create branch
(name input) from the design editor; a branch indicator/switcher (branch working copies are
plain Design rows at their own URLs — switching = navigation, the settled model); merge
(source→target) with the MergeReport rendered (conflicts list + accessChanges must-see block
— the settled always-surfaced rule) and resolve-by-args round-trip (take-source/take-target
per conflict) as the v1 resolution UI over the existing report contract.
**Accept:** browser scenarios — create branch, edit+commit on it, merge clean, merge with a
conflict → report renders → resolve → merged. Suite green.
**Conflicts:** app.deenv + KernelHostActions + interpreters — run alone.

### B5 — Fine per-field conflict UI  [DONE 2026-07-04 — ctx.conflicts widened base/mine/theirs + <ConflictBar> per-field resolve + beforeunload guard; suite 720/720; arch + ui-arch SHIP, ux SHIP-with-1-deferred (the double-banner, user's call); slice 13 in versioning-slices.md. **TRACK B COMPLETE (B1–B5).**]
The slice-6 ledger (versioning-slices.md §6): widen `ctx.conflicts` items with
base/mine/theirs (the wire already carries them; the client drops them before Code — the
recorded surface-widening) + per-field take-mine/take-theirs; show THEIRS inline before
choosing (the choosing-blind gap — the headline obligation); disambiguate multi-object
conflicts by object label; decide the refresh-leave-guard; `<ConflictBar>` lib component for
custom renders composing the same surface. Client-only surface per the ctx.status precedent.
**Accept:** the slice-6 scenarios extended per-field; a two-field two-object conflict renders
disambiguated; theirs visible pre-choice.
**Conflicts:** ws.ts/codeExec.ts/GenericUi — run alone; A2 must land first.

---

## Track C — needs a DESIGN PASS first (do NOT hand to a bg builder as-is;
run `/design` or a planning session, then slice)

- **Semantic migrations — DONE 2026-07-05** (both slices landed + verified; ledger entry in
  `versioning-slices.md`; suite 739 after the review trims 6ca82c4). Remaining from that
  design, each with its own gate: the restoration bundle (**primitive APPROVED 2026-07-05 —
  brief below**); decimal/date/datetime migration writes (Code-runtime ceiling);
  merged-migration publishes (refused loudly).

### RESTORATION BUNDLE — sys.revertCommit + identity re-add restoration
[L; storage+kernel+designer UI; review architecture (opus) MANDATORY + ui-arch/ux for the UI
bits; suite baseline 741. RUN AFTER the designer small-slice bundle (shared app.deenv +
KernelHostActions).]
**The spec:** `docs/plans/semantic-migrations.md` — the sections "Reverting a publish — a
normal publish, not a special op" (points 1–6, incl. the G1 invariant) and "Authored revert
fns"; plus grill #2's integrated fixes. Twice-designed, user-approved (the resurrect-with-id
IInstanceStore addition approved 2026-07-05). READ IT FIRST; do not re-decide settled points.
**Deliverables:**
1. **Resurrect-with-id store primitive** (the approved interface addition): create an extent
   row with a CHOSEN id. Precedent for the shape: slice 3's server-side-only
   `DictWriteMutation` CommitMutation case (a batch-level case keeps it atomic with sibling
   writes); a standalone method is acceptable if the batch shape fights it — propose, the
   arch review judges. Constraints: the id MUST be currently free (ids are monotonic and
   never reused, so any removed row's id is free forever — assert absence across extents,
   throw otherwise); NextId never decreases; the write logs as a literal `Create(id, ...)`
   (replay already applies literal ids — no log-format change); model-terms only.
2. **`sys.revertCommit(design, commit)`** host action: constructs a REVERT COMMIT — the
   design's meta rows (MetaType/MetaProp + Design section fields) restored to the target
   commit's state WITH ORIGINAL identities (deleted rows resurrect via the primitive; edited
   rows edit back; rows added since get removed); REFUSES when `commit` is not the design's
   head's parent-line predecessor... v1 rule, exactly as specced: revert THE LAST commit only
   (target commit must be the head's parent; reverting past later commits = refused loudly —
   the staleness trap). Then the operator reviews/commits normally — sys.revertCommit
   PREPARES the working copy + auto-commits the revert commit (one atomic capture, message
   "Revert to '<msg>'"), it does NOT publish. Copies the reverted commit's `revertMigration`
   (deliverable 4) onto the new commit's `migration`. UI affordance: a "Revert" control on
   the commit-detail page, shown only where the v1 rule allows; ui+ux review it.
3. **Identity re-add restoration in the publish pipeline** (MigrationRunner/PublishReportComputer
   era): (a) `DesignDiff.PropAdd` gains its prop id + a new `TypeAdd` record (grill-verified
   additive-safe at every consumer; NOTE: TypeAdd also fixes B2's ledgered
   brand-new-type-invisible diff limitation — sys.diffCommits will now report pure type adds;
   update the affected diff-view scenario legs and the isEmpty accounting deliberately).
   (b) The backscan, EXACTLY as specced: detection by idMap MEMBERSHIP (identity N removed at
   boundary entry E iff N ∈ base(E).idMap ∧ N ∉ commit(E).idMap — NEVER pattern-match writes,
   renames emit the identical FieldWrite shape); newest-first; an entry with null BaseCommitId
   is a HARD horizon (stop + report "history unresolvable below this point" — skipping could
   restore stale); genesis/compaction horizon reported as "unrestorable (history compacted)".
   (c) Restored values pass ConvertScalar against the re-added declared type (unconvertible →
   default + report). (d) INTEGRITY PASS: restored refs to rows deleted since → drop/null +
   report (else StoredDataValidator 503s the remount); a TYPE restore verifies post-transform
   REACHABILITY of restored rows or refuses the restore (else the next GC silently sweeps
   them). (e) Restoration is LOUD in PublishReport + dry-run ("N cells restored from history").
   (f) The cross-branch identity gap (idMaps key raw ids, origins ignored) is INHERITED and
   documented, not fixed.
4. **`Commit.revertMigration text`** (the authored-reverse rider): meta-type field + a second
   collapsed textarea in the commit bar ("Revert migration") + the SAME validations as
   `migration` but pointed at the PARENT snapshot (a revert fn migrates data back TO the
   parent's schema); root commit → reject like migration. Rendered on the detail page like
   `migration`. sys.revertCommit copies it (deliverable 2).
**Scenario spine (Gherkin first, @milestone-13):** publish removes a field → users create/edit
rows under the new schema → sys.revertCommit → publish the revert → OLD rows get their exact
pre-removal values back (from the log), post-publish rows get the revert fn's definition,
user edits on surviving fields untouched; a MANUAL re-add (fresh id) starts empty (fresh-start
semantics — identity determines continuity); dangling restored refs dropped + reported;
unreachable type restore refused; revertCommit on a non-last commit refused; revertMigration
with a bad parent-schema fn name rejects at commit; a pure type-add now visible in the B2
diff view (the ledgered fix, scenario updated); dry-run shows restorations without applying.
**Process pins:** isolated worktree off LOCAL main; suite from PowerShell, Release, output
captured; `.deenv` UTF-8 no BOM, NO comments in app.deenv; no sleeps; never kill chrome by
name; success-callback consumers note: post-success DISPLAY state = top-scope + guards (see
host-action-success-signal.md's limitation section); sys.revertCommit wiring: host action +
lockstep sites ONLY if called from Code (it will be — the Revert button — so all five sites,
commitDesign precedent); ff-merge; docs sync (semantic-migrations.md status, this entry,
memory) in the same landing.

### Designer small-slice bundle — DONE 2026-07-05  [S; self-contained; suite 744]
Three small, disjoint designer items, one worktree, one landing. All touch
`DeEnv/instances/1/app.deenv` (+ DesignerSteps for scenarios); Gherkin first; @milestone-13.
1. **Commit author `by`** — unblocked by login persistence (recorded in versioning-slices.md
   slice 3's deferred list: "author `by` rides the login-persistence flip; the slice-1 log
   already records who" — the shape was NAMED in the slice-3 approved list, so the schema
   addition rides that approval; normal additive apply, slice-5 precedent). Add `by User` to
   the Commit meta-type (instances/1 app.deenv types); in `KernelHostActions.CaptureAndCommit`
   set it from the acting principal — find how the store learns "who" today
   (`StoreWriteContext`, the AsyncLocal ambient set at the WS boundary; the log entries
   already carry it) and resolve that user id to the User row (absent/anonymous → leave the
   ref empty). Render on the commit-detail page as a field line ("By" + the user's name)
   only when set — same idiom as the parent/mergeParent lines. Old commits have no `by` —
   the guard handles them. Scenario: commit as the logged-in admin → detail shows "By admin";
   the DesignCommit kernel scenarios stay green (they may commit with no principal — verify
   the empty leg).
2. **Access-section editor textarea** — B4's ledgered note (c): the designer's "Advanced
   (code)" details block has ui/common/initialData textareas (app.deenv:215-226) but NO
   `access` one, so operators cannot author access rules end-to-end. Add the fourth
   label+textarea (`class="design-access"`, `value={sys.field(design, "access")}`) — same
   idiom, one addition. Scenario: type an access rule in the editor, commit, publish to an
   instance → the rule is live on the target (reuse B4's AccessChanges fixture idea, but
   end-to-end through the textarea instead of seeding the store).
3. **B3 drift-only Apply relabel** — the ledgered fast-follow: when a publish preview's
   report has ONLY `uncommittedDrift` non-empty (no structural ops, no migrations), the
   Apply button looks like a no-op deploy. Relabel or suppress in `previewBody`: if the
   report is empty EXCEPT drift, render an explanatory line ("Working copy has uncommitted
   changes — commit before publishing" or similar honest wording) instead of/beside the
   misleading Apply. Judge exact wording against the section's existing captions. Scenario:
   preview with drift-only → the honest state renders, Apply doesn't offer a no-op.
**Process pins:** isolated worktree off LOCAL main; full suite from PowerShell, Release,
output captured; `.deenv` UTF-8 no BOM, NO comments in app.deenv; no sleeps; never kill
chrome by name; reviews: item 1 architecture (kernel touch) + ui-arch/ux ride the bundle;
ff-merge + docs sync (tick the two ledger notes) in the same landing.
**Delivered:** `Commit.by User` is schema-additive and `CaptureAndCommit` fills it from the
`StoreWriteContext` principal only when the active Commit schema declares that reference (so
test-local schemas without the field stay valid); commit detail renders "By". The advanced
editor now includes the access-section textarea (`design.access`), proven through Apply to an
instance document. Drift-only publish previews now say to commit first and suppress Apply.

### Success-signal consumers + guard tests — DONE 2026-07-05  [S-M; self-contained; suite baseline 739]
Three small, disjoint items unlocked this week — one worktree, one landing, reviews per item.
**Context docs:** `docs/plans/host-action-success-signal.md` (the callback mechanism — optional
trailing fn on every kernel host action, runs on the ok reply as a FULL handler, never on
error; the commit bar's clear-on-success at `instances/1/app.deenv` `doCommit`/`afterCommit`
is the worked example to copy); `versioning-slices.md` B3/B4 fast-follow notes.
**Delivered:** the publish and merge confirmations use plain editor UI vars set by the success
callbacks; the suggested per-row component-state variant was skipped because host-action success
resets row component slots. The merge-preview guard asserts the still-open preview recomputes to
"Already up to date" after apply, which is the actual stale-cache surface in this UI.
1. **B3 "Last published" line** — in the publish UI's component (`publishRow`/the publish
   preview component in `instances/1/app.deenv`), pass a callback to `sys.publish(...)` that
   sets a component-state var (e.g. `state.lastPublishNote = "Published to " + inst.name`);
   render it as a confirmation line near the Apply control (mirror the commit bar's
   "Last commit:" idiom). Component state = `var state = {...}` INSIDE the component fn,
   which MUST be invoked in TAG form (`<publishRow ...>` — it already is): plain fn calls
   memoize the whole body and wipe state on any invalidation. app.deenv has NO comment syntax.
2. **B4 "Merged X into this design" line** — same pattern on the merge apply
   (`sys.mergeBranch(source, target, resolutions?, callback)` — callback is always LAST;
   the executor type-disambiguates fn-vs-array in third position). Also add the ledgered
   guard scenario: re-open the merge preview AFTER a merge apply and assert it recomputes
   (pins the `mergePreview:` cache-drop — B4 note (b)).
3. **The G1 boundary-or-rebaseline invariant guard test** (from the semantic-migrations
   design's grill #2 — `docs/plans/semantic-migrations.md` §5 of the revert section): pin
   with scenarios that "every path that removes schema-shaped data either logs a boundary
   entry or re-baselines the whole log": (a) versioned publish with a field removal →
   boundary entry present, log/genesis INTACT; (b) `setDesign` (the IDE's Apply) applying a
   field removal to a stamped instance → log + genesis GONE (the re-baseline — assert the
   files' absence, i.e. the CURRENT honest behavior, so any future gentler replacement that
   forgets to log removals FAILS this test and must handle restoration-staleness consciously).
   Kernel-level scenarios (Publish.feature / a kernel feature), no browser needed.
**Scenarios first (Gherkin), @milestone-13.** Items 1-2 are rendered-UI → screenshot-verify
yourself + reviews ui-architecture + ux; item 3 → architecture note or skip (test-only).
**Process pins:** isolated worktree off LOCAL main; full suite `dotnet test DeEnv.Tests -c
Release` from PowerShell, output captured to a file, read on failure; `.deenv` UTF-8 no BOM;
NO fixed sleeps; never kill chrome by name (testhost / `*ms-playwright*` only); commit on the
branch, ff-merge; docs sync (tick the B3/B4 fast-follow ledger lines) in the same landing.

### Migrations slice 2 — EXECUTION — DONE 2026-07-05 (suite 737)
[L; interpreter+storage+kernel; review architecture (opus) MANDATORY; suite baseline 729]
**The spec is `docs/plans/semantic-migrations.md` §3 (scope wiring — the StoreDoc↔ExecObject
seam, steps 1–5) + §4 (collapse–step–collapse range walk) — READ THEM FIRST; they are
twice-grilled and user-approved; every decision below is pinned there. Do not re-decide.**
**Deliverables:**
1. **Internal in-memory ctor** `JsonFileInstanceStore(StoreDoc, InstanceDescription)` — no
   file paths, no boot reconcile, `Save()` throws loudly. Grill-verified viable: the read path
   (`ReadNode`/`ReadById`/`ReadExtent`/`ResolveRef`/`BuildObject`) needs only `_doc`+`_desc`+
   `_resolver`+`_sync`. This is the ONLY sanctioned store view for migration worlds — NEVER
   open a second file-backed store (the one-store-per-file kill class, 938b678).
2. **`TransformDoc` refactor**: split `ApplyPublishBoundary` into a pure per-segment in-memory
   transform (structural passes + writes list + `doc.NextId` threading) and the final
   ONE-entry + log-first `SaveRaw` bracket (slice-1 WAL law; one `Version` bump; `Seq==Version`).
   k=0 must remain byte-equivalent to today's single-shot publish — one code path, no drift.
3. **Migration runner** (new file in `DeEnv.Code` beside DbBridge — Code imports Storage,
   Storage must NEVER import Code): per step — OLD world = pre-step doc + the PREVIOUS
   endpoint commit's cached `text` → desc, wrapped in the in-memory ctor, loaded ONCE via
   `DbBridge.LoadRoot` → `oldDb` root + an id→ExecObject index; NEW world = the SAME
   (post-`TransformDoc`) doc + the step commit's desc, loaded the same way. For each migration
   fn (= type name in `Commit.migration`, parsed like slice 1's `ValidateMigration`), for each
   object in that type's NEW extent: `CodeExecutor.InvokeFunction(fn, [oldById[id]],
   scope items {new: newObj, oldDb: oldRoot}, bare ExecContext)` (scope ITEMS, not ambient —
   bare context ⇒ writes land in `ExecObject.Props` in place). Iteration is EXTENT-based.
4. **Harvest** (ExecObject → StoredValue): per migrated object, snapshot scalar props before
   the fn, diff after; each change checked against the STEP schema's declared prop type —
   **v1 write surface = Int/Text/Bool ONLY** (no ExecDecimal exists; decimal/date/datetime
   live as ExecText and would 503 the target at remount): a write landing on a
   decimal/date/datetime prop, a wrong-typed scalar, or ANY non-scalar (set/ref/dict/object)
   **aborts the whole publish loudly at harvest** — never deferred to StoredDataValidator.
   Accepted change → `ExecToScalar` → `StoredLeaf` → `FieldWrite{old,new}` appended to the
   SAME writes list + the doc mutated in place. Dict rule: a fn writing a dict prop aborts
   ("dictionary migration not supported yet"); READING dicts off old/oldDb is fine. Writes to
   OLD-world objects are discarded silently (by-discard, the named v1 ceiling — no guard).
5. **Range walk in `Publish`** (KernelHostActions, versioned path only): first-parent chain
   head→stamped via `parent` refs (NEW walk — `AncestorsOf` gives the DAG set, not the chain;
   both needed) + DAG ancestors via parent+mergeParent. Steps = chain commits (exclusive of
   stamped) with non-empty `migration`, chronological. REFUSE loudly before any work when:
   a migration-carrying commit is in the DAG range but NOT on the first-parent chain ("merged
   migration — not supported yet"); or stamped is unreachable from head AND any head-ancestry
   commit carries a migration ("cannot establish a migration path"). Execution: `prev =
   stamped; for each step Mi: TransformDoc(diff(prev→Mi)) → run Mi's fns → prev = Mi;` finally
   `TransformDoc(diff(prev→head))`, no fns. All diffs over cached `{text, idMap}`. ONE
   boundary entry: ALL segments' writes concatenated in execution order,
   `BoundaryMarker{designId, headCommitId, baseCommitId: stampedCommitId}` — byte-compatible
   with slice-7 era resolution; intermediate step states are NOT time-travel-addressable.
6. **Re-publish crash guard**: before executing, read the target log's LAST boundary marker;
   if it already names `{designId, headCommitId}` → skip transform+fns, just re-stamp + report
   (closes the crash-after-entry-before-stamp re-run window; the structural re-diff was
   already inert, migrations are NOT).
7. **Report + one-code-path dryRun**: `PublishReport` gains `migrations:
   [{commitId, message, types, objectsMigrated}]` per step + the abort error shape; `dryRun`
   runs the FULL pipeline INCLUDING fns on the throwaway doc and discards. CRITICAL
   INTEGRATION: B3's `sys.publishPreview` READ builtin ships the report to the designer UI and
   has a preview→apply CONSISTENCY GUARD test — the preview must flow through the SAME
   pipeline (fns included) or that guard breaks; verify how publishPreview reaches Publish's
   compute and keep them one path. The unstamped/no-commit fallback paths run NO migrations;
   the legacy unstamped-WITH-data fallback reports `migrationsSkipped: true` loudly when
   head's ancestry carries migrations.
**Scenarios (Gherkin FIRST, `Publish.feature` + slice-4 fixtures, @milestone-13):** a
rename+compute migration carries data through a publish (the §3 example shape:
`new.total = old.net + old.tax` on ints); a fn throw aborts atomically (target files
byte-untouched, no entry, no stamp); a harvest type-violation aborts loudly (fn writes text
into an int prop); dryRun previews migrations without applying (report shows them, disk
clean); a merged migration refuses; a fn writing a dict prop refuses; the crash-window
re-publish re-stamps WITHOUT re-running fns (seed: entry present, stamp absent);
multi-commit collapse–step–collapse (two migration commits + structural commits between =
two steps, one entry, correct final data).
**Process pins:** isolated worktree off LOCAL main; full suite `dotnet test DeEnv.Tests -c
Release` from PowerShell (solution `DeEnv.slnx`; subset = `-- --treenode-filter
"/*/*/<RealClass>/*"`), output captured to a file, read on failure; `.deenv` = UTF-8 no BOM;
app.deenv has NO comment syntax; NO fixed sleeps (poll); never kill chrome by name (testhost /
`*ms-playwright*` only); no conformance case (kernel-side C#-only execution — the floor-
condition classification, spec §3); commit on the branch, ff-merge to main; architecture
review (opus) with the diff PINNED to the branch's merge-base before landing.
- **Compaction** — `sys.compact(instance, horizon)` (§6): fold-to-new-genesis, cache
  promotion, retention knobs, never-compact designer default; interacts with time-travel's
  era resolution (the recorded residual: promoted checkpoints become the era source).
- **Non-temporal field flag** (§0b): PII/erasure + high-churn exclusion — schema surface
  decision (a per-field flag = authoring surface, minimal-by-default scrutiny).
- **Dict READ floor** — gated on how the login-persistence world shakes out; revisit with
  the security follow-ups.

## Explicitly parked (do not schedule)
Deploy to the box + devlog dogfood (user-skipped 2026-07-04); M12 visual designer (post-MVP);
real-time/live-push (deliberate future milestone — the M13 log is its substrate when its day
comes); per-verb sys granularity; the global banner vocabulary cosmetics unless a Track B
task touches that exact string anyway.

## Suggested execution order
1. A1 → A4 (tiny, parallel-safe with each other and with A2/A3 in separate worktrees).
2. A2 + A3 in parallel with Track B start.
3. B1 → B2 → B3 → B4 → B5 strictly sequential (shared designer files).
4. Track C design passes whenever a chat session wants a thinking task rather than a build.
