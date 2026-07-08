# S3 — Live-preview mini-instance (design)

*2026-07-08. Design pass (grounded + self-grilled) for M12 slice S3, the pivot toward the
WYSIWYG canvas. Architecture APPROVED by the user 2026-07-08 (Option A + seed-data-first).
Companion: docs/plans/visual-designer.md (the M12 map; S0–E2 landed — a design's render is
structured MetaNode rows editable via a recursive tree editor). This is direction; the
build is sliced below.*

## Position (one sentence)

The designer shows a live preview of the design being edited by **projecting the working
copy into an app document, mounting it as a throwaway (non-registry-backed) instance, and
embedding that instance as an `<iframe>`** — so the preview *is* a real instance running the
real interpreter: **zero design/runtime divergence** (deenv's edge over XAML, where the
design surface can diverge from runtime).

## Why Option A (iframe + throwaway instance) — it's almost entirely reuse

- **`SchemaBridge.ProjectDesignDocument`** (SchemaBridge.cs:84-118) turns a Design working
  copy → a validated app-doc string, and it **already validates** (throws
  SchemaValidationException on an invalid design). So "the design doesn't project" is caught
  *before any mount* — a projection error to surface, never a broken preview.
- **Mount is cheap and file-backed:** `HostedInstance.Start` (HostedInstance.cs:54-73) loads
  a description, opens a store, builds two GenHTTP handler trees (app SSR + asset /ws+/js)
  via `InstanceApp.Build` — **no port bind, no thread**. The `KernelHost` front `PathRouter`
  resolves `/apps/<name>` by name over the live set each request (KernelHost.cs:355-360, 121)
  — add a name to `_instances` and it routes on the next request, no host rebind.
