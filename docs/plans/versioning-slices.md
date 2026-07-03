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

6. **Data conflict payload + coarse UI.** Stale-overlapping commit returns `{base,mine,theirs}`
   (base read from the log) instead of a flat reject; generic form + `<ConflictBar>` render it;
   keep-mine / take-theirs. First slice to touch wire + TS twin. GATE: conflict payload wire shape.
   Depends on 1.

7. **Time-travel + `cloneInstance(id, atSeq)`.** Replay materializer; floor-over-history = today's
   rules via lineage, removed fields deny-default, history-read gated to rule-changers. Depends on
   1 (+5's floor bits).

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
