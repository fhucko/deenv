---
name: fix-tests
description: >
  Fix failing deenv tests one at a time — diagnose root cause, grill the fix, no timeout
  inflation, split light vs E2E when it helps. Use when the user says "fix tests", "make
  tests pass", "failing scenario", "one test at a time", "suite is red", or runs /fix-tests.
---

# Fix tests (one failure at a time)

Drive the suite green **without** masking slowness or flakiness with longer timeouts.
Each failure is a diagnosis + a grilled decision, not a timeout bump.

## Hard rules

1. **One failing test at a time.** Fix it, re-run *that* test, then re-scan for the next failure. Never batch-fix five reds in one commit unless they share one root cause proven by a probe.
2. **Do not change timeouts** (`TestTimeouts`, `EventuallyAsync` defaults, Playwright defaults, `timeoutMs:` args) unless the user explicitly asks. Timeouts hide real bugs (wrong selectors, missing waits, conflated scenarios).
3. **Grill every decision** before editing (short adversarial pass — see below). Prefer product/test-structure truth over making the assert pass.
4. **Prefer fixing the right layer:** wrong step selectors → fix steps; conflated Gherkin (authoring + full deploy) → split scenarios; product bug → fix product + keep the failing assert.
5. **No fixed sleeps.** Use `Polling.EventuallyAsync` or Playwright auto-wait locators only.
6. **Never kill browsers by name** (`chrome` / `msedge`) — only test hosts / Playwright paths if needed.
7. **No shared kernel across scenarios** unless the user explicitly opts in. Isolation stays default.

## Invocation

- `/fix-tests` or "make all tests pass" / "fix failing designer tests"
- Optional args in the user message: a feature class, a scenario name, or "designer only"

## 0. Baseline

```powershell
# From repo root, PowerShell (not git-bash — treenode paths get mangled there).
# Prefer -c Release if Visual Studio holds Debug bin locks.
dotnet test DeEnv.Tests -c Release -- --treenode-filter "/*/*/<RealClassName>/*"
```

**Never** `dotnet test --filter` (VSTest-style noise). Real class names from generated
`DeEnv.Tests/obj/*/net9.0/Features/*.feature.cs` (`public partial class …Feature`).

Unit class example: `/*/*/DesignerSourceTests/*`.

List names: `dotnet test DeEnv.Tests --list-tests`.

## 1. Pick the next failure

- Prefer the **first** failure in a focused feature run, or a named scenario the user cares about.
- Capture: scenario title, step that failed, exception type, call log, step standard output.
- Re-run **only that test** with the exact method/treenode until green before touching the next.

## 2. Diagnose (before any fix)

Ask, in order:

| Question | If yes… |
|----------|---------|
| Wrong locator / stale class names vs real markup? | Fix the step to match `instances/1/app.deenv` (or the fixture under test). |
| Assert races async work but never waits for the real condition? | Wait for the *outcome* (DOM text, store field, projected string) — not a longer timeout. |
| Scenario conflates UI authoring with full apply/file deploy? | **Split**: light scenario = store + `SchemaBridge.ProjectDesignDb`; keep one E2E canary elsewhere (e.g. `DesignerCommitPublish`). |
| Product regression? | Fix product; keep the test strict. |
| Twin/interpreter drift? | Fix C# + TS + `conformance.json` together. |

### Probe when unclear

- Read the failing step definition and the **actual** DOM classes in the app document / library Code.
- For designer store asserts, confirm which `Meta*` fields the When steps write.
- For projection asserts, call `SchemaBridge.ProjectDesignDb` on the design node (see existing `ThenProjectedOrder` / light authoring patterns).

## 3. Grill the decision (mandatory, short)

Before editing, answer:

1. **Is this a product bug or a test bug?** Evidence?
2. **Does raising a timeout "fix" it?** If yes, reject that path — what should we wait for instead?
3. **Does the scenario test two milestones/seams at once?** (e.g. UI edit + kernel apply + disk poll). If yes, split or thin it.
4. **Does the fix weaken the assertion?** If yes, redesign so the real contract is still proven.
5. **Could a unit/Gherkin-light path cover this without full kernel boot?** Prefer that for authoring/projection; keep sparse E2E for apply/restart.

Record the grill in the commit message or a brief reply to the user (2–5 bullets).

## 4. Apply the smallest fix

Patterns that have already worked in this repo:

- **Stale selectors** — match real classes (e.g. `.publish-section` / `.branch-section`, not instance-only `button.apply-design` or ambiguous `.branch`).
- **Async "eventually" that wasn't** — use Playwright `Expect(...).ToHaveTextAsync` / locator waits that poll the outcome (still under existing action timeouts).
- **Light vs deploy** — author in browser → assert designer store + `ProjectDesignDb`; drop open-instance + apply + 30s file poll when deploy is covered elsewhere.
- **Unit projection tests** — in-memory `ObjectValue` / `DesignFromText` instead of temp `JsonFileInstanceStore` when the code under test takes a design node.

Do **not**:

- Raise global or step timeouts to "make CI green".
- Share kernel/browser across scenarios without an explicit user ask.
- Delete coverage without a remaining canary that still proves the hard path.

## 5. Verify

```powershell
# The one test (or its feature class if isolation is required)
dotnet test DeEnv.Tests -c Release -- --treenode-filter "/*/*/<Class>/<MethodOrGlob>"
```

Only after that passes, run a wider slice (same feature, then related features, then full suite if asked).

## 6. Commit cadence

- One logical fix → one commit (complete sentences).
- Mention the scenario name and the grilled root cause in the body.
- Do not force-push; do not push unless the user asks.

## deenv gotchas (paste into subagent briefs)

- Solution is `DeEnv.slnx`. Whole suite = `dotnet test DeEnv.Tests` (no filter).
- Treenode: 3rd segment is a **real** generated class name, not the literal `Class`.
- PowerShell for treenode filters; git-bash mangles `/*/*/...`.
- Designer GIVEN boots a real kernel (~tens of seconds) — don't "fix" slow boots by sharing kernel; thin the scenario or optimize boot later.
- Storage behind `IInstanceStore` only; no direct file access in product code.
- Twin C#/TS lockstep via `conformance.json` when interpreters change.
- No `Thread.Sleep` / fixed `WaitForTimeout` in tests.

## Done criteria

- The named failure is green under the **same** timeouts.
- Grill notes are visible (reply or commit).
- No drive-by refactors outside the failure's seam.
- Next failure only after this one is committed or clearly left uncommitted per user.
