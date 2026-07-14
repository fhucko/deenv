# Testing

This document captures testing conventions, especially browser/Playwright-driven tests for the designer and generic UI. It exists so that hard-won lessons about reliability, re-renders, and Playwright behavior do not need to be rediscovered.

## Philosophy

- **Use the simplest possible Playwright API calls.** Default `ClickAsync()`, `FillAsync()`, `WaitForAsync()`, etc.
- **Prefer native locator waits over custom polling or `WaitForFunctionAsync`.**
- **Root causes live in the app, not the tests.** If something requires `Force = true`, `Evaluate("el => el.click()")`, or heavy workarounds in steps, fix the render/reconciliation or add stable keys in the UI instead.
- Tests should observe the *effect* the user would see (new row appears, value is in the DOM, empty state is gone, count changes, etc.).
- Designer tests in particular are sensitive to re-renders because the entire designer UI is itself a deenv-rendered application.

## Locators and Actions (the core rule)

- **Always use `ILocator`**. Never store `ElementHandle` or raw DOM references returned from `EvaluateAsync`.
- Locators are lazy. Every action (`ClickAsync`, `FillAsync`, `WaitForAsync`, etc.) re-resolves against the current DOM.
- Plain calls are the default:

  ```csharp
  await page.Locator("button.add-type").First.ClickAsync();
  await page.Locator(".type-card:has(input.type-name[value=\"\"])").First.WaitForAsync(...);
  await input.FillAsync(name);
  await TypeNameInput(name).WaitForAsync(...);   // value-attribute locator
  ```

- After a fill that should cause a re-render to reflect the new value, wait using the value-attribute locator (e.g. `input.type-name[value="Db"]`). This is the native Playwright equivalent of "wait until the bound value is visible in the DOM."

- For counts / "N items exist":
  - Use `.Nth(count-1).WaitForAsync(Attached)` for "at least N".
  - Use `.First.WaitForAsync(Detached)` when expecting zero.

## Playwright Actionability and Retries (exact behavior)

Playwright actions perform **actionability checks** before/during the action:

- Attached
- Visible
- Stable (bounding box)
- Receives events (not obscured)
- Enabled

From the docs (locator.click and similar):

> 1) Wait for actionability checks on the element, unless `force` option is set.
> 2) Scroll the element into view if needed.
> 3) Use `page.mouse` to click...
>
> If the element is detached from the DOM **at any moment during the action**, this method **throws**.

For some actions (e.g. check/setChecked) the docs explicitly say:

> If the element is detached **during the checks**, the whole action is retried.

Key points:
- The retry/re-resolution loop is primarily tied to the **checks** phase.
- Once past checks, detachment during the actual gesture or dispatch usually produces "element is not attached to the DOM" rather than a silent second click.
- Re-renders that land *between* "checks passed" and "mouse events dispatched" are a documented source of flakes ("element not attached" or the action acting on a now-stale node).
- Using pure locators (never element handles) ensures that any retry re-queries the current DOM. This is the correct and recommended approach.

We therefore use the plain default calls + strong post-action locator waits. We do **not** use `Force = true` or `Evaluate("el => el.click()")` as a general pattern.

## Re-renders and the Reconciler

The client has a reconciler in `DeEnv/Instance/ui.ts` (`updateChildren`):

- Prefers `data-key` (and the `key` supplied by the renderer) for identity.
- Falls back to positional + tag name for unkeyed children.
- Real db-backed lists (SetTable rows, etc.) usually get stable `data-key` from the member id.
- Designer-internal lists (`design.types`, `design.fns`, `f.uses`, `design.vars`, render tree nodes, etc.) are plain `foreach` over in-memory arrays. They frequently lack stable keys on the wrapper elements.

Consequence: mutating one of these lists (add type, add config, add arg, etc.) can cause surrounding DOM (including the `+ Type` / `+ Configuration` button) to be recreated. This is the source of the "renders twice / double add" appearance during fast client mutations.

The correct long-term fix is proper keying when we add better array support in the database layer (and when we emit keys from `foreach` over designer meta-structures). In the meantime, tests rely on:
- Waiting for the *observable result* (new empty card, named value present, empty card count decreased via `Detached`, etc.).
- Scoped locators (`.fn-uses .use-row`, specific type card via `value=`, etc.).

## Designer Test Patterns

- Scope aggressively. Generic `.use-row` or `.type-card` is fragile once multiple components or types exist.
- Use value-attribute selectors for "the thing that currently has this name/value":
  - `TypeNameInput(name)`, `JustAddedTypeRow()`, `PropKeytypeInput(...)`, etc. (see `DesignerSteps.cs` helpers).
- After naming/editing something that was previously the "empty" item, wait for the old empty state to disappear (`:has(input...[value=""])` → `Detached`) *and* for the named version to appear.
- Configuration area uses `.fn-uses .use-row` (not bare `.use-row`).
- Preview/live-instance checks often need to distinguish static render-tree output from real mounted instances (data-node marker, etc.).
- Many steps already do `await X.WaitForAsync()` immediately after the action that should produce X.

## What to Avoid

- `WaitForFunctionAsync` for presence, value equality, or counts when a locator equivalent exists.
- Storing element handles.
- `Force = true` on clicks (use plain calls + better waits or fix the UI).
- `Evaluate("el => el.click()")` or value-forcing scripts for user actions.
- Custom polling or `Task.Delay` for DOM state.
- Assuming a button or card will survive a mutation that affects its parent list.

## General Rules

- Always do a clean build (`dotnet build -c Release`) before diagnosing test behavior.
- Use the existing `EventuallyAsync` helper for *store* conditions, not for waiting on the browser DOM.
- Post-action waits should be on the *visible/structural effect*, not on internal implementation details.
- Designer tests exercise the full client runtime + reconciler + WS round-trips. They are the canary for rendering and identity problems.

## References

- `DeEnv/Instance/ui.ts` — reconciler (`updateChildren`, keyed vs unkeyed)
- `docs/plans/` various (component keying, designer tests, etc.)
- Playwright docs: actionability, auto-waiting, locator retry behavior
- `DeEnv.Tests/Steps/DesignerSteps*.cs` and the various `Designer*.feature` files for current patterns
- `TestSupport/Polling.cs`, `SharedBrowser.cs`, `TestTimeouts`

When in doubt, make the test step as close as possible to:

```csharp
await locator.ClickAsync();           // or FillAsync
await observableEffect.WaitForAsync(Attached);   // or Detached, or Nth(...)
```

Anything more complicated should be justified and documented here.