---
name: design
description: Scaffold a new DeEnv instance — a hand-authored app.deenv (types + initialData + optional access/common/ui), registered in kernel.json and verified booting locally. Use when the user wants a new app/instance created, not an existing one's schema evolved (that's the non-destructive-apply/publish path). Args (optional): a short description of what the app should model.
---

# Design a new instance

Scaffolds one new DeEnv instance the way every instance in this repo is
actually made today: a hand-written `app.deenv` (INSTANCE_DESCRIPTION_FORMAT.md
is the canonical grammar), a `kernel.json` entry, and a local boot-and-render
check. Not for evolving an EXISTING instance's schema in place — that already
has its own mechanism (non-destructive apply / publish) and isn't this skill's
job.

## 1. Ground the design in the real rules and real examples

Read, in order:

- **INSTANCE_DESCRIPTION_FORMAT.md** — the canonical section grammar
  (`types` → `initialData` → `access` → `common` → `ui`), what each section
  requires, and the fully-auto-vs-fully-custom UI split.
- **One or two existing instances** as style references, picked by what the
  new design needs:
  - `DeEnv/instances/2/app.deenv` (todo) — plain schema + seed, no access
    rules, no custom UI (fully auto).
  - `DeEnv/instances/5/app.deenv` (devlog) — a `User` type + role-based
    `access` rules + the `multiline` text attribute; the reference for ANY
    design that needs login/roles.
  - `DeEnv/instances/1/app.deenv` (designer) — the one fully-custom `fn
    render()` in the repo; only worth reading if the new design truly needs
    bespoke rendering the generic UI can't give it.

## 2. Settle the shape before writing

If the user's description doesn't already answer these, ask (don't guess on
the ones that are genuinely theirs to call):

- **The type graph** — what `Db` holds, the object types, their props, sets
  vs. single references, any enums.
- **Auth** — does this app need a login (a `User` type + `access` rules), or
  is it open, single-operator, no-auth (the todo/crm/shop default)? Don't add
  a `User` type or `access` section unless something in the request actually
  needs gating — per-type rules govern only the types they name, so a
  no-auth app is just the absence of the section, not an empty one.
- **UI** — fully auto (no `ui` section — the default, and the right choice
  unless a specific interaction the generic ObjectForm/SetTable/DictTable
  library genuinely can't express) vs. fully custom (`fn render()`).

Minimal by default: the smallest schema that models the request, no
speculative fields or types "for later."

## 3. Write the app.deenv

- New file at `DeEnv/instances/<next-id>/app.deenv` — `<next-id>` is one past
  the highest id in `DeEnv/kernel.json`.
- Follow the section order and indentation rules exactly
  (INSTANCE_DESCRIPTION_FORMAT.md — four-space canonical indent, no colons in
  `types`, `TypeName id` + indented `field: value` in `initialData`).
- `.deenv` files are **UTF-8 without a BOM** — use the Read/Edit/Write tools
  (not a raw PowerShell redirect, which defaults to UTF-16).
- A seed (`initialData`) is optional but makes the instance useful the moment
  it boots — include a small one unless the user wants to start empty.

## 4. Register it

Add one entry to `DeEnv/kernel.json`'s `instances` array:
`{ "id": <next-id>, "app": "<name>" }` — no `designId` (that field marks an
instance spawned from the self-hosted designer; a hand-authored instance
never has one, matching todo/crm/shop/devlog/demo).

## 5. Verify it boots and renders

Build, run the kernel locally, and hit the new instance's root path —
confirm no load-time `SchemaValidationException`/`StoredDataException`, and
that the generic UI (or the custom render) actually renders the seeded data.
Use the `run` skill or the preview tools if the app is reachable over HTTP;
otherwise `dotnet run --project DeEnv` and a plain curl/browser check is
enough.

## 6. Land it

Same discipline as `/build`'s landing step:

```
git -C <worktree> add DeEnv/kernel.json DeEnv/instances/<id>/app.deenv
git -C <worktree> commit -m "..."
git merge --ff-only <branch-name>           # from the main worktree
```

Skip a full review for a plain schema-and-seed, no-custom-code design — it's
declarative shape, low risk. Run `architecture-reviewer` first if the design
has an `access` section with non-trivial conditions, or `ui-architecture-reviewer`
+ `ux-reviewer` if it has a custom `fn render()` — same bar `/build` applies to
any change in those zones.

---

## Gotchas

- **`sys` is reserved** — never usable as a type name; it's the host-action
  access subject (see INSTANCE_DESCRIPTION_FORMAT.md's `access` section).
  Irrelevant to an ordinary app design.
- **Dict-valued fields aren't access-gated yet** — don't design a `dict of X`
  prop as the enforcement point for anything that needs to be private.
- **An `access` section only governs the types it names** — adding rules for
  one type doesn't lock down the rest of the schema; a type with no block of
  its own stays open.
- **Isolated worktree, local main** — this repo runs multiple concurrent
  Claude sessions on the shared tree; never write/build/commit there directly.
  `git worktree add -b <branch> C:\Users\Filip\Documents\deenv-worktrees\<branch> main`
  (local `main`, not `origin/main` — nothing is pushed, so the remote is stale).
