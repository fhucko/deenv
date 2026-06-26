---
name: build-slice
description: Drive a milestone slice of deenv end-to-end — isolated worktree off LOCAL main, brief the slice-builder agent (Gherkin-first, twin-locked), verify, review, commit, FF-merge. Use when a concrete in-scope slice of the current milestone is ready to build, especially when other Claude sessions share the tree.
---

# Build a slice

The orchestration loop *around* the `slice-builder` agent — how to drive one slice
from "ready to build" to "merged on main" without clobbering a shared tree or
breaking twin conformance. The agent builds; this is how you set it up, check it,
and land it.

## 1. Isolated worktree off LOCAL main

This is a solo project that commits locally **without pushing**, and the user often
runs **multiple Claude sessions on the same tree at once**. So: never edit/build/test
in the shared checkout, and never `git stash|reset|checkout` it. Work in a worktree.

```
git worktree add -b <slice-name> C:\Users\Filip\Documents\deenv-worktrees\<slice-name> main
```

Base off **`main` (local), not `origin/main`** — `origin/main` is stale (nothing is
pushed), so `Agent(isolation:"worktree")` comes up frozen at the last push and wastes
runs. Either hand-create the worktree as above and pass it to a **non-isolated** agent,
or put a base-verification guard in the agent prompt (`git log -1 main` must match).
See memory `env_agent_worktree_base`, `feedback_isolate_concurrent_sessions`.

## 2. Brief the slice-builder agent

Point it at the worktree. The brief should pin:

- **The one Gherkin scenario** it must make pass (write the scenario first — it's the
  spec). Tag it with the current milestone.
- **Smallest change that passes it.** No future-milestone scope (CLAUDE.md ground rules);
  minimal authoring surface (any new flag is temporary scaffolding to delete).
- **Twins in lockstep:** any change to the interpreters (`DeEnv/Code/CodeExecutor.cs` /
  `DeEnv/Instance/codeExec.ts`) needs a `DeEnv/Code/conformance.json` case proving both
  agree. Client-only behavior (e.g. run-once-across-renders — C# `Memoize` is write-only)
  is proven by a Gherkin/TS-client test, not conformance.
- **Storage stays behind `IInstanceStore`** in model terms (paths/nodes/entries), never
  flat key-value, never direct file calls.
- **Gotchas to hand the agent** (below) — paste the relevant ones into its prompt.

## 3. Verify (before any review)

- **Scope:** `git -C <worktree> diff --stat main` — only the files this slice should touch.
- **Twins:** if the interpreters changed, the conformance suite is green on both.
- **Main untouched:** the shared checkout has no new commits and no stomped files.
- **Suite green.** Solution is `DeEnv.slnx` (no `.sln`). Whole suite = `dotnet test
  DeEnv.Tests` (no filter). Subset = `dotnet test DeEnv.Tests -- --treenode-filter
  "/*/*/<RealClassName>/*"` (3rd segment is a REAL class name, not the literal "Class").
  Run from **PowerShell, not the Bash tool** (git-bash mangles the `/*/…` path → 0 tests).
  Never `dotnet test --filter` (VSTest-style → noise). See memory `project_test_invocation`.
- **Release config if the user has VS open** — the Debug `bin` is file-locked; build/test
  `-c Release` to dodge the lock.

## 4. Review (heavy slices only)

Trivial mechanical edits skip review. For an interpreter/parser/storage/object-model/wire
slice, spawn **`architecture-reviewer`**. For a rendered-UI slice, spawn
**`ui-architecture-reviewer` + `ux-reviewer` together**. Reconcile a reviewer flag against
decisions already settled *in this conversation* before relaying it as fresh — a re-litigated
agreement wastes a round-trip (memory `feedback_auto_review_after_slices`). Fix findings in
the worktree, re-verify.

## 5. Land it

Commit on the slice branch (end the message with the `Co-Authored-By` trailer), then
fast-forward main — never a merge commit for a solo linear branch:

```
git -C <worktree> add <explicit paths>      # NOT -A — .claude/launch.json is untracked-local
git -C <worktree> commit -m "..."
git -C <main> merge --ff-only <slice-name>
```

Then **sync docs in the same landing** (CLAUDE.md current-focus, ROADMAP, DECISIONS, memory)
— a DONE marker must never coexist with an "in flight" body (memory `feedback_keep_docs_in_sync`).

## 6. Clean up

```
git worktree remove C:\Users\Filip\Documents\deenv-worktrees\<slice-name>
git branch -d <slice-name>          # already merged into main
```

## Gotchas to paste into agent prompts

- **NEVER kill the browser by name.** To clear Playwright strays, never
  `Get-Process chrome|chromium|msedge | Stop-Process` — Playwright's headless browser IS
  `chrome.exe` = the user's real browser. Kill only `testhost`, or filter by path
  (`*ms-playwright*`), or just re-run. (memory `feedback_no_kill_browser_by_name`)
- **Name-resolution footgun:** a tag whose name resolves to an in-scope fn is a component;
  renaming a fn that returns an element (`nav`→`navBar`) can silently turn it into a
  component. (memory `project_m11_reactive_components`)
- **Conditional node inside a `foreach` row won't reconcile**, and a no-arg `render()`
  collides across list rows — use always-stable children. (memory `project_m10_kernel_host`)
- **Don't remove the only server-side accessor of data a client-toggled view reuses** —
  structural privacy ships only what the server render touched. (now mitigated by the
  client data layer, but still the cheapest mental model. memory
  `project_client_toggle_needs_shipped_data`)
- **No fixed sleeps in tests** — poll for the condition (`Polling.EventuallyAsync`).
  (memory `feedback_no_test_timers`, `project_test_speedup`)
