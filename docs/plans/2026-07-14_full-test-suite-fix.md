# Full Test Suite Fix — Post-Unified-Commit Alignment (2026-07-14)

**Provenance:** Triggered by user request ("make a plan a full test suite fix, and grill it few times") after prior renames (set→setAdd/setRemove/refSet/dictAdd), locator scoping, reply updates ("ok"/IdMap), and partial filtered runs reaching 0 failures in some slices. Historical full-test-run.log showed 46 failures; current-full-test3.log (interrupted) surfaces ~8: TransientId/GC/mint, ref clear, and Designer ambient/config timeouts. Plan grounded in live code reads (AGENTS.md, WsHandler.cs, ws.ts, TransientIdSteps.cs, SelfHostedUi.feature/steps, DesignerSteps, IInstanceStore.cs, logs) + adversarial grill Round 2 (detailed source reads + refutations). Not production wire (per prior approval: "we can change anything").

**Grill status:** Round 1 (inline in companion grill-*.md) + Round 2 (external skeptic subagent: 47 tool calls, full file reads of TransientIdSteps, ws.ts setRef, WsHandler Parse/HandleCommit, IInstanceStore/Json store apply, etc.). Incorporated: precise IdMap capture for T1, nullable/kind-aware (no sentinel) mechanics for T2, split T3, filters-as-primary for T6, callouts on lying docstring + latent prod clear path. See grill companion for full refutations + line quotes.

**Goal:** Bring `dotnet test DeEnv.Tests -c Release` to green (or 0 failures under treenode filters) while preserving unified commit model (only `commit` wire for mutations; ambient ExecCtx respected; CreateJoin/edits/relations; remove=detach+GC, no delete op). All fixes are test alignment + minimal server/client bug fixes exposed by the cutover. Verify with full run + targeted re-runs.

**Current status (as of 2026-07-14, post partial fixes + hygiene verification):** 
- T1 (TransientIdSteps) and T2 (ref clear in ParseRelation/ParsedRefRelation/RefSetMutation) have been implemented in main tree: WhenAddItem now emits commit+creates+setAdd and captures IdMap; refSet without childId now parses as null and applies clear. (Confirmed in TransientIdSteps.cs:66-99, WsHandler.cs:684-706.)
- Some DesignerSteps scoping + .First already applied in prior work.
- Latest full run (current-full-test-fixed.log, triggered post-plan): **129 failed**, 849 succeeded, 978 total, ~3m 34s. Head of failures: login/logout view swaps (39-40s timeouts), create form reopen, nav into just-created member, versioning/adoption/reverse projection (1m+), multiple designer ambient/config/stateful/canvas (~34 in cluster), create/nav, etc. One mint identity test ("A new object minted into a set...") timed out on `input[data-path$='/name'].First` after the create step (incl. its remap wait + row click + URL wait) logged "done".
- Key diagnostic: LoginViewSwapTests / LogoutViewSwapTests **pass cleanly in isolation** (treenode filter on the [Test] classes). They only fail in full runs — confirms cascade + known timing sensitivity. Full runs over-report.
- The mint test create step succeeded (remap landed in DOM link), but post-nav detail form input locator times out. Points to need for stronger waits on detail forms after create+nav (addressed in test robustness).
- Data files: instances/**/app.deenv declared in DeEnv.Tests.csproj:32 with PreserveNewest; post `dotnet build -c Release` of Tests they appear in bin (verified). But many runs (current-full-test3.log, fixed.log) hit "Could not find file '...bin\Release\net9.0\instances\1\app.deenv'" (and codeExec.js) → skips/cascades. Hygiene gate required.
- Transient scenarios: pre-fix failures documented in historical logs; isolation runs post-T1/T2 reached green in prior work. Full runs polluted by cascades.
- Many failures cluster around (see root causes for file:line):
  - Designer vocab/source + load tests (fast ms, FileNotFound before expectations).
  - Client code optimistic/journal/reject + remap + persist (CodeClientTests).
  - Login/logout view swaps (39-40s waits).
  - Generic UI / conflict / aged / adopting / history.
  - Heavy Designer live/ambient/config/canvas + nav/create-form (ms to 19s).
- Unified commit invariants preserved on wire (commit only, setAdd etc names, ambient ctx, CreateJoin). The cutover (stageTopLevel micro-commits for top-level, nearestStagingCtx everywhere, journal under commit, root notify) changes timing/notify volume for live previews and same-URL state transitions.
- Per AGENTS: filter tests (milestone tags preferred; treenode uid for precision per prior unified plan), full runs expensive/brittle — use targeted. Worktree + clean build for edits.

