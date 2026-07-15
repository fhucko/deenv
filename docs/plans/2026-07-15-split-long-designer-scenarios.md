# Analysis + Recommendations: Splitting Long-Running Designer Scenarios (2026-07-15)

**Trigger:** User question after raising globals to ActionMs=10s / TestMs=30s and restoring real error asserts: "should we split the tests that take long, and lower the timeouts?"

**Approach:** Tool-assisted read of the two main @m12 clusters (`DesignerLibrary.feature`, `DesignerComponents.feature`), step definitions, `Polling.cs`/`InstanceContext.cs` boot path, `TESTING.md`, and prior 2026-07-14 designer split/relevance plans + grills. Ran dedicated plan and adversarial grill subagents. Grounded in actual scenario bodies, boot cost, and AGENTS.md testing rules.

## Key Facts

- Dominant cost for "long" tests is **per-scenario kernel + browser boot** (`Given the operator IDE is running...` → `StartKernelDesignerBrowserAsync` in InstanceContext). This includes temp dir, template xcopy or Write, KernelHost start on two ports, page creation, login. Boot uses a 60s temporary override then drops to ActionMs.
- Current globals (after recent raise): ActionMs=10s (Playwright action waits), TestMs=30s. `EventuallyAsync` defaults to 30s (explicitly for peak load under parallel browsers + WS/GC).
- The @m12 tail scenarios (live configs, previews via workbench + instancemount, error banners) repeat heavy prefix:
  - IDE boot + open list + create design + edit + (Db type + note field) + ensure Advanced + author convertible + Convert + "shows the structured render tree editor" (60s wait in some paths).
  - Then: add-configuration(s), live "shows a ... element", clicks inside `.workbench-instance-content`, error asserts, sibling checks, outer rename.
- Error-isolation scenarios (the ones that were no-op'ed earlier):
  - DesignerLibrary: "A throwing instance handler shows the real error without disabling the page or its sibling instance" (@m12-live-isolation tag added).
  - DesignerComponents: "A component reading an unseeded ambient shows the real error in its configuration card, and the page stays alive" (same tag).
  - These deliberately do *multiple* live previews + error + post-error interaction + outer designer mutation **in one boot** to prove the sandbox brackets (workbench.ts withSandboxGlobals, runInstanceHandler, noWiring for errors, ctxKey/argsSignature for independent mounts).
- No Backgrounds in these Designer*.feature files today (other features use them for cheap fixtures, not heavy hosts).
- Tagging: already `@m12 @single-user`. Treenode filters (`-- --treenode-filter '/*/*/<FeatureClass>/*'`) + tags are the documented way to run subsets (TESTING.md, plans).
- Wait modernization debt still present in places (some WaitForFunction for live content vs static data-node, preview dumps, dispatch+refresh patterns). Per TESTING.md: prefer native Locator + Attached + Has + InputValue + auto-waiting actions.

## Should We Split the Long Tests?

**Limited value for wall time. High risk for correctness on isolation proofs.**

- Backgrounds run *per scenario* → no boot savings. They only dedup prose.
- The isolation scenarios must stay as single combined scenarios (single boot + multiple live cards in one designer page). Splitting them into "setup then assert error" would give each its own kernel+page → no "sibling still works after error in other card" proof. This would silently invalidate the test (the whole point of the W1a/W1b/W1c workbench driver fidelity stories).
- Other live-config scenarios (two-way, stateful independent, extent/schema in cards, Reset after handler mutate) are also coupled to the live mount machinery; aggressive splitting loses the "unrelated re-render never clobbers" and "independent" invariants that are the value.
- Composite higher-level "Given a design with X + two configs already added and converted" *could* reduce some duplication, but only if the assertions in the scenario body still make the proof obvious. Over-abstraction hides intent.

**What actually helps "tests that take long":**
- Precise targeting via tags + treenode filters (already supported, under-used for the heavy cluster).
- Modernize waits in the live preview paths (remove remaining WaitForFunction for presence/text when Locator + Has suffices; remove debug HTML/console dumps).
- Continue the partial-class split of DesignerSteps (coarse groups: live/components together) so the owning code for long scenarios is smaller and reviewable.
- Accept that the full "create from scratch + convert + live multi-config + isolation" flows are integration canaries for the deferred M12 surface + unified commit timing surfaces. Run them targeted, not in every fast smoke.

**Do not lower the globals further.** 10s/30s is the post-5s-stress balance. The 30s Eventually ceiling comment already acknowledges real WS/GC costs under load. Lowering would increase flakes on legitimate paths and hide app issues (re-renders after Convert, missing stable keys on designer lists, evalContext freshness after authoring).

## Recommended Changes (Minimal, Grilled)

1. Add `@m12-live-isolation` (and similar) sub-tags to the critical combined scenarios + a comment explaining why they must not be split. (Done in this slice.)
2. Update `docs/TESTING.md` with concrete examples of filtering the long @m12 live cluster (treenode + tag).
3. Produce this plan + companion grill (this doc + grill-2026-07-15-...).
4. If desired later: introduce 1-2 thin composite Given helpers for the common "Db type + convertible + convert" prefix (in the steps partial that owns live config). Do **not** rely on them for boot savings.
5. No changes to Polling.cs timeouts.
6. No structural split of the isolation scenarios.
7. Verify with targeted build + treenode runs on the tagged scenarios.

This satisfies AGENTS.md: milestone tags, treenode filters for targeted runs, "minimal by default", no hidden scope pulling M12 render-coupled details into test reorg, fix timing in app where it shows up.

## Files Touched in This Work

- DeEnv.Tests/Features/DesignerLibrary.feature
- DeEnv.Tests/Features/DesignerComponents.feature
- docs/TESTING.md (filter examples)
- docs/plans/2026-07-15-split-long-designer-scenarios.md (this)
- docs/plans/grill-2026-07-15-split-long-designer-scenarios.md

## Verification Steps

- Clean build: `dotnet build -c Release DeEnv.Tests/DeEnv.Tests.csproj`
- Targeted: `dotnet test DeEnv.Tests --no-build -- --treenode-filter '/*/*/*/*@m12-live-isolation*' --log-level Information`
- Confirm the two isolation scenarios still run as one boot each and the sibling/page-alive assertions pass.
- Spot-check one non-isolation @m12 live scenario still works.

## Open Decisions (deferred)

- Whether to create a narrow `DesignerLivePreviews.feature` to physically group the live config scenarios (would change generated class names/treenode UIDs; low priority).
- Any app-side improvements to Convert + first live mount latency (outside test-only work).

This is the direct response to the "split the tests that take long" question after the timeout raise and error-assert restoration work.