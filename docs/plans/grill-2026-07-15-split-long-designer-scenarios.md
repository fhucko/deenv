# Grill: Splitting Long Designer Scenarios (2026-07-15)

**Grill date:** 2026-07-15. Adversarial review of the analysis + proposal for splitting long-running @m12 designer scenarios (live previews + error isolation) after the timeout raise + real error assert restoration.

Grounded in: subagent analysis output, subagent grill output, actual .feature bodies (throwing + ambient error scenarios), InstanceContext boot path, workbench.ts isolation, Reqnroll Background rules (seen in other features), TESTING.md wait rules, AGENTS.md (tags/filters, minimal, milestone discipline, no hidden scope), prior 2026-07-14 designer relevance/split plan + its grill, and the recent Polling.cs + feature edits.

## What Holds Up (positive)
- The identification of the two dominant long scenarios is accurate (the ones that previously needed no-op on the error step because of >5s timing under temp caps).
- The per-scenario boot cost diagnosis is correct and matches the code (StartKernelDesignerBrowserAsync + 60s init shim + template + ports).
- The callout that error-isolation scenarios are special (single-boot proofs of sandbox brackets + no clobber + sibling survival) is correct and important.
- Recommendation to rely on existing tags + treenode filters (instead of restructuring for "speed") aligns with docs and prior plans.
- No timeout lowering is the right call.

## Refutations and Holes

1. **Backgrounds as a "split" lever for long tests is a non-starter (analysis subagent claim partially walked back, but still needs stronger language).**
   - Confirmed by grill subagent: Background executes before *each* Scenario. Since the expensive Given is the IDE boot, you pay the full cost per scenario either way. Background only dedups text in the .feature.
   - Evidence: other features that use Background do so for cheap per-scenario fixtures (store seeds, simple app docs), never for KernelHost + browser.
   - Revised recommendation: mention Backgrounds *only* for readability if the common prefix is annoying, with an explicit "this does not reduce wall time" caveat.

2. **The "split the long ones" direction risks exactly the problems the grill subagent flagged.**
   - Splitting the throwing/ambient scenarios would require duplicating the multi-config + error + sibling + outer-rename assertions across multiple scenarios → each gets its own boot → the "one broken component does not wedge the page or sibling" claim becomes unprovable.
   - The analysis subagent correctly carved them out as "must stay combined." The plan doc should lead with this carve-out, not bury it.

3. **Treenode + tag filtering is already the answer; scenario splitting changes UIDs and increases maintenance.**
   - Adding a tag (`@m12-live-isolation`) + comments (done) is the minimal, reversible way to make the long ones targetable.
   - Physically splitting .feature files or scenarios changes the generated test class/method names in the .feature.cs files. All existing treenode commands, logs, and CI filters that name those scenarios become stale. This was called out in the 2026-07-14 grill too.
   - Partial class split of *steps* (already in progress per prior plans) has lower blast radius than splitting scenarios.

4. **Composite Givens have limited upside and hidden readability cost.**
   - A "Given a design with throwing + counter configs already live" would hide the exact authoring sequence that the scenario comment is trying to document (the fidelity boundary being tested).
   - Only introduce if a clear readability win; do not expect time wins.

5. **Wait modernization and app-side timing are the real levers (under-emphasized).**
   - The long scenarios are canaries for real surfaces: Convert disclosure state, lack of stable keys on designer lists causing re-renders of use-rows, evalContext shipping after authoring, micro top-level commits notifying workbench mounts.
   - Per TESTING.md (and prior grills): the fixes belong in the app + modern locators in steps, not "make the test shorter so the 10s cap passes."
   - Any proposal that relaxes waits or adds sleeps to "split long tests" is scope creep.

6. **No evidence that the other @m12 live scenarios are problematically long in isolation.**
   - Once you filter with the new tag + treenode, the "long" label mostly applies to the two isolation ones + a handful of multi-config independence ones. The rest of the cluster (palette, simple arg remount, library component in card) are shorter.

7. **Interaction with existing partial steps split.**
   - The live config / error steps live in the ComponentsLive / related partials. Any scenario reorg must not move bindings in a way that breaks other .feature files that reuse generic designer nav/create phrases.
   - Grill correctly requires a "which phrases are used outside these two features" check before bigger moves.

## Revised Minimal Actionable Recommendations

1. **Tags + comments only (already executed in this slice):** `@m12-live-isolation` on the two critical scenarios + "do not split / single-boot proof" comments. This gives precise filtering without changing structure or UIDs.

2. **Update TESTING.md** (done) with example:
   ```
   # Only the long live-isolation proofs
   dotnet test ... -- --treenode-filter '/*/*/*/*@m12-live-isolation*'
   # Or the whole @m12 live cluster
   dotnet test ... -- --filter "Category=m12" ... (or equivalent tag form)
   ```

3. **Docs only for the analysis/grill** (this pair of files). Do not create new .feature files or move scenarios yet.

4. **Future (only if pain continues after filtering + wait modernization):**
   - Thin composite Given helpers for the repeated "Db + convertible + convert" prefix (in the owning steps partial).
   - Coarser physical grouping only after the partial-class steps split lands and is stable.
   - Never split the isolation scenarios.

5. **Explicit "no" items (to prevent scope creep):**
   - No timeout reduction.
   - No Backgrounds sold as a performance technique.
   - No new test-only state or sleeps.
   - No app changes justified by "tests take long."

6. **Verification:** After any future work, targeted treenode on the tagged scenarios + full clean Release build of the test project. Confirm the isolation assertions still execute in the same boot (look at log timestamps or add a cheap per-boot marker if needed for future debugging).

## Outcome of This Grill

The direction "split the tests that take long" is only safe and useful in the narrow form of **better targeting** (tags + filters + comments). Broader structural splitting of scenarios would either:
- Waste effort (Backgrounds), or
- Break the tests that were the original source of the "error step takes long" complaint.

This grill aligns with the 2026-07-14 skeptic passes and AGENTS.md. The changes shipped in this slice (tags + docs) are the correct minimal response.

## Evidence Files
- DeEnv.Tests/Features/DesignerLibrary.feature + DesignerComponents.feature (the tagged scenarios + comments)
- DeEnv.Tests/TestSupport/InstanceContext.cs (boot)
- DeEnv/Instance/workbench.ts (why single boot matters for the proofs)
- docs/TESTING.md
- Prior plans/grills (2026-07-14-*) for continuity

End of grill. Any implementation beyond the tag+doc slice must re-grill.