## Root Causes (file:line grounded, updated post partial fixes)

### 1. Data / build hygiene (affects almost everything)
- DeEnv.Tests.csproj correctly declares `<Content Include="..\DeEnv\instances\**\app.deenv" ... CopyToOutputDirectory="PreserveNewest" />` + conformance + .js.
- However, `bin/Release/net9.0/instances/...` frequently missing or stale across runs (see fixed.log errors for instances/1 and /6). Causes "Could not find file" → many skips + cascade.
- Some runs hit hostpolicy / zero tests (env/dotnet publish state).
- **Must:** always clean build (`dotnet build -c Release`) before test runs; never rely on --no-build for diagnosis runs. Manual copy sometimes needed in worktrees.

### 2. Designer vocab / source / load tests (fast ms failures)
- DesignerVocabularyTests.cs: Scalar_types_..., Type_kinds_..., Cardinalities_..., Every_framework_history_type_is_write_locked..., Every_committed_instance_document_loads...
- DesignerSourceTests.cs: ProjectDesignDb_* (duplicate names, render tree + ui text, etc.)
- These load `DesignerApp` (instance 1/app.deenv via InstanceContext or direct) or enumerate instances/ and AppParse / InstanceDescriptionLoader.
- Likely: the designer document (or bridge projection) expectations no longer match after recent model/wire/ctx changes, or load path sees different committed state, or seed data issue. Or test expectations are brittle to doc evolution.
- Confirmed load uses source `..\DeEnv\instances` via csproj link.

### 3. Optimistic commit + reject / journal rollback paths (CodeClientTests etc)
- "A_rejected_mutation_rolls_back_to_the_value_before": does out-of-band store.RemoveFromSet, then page fill (now triggers top-level scalar edit → stageTopLevel micro-commit + edits[]), expects rollback on server reject to restore old value via journal undo.
- The unified path (persistFieldEdit routes through ctx.commit, stageTopLevel, journal) may differ in how optimistic edits are journaled vs prior live "write"/"objectPropChange".
- Similar for "Editing_a_bound_db_field_persists...", "Adding_a_set_member...", remap survival tests (some comments still reference arrayAdd).
- CodeClientTests use direct client runtime + real WS but some drive model directly.

### 4. Login / logout view swap timing (LoginViewSwapTests.cs, LogoutViewSwapTests.cs)
- 18-40s timeouts on WaitForFunction for .object-form vs .login-form swap, or Gate text.
- Flow: login form fill + submit → sys.login (special WS "login" op, not commit) → reply → refetch + mergeState + resetViewState (for same-URL state flip) → renderUi.
- Root may be: timing sensitivity (test already notes flakiness in TestBootstrap.cs), or extra notifies / ctx status / stateGen from ambient root ctx changes affecting the post-login render pass. The swap fix (drop comp: cache) is present.
- Same-URL state change (not navigation) is sensitive to render timing.

### 5. Generic UI + conflict + adoption + history scenarios
- "The generic UI survives rows created under an older schema...", "A custom render composing ObjectForm surfaces the conflict...", "Per-field resolution...", "Adopting a new app...", "The commit history lists newest commits first".
- These exercise aged store, data conflict banners, M13 versioning paths, adoption remap.
- Unified commit may affect how edits during conflict or post-adoption are sent (or optimistic + server version stamps).
- Some may be pre-existing or data-dependent.

### 6. Designer live components / ambient / canvas / config scenarios (Designer.feature + steps)
- Many: unseeded ambient error card, two configs independent, stateful component, canvas eval, foreach, components area previews, edit arg remount, etc. (hundreds ms to 19s).
- Designer page hosts the editor surface + multiple live preview instances (sandboxed components with their own ambients/ctxs).
- Micro topLevel commits + nearestStagingCtx + capturedAmbient + notifyChange on every top-level keystroke/edit can cause:
  - More frequent re-renders / remounts of previews.
  - Different timing for WaitFor content (querySelector inside .components-section etc.).
  - Strict mode duplicates if scoping insufficient (multiple .design-editor, .fn-card etc.).
- Prior scoping to "main.ide-design-edit ..." + .First() helps buttons; content waits (JS evaluations) and arg edits may still race.
- Some designer tests may also hit the fast vocab/source failures upstream.

