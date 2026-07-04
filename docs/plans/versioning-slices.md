# App versioning (M13) — slice plan

Session-5 slicing of `app-versioning-design.md`, 2026-07-03. That doc holds the design and the
reasoning; this one holds the sequenced work list, the per-slice decision gates, and status.
Tag: **`@milestone-13`** (precedent: `Concurrency.feature`); `@multi-user` only on scenarios whose
point is cross-session. One feature file per capability (`AppLog.feature`, `DesignCommit.feature`,
`Publish.feature`, `DesignMerge.feature`, `DataConflict.feature`, `TimeTravel.feature`).

## Sequence

0. **baseVersion anti-clobber guard — DONE** (main `4c72a92`; design doc §0). Pulled ahead of the
   milestone as a bug fix.

1. **Append-only log behind the store (invisible) — DONE 2026-07-03** (main `1961d10` +
   review fix `403868a`; suite 628/628; architecture review SHIP-WITH-FIXES, the one fix-soon
   finding fixed + regression-tested both directions). Every `JsonFileInstanceStore` mutation
   appends one changeset entry (post-remap real ids, old+new per field write, msgId, who, when,
   seq, nextId) to `instances/<id>/app-data.log.jsonl` under the same `_sync` lock, fixed crash
   order append→snapshot; `app-data.genesis.json` frozen at first mutation; boot replays a
   lagging snapshot tail (torn final line repaired); fsck invariant
   `replay(genesis→head) == app-data.json` via order-independent structural compare. Also rebuilds
   the guard's in-memory `_objectVersions` from the log on boot — closes §0 residual 2, INCLUDING
   set-link/unlink member advances (the review finding: a member whose last change was a set link
   could be stale-clobbered across a restart). Server-only C# — no wire, no TS twin, no UI.
   Defers: checkpoints/compaction, non-temporal field flag, any reader of the log.
   Decisions taken: genesis freezes at first mutation (not ctor — read-only boot never writes);
   who/msgId reach the store via an `AsyncLocal` ambient set at the WS message boundary (no
   `IInstanceStore` signature change; bare set-then-call would race across sessions); ONE log entry
   per store commit gated on version-change not pending-writes (no-op removals bump the version —
   they log an empty-writes entry so seq==version stays exact); GC-swept extent removals + dict
   mints MATERIALIZED into entries (replay is literal — no GC/mint/resolve at replay, so old logs
   replay identically under future code); filenames derived by suffix from the data file (bare
   temp-file stores can't collide); Reset()/a rewriting MigrateTowardSchema pass re-baseline
   (delete log+genesis — history resets at schema apply until slice 4's boundary entries, the
   ponytail'd ceiling).

2. **Per-commit caches: canonical printed text + name-path→id map — DONE 2026-07-03** (main
   `5f9cfbc` + review fix `92d3777`; suite 634/634; first Sonnet-5 architecture review under the
   new tier policy — SHIP-WITH-FIXES, graded at the Opus bar and PASSED). `SchemaBridge.Snapshot(design)`
   → `DesignSnapshot { Text, IdMap }`: Text = the existing validated `ProjectDesignDocument`
   (computed first — an invalid design throws before any map exists); IdMap = the same
   types/props walk keeping each set-member's intrinsic id (`"Type"` / `"Type.prop"` → row id).
   Review finding fixed: the map now emits prop entries only for non-enum bases, mirroring the
   projection's enum branch — an object type flipped to enum keeps leftover `props` members in
   the store that vanish from the printed doc, and the map must key exactly what the document
   shows (regression scenario both-directions verified). Pure builder, no persistence — slice 3
   stores both fields on the Commit row. Rename-keeps-identity and delete+re-add-differs are
   directly scenario-tested against store-minted ids. Depends on 1. ✔

3. **`Commit`/`Branch` rows + `sys.commitDesign` + the authority inversion — DONE 2026-07-03**
   (main `96fb793` + review fixes `3b23e05`; suite 645/645; Opus review — escalated per the tier
   policy's security carve-out — SHIP-WITH-FIXES, all three findings fixed + both-directions
   verified). What landed: the approved meta-types + `db.commits`/`db.branches` root sets in the
   designer's app doc, with `create edit delete where false` deny rules (the floor is
   allow-when-unruled — the "deny-by-default" shorthand below was wrong, explicit rules are the
   mechanism); `sys.commitDesign(design, message)` = optimistic capture loop (version bracketed
   around the snapshot read, bounded retry) + ONE atomic `CommitBatch` (the `CommitMutation` union
   gained a server-side-only `DictWriteMutation` case — user-approved interface extension — so
   commit row + refs + db.commits link + idMap entries + head advance are a single WAL write: one
   `sys.commitDesign` = exactly one log entry, scenario-proven); the AUTHORITY INVERSION per the
   six invariants (no boot `Reset`, adopt-once via `DesignerSeed.AdoptInto` live writes with a
   design-id remap into the instances mirror, `db.instances` reconciled every boot,
   `EnsureMainBranches` idempotent on both paths, fresh install unchanged; `DesignerSeed.Merge`
   deleted as dead). Review findings fixed: (1) client dict-WRITES now floor-gated
   (`RequireDictWrite` — sealed the WRITE half of the security review's open dict-floor gap for
   ALL ruled types; read side stays deferred); (2) post-adopt registry id remap (a stale
   `Instance.design` ref would 503 the designer on next boot — now impossible, scenario-proven);
   (3) the single-batch atomicity above. Scope line flagged for the future Commit-button slice:
   `sys.commitDesign` is WS-op-only — wiring it into HostActionScan/CodeExecutor/codeExec.ts must
   happen in lockstep across all of them. Named residual (crash-durability class, deferred): a
   crash between `AdoptInto` and the registry rewrite re-adopts the file as a duplicate on next
   boot. Adopted designs get NO baseline commit (head empty until first commit) — slice 4 decides
   if it wants one.
   **GATE RESOLVED — user approved the FULL authority inversion 2026-07-03:** design-data + its
   commit history become the source of truth; `app.deenv` becomes a publish artifact (written BY
   publish); boot-sync (`KernelHost.SyncDesignHost` / `DesignerSeed.Merge`) is demoted to one-time
   adoption and stops overwriting the `Design` row thereafter. (Side effect: the designer's data
   log stops being truncated at every boot — slice 1's Reset-on-boot-sync caveat ends here.)
   **Shapes APPROVED (user 2026-07-03, lean-per-slice + caches-on-row):**
   `Db` gains root sets `commits set of Commit` + `branches set of Branch` (GC-pins history incl.
   abandoned tips; history UI = free SetTable filtered by design, ordered by logSeq).
   `Commit { message text, at dateTime, design Design, parent Commit (empty = root), logSeq int,
   text text (cache: canonical printed doc), idMap dictionary of int (cache: name-path → lineage
   id) }`. `Branch { name text, head Commit, workingCopy Design }` — "main" seeded per design at
   adoption; at slice 3 workingCopy IS the design row. Created only via
   `sys.commitDesign(design, message)` under the store lock; immutability = explicit
   `create edit delete where false` rules (the floor is allow-when-unruled, so "no grants" alone
   would NOT deny — corrected at build time).
   Deliberately deferred to their slices (normal additive apply): `mergeParent` + `origin int` on
   MetaType/MetaProp (slice 5), `migration` (slice 4), author `by` (rides the login-persistence
   flip; the slice-1 log already records who). Slice-5 revisit flagged: app-identity vs
   working-copy anchoring once branches clone Design rows. Depends on 1.

4. **Structural diff + forward publish — DONE 2026-07-03** (main `e5c2566` + review fixes
   `7b7afbd`; suite 668/668; Opus review BLOCK → all findings fixed, both-directions verified).
   THE MVP PAYOFF LANDED: a rename carries data through a deploy. Shipped: `DesignDiff`
   (endpoint identity-join over two commits' {text, idMap}; rename vs remove+add told apart by
   id; honest double-report for renamed+retyped props); `ApplyPublishBoundary` — offline apply
   appending ONE boundary-marked entry (`LogEntry.Boundary{designId, commitId}`, materialized
   literal writes; type rename = same-id Remove+Create + ref refresh; genesis preserved; fsck +
   cross-boundary replay proven); baseline commits at adoption + instance stamps via registry
   `publishedCommitId` (canonical-match; one-time name-match fallback for unstamped instances);
   publish = the design's HEAD commit only, drift reported; structured `PublishReport` +
   `dryRun` on the hostAction reply (the approved wire gate); stale drafts reject via the
   existing baseVersion guard. Review findings fixed: (1) WAL INVERSION — the apply wrote
   snapshot-before-log (a deploy-time crash would brick the instance at boot); swapped, and the
   new crash-window scenario forced out a coupled ctor flaw (boot validated the snapshot against
   the NEW schema before tail-replay — now reconcile-then-validate, so publish-crash recovery
   works across schema boundaries); (2) unsupported cardinality reshapes now DROP-to-new-shape-
   default + report `unsupported`+`dropped` (was: unloadable store reported as applied; the old
   value stays recoverable from the log); (3) a parse-every-committed-app-doc guard test (closes
   the parallel-branch skew class a review artifact exposed). Residuals recorded: design-doc §4's
   lock/reject-commits/queueing step DEFERRED (inherited single-operator concurrency; comment at
   the call site); publish-crash-before-stamp re-derives an inert re-diff (guarded; explicit
   idempotence test when crash-recovery hardens). The sequencing note is SATISFIED: boundary
   entries exist, so the later `Commit.migration` designer-schema addition is safe. Semantic
   `fn migrate` = a later slice. ✔

5. **Branches + origin-keyed three-way merge — DONE 2026-07-03** (main `4aed141` + review fix
   `7c93b6f`; suite 678 → landed at 682 with the parallel XSS-guard tests). V1 contract
   (user-approved): report + resolve-by-args — clean merges auto-commit a two-parent merge
   commit; conflicts = NO writes + a structured `MergeReport` (content-derived stable conflict
   ids); re-run with `resolutions: [{id, take: source|target}]`; the interactive ctx-staged UI
   comes with the conflict-UI work. Shipped: `sys.createBranch` (clone-with-FLATTENED-origins —
   clone.origin = source.origin ?? source.id, so N-deep branches anchor to original rows; clones
   join `db.branches` only, never `db.designs`); `sys.mergeBranch` (max-logSeq LCA over
   parent+mergeParent; lineage-keyed 3-way per meta-field with the settled policies — `order`
   renormalizes on the target spine, never conflicts; existence rules incl. delete+modify;
   whole-fn code merge by name; initialData whole-section; access rule-granular union with the
   ALWAYS-surfaced accessChanges block; drift refusal — merge computes over committed heads;
   cross-app refusal); resolutions thread INTO the compute (resolved values feed downstream
   decisions). `commitDesign` widened to commit on any branch's working copy (was main-only —
   a slice-3 coherence gap the builder caught). Review (orchestrator-run in-context — the
   reviewer classifier was down; verdict SHIP-WITH-FIXES): `CreateMergeCommit` lacked the s1/s2
   capture bracket → both commit paths now share ONE `CaptureAndCommit` helper
   (consistency-by-construction); accepted+documented: the two-batch clone GC window (forced —
   a fresh Design's set ids don't exist pre-batch; failure = clean refusal + inert orphans) and
   first-merge section-text canonicalization (the Code language has no comments to lose; printer
   canonical → byte-stable thereafter). Meta-schema additions: `origin int` ×3 + `mergeParent`.
   Deferred as settled: branch deletion, rebase/cherry-pick, recursive LCA, fn identity. ✔

6. **Data conflict payload + coarse UI — DONE 2026-07-04** (main `3e25bc3` + `a80dbe0` +
   `e34e401`; suite 694/694 over the combined tree with login-persistence slice 1). What landed:
   **field-level overlap analysis** — a stale-based commit whose fields are DISJOINT from the
   interleaved changes now AUTO-MERGES (apply, not reject — the designed no-retry-storm
   behavior); only same-field collisions reject, carrying the approved wire payload
   `conflicts: [{object, field, base, mine, theirs}]` with bases read from the slice-1 log
   (first-interleaved-write's old); sets/different-dict-keys commute; creates never conflict;
   multi-object batches stay all-or-none. Client: `ctx.conflicts` + `ctx.keepMine()` /
   `ctx.takeTheirs()` (client-only, the ctx.status precedent — C# twins are fixed constants, no
   conformance case, verified). Generic form renders the coarse bar (names the conflicted
   fields, draft kept); keep-mine = consent-framed force re-commit; take-theirs = drop + refetch
   with an "Updated to latest" confirmation; Save/Discard HIDE while the bar owns the decision
   (plain Save was a hidden force — ux finding); the global banner clears on resolution + any
   successful ack (was stale post-resolution — ux blocks-landing finding; the unconditional SET
   stays, it is load-bearing for no-silent-clobber); Take-theirs = solid safe-primary, Keep-mine
   = outlined danger; custom renders fall back to the action-first global banner (no app can
   silently clobber). Build saga worth remembering: the first full-suite run "failed 54" — a
   two-tab hold-and-wait deadlock on the test pool's 4 page permits (slice 6 took two-tab
   scenarios from 1 to 4), proven starvation-free-fixed via the project's ParallelLimiter idiom
   (`TwoTabScenarioLimit`), NOT a ws.ts race; the builder initially misdiagnosed it as
   environmental — falsified by a settled-machine rerun. Three-lens review (arch SHIP /
   ui-arch SHIP / ux SHIP-WITH-FIXES, all findings fixed + both-directions verified).
   **FINE-SLICE OBLIGATIONS LEDGER — ALL MET in B5 (slice 13, 2026-07-04):** show `theirs` inline
   before choosing (today the operator chooses blind — the payload carries it, the client drops
   it before Code: widen `ctx.conflicts` items with base/mine/theirs + per-field resolve when
   `<ConflictBar>` lands); disambiguate multi-object conflicts by object (today two `title`s
   render as "Title, Title"); decide the refresh-leave-guard (draft silently lost on F5 —
   accepted coarse limit for now); double-banner-during-conflict is DELIBERATE (not a bug);
   Take-theirs int-tagging of wire scalars is a documented display-transient seam if the fine
   UI ever reads `theirs` without a refetch. Log-read cost on the stale path = whole-file,
   honestly ceilinged (in-memory tail when it ever gets hot). ✔
   keep-mine / take-theirs. First slice to touch wire + TS twin. GATE: conflict payload wire shape.
   Depends on 1.

7. **Time-travel clones — DONE 2026-07-04** (main `719a2cf` + review fixes `c82855d`; suite
   704/704 over the combined tree with the designer auth gate). `cloneInstance(id, atSeq)`:
   materialize = genesis + fold of the ONE shared `AppLogReplay.Apply` up to atSeq (agrees with
   fsck at head, scenario-proven); era-schema resolution via boundary markers —
   **`BoundaryMarker` gained `baseCommitId`** (the commit the publish migrated FROM, known at
   publish time) after the Opus review proved the initial parent-walk heuristic wrong for
   multi-commit gaps (deployed C1 ← C2 ← C3-published: parent says C2, truth is C1 — the
   scenario now exercises the multi-gap both directions); a boundary naming an unresolvable
   commit FAILS LOUDLY (never silently serves the current doc; compaction's promoted
   checkpoints become the era source when §6 lands — recorded residual); clones are forks with
   fresh history (no log/genesis copied; slice-1 adoption on first mutation); era-stamped
   `publishedCommitId` so future publishes diff from the right anchor; atSeq-absent =
   byte-identical old clone; invalid atSeq creates nothing (registry + filesystem byte-clean).
   §5's history-read gate = the existing `sys` rule for v1 (per-field floor-over-history rides
   the future history-browsing UI). Builder-caught extras: era-resolution reads use a FRESH
   design-host store (the boot-cached staleness class), and it DISCOVERED (not fixed) the
   severe pre-existing mirror-clobber bug below. ✔

   **THE MULTI-STORE CLASS — FIXED 2026-07-04** (main `938b678` + `6cd9f74`; suite 708/708;
   Opus review SHIP). Discovered during slice 7; pre-existing since the inversion. The class:
   multiple `JsonFileInstanceStore` instances over ONE file (boot-cached mirror store, live
   hosted store, fresh per-call host-action stores) — each with its own lock/version/`_doc`.
   BOTH members proven: the mirror-clobber (boot → commitDesign → clone×2 silently deletes the
   commits) AND the sibling (commitDesign → any live designer edit → the commit is clobbered
   off disk; would also mint colliding WAL seqs). Fix: **one store per data file in the
   kernel** — the design host's live `HostedInstance.Store` is THE store (KernelHostActions
   takes a `resolveStore` Func; the boot path is a sequential dead-hand-off, verified safe by
   construction; `FreshDesignHostStore` deleted; era/clone reads share the live store). Bonus
   guard from the review's open question: **the design host can never be its own publish
   target** (a raw publish(design, 1) would have rewritten the designer's own schema —
   scenario-pinned). Named residual: cross-call read atomicity on a live source during clone
   = the deferred concurrency class (comment honest now). `SchemaBridge.Export` was deleted by A4.

