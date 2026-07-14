# Designer Tests Analysis: Relevance vs Current Codebase + Categorization for Split (2026-07-14)

**Context:** Designer.feature (2751 lines, ~115 scenarios) + DesignerSteps.cs (3353 lines, ~334 step bindings) is the largest single test surface. User request: first analyze whether the tests are still relevant and up-to-date against the current codebase (post unified commit migration, setAdd/setRemove/refSet/dictAdd, ambient ExecCtx + micro top-level commits, scalars in ctx, no setByProp/live, instancemount/workbench live previews, structured renderTree + evalContext, current app.deenv), then categorize for splitting the monolithic steps file.

**Approach taken:** 
- Read current app.deenv (the real designer implementation hosted at instances/1), workbench.ts, related kernel (KernelHostActions.cs, SchemaBridge.cs), storage commit paths (WsHandler.cs, IInstanceStore/JsonFileInstanceStore.cs).
- Extracted all scenario titles and grouped them.
- Extracted step binding counts and method groupings from DesignerSteps.cs.
- Cross-checked selectors, interactions, data model, live preview paths, commitDesign usage, tree editing, palette, components, canvas, and wait strategies.
- Compared against unified commit wire (creates/relations/edits, CommitResponse with IdMap, no legacy arrayAdd/setByProp).
- Reviewed prior plan docs for context on timing/ambient impact on designer live surfaces.
- Verified DOM class names, fn signatures (addType/addProp/addUse/addFn/orderForAppend/moveRow/etc.), and live mechanisms (evalRefresh + renderTree for static previews, instancemount + workbench for live config previews).

## Relevance and Up-to-Dateness Assessment

### Overall Verdict: **Mostly relevant and aligned with current codebase.**
The tests exercise the primary operator authoring flow that is still fully present and implemented:
- Design library (SetTable + kebabs + generic New create) + instance selector/Apply.
- Type/prop/enum authoring (MetaType/MetaProp mutations via .add/.remove on the design working copy).
- Structured render tree editor (MetaNode tree: elements, text/expr, for, if/else + attrs + children/elseChildren + order).
- Canvas (live sys.renderTree + evalContext(design, evalRefresh) against seed).
- Components (MetaFn + MetaVar state + MetaUse "configurations" + palette insert of own fns + library).
- Live config previews (workbench sandboxed instances via instancemount + private ctx for stateful/arg-bound components).
- Commit (sys.commitDesign + history), publish preview/apply, branches + merge.
- Selection sync (click canvas <-> tree row via data-node/selecttarget), unwrap/wrap, reorders, expression eval + "chip until Refresh", foreach, error cases (invalid design, unseeded ambient in card).

These map 1:1 to current code in:
- app.deenv (types/props editor, renderNodeEditor, componentPalette, fn-card/use-row/use-preview, designCanvas, commit bar, refresh-eval, evalRefresh var, orderForAppend, isFirst/LastSibling, moveRow, wrap/unwrapNode, insertComponent, etc.).
- workbench.ts (mountWorkbenchInstances, renderWorkbenchInstance, sandbox ctx seeding, instancemount detection, useArgsSignature, remount on arg change).
- SchemaBridge.cs (projections, render tree import, workbench feed of use.args).
- KernelHostActions.cs (commitDesign, importRender/convert, publishPreview, branches).
- Wire: all designer data edits (field binds + set adds/removes on Meta* collections) and target instance previews ultimately surface through the unified commit path (HandleCommit, creates + CommitMutation relations for refs/sets/dicts, IdMap remap, ambient ctx for client state). No direct setByProp or live mutator ops remain in the exercised paths.