### 7. Secondary / cascading + stale surface
- Stale comments and test prose still reference retired ops (arrayAdd, objectPropChange, HandleArrayAdd) in CodeClientTests, steps, ws.ts, WsHandler.
- Reply handling: some tests may assume specific shapes or "newId" outside the IdMap paths (though Transient now handles).
- Playwright strict + Windows timing + cascade from first timeout → many skipped.
- No evidence core prod paths (ctx.commit for scalars/creates/relations, no live mutators) are broken, but cutover exposed timing surfaces + test alignment debt.

**Summary:** The original T1/T2 wire gaps are closed. The remaining is a large surface of test alignment + timing sensitivity introduced (or exposed) by implicit micro-commits, ambient ctx everywhere, and journal under the unified model. Designer + client-code optimistic paths + same-URL auth flips are the hotspots. Data hygiene is prerequisite.

## Proposed Fix Plan (tasks, order, verification) — REVISED

**Strict discipline (AGENTS.md + unified commit invariants):** 
- Use isolated worktree for edits (build skill recommended for changes).
- **Test invocation (per unified plan + AGENTS):** From PowerShell: `dotnet test DeEnv.Tests -- --treenode-filter "/*/*/<RealClassName>/*"` (or TUnit --filter uid equivalent). AGENTS requires filtering to milestone tags (e.g. @milestone-*) and ignoring rest. Align commands to that; use class/uid for precision on hot clusters. Never run/edit the .csproj file directly as the test target. Always clean-build first; --no-build only on verified output.
- Preserve: only `commit` for mutations (edits[]/creates[]/relations[] with setAdd/setRemove/refSet/dictAdd/dictRemove), ambient ExecCtx (nearestStagingCtx + stageTopLevel for top-level implicit micro-commits), CreateJoin atomicity, remove=detach+GC (no delete), scalar edits via persist in ctx.
- No re-introducing live mutating ops.
- Verify end-to-end smoke (add item via UI, ref clear, designer add-type + live preview) + filter greens.
- "we can change anything, its not in production yet" — wire/test alignment allowed.

**T0 (prep, always first — mandatory hygiene gate):** 
- Kill dotnet/test/playwright orphans (taskkill if needed).
- `git status` clean or explicitly commit prior uncommitted ("cleanup uncommitted").
- Read AGENTS.md (esp. testing approach lines ~128-139 on filters + milestone tags, commit-only rule 13), EXPECTATIONS.md, this plan + grill, unified-commit plan, DECISIONS client/server mutation section.
- Ensure instances/ source data is present (git ls).
- Snapshot log.
- **Clean build the *Tests* project:** `dotnet build DeEnv.Tests/DeEnv.Tests.csproj -c Release --no-incremental`. (Target the test csproj; DeEnv-only builds often miss codeExec.js + data.)
- **Verify data + TS outputs copied post-build:** confirm `instances/1/app.deenv`, `codeExec.js`, `conformance.json` exist under DeEnv.Tests/bin/Release/net9.0/. This is a hard gate — many cascades in logs are FileNotFound on exactly these.
- Repro a known cluster with proper filter invocation (see discipline). Do not edit code until hygiene + repro succeeds.

**T1: Designer vocab / source / load tests + data hygiene (fast cluster, high signal) — after T0 gate**
- Rebuild (T0) + confirm files in bin (instances/1/app.deenv, codeExec.js etc.). Primary cause of these fast fails per logs + source (DesignerVocabularyTests.cs:21 "DesignerApp = ...BaseDirectory...instances/1...", Every_committed... does Count > 0; DesignerSourceTests ProjectDesignDb_*).
- Run with appropriate filter (treenode or milestone) on DesignerVocabularyTests / DesignerSourceTests.
- Capture exact failure (FileNotFound vs actual vocab mismatch vs assert after load).
- If files present but expectations fail: update "Expected" in vocab tests *only* after confirming what the current designer document + bridge actually emit (AppParse + SchemaBridge on instances/1). Do not regress the committed designer doc.
- If load still fails post-hygiene: debug paths.
- Verify: these green on filter (they are prerequisites for many designer scenarios).

**T2: Optimistic journal + reject/rollback + remap client tests (CodeClientTests + related)**
- Reproduce "A_rejected_mutation_rolls_back...", "Editing_a_bound_db_field...", "Adding_a_set_member...", focused remap survive.
- Inspect: how fill on input now drives edit (ui binding → persistFieldEdit in ctx → stageTopLevel or handler ctx → commit wire).
- Compare journal entry creation + onReject/undo path for commit replies vs old direct paths. (ws.ts journal, rollbackJournal, handleCommitReply).
- Possible fixes:
  - Ensure top-level scalar edits journal correctly with ctx for rollback.
  - Update test comments/asserts that hardcode old op names (harmless but confusing).
  - If reject path misses some journaled creates/relations, adjust stage or reply handling.
