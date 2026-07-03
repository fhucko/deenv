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

2. **Per-commit caches: canonical printed text + name-path→id map.** Built per marked log seq on
   the designer instance so slice-4 diff/publish read two cached artifacts with zero replay. May
   fold into slice 3 if small. Depends on 1.

3. **`Commit`/`Branch` rows + `sys.commitDesign`.** Design history as ordinary data in the
   designer instance; host action captures head seq under the store lock, advances the branch tip.
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
   `sys.commitDesign(design, message)` under the store lock; no write grants = floor-immutable.
   Deliberately deferred to their slices (normal additive apply): `mergeParent` + `origin int` on
   MetaType/MetaProp (slice 5), `migration` (slice 4), author `by` (rides the login-persistence
   flip; the slice-1 log already records who). Slice-5 revisit flagged: app-identity vs
   working-copy anchoring once branches clone Design rows. Depends on 1.

4. **Structural diff + forward publish (the MVP payoff: rename-safe deploy).**
   SEQUENCING NOTE (from the shape decision): adding `Commit.migration` to the designer's own
   schema is itself a `changed` apply, which under slice-1 rules truncates the designer's log —
   slice 4 must land the log-preserving boundary-entry apply BEFORE its own designer-schema
   addition, or slice-3 commits get wiped by the slice that was meant to preserve them. Endpoint
   identity-diff between design commits (rename = same id → data carries; the gap
   `MigrateTowardSchema` can't close today because the by-name projection drops identity); publish =
   lock → migrate in-memory copy → ONE log entry (boundary marker + materialized changeset) →
   epoch bump → remount. GATE: publish-preview wire shape, if any ships to the client. Depends on
   2+3.

5. **Branches + three-way structural merge (ctx-staged).** Lineage-keyed 3-way, per-meta-field
   policies (`order` auto-resolves; `name`/`type`/`cardinality` surface), merge rides ctx,
   two-parent merge commit. Access-rule union + must-see block already settled (design doc §1).
   Depends on 3+4.

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
