# App versioning — consolidated design (M13 clump)

Drafted 2026-07-02 from the design session + grills. Status: **design draft, not accepted; nothing here is scheduled.**
Companions: `grill-b-vs-c-commit-storage.md` (storage choice), `grill-versioning-session-results.md`
(cross-cutting holes). This doc consolidates the settled foundation, then works the remaining areas
(merge, conflicts, migrations, publish, floor-over-history, retention, undo, idempotency) in passes:
position → grill → verdict. Settled vs open is marked per topic; roll-up lists at the end.

## 0. Settled foundation (from the session; recorded, not re-argued)

- **Two layers.** Designs (schema+code) get git-like history: commit DAG + branches + merge. Instances
  (design version + live data) have linear append-only data history; never branch, never merge.
  **Publish** bridges them (migrations run there). Branch-on-real-data = publish to a `cloneInstance`.
- **Self-hosted, variant C.** Every instance gets an append-only changeset log — **IMPLEMENTED
  2026-07-03 as slice 1** (`docs/plans/versioning-slices.md`; filenames landed suffix-derived from
  the data file: `app-data.log.jsonl` + `app-data.genesis.json`, so bare-file test stores can't
  collide) — post-remap real-id changesets appended under the store lock; genesis frozen at first
  mutation; `app-data.json` = derived head materialization (boot unchanged, tail-replayed after a
  crash). The log is the only non-derivable artifact (with the compaction asterisk, §6). A design commit = a `Commit` row marking a log seq in the
  designer instance's own log; branch = `Branch` row; DAG via parent refs; refs are data.
- **Caches per commit:** canonical printed text + id-map (name-path→id) → diff/publish run with zero
  replay; replay = cache-rebuild, fsck (`replay(genesis→head) == app-data.json`), data time-travel.
- **Identity.** Rename vs delete+add is recorded (same id vs new id), never detected — except at
  hand-written text import (name-match vs parent, human-confirmed). Publish preview shows identity-level
  destructive ops loudly + **relink** override. `origin` lineage = plain int values, not object refs.
- **Migrations forward-only.** Structural migrations derived at publish by endpoint identity-diff (never
  stored). Semantic `fn migrate` is the only stored migration. Backward = replay own log or
  `cloneInstance(id, atSeq)`; no down-scripts. Testing = fork disposable instances per candidate commit.
- **Commits are Figma-model snapshots** of the shared working copy (no per-user staging — say it loudly).
- **Recovery.** Revert commit restores subtrees WITH identity; published data losses recover from the
  instance log (clone-at-seq now; "resurrection fills from history" later). Loss horizon = retention.
- **baseVersion** = the log seq a draft loaded at; commit carries it; server compares to head. Stale base
  ≠ conflict: disjoint interleaved commits auto-merge (no OCC retry storms; per-object last-modified seq
  makes the check O(objects touched)); only same-field collisions surface. Sustained same-field
  contention wants commutative ops (increment/append) — named future op. **IMPLEMENTED 2026-07-02
  (main `4c72a92`, suite 607+)** as the pulled-ahead detection-only slice fixing the current silent
  two-tab clobber bug: `StoreDoc.Version` (persisted monotonic) + an in-memory per-object last-modified
  map; the staleness check + apply + bump run in ONE `_sync` critical section (reviewed atomicity); every
  mutating store method returns its post-write version captured under-lock and each mutating WS reply
  reports THAT (no check-vs-report split); a rejected commit surfaces via the existing error path, draft
  intact. NOT deployed to the box yet. Residuals: (1) the two blank-password NO-OP live-edit reply
  paths still read `CurrentVersion` separately (a much narrower same-class window; no write occurs) —
  close by omitting `newVersion` on no-op replies if it ever matters; (2) "`_objectVersions` is
  in-memory, cleared on restart" — **CLOSED by slice 1 (2026-07-03)**: the boot rebuild restores
  per-object versions from the durable log, including set-link/unlink member advances (the slice's
  architecture review caught that gap; fixed + regression-tested both directions).

## 0b. Relation to prior design memos (reconciled 2026-07-02)

Two earlier DECISIONS.md entries cover this ground; this design builds on both.

- **"Versioning — unified schema+data, Git-for-data, the instance pins a commit (north star)"
  (2026-06-16).** Convergent: commits + branches-as-refs, instance-pins-a-commit (our registry
  design-commit stamp), migrate-on-checkout named as the hard part (§3 is its execution model).
  Superseded in one respect: that memo scoped M13 "linear-schema-only, no branches/merge" — this
  design INCLUDES design-layer branches + structural merge (slicing may still sequence them late).
  The far-future convergence (content-addressed whole-store snapshots, data branching, Dolt-style)
  remains the north star; the two-layer split here is the staged step that never mints data ids on
  two branches.
