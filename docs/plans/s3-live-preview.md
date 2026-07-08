# S3 — Live-preview mini-instance (design)

*2026-07-08. Design pass (grounded + self-grilled) for M12 slice S3, the pivot toward the
WYSIWYG canvas. Architecture APPROVED by the user 2026-07-08 (Option A + seed-data-first).
Companion: docs/plans/visual-designer.md (the M12 map; S0–E2 landed — a design's render is
structured MetaNode rows editable via a recursive tree editor). This is direction; the
build is sliced below.*

## ⛔ 2026-07-08 — build-time finding: the `sys.previewRender(design)` inline-splice variant is INFEASIBLE (STOP)

A later S3a build attempt was directed to REPLACE the iframe (Option A, below) with a
server-backed read builtin `sys.previewRender(design)` returning the design's RENDERED
ExecTag TREE, spliced inline in the designer via `{sys.previewRender(design)}` (mirroring
`sys.publishPreview`). **That variant cannot work, at the interpreter/client boundary — it
is not a wiring detail but a structural incompatibility.** Recorded here so it is not
re-attempted:

- **A tree has NO wire form to the client.** `ClientState.Serialize` (ClientState.cs:131)
  EXPLICITLY skips any memo entry whose result is `ExecTag`/`ExecFunction` — "a tag- or
  function-valued result has no wire form; the client recomputes it from the shipped data on
  first render." The DtValue wire union (dt.ts:7) has no tag variant. So a tag-valued
  `sys.previewRender` result, cached like `publishPreview`, is NEVER shipped. This is the
  privacy/design floor, not an oversight: trees are *recomputed on the client*, never
  serialized.
- **The client CANNOT recompute this tree.** Every other `sys.` read returns plain DATA the
  client reuses; a preview is a foreign design's `fn render()` run against a throwaway store
  — the client has NO store, NO Designer, NO kernel, and (Option B, rejected below) cannot
  host a second app scope. So on the client `execPreviewRender` MUST miss the memo → throw
  "Value not available" → return `nothing` → the preview slot renders EMPTY → refetch. The
  refetch re-runs SSR but STILL can't ship the tree (same skip) → the client render misses
  again → **an empty preview + a self-re-arming refetch**, never a painted preview.
- **The SSR-only floor is ALSO wiped.** The designer (instances/1) is a fully-custom
  `fn render()` app; `init.ts:104` runs `renderUi()` on hydration and REPLACES the `#app`
  DOM with the client's own render. So the preview the server splices into the SSR HTML
  survives only until the first client frame, then the client recompute (which misses, above)
  blanks it. There is no stable SSR-only slice here.
- **This is exactly the interpreter-boundary the doc's rejected Option B hit** ("Running app
  B's render needs app B's whole ambient state → it collapses into a second HostedInstance").
  Splicing a foreign design's rendered tree into the designer's OWN client render is Option B
  wearing a `sys.` builtin's clothes.

**Consequence for the direction:** the preview must be ISOLATED from the designer's own
client render — it cannot be inline content. The inline-splice framing is CLOSED. Two feasible
isolated shapes remain, both using an iframe (the isolation boundary):

- **Lightest feasible — `sys.previewRender(design)` → HTML STRING + `<iframe srcdoc={html}>`.**
  A *string* HAS a wire form (unlike a tree), so a read builtin that returns the headless-
  rendered HTML string ships to the client fine and refetches on edit; the designer shows it via
  a passive `srcdoc` iframe. NO kernel mount, NO host action, NO throwaway-instance lifecycle —
  just the builtin + an iframe attribute. Static (non-interactive) preview, updates on edit.
  This recovers almost all the lightness; the only concession is the iframe wrapper. **This is
  the recommended shape if/when S3 is revisited.**
- **Heavier — Option A (throwaway mount + `sys.mountPreview` host action), below.** A live
  *interactive* preview (its own WS). Only worth the machinery if in-preview interactivity is
  genuinely needed — not needed for a WYSIWYG-canvas preview.

## ✅ STATUS: BUILT 2026-07-08 — S3a inline preview via tree-AS-DATA + twin revival

The PAUSE above is SUPERSEDED. S3a shipped as an **INLINE** preview (no iframe, no kernel mount) —
the shape the ⛔ finding said was infeasible — by DISSOLVING all three blockers the finding names.
The finding stays correct about the variant it examined (shipping a rendered ExecTag **tree**); this
slice is its RESOLUTION, not its contradiction. What changed: **the server ships the rendered tree AS
PLAIN DATA, and both twins revive it into a tag tree at the call site.**

