---
name: build
description: Drive any code change in deenv end-to-end — isolated worktree off LOCAL main, brief a background agent, verify, review, commit, FF-merge. Use PROACTIVELY whenever a concrete change is ready to build and other Codex sessions may share the tree. Args (optional): "slice" (default — Gherkin-first, slice-builder agent), "feature" (feature-builder agent, no Gherkin gate), "ui" (feature-builder agent + ui+ux reviewers).
---

# Build a change

The orchestration loop *around* a background agent — how to drive any code
change from "ready to build" to "merged on main" without clobbering a shared
tree or breaking twin conformance. The agent builds; this is how to set it up,
check it, and land it.

**Change type** (first arg, default `slice`):

| Arg | Agent | Model @ effort | Gherkin? | Review |
|-----|-------|----------------|----------|--------|
| `slice` | `slice-builder` | sonnet @ medium (frontmatter-pinned) | Yes — write scenario first | architecture-reviewer or ui-architecture-reviewer+ux-reviewer |
| `feature` | `feature-builder` | sonnet @ medium (frontmatter-pinned) | No | architecture-reviewer or ui-architecture-reviewer+ux-reviewer |
| `ui` | `feature-builder` | sonnet @ medium (frontmatter-pinned) | Optional | ui-architecture-reviewer + ux-reviewer |

Model + effort live in each agent's frontmatter — never spawn these paths on a
general-purpose agent: the Agent tool has no effort parameter, so a
general-purpose spawn inherits the session's effort (often xhigh) and lands
sonnet in exactly the worst-value configuration.

## 1. Isolated worktree off LOCAL main

This is a solo project that commits locally **without pushing**, and the user often
runs **multiple Codex sessions on the same tree at once**. So: never edit/build/test
in the shared checkout, and never `git stash|reset|checkout` it. Work in a worktree.

```
git worktree add -b <branch-name> C:\Users\Filip\Documents\deenv-worktrees\<branch-name> main
```

Base off **`main` (local), not `origin/main`** — `origin/main` is stale (nothing is
pushed), so `Agent(isolation:"worktree")` comes up frozen at the last push and wastes
runs. Hand-create the worktree and pass it to a **non-isolated** agent.
See memory `env_agent_worktree_base`, `feedback_isolate_concurrent_sessions`.

## 2. Brief the agent

Point it at the worktree. The builder agents (`slice-builder`, `feature-builder`)
are **pinned to sonnet @ medium effort in their frontmatter** — the design lives in
the brief and the suite + reviewers are a hard gate, so the builder's job is mechanical
execution. That is exactly Sonnet 5's niche: cheap bulk work at **low/medium effort**.
Never run sonnet at high/xhigh effort — per Anthropic's own effort/cost curve, at that
price Opus 4.8 is both cheaper and better, and Sonnet 5's tokenizer emits ~30% more
tokens for the same text, eating the sticker discount. If a task needs high effort,
it's an opus task. Escalate the builder to `opus` (Agent `model` override) when the
change lands in subtle zones (twin memo cache, client reconcile, negative-id remap)
where a plausible-but-wrong diff costs more round-trips than the tier saves.

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

Reviewers run on **`opus` @ `high` effort, pinned in their frontmatter** (user decision
2026-07-03, reversing the same-day sonnet default once the effort/cost data landed:
adversarial review is high-effort judgment, and Sonnet 5 at high/xhigh costs the same as
Opus 4.8 at low/medium while performing worse — plus the ~30% tokenizer overhead. `high`
not xhigh: per Opus 4.8 guidance, xhigh's payoff is in agentic *execution*, not review
verdicts). Sonnet is acceptable only for a routine, low-stakes review at medium effort.
Fable is NOT the review tier — it's reserved for the /design grill (see that skill). Hold every review to the bar the 2026-07-03 M13 slice-1 review
set: built and ran empirical probes, proved a cross-restart bug, killed a doc over-claim —
a review with no probing and ungrounded verdicts is worse than none; send it back.
Reconcile reviewer flags against decisions already settled in the conversation before
relaying — don't re-litigate closed decisions.
**Pin the reviewer's diff to the branch's merge-base commit** (`git diff <base>..HEAD`, base
recorded at spawn time) or rebase before review — main can move during long builds, and a
`diff main..HEAD` against moved main makes the reviewer refute skew as if it were the change
(cost a full artifact-finding once, 2026-07-03).
(memory `feedback_auto_review_after_slices`, `feedback_agent_tier_by_role`)

## 5. Land it

Commit on the branch (Co-Authored-By trailer), then fast-forward main:

```
git -C <worktree> add <explicit paths>      # NOT -A — .codex/launch.json is untracked-local
git -C <worktree> commit -m "..."
git merge --ff-only <branch-name>           # from the main worktree
```

Sync docs in the same landing (AGENTS.md current-focus, ROADMAP, DECISIONS, memory) —
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
