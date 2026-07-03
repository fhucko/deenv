---
name: build
description: Drive any code change in deenv end-to-end — isolated worktree off LOCAL main, brief a background agent, verify, review, commit, FF-merge. Use PROACTIVELY whenever a concrete change is ready to build and other Claude sessions may share the tree. Args (optional): "slice" (default — Gherkin-first, slice-builder agent), "feature" (general-purpose agent, no Gherkin gate), "ui" (general-purpose agent + ui+ux reviewers).
---

# Build a change

The orchestration loop *around* a background agent — how to drive any code
change from "ready to build" to "merged on main" without clobbering a shared
tree or breaking twin conformance. The agent builds; this is how to set it up,
check it, and land it.

**Change type** (first arg, default `slice`):

| Arg | Agent | Gherkin? | Review |
|-----|-------|----------|--------|
| `slice` | `slice-builder` (sonnet) | Yes — write scenario first | architecture-reviewer or ui-architecture-reviewer+ux-reviewer |
| `feature` | general-purpose (sonnet) | No | architecture-reviewer or ui-architecture-reviewer+ux-reviewer |
| `ui` | general-purpose (sonnet) | Optional | ui-architecture-reviewer + ux-reviewer |

## 1. Isolated worktree off LOCAL main

This is a solo project that commits locally **without pushing**, and the user often
runs **multiple Claude sessions on the same tree at once**. So: never edit/build/test
in the shared checkout, and never `git stash|reset|checkout` it. Work in a worktree.

```
git worktree add -b <branch-name> C:\Users\Filip\Documents\deenv-worktrees\<branch-name> main
```

Base off **`main` (local), not `origin/main`** — `origin/main` is stale (nothing is
pushed), so `Agent(isolation:"worktree")` comes up frozen at the last push and wastes
runs. Hand-create the worktree and pass it to a **non-isolated** agent.
See memory `env_agent_worktree_base`, `feedback_isolate_concurrent_sessions`.

## 2. Brief the agent

Point it at the worktree. **Spawn it on `model: "sonnet"`** — the design lives in
the brief and the suite + reviewers are a hard gate, so the builder's job is mechanical
execution. Escalate to `opus` only when the change lands in subtle zones (twin memo cache,
client reconcile, negative-id remap) where a plausible-but-wrong diff costs more
round-trips than the tier saves.

The brief must pin:

- **For `slice`:** the one Gherkin scenario it must make pass (write it first — it's
  the spec). Tag it with the current milestone. Smallest change that passes it.
- **For `feature`/`ui`:** the exact files, the concrete changes, and the acceptance
  criteria (what tests must be green, what behaviour the change must produce).
- **Twins in lockstep:** any change to the interpreters (`DeEnv/Code/CodeExecutor.cs` /
  `DeEnv/Instance/codeExec.ts`) needs a `DeEnv/Code/conformance.json` case proving both
  agree. Client-only behaviour is proven by a Gherkin/TS-client test, not conformance.
- **Storage stays behind `IInstanceStore`** — model terms (paths/nodes/entries), never
  flat key-value, never direct file calls.
- **Gotchas block** (bottom of this file) — paste the relevant ones into the prompt.

## 3. Verify (before any review)

- **Scope:** `git -C <worktree> diff --stat main` — only the expected files.
- **Twins:** if interpreters changed, conformance suite is green on both sides.
- **Main untouched:** the shared checkout has no new commits and no stomped files.
- **Suite green.** Solution is `DeEnv.slnx` (no `.sln`). Whole suite = `dotnet test
  DeEnv.Tests` (no filter). Subset = `dotnet test DeEnv.Tests -- --treenode-filter
  "/*/*/<RealClassName>/*"` (3rd segment is a REAL class name, not the literal "Class").
  Run from **PowerShell, not the Bash tool** (git-bash mangles the `/*/…` path → 0 tests).
  Never `dotnet test --filter` (VSTest-style → noise). See memory `project_test_invocation`.
- **Release config if VS is open** — the Debug `bin` is file-locked; build/test `-c Release`.

## 4. Review

Trivial mechanical edits skip review. Otherwise:
- Interpreter/parser/storage/object-model/wire change → **`architecture-reviewer`**.
- Rendered-UI change → **`ui-architecture-reviewer` + `ux-reviewer`** together.

Reviewers default to `sonnet` (Sonnet 5 — near-Opus review quality at a fraction of the
cost; user decision 2026-07-03). Adversarial review is judgment and a shallow reviewer is
worse than none, so GRADE each sonnet review against the bar the 2026-07-03 Opus slice-1
review set: it built and ran empirical probes, proved a cross-restart bug, and killed a
doc over-claim. Escalate to `opus` for the highest-subtlety gates (publish/migration
atomicity, merge engine, security) or when a sonnet review comes back shallow (no probing,
ungrounded verdicts). Reconcile reviewer flags against decisions already settled in the
conversation before relaying — don't re-litigate closed decisions.
(memory `feedback_auto_review_after_slices`, `feedback_agent_tier_by_role`)

## 5. Land it

Commit on the branch (Co-Authored-By trailer), then fast-forward main:

```
git -C <worktree> add <explicit paths>      # NOT -A — .claude/launch.json is untracked-local
git -C <worktree> commit -m "..."
git merge --ff-only <branch-name>           # from the main worktree
```

Sync docs in the same landing (CLAUDE.md current-focus, ROADMAP, DECISIONS, memory) —
a DONE marker must never coexist with an in-flight body.
(memory `feedback_keep_docs_in_sync`)

## 6. Clean up

```
git worktree remove C:\Users\Filip\Documents\deenv-worktrees\<branch-name>
git branch -d <branch-name>
```

---

## Gotchas — paste relevant ones into every agent prompt

- **NEVER kill the browser by name.** Never `Get-Process chrome|chromium|msedge | Stop-Process`
  — Playwright's headless browser IS `chrome.exe` = the user's real browser. Kill only
  `testhost`, or filter by path (`*ms-playwright*`). (memory `feedback_no_kill_browser_by_name`)
- **Tag-name/fn-name collision:** a tag whose name resolves to an in-scope fn is a component;
  renaming a fn that returns an element can silently break rendering.
- **Conditional node inside a `foreach` row won't reconcile** — use always-stable children.
- **No fixed sleeps in tests** — poll for the condition (`Polling.EventuallyAsync`).
  (memory `feedback_no_test_timers`)
- **`.deenv` files are UTF-8 without BOM.** Use Read/Edit/Write tools. If writing via
  PowerShell: `[System.IO.File]::WriteAllLines($path, $lines, [System.Text.UTF8Encoding]::new($false))`.
- **Don't remove the only server-side accessor of data a client-toggled view reuses** —
  structural privacy ships only what the server render touched.
  (memory `project_client_toggle_needs_shipped_data`)
