# Grill: Designer Tests Relevance + Split Proposal (companion to 2026-07-14-designer-tests-relevance-and-split.md)

**Grill date:** 2026-07-14 (self + evidence-based). Goal: poke holes before any split work, per user's history of "grill it few times".

## Positive findings (what holds up)
- The analysis correctly grounds in live files (app.deenv renderNodeEditor + fn-card/use-row/use-preview/instancemount/evalRefresh/refresh-eval + paletteTarget + wrap/unwrap + orderForAppend; workbench.ts mount + sandbox; WsHandler unified commit; current scoped selectors in steps).
- Relevance verdict is accurate: core authoring surface (types/props, structured tree, components+configs+live previews, canvas, palette, commit/publish/merge) is still exactly what the current designer implements. No mass obsolescence from unified commit.
- Categorization maps cleanly to both scenario titles and step bodies.
- Callout of wait modernization as prerequisite is correct and aligns with user feedback ("no WaitForTimeoutAsync except true negative", "prefer builtins", "Eventually only for non-Playwright", 10s/30s constants).
- Partial class split noted as low-risk option — good.

## Refutations / Weaknesses / Risks Found

1. **Partial classes are the obvious winner — the proposal under-emphasizes them.**
   - Separate [Binding] classes + new DesignerTestContext requires Reqnroll container registration for the new type (or polluting InstanceContext). Evidence: all current steps just do `(InstanceContext ctx)`. No precedent for extra designer-only injectable in the tree.
   - `partial class DesignerSteps` across files keeps fields, private helpers, and ctor exactly as-is. Zero DI change, zero behavior change. Much lower blast radius for a "just split the 3k line file" task.
   - Recommendation in analysis should lead with "use partials first; only introduce context bag if you want true separate ownership later."

2. **Over-granular split (7-8 files) risks fragmentation.**
   - Canvas + Tree naturally cross (clicks, selection, highlights, data-node). A scenario "clicking canvas selects tree row and vice-versa" will have When in one file, Then in another — still works but mental model suffers.
   - Components + LivePreview are very tightly coupled (use-row lives inside fn-card; arg edit directly exercises the preview mount/remount).
   - Better practical grouping (after partials):
     - DesignerLibraryNavigationSteps.cs (Given + list + create + nav + kebab + instance apply)
     - DesignerTypePropSteps.cs
     - DesignerTreeCanvasSteps.cs (merge tree + canvas + select + eval)
     - DesignerComponentsLiveSteps.cs (fns/vars/uses + palette + previews + live mounts + refresh)
     - DesignerCommitPublishMergeSteps.cs
   - 5 files instead of 8 is more maintainable. "One file per major milestone area" is overkill.

3. **State ownership and "just added" tracking is glossed over.**
   - _justAddedTypeName + special empty-value locators + post-steps that rename are only for type editor. If split, the "When I add a type" (which sets the field) and "When I name the just-added" must stay in same partial (or same logical group). Trivial with partials, annoying with separate classes.
   - Similar for _newInstanceName used in "that new instance" steps.

4. **Relevance misses a couple of cross-checks.**
   - Some scenarios test "sys.extent over seed" and "Field over sys.schema" inside component config cards (library component using real editors). This exercises not just designer but the *kernel's schema bridge + extent* in the sandbox ctx. Still relevant, but the test is also an integration test for the runtime hosting the designer previews.
   - "A component's configuration mounts a real live instance, and an unrelated page re-render never clobbers it" — this specifically tests workbench isolation + cache keys (ctxKey from evalContext). Good, but may be the first to flake under micro-commit notify volume.
   - Convert scenarios are still wired (convert-render button + importRender), but the primary path in app.deenv and all new work is structured. They are "relevant for compat" but low-signal for the active model. Could be @legacy or deprioritized.

5. **"Up to date" claim is strong but some implementation debt remains visible in steps.**
   - Dispatch + WaitForFunction + refresh-eval after almost every arg/config edit is current workaround, not ideal design. Tests are "up to date with reality" but the reality (preview not live on every input for workbench previews) may be worth a note as a follow-up fix area in the app.deenv/workbench side.
   - Preview HTML dumps and broad EvaluateAsync for state are still in the file (as analysis notes). They should be deleted in the "modernize waits" pass before split.

6. **Interaction with other .feature files not fully audited.**
   - DesignCommit.feature, DesignMerge.feature, DesignSnapshot.feature, and AgedStoreSteps use some overlapping designer navigation/commit UI. If we move commit steps, we must ensure the other features still compile (step defs are global). Quick grep showed most heavy logic is in DesignerSteps only; but after split, moving a step used by another feature would break it.
   - Action: before final split, run a full "which steps are referenced by which features" pass (or just compile after each move).

7. **No mention of generated code or treenode filters.**
   - The .feature.cs files use the full class name (e.g. TheOperatorIDEDesignsLibraryInstanceDesignSelectorFeature). Splitting steps does not affect this, which is good. But if we later split the .feature itself, it would.
   - Tre enode filters like /*/*/<StepClass>/* will become more useful after split (target just DesignerComponentsLiveSteps).

8. **Minor: numbers slightly off.**
   - Scenario count ~115 from one extraction; group math added to ~105. Some Scenario Outlines or Backgrounds or exact regex variance. Not material, but the doc should say "approximately 110+" .

## Suggestions / Refined Proposal
- Lead recommendation: **Use C# partial classes** for the split. One logical `DesignerSteps` type. Five focused files as above.
- Do the wait modernization + dump cleanup **first** (in current monolithic file or a prep branch). This makes the split diff smaller and improves the test surface per user timing requirements.
- Add a small internal `DesignerLocators` static or instance helper even inside the partials (centralize the long "main.ide-design-edit .design-editor ..." strings + .First() defenses).
- After split, add file-level comments: "This partial owns the X surface. Keep related When/Then together."
- For any new DesignerTestContext later: put it inside InstanceContext as a `public DesignerState Designer { get; } = new();` bag (no new DI surface).
- Prune or tag 2-4 low-value timing/convert scenarios as follow-up.
- Verify: after any move, `dotnet build DeEnv.Tests.csproj` + targeted treenode filter run of the affected scenarios.

## Outcome of grill
The core analysis (relevance=yes, categories=area-based) survives. The split plan is directionally correct but should be adjusted to:
- Prefer partial classes.
- Coarser practical grouping (Tree+Canvas together; Components+Live together).
- Explicit pre-split hygiene pass on waits/dumps.
- Cross-feature usage audit + build verification step.

No fundamental refutation of "analyze first, then categorize for split" or of keeping most tests. The 3k-line problem is real and solvable with low risk.

(Grill complete. Ready for user review or execution via worktree+build skill.)