- **"Temporal immutability (pillar 4) — design pass" (2026-07-01).** Heavily convergent, reached
  independently: log-authoritative with the live store as a rebuildable checkpoint; one
  authoritative log append + HEAD-marker crash replay (the WAL rule); the OCC stamp = the log's
  per-object last-commit — including that the staleness check and the append **share one critical
  section**. Two reconciliations: (a) that memo replays across schema boundaries by RE-RUNNING
  migrations — superseded by §3's **materialized migration changeset** (correct without a
  migration-purity requirement; macro-entries may return later as a size optimization); (b) its
  DAG commit-id coordinate specializes to a linear seq for instance data (a chain is the
  degenerate DAG; design history keeps the DAG).
  **Adopted from it — a gap this design's passes missed: the per-field NON-TEMPORAL declaration.**
  A field flag excludes a field from the log entirely (live-store-only, mutable, erasure = plain
  row delete), for (a) PII/erasure-law compliance (immutable history cannot hold erasable data)
  and (b) high-churn machine counters that would drown the log. Consequences: non-temporal fields
  have no history, no 3-way base (conflicts fall back to coarse resolution), and are absent from
  time-travel renders.

---

## 1. Design merge (session-2 topic)

**Position.** Three-way merge keyed on lineage ids. Base = DAG common ancestor of the two heads.
Per entity (type/prop), per meta-field (`name`, `type`, `cardinality`, `values`, …): changed one side →
take it; both sides same value → take; both sides different → conflict. Existence: added-in-one →
include; deleted-in-one + untouched-other → delete; **deleted + modified → conflict** (falls out of
lineage matching automatically — includes "A renamed it, B deleted it"). Code sections parse into fns and
merge at **whole-fn granularity keyed by name** (name-grade: fn rename = delete+add; both-edit-same-fn =
conflict on the whole body). `initialData`: whole-section text compare (rarely edited post-launch).
Access rules: rule-granular; both-sides-added rules **union** (each grant was intended); same-(type,
field,verbs) rule with differing conditions = conflict.

**Grilled.**
- *Cosmetic conflicts:* both sides reorder props → `order` collisions that mean nothing. Verdict:
  per-meta-field merge **policy** — `order` auto-resolves (renormalize, taking the merge target's
  sequence as spine); `name`/`type`/`cardinality` always surface. Never bother a human with cosmetics.
- *Union-of-grants is a security decision:* deny-by-default means adding rules only ever widens access —
  union can widen more than either branch intended in combination. **SETTLED (user, 2026-07-02):
  mechanical union, but access-section changes are ALWAYS surfaced as a must-see block in the merge
  review, and the publish preview additionally shows the EFFECTIVE ACCESS DIFF on the target instance.**
  Combination effects become visible at the two moments a human already reviews.
- *Criss-cross merges* (multiple common ancestors): v1 picks the max-seq LCA and accepts extra conflicts.
  Named simplification; recursive-merge only if real usage hits it.
- *Where does resolution happen?* The working copy is shared live state — a half-resolved merge must not
  sit visible in it. Verdict: **merge rides ctx**: the merge result is a staged changeset + a transient
  conflict list in an editing context; resolving = editing staged values; commit-on-resolve produces the
  two-parent merge commit. Reuses the atomic-ctx machinery wholesale.

**Verdict.** Settled shape: lineage-keyed 3-way, per-meta-field policies, ctx-staged resolution,
two-parent merge commit. Open: access-rule union (security review), criss-cross beyond v1, fn identity
(needs code-as-data — future).

## 2. Data conflicts (session-3 topic)

**Position + grill outcome.** A commit with a stale base whose fields overlap interleaved commits is
**rejected with a conflict payload** — per field: `{base, mine, theirs}`, base read from the log (§7
makes old values available). Conflicts are **transient — wire/ctx state, never persisted rows**
(refinement of the earlier "Conflict-as-data" phrasing: it is data to the renderer, not data in the
store; persisting conflicts would invent cleanup and multi-user ghosts). The generic ObjectForm gains a
conflict mode driven by `ctx.conflicts`: conflicted fields render mine-editable + theirs-adjacent with
take-theirs affordance + a banner; resolve, re-commit with the new base. Custom `fn render()` gets the
same `ctx.conflicts` + a lib `<ConflictBar>`; a custom form that ignores it falls back to the global
error banner ("conflicting changes — reload or resolve"), so no app can silently clobber.