8. **The Commit-button UX slice — DONE 2026-07-04** (main `cc9d17a` + ux fixes `19c103f`; suite
   715/715; ui-arch review SHIP — lockstep sweep clean at all five sites; ux SHIP-WITH-FIXES,
   all applied). `sys.commitDesign` wired LOCKSTEP (HostActionScan + CodeValidator arity +
   CodeExecutor ExecNothing + codeExec.ts execCommitDesign, mirroring publish exactly;
   host actions are outside the conformance contract by documented precedent;
   createBranch/mergeBranch deliberately stay WS-op-only until their UI). Designer UI (Code
   only): commit-bar (message input + Commit + History link) on the design editor; a
   "Last commit: …" confirmation line reading the branch head (refetch updates it = positive
   confirmation; the input is NOT cleared, so a rejected commit retains the typed message);
   /commits = the free generic SetTable, newest-first (orderBy 0−logSeq), `linked={false}`
   (no dead row-links until a detail page exists). ✔

**THE MILESTONE'S CORE + UX SURFACE IS COMPLETE (slices 1–7 + the single-store fix + the
Commit button, 2026-07-03/04, suite 628→715).**

9. **B1 — Commit-detail page — DONE 2026-07-04** (Track B of the post-M13 backlog; suite
   716/716; ui-arch + ux reviews SHIP). `/commits/<id>` route branch in the designer's
   `render()` → `commitDetailPage` (mirrors `instanceSelectorPage` segment-for-segment:
   `sys.toInt(sys.segment(path,2))` resolve, `db.commits.any` guard, `.not-found` + Back leg)
   → `commitDetail(c)` renders message / at / design / parent (+ mergeParent when set) / logSeq
   as a read-only field list, plus the cached canonical `text` in a height-capped `<pre>`.
   The history `<SetTable>` is re-LINKED (`linked={false}` removed) so rows navigate to their
   detail page; parent/mergeParent are links to their own detail pages. Generic ride-along in
   the stdlib SetTable: the label cell renders a humanized `"(no <labelProp>)"` placeholder for
   an EMPTY label WHEN linked (only the linked `<a>` branch — the non-linked `<span>` branches
   are byte-for-byte unchanged), killing the phantom-empty-anchor that restoring `linked` would
   otherwise reintroduce. ux fix applied: `.commit-text` capped at `max-height: 24rem` so a long
   snapshot doesn't strand the metadata above a scroll-wall. ✔

