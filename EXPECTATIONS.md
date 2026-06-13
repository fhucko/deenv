# Expectations

The layer between VISION.md (the destination) and ROADMAP.md (the sequence):
**what "good" looks like.** A milestone can build and still be wrong if it
doesn't meet these. The first slice was scrapped partly because this layer
wasn't written down — so a working app couldn't be judged against anything.

## Design principle — minimal by default

**Keep everything as minimal as possible: the least boilerplate, the most
defaults — unless minimalism would hinder the functionality.** This is a
first-class quality, not a nicety. An app, a type, a `ui` section, a function
signature should require only what carries meaning; everything derivable should
be derived, everything optional should be optional with a sensible default.

- **The common case is zero-config.** The plain, expected behavior happens with
  no markers, flags, or ceremony. You write configuration only to *deviate* from
  the default, never to ask for it.
- **Optional is the default for anything inferable.** `fn render()` is optional,
  `initialData`/`common` are optional, a lambda's single param drops its
  parentheses — because none of them carry information the system can't supply or
  do without. Add a required token only when its absence is genuinely ambiguous
  or wrong.
- **The "unless it hinders functionality" caveat is the only escape hatch.** A
  flag or opt-in is justified *only* while removing it would break or limit real
  behavior — and then it is **temporary scaffolding to delete**, not a permanent
  part of the surface. State that intent where the flag lives, and remove it when
  the blocker is gone (e.g. the self-hosted generic UI is opt-in *only* until its
  slices reach parity and it can become the default; see DECISIONS.md).
- **Boilerplate is a smell.** If two apps must repeat the same lines to get the
  same ordinary outcome, that outcome should be the default instead.

This pulls against *premature* scope, not against ambition: minimal surface, full
capability. When a default would hide a real choice, surface the choice — but
make the common answer the one you get for free.

## Technical expectations

- **One solution, two projects.**
  - `DeEnv` — the whole environment. Buildable in **Visual Studio 2026**.
    The instance lives *inside* this project (not its own project) for now.
  - `DeEnv.Tests` — Gherkin-style tests (Reqnroll on TUnit), C# step
    definitions.
- **TypeScript only — no hand-written JavaScript.** Author `.ts`; the
  compiled `.js` is build output, never edited by hand, ideally not
  committed (gitignore it).
- **TS compiled via `Microsoft.TypeScript.MSBuild`** (NuGet) wired into the
  `DeEnv` build, so `dotnet build` / VS build also compiles the TS. No
  `npm install`, no `package.json` clutter.
- **"Fully buildable in VS2026" is a verified claim, not an assumption.**
  Confirm a clean VS2026 install builds the TS with only the NuGet package
  added; record any real prerequisite (e.g. a JS runtime) in DECISIONS.md.
- Milestone-1 stack stays minimal: simple HTTP handler, plain-JSON storage
  behind a storage interface. No SQLite/ASP.NET MVC yet.

## Functional expectations

The instance is the only focus for now. The formal instance description is
written **by hand** for now — no designer, no generator.

The full model is in **INSTANCE_MODEL.md**. In short, what building/running
an instance should feel like:

- **The URL is navigation into the data.** Every node has an address; the
  path walks the data tree from the `Db` root. This is the organizing
  principle — not an afterthought. (It also feeds the future render-coupled
  engine: a URL is exactly the "what does the UI want" signal to preload
  against.)
- **One form per type instance.** Navigating to a node shows a single form
  for that node. Objects render as forms; nested lists/dictionaries render
  as HTML tables whose rows link deeper. This predictable forms-and-tables
  rule is the experience.
- **Breadcrumbs mirror the URL path** segment-for-segment.

This is the layer the scrapped slice lacked: a working checkbox met no
functional expectation because none was written. A slice now meets the
functional bar only if it embodies URL-as-navigation and one-form-per-type,
not merely "it runs."

## How a slice is judged "good"

1. It builds in VS2026 as one solution, per the technical expectations.
2. Its `@milestone-1` Gherkin scenarios are green.
3. It meets the functional expectations above — not just "it runs."
4. It doesn't quietly pull in a future milestone (see CLAUDE.md ground rules).
5. It is minimal by default (above): no boilerplate the common case doesn't
   need; any flag/opt-in it adds is justified by functionality and marked as
   temporary.

A slice that passes 1–2 but fails 3 is the failure mode that scrapped the
first attempt. Define 3 before building, not after.
