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
only). Known flake: LogoutViewSwapTests under parallel load — verify isolated + rerun, report.
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

### A2 — dt.ts scalar-var refetch race (LIVE bug; task chip task_ebc3903d exists)
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

### B2 — Diff view between commits  [M-L; builder sonnet; reviews ui-arch + ux]
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

### B3 — Publish + dry-run from the designer  [M; builder sonnet; reviews ui-arch + ux]
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

### B4 — Branch UI + lockstep wiring  [L; builder sonnet; reviews ui-arch + ux + architecture]
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

### B5 — Fine per-field conflict UI  [L; builder opus (client-reconcile); reviews all three lenses]
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

- **Semantic migrations** — `fn migrate` + `Commit.migration` (design doc §3 settled at the
  design level; the designer-schema addition is safe now that boundary entries exist — but
  the execution model needs its implementation design pass: authoring UX in the commit
  dialog, old/oldDb/new scope wiring, collapse–step–collapse pathing).
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