- **v1 coarse cut:** banner with keep-mine (force: re-commit at current base — *chosen* overwrite is
  consent, which is the whole point) / take-theirs (drop mine). Per-field UI = v2.
- **Delete/modify at object level** (they deleted the invoice you were editing): conflict offers
  restore-and-apply (identity resurrection from §0 recovery, reused) or discard.
- Set add/remove commute (no conflict); ref-set and dict entries conflict field/entry-granular; creates
  cannot conflict (fresh identity). Multi-object ctx commits stay all-or-none: resolve the conflicted
  subset, apply the batch whole.

**Verdict.** Settled: transient conflicts, ctx-driven UI, coarse-then-fine, resurrection on
delete/modify. Open: the fine per-field UI's visual design (see-it loop when built); live-edit
write-write stays last-write-wins until real-time (line already drawn).

## 3. Semantic migrations — execution model (the sharpest gap; session-4 topic)

**Position.** A migration is authored **on the commit, not in the design** — it describes a *transition*,
not a state (putting it in the design doc would make it "always true"). `Commit.migration` holds Code
source: per-type transform fns, e.g. `migrate Invoice(old) { total = old.net + old.tax }`.

**Execution pipeline, per object:** `new = structuralTransform(old)` (renames carry values, adds fill
defaults, removes drop) → semantic fns run with **`old` = the full pre-migration object (read-only,
removed fields still readable) + `oldDb` = the whole pre-migration store (read-only, for cross-object
computes) + `new` = writable result**. This dissolves the ordering problem outright: a fn computing from
a field being removed just reads it off `old`; there is no "run before the drop" sequencing to design.

- **Multi-commit publish paths:** pure-structural spans collapse into one endpoint diff; each commit
  carrying a semantic migration forces a **step** at that commit (its fns assume adjacent schemas).
  Path = collapse–step–collapse.
- **Atomicity:** migration runs against an in-memory copy; any fn throw/invalid result aborts the whole
  publish; `Save()` only on full success. Falls out of full-rewrite storage for free.
- **Where it runs:** kernel-side, C#-interpreter only, classified like floor conditions (migrations never
  execute client-side by definition; no twin conformance burden — but builtins used in migrations stay
  covered by the conformance suite via the C# path).
- **Determinism & the log:** v1 logs the **materialized migration changeset** (one big entry — correct
  regardless of fn purity, once per publish). A macro-entry ("re-run migration on replay") is a size
  optimization later and would then require migration purity; not now.

**Verdict.** Settled: commit-attached authoring, old/oldDb/new model, collapse–step–collapse, atomic
abort, C#-side classification, materialized log entry. Open: purity enforcement (only if macro-entries
ever happen); migration authoring UX in the commit dialog.

## 4. Publish onto a live instance

**Procedure (settled shape):** take the store lock → briefly reject incoming commits with "updating"
(publishes are seconds; queueing is v2 polish) → run migration on the in-memory copy → append ONE log
entry (schema-boundary marker + materialized changeset + the design-commit stamp) → rewrite snapshot
(WAL order preserved) → **bump the schema epoch**, which invalidates in-flight drafts staged against the
old schema ("app was updated — reload"; direct generalization of the login-epoch/state-generation
pattern) → remount instance; warm sessions resync via the existing restart path. Crash-safe by the WAL
ordering; boot reconciliation replays the tail. Footnote stated as intent: **`initialData` edits never
apply to instances with existing data** — the diff shows them, the migration ignores them (seed data is
for new instances).

## 5. Floor-over-history (the spicy one, resolved on principle)

**Question:** reading data at seq N — whose access rules apply, today's or those in force at N?

**Verdict: today's rules govern historical reads.** The deciding argument is privacy tightening: data
reclassified more-private today must be protected *retroactively*, or time-travel is a leak (yesterday's
rules didn't know today's sensitivity; "it was public once" must not mean "re-servable forever").
Mechanics: current rules join historical data **through lineage** — a renamed field inherits its current
rule via identity; a field with **no current counterpart (removed) falls to deny-by-default**, i.e.
removed fields are invisible in history unless a broad rule grants them. The capability gate (user
refinement, 2026-07-02): **history reads default to "whoever can change the current rules"** — a
meta-condition in the existing ruleset machinery, NOT a new admin primitive (roles stay non-primitive),
overridable per app like any rule. Per-field floor mapping generalizes it when a history UI ships for
end users. Named consequence, accepted: rendering "the app exactly as a user saw it then" is
deliberately NOT reproducible when today's rules are stricter — that asymmetry is the security
property, not a bug.

## 6. Retention / compaction

**Settled shape:** compaction is an **explicit operator op** (`sys.compact(instance, horizon)`), not
automatic. It folds genesis→horizon into a new genesis (a checkpoint), truncates entries below, and —
per the session grill — **promotes pre-horizon commit caches from cache to record** (they're
floor-immutable rows; the promotion is documented, or compaction writes per-commit checkpoints first —
operator's choice). Per-instance policy: the **designer instance defaults to never-compact** (design
history is tiny and precious); app instances get a retention knob (default generous). The WAL tail is
always retained. Unreachable commits (abandoned branch tips) are kept in v1 — rows are cheap, GC later.
Recovery reach = the horizon, by policy, never by accident.

## 7. Undo vs history

**Settled principle:** undo = **inverse commit** — a new forward commit whose changeset inverts a prior
entry; history stays linear and honest, nothing rewrites. Design-side undo is the revert commit (§0).
**Ripple decision with triple payoff:** log entries record **old AND new** values per field write
(~2× entry size, worth it) — enabling (a) inverse-commit derivation, (b) 3-way conflict bases read
straight from the log (drafts need not carry base values — simpler than the earlier assumption), and
(c) blame/audit views later. No undo UI ships in v1; the primitive is the design.

## 8. Idempotency of commits

Commit messages already carry a correlation `msgId`; the log entry records it. Duplicate delivery
(WS retry, SSR+refetch double-fire — the analytics lesson) = per-client recent-msgId window + log
dedupe → ack-as-no-op. Small, mechanical, settled.

---

## Ripple effects on earlier decisions (pass-3 convergence)

1. **Log record shape:** post-remap changesets **with old+new values** per field write + msgId +
   who/when (+ schema-boundary marker on publish entries).
2. **Migration code lives on Commit rows** — a transition artifact, not design state.
3. **"Conflict-as-data" clarified:** transient wire/ctx data, never persisted rows.
4. **Publish = one log entry** (materialized migration changeset).
5. **3-way bases come from the log**, not from drafts.
6. Nothing dislodged: C, two layers, forward-only, identity model, caches, recovery all held through the
   passes.

## Roll-up — SETTLED (design-level)

Two-layer model; variant C storage (log/genesis/head + WAL + fsck); commits/branches/refs as data with
Figma-model snapshots; identity + relink; caches (text + id-map, zero-replay publish); forward-only
migrations with derived-structural/stored-semantic split and old/oldDb/new execution; collapse–step–
collapse publish paths; atomic publish procedure with epoch bump; lineage-keyed 3-way design merge with
per-meta-field policies and ctx-staged resolution; transient data conflicts with coarse-then-fine UI and
resurrection on delete/modify; floor-over-history = today's rules via lineage, deny-default for removed
fields, admin-gated v1; explicit compaction with cache-promotion and per-instance policy; undo = inverse
commit with old+new logging; msgId idempotency; baseVersion contention story (disjoint auto-merge,
commutative-ops ceiling); per-field **non-temporal declaration** (PII/erasure + high-churn exclusion
from the log — adopted from the pillar-4 memo, §0b).

## Roll-up — OPEN (by blocker type)

- **Needs real usage first:** criss-cross LCA beyond max-seq; migration purity/macro-entries; per-user
  (vs global) undo semantics; pagination of long histories (existing named gap).
- **Scheduled by decision (user, 2026-07-02):** fn intrinsic identity (fns stored structurally like
  types) lands **with M12 (visual component designer)** — which wants structured fns anyway — or
  earlier only on real merge pain; NOT in versioning v1 (hidden-scope guard). Postponing is cheap: the
  switch is itself a designer-schema migration the versioning machinery absorbs (boundary marker; old
  history replays under the old shape). Upgrades code merge from name-grade to identity-grade, enables
  per-fn history/blame.
- **Needs the build + see-it loop:** per-field conflict UI, merge-resolution UI, commit-dialog migration
  authoring UX.
- **Future milestone boundary (drawn, not open):** live-edit write-write contention and live-push
  rebasing = real-time milestone; cross-instance data merge = distributed future.
- **User's explicit call:** pull the bare baseVersion anti-clobber check ahead of the milestone? →
  DONE 2026-07-02 (main `4c72a92`); two narrow residuals tracked in §0. Not yet deployed to the box.

## Next

Session 5 = milestone-planner slicing. Natural spine (sketch, not the plan): log+seq+WAL behind the
store interface (invisible) → baseVersion guard → Commit/Branch rows + `sys.commitDesign` → diff +
forward publish (rename-safe — the MVP-visible payoff) → branches + merge → conflict UI → time-travel /
`cloneInstance(atSeq)`.