**The mechanism (built):**
- A handler-stripped rendered tree is PLAIN DATA: `{tag, attrs:{name→scalar}, children:[same | text]}`.
  Plain nested object/array/text data already HAS a wire form and already ships in memo entries (it is
  how `sys.publishPreview`'s report ships). So the blocker "a tree has no wire form" (ClientState skips
  ExecTag) never triggers — the memo result is a DATA object, not a tag.
- `sys.previewRender(design)` is a server-backed read (like publishPreview/mergePreview), **self-built in
  `SsrRenderer.BuildPreviewRenderData`** (no kernel wiring — a design is a row in the designer's own
  store; the compute needs only the design node + its own `initialData` seed). It projects the design
  (validates), loads it, opens a **throwaway file-backed store that self-seeds from `initialData`** (no
  kernel/registration/WS; temp dir deleted after), runs the design's own render HEADLESSLY at `/` as an
  ANONYMOUS request, STRIPS handlers (function-valued attrs dropped), and returns the tree AS DATA.
- At the `{sys.previewRender(design)}` call site BOTH twins REVIVE the data → a real ExecTag tree and
  RETURN it (`CodeExecutor.RevivePreviewTree` / `codeExec.ts revivePreview`). The interpreter splices a
  returned tree inline; the client reconciler builds DOM from it. **The client never recomputes the
  foreign render — it revives it from shipped data**, so hydration KEEPS the preview instead of blanking
  (the finding's third blocker). The revival is deterministic → both twins paint identical DOM.
- **Refresh — ON DEMAND, not auto-live per edit (build finding).** The preview shows the design as of the
  last (re)compute — a fresh compute on navigation to the editor, and on demand via a **Refresh button**
  (`sys.previewRender(design, previewRefresh)` — a `refreshKey` scalar folded into the memo key; the button
  bumps `previewRefresh`, so the next read keys a fresh entry → miss → refetch → fresh preview). AUTO-LIVE
  per edit was BUILT FIRST (dep-record the whole design subgraph so any edit stales the entry) and it WORKED
  in isolation — but it forced a server refetch on EVERY design edit, and those refetches RACED the
  designer's optimistic tree-editor mutations (add-node / edit-tag were previously pure-client, no
  round-trip; making them refetch destabilized the just-added optimistic nodes). Two attempts to contain it
  (taint the hosting view incomplete; isolate the preview in its own component) each still left a per-edit
  refetch that regressed the tree editor. Per the slice's stop-condition (docs task: "if dep-recording the
  subgraph is hairy … ship explicit refresh + report"), auto-live was DROPPED for an explicit Refresh. The
  preview compute reads raw store, so no dep recording is needed; the memo entry has empty deps. Proven by a
  browser scenario (edit the seed → click Refresh → the inline preview updates). Re-instating auto-live is a
  later, isolated problem (it needs the designer's optimistic-mutation path to be refetch-race-safe first).

**Where it lives:** `sys.previewRender` dispatch + revival (+ the `refreshKey` in the memo key) in
`CodeExecutor.cs` / `codeExec.ts`;
the compute + tree→data conversion + throwaway-store render in `SsrRenderer.cs`
(`BuildPreviewRenderData`, threaded into the render executor like `mergePreview` — self-built, no kernel);
arity in `CodeValidator.cs`; the `<div class="design-preview">{sys.previewRender(design)}</div>` Preview
section + scoped CSS in `instances/1/app.deenv` + the designer stylesheet. Tests: `PreviewRender.feature`
(server compute + revival: structure/seed/handler-strip/error-div/temp-cleanup) + `PreviewRenderBrowser.feature`
(end-to-end inline preview, hydration survival, live update on edit). Security: READ-ONLY over a throwaway
seeded store, headless (no host-action seam) — same trust class as publishPreview's compute; rides the
designer's `sys` floor like other reads.

**Deferred (unchanged direction):** a real-data (time-travel clone) preview source; per-boundary remount
of an interactive preview (Option A, below) if in-preview interactivity is ever needed. The
`srcdoc`/HTML-string isolated shape (recommended by the finding as the lightest FEASIBLE **isolated** shape)
was NOT needed — the tree-as-data mechanism recovered the INLINE shape the user wanted without an iframe.

**2026-07-08 review fixes (architecture + ux, both SHIP-WITH-FIXES) — landed same day:**
- Client memo leak: each Refresh mints a fresh `previewRender:<id>:<refreshKey>` entry; nothing evicted the
  PRIOR generations (only `comp:` keys were dropped on navigation). **First attempt (wrong, caught by
  testing, NOT what shipped):** extending `resetViewState` (ui.ts) to also drop `previewRender:` keys on
  every navigation. This closed the leak but broke navigation itself — the SPA's speculative flash guard
  (`navigateClientSide`/`renderUiSpeculative`, ui.ts) treats ANY incomplete subtree as "hold the WHOLE
  navigation, blank until the refetch replies", and forcing every re-entry into a design editor to MISS
  the preview made every such navigation blank-and-wait for a round trip — a real, deterministic UX
  regression (reproduced via `TheCommit_DetailPageShowsARenameAsARenameInChangesSinceParent`, which failed
  100% of the time in isolation, not a flake — bisected to this hunk by reverting it alone against the
  same test). **What actually shipped:** prune stale generations at the point a NEW key is genuinely about
  to be computed (`execPreviewRender`, codeExec.ts) — right before a real miss for a NEW `refreshKey`, sweep
  and delete any OTHER `previewRender:<designId>[:*]` entry for the SAME design. An ordinary re-render or
  re-navigation with the SAME (unchanged) key stays a plain cache HIT (no eviction, no VNA, instant paint —
  byte-identical to pre-fix navigation behavior); only an actual Refresh click (a genuinely new key) prunes
  the old generation. Bounds the cache to at most one entry per design without touching navigation.
- The caption now states the contract imperatively in one place (anonymous-visitor view, not interactive,
  edits may need Refresh — "may" not "won't", per the semi-liveness below) instead of a weaker "as of the
  last refresh" that didn't warn about the edit→look-down moment.
- `.design-preview { pointer-events: none; }` makes the "not interactive" promise VISIBLE — without it a
  native `<input>` inside the revived tree still accepted keystrokes (looked live, did nothing on submit).
  The Refresh button lives OUTSIDE `.design-preview` (a sibling in `.preview-head`), so it stays clickable.
- `PreviewRenderBrowser.feature`'s refresh scenario now asserts the NEGATIVE case explicitly: after the seed
  edit and BEFORE clicking Refresh, the preview still shows the OLD value — the provable half of the manual
  model (previewRender's memo entry has empty deps, so nothing stales it without a refreshKey bump).

**Ledger — not built, flagged for later:**
- **Semi-liveness is opportunistic, not a race.** Refresh is the ONLY *guaranteed* update path, but the
  preview is an ordinary memo entry: ANY refetch the editor page fires for its own reasons (e.g. a host
  action's reply) re-renders the whole page and, if the previewRender key happens to be a hit, repaints
  from that hit's already-shipped result — so the preview can occasionally look fresher than "last Refresh"
  without another race, because it never triggers ITS OWN refetch (dep-free) and only rides refetches the
  page was already doing. Display-only, harmless; the caption's "may" already covers it.
- **The concrete-store guard leaks storage-engine identity into the render layer.** `SsrRenderer.cs`'s
  `BuildPreviewRenderData` checks `_store is not JsonFileInstanceStore` and hard-constructs a
  `new JsonFileInstanceStore` for the throwaway preview seed — fine today (the only store the render layer
  is ever handed), but it MUST NOT HARDEN: the already-ledgered in-memory-throwaway-store seam (below) is
  what removes this coupling, not a workaround bolted on here.
- **Per-editor-render recompute cost.** The throwaway store does real disk I/O (temp dir create/write/
  delete) on every SSR paint of the editor AND on every Refresh AND on any host-action-triggered refetch
  of the page — never per-keystroke (the preview isn't in that path), but still more than zero. The
  in-memory store + a session-scoped memo (skip the file round-trip entirely for a preview compute) is the
  optimization; deferred, not needed at S3a's scale.
- **No "stale" badge.** The preview cannot self-report "this may be out of date since your last edit"
  without the design-subgraph dep-tracking that auto-live needed and this slice dropped (the race). Rides
  the auto-live follow-up (which needs the tree editor's optimistic-mutation path made refetch-race-safe
  first) — a badge without real dep-tracking would just be another caption, not a signal.
- **Layout.** The Preview section is appended below Structured render / Publish / Branches — the editor page
  is getting long. Side-by-side or sticky layout is the visual-canvas slice's problem to solve, not S3a's.
- **`previewRefresh` is one global `ui var`.** Fine while exactly one design editor is open per session (the
  designer's current shape); the multi-instance IDE future (multiple editors live at once) may want the
  refresh key scoped per-design rather than shared. Open question, not a bug today.

---

*Everything below is the original (pre-finding) Option A design, kept for reference — a heavier
interactive alternative, not what S3a built.*

## ⏸ (superseded) STATUS: PAUSED 2026-07-08

After the inline-splice infeasibility surfaced, the user chose to PAUSE/RETHINK S3 rather than
proceed with either isolated shape. The editable structured-render
designer (S0–E2, on main) is a complete, self-contained milestone. Everything below is the
original (pre-finding) Option A design, kept for reference.

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
