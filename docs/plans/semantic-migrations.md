# Semantic migrations — implementation design (`fn migrate` + `Commit.migration`)

Drafted 2026-07-04, triggered by the post-M13 backlog's Track C ("needs a DESIGN PASS first").
Status: **ACCEPTED — self-grills #1 (migration core) and #2 (revert model) both done,
verdicts SOUND-WITH-FIXES, all fixes integrated; the §2 schema/arity ask APPROVED by the
user 2026-07-04** (`Commit.migration text` + the hard 3-arity `sys.commitDesign` bump).
Slice 1 landed 2026-07-05: `Commit.migration text`, hard 3-arity `sys.commitDesign`,
commit-time validation, designer textarea, and commit-detail rendering. Buildable next:
slice 2 (§6). The restoration bundle (resurrect-with-id primitive +
sys.revertCommit) is designed but re-scoped to its own later slice with its own approval
ask. Next: milestone-planner slices it; sequence after Track B's B3/B4 (shared app.deenv +
KernelHostActions).
Companions: `app-versioning-design.md` §3 (the settled design-level position this implements),
`versioning-slices.md` (what actually landed in M13), `post-m13-backlog.md` (Track C entry).

## What §3 already settled (recorded, not re-argued)

- Migration authored **on the commit, not in the design** — a transition, not a state.
- Execution per object: `new = structuralTransform(old)`, then semantic fns with
  **`old`** = full pre-migration object (read-only, removed fields readable),
  **`oldDb`** = whole pre-migration store (read-only), **`new`** = writable result.
  Dissolves ordering: a fn computing from a removed field reads it off `old`.
- Multi-commit publish paths: pure-structural spans collapse into one endpoint diff; each
  migration-carrying commit forces a **step**. Path = collapse–step–collapse.
- Atomic: in-memory copy, any fn throw aborts the whole publish, save only on full success.
- Kernel-side, **C#-interpreter only** (classified like floor conditions; no twin burden).
- v1 logs the **materialized migration changeset** — one entry per publish, correct without
  fn purity. Macro-entries (re-run-on-replay) = later size optimization only.

Open per §3, designed here: authoring UX in the commit dialog; old/oldDb/new scope wiring;
collapse–step–collapse pathing; the `Commit.migration` schema addition.

## Code ground truth this design stands on

Confirmed by the draft pass + the grill (2026-07-04, worktree at B2/708):

- Publish today = **endpoint diff only**: `KernelHostActions.Publish` (KernelHostActions.cs:116)
  diffs the target's stamped commit against the design's HEAD via `DesignDiffer.Compute` over the
  two commits' cached `{text, idMap}` — "collapse" is already the implemented behavior; there is
  no chain walk anywhere.
- `JsonFileInstanceStore.ApplyPublishBoundary` (JsonFileInstanceStore.cs:1142) = load raw doc →
  structural passes mutating in memory while recording `LogWrite`s (old+new per field) →
  ONE boundary-marked entry → log-first `SaveRaw`. `dryRun` skips the two disk effects, same
  compute path. One `doc.Version = startVersion + 1` bump regardless of write count
  (JsonFileInstanceStore.cs:1320); `LogEntry.Seq == Version`; replay applies boundary writes
  literally (AppLog.cs:20-31) — so N segments concatenated into one entry with one bump is
  seq-consistent (grill-verified).
- `BoundaryMarker{designId, commitId, baseCommitId}` (slice 7) — time-travel era resolution
  hangs off this one entry.
- Kernel-side Code execution precedent: `AccessFloor` (AccessFloor.cs:47) holds a bare
  `new CodeExecutor()` and evaluates rule conditions over bound ExecValues — no host actions,
  no ctx, C#-only. Migrations are classified the same way.
- `CodeExecutor.InvokeFunction(fn, args, scope, context)` (CodeExecutor.cs:1119) — public entry
  for calling a `CodeFunction` with explicit args. Symbol resolution is lexical-first,
  ambient-fallback (CodeExecutor.cs:231→254) — plain `ExecScope` items work for `new`/`oldDb`.
- Assignment on an ExecObject prop lands **in place** (`obj.Props[name] = value`,
  CodeExecutor.cs:155); a bare ExecContext defaults `ReadOnly=false`, `Ambient=null`
  (ExecValues.cs:279/247), `NearestStagingCtx` → null (CodeExecutor.cs:273) — writable-`new`
  semantics confirmed (grill A5).
- `DbBridge.LoadRoot(store, desc, context)` loads scalars, refs, sets, and dictionaries (the
  header's "dictionaries are not yet loaded" is stale — `Cardinality.Dictionary` at
  DbBridge.cs:88). BUT dict entries are display rows with **KeyHash ids** and scalar-only
  fields — see the dict exclusion below (grill B1).
