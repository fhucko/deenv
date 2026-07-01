# App description: URL handling + cross-platform links — plan

Design-only, from a Discord thread (2026-07-01). Not yet implemented. Scoped to the `app.deenv`
**description format** — kernel/HTTP wiring is a separate, later concern.

## Problem
`fn render()` reads the `path` system var and hand-parses it (`sys.segment(path, n)`,
`sys.toInt(...)`) to pick which page to show — see `instances/1/app.deenv:244` (the operator
designer's 4-route dispatch). This ties every custom `ui` to "there's a URL," which blocks reusing
the same `ui` functions as a desktop (or mobile) app's screens — those hosts have no URL at all.

## Non-goal: do not revive M8
M8's `view`s (render-fns keyed to a type/path slice, replacing custom render — `DECISIONS.md:643`,
dropped `DECISIONS.md:1014`, guarded against resurrecting `DECISIONS.md:1080`) are the wrong shape
to reach for here. The design below maps a URL to ONE ALREADY-EXISTING `ui` function call — it is
routing *into* custom code, never a rendering system of its own. `ui` stays fully custom-code, the
"no partial-customization middle layer" rule (`INSTANCE_DESCRIPTION_FORMAT.md`) is not being broken.

## Design

### 1. `web` section — URL → `ui` entry point
New top-level section. Maps a URL pattern to a call into a `ui` function; `{name}` captures a
segment and passes it as that call's argument.

```
web
    "/" -> mainPage()
    "/designs" -> designsListPage()
    "/designs/{id}" -> designEditorPage(id)
```

`designEditorPage` becomes `fn designEditorPage(id)` — takes an explicit param instead of reading
`sys.segment(path, 2)` itself. **`ui` never references `path` again.**

### 2. Desktop/other hosts need no section at all
A non-web host just calls whichever `ui` fn it wants as its root/screen directly — no `web` section
required, no URL concept in scope. This is *why* `ui` has to stop reading `path`: it's the only way
the same functions serve a host with no URL.

### 3. `sys.link(fn, ...args)` — links without hardcoding a URL
```
ui
    fn render()
        return <a to={sys.link(designEditorPage, 5)}>
            "Edit design 5"
```
Produces an opaque nav target (function + args), not a string. Web resolves it via reverse-lookup on
`web`'s table (`designEditorPage(id) -> "/designs/{id}"`, substitute `5`). Desktop resolves the same
value by invoking `designEditorPage(5)` directly (e.g. in a new window). `web` stays the only place
that ever knows a URL exists.

Open question, not resolved in the thread: reverse-lookup is ambiguous if the same fn is registered
at two different `web` patterns, or absent if it's registered at none.

### 4. Presentation (push / switch / overlay) — declared on the target, not the caller
Three verbs cover every platform's link-opening behavior:
- **push** — new screen on the current context's stack (web: default nav; mobile: default).
- **switch** — jump to a different persistent context, own history (web/desktop: tab/window; mobile:
  tab-bar / Discord-style server-rail pattern).
- **overlay** — modal/dialog/sheet on top, no context change. Same concept on all three platforms.

Push and switch both collapse to "host's default when `mode` is omitted." Only `overlay` needs
naming.

Mode is a property of the **target fn**, not the call site — every link to the same page should
present the same way:

```
ui
    fn designEditorPage(id) mode: overlay
        ...
```

`sys.link(fn, args)` takes no `mode` param; it resolves to whatever the target declared.

### 5. Per-platform override, only when platforms actually disagree
No `sys.platform` runtime var — a platform section overriding the fn's default is more declarative
and avoids adding a system var nothing uses yet:

```
mobile
    designEditorPage mode: push
```

**Default case (expected common case): no `mobile`/`desktop` section exists at all** — the fn's own
`mode` is the answer everywhere, same pattern as optional `initialData`/`ui` today. A platform
section is only written for the fns where that platform genuinely diverges — never a restatement of
the default.

## Skipped, revisit only if a real case shows up
- `sys.platform` system var — no conditional needs it once overrides are declarative per-section.
- Query strings / wildcard route patterns — flat `{name}` segment capture only, for now.
- What "other window" means per host (new tab vs native window vs mode) — a host-rendering detail,
  not a description-format one; only comes back into scope if it needs to be *declared* per-link.
