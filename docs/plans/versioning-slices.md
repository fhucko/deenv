# App versioning (M13) — slice plan

Session-5 slicing of `app-versioning-design.md`, 2026-07-03. That doc holds the design and the
reasoning; this one holds the sequenced work list, the per-slice decision gates, and status.
Tag: **`@milestone-13`** (precedent: `Concurrency.feature`); `@multi-user` only on scenarios whose
point is cross-session. One feature file per capability (`AppLog.feature`, `DesignCommit.feature`,
`Publish.feature`, `DesignMerge.feature`, `DataConflict.feature`, `TimeTravel.feature`).

## Sequence

0. **baseVersion anti-clobber guard — DONE** (main `4c72a92`; design doc §0). Pulled ahead of the
   milestone as a bug fix.

1. **Append-only log behind the store (invisible) — IN PROGRESS 2026-07-03.** Every
   `JsonFileInstanceStore` mutation appends one changeset entry (post-remap real ids, old+new per
   field write, msgId, who, when, seq) to `instances/<id>/app-log.jsonl` under the same `_sync`
   lock, fixed crash order append→snapshot; `genesis.json` frozen at first mutation; boot replays a
   lagging snapshot tail; fsck invariant `replay(genesis→head) == app-data.json`. Also rebuilds the
   guard's in-memory `_objectVersions` from the log tail on boot (closes §0 residual 2). Server-only
   C# — no wire, no TS twin, no UI. Defers: checkpoints/compaction, non-temporal field flag, any
   reader of the log.
   Decisions taken: genesis freezes at first mutation (not ctor — read-only boot never writes);
   who/msgId reach the store via an `AsyncLocal` ambient set at the WS message boundary (no
   `IInstanceStore` signature change; bare set-then-call would race across sessions); ONE log entry
   per store commit/Save (per-field entries would break the 1:1 append↔snapshot WAL invariant).

2. **Per-commit caches: canonical printed text + name-path→id map.** Built per marked log seq on
   the designer instance so slice-4 diff/publish read two cached artifacts with zero replay. May
   fold into slice 3 if small. Depends on 1.

3. **`Commit`/`Branch` rows + `sys.commitDesign`.** Design history as ordinary data in the
   designer instance; host action captures head seq under the store lock, advances the branch tip.
   **GATE (user sign-off before build): authority inversion** — `KernelHost.SyncDesignHost`
   currently overwrites the `Design` row from `app.deenv` every boot; a committed design would be
   clobbered. Options at that point: full inversion (boot = one-time adoption; app.deenv written by
   publish) / non-clobbering merge-by-identity transition / gate behind the login-persistence flip.
   **Plus:** the concrete `Commit`/`Branch` meta-type shapes are a meta-schema change → same
   sign-off moment. Depends on 1.

4. **Structural diff + forward publish (the MVP payoff: rename-safe deploy).** Endpoint
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