- The store read path (`ReadNode`/`ReadById`/`ReadExtent`/`ResolveRef`/`BuildObject`,
  JsonFileInstanceStore.cs:271-994) depends ONLY on `_doc`+`_desc`+`_resolver` (+`_sync`) —
  never on file paths or log state. The proposed in-memory ctor is viable (grill A1).
- ExecValue→store direction today: `DbBridge.ExecToScalar` (DbBridge.cs:246-252) covers
  **Int/Text/Bool only**, returning a `NodeValue` (harvest wraps it in `StoredLeaf`). There is
  no ExecDecimal/ExecDate/ExecDateTime — those load as `ExecText` (DbBridge.cs:259-261). This
  bounds the v1 write surface (grill A2, integrated below).
- `CodeValidator.BuiltinArities` is EXACT-match (CodeValidator.cs:229-232); `publish`'s dryRun
  never crosses the Code arity check (it lives in the host-action JSON layer,
  `ArgBoolOptional`, KernelHostActions.cs:121/1167) — NOT a precedent for an optional Code arg
  (grill A4, integrated below).
- Parse entry: `CodeParse.Section(keyword)` (CodeParse.cs:369) + public `SectionItems` (358) +
  `MapCommon`'s "may only contain functions" guard (387) — reusable pieces, not turnkey
  (grill A3).
- Commit walk helpers exist: `AncestorsOf` DAG-walks `parent`+`mergeParent`
  (KernelHostActions.cs:1022-1023), `FindCommit`/`FindHeadCommit` (1089/1080). A first-parent-only
  chain walk is new-but-trivial. Merge commits carry no migration BY CONSTRUCTION: the only
  migration-bearing path (`CommitDesign`) always passes `mergeParent: null`
  (KernelHostActions.cs:403); `CreateMergeCommit`'s fields have no migration key (453-459)
  (grill B2).
- commitDesign errors surface via the existing host-action error path → client `lastError`
  (WsHandler.cs:1105/1081); the commit bar's inputs are client-owned ui vars that survive a
  rejection (no refetch fires) — the rejection UX claim holds (grill B6).
- `StoredDataValidator.Scalar` rejects a wrong-tagged leaf at REMOUNT (StoredDataValidator.cs:162-165)
  — i.e. a bad harvest write would 503 the instance LATER unless the harvest checks first
  (grill B4, integrated below).
- Designer schema (instances/1/app.deenv:35): `Commit { message, at, design, parent,
  mergeParent, logSeq, text, idMap }`; `Design` stores code sections as plain text — the
  storage precedent for `migration text`. Commit bar at app.deenv:137
  (`var commitMessage`/`commitMigration` + `doCommit` → `sys.commitDesign(design, commitMessage, commitMigration)`), wired
  lockstep at 5 sites (slice 8).
- Layering: `DeEnv.Code` imports `DeEnv.Storage` (AccessFloor.cs:2) — Storage must never import
  Code; the migration runner cannot live inside `ApplyPublishBoundary`.
- `new` and `oldDb` are NOT keywords (CodeParse.cs:19-20) — ordinary symbols, injectable; also
  shadowable (the guard below).

## Position (grilled; fixes integrated)

### 1. Authoring surface — plain `fn` per type, no new grammar

`Commit.migration` holds ordinary Code source, parsed by the EXISTING section-parsing pieces
(`SectionItems` + a fns-only guard, per A3). Each top-level fn is named for a type **in the
schema being committed** and takes one param:

```
fn Invoice(old)
    new.total = old.net + old.tax
fn Customer(old)
    new.displayName = old.firstName + " " + old.lastName
```

- `old` = the fn's parameter (per §3's sketch). `new` and `oldDb` = **plain `ExecScope` items**
  in the invocation scope — NOT ambient (grill B7 flip: lexical-first resolution makes scope
  items identical in effect with none of the AmbientFrame machinery).
- **No `migrate` keyword** in v1. §3's `migrate Invoice(old) { ... }` sketch is treated as
  illustrative: plain `fn` needs zero parser/printer work, and the section IS a migration by
  position (it lives in `Commit.migration`). Deferred cosmetic, not rejected forever.
- **No bare-field writes** (`total = ...`): bare assignment would put user prop names into the
  same scope key-space as `old`/`oldDb` — the settled encapsulate-user-namespace rule forbids
  exactly that. Writes are explicit: `new.total = ...`.
