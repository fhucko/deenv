# Grill — versioning design, whole-session results

Self-grill (10 rounds) run 2026-07-02 over the accumulated session decisions (two-layer model,
variant C, forward-only migrations, identity model, caches, recovery). Hunts cross-cutting holes
between decisions made in isolation. Companion to `grill-b-vs-c-commit-storage.md`.
Design-only notes, not a build plan.

---

1. **Q:** Commits snapshot "the working copy at head seq" — what else is in the snapshot besides
   the committer's work? **A:** Other sessions' in-flight live edits (autosave writes land
   immediately; only ctx edits stage). No per-user index exists. That's the **Figma model, not
   git** — shared live working copy, commit = snapshot of shared state. Fine, fits the product,
   but must be documented loudly: staging will not protect git-minded users.
2. **Q:** "Ids are stable across replay" — true given how creates work? **A:** Only if the log
   records **post-remap changesets**. The wire carries transient negative ids remapped to real ids
   inside `CommitBatch`; logging the wire form makes replay re-mint different ids — silently
   breaking identity, diffs, and recovery. The logging choke point sits AFTER remap, inside the
   store. Load-bearing implementation constraint.
3. **Q:** "The log is the only non-derivable artifact" vs "compaction truncates" — coexist?
   **A:** **They contradict at the compaction boundary**: after truncation, old commits' cached
   `{text, id-map}` can no longer be rebuilt or fsck'd — caches silently become records. Resolve
   explicitly: either accept the promotion (caches are floor-immutable rows; document it) or have
   compaction write a checkpoint per retained commit before truncating. No unqualified invariant.
4. **Q:** "Structural merge, fn-level" — what is a function's identity? **A:** Its **name**. Code
   sections are opaque text; no MetaProp-style identity exists for fns, so a fn rename across
   branches merges as delete+add and same-fn edits conflict at whole-fn granularity in v1. Schema
   merges are identity-grade; **code merges are name-grade**. True fn identity needs code-as-data
   (future). Name the asymmetry in the doc.
5. **Q:** Semantic `fn migrate` — runs where, against what, in what order? **A:** **Undefined, all
   three.** Presumed: kernel-side C#-only (classified like floor conditions, no twin conformance);
   old snapshot read-only while the new doc builds; ordering matters (`total = net + tax` with
   `tax` being removed must run BEFORE the structural drop). Sharpest unsolved piece — session 3/4
   first agenda item.
6. **Q:** Field-level 3-way conflicts — coverage? **A:** **ctx commits only.** Live single-field
   autosave writes stay last-write-wins (each is its own micro-commit), exactly as today —
   deliberately deferred to the real-time milestone. Draw the line explicitly.
7. **Q:** `origin` links — object references? **A:** **No — lineage-id VALUES (plain int, chased
   to root).** Branches get deleted; origin-as-reference would dangle or block GC.
8. **Q:** Publish onto a live instance (warm sessions, in-flight commits)? **A:** Sketched, not
   solved: needs quiesce-under-store-lock → migrate → schema-epoch bump invalidating in-flight
   drafts → remount. Footnote: `initialData` edits never apply to instances with existing data —
   diff shows them, migration ignores them; state as intent.
9. **Q:** Scope smuggled into the MVP? **A:** Clean — design-only session, gate #3 untouched. One
   named candidate to pull ahead (user's explicit call, not folded in): the `baseVersion`
   anti-clobber check, which fixes a CURRENT silent-clobber gap. Untouched all session and still
   open: **floor-over-history** (whose access rules govern reads of the past).
10. **Q:** Verdict? **A:** Architecture held — nothing dislodged C, the two layers, forward-only,
    or identity; rounds 1–2 hardened them (Figma-model commits, post-remap logging). The clean
    narrative oversold two spots: log-only-truth needs its compaction asterisk (r3); "structural
    merge" is identity-grade only for schema, name-grade for code (r4). Genuinely unsolved core:
    semantic-migration execution model (r5), publish-under-load (r8), conflict UI, floor-over-
    history, retention shape. **The design doc must carry the settled-list AND this open-list with
    equal prominence.**