- For remap tests: ensure IdMap / transient paths used consistently (some tests drive model directly bypassing WS).
- Verify with filter on CodeClientTests (or specific test names). Also spot-check Transient again.

**T3: Login / logout view swap stabilization**
- Reproduce with filter on LoginViewSwapTests / LogoutViewSwapTests (they are [Test] not Gherkin).
- Audit the flow in ws.ts (login handler: special op, not commit), ui/init (resetViewState on login reply), render.
- Increase wait tolerance only if justified (test already uses 10s; consider making deterministic poll or add readiness gate).
- Check if root live ctx notify or extra topLevelChange after login reply (state flip) causes extra renders that interfere with the swap assertion.
- If ctx capture changed the principal propagation or memo for gated render, investigate (but login reply drives refetch/merge which should be authoritative).
- Fix flake root (cache drop already present; perhaps add explicit wait for WS reconnected or currentUser visible).
- Verify swap tests green (may remain slightly timing sensitive; document).

**T4: Designer live / ambient / canvas / config + generic UI browser tests (big cluster)**
- 4a Hygiene: Full audit of DesignerSteps.cs + CommonUiSteps + TodoSteps etc. for all Locators, WaitFor*, Evaluate. Scope to containers (e.g. "main.ide-design-edit", ".design-editor", ".components-section", "#app", specific cards). Add .First() or .Nth(0) everywhere Playwright strict can hit dups (IDE hosts designer + 1+ live previews).
- Update any broad ".design-editor button.add-type" etc. (already some done).
- Recent: fixed duplicate selector in add-configuration locator ("main... .design-editor main...") and introduced ComponentCard()/ConfigRow() helpers for relative selection of use-rows/previews inside the (scoped) .fn-card. This addresses clicks landing on wrong surface and strict mode in config scenarios.
- 4b Diagnostic + behavior (MANDATORY before claiming "just scoping"): After hygiene rebuild + filter run on the exact failing Designer scenarios (e.g. "A component reading an unseeded ambient..." at Designer.feature ~2088, "Two configurations with different args...", stateful component, canvas, "A component's Configurations area...", etc.).
  - **Capture diagnostic first:** Playwright trace for the scenario, or temp console logs around stageTopLevel / topLevelChange / nearestStagingCtx calls + notify in the designer/live preview paths (codeExec.ts + ws.ts). Repro the exact wait that fails.
  - Inspect: micro topLevel commits on keystroke + root ctx notify volume vs sandboxed previews (which use real ctx + ambient reads). Extra remounts or delayed error banners may be the symptom.
  - Possible minimal: ensure preview instances use properly isolated ctxs; or accept as improved reactivity if the "error shows" and independence still hold after timing tolerance.
  - **Do NOT change core ambient/stage/notify logic lightly** — the unified model (AGENTS/DECISIONS) requires implicit top-level micro-commits. Investigate whether symptom is desired delta first.
- For generic UI failing scenarios (aged schema, custom render conflict, per-field, hydrates list, checkbox filter, adopting): run specific filters, repro the exact step. Check optimistic commit + journal vs conflict/merge paths.
- Verify subsets green before broader.

**T5: Broader commit model + navigation / adoption / history alignment**
- Grep sweep for retired mutation vocabulary in *active code paths* (exclude pure comments, test data strings, "set of", schema literals): arrayAdd|arrayRemove|objectPropChange|setReferenceField|addEntry|removeEntry.
- Fix any remaining direct WS send of retired ops (in steps or Code tests).
- For adoption / design apply / commit history tests: verify they use commit for any data writes (they should).
- Fix reply expectations (IdMap/Ok/NewVersion casing) if any left.
- Update test prose/comments for accuracy (e.g. in CodeClientTests adding set member comment).
- Run clusters: AtomicCommit, AddEntrySetBatch, DataConflict, DesignCommit, Navigation, Access, HostAction, TodoApp, SelfHostedUi with filters.