### Specific Alignment Checks (positive)
- **DOM / Locators:** Steps use `main.ide-design-edit .design-editor ... .fn-card .use-row .workbench-instance-content`, `.add-type`, `.add-prop`, `.palette-item`, `.refresh-eval`, `input.node-attr-value`, `button.add-use`, etc. These exactly match classes/structures emitted by current app.deenv renderNodeEditor + component sections + use-preview.
- **Component configs + live:** "Configurations" (MetaUse) + per-use preview + "editing an arg remounts", "stateful config mounts independent live instance", "unseeded ambient shows real error in card". Matches fn-uses + instancemount + workbench sandbox + evalContext.
- **Tree/canvas:** Add element/text/for/if, tag/expr/condition edits, attrs, reorders (move-up/down), unwrap/wrap (with honest disables + hints), palette insert targeting (selected leaf/sibling, for body, if, root fallback), canvas selection + Escape + loop instance selection + literal-href containment. All implemented and exercised.
- **Eval/Refresh:** Canvas + previews use evalContext(..., evalRefresh); tests bump via button or direct eval + dispatch(input/change) after Fill for arg edits. This is how current live updates are forced (post micro-commit timing).
- **Commit:** doCommit -> sys.commitDesign; tests assert last-commit line, history, detail, clears on success, author, migration roundtrip. Matches current commit bar + KernelHostActions + Branch head.
- **Unified commit impact:** Designer edits go through client bindings -> ctx.commit() (top-level scalars/micro, setAdd etc for collections like types/props/render/children/uses). Tests that assert server store after UI action (Eventually + ReadExtent("MetaType")) or remap survival continue to be valid. No test steps were found still emitting legacy arrayAdd/setByProp.
- **Other surfaces:** Login gate on committed designer, client-side nav (no flash), kebabs (independent state), generic New/RefSelect in create forms, SetTable rows — still accurate (designer is a custom render but uses shared generic pieces + kernel hosting).

### Areas of Potential Staleness / Hygiene Debt (minor)
- **Wait strategies (biggest mismatch to recent guidelines):** Heavy use of `EventuallyAsync` (store polling) + `WaitForFunctionAsync` (for value mirrors after Fill/Click, previewHtml, counts, hints) + explicit dispatchEvent("input"/"change") + refresh-eval. 
  - Many value assertions could use built-in `InputValueAsync()` / `IsCheckedAsync()` + short attached waits (as done in NavigationSteps/ObjectModelSteps).
  - "eventually" comments and high ActionMs waits remain.
  - Preview update after arg set is not fully automatic in some paths (hence dispatch + bump); this is current reality but fragile.
- **Debug / dump code:** Several previewHtml dumps via EvaluateAsync + console-like logging for "remap/previews" (legacy from debugging live mount timing). Should be removed or behind a flag.
- **"Just added" state machine:** Private fields (_justAddedTypeName, _newInstanceName) + special "has(input.type-name[value=\"\"])" locators + post-fill rename steps. Works because initial rows start anonymous; still needed but could be improved with better test ids or data-testid.
- **Convert path:** A handful of scenarios test "text-authored design" -> "Convert" button -> structured tree. The path still exists (if no render rows, show convert + importRender). Relevant for compat but low priority vs primary structured authoring.
- **Duplication with other features:** Kebab menus, create-form reveal, RefSelect, basic field input, client nav are exercised here + in SelfHostedUi.feature / Navigation.feature / ObjectModel.feature. Designer version adds kernel hosting + custom surfaces, but some assertions overlap. Not "stale" but worth noting for future dedup.
- **Timing sensitivity exposed by unified commit:** Micro top-level commits + ambient ctx + notify on every keystroke can cause more re-renders/remounts of .use-preview workbench instances and canvas. Tests that assert "same-frame", "never clobbers", "live without Refresh" or "independent" are still conceptually valid but more sensitive (prior fixes added scoping + .First/.Last + explicit waits).
- **No evidence of tests for retired features:** No remaining references to setByProp, arrayAdd in active step bodies (only historical comments). No live-op direct writes. All data changes route through commit.
- **Store bypass in assertions:** Steps read _designer.Store.ReadExtent("Meta*") directly. This is powerful for end-to-end verification but assumes the meta schema (MetaNode, MetaFn, MetaUse, order, etc.) is stable — it is.

**Conclusion on relevance:** Keep the large majority of scenarios. They validate the current "Figma-like" designer (structured tree + components + live config previews + canvas + commit/publish/merge). Minor pruning candidates: ultra-timing "never flickers" + some convert + redundant generic-UI assertions. Prioritize modernizing waits (no fragile high WaitForTimeout except true negatives; prefer Playwright builtins + event-driven + short constants) before/while splitting. The unified commit model did not invalidate the test intent; it changed timing surfaces that the tests (and prior scoping/dispatch fixes) are now probing.

## Categorization for Splitting DesignerSteps.cs

**Problem:** 3353 lines + 334 bindings in a single class is unmaintainable. Natural boundaries exist that align with UI areas, data model sections, and scenario groups.