- **Commit-time validation (all reject the commit; inputs retained — B6 verified):**
  1. Source parses (fns only, the MapCommon-style guard).
  2. Every top-level fn names a type in the committed snapshot — `fn Invocie(old)` fails
     loudly at commit, never skips silently at publish. No helper-fn affordance in v1 — see
     the correction below.
  3. **Shadow guard (grill hole):** no fn param/local named `new` or `oldDb` — because they are
     ordinary symbols, a body-local `var new = ...` (or a second param) would shadow the
     injected binding and turn every write into a harvested-by-nobody no-op. Rejected at
     commit, same path.
  4. A migration on a **root commit (no parent) is rejected** — no transition to describe (and
     the range walk can never execute one).
  5. Every top-level fn takes **exactly one parameter** (`old`) — a fn with zero or more than
     one param is rejected at commit; it could never be invoked the way §3 calls it.
- Validation depth = parse + the four structural checks above. No type checking of
  `old.x`/`new.y` field references — the project has no type checker anywhere (AGENTS.md
  rule 9); runtime errors surface at publish and abort atomically.
- **Correction (review pass 2026-07-05):** the earlier claim that "helpers remain possible as
  fn-values (`var f = fn(...)`) inside a type fn" is WRONG — block-bodied fn-values do not
  parse in ANY section today (a pre-existing grammar limitation, confirmed by the architecture
  review, not something this slice introduces or could cheaply fix). v1 has **no helper
  affordance** inside a migration fn; named limitation, revisit if a real migration wants one.

### 2. Commit dialog UX — migration input + a hard 3-arity bump

The commit bar (app.deenv:137) gains a collapsed-by-default "Migration" affordance: a toggle
revealing a multiline input bound to a new `var commitMigration = ""`, passed as the third arg:
`sys.commitDesign(design, commitMessage, commitMigration)`. Empty string = no migration (the
zero-config common case — the AUTHOR types nothing extra for a structural-only commit; the
empty third arg is internal plumbing, not authoring surface).

- **Arity is a HARD bump to 3, not an optional arg** (grill A4): `BuiltinArities` is
  exact-match and `publish`'s dryRun is NOT a precedent (it never crosses the Code arity
  check). The designer is the only Code caller of `commitDesign`; it always passes the third
  arg. Optional-arity support in CodeValidator = YAGNI.