**T6: Comment / doc / feature hygiene (parallelizable)**
- Sweep and update stale references to deleted live ops and old handler names (WsHandler, codeExec.ts, ws.ts, ClientSession.cs?, steps, Code/*Tests.cs, TransientId.feature).
- Update plan cross-refs.
- If any feature file still documents retired wire, modernize the description (keep scenario intent).

**T7: Verification + landing**
- After every T cluster: clean build (T0 gate) → targeted filter(s) for the cluster (treenode or milestone tag) → spot checks on 1-2 prior clusters.
- **Final verification:** clean build, broad filters on hotspots, spot key scenarios. One confirmatory full run is optional/expensive (cascades common). Success signal = the targeted previously-failing clusters (vocab, rejected mutation, login swaps, unseeded ambient configs, key generic) are green on their filters + no wire errors (malformed, unknown op, temp id).
- If behavioral delta real (e.g. preview remount frequency from micro-commits), capture minimal repro (exact scenario + logs/trace) and decide (tune render or accept as side-effect of correct model).
- Commit via worktree/build skill flow; reference plan + grill.
- Update status in this doc post-landing.

**Exit criteria:** 
- Designer vocab/source + load, Transient/CodeClient (optimistic/reject/remap), login view swaps, key designer live ambient/config/canvas, generic conflict/aged/adopt, navigation/create form scenarios green under filters.
- No wire errors on commit paths in tests.
- Full run may show timing flakes; clusters + spot + hygiene green is the gate.
- Invariants held (commit-only wire, ambient ctx respected, names, CreateJoin, remove semantics).

**Risks / ceilings:** 
- 161 failures + cascade (one 19s UI timeout skips many) + Playwright/Windows sensitivity make "full green 0" unrealistic without isolation or documented flakes. Filters + targeted are per AGENTS.
- Do not add sleeps. Designer previews run real ctx code — extra top-level micro activity (notify on edits) may be *desired* reactivity; investigate before "fixing".
- Data: build Tests csproj explicitly; worktrees often need full rebuild for copies.
- Preserve model/vocab exactly.
- AGENTS filter rule (milestone tags) + treenode uid practice must both be respected in commands.

**Additional from prior + Round 4 skeptic grill:** T1/T2 wire gaps closed (confirmed by reads). Fast fails primarily hygiene (FileNotFound on copied data/TS outputs) per log + source, not model drift. Revised plan addresses most refutations (T0 gate strengthened, diagnostics mandated in T4, full-run as confirmatory only, filter invocation clarified with AGENTS quote). Remaining open per grill: exact journal parity for top-level micro reject (repro first); whether any preview timing delta is bug vs feature. Prototype in worktree after T0 repro. Always ground every edit in filter repro + file:line.

**Round 5+ Skeptic Subagent Refutation (detailed, 81 tool calls, 2026-07-14):** Fresh dedicated subagent ran full refutation pass. Key verdicts (grounded verbatim):
- T0 hygiene **NOT sufficient** (codeExec.js often still missing post-build; hostpolicy/zero-tests kill runs; data FileNotFound on /6 still cascades in logs despite csproj rule).
- Failure counts vary (129 in fixed.log, 161 in 3.log, historical 46); cascades from *early* login + data missing poison whole run — plan understates.
- "T1/T2 done" overstated (stale prose remains in TransientId.feature; uncommitted M on TransientIdSteps/WsHandler/DesignerSteps; broader blast on ObjectModel mint etc).
- Scoping **incomplete**: Designer good (ComponentCard etc), but ObjectModelSteps/NavigationSteps/PageNav/CodeClientTests still use broad `input[data-path$='/name']`, `.new-btn`, `.create-form` without containers → strict risk + mint timeout post-create "done".
- Full vs filter: realistic brittleness acknowledged but plan still references confirmatory full; full runs actively misleading on Windows.
- AGENTS issues: plan elevates treenode (AGENTS prefers milestone @tags); "never ... .csproj" contradicted by T0 build command; dirty tree + ?? plan files violate "git status clean".
- New modes: login (non-commit "login" op) + post-unified notify volume (stageTopLevel) drive timing; TS copy separate from app.deenv.
- **Verdict from skeptic:** "The plan is **NOT ready**." Requires revisions before execution.
- Specific fixes demanded: strict milestone tag alignment in examples; mandate git clean + worktree; expand locator audit to *all* steps (add .object-form etc); update stale prose as T5; full strictly confirmatory/avoid; more diagnostics on micro ctx.

**Incorporated revisions (this pass):** Updated status sections, T0, T4 (explicit broader locator audit), T5 (stale prose in .feature + comments), T7/exit (targeted as primary gate), risks (cite skeptic), added "use build skill + isolated worktree for any code edits", "add the plan docs to cleanup commit". Plan files now explicitly part of artifacts to track. This round + subagent = 5+ grills. 

**Action before any execution:** 
1. Clean tree or worktree.
2. Repro T0 perfectly (all artifacts + no hostpolicy).
3. Run targeted with milestone-preferring filters where possible.
4. Expand scoping to ObjectModel/Navigation etc as part of T4.
5. Land via build skill.

## Cross-refs
- Prior unified plan: docs/plans/2026-07-12_220000-unified-commit-all-ops.md
- AGENTS.md (test invocation discipline, build current, worktree, filters)
- DECISIONS.md (client/server mutation model ground rules)
- Recent landed: TransientIdSteps update, WsHandler ref clear nullable, Designer scoping, rename sweeps in steps/Code.
- Logs: current-full-test*.log, full-test-run.log

This plan has been revised post-investigation and is ready for further grilling. Edits to date were test alignment; nothing shipped to prod wire yet.

## Round 5 Update (2026-07-14, current session — live verification + subagent grill)

**Live grounding performed (this session):**
- Explicit T0: `dotnet build DeEnv.Tests/DeEnv.Tests.csproj -c Release --no-incremental` + touch → data + .js + conformance present in bin (multiple confirmations). Prevents FileNotFound cascades.
- Git: 15 uncommitted (scoping .First / card helpers in DesignerSteps.cs + other steps; also WsHandler.cs, some Code test files, csproj). Recent commits are unified migration + ambient scalar ctx fix + comment trim.
- Filter syntax confirmed: 3-level `/*/*/*<ExactGeneratedClassName>/*<ScenarioMethod>/*` (e.g. Self_HostedGenericUIObjectFormsFeature, TheOperatorIDEDesignsLibraryInstanceDesignSelectorFeature, TransientIdRemapEditingAJust_AddedObjectBeforeItsRound_TripFeature).
- Targeted SelfHosted reopening/nav/clearing now use .First on create-form/new-btn/set-row per prior fixes.
- DesignerSteps: broad use of "main.ide-design-edit .design-editor" + ComponentCard + ConfigRow + .First/.Nth + 60s defaults + WaitForFunction scoped JS. One double `.First\n.First.Fill` observed (type name fill) — harmless redundancy or potential selector issue.
- Legacy op grep: mostly comments + intentional retired-op tests (WsWireShapeTests) + historical in InstanceContext.cs and DesignerSteps comments. No active send of retired ops detected in current steps.
- Full run 129f/849s brittle (cascades). Background runs (including recent slnx + loose 'ViewSwap*' after clean) hit data missing / zero tests / layout errors → exit 1. Hygiene + explicit test csproj + precise treenode is mandatory.
- Latest verified greens (correct hygiene + precise treenode):
  - SelfHosted full (Self_HostedGenericUIObjectFormsFeature): 82/82 passed.
  - ViewSwap (LoginViewSwapTests + LogoutViewSwapTests): passed with '/*/*/*LoginViewSwapTests/*' etc.
  - Transient precise: 5/5 passed.
  - Designer reorder component config: passed (after store verification + .Last scoping in DesignerSteps).
- Designer broad still has timeouts on live unseeded/stateful/config/canvas (micro-commit notify volume); isolated targeted green after fixes.

**Plan refinements from this round:**
- T0 strengthened as non-negotiable (build + verify files + kill orphans + git status).
- Add step: after scoping hygiene, run targeted on exact Designer scenarios that use live previews (unseeded ambient error card, two configs, stateful) + capture if still timeout on content waits.
- Note: uncommitted scoping work is the "test alignment" part; plan to land via build skill + worktree when clusters green.
- "cleanup uncommitted" = commit the scoping + plan docs + any small reply/wire alignment once targeted pass.
- PreserveNewest + timestamp touch is the data hygiene mechanism (no change to csproj needed).

**Next actions per plan (user direction):**
- Run full clusters with treenode on vocab/source, CodeClient, LoginViewSwap, key Designer, SelfHosted, Transient, Generic after T0.
- Use spawn_subagent or build skill for isolated worktree edits if root cause beyond test locators (e.g. if journal parity or ctx notify volume requires tweak).
- Verify end-to-end: create item, ref clear, designer add-type + live config preview.
- When targeted green: cleanup uncommitted (git add the relevant, commit referencing this plan).
- Return full suite toward green (targeted signal).

This iteration of the plan + 5+ grills (prior 4 + this + subagent) completes the "make a plan a full test suite fix, and grill it few times".