**High-level scenario distribution (from titles):**
- Auth/Login: ~1
- Library/List UI (designs list, SetTable, kebabs, create New, delete, instances list/selector): ~15
- Type/Prop Editor (add/rename/retype/set card/key/multiline/enum/remove): ~15
- Structured Render + Canvas + Components (tree nodes, unwrap/wrap/reorder, canvas live/select/eval/foreach, palette insert, fn-card, state vars, configs/uses, live previews, independent mounts): ~46 (largest cluster)
- Commit/History: ~11
- Publish/Preview/Apply: ~7
- Branches/Merge: ~5
- Instances/Apply + misc: ~9 + ~6

**Step method distribution (approximate, by name + body focus):**
- Misc/Setup/Navigation/Library/Instances/kebabs/create: ~ (large share of bindings)
- Components (fn-card, add-use, configs, palette): high
- TypePropEditor
- TreeEditor (add-element/for/if, node edits, unwrap etc.)
- CanvasLive (select, eval, refresh)
- Commit + Publish + BranchMerge: smaller dedicated

### Recommended Split (target files, ~400-700 lines each)

Introduce a small supporting type for shared mutable state (avoids duplication and ctor explosion). This is the key enabler for clean split.

1. **DesignerTestContext.cs** (new, tiny)
   - Holds the cross-cutting per-scenario fields: _designer, _newInstanceName, _justAddedTypeName, _urlBeforeClick, _lastCreatedInstanceId, _todoTargetLogLinesAfterStaleness, any preview caches.
   - Or simply extend InstanceContext with designer-specific bags if preferred.
   - Provides helpers that were private: ComponentCard(), ConfigRow(index), JustAddedTypeRow(), scoped locators, common JS eval helpers (bumpEvalRefresh, getPreviewHtml).
   - Injected into all step classes (composition over inheritance).

2. **DesignerNavigationLibrarySteps.cs**
   - Given (operator/anonymous IDE running + login)
   - Open routes (designs list, instances list, edit design, open instance)
   - Create design/instance via generic New / create form / RefSelect
   - Client nav, back, no-flash, live mark survival
   - Kebabs, actions menus (list + detail), independent open state, rename on detail
   - List assertions (shows design, has actions, generic New only, no bespoke)
   - Delete confirm/cancel
   - Some instance apply + design pick

3. **DesignerTypePropEditorSteps.cs**
   - Add type / remove unnamed
   - Name just-added type, set base-type, values (enum)
   - Add/edit prop (name, type picker, cardinality, keyType, multiline toggle)
   - Rename type, retype prop, set card/key
   - Assertions: editor shows types/props, hints (needs at least one field), irrelevant fields hidden, picker groups
   - "just added" tracking for types (can move the field here or keep in context)

4. **DesignerTreeEditorSteps.cs**
   - Add element/text/expr, for (item+collection), if (condition + else)
   - Edit node-tag, node-expr, node-for-*, node-if-condition, attr name/value
   - Reorder (move up/down) for nodes + attrs + components configs
   - Remove node/attr
   - Unwrap (mid-tree, sole root, disabled cases + honest hints)
   - Wrap
   - Assertions on tree structure, order survives reload, children spliced

5. **DesignerCanvasSteps.cs**
   - Canvas renders live (no reload on tree edit)
   - Expression eval vs seed data, "chips until Refresh", live new-edit eval without click, degrade on invalid, no race with structural
   - Click canvas element -> selects tree row + highlight + scroll
   - Click loop instance -> selects template
   - Click inside expanded component content -> selects body row
   - Click literal-href anchor -> selects row (no nav)
   - Tree row click -> highlights canvas
   - Escape clears both
   - Invalid design (fieldless root) shows degrade + type hint; fix + refresh clears
   - Foreach: imports, shows items + if-branch, falls to template

6. **DesignerComponentsSteps.cs**
   - Add component (+ Component), edit name/params (reserved name, duplicate hints)
   - Design-level + fn state vars (add, edit init, name hints, shadows)
   - From-scratch component (root-only add-row -> first body)
   - Add/remove use (configuration)
   - Add/edit use args
   - Reorder uses
   - Palette: open, lists own + library, insert into selected/fallback/for/if/leaf; repeated inserts siblings; disabled on bare root
   - Assertions: Components area shows fn + params persist to projection; canvas expands invocation + body edit repaints live; staleness banner on unnamed->named + refresh clears