- On rejection (any §1 check) the message AND migration inputs retain their text (B6).
- `/commits/<id>` (B1's detail page) renders `migration` read-only in a `<pre>` when non-empty,
  beside the cached `text` — same idiom, ride-along.
- **Approved and landed in slice 1:** `migration text` on the Commit meta-type
  (slice-4 note: boundary entries make the addition safe; slice-5 precedent: "normal additive
  apply") + the 5-site lockstep arity change to `sys.commitDesign` (slice-8 precedent). Both
  follow recorded precedents but ARE a schema + host-action-shape change.

### 3. Scope wiring — DbBridge over an in-memory store view; the seam written down

The migration runner (new file in `DeEnv.Code` beside DbBridge — Code already imports Storage;
Storage never imports Code) is invoked by the publish orchestrator per step. **The
StoreDoc↔ExecObject seam, explicitly** (grill's biggest hole — two worlds, one round trip per
step):

1. **Old world (StoredValue → ExecObject, read side).** The pre-step `StoreDoc` (retained
   before the step's structural passes) + the pre-step schema (`InstanceDescriptionLoader.Load`
   of the PREVIOUS endpoint commit's cached `text`) wrap in the **internal in-memory ctor**
   `JsonFileInstanceStore(StoreDoc, InstanceDescription)` — no file paths, no boot reconcile,
   `Save()` throws loudly; read path verified `_doc`+`_desc`-only (A1). Load once via
   `DbBridge.LoadRoot` → `oldDb` root + an id→ExecObject index for per-object `old` lookup by
   intrinsic id (rename-safe by construction).
2. **Structural transform (StoredValue world).** `TransformDoc(doc, diff_i, stepDesc)` — the
   extracted pure in-memory passes of today's `ApplyPublishBoundary`, appending to the shared
   writes list, threading `doc.NextId` (B3 verified: one Version bump + one entry at the END is
   seq-consistent).
3. **New world (StoredValue → ExecObject, again).** The SAME (now-transformed) `StoreDoc` +
   the step commit's schema, wrapped and loaded the same way. Renamed/converted fields read
   back post-transform — exactly the `new = structuralTransform(old)` semantics.
4. **Run fns (ExecObject world).** For each migration fn (= type name), for each object in
   that type's NEW extent: `InvokeFunction(fn, [oldById[id]], scope{new: newObj, oldDb: oldRoot},
   bare ExecContext)`. Writes land in `ExecObject.Props` in place (A5).
5. **Harvest (ExecObject → StoredValue, write side).** Per migrated object: scalar props
   snapshotted before the fn, diffed after. Each change is checked against the STEP schema's
   declared prop type and converted `ExecValue → NodeValue → StoredLeaf`:
   - **v1 write surface = Int/Text/Bool, EXPLICITLY** (grill A2 refutation): the only forward
     conversion is `ExecToScalar` (Int/Text/Bool); decimal/date/datetime have no ExecValue
     representation (they load as ExecText). A fn write landing on a decimal/date/datetime
     prop, a wrong-typed scalar write, or a non-scalar write (set/ref/dict/object) **aborts the
     publish loudly at harvest** — never deferred to `StoredDataValidator`'s remount 503
     (B4/B9). This is a CODE-RUNTIME ceiling (no decimal arithmetic exists in Code at all —
     §3's own `old.net + old.tax` example only computes for ints today); when Code gains real
     decimal/date values, the harvest widens by the same declared-type check.
   - Each accepted change → `FieldWrite{old, new}` appended to the SAME writes list the
     structural passes feed + the `StoreDoc` mutated in place — so subsequent segments and the
     final entry see one consistent world.

Iteration note (grill hole, named): "each object in the type's NEW extent" is extent-membership;
`LoadRoot` walks from root. The two agree exactly when the store is GC-consistent (M5 GC sweeps
unreachable rows; a store at rest is consistent; the structural passes remove whole extents but
never orphan-and-keep rows). The runner iterates extent rows (the `LoadExtent` precedent) and
resolves them in the loaded graph; the GC invariant this leans on is now STATED, not silent.

**Dict-typed props are EXCLUDED from migration in v1, loudly** (grill B1 refutation): a dict
entry's ExecObject id is `KeyHash(dictId, key)` — not an extent id — so the old↔new id join
breaks whenever the dict's intrinsic id differs; and DbBridge loads only an entry's scalar
fields. A migration fn TARGETING a dict-entry type name, or writing a dict prop, aborts loudly
("dictionary migration not supported yet"). Reading a dict off `old`/`oldDb` for scalar
computation is fine (read side loads). v2 re-keys entries by `SourcePath`/key if real usage
wants it.

The store-view choice, against the alternatives considered (unchanged from the draft, grill
A1-confirmed): a second store over the target's real files = the one-store-per-file kill class;
a temp-file store = pays I/O + boot path for nothing; a direct StoredValue→ExecObject loader =
duplicates DbBridge ("the only place a collection changes shape"); the live target store =
right only for step 1's old world. One mechanism for all steps wins.

Read-only-ness of `old`/`oldDb` is **by discard, not enforcement** in v1: writes to old-world
ExecObjects mutate a graph that is thrown away, never harvested. Named ceiling. Non-determinism
ceiling also named: whatever pure builtins the bare executor exposes are callable; the
materialized entry keeps the LOG honest regardless (§3's purity deferral, unchanged).

### 4. Collapse–step–collapse — the range walk in Publish

`Publish` (KernelHostActions.cs:116), versioned path only, before diffing:

1. Walk head → stamped via `parent` links ONLY (a new first-parent chain walk — `AncestorsOf`
   gives the full DAG set, not the chain; both are needed, B10). Collect the chain; DAG-walk
   `parent`+`mergeParent` for the full ancestor range.
2. **Steps** = commits ON the first-parent chain (exclusive of stamped) whose `migration` is
   non-empty, in chronological order.
3. **Refusals (v1 ceilings, loud, before any work):**
   - A migration-carrying commit in the DAG range but NOT on the first-parent chain → refuse:
     "publish range contains a merged migration — not supported yet". A side-branch migration's
     adjacency assumption does not survive a merge; ordering it is rebase-class semantics.
     Merges of NON-migration branches collapse fine (the merge commit's snapshot is just
     another endpoint).
   - Stamped unreachable from head (exotic: published from an unrelated line) AND any
     head-ancestry commit carries a migration → refuse: "cannot establish a migration path".
     Without migrations, unreachable-stamped behaves as today. (Grill B10 confirmed the
     ancient-migration worry is unfounded: on the normal path the range is stamped→head and
     older migrations are never walked.)
   - Merge commits never carry migrations by construction (B2 verified) — noted, not checked.
   The common solo flow — linear commits on main — never hits any refusal.
4. **Execution** = segments over one throwaway doc (B5 verified: publish already operates on a
   fresh `LoadRaw` doc, offline): `prev = stamped; for each step Mi: TransformDoc(diff(prev→Mi))
   → run Mi's fns (§3) → prev = Mi;` finally `TransformDoc(diff(prev→head))` with no fns.
   k=0 degenerates to EXACTLY today's single endpoint diff. All diffs run over cached
   `{text, idMap}` — zero replay.
5. **One log entry, unchanged shape**: all segments' writes concatenate into the ONE
   boundary-marked entry (`BoundaryMarker{designId, headCommitId, baseCommitId: stamped}` —
   byte-compatible with slice 7's era resolution). One Version bump, `Seq == Version`,
   literal replay (B3 verified). Intermediate step states are deliberately NOT addressable by
   time travel — they never existed as served states.
6. **Refactor**: `ApplyPublishBoundary` splits into `TransformDoc(doc, diff, desc) → writes`
   (pure in-memory, per segment) + the final append-entry + log-first `SaveRaw` bracket (the
   slice-1 WAL law, unchanged). Single-step publish = the k=0 instance of the same pipeline.
7. **Atomicity + dryRun**: any fn throw / harvest abort / refusal exits before the entry;
   nothing on disk changes; errors reach the operator via the existing host-action error path.
   `dryRun` runs the FULL pipeline including fns on the throwaway doc and discards (one code
   path — the established publish rule).
8. **Re-publish-after-crash guard (grill hole, fixed cheap):** a crash after `AppendLogEntry`
   but before `stampPublishedCommit` leaves the target migrated but unstamped; a naive
   re-publish would RE-RUN the fns over already-migrated data (the structural re-diff is inert
   — slice 4's residual — but migrations are not). Before executing, read the target log's
   LAST boundary marker; if it already names `{designId, headCommitId}`, skip transform+fns and
   just re-stamp (report it). ~Tail-read of the log; closes the non-idempotent window instead
   of documenting it (difficulty-vs-correctness: cheap fix, taken).

### 5. Report + fallback edges

- `PublishReport` gains `migrations: [{commitId, message, types, objectsMigrated}]` per step +
  the abort error shape. B3's publish UI renders it; report-on-reply is the approved precedent.
- **Unstamped-target fallback** (name-match path) and the no-commits path do NOT run migrations
  (no identity diff, no range). A fresh instance has no rows — migrations are no-ops anyway.
  The one honest gap: a pre-slice-4 legacy instance WITH data taking its one-time fallback
  skips any migrations in head's ancestry — report `migrationsSkipped: true` loudly rather
  than block. Bites only legacy unstamped instances, exactly once.
- Per-step unconvertible/unsupported structural cells aggregate with step attribution.

### Reverting a publish — a normal publish, not a special op (user decisions 2026-07-04)

Two user steers shaped this, in sequence: (1) data reverts around migrations must not be
handled manually; (2) a mechanical inversion of the boundary entry — drafted first — "would
not work in general", because reverts need the ability to DEFINE what happens to data.
The second steer is right and kills the first draft's special op: rows created or edited
UNDER THE NEW SCHEMA were never in the boundary entry, so inverting it says nothing about
them (publish adds `total`, drops `net`/`tax`; users create 10 invoices; the inverse has no
`net`/`tax` for them and no rule to make any). Lossy migrations make the reverse
UNDERDETERMINED — a definition, not a derivation. There is no `sys.revertPublish`.

**The general model — revert rides the standard pipeline, uniformly:**

1. **Revert commit** restores the old schema WITH identity (settled §0 recovery). Honest v1
   split (grill #2 correction — the earlier "manual editing suffices" claim was wrong for
   restoration): MANUAL revert-commit editing gives rename reverts, scalar-type/cardinality
   reverts (same rows, fields edited back — identity never deleted), and authored migrations
   recomputing from SURVIVING fields. What manual CANNOT give: removed-field/removed-type
   data back — a manual re-add mints a FRESH id (new identity, correctly fresh-start), and
   the removed values live only in the log, unreachable from a fn's `old`. That capability
   needs `sys.revertCommit(design, commit)`, which itself needs a **new store primitive —
   resurrect-with-id** (both model-level create paths MINT: CreateObject and CommitBatch
   creates; only sub-model paths apply literal ids — grill-verified). An `IInstanceStore`
   interface-shape change → flagged as a FUTURE approval ask, bundled with the restoration
   slice. sys.revertCommit must also REFUSE when `commit` is not the target's stamped
   commit / the design head (reverting an older publish past later commits would silently
   revert those too, and the copied revert fn's schema adjacency breaks — merge-class
   territory, refused loudly).
2. **Structural transform** carries renamed data back by identity — the existing rename-safe
   publish, no new behavior.
3. **The revert commit carries a plain `fn migrate`** — the SAME §1–§3 machinery — defining
   everything underdetermined: post-publish rows, lossy inverses
   (`fn Invoice(old)` → `new.net = old.total` / `new.tax = 0`, or whatever the domain wants).
   Zero new concepts; the authoring, validation, execution, report, and dry-run of this
   design apply verbatim because a revert IS a forward publish of a commit whose schema
   happens to equal an older one. Forward-only (§0) stays absolute — this is not a
   down-script, it is a forward migration.
4. **Identity re-add restoration** — the one thing a fn CANNOT define is the exact
   pre-publish values a lossy migration destroyed; only the log has them. So restoration
   becomes PIPELINE behavior (not a revert op): when the structural transform applies a
   PropAdd/TypeAdd whose identity PREVIOUSLY EXISTED in this instance, it restores each
   object's recorded value from the boundary entry that removed it, instead of defaulting.
   **Grill #2 re-scoped this: restoration is NOT in the migration slices** — it ships as one
   coherent later slice WITH `sys.revertCommit` and a new store primitive (below), because a
   MANUAL re-add mints a fresh MetaProp id (new identity → restoration correctly never
   fires), so no real authoring surface can reach it until sys.revertCommit exists.
   - Fills ONLY cells that would otherwise default — the field did not exist under the
     current schema, so no user edit can be clobbered (grill-verified for prop-level scalars,
     including repeated remove/re-add cycles — most-recent removal carries the edits).
   - Fresh-start is expressible, not configured: re-adding the OLD identity restores its
     data; adding a NEW field starts empty. Identity determines data continuity.
   - Fns run AFTER restoration and can overwrite it.
   - Detection mechanics (grill #2 correction — never pattern-match writes, a rename emits
     the same FieldWrite shape): identity N was removed at boundary entry E iff
     N ∈ base(E).idMap ∧ N ∉ commit(E).idMap (both cached on commit rows); the era prop name
     is the base path's suffix; then (ObjectId, eraName, New==null) is unambiguous.
     Newest-first; an entry with null BaseCommitId (pre-slice-7) is a HARD HORIZON — stop and
     report "history unresolvable below this point", never scan past it (skipping could land
     on a STALE older removal). Compaction (§6) likewise: scan stops at genesis, reports
     "unrestorable (history compacted)" — consistent with §6's recovery-reach-=-horizon, no
     foreclosure. Restored ids can never collide (MintId strictly monotonic, ids never
     reused — grill-verified).
   - **Integrity pass (grill #2 holes):** restored values may carry refs to rows deleted
     since → drop/null dangling refs + report (else StoredDataValidator 503s the remount);
     a TYPE restore must verify post-transform REACHABILITY of restored rows (anchoring
     props re-added in the same publish) or refuse — else the next mutation's GC silently
     sweeps what the report just claimed restored.
   - Requires the diff to carry identity on adds: `PropAdd` gains the prop id and a `TypeAdd`
     record appears — grill-verified additive-safe at every DesignDiff consumer (publish has
     no Adds pass; report/diff-view read names only), and TypeAdd FIXES B2's ledgered
     "brand-new type invisible" limitation. The needs converge.
   - Known inherited gap, named: idMaps key RAW row ids, not origin-flattened ones — an
     instance stamped from a BRANCH commit never identity-joins a main-side revert
     (pre-existing cross-branch-publish gap in the diff itself; restoration inherits it).
   - Restoration is LOUD in the report ("N cells restored from history"), dry-run included.
5. **The G1 invariant — an OBLIGATION, not a fact (grill #2 refutation, HIGH):** the draft
   claimed removals only enter instances via boundary entries. FALSE — three live paths
   (`setDesign` = the IDE's Apply, KernelHostActions.cs:292; the no-commit publish; the
   unstamped fallback) remove fields via `MigrateTowardSchema` with NO log writes, and then
   RE-BASELINE the store (delete log + genesis, JsonFileInstanceStore.cs:1080-1087 — the
   slice-1 ponytail). Two consequences, both now stated: (a) stale restores are impossible
   today only AS A SIDE EFFECT of the wipe — the invariant to MAINTAIN (and pin with a guard
   test) is "every path that removes schema-shaped data either logs a boundary entry or
   re-baselines the whole log"; whatever someday replaces the re-baseline must log removals
   or it silently creates the stale-restore class. (b) The corollary an operator must know:
   **one setDesign/fallback apply on a stamped instance ANNIHILATES its restoration and
   time-travel history.** Making those paths history-preserving is its own future item.
6. **In-place destructive fn overwrites** (a forward migration rewrote an EXISTING field —
   no structural change, so no re-add to hook restoration on): the revert fn defines the best
   recompute; exact per-cell restoration is the §7 inverse-commit/undo primitive's future
   territory (the log has old+new per write — the entry-inversion insight from the first
   draft remains TRUE and remains §7's foundation; it is just not the revert mechanism).
   Named ceiling. `cloneInstance(id, atSeq)` stays the full-state disaster tool.

Everything an operator sees is ONE pipeline: the same dry-run, the same report (destructive
ops loud, restorations loud), whether the publish moves forward or "back".

**Authored revert fns (optional follow-on — the Django `reverse_code` lesson, 2026-07-04).**
Industry survey, for the record: mature up/down ecosystems (Rails/Django/EF/Alembic/Liquibase)
auto-derive SCHEMA inverses but always HAND-AUTHOR data reverses — optional, with an explicit
irreversible marker when omitted; forward-only ecosystems (Prisma: no down migrations at all;
Flyway default; big-co expand/contract practice) skip down-scripts entirely; Dolt (the
git-for-data cousin) reverts via inverse commits = our §7. deenv's model is the latter two
camps PLUS log restoration the SQL world structurally can't do. The one useful import from
camp 1: let the FORWARD author define the reverse WHILE context is fresh —
`Commit.revertMigration text` (second collapsed textarea; same four validations pointed at
the PARENT snapshot — a revert fn migrates back TO the parent's schema). It needs NO
execution model of its own: `sys.revertCommit(design, commit)` (the ledgered one-click
revert-commit creator) simply COPIES it onto the revert commit as its plain forward
`migration`; the standard pipeline runs it like any other. Omitted revertMigration = nothing
copied = underdetermined parts default + report loudly (the dry-run IS the irreversibility
marker — no error class). Ceiling, same as Rails/Django: a revert fn targets its own parent's
schema, so multi-publish reverts chain one commit-step at a time. Cost: S on top of
sys.revertCommit (itself M — the identity-restored commit construction is the meat, needed
for one-click reverts regardless); pipeline/executor/report changes: zero.

### 6. Slicing sketch (for milestone-planner, not binding)

1. **Authoring + storage — DONE 2026-07-05**: `Commit.migration` + the hard 3-arity commitDesign change (5
   lockstep sites) + the four commit-time validations + commit-bar input + B1 detail render.
   No execution. Scenarios: commit with migration stores + renders; bad fn name rejects;
   shadowed `new` rejects; root-commit migration rejects. Review pass 2026-07-05 (arch+ui-arch+ux
   SHIP-WITH-FIXES, fixes applied — parse-error coordinates, banner pre-wrap, detail-pre styling,
   placeholder+non-empty indicator, browser round-trip scenario; clear-on-success DEFERRED to the
   host-action success-signal mechanism, wanted 3× now: B3 + B4 + this).
2. **Execution**: in-memory store ctor + migration runner (seam steps 1–5) + range walk +
   `TransformDoc` refactor + re-publish guard + report. Scenario spine: rename+compute
   migration carries data through publish (extends slice-4 fixtures); fn throw aborts
   atomically; harvest type-violation aborts loudly; dryRun previews without applying; merged
   migration refuses; dict-targeting fn refuses; crash-window re-publish re-stamps without
   re-running.
3. ~~Identity re-add restoration~~ — **MOVED OUT of the migration slices (grill #2 verdict:
   unbuildable as scheduled).** Restoration ships as ONE coherent later slice bundling:
   the resurrect-with-id store primitive (interface approval ask) + `sys.revertCommit`
   (identity-restored commit construction + revertMigration copy + not-stamped/head refusal)
   + PropAdd id + TypeAdd in DesignDiff (converges with B2's ledgered TypeAdd want) + the
   idMap-membership backscan with its horizons + the integrity pass (dangling refs,
   type-restore reachability) + the G1 invariant guard test. Slices 1–2 stand alone; their
   honest revert story meanwhile: renames/conversions revert manually, authored fns recompute
   from surviving fields, clone-at-seq for full state. Scenario spine when it lands: publish
   removes a field → users work → sys.revertCommit → publish → old rows get exact values
   back, post-publish rows get the revert fn's definition, nothing user-edited clobbered; a
   manually re-added same-named field (different identity) starts empty; dangling restored
   refs dropped + reported; unreachable type restore refused.

## Self-grill #1 (2026-07-04) — verdict SOUND-WITH-FIXES, all integrated

Fresh-context opus grill, briefed to refute, verified against real code. Disposition:

- **Refuted → fixed in §3:** the "1:1 by id" join breaks for dict entries (KeyHash ids,
  scalar-only entry loading — DbBridge.cs:108/117-124) → dicts excluded loudly in v1.
- **Refuted → fixed in §3:** "ExecValue→StoredValue conversion falls out of the persist path"
  was optimistic — `ExecToScalar` covers Int/Text/Bool of six scalar types; decimal/date/
  datetime live as ExecText and would brick the target at remount → v1 write surface named
  Int/Text/Bool, harvest-time abort against the declared type.
- **Refuted → fixed in §2:** "optional third arg is mechanical" — `BuiltinArities` is
  exact-match; publish's dryRun never crosses the Code check → hard 3-arity bump.
- **Hole → fixed in §3:** the StoreDoc↔ExecObject seam is now written down (the round trip per
  segment is the actual work).
- **Hole → fixed in §1:** `new`/`oldDb` shadow guard at commit validation (plus the B7 flip:
  plain scope items, not ambient).
- **Hole → fixed in §4.8:** re-publish-after-crash migration re-run — cheap last-boundary
  guard taken instead of a documented ceiling.
- **Hole → named in §3:** the extent-vs-reachability GC invariant the iteration leans on.
- **Confirmed sound:** in-memory ctor viability (read path is `_doc`+`_desc`-only); bare-context
  in-place writes; one-entry/one-bump seq consistency across segments; throwaway-doc dry run;
  merge-commits-never-carry-migrations by construction; rejection UX (inputs retained); the
  B10 refusal semantics (ancient migrations never block; the walk terminates, unambiguous).

## Self-grill #2 (2026-07-04) — the revert model; verdict SOUND-WITH-FIXES, all integrated

Same agent resumed (classifier outage — the slice-5 workaround), briefed to refute the
revert sections it never saw. The MODEL — revert = a normal forward publish of a revert
commit carrying a normal migration — survived fully verified (walk coherence, chaining,
report reuse, no-clobber for prop-level scalars incl. repeated cycles, id monotonicity,
PropAdd/TypeAdd additive-safety + the B2 convergence). What it killed or forced:

- **Refuted (HIGH) → §5 above:** "removals only enter via boundary entries" — three live
  paths (setDesign / no-commit publish / fallback) remove WITHOUT logging and then WIPE the
  log (re-baseline). Restated as a maintained invariant + guard test; the
  operator-facing corollary (Apply annihilates restoration/time-travel history) named.
- **Refuted (HIGH) → §1 above:** no model-level resurrect-with-id exists — sys.revertCommit
  needs a NEW IInstanceStore primitive (future approval ask; fabricating idMap entries is
  incoherent — the next commit re-keys from live rows).
- **Refuted (HIGH) → slicing:** "manual editing suffices v1" was false for restoration;
  slice 3 was unbuildable as scheduled → restoration re-scoped into one coherent later
  slice bundling primitive + sys.revertCommit + backscan + integrity pass.
- **Holes → §4 above:** dangling restored refs would 503 the remount (integrity pass);
  partially-restored types get silently GC'd (reachability check or refuse); backscan must
  detect removals by idMap MEMBERSHIP (renames emit the identical write shape); null
  BaseCommitId = hard horizon (skipping could restore stale); compaction horizon reported
  gracefully; the cross-branch-publish identity gap named as inherited.
- **Hole → §1 above:** sys.revertCommit refuses a non-stamped/non-head target (the
  revertMigration staleness trap).

## Settled vs open (post-grill)

Settled by this pass (§2 schema/arity ask APPROVED by the user 2026-07-04):
plain-`fn` authoring, no new grammar; explicit `new.x` writes; four commit-time validations
(parse, strict names, shadow guard, no-root-commit); plain-scope-item injection; in-memory
store ctor + DbBridge reuse with the seam specified; **Int/Text/Bool-only** harvest with
harvest-time aborts; dict exclusion; first-parent steps with loud merge/no-path refusals; one
unchanged-shape boundary entry; one-code-path dryRun; last-boundary re-publish guard;
**revert = a normal forward publish of a revert commit with a normal migration on it — no
special revert op (user, 2026-07-04: reverts need DEFINABLE data handling; a mechanical
inversion "would not work in general") — with identity re-add restoration DESIGNED as
pipeline behavior but RE-SCOPED by grill #2 into its own later slice (bundled with
sys.revertCommit + a new resurrect-with-id store primitive)**.

Open / deferred, named:
- `migrate` headword sugar (cosmetic; zero semantics).
- Decimal/date/datetime migration writes — gated on Code gaining real decimal/date values
  (a Code-runtime ceiling, not a migration one; the §3 example `old.net + old.tax` only
  computes for ints today).
- Non-scalar writes (create/link objects, sets, refs) — v2, aborts loudly today.
- Dict-entry migration (re-key by SourcePath) — v2 if real usage wants it.
- Old-graph write enforcement (discard-only v1).
- Merged-migration publishes (rebase-class; refused loudly).
- Purity/macro-entries (§3's own deferral, unchanged).
- Migration authoring ergonomics beyond a textarea (editor tooling is a future layer).
- **The restoration slice** (one coherent bundle, grill #2 re-scope): resurrect-with-id
  store primitive (IInstanceStore interface-shape change — APPROVAL ASK when scheduled) +
  `sys.revertCommit` (identity-restored commit construction; refuses non-stamped/non-head
  targets) + the idMap-membership backscan + integrity pass + G1 guard test + the optional
  `Commit.revertMigration` authored-reverse rider (S on top; copy-onto-revert-commit, zero
  pipeline change).
- Making setDesign/fallback applies history-PRESERVING (today they re-baseline = wipe the
  log; the G1 corollary) — its own item, prerequisite-adjacent to restoration mattering in
  practice.
- Per-cell exact undo of in-place fn overwrites (the §7 inverse-commit primitive — the log's
  old+new per write makes entries mechanically invertible; future undo/blame tooling, not the
  revert mechanism).