- **The real renderer:** `SsrRenderer.Render` (SsrRenderer.cs:105) runs the app's `fn
  render()` (or the synthesized generic UI) against the store data through the real
  interpreter → HTML. The iframe is a real page; the preview cannot lie about runtime.
- **Security floor for free:** a preview instance calls no host action → `HostActionsFor`
  gives it `NoHostActions` (KernelHost.cs:136-138; `UsesHostActions` fails closed) → its
  `/ws` rejects any `hostAction` frame. So it runs the design's arbitrary render/handler
  code but **cannot run kernel devops**. Its access floor is the design's own `access`
  section (rides the projection). The browser iframe is a second isolation boundary. **No
  new security machinery.**

## The one genuinely-new thing to build: a NON-registry-backed mount

Today every "spin up an instance" is registry-backed and persistent: `CreateAsync`
(KernelHost.cs:778-813) mints an id, `mkdir instances/<id>/`, writes the schema file,
appends kernel.json, and mirrors into `db.instances` (`MirrorInstanceInsert`). There is **no
ephemeral-instance concept** (grep ephemeral|throwaway = nothing). The versioning doc's
"forks are ms-cheap in-process mounts" is aspirational relative to this — the *mount*
(handler build) is cheap, but the surrounding create flow persists.

S3 adds a narrower sibling seam to `CreateAsync` minus the persistence:
- **`MountPreview(previewName, appDoc, seed) → name`** — `Start` over a **scratch** schema/
  data path (a temp dir, NOT `instances/<id>/`), registered in `_instances` under a reserved
  `__preview-<key>` name, but **no kernel.json write, no `MirrorInstanceInsert`, not in
  `SpecsFor`**. It routes (router is name-over-live-set) but never persists.
- **`UnmountPreview(name)`** — `TryRemove` from `_instances` + delete the scratch dir. No
  registry/mirror touch.
- **`RefreshPreview(name, appDoc)`** — rewrite the scratch schema + `RestartAsync`-style
  handler swap. (S3b — deferred.)

## Data source — SEED-DATA-FIRST (decided, user 2026-07-08)

Every design carries its own **`initialData` seed** (its `initialData` section; a fresh
store with no data file self-seeds from it — JsonFileInstanceStore.cs:141). So a preview
against `initialData` works **with zero cross-instance dependency and before any real
instance exists** — the *default* case, not an edge. This inverts the map's earlier
"clone real data" lean; grounding showed `initialData` is the cheaper, dependency-free
default and should lead.

**Deferred opt-in:** a time-travel clone of a real target's data via `cloneInstance(id,
atSeq)`'s `MaterializeAtSeq`/era-resolution (KernelHost.cs:826-910). It drags in
non-destructive apply (`SchemaBridge.WriteDocument`, :205-229 — the cloned data must survive
the *edited* schema) — real value, but a later slice, not the first cut.

## Slice plan

- **S3a — on-demand seed-data preview (the thin first cut).** A "Preview" control in the
  design editor → the kernel projects the working-copy design (validates) → mounts a
  throwaway preview instance seeded from `initialData` → the editor shows it in an `<iframe>`.
  Lands the ONE new seam (non-registry-backed mount) + proves no-divergence. **Defers:**
  live-per-edit refresh, real-data cloning, in-session hot patching.
  - *Gherkin shape:* Given a design with a custom `fn render()` + `initialData`, when the
    operator opens Preview, then an iframe shows the design's real rendered UI against its
    seed data; when the design is invalid, Preview shows the projection error, not a broken
    mount.
  - *Mostly-deenv-code:* the only C# is the `MountPreview`/`UnmountPreview` kernel methods
    (+ a `sys.mountPreview` host action riding the existing dispatch + `sys` floor); the
    Preview button, the iframe, and cleanup triggers are designer app code.
- **S3b — boundary-live refresh.** Re-project + `RefreshPreview` on **save/blur/commit
  boundaries** (NOT per-keystroke — per the map's boundary-op rule; per-keystroke re-parse+
  re-validate+rebuild would be wasteful and racy). Latency = remount-per-boundary (~a page
  load); acceptable at boundary granularity.
- **Later:** real-data clone source; in-session hot patch (WS-diff the preview's warm
  session instead of reloading the iframe — needs the "keep server state warm" foundation);
  a mount-from-in-memory-description seam (skip the scratch-file round-trip).

## Lifecycle + leak defenses

- **Create** on preview-open, keyed to the editor's WS **session/clientId** — one preview
  per editing session, `__preview-<clientId>`.
- **Destroy** on: editor close, design switch, and **WS-session teardown** (the durable hook
  — a preview dies with its session).
- **Structurally bounded:** previews are never in kernel.json or `db.instances`, so a leak
  is at worst a scratch dir + an in-memory `_instances` entry — never a phantom registry row
  or `db.instances` ghost. Defenses: scratch dir cleared wholesale on kernel boot; session-
  death unmount; an idle-sweep (a preview with no live editor session for N min is reaped).
  A `__preview-` name prefix that `SpecsFor`/boot NEVER loads → a crashed-kernel restart
  never resurrects a preview.
- **Reserved-name guard:** extend `EnsureNoCollision` to forbid *user* creates/renames into
  `__preview-` (the router resolves by name — a colliding real instance would shadow a
  preview). Preview names must be unspoofable.

## Self-grill (folded in)

1. **Stale preview after edits** — S3a is on-demand by design; the affordance must read
   "Preview (as of last open)" / carry a manual refresh, never imply liveness. S3b closes it
   at boundaries.
2. **Invalid design** — `ProjectDesignDocument` throws pre-mount → a projection error, never
   a broken preview instance. Better than a runtime-error preview. Gherkin covers it.
3. **Preview-instance leaks** — the real risk; mitigated 3 ways + structurally bounded
   (above). Residual: kernel crash mid-session leaves a scratch dir → reaped on next boot
   (same crash-durability class create already accepts).
4. **Warm-session / iframe interaction** — the preview's WS session is SEPARATE (its own
   `/apps/__preview-.../ws`); no cross-contamination with the editor. On S3b refresh the
   preview reloads (its transient view-state resets — fine for a preview; the `seed` param at
   SsrRenderer.cs:106 can reproduce it later if it grates).
5. **Remount-per-edit perf** — a refresh ≈ a page load per boundary; fine at save/blur,
   NOT keystroke (hence boundary). Optimization ladder if janky: mount-from-in-memory-
   description (skip file round-trip), then hot-patch the warm session. Deferred; guard the
   seam so S3a's file-path mount doesn't harden.
6. **No-real-data case** — NOT an edge; `initialData` is the default source, fully served
   day one. (This is why seed-first.)
7. **`db.instances` need?** No — it's the designer's registry mirror; a preview is invisible
   to it by construction. (Previewing the designer-app itself is out of scope — guard like
   Publish's self-target guard, KernelHostActions.cs:140.)

## Rejected

- **B — inline nested render** (the designer renders the target design's `fn render()` inside
  its own page): breaks the interpreter's one-app-scope-per-instance model
  (SsrRenderer.cs:84; `_systemNames`/`_descriptors` from the one `desc`) + the two-modes /
  system-user-separation guards. Running app B's render needs app B's whole ambient state →
  it collapses into a second `HostedInstance`. Interpreter-boundary violation.
- **C2 — point the iframe at the REAL target instance + `setDesign` per edit:** mutates a
  real app's live data/schema (destructive, restarts real sessions; design host can't target
  itself). A preview must never write a real instance.

## Open questions for the build

- **Preview identity key** — `__preview-<clientId>` ties a preview to a WS session; confirm
  clientId is the right key (vs a per-editor token) for the one-per-session + cleanup story.
- **The mount seam as a host action** — `sys.mountPreview(design)` riding the existing
  WsHandler host-action dispatch + `sys` floor keeps S3 "mostly deenv code". Confirm at slice
  time (S3a-1 could land the kernel seam + host action test-first, S3a-2 the designer UI).
- **Foreclosure:** guard `HostedInstance.Start` toward accepting an in-memory
  `InstanceDescription` (not just a path) so the perf ladder stays open.
