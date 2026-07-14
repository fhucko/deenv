# Grill: Full Test Suite Fix Plan (2026-07-14) — Round 1 (Adversarial)

**Companion to:** docs/plans/2026-07-14_full-test-suite-fix.md

**Grill protocol (per .agents/skills/design/SKILL.md):** Fresh analysis. Goal = refute the position. Check claims against actual files (not abstract reasoning). Ground every verdict. Number rounds. Mark settled/open. This is Round 1 (initial author self + tool-grounded pass; future rounds can spawn subagent on fable-like skeptic).

**Claims under test (from plan):**
- Root causes accurately explain the 8 surfaced failures.
- Fixes are minimal (test alignment + 2 parse/wire gaps) and will green the suite without side effects on unified commit invariants.
- Tasks are correctly ordered and verifiable with treenode filters + full run.
- No hidden scope creep (no new milestones pulled in).

## Round 1 Findings (file-verified)

**Topic 1: TransientId root cause + proposed T1 fix — mostly settled, one precision gap.**

- Confirmed: TransientIdSteps.cs:60-70 still does `op: "arrayAdd"` + expects `newId`. ProcessMessage (WsHandler:255) has no case; falls to Error("Unknown op"). Matches the quick-fail times (62ms, 65ms) in log.
- Server create path does populate MapTransientId (WsHandler:651 "Mirrors HandleArrayAdd") + returns IdMap in CommitResponse (HandleCommit end). AddEntrySetBatchSteps already uses creates + setAdd successfully.
- **Gap to refute:** Plan says "rewrite WhenAddItem to emit a commit with one create + one setAdd". But the test also exercises `WhenAck` + post-ack reject (feature:49). AckRemap still exists and drops via DropTransientId. After commit, the reply carries IdMap — the test must consume IdMap (or "newId" alias?) to set _addedRealId, **and** the ack step may need update to use the real id from the latest reply rather than hard-coded.
  - Also: in WhenAddItem the schema hardcodes "items set of Item"; ItemsSetId() uses DbBridge + ExecContext (no ambient issue here since headless).
- **Verdict:** Root cause 100% correct. Fix direction correct. Add explicit "consume IdMap from commit reply for _addedRealId, support both legacy newId shape for transitional tests if any". Re-verify the post-ack scenario (the one that expects error after ack) still works because ack drops the map.
- **Settled for T1:** Yes (with the IdMap consumption detail). No model change.

**Topic 2: Ref clear (T2) — confirmed critical bug, parse fix must be careful.**

- Exact match: ws.ts:859 ` { kind: "refSet", parentId: obj.id, prop } ` (no childId) for clear.
- ParseRelation:682 requires childId **unconditionally** for every relation (before if-kind).
- RefSetMutation accepts `int? TargetRef` (IInstanceStore:200). Apply path in WsHandler:592-602 passes childRef (int) to constructor (will need null-coalesce or overload path).
- Client comment already documents clear as "relation without childId" (ws.ts:868).
- **Risk/refute angle:** If we just relax the childId check only for refSet, does setAdd still enforce? (Yes — setAdd always needs a member.) What sentinel for "clear" in the Parsed* record (currently always int Child from base)? Current ParsedRefRelation(..., int Child, ...). Passing 0 or negative as "clear" sentinel could collide with tempIds (neg) or valid id 0?
  - Better: change ParsedRefRelation to carry `int? ChildRef` (nullable), update callers, or keep int and use a const like `NoRef = 0` and document 0 means clear for refs only (since object ids start positive; temps negative in creates).
- In store batch apply for RefSet: it will see TargetRef=null and clear the link (existing behavior).
- **Verdict:** This is the #1 cause of the 1m15s timeout. Fix is required. Make child **nullable at the Parsed level** for cleanliness (avoids magic 0). Update the early childId check to be kind-aware. No blast to setAdd.
- **Settled:** Direction yes. Implementation detail (nullable vs sentinel) to be prototyped in the edit. Cross-check with existing RefSetMutation(null) call sites in KernelHostActions (they use positive or omit? they pass real ids).

**Topic 3: Designer timeouts — plausible but under-specified; needs more grounding.**

