# Grill — design-commit storage: B (text + stored ops) vs C (log-position commits)

Self-grill (10 rounds) run 2026-07-02, part of the M13 versioning design (session-1 material).
Design-only notes, not a build plan.

Context: designs are data in the designer instance (`Design`/`MetaType`/`MetaProp`). The question
is what a design *commit* stores. **B** = canonical printed text + a stored list of structural ops
(renames etc.) + semantic `fn migrate`. **C** = a small ref row marking a position in the designer
instance's own append-only data log; snapshots reconstruct by replay; canonical text cached
(derived, never authoritative). Option A (full graph copy per commit) was already eliminated —
per-commit duplication in a fully memory-resident store.

---

1. **Q:** B's alleged fragility — renames must be recorded at edit time, so a UI delete+recreate
   silently loses them. Is that inherent to B? **A:** No — steelman it: derive the ops at **commit
   time** by id-diffing the live graph against a kept shadow subgraph of the last commit (one
   shadow per branch, bounded). Ids exist in the live store, the diff is exact, the ops freeze
   into the commit row. Edit-time recording — and its fragility — disappears. Call it **B′**.
2. **Q:** Does B′ match C's diff quality? **A:** Within one commit, identically. Across N commits,
   B′ must **compose frozen op-chains** (rename a→b→c, add-then-rename, remove-then-recreate) — an
   op algebra with real correctness surface. C diffs two replayed endpoints directly; no
   composition exists.
3. **Q:** Stored ops vs derived diffs — why does it matter long-term? **A:** A bug in B′'s
   derivation/composition **freezes wrong history forever** (the ops are the record). C's record
   is the raw changeset log; diffs are derived, so a diff bug is fixable retroactively by
   re-deriving. Derived beats stored while the deriving code is young — and it will be young.
4. **Q:** C replays changesets recorded years earlier. What breaks on format drift? **A:** Compare
   frozen surfaces: B freezes canonical **text** — the `.deenv` language must stay back-compatible
   forever, and it grows every milestone. C freezes **StoreModel-level changesets** — a closed
   union of four value kinds, stable since d13def5, framework-controlled. C's frozen surface is
   smaller and slower-moving.
5. **Q:** And when the *designer's own* schema evolves (e.g. MetaProp gains `origin`)? **A:** Same
   tolerance non-destructive apply already grants — absent field reads as default. A
   designer-publish becomes a schema-boundary **marker** in the log; replay across it needs marker
   + tolerance. B's commit rows migrate through the same publishes — both live with this; C
   exposes more surface; framework-controlled either way. Requirement, not blocker.
6. **Q:** C logs every autosave keystroke; B′ stores O(commits). Log explosion? **A:** Human-paced
   design editing: keystroke-grain years ≈ low-100k entries ≈ tens of MB, replay µs/entry —
   comparable to B′'s ~5MB of texts at 500 commits. Neither dies; retention/compaction stays a
   named ceiling. C additionally reconstructs *any* moment, not just commits.
7. **Q:** Hidden C cost: **every** store write path must log or replay silently diverges —
   boot-sync writes too? **A:** Yes — choke point at the store mutation layer (every write already
   funnels to `Save()`); the authority inversion kills the boot-sync overwrite anyway. The risk
   gets teeth via a cheap self-check: `replay(genesis→head) == app-data.json` — an fsck in the
   conformance-suite spirit. Must-build, small.
8. **Q:** Crash story? **A:** C's log doubles as a **WAL**: crash between log-append and snapshot
   rewrite → boot replays the tail. M1's deliberately-deferred crash-durability falls out
   platform-wide. B contributes nothing here. The log pays rent beyond versioning.
9. **Q:** Time-to-first-value — "publish survives a rename"? **A:** **B′ wins standalone**
   (shadow + id-diff + apply-ops, zero log infrastructure). C waits on log + genesis + replay. But
   the log is not optional this milestone — data-conflict detection needs the seq, pillar-4 and
   checkpoints need the log, the WAL wants it. Given the log, C's marginal cost is commit rows +
   replay + diff; B′ is a **second mechanism that goes redundant the day the log lands** — a
   parallel special mechanism next to a general one, which this project rejects.
10. **Q:** Verdict? **A:** **C — conditionally, and the condition is real: the instance data log
    must genuinely be this milestone's core.** It is (conflicts, pillar-4, durability). If design
    versioning ever had to ship standalone before log work, B′ would be the right cut. Corrected
    framing: B doesn't lose on capture fragility (B′ fixes that) — it loses on **redundancy** and
    **freezing derivation bugs into history**. C's obligations out of the grill: exhaustive
    logging choke point + replay-to-head self-check; schema-boundary markers in the log; the named
    retention ceiling.
11. **Q (round added on the user's lean):** "Commits and branches are just data like any other,
    mostly" — where does that hold, where does it break? **A:** Holds above the log, with a real
    payoff: commits/branches/diffs are rows and refs — generic-UI-rendered, client-data-layer
    shipped, reachability-GC'd, URL-linkable; and **branch working copies as plain Design rows
    dissolve git's checkout singleton** (every branch simultaneously live; switching = URL
    navigation; concurrent editing of two branches free). Breaks in exactly THREE places, all at
    the log seam: **(1)** the log itself is infrastructure below the object model — not `db.*`
    readable, not renderable, not floor-governed; **(2)** history integrity is MECHANISM on the
    log (append-only) but only POLICY on Commit rows (floor rules in the designer's app doc —
    admin/bugs could rewrite `parent` refs); acceptable solely because that app doc is
    framework-authored, so policy ≈ mechanism there — a justification, not a triviality; **(3)**
    retention couples the layers in reverse — compaction may only fold-and-truncate up to the
    oldest `logSeq` referenced by a live Commit row, so deleting a Commit (data op) changes what
    infra may discard. Plus the one obligation data-ness can't help: **log-append + `Save()`
    atomic under the same lock, fixed crash ordering** (append log → rewrite snapshot; boot
    replays a longer log's tail). Verdict: "just data, mostly" survives — the "mostly" IS the log
    seam; the design doc must name these three exceptions explicitly.