7. **DesignerLivePreviewSteps.cs** (or merge into Components if small)
   - Config previews: count rows, name shown, arg values
   - Static preview (renderTree) + live mount (.workbench-instance-content)
   - Independent mounts for different configs/args
   - Edit arg -> remounts only that instance (dispatch + refresh)
   - Unseeded ambient error shown in card; page stays alive
   - Stateful component initial state + design var init shown; chip until Refresh
   - Reset returns local draft; handler isolation (logout, throw, extent)
   - Library component using sys.* renders real UI in card
   - Refresh values button / evalRefresh bump forces update
   - (Includes the previewHtml debug paths — to be cleaned)

8. **DesignerCommitPublishBranchSteps.cs** (or split Commit vs Publish if volume grows)
   - Commit bar: message/migration/revert inputs, commit button, success clears, error banner on invalid
   - History: lists newest first, empty msg placeholder, author, open detail + back
   - Commit detail: shows changes (rename), migration render
   - Publish: preview (destructive loud, drift-only advice), apply carries data, stale reject, guarded apply
   - Branches: create lists link; merge clean, conflict resolve by pick, access rule surfaces block
   - (Note: DesignCommit.feature / DesignMerge.feature / DesignSnapshot.feature may share or duplicate some; review for dedup on split.)

### Additional Recommendations for the Split
- **Locator hygiene:** Centralize all "main.ide-design-edit .design-editor ..." + .First/.Last/.Nth + has-text helpers in the context or a DesignerPage class. Continue the scoping that fixed strict mode.
- **Wait modernization (do this in parallel or first):** Replace value WaitForFunction with `await locator.InputValueAsync()` after WaitFor Attached + short constant (TestTimeouts.ActionMs = 10s). Use EventuallyAsync **only** for pure store assertions (non-Playwright). Prefer visible/attached waits + auto-waiting actions. Remove or conditionalize previewHtml dumps. Drop "not using custom JS" style comments.
- **State ownership:** _justAddedTypeName stays relevant for type editor steps; consider making "add and immediately name" a single higher-level step or use better test data (e.g. add with a temp name then assert).
- **Test data / seeds:** The designer uses its own seeded designs ("todo", "crm", "designer"). Live previews run against design's initialData/seed. Keep consistent.
- **Build / run impact:** After split, the generated .feature.cs treenode names remain the same; no change to filter commands. Steps are still discovered globally.
- **Potential further extractions:** 
  - A DesignerComponentCard / UseRow page object for the complex config preview interactions.
  - Move common "fill + dispatch + wait mirror + bump refresh" into a helper for arg edits.
- **Verification after split:** Targeted treenode runs on Designer* classes + full relevant milestone tags. Explicit `dotnet build -c Release DeEnv.Tests.csproj` (touch src if PreserveNewest needed). Then isolated runs before any broad suite.
- **Order of work:** (1) modernize waits + remove debug dumps (smaller PR, improves speed/reliability first), (2) introduce DesignerTestContext, (3) split file-by-file with one area at a time + re-run the owning scenarios, (4) prune obvious stale timing tests if any.

## Next Steps / Integration with Broader Plan
- This analysis feeds the full test suite fix (designer cluster was a known hotspot due to ambient/micro-commit timing on live previews/canvas).
- Any structural change to steps or waits must follow AGENTS.md: isolated worktree, build skill for the change slice, explicit test csproj build before --no-build runs, PowerShell treenode filters for precision ("/*/*/<Class>/*"), PreserveNewest data hygiene.
- After split, the ~3k line problem is solved; individual files become reviewable and ownable by area (tree vs live components vs commit paths).
- Consider tagging scenarios more granularly (@designer-library, @designer-tree, @designer-components-live, @designer-commit) to enable cheap targeted runs.

**Evidence files referenced (current as of session):**
- DeEnv/instances/1/app.deenv (render fns, component sections, evalRefresh, instancemount)
- DeEnv/Instance/workbench.ts (live mount driver)
- DeEnv/Designer/SchemaBridge.cs, Kernel/KernelHostActions.cs
- DeEnv/Http/WsHandler.cs + Storage/* (unified commit)
- DeEnv.Tests/Features/Designer.feature (all 115 titles)
- DeEnv.Tests/Steps/DesignerSteps.cs (bindings + current locators/dispatches)
- Prior docs/plans/2026-07-14_full-test-suite-fix.md (context on ambient impact)

This document is the direct response to "first analyze the tests if they are still relevant and up to date against current code base. then categorize tests for splitting them into multiple files."

(End of analysis.)