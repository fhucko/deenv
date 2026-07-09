# Distributed ACID — implementation ladder (design-ahead)

**Provenance:** 2026-07-06, user-requested rough plan for pillar 7. **Status:
design-ahead doc only — nothing scheduled; Stage 5 stays last** (user-confirmed
2026-07-06). User inputs (interview #1): capability = one instance's data
spread across multiple devices; failover = **automated is the point**;
consensus dependency choice deferred. **Grilled once (self-grill #1,
fresh-context adversarial pass, 2026-07-06)** — five claims refuted/reworked,
marked inline; verdict summary at the end. Same-day addenda: §7 cluster-config
surface + §8 prior-art comparison (user request).

**VISION CHANGE (2026-07-06, user decision — DECISIONS.md "Distributed ACID
upgraded: sharding is the destination"):** after weighing the §8 comparison,
the user upgraded pillar 7 to the **Spanner/CockroachDB class** — intra-
instance sharding, cross-shard ACID transactions, auto-rebalancing, strong
consistency. This doc was reworked the same day: what §0 called "reading 1 /
the road not taken" is now the DESTINATION; the replicated-log ladder (rungs
1–5) is unchanged and becomes the STEPPING STONE; rungs 6–8 + the
simulation-harness precondition are new. **Grill #2 ran the same day against
the new material** — verdict "must-rework" on three claims, all reworked and
marked inline (footprint-as-read-set refuted; single-shard-majority capped by
whole-graph GC; ROADMAP contradiction fixed); summary at the end. Decisions
user-fixed in the same session: sequencing stays last; CP stance unchanged;
instance-splitting stays the interim hatch; positioning rewritten (scale in
scope). **Final user refinement (same day): rungs 6–8 = far future,
conditional on traction + more developers + emerging need (the §6 gate);
today's whole obligation is foreclosure-avoidance — the rung-0 guard list.**

Companions (settled ground this doc builds on, not re-derives):
- DECISIONS.md → "Distributed ACID upgraded: sharding is the destination"
  (the vision change this rework implements).
- DECISIONS.md → "The endgame database — the storage pillars' convergence
  path" (the 6-rung north-star ladder; CAP stance; Raft-not-hand-rolled;
  sovereignty now scoped as the interim/coarse granularity).
- DECISIONS.md → "The self-hosted image — kernel, instances, and
  cross-instance data" (kernel-owned data; the fabric; owner + idempotent
  projection as the interim cross-instance answer).
- DECISIONS.md → "Multi-device is architecture, not an implementation
  detail", "Conflict handling: two different problems".
- docs/plans/app-versioning-design.md + versioning-slices.md (the store log
  this whole design stands on).

---

## 0. The shape: replication first, sharding on top of it