10. **B2 — "Changes since parent" diff view — DONE 2026-07-04** (Track B; suite 708/708;
   architecture + ui-arch + ux reviews all SHIP after one ux fix). A new server-backed READ
   builtin **`sys.diffCommits(from, to)`** — NOT a host action; modeled exactly on
   `sys.schema`/`sys.canRead` (server computes + memoizes, the cache entry ships to the client,
   which reuses it or VNAs→refetches on a miss). Renders inline on SSR AND hydrates/refetches on
   client-side nav — no async reply, no wire payload. Keyed `diffCommits:{fromId}:{toId}` (both
   twins mint the SAME key off the commits' intrinsic ids). LAYERING: `DeEnv.Code` still never
   references `DeEnv.Designer` — the executor takes an injected `Func<ExecObject,ExecObject,
   ExecContext,IExecValue>` delegate; `SsrRenderer.BuildCommitDiffReport` (where Designer is
   visible) reads each commit's cached `text`+`idMap` off the object, runs `DesignDiffer.Compute`,
   and builds the report as a Constant, distinct-negative-id object tree (so `ClientState` ships
   it whole and its dedup doesn't collapse it to id=0 — the one real subtlety, cached DIRECTLY like
   sys.schema since Memoize's factory guard refuses a fresh negative-id object). NO conformance
   case (server computes, client only reuses — same as sys.schema/canRead). Report reuses
   PublishReport vocabulary (renames from/to, adds/removes as `Type.prop` paths, conversions +
   cardinality path/from/to), omitting publish-only boundary fields. UI: a "Changes since parent"
   section in `commitDetail` (only when `c.parent != null`; "No parent to compare." / "No
   structural changes." for the degenerate cases). ux fix: removes = red `--danger` (always
   destructive); retypes + cardinality = amber `--warn` (may lose data) — distinct from
   always-safe renames/adds. LIMITATION (ledgered, inherited from DesignDiff's migration lens): a
   BRAND-NEW type is invisible in the diff (nothing to migrate) → a pure type-add reads as "No
   structural changes"; fixing needs a `TypeAdd` in DesignDiff, which also touches publish — out of
   B2 scope. A commit whose cached text somehow fails to parse blanks the WHOLE detail page (SSR
   catch-all, logged not swallowed) — defensible: the cache is built from an already-validated
   design at commit time. ✔

11. **B3 — Publish + dry-run from the designer, with a preview→apply consistency guard — DONE
   2026-07-04** (Track B; suite 713/713; architecture + ui-arch + ux reviews all SHIP after fixes).
   The design editor's Publish surface: per instance running the design, a toggle-gated Preview of
   the dry-run report (destructive ops LOUD) → Apply. **`sys.publishPreview(design, targetId)`** is a
   server-backed READ builtin that RETURNS the `PublishReport` (like B2's `sys.diffCommits`, NOT a
   host action — the user pushed for "the fn returns the data" over an async holder). Its delegate is
   KERNEL-wired (the preview needs the target instance's DataPath/stamp/live store, which the
   designer's render can't reach) and threaded `KernelHost.PublishPreviewFor` → `HostedInstance.Start`
   → `InstanceApp` → `ContentHandler`/`WsHandler` → `SsrRenderer` → `CodeExecutor`. The dry-run compute
   is EXTRACTED into `PublishReportComputer`, shared with `sys.publish` (apply) so preview/apply can't
   drift; `PublishReportCode` builds the report Code-value in the kernel (avoids an Http→Kernel cycle).
   No conformance case (server computes, client reuses). **Preview→apply consistency GUARD** (closes
   the TOCTOU the preview/apply split opens): the report carries `{targetCommit=design head,
   targetVersion=target store version}`; Apply = `sys.publish(design, targetId, expectedHeadCommit,
   expectedTargetVersion)` (optional guard args — 2-or-4 arity, disambiguated from the legacy 3-arg
   `dryRun` by arg COUNT; `CodeValidator` arity map generalized to `int[]`). The server refuses a stale
   apply ("changed since the preview") — a review caught the guard running AFTER the destructive
   boundary write (`Compute(dryRun:false)` materializes as a side effect); FIXED to check BEFORE any
   materialization + regression-tested (a rejected apply appends ZERO log entries). UI: per-instance
   reactive preview component, destructive report reusing B2's groups via a shared `opGroup` helper,
   `ConfirmButton` + danger-colored confirm on a destructive apply, a section caption. FAST-FOLLOWS
   (ledgered below): post-apply success signal; drift-only-Apply relabel; "Cardinality" humanize. ✔

12. **B4 — Branches + merge from the designer — DONE 2026-07-04** (Track B; suite 718/718;
   architecture + ui-arch + ux reviews all SHIP after fixes). The design editor's Branches
   section: create a branch (`sys.createBranch`), branch links (branches are Design rows at their
   own `/designs/<id>` URLs — switching = navigation), and MERGE. **`sys.mergePreview(source,
   target)`** = a server-backed READ builtin returning the existing `MergeReport` (like B2's
   `sys.diffCommits` — SELF-CONTAINED on the designer store, self-built `SsrRenderer` delegate, NO
   kernel wiring). `KernelHostActions.ComputeMergePlan` extracted as the shared merge-compute
   (preview + apply can't drift — DesignMerge 10/10 proves byte-identical); `MergeReportCode` builds
   the report Code-value in the kernel. `sys.createBranch`/`sys.mergeBranch` wired into Code
   lockstep (commitDesign 5-site precedent). **One additive wire touch:** `scalarOf` (ws.ts) now
   recursively serializes array/object-of-scalar host-action args, so `mergeBranch`'s
   `resolutions:[{id,take}]` crosses natively (server + existing tests unchanged; other host-action
   arg sites reject non-scalars via LeafForType, so no leak). UI: toggle-gated preview
   (drift/no-op/clean/conflicts with base + source/target values named by branch), an always-shown
   AccessChanges must-see block, Apply gated until every conflict resolved. Coarse take-source/
   take-target v1 (fine per-field UI = B5). Reviews caught + fixed: the `mergePreview:` cache not
   dropped on apply (the B3 publishPreview bug class); drift/pick-button/conflict-header string
   clarity; branch-name input clear. **FRAMEWORK CONSTRAINT (load-bearing, ledgered):** merge picks
   live in module-level `ui var`s (`previewingBranch`, `mergePicks`), NOT reactive component
   `state` — the client-data-layer refetch CANNOT round-trip client-only collections (slotState
   THROWS on an array in component state / drops nested draft arrays; `sessionVars` doesn't ship
   arrays either). Module vars in the render-fn scope are the framework-sanctioned home for
   client-only collection input; picks stay client-authoritative and re-key off fresh report ids.
   (rule-12-clean: picks are operator INPUT, not server data.) One-preview-at-a-time (scalar
   `previewingBranch`) is intentional. ✔

13. **B5 — Fine per-field DATA-conflict UI — DONE 2026-07-04** (Track B, the LAST slice; suite
   720/720; architecture + ui-arch reviews SHIP, ux SHIP-with-one-deferred-finding). The slice-6
   coarse conflict banner (named fields, whole-ctx keep-mine/take-theirs, operator chose BLIND)
   becomes the fine per-field UI. **`ctx.conflicts` items WIDENED** to `{object, typeName, field,
   base, mine, theirs}` — the wire (`WireConflict`) already carried them (slice 6); the client just
   stopped dropping them (`conflictItem` in ws.ts, via `wireScalarToExec`). **`<ConflictBar>`** stdlib
   lib component (composable by custom renders like SetTable): groups conflicts by object (`TYPE #id`
   — always-correct vs resolving a label an object may not have in readable extent), shows MINE vs
   THEIRS INLINE per field (the headline — no more blind choosing), per-field Keep-mine/Take-theirs
   (`ctx.resolveField(object, field, take)`) + whole-bar "Keep all"/"Take all" shortcuts. **Per-field
   resolution SHRINKS `ctx.conflicts`** (take-theirs writes theirs to obj.props + removes the item;
   keep-mine removes it; empty → `resend()` at the fresh base — take-theirs fields no-op, keep-mine
   fields force) — deliberately avoids a client-only picks COLLECTION (the B4 slotState constraint).
   `beforeunload` guard (one idempotent listener while conflicts outstanding) so an F5 no longer
   silently loses the kept draft. Client-only, NO conformance (ctx.status precedent — `resolveField`
   is a C# `ExecNothing()` twin like keepMine/takeTheirs; wire unchanged). **`sendCommit` fix** found
   in review: it reused a stale `wireEdits` snapshot from MINE's values, so a per-field take-theirs
   resend would have wrongly forced mine — rebuilt to read CURRENT `obj.props` at send time
   (arch-verified byte-identical for every non-conflict commit). Coarse whole-ctx buttons + the global
   no-silent-clobber banner both preserved. **§6 FINE-SLICE OBLIGATIONS: MET** (show-theirs-pre-choice,
   widened items, per-field resolve, object disambiguation, refresh-leave-guard). Residuals/flags: (a)
   **ux finding DEFERRED for user decision** — the global "reload to see the latest" banner still fires
   alongside the in-form fine bar (slice-6's DELIBERATE double-banner), and with the richer resolver it
   now reads as contradictory (loud banner says reload=discard; form says resolve). Recommended fix:
   suppress the global banner for the generic UI (the `isGeneric` flag — ObjectForm always renders
   ConflictBar), keep it only as the custom-render fallback. Reverses a settled decision → user's call.
   (b) a genuine TWO-OBJECT same-ctx conflict is NOT producible through user-authorable Code today
   (only ObjectForm opens a staging ctx, binds ONE object; app-level `ambient` is validator-rejected) —
   the grouping/disambiguation is implemented + live-when-reachable but browser-tested only at the
   two-FIELD level; (c) ref/decimal conflict values display as raw id / int-lossy (fixture is
   text+int); (d) per-field keep-mine has no intermediate confirmation (row just vanishes); (e)
   beforeunload shows the browser's generic dialog, not a conflict-specific message. ✔

**TRACK B COMPLETE — B1–B5 all landed 2026-07-04 (slices 9–13). M13 versioning is fully surfaced in
the designer.**

## Versioning-UX + follow-up ledger (deferred deliberately; do not lose)

- **Fast-follow (small, real):** a design created at RUNTIME has no `main` Branch until the
  next boot (`EnsureMainBranches` is boot-only) → Commit on a fresh design fails with the
  branch-lookup error (scenario-documented). Fix: lazy ensure-main-branch on first
  commitDesign (idempotent, mirrors the boot semantics).
- **Live runtime bug (own task chip spawned):** dt.ts `mergeState` scalar-var refetch race —
  a refetch reply can stomp text the user is typing in ANY bound input. Test harness
  works around it; the app hazard is real.
- Commit-detail page (`/commits/<id>`) — DONE (B1, slice 9). Diff view between commits — DONE
  (B2, slice 10). Publish + dry-run from the designer — DONE (B3, slice 11). Branches + merge — DONE
  (B4, slice 12). Fine per-field conflict UI — DONE (B5, slice 13). **All of Track B (B1–B5) DONE.**
  B2 residual: a brand-new type is invisible in the diff (DesignDiff's migration lens — no TypeAdd);
  revisit with B3's publish UI (same vocabulary) if a "what changed" (vs "what migrates") view is wanted.
- B3 fast-follows: post-apply success signal (a "Last published: … · time" line, like the commit-bar);
  drift-only preview offers a no-op-looking Apply (relabel/suppress when only drift is non-empty);
  "Cardinality" label + `Single → Set` values are schema jargon on the publish + diff surfaces (a
  global humanize pass — deferred with the same on B2's diff view, kept for now for consistency).
- B4 fast-follows / notes: (a) post-merge "Merged X into this design" confirmation line — needs an
  apply-success signal Code can't cheaply read (same class as B3's deferred success line; the
  cache-drop's "up to date" re-preview is the interim signal); (b) NO regression scenario re-opens
  the merge preview after apply to pin the `mergePreview:` cache-drop (the fix is verified by the
  green flow + mirrors B3's tested publishPreview drop — add a guard scenario when convenient);
  (c) the `access` section has NO editor UI in the designer — B4's AccessChanges scenario seeds the
  rule via the store; an access-section editor is a separate small slice if operators should author
  access changes end-to-end; (d) the merge UI hardcodes "this design" for the target side (correct
  while target is always the currently-open design; a future merge-two-arbitrary-branches view would
  need the real target name).
- Fine per-field conflict UI obligations — DONE (B5, slice 13; §6 obligations MET). Open ux decision
  ledgered in slice 13: the global "reload" banner vs the in-form fine bar (settled-deliberate
  double-banner — recommended suppress-for-generic-UI fix awaits user's call).
- Semantic migrations (`fn migrate` + `Commit.migration` — boundary entries exist, the
  addition is safe per the slice-4 note); compaction (`sys.compact`, §6); non-temporal field
  flag (§0b); per-verb sys granularity.
- Cosmetics: humanized datetimes in generic tables (still deferred — no cheap generic path yet);
  the global banner's "Change rejected" vocabulary vs commit actions. (The generic humanized
  "(no <labelProp>)" empty-label placeholder landed with B1.)

## Slice-1 spec pointers

Full detail (entry shape, files, Gherkin) lives with the slice's feature file
`DeEnv.Tests/Features/AppLog.feature` and the design doc §0. Key anchors:
`JsonFileInstanceStore` (ctor boot-replay; `BumpVersion` accumulator; `Save`→append-then-snapshot;
`Reset()` re-freezes genesis + truncates the log — a fresh document, not a continuation),
`StoreModel.StoreDoc.Version` **is** the log seq (one counter; batches may advance it by >1 →
entry seqs are monotonic with gaps), `AppPaths` sibling paths, value encoding via the existing
`StoredValueConverter`. GC sweeps are NOT logged — replay re-derives them by running the same
collection after applying an entry. Boot-replay's catch-up `Save()` appends nothing (checkpoint
write, not a mutation commit).