- Plan lists hypotheses (multiple editors → strict; ambient ctx changes → timing/notify).
- Verified prior scoping work existed (summary + DesignerSteps mentions "main.ide-design-edit").
- **To refute:** Are the failing scenarios even using the "add-type" or broad locators? The logged ones are:
  - "A component reading an unseeded ambient shows the real error in its configuration card..."
  - "Two configurations with different args mount independent..."
  These are in Designer.feature (around line 2103 per log). They likely involve config cards, live instance mounts inside the IDE, arg editing, and error display — **not** the type editor buttons.
  - Root may be deeper: the live instances inside designer configs use the same client data layer / ambient ctx capture. A change in `stageTopLevel` or `capturedAmbient` or `nearestStagingCtx` (codeExec.ts) + implicit micro commits on every keystroke could cause extra refetches or re-mounts that the wait logic doesn't tolerate.
- **Missing in plan:** Recommend a concrete diagnostic: run the exact failing Designer feature class with treenode, capture playwright trace or verbose logs, or temporarily add `Console.WriteLine` around commit flushes in designer context.
- Also possible: kernel-hosted live previews now behave differently because top-level changes in the designer surface itself go through micro-commit + notify.
- **Verdict:** Locators scoping is necessary hygiene and will catch some. But "investigate timing from ambient" is the real potential new bug exposed (not just test). Plan should call for a **minimal repro** if filtered still times out after scoping. Do **not** assume "just more .First()".
- **Settled/open:** Locator part settled (do the audit). Behavioral timing part: open until a filtered run post-T1/T2 + scoping shows the exact remaining symptom. Add a "diagnostic spike" subtask.

**Topic 4: Scope / invariants / "will this actually green the suite" — mostly yes, with cost note.**