Two architectures were weighed (interview #1 + the vision-change session):

1. **Shard the transaction domain** — different nodes own disjoint parts of
   one instance; transactions span shards (2PC over consensus groups; the
   Spanner/CockroachDB class). **This is now the destination** (vision change
   2026-07-06). Its full cost is real and stated: cross-shard commit,
   distributed deadlock/contention handling, a distributed clock story,
   rebalancing machinery, and a verification burden that consumed specialist
   teams — answered by the simulation-first method commitment, not denied.

2. **Replicate the log; single leader per instance; disaggregate storage** —
   every device holds the instance's append-only log; one leader serializes
   commits; ACID by construction. **This is the stepping stone, unchanged
   from the first draft** — and not a detour: CockroachDB *is* architecture 2
   applied per-range plus a transaction layer. Every rung of it is load-
   bearing for the destination (a shard IS a replicated single-leader log).

The granularity story that unifies them: **the instance is the first,
coarsest shard.** Rungs 1–5 build replication/failover/placement at instance
granularity; rungs 6–8 shrink the granularity below the instance and add the
transaction layer across shards. Until rungs 6–8 land, a hot app splits into
sovereign instances + owner/idempotent-projection (the settled interim hatch,
user-confirmed — coarse sharding by hand, subsumed later, not throwaway).

Why the codebase makes the stepping stone unusually cheap (verified against
code 2026-07-06, re-verified by grill #1):

- **Live mutations flow through one chokepoint.** `JsonFileInstanceStore.Save()`
  — WAL order append-then-snapshot (JsonFileInstanceStore.cs:1787-1817).
  `LogEntry {Seq, At, Who, MsgId, NextId, Writes[], Boundary?}` is stored-level,
  schema-free, replayable without app type info (AppLog.cs:32-107).
  **CAVEAT (grill #1, front B): the log is NOT total today.** Two existing
  paths *delete* it: `Reset()` (JsonFileInstanceStore.cs:1676-1697, fresh
  publish / tests) and the unversioned `MigrateTowardSchema` re-baseline
  (JsonFileInstanceStore.cs:1103-1117 — its own comment already flags it as a
  stopgap). Versioned publish preserves the log (`SaveBoundary` appends,
  :1516-1530). See rung 0's wipe-is-an-epoch-event law.
- **A follower already exists in embryo.** `ReconcileLogOnBoot`
  (JsonFileInstanceStore.cs:197-230) replays trailing log entries via
  `AppLogReplay.Apply`; `Fsck` (:1910-1920) proves replay ≡ live doc.
  Replication = the same replay fed over a wire.
- **Seq is the single logical clock — for the replication era only.** Log seq
  = store version = OCC token = time-travel coordinate
  (JsonFileInstanceStore.cs:124-129). Single-leader needs nothing more.
  **Sharding does** (rung 6+): per-shard logs break the single total order,
  so Seq's successor is a commit timestamp (likely HLC) with a mapping story
  for time-travel and OCC — a named seam guard from the vision change: new
  features must not DEEPEN the total-order assumption.
- **Id minting survives failover through the log** [verified by grill #1]:
  `LogEntry.NextId` on every entry, restored by replay (AppLog.cs:37, Save
  :1812, AppLogReplay.cs:18). **Vision-change reversal:** the M5 per-node
  id-range reservation (DECISIONS.md:183-185) is **needed again** — sharded
  minting is multi-authority. (The first draft's "not needed" held only for
  the single-leader era.)
- **The footprint machinery is a RELATED mechanism to grow, NOT the
  transaction read-set** [corrected by grill #2, front B — the first
  claim here was refuted]: the harvested footprint is a *render/view*
  artifact (what did this view display). A commit's WRITE-set is explicit in
  the `CommitBatch` payload, but its serializable READ-set (validation
  reads, access-rule reads, OCC old-values) is server-side, narrow
  (field-overlap against `baseVersion` only, JsonFileInstanceStore.cs:582-618),
  and NOT harvested today. Distributed OCC (rung 7) needs a commit-time
  read-set tracker — new work; the render-footprint culture is the design
  precedent, not a latent asset in hand.
- **Single-store-per-file is already a kernel invariant**
  (KernelHost.cs:88-99) → single-leader-per-instance → (rung 6)
  single-leader-per-shard.
- **App code is distribution-blind** (pillar 2, settled) — and must stay so
  through sharding: the shard map is the runtime's business, never Code's.

---

## 1. What "ACID maintained across distribution" concretely means here

Replication era (rungs 1–5):
- **A**tomicity: unchanged — a commit is one `LogEntry`, applied
  all-or-nothing by replay.
- **C**onsistency: unchanged — schema/access validation on the leader before
  append (WsHandler.cs:735-877).
- **I**solation: unchanged — the leader's `_sync` critical section.
- **D**urability: redefined — acked by a quorum before the client sees
  success.

Sharding era (rungs 6–8) — each letter changes:
- **A**: a commit may span shards → atomic commit protocol (2PC over shard
  groups, each participant itself Raft-durable — the CockroachDB shape).
- **C**: validation may need cross-shard reads at a consistent timestamp.
- **I**: serializable isolation across shards = concurrency control over
  read/write-sets (the footprint machinery) + a timestamp order (HLC), not a
  single lock.
- **D**: per-shard quorum, unchanged in principle from rung 3.

CAP stance (settled; re-confirmed at the vision change): CP. Spanner and
CockroachDB are CP too — the upgrade changes shard granularity, not the CAP
position. A shard that cannot reach quorum refuses writes.

(*) Open item: whether `File.AppendAllText` (:1843-1848) actually reaches disk
on power loss (fsync semantics). Audit before any rung claims "backup".

## 2. The implementation ladder

Preconditions from the settled north-star ladder (not restated): Stage 3
real-time/multi-user proven single-machine; pillar 4 temporal versioning;
pillar 5 render-coupled engine. Sequencing user-confirmed 2026-07-06:
distribution stays LAST. Each rung is independently shippable.

**Rung 0 — seam guards (free, applies NOW to all future work).**
No code, just laws to keep true:
  - Every **live** mutation goes through `CommitBatch`/`Save()` — no new write
    path may bypass the log.
  - **Log-destroying operations are epoch events, not silent** [grill #1].
    `Reset()` and the unversioned migrate re-baseline delete the log today; a
    follower cannot detect a replaced history (a genealogy fork, not a torn
    tail). Before replication, both wipe sites become logged/announced
    genesis-reset events. Until then: add no new wipe sites.
  - `IInstanceStore` stays locality-free (AGENTS rule 6; already true).
  - `Seq` stays the only ordering truth *within a log*; `At` never becomes
    load-bearing. **New (vision change, reworded actionable per grill #2
    front C — "don't deepen" was a platitude everything already violates):
    guard the CONCENTRATION of the total-order assumption, not its
    existence.** Stage 3 and pillar 4 will rightly lean on Seq harder;
    the checkable law is: any NEW ordering-dependent feature reaches Seq
    through a narrow, swappable seam (one accessor/comparator, not raw int
    comparisons scattered ad hoc), so rung 6 can swap the coordinate type
    (HLC commit timestamp) in one place. Violation test: a new feature
    doing raw `seq <`/`==` arithmetic in its own code instead of the seam.
  - **Keep the M5 per-node id-range reservation alive** — needed at rung 6.
  - **Footprints stay precise** — the render-footprint culture is the
    precedent for the rung-7 commit-time read-set tracker (which does not
    exist yet — see §0); letting harvest precision decay to "whole
    instance" would also degrade that future tracker's ceiling.
  - **Extents are the natural shard seam** — nothing new should straddle
    extents with un-transactional atomicity assumptions.
  - One live store per data file — forward: one authority per log (instance
    now, shard later).
  - Kernel-owned data (registry, future topology) stays out of instance dbs.

**Rung H — the deterministic simulation harness (FIRST BRICK of the
milestone; user-elevated at the vision change).**
Before any consensus/replication/transaction code: a deterministic,
seed-replayable cluster simulator — simulated network (partitions, delays,
reorder, duplication), simulated disks (torn writes, fsync lies), simulated
clocks (skew, jumps), N kernel nodes as in-process state machines, and
property checks (linearizability of acked commits, no acked-write loss,
replica equivalence via the fsck invariant). This is the FoundationDB lesson
(§8) adopted as method: AI can write distributed code fast; the harness is
what makes it TRUSTABLE fast. Every subsequent rung lands with its
simulation properties first (the Gherkin-first culture, applied to physics).
**Retrofit cost, named (grill #2, front D):** FoundationDB's determinism
came from building the whole system inside the simulator from day one;
deenv's store/WS layers today do synchronous `File.*` IO on thread-pool
threads under a monitor lock, with NO injection seam for time, disk,
network, or scheduling (File.AppendAllText/WriteAllText/Move at
JsonFileInstanceStore.cs:1829-2015). So rung H is a real choice with a
real bill: EITHER abstract time+disk+network+scheduling behind interfaces
(a genuine refactor of the store and transport before any protocol code)
and get near-FDB-grade determinism, OR accept coarser message-level
simulation (protocol state machines in-process, real IO mocked at a higher
boundary) with weaker guarantees — provable: protocol safety under
partition/reorder/crash schedules; not provable: whole-binary determinism,
allocation/scheduling races. The choice is made when the rung is reached;
the refactor option is cheaper the earlier the IO seams are introduced
(a reason new storage code should take IO through an interface even now).
Scope note: the harness tests the KERNEL's distribution layer; it is not
user-facing — its budget is bought by the vision change ("fast with AI"
is honest only with it).

**Rung 1 — log-shipping follower (async replication).**
Kernel-to-kernel transport (a new trust boundary: TLS + kernel authn):
follower receives **a current snapshot + the log tail from its seq, NOT
genesis + full history** [grill #1: genesis is frozen at *first mutation*
(EnsureGenesis :1823-1832), so genesis→head is the instance's whole life;
`CloneDoc` (:1837) + the seq-addressable log make snapshot+tail cheap].
Read-only follower; stream + replay via `AppLogReplay.Apply`; correctness =
fsck equivalence cross-machine at equal seq. Also replicated (grill #1: "the
instance is a directory, not a file"): the published `app.deenv` artifact and
the content-addressed asset/blob pool (a follower with the log but no blobs
renders broken images). Follower-lag backpressure is part of the protocol.
Value shipped: live off-box standby.

**Rung 2 — synchronous replication + manual promote.**
`Save()` acks the client only after follower confirmation (sync / semi-sync
configurable). Static topology in kernel-owned data. Failover = one operator
command. **Honesty (grill #1): manual promote is NOT split-brain-safe** —
safe only under an operator-guaranteed dead old leader. The demote/fencing
epoch number is a **log-format addition** (LogEntry has no epoch today —
structural change, own approval).

**Rung 3 — consensus: automated failover.**
Leader election on a proven consensus protocol (Raft per DECISIONS;
library-vs-own deferred to this rung — option set in §5). Minimal
topology/membership moves INTO this rung [grill #1: you cannot elect among
peers you learn from an unreplicated registry]. **Raft-log/store-log
relationship = OPEN QUESTION** [grill #1 refuted "one log": Raft truncates
uncommitted suffixes, but today's `Save()` acks on append — the entry is
final on landing, and OCC/time-travel/fsck consume it immediately;
`ReconcileLogOnBoot` hard-fails a snapshot-ahead-of-log state (:215-224),
exactly what truncation produces]. Prerequisite either way: **split append
from commit** (append-uncommitted → quorum → ack+apply) or a separate
staging log promoting into the store log. Decide at this rung, in the
harness. Note: rung 6 reuses this machinery per-shard, so the choice made
here is the one that scales down in granularity.

**Rung 4 — placement + routing (the fabric proper).**
Full topology as kernel-owned data via the privileged control-plane path.
Instances placed across nodes; kernels route WS/HTTP to the current leader;
clients reconnect on failover. Named hard spots (grill #1): read-your-writes
across failover (a client acked at seq N whose new leader is at N−1 sees
data go backwards — the client journal cannot reconcile that today);
exactly-once resubmit (the dedup key is NOT `LogEntry.MsgId` — that is
`req.Id`, a nullable per-connection correlation breadcrumb, WsHandler.cs:23,
426; a real idempotency key is clientId + client-monotonic-seq or
server-minted); the design-host is a special case (it mutates other
instances via host actions — own design pass). Fault/resource isolation
between instances becomes real here.

**Rung 5 — disaggregated storage.**
Log segments, snapshots, and cold blob content live on devices the leader
pages from; the render-coupled engine (pillar 5) decides materialization and
preload. An instance's dataset may exceed one device's disk; the leader
holds the working set + the tail.

--- the sharding era (the vision-change extension; UNGRILLED, grill #2) ---

**Rung 6 — range-ification: shards below the instance.**
The instance's single log splits into per-shard logs (natural first split:
per-extent, then id-ranges within an extent — the M5 monotonic ids make
range boundaries meaningful), each shard = a Raft group reusing rung 3's
machinery, placeable independently by rung 4's fabric. Single-shard
transactions stay as fast and simple as rung 3 — **but the "overwhelming
majority single-shard" hope is capped, not assumed (grill #2, front A):**
creates and field edits are genuinely single-shard, while **any commit
that removes a reference triggers today's whole-graph mark-and-sweep GC**
(`CollectGarbage` recurses from root across ALL extents,
JsonFileInstanceStore.cs:2188-2224, and runs on every removing mutation —
removeFromSet :873, clearRef :909, orphaning setRef :978). Under sharding
that is an inherently cross-shard sweep riding an ordinary delete.
**Cross-shard GC is therefore a rung-6 BLOCKER to design, not a footnote**
(candidate shapes when reached: deferred/async GC epochs, per-shard
refcounts with cross-shard escrow, or reachability snapshots at closed
timestamps). Also requires: the commit-timestamp successor to Seq (HLC) +
the time-travel/OCC mapping story; per-node id-range allocation activated
(the M5 reservation spent here); the shard map as kernel-owned data (a
projection of consensus state).

**Rung 7 — cross-shard ACID transactions.**
Atomic commit across shard groups: 2PC where every participant is itself
Raft-replicated (coordinator failure ≠ blocked participants — the
CockroachDB/Spanner shape), serializable isolation across shards. **Honesty markers (grill #2, fronts
B+E):** the write-set is explicit in `CommitBatch`; the commit-time
READ-set tracker does NOT exist yet (the render footprint is a view
artifact, §0) and is this rung's real new mechanism. And "validation at
HLC timestamps" is a category, not a recipe — the known-sound instantiation
is the CockroachDB transaction stack (commit-timestamp assignment,
read-refresh/retry when a timestamp gets pushed, write-intent resolution,
closed timestamps), which is where those systems spent years; §5 carries it
as its own open item. Every protocol decision lands in the harness first. This rung retires the interim instance-splitting hatch — and the
owner/projection pattern remains available as an *optimization* (avoiding
coordination is still cheaper than doing it well).

**Rung 8 — automatic splitting + rebalancing.**
Hot/large shards split and move without operator action (load + size
thresholds); the placement UI (§7) becomes observability over an automatic
system with manual override, completing the "add a node → capacity and
throughput grow" property (§8's CockroachDB column, now a deenv property).

## 3. What deliberately does NOT change

- Application-conflict handling (`ctx.conflicts`, OCC field conflicts, the
  ConflictBar) — orthogonal to replica agreement (DECISIONS "two different
  problems") at every rung, including 6–8: two humans editing one field is a
  UX event, never a protocol event.
- The storage interface — replication/sharding live below `IInstanceStore`
  at the log layer; the interface stays locality-free. (Grill-#1 caveat
  stands: the kernel above it learns "which node leads what" for routing.)
- App Code and the wire — distribution-blind through ALL rungs; the shard
  map is never visible to Code.
- CP / strong consistency — re-confirmed at the vision change.

## 4. Non-goals and ceilings (post-vision-change)

- ~~No intra-instance sharding~~ **superseded 2026-07-06** — sharding is now
  rungs 6–8. The *interim* ceiling stands until rung 6: one instance's write
  throughput = one leader's throughput; the hatch = split into sovereign
  instances + owner/projection (user-confirmed interim answer).
- **No multi-master / offline-writer merging — still out, permanently.**
  Divergence-with-reconciliation stays pillars 3+4's job where divergence is
  *allowed* (branches), never silent. Sharding adds authorities by
  PARTITIONING the data, never two authorities over the SAME datum.
- **Geo-locality (regional shard pinning) = a rung-8+ property**, noted not
  designed.
- **No TrueTime.** Commodity VPSes → HLC + explicit uncertainty handling,
  the CockroachDB path, not Spanner's hardware clock.

## 5. Open items (deferred to their rung, listed so they don't vanish)

- Local fsync/crash-durability audit — **DONE 2026-07-06, finding recorded:**
  - **Process-crash durability: SOLID.** `AppendLogEntry` =
    `File.AppendAllText` (:1843-1848), `SaveRaw` = write-tmp + `File.Move`
    with retry (:1995-2021), genesis same pattern (:1826-1831). Once the
    append returns, data is in the OS page cache, which survives a process
    kill; the WAL order + `ReconcileLogOnBoot` + torn-final-line repair
    cover every process-crash interleaving. This — the overwhelmingly common
    failure — is handled correctly today.
  - **Power-loss durability: NOT guaranteed.** Nothing fsyncs
    (`Flush(flushToDisk:true)` / `FileOptions.WriteThrough` absent from both
    paths), so on power loss / host failure an ACKED commit can vanish from
    the page cache (Linux flushes dirty pages on a ~30s cadence). Worst
    ordering: snapshot rename made durable while the log append wasn't →
    boot hits the loud snapshot-ahead-of-log `StoredDataException` (:215-224)
    → instance parks failed (per-instance boot isolation) — visible, not
    silent, but bricked-until-operator-fix.
  - **Minimal robust fix when wanted: fsync the LOG APPEND only** (open the
    log via FileStream and `Flush(true)` per entry; ~0.5-5ms/commit on VPS
    SSD). The snapshot and genesis need nothing: the log is the truth and
    the snapshot self-repairs from it on boot (behind-log = normal repair
    path). Residuals after that fix, accepted and named: a torn snapshot on
    a non-ext4-like fs = loud boot failure (parks, repairable by deleting
    the snapshot — replay rebuilds it); the very FIRST entry of a
    freshly-created instance can still vanish (directory-entry durability —
    .NET has no portable dir-fsync); both are edge-of-edge and visible.
  - Status: finding only — the fix is a write-path latency change on the
    trust floor, **user's call, not applied**.
- Convert the two log-wipe sites (`Reset`, unversioned migrate re-baseline)
  to epoch/boundary events — prerequisite for any replication rung.
- Log compaction (versioning backlog) must preserve the **snapshot + tail**
  join point — design with rung 1's join protocol.
- Split-append-from-commit vs separate staging log (rung 3; choose the form
  that scales down to per-shard at rung 6).
- Raft grouping granularity pre-rung-6: per-instance group vs per-kernel
  multiplexed. (At rung 6 the answer becomes per-shard with multiplexed
  transport regardless — CockroachDB multiplexes thousands of groups.)
- Consensus dependency — user-deferred to rung 3; option set: (a) in-process
  library — dotNext.Net.Cluster, verified actively maintained under the .NET
  Foundation as of 2026-06 (6.4.0, .NET 10, ships a WAL + failure detection);
  (b) own Raft with the harness as its proof; (c) external DCS/lease (the
  LiteFS-Consul / Patroni-etcd pattern) — likely REJECTED when reached (an
  external coordination service contradicts the one-binary devops thesis)
  but named as the industry's most common shape.
- Idempotency key for resubmit-across-failover (clientId + monotonic seq;
  `req.Id` is not it).
- Read-your-writes / client-journal behavior when a promoted leader is
  behind the client's acked seq.
- Design-host replication (host actions mutate other instances) — own pass.
- Inter-kernel TLS + authentication; follower-lag backpressure.
- Kernel binary-version skew across replicating kernels (log/wire format
  versioning discipline).
- **Rung 6–8 era (grilled once, grill #2):** the HLC commit-timestamp
  design + its time-travel/OCC mapping; shard-map representation as
  kernel-owned data; **cross-shard GC — promoted to a rung-6 BLOCKER**
  (whole-graph mark-and-sweep rides every removing commit today; see rung
  6); **the commit-time read-set tracker** (does not exist; the render
  footprint is a view artifact — rung 7's real new mechanism); **the
  transaction-timestamp stack** (commit-ts assignment, read-refresh/retry
  on push, write-intent resolution, closed timestamps — the CockroachDB
  machinery behind "§1 I", not free once HLC is chosen); cross-shard schema
  publish/migrations (a publish touches all shards atomically — the
  migration boundary entry meets 2PC); harvest/footprint semantics across
  shards; contention/deadlock policy (wound-wait vs timestamp-ordering);
  the temporal-versioning (pillar 4) interaction — per-shard logs must
  still answer "the db as of T" globally.

## 6. Sequencing honesty

User-confirmed 2026-07-06: the roadmap order stands — usable MVP → Stage 3
real-time → Stage 4 engine/temporal → Stage 5 distribution (harness first,
then rungs). The vision grew; the order didn't. Items with *current* force:
rung 0's seam guards (now including the three vision-change guards:
total-order shallowness, id-range reservation, footprint precision).

On pulling rung 1 forward as a better backup story: grill #1 called the
first framing a hidden ladder-reorder (cross-kernel transport + TLS/authn +
wire versioning + the wipe-site conversion are its real costs). It stays a
vision-keeper question, recorded not proposed.

**The sharding-era gate (USER-SETTLED 2026-07-06, superseding grill #2's
proposed harness-only checkpoint).** Rungs 6–8 are **far future and
conditional**, entered only when three things are true together:
**(a) traction** — the project has real adoption; **(b) team** — more
developers than the steward carry the trust floor (the bus-factor-1,
verification-bottleneck, and maintenance-tail failure modes are all
functions of "solo," and the gate requires them dissolved, not braved);
**(c) emerging need** — a real app is actually pressing against the
single-leader ceiling, so the field-hardening loop has users to run on.
The harness demonstration (single-leader consensus proven under injected
faults) remains the *technical* precondition inside that gate. Until the
gate opens, the instance-split hatch is the scaling story and the
sharded destination is held open **purely by foreclosure-avoidance**:
today's obligation is the rung-0 guard list, nothing more. This is the
settled resolution of grill #2's front-G economics tension: the summit
stays on the map; nobody climbs it alone.

## 7. Cluster configuration — the operator surface (sketch, ungrilled)

**Status: derived from settled ground (kernel-owned data +
kernel-as-restricted-instance, DECISIONS "The self-hosted image"); the
specific fields/actions are assumed sketch-level, to be re-derived at each
rung.** The frame is NOT open — it is the settled answer to "where does
kernel-governing config live and how is it edited": kernel-owned data,
rendered by the designer through the *same* generic UI, mutated only via the
privileged control-plane path. No one-off C# admin panel (the M4 trap,
named in DECISIONS — the exact mistake this section exists to pre-empt).

**Two layers, thin floor first (the settled bootstrap discipline):**

1. **Bootstrap floor — a plain file the kernel reads without the
   interpreter.** `kernel.json` grows a `cluster` section: this node's
   identity + listen address, seed peer address(es), and a join credential.
   It must exist before any instance runs (lifetime argument) and must not
   live in an instance (authority argument). Nothing else belongs here — the
   minimality test is "needed to assemble the system before instances run."
   Joining a new box is the devops thesis's one act: put the kernel there,
   point it at a peer, start it. Everything further happens from within.

2. **Everything else — kernel-owned data behind the restricted
   kernel-instance.** The designer renders it like any data (self-describing
   all the way down):
   - **Nodes**: the generic SetTable over the peer set — address, role
     (leader/follower per instance, later per shard), liveness/lag/last-seq
     as *read-only* fields (observability rides the same surface, no
     separate dashboard).
   - **Per-instance replication policy**: replica count, placement, sync
     mode (async / semi-sync / quorum). **Minimal-by-default is the law
     here**: a single-node kernel has NO cluster config at all and behaves
     exactly as today; policy fields appear with working defaults
     ("replicate everywhere, quorum acks") only once a second node exists —
     you configure only to deviate. At rung 8, placement/splitting go
     automatic and this surface becomes observability + override.
   - **Mutations = host actions** (`sys.` control-plane, same shape as
     `sys.publish` today): add/remove node, place/move an instance, promote.
     Dangerous connectivity edits get the router-style
     commit-confirm-or-auto-rollback DECISIONS already anticipates for
     kernel-instance writes (a bad edit can partition the kernel from
     itself).
   - **Access**: gated by the same `sys`-rule access mechanism the security
     hardening built — cluster ops are operator-only by an ordinary access
     rule, not a special code path.

**Per-rung shape:** rungs 1–2 need only the bootstrap file + a static peers
table (manual promote = one host action). Rung 3 moves *membership truth*
into consensus (the file keeps only seeds; the table becomes a projection of
the consensus state — same owner-plus-projection move as everything else).
Rung 4 adds the placement UI; rungs 6–8 add the shard map (read-mostly) and
auto-placement observability. The operator surface does not change shape
across rungs — only which fields exist and who owns their truth.

## 8. Prior art — where this design sits (synthesis, verified 2026-07-06 where dated)

Post-vision-change reading: deenv **starts** in the replicated embedded
single-writer family (rungs 1–5) and **grows into** the distributed-SQL
family (rungs 6–8). The stepping-stone era has battle-tested small-team
analogues for every rung; the sharding era's analogues are the specialist-
team systems — which is exactly why the harness (rung H) is the method
commitment that makes the growth honest.

| System | What it is | Maps to | The lesson / the difference |
|---|---|---|---|
| **Litestream** | async SQLite-WAL streaming to object storage (revamped 2025, alive) | rung 1 minus the live follower | A shipping-the-log standby is a *standalone product* people trust — rung 1 has independent value. Difference: deenv ships to a live kernel, not S3; an object-storage target is a conceivable rung-1 variant for pure backup. |
| **LiteFS** (fly.io) | transparent SQLite replication; **single primary via Consul lease**, read replicas, proxy routing (alive, pre-1.0) | rungs 1–4 in miniature | The closest whole-shape cousin for the stepping-stone era: single-writer + log shipping + routing, app unaware. Differences: page-level replication vs deenv's semantic `LogWrite` entries; lease-based failover (bounded ~10s write outage, external Consul) vs deenv's in-kernel consensus goal. |
| **rqlite / dqlite** | SQLite + Raft (statement-level vs WAL-frame-level replication respectively) | rung 3's open question, both answers | The two ways to marry Raft to an embedded store, in production. deenv's stored-level `LogWrite[]` is the dqlite side (deterministic replay of physical-ish writes) — statement replay (rqlite) is off the table since deenv's log is already below Code. |
| **Postgres streaming replication + Patroni** | physical WAL shipping; async → `synchronous_commit` quorum → automated failover via external DCS | the ladder rungs 1→2→3, exactly | The industry-standard proof that this *ordering* is right (async ship → sync ack → automated promote). Difference: Patroni externalizes consensus to etcd/Consul; deenv's one-binary devops thesis wants it in-kernel. |
| **Aurora** | "the log is the database": single writer, disaggregated quorum storage | rung 5 | Validates log-first disaggregation. deenv's twist: the *render-coupled engine* chooses what to materialize — Aurora has no equivalent because it cannot know the app. |
| **Spanner / CockroachDB** | sharded ranges, per-range Raft/Paxos, 2PC across ranges, TrueTime/HLC | **rungs 6–8 — the destination** (upgraded 2026-07-06 from "road not taken") | The proof the destination exists and the map of its price: cross-group transactions + a distributed clock + rebalancing machinery. deenv's route there is theirs in reverse-discovery order — per-shard groups (their range = our rung-6 shard; our instance = the first coarse range) then the txn layer. Clock: HLC (CockroachDB path), never TrueTime hardware. |
| **FoundationDB** | separated transaction/storage planes; famous **deterministic simulation testing** | **rung H — promoted to the milestone's first brick** (user decision at the vision change) | The portable lesson is the testing culture: consensus-adjacent code verified by deterministic fault-injected simulation, replayable from a seed. This is the price of admission for rungs 3 AND 6–8 regardless of library-vs-own, and the thing that makes "fast with AI" honest. |
| **CouchDB / PouchDB** | multi-master, revision trees, sync + app-level conflict docs | the still-rejected divergence model | What multi-AUTHORITY (vs multi-shard) looks like: every app inherits conflict documents. deenv shards authority by partitioning, never duplicates it; divergence stays confined to *branches* (pillars 3+4). |
| **Kafka (ISR, `acks=all`)** | replicated log as the whole product | rung 2's ack knob | The async/semi-sync/quorum ack spectrum is standard, well-understood territory — nothing novel to design there, only defaults to choose. |

**"Could we just use one of these?"** No, for storage — they replicate
SQLite/Postgres pages, and deenv's store is its own document model with a
future render-coupled engine (pillar 5 is the point of owning the store). The
adoptable pieces: the consensus building block at rung 3 (option set in §5)
and the *patterns* everywhere else, each with a production existence proof.
The genuinely deenv-specific assets going INTO the sharded era: the semantic
stored-level log (already below Code, already replayable), the harvested
footprint as a native read/write-set, and the render-coupled engine as the
latency-hider no general-purpose system can copy.

---

## Self-grill #1 verdict summary (2026-07-06, fresh-context adversarial pass)

Ran against the PRE-vision-change draft (single-leader destination); its
verdicts carry into the stepping-stone rungs unchanged.

Held: replication-first over sharding-first as the *path* (survives the
vision change — sharding moved from non-goal to destination, but the route
still goes through the replicated log); the ACID letter analysis (§1
replication era); NextId-through-the-log (verified in code); rung-0 guards
for the live path; conflict-handling orthogonality (§3).

Refuted/reworked (all folded in): log-totality claim (Reset + unversioned
migrate wipe the log → wipe-is-an-epoch-event law, rung 0); Raft-log ≡
store-log unification (incoherent under ack-on-append `Save()` → rung-3 open
question with the split-append-from-commit prerequisite); `MsgId` as dedup
key (it's a per-connection correlation id → real idempotency key needed);
genesis-replay join (genesis = first-mutation freeze → snapshot+tail is rung
1 core); rung-1 severability pitch (hidden ladder-reorder → vision-keeper
question, §6).

Added on the grill's missing-pieces sweep: asset-pool + `app.deenv`
replication, design-host special case, topology-before-election ordering,
read-your-writes-across-failover, inter-kernel TLS/authn + backpressure.

## Self-grill #2 verdict summary (2026-07-06, fresh-context adversarial pass on the sharding-era additions + vision-doc edits)

Held: the ladder-reuse thesis (a shard IS a replicated single-leader log —
rungs 1–5 aren't a detour); the 2PC-over-Raft-groups topology and the
HLC/no-TrueTime call; the vision-doc edit set's internal consistency
(VISION/STAGES/ROADMAP:468/README/DECISIONS-new-section agree; AGENTS rule 1
still correct — it scopes current work, not the destination).

Refuted/reworked (all folded in above): **"the footprint is already the
transaction read/write-set" — REFUTED**, the grill's most important fix (a
render footprint is a view artifact; the commit-time read-set tracker does
not exist — §0, rung 0, rung 7 rewritten); **rung 6's "overwhelming majority
single-shard" — capped** (whole-graph GC rides every removing commit →
cross-shard GC promoted to a rung-6 blocker); **ROADMAP "the only storage
milestone" — live contradiction fixed**; the total-order seam guard reworded
from platitude to a checkable concentration rule (one Seq accessor seam);
rung H's retrofit cost named (no IO/time/scheduling seams exist — abstraction
refactor vs weaker message-level simulation, chosen at the rung); §1-I's
"validation at HLC timestamps" tagged as a category whose sound instantiation
is the CockroachDB txn stack (own §5 item); DECISIONS:1925 sovereignty clause
got its interim pointer.

Open from the grill: the §6 re-evaluation checkpoint (proposed, pending user
confirmation); the economics question it guards — the entry wedge needs none
of rungs 6–8, so the sharding era must keep earning its place at each gate
rather than being assumed.