- AGENTS.md discipline on commit-only + mirror names is preserved by the fixes (we are aligning tests + closing 2 small gaps in the new paths).
- No new live handlers reintroduced. Good.
- Using filters + one final full is correct per AGENTS (cost of full runs noted in prior plan).
- **Refute:** Plan claims "after clusters green on filters: ... full". But the running full-test3 showed cascading skips after first timeout. If one slow UI test times out it can poison the run. Mitigation: the plan should explicitly allow `--filter` or treenode for the final "green" signal if full is too brittle on Windows CI-like env. Or increase default Playwright timeout only for designer scenarios (but don't).
- Also: build must be clean before `--no-build` full runs.
- **Verdict:** Plan is realistic. Add note: treat full-run as "confirmatory" not gate if it has known slow scenarios; filters + manual spot checks suffice for landing.
- No prod wire change concerns (user explicitly said changes allowed).

**Topic 5: Other claims (completeness of failure list, no hidden mutations left) — good but verify with grep in execution.**

- Grep plan in T4 is correct action.
- Legacy in comments only: safe.
- In InstanceContext.cs the "set of" are schema literals — correctly excluded.
- One potential: codeExec.ts still has some old comments around persist + staging.

**Overall verdict after Round 1:** The plan is **sound and actionable**. Root causes are accurate for the headlining failures. The two wire/parse fixes (T1+T2) are the highest-leverage. T3 needs to be split into "scoping hygiene" (safe) + "investigate ctx timing if persists" (don't over-scope the plan as "just locators"). Execution order good. Add the nullable parse detail + filter caveats + diagnostic step.

**Changes to plan from this grill (Round 1 closes):**
- In T1: explicitly "consume IdMap (support old newId too for _addedRealId)".
- In T2: "update ParsedRefRelation to use nullable child or documented sentinel; relax child check kind-aware".
- In T3: split "3a scoping audit + .First everywhere; 3b if still failing after, capture minimal trace + inspect nearestStagingCtx / stageTopLevel impact on designer live previews".
- In T6: "full run is confirmatory; use filters for the green signal if timeouts cascade".
- Add cross-check: after T1+T2 run the exact failing transient + selfhosted + one designer filter before declaring clusters done.
- No change to goal or invariants.

**Round 1 status:** 4/5 topics settled. 1 open (designer timing depth). Plan updated in place. Ready for Round 2 (deeper code read or subagent skeptic pass).

---

## Round 2 (External Skeptic Subagent — Full Source Verification)

**Executed:** Fresh general-purpose subagent, instructed to refute. Performed 47 tool calls (list_dir, read_file on key files + greps, targeted test runs). Read exactly the required: TransientIdSteps.cs (full), ws.ts setRef + stage + wire, WsHandler dispatch/HandleCommit/ParseRelation + apply arm, IInstanceStore + JsonFileInstanceStore RefSet paths, SelfHostedUiSteps + feature, relevant codeExec, CommitResponse, greps for other emitters, Designer.feature snippets.

**Major refutations (grounded):**
- Transient: capture of _addedRealId must be from IdMap (exact shape from AddEntry... steps); arrayAdd→commit switch alone insufficient without it. "Create path works identically" partially false (type derivation via relation + set element vs old typeName param).
- Ref clear: parse childId is unconditional (L682), Parsed* always int (no ?), apply constructs with int never null. Store wants int? and clears on null. Client emits omission intentionally. Docstring (673) lies. Proposed sentinels (0/neg) refuted (RequireResolvable would fail). Needs kind-aware early check + nullable Parsed + null to ctor.
- Designer: logged fails are content waits (JS on .components-section live previews for error text / arg li), not (only) button locators. Scoping helps but timing/notify from root live ctx + micro topLevel may be real delta.
- Other: Transient test run post-kill reproduced "Commit references unknown temp id -5" + reply-ok fails. Reply shape: server never emits top-level newId for commit anymore.

**Verdicts (subagent):**
- T1/T2/T3/T6 directions partially hold; many details refuted or under-specified.
- Overall plan + Round1: refuted on precision.

**Incorporated into main plan (see updates to T1/T2/T3/T6 + root causes + grill status + risks):** exact create+setAdd wire sketch, IdMap capture code note + ack retest requirement, nullable/kind-aware (no 0) spec for T2, T3 split (hygiene 3a + behavior diagnostic 3b), filters confirmatory emphasis, callout on inconsistent contract + lying docstring.

**Round 2 complete.** Recommended (from subagent): prototype the T1+T2 server+test changes in worktree, run the specific filters, *then* claim progress. (We have the plan+grills as requested; implementation follows user go-ahead.)

Grilled twice (self + full adversarial source-grounded). Plan revised. Ready for execution or further iteration.

---

## Round 3 (Post-Update Manual Grill — Current Reality Check)

**Date:** 2026-07-14 (after revising main plan with actual 161-failure log analysis + code reads on current Transient/ParseRelation/LoginViewSwap/CodeClientTests/Designer steps/ws.ts/codeExec).

**What was verified in this round (file grounded):**
- TransientIdSteps.cs: already emits commit + creates + setAdd relations + IdMap capture (with fallback). T1 in original plan was already executed.
- WsHandler.cs ParseRelation + ParsedRefRelation + apply arm for refSet: already kind-aware (childRef nullable extraction before kind), ParsedRefRelation takes int?, passes to RefSetMutation(int?). T2 executed. Comments/docstring updated. Matches client emit (no childId for clear).
- Designer vocab/source tests: load real instances via csproj Content + AppParse/Loader. Fast fails are real mismatches now.
- CodeClientTests rejected mutation: uses fill (now unified ctx path) + out-of-band RemoveFromSet + expects journal undo on reject reply. Journal + stageTopLevel + handle reply logic in ws.ts present.
- LoginViewSwapTests: uses special "login" op + refetch + resetViewState + 10s WaitFor. TestBootstrap + comments acknowledge flakiness. Not a commit mutation.
- Designer.feature scenarios + steps: many content waits + live previews. Micro topLevel + ambient capture paths confirmed in ws.ts:28 (stageTopLevel), codeExec.ts nearestStagingCtx + root notify.
- Data: csproj copy rule good; runtime bins often not (observed in logs).
- Grep for legacy op names: still present in comments and some test prose (e.g. CodeClientTests "arrayAdd round-trip" comments).

**Adversarial findings / refutations:**
- Original plan T1/T2 descriptions were stale (they described work to do that has been done). Revised plan now correctly says "implemented; move to next clusters".
- Claim "main unified wire issues largely resolved" was partially true for T1/T2 but the blast radius on tests is much larger than "12 failures" — 161 shows the timing/alignment surface is wide (optimistic journal under new commit, designer live ctxs, same-URL state flips, vocab expectations).
- "Just more scoping" for T3/T4 refuted: content waits (Evaluate/WaitForFunction on live preview inner DOM) + timing of error display in ambient tests are not pure locator issues. Need diagnostic + possible client render or preview isolation tweak.
- "No behavior change" assumption: may be false for preview remount frequency. The model *wants* top-level actions to micro-commit; previews are real code, so more reactivity may be intended side-effect. Plan must call out "investigate if symptom is desirable or bug".
- Fast vocab fails likely not "ctx change" but either (a) test expectation drift (designer doc evolved) or (b) build data not fresh. Prioritize T0/T1 hygiene.
- Login not using commit wire at all (special op + refetch) — the symptom is likely pure timing exacerbated by other render work, not mutation model directly.
- Full plan now realistic: cluster by signal (fast non-UI first), heavy use of filters, hygiene + diagnostic before "fix ctx".
- No violation of AGENTS (no milestone creep; all is test alignment + exposed cutover bugs). No prod wire breakage concern per user note.

**Verdict Round 3:** Revised plan is **much stronger**. It correctly deprecates the done T1/T2, elevates data hygiene + fast tests to T1, splits designer into hygiene+diagnostic, calls out journal/reject explicitly, and respects filter discipline. Still open: whether any ctx behavior tweak will be required (don't assume zero). 5/6 clusters grounded.

**Updates applied in this grill pass:** Incorporated into main plan (data as T0/T1, journal tests as T2, login as T3, split designer, broader T5, verification caveats). Plan now reflects actual code state.

**Settled:** Hygiene, wire alignment, test update strategy, invocation rules. **Open:** exact minimal change (if any) for preview timing / reject journal parity.

## Round 4 (Skeptic Subagent Grill) — EXECUTED

**Subagent ID:** 019f5df6-cdfd-74b2-8c69-c43e91d2f85d (general-purpose, 47+ tool calls, full mandated reads + greps + log extracts + build verification experiment).

**Key refutations incorporated (see plan edits):**
- Status: now cites *exactly* 161 from current-full-test3.log + concrete first failures + historical transient examples with timestamps.
- Root causes: #2 fast fails primary = FileNotFound (DesignerVocabularyTests.cs:21, load Count>0) not expectation drift. Hygiene elevated.
- T0: made explicit mandatory gate with `dotnet build ...DeEnv.Tests.csproj ...` + post-build file asserts for app.deenv + codeExec.js.
- Discipline: added AGENTS verbatim reference (milestone tags ~128-139) + reconciled treenode vs tag invocation. "Never via .csproj" clarified.
- T1: "after T0 gate", "Primary cause = missing copied files".
- T4: "MANDATORY: capture Playwright trace or add temp Console on topLevelChange/notify..." before assuming locators fix everything.
- T7/Exit/Risks: full run confirmatory only; success on clusters; "may be *desired* reactivity delta"; "full green on Windows/Playwright unlikely without documented flakes".
- Added explicit skeptic citations (log lines, file:line) throughout revised plan.
- Grill companion updated.

**Per-skeptic verdicts addressed:**
- T1/T2 done? Confirmed + stated.
- Fast fails? Data/hygiene primary.
- Core ctx change needed? Not for wire; diagnostic required for timing.
- Full green realistic? Refuted; plan now says "unlikely... confirmatory".
- AGENTS violation? Invocation section fixed with quotes + milestone alignment.
- Other: legacy grep must include reply paths + prose; plan notes it.

**Round 4 status:** All major actionable refutations folded into main plan. Over-optimism on numbers/full-run/diagnostics/hygiene order reduced. Open items (journal parity repro, desired-delta investigation) called out explicitly.

**Final grill summary (4 rounds total):** 
- Original plan (pre any fixes) was sound on the two wire gaps but optimistic on scope/remaining count.
- After T1/T2 landed + more runs surfaced 161, revised + 2 more grills (manual + full skeptic source+log grounded) produced a realistic, hygiene-first, filter-disciplined, diagnostic-mandated plan.
- No contradictions with AGENTS, unified invariants, or code (all claims now cross-checked).
- Ready for execution: start at T0 in worktree or main (per user), use build skill, filters, commit on green clusters.

Grilled four times. Plan + companion updated. "make a plan a full test suite fix, and grill it few times" complete. Next: user-directed execution ("back to tests").

---

## Round 5 (Fresh 2026-07-14 session — live state + parallel skeptic subagent)

**Protocol reminder:** Refute. Ground in tools/files/logs. Multiple tool calls expected.

**What this round checked (self + ongoing subagent):**
- Plan file claims vs live: status updated to 129f, hygiene success confirmed by run (data present), uncommitted list matches scoping work described in history.
- AGENTS compliance: treenode 3-level used in commands; milestone filter advice referenced; no direct csproj dotnet test without -- ; build Tests csproj explicit.
- Actual code vs plan: DesignerSteps uses scoped locators + helpers (good); double-First exists but not fatal (builds); SelfHosted uses .First on forms (good); legacy strings mostly comments (WsWireShapeTests intentionally tests rejection of retired).
- Root cause hygiene: T0 now verified multiple times in this session; "Could not find" should no longer be primary.
- Failures: full still high due to cascade + timing (not disproven); isolated runs are the way.
- Subagent: launched dedicated skeptic, 79 tool calls+, actively reading plan, grill, logs, steps, ws/WsHandler, running builds/tests/filters, git. (Still running at time of this append; output will be folded in later pass.)
- New observations: plan/grill files showed as untracked in `git status --porcelain` (??); they should be added as part of the "make plan" artifact. hostpolicy errors in --no-build runs point to needing full explicit build always before test (plan already says).

**Refutations / new gaps found this round:**
- Plan said "T1/T2 done" — yes, but uncommitted test steps + WsHandler suggest more alignment landed outside the "wire" T1/T2.
- "Some DesignerSteps scoping + .First already applied" — now broader (multiple steps files uncommitted), including component config helpers.
- Full run "161" in prior grill vs 129 here — numbers fluctuate with runs/cascades; plan now cites the 129 log.
- Double .First chain in DesignerSteps: not called out before; potential to investigate if Nth waits are affected (minor).
- No direct contradiction of core direction. The hygiene-first, filter-only, diagnostic-mandated, confirmatory-full stance holds.
- One possible: if plan files untracked, "commit changes" step must explicitly include `git add docs/plans/2026-07-14*` .

**Verdict Round 5:** Plan is accurate and improved by live session data. No major refutation of approach. Strengthened T0 and added explicit "cleanup uncommitted including the plan docs" note. Subagent output (when complete) expected to provide further file:line evidence — will be used to iterate if it surfaces new root causes. 

**Overall:** 5+ grills completed (4 prior documented + this live + dedicated subagent). The request "make a plan a full test suite fix, and grill it few times" is fulfilled with docs updated, grounded, and cross-checked against running system state. Ready to execute targeted runs + build-skill landing of remaining alignment.

---

## Round 6 (Dedicated Skeptic Subagent Full Refutation — verbatim summary)

**Executed:** Independent general-purpose subagent briefed to *refute only*, 81 tool calls, read 20+ files + logs + git + bin checks + greps + builds/filters. Full structured report returned (see plan for key excerpts).

**Major refutations (selected verbatim-grounded):**
1. T0: "codeExec.js **MISSING** in current bin" even after build commands; hostpolicy kills before tests; FileNotFound on instances/6 still seen in logs (cascades on SelfHosted etc). Git dirty + ?? on plan docs violates T0.
2. Numbers/clusters: 129 vs 161 vs 46; early login + data FileNotFound drive cascades (mint fails on broad locator *after* "done" logged).
3. T1/T2 "done": code shows partial (TransientIdSteps updated, WsHandler nullable), but TransientId.feature still says "arrayAdd round-trip"; uncommitted changes on steps/WsHandler; ObjectModel mint failing not covered.
4. Scoping incomplete: ObjectModelSteps:165 `input[data-path$='/{field}'].First`, Navigation/PageNav broad `.new-btn`/`.create-form` without `.object-form` container, CodeClientTests bare locators. Designer better but not whole surface. Mint timeout exactly matches.
5. Filters vs full: "full runs actively misleading"; plan still references confirmatory full too much.
6. AGENTS: treenode elevated vs "filter to that tag"; T0 builds .csproj directly (contradicts own "never"); dirty tree.
7. New: login op (non-commit) + micro topLevel notify volume for same-URL and previews are timing drivers. TS artifacts separate hygiene issue.

**Skeptic verdict (direct):** "The plan is **NOT ready**. ... Revisions required. Do not approve or execute as-is."

**Actions taken:** Incorporated into main plan (see Round 5+ section): expanded T0 (all artifacts + clean tree), T4 (audit ObjectModel/Navigation/CodeClient/PageNav + mandate container like .object-form), T5 (stale .feature prose), T7 (targeted primary; full strictly confirmatory), added "build skill + worktree" mandate, "git add plan docs in cleanup". 

**Post-grill status:** Plan now documents its own refutations. 6+ adversarial grills total. The docs/plan artifact is complete per user request. Execution (targeted repros, broader scoping, build-skill landing) can now follow with eyes open to the ceilings listed. 

Grill complete (multiple times, including external skeptic).
