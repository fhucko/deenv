# M12 — the remaining work (consolidated spec)

*2026-07-10. Written at the workbench-build-phase close (main `93c27ed`). This gathers
every remaining M12 item — recorded across docs/plans/visual-designer.md,
component-workbench.md, canvas-eval.md, structured-fns.md and the session ledgers — into
one build-ready ladder. Each item: goal, grounded mechanism (anchored in what is LANDED,
not speculation), slices, and the decisions still open. Order below = the recommended
build order; W2 and the hybrid editor are user-gated entry points that can be pulled
whenever wanted.*

**Status audit 2026-07-16** (code + plan recon against main): S4a/b and S5a/b/c **still
DONE** in live code (`instances/1/app.deenv`, `ui.ts`, `SsrRenderer`). Remaining ladder
items (per-use ambients, W2, hybrid editor, independent tracks) are **NOT STARTED**
except chip `task_074f6ccf` (scalar-var reactivity) which **landed**. Do not confuse the
reserved-empty `evalContext.ambients` / `params` payload keys with MetaUse per-use
ambients — those keys are empty placeholders, not this feature. S5c *mechanism* prose
below was written pre–unified-commit; the product ops live (wrap/unwrap via ordinary
commit / set membership), only the old MOVE/two-FIFO story is stale.

## Where M12 stands

Landed: structured rows for the whole ui surface (render tree, for/if, component fns,
params, state vars, configurations), import/projection with the full refusal ledger, the
editable tree editor, the live client-computed canvas with real evaluation (seed db +
shipped ASTs + the real twin; init-evaluated state; loops/branches; component expansion;
call-position eval + the fingerprint staleness banner; the degrade banner; auto-live
expression editing via the parseExprs op), and the component workbench (configurations as
live sandboxed instances: real state, real handlers, Reset, schema/extent/lib seeding —
"airtight at the wire seam"). **Also landed (S4+S5 ladder):** canvas/tree selection
(`selecttarget`, bidirectional highlight, Escape, page reorder types→render→publish→
branches), structure ops (▲/▼ `moveRow`, palette + insert-at-selection via `libNames`/
`ComponentReturnsElement`, wrap/unwrap), plus component-local bare scalar `var`
reactivity (chip `task_074f6ccf`). Guards in force: no-second-engine, canvas-never-lies,
empty-deps eval-ctx memo, printer fixpoint, plus the foreclosure list in
visual-designer.md.

**Next buildable rung:** §3 per-use ambients. W2 remains user-gated; hybrid editor needs
its own design session first.

---

## 1. S4 — selection: the canvas becomes the editor ✅ DONE

**Goal.** Click any element in the canvas (or a workbench card) → the producing row is
selected → its editor scrolls into view / an inspector form opens → edits flow through
the ordinary row mutations the tree editor already makes. This is the "select-to-edit"
item from the original first-scope bar, and the rung S5/S7 build on.

**Grounded mechanism (all spine already landed):**
- Every canvas element carries `data-node={rowId}` — REAL store ids (CANVAS-1), N:1 for
  loop instances (S6a decision: all instances share the template row's id) and for
  expansions (every expanded element carries its OWN body-row id; the invocation row's
  id rides only the inert splice wrapper — the recorded CALLEE-ONLY decision).
- Selection state = a client-only concern → a module `ui var` (the M13 rule: client-only
  collections/state live in module ui vars, not component state).
- A canvas click handler resolves the nearest `data-node` ancestor → sets the selected
  row id; the tree editor's `renderNodeEditor` highlights the row whose `sys.id(node)`
  matches and the canvas outlines the element(s) carrying it (CSS on a `is-selected`
  class both sides — N:1 means possibly several outlined instances, correct).
- The canvas is display-inert for app handlers, so a click listener for selection does
  not conflict; in WORKBENCH cards, instance handlers are live — selection clicks there
  are a later nicety, not v1 (state the boundary).
- "Inspector" v1 = the EXISTING tree-editor row, highlighted + scrolled to
  (`scrollIntoView`-class behavior — decide the deenv-native mechanism: an anchor/focus
  idiom or a small client hook). A separate generic-UI inspector form is NOT v1 — the
  map's S4 note says canvas v1 = the live preview with selection handles, one renderer.

**Slices.**
- **S4a — click→select→highlight. ✅ DONE 2026-07-10** (reviews arch SHIP + ui-arch
  SHIP + ux SHIP-WITH-FIXES, folds applied; `0e8655d`). As specced: the general
  `selecttarget="<uiVarName>"` marker (instancemount family — ui-arch affirmed it
  against both alternatives), one delegated resolver stopping at the container
  boundary, `writeSelectedNode` = the popstate/path write precedent (nonexistent/
  read-only var no-ops; selection SURVIVES refetch via the scalar-divergence guard —
  desirable), canvas chrome = a commitRender post-pass (twins untouched), editor
  highlight = reactive `nodeClass` (O(rows)/click — the noted deliberate asymmetry).
  N:1 + callee-only needed ZERO new plumbing — the landed provenance was already
  right. Folds: cursor+hover affordance scoped to the marker (doubles as the boundary
  signal vs the look-alike previews) + caption clauses (click/clear/N:1); anchor
  clicks inside the container SELECT instead of navigating (W1b containment + a
  listener registration-order fix so preventDefault lands before interceptNavigation's
  defaultPrevented check); both id-invariants CONFIRMED + comment-pinned (sys.id ≥ 1;
  one global per-instance NextId — a stale cross-design selection matches nothing).
  Screenshot-verified; honest caveat for S7: the selection outline is distinct from
  input focus rings but not airtight — wants its own style token at the theming pass.
  v1 boundaries stated: workbench cards + U1 previews unmarked (structural exclusion,
  zero special-case code).
- **S4b — bidirectional selection + the page reorder. ✅ DONE 2026-07-10** (reviews
  ui-arch SHIP + ux SHIP-WITH-FIXES, folds applied; `4d8d8ed`; SUPERSEDED SCOPE: the
  original "surface add/remove at the selection" was dropped as zero-value — S4a's
  scroll-to-row already lands the operator on the row carrying those controls;
  duplicating them = competing controls). Landed: tree-row click selects via an
  ORDINARY deenv handler (`fn selectNode`; the innermost nested row wins NATIVELY —
  wireEvents' stopPropagation, empirically pinned); row clicks deliberately DON'T
  scroll (they route through executeAssignment, never the scroll-arm — pinned; the
  asymmetry ux-affirmed: bring the OTHER surface into view, not the one you're at);
  Escape deselects (generic client chrome, guarded to skip text-entry targets); THE
  PAGE REORDER — types → render (canvas+tree+State+Components) → publish → branches →
  Advanced — closing the twice-recorded UX-checkpoint item (pure markup move; the
  type-hint↔canvas adjacency comes from the section order; State/Components between
  render and publish = intended, authoring precedes shipping); row hover tint
  (adjudicated fold — the faint cousin of the selected tint, no cursor lie,
  :not(.is-selected) so selected dominates). Screenshot-verified: Publish reads as a
  discrete next step; a selected row's tint suffices as confirmation with the canvas
  off-screen. Named honestly (shared with the canvas's own hover, not new): nested
  rows cascade-tint together under the pointer (ancestor boxes contain it
  geometrically); the convert-button → content-up-page jump is real but reads
  reasonable (the operator lands on their new content). Focus is never stolen —
  editing a row's input highlights it (reconciler-verified bonus). One test fix:
  ±1px scroll-assert tolerance (sub-pixel getBoundingClientRect rounding,
  base-vs-branch verified).

**Open decisions.** The scroll/focus mechanism in deenv code (may need one tiny client
hook — flag if so); whether canvas-outline styling needs the theming seam or one chrome
rule (one rule, likely).

**Effort class:** small-medium; almost entirely deenv code; no twin changes expected
(the click resolver is client chrome like the workbench driver).

## 2. S5 — structure ops + palette

**Goal.** Insert a component (lib or design fn) from a palette at/into the selection;
wrap/unwrap; REORDER (the E3 ledger item — the one structural op the tree editor still
lacks).

**Grounded mechanism.** All inserts/moves are ordinary row set-ops (E2 precedent);
reorder = rewriting sibling `order` ints (conflict-capable by design — the S1c note);
the palette = the lib scope + `design.fns` reflected (the foreclosure guard: the library
IS the palette, no registry). Lib component INSERTION is just a tag row with the lib
name — no expansion machinery needed at insert time (the canvas already literal-renders
unknowns and W1c seeds lib for workbench cards).

**Slices.**
- **S5a — reorder. ✅ DONE 2026-07-10** (reviews arch SHIP + ui-arch SHIP + ux
  SHIP-WITH-FIXES, adjudicated fold applied; `0ff81e6`). ▲/▼ per row via ONE
  `moveRow(coll, node, dir)` + isFirst/isLastSibling serving four families (render
  children/elseChildren, node attrs + use-args via the shared attrRow, MetaUse
  configurations); two-int `order` swap, ties = hand-edit-only (the OrderedMembers
  precedent), no renormalization (no statement loop in the language — and a swap needs
  none). Edge buttons DISABLE-IN-PLACE (the adjudicated ux catch: hiding ▼ at the last
  position slid the destructive × under a chase-clicking cursor — an unconfirmed
  subtree delete on overshoot; the onRemove-null hide precedent is STATIC and doesn't
  transfer to a mid-interaction flip; `disabled={bool}` was already a generic idiom in
  both renderers). fns/vars/top-level-roots deliberately SKIPPED (unsorted display
  foreach — a control there would reorder data invisibly; tied to task_d7c6ed6a).
  Same-frame canvas repaint pinned (order was already a dep-recorded read); three-way
  proof (editor/canvas/store + reload). RODE ALONG (the slice's real substance per
  ui-arch): executeAssignment's objectProp branch dropped the WS send for transient-
  negative-id objects (`obj.id > 0` gate) — plain-assignment writes to a just-added row
  silently didn't persist; now routed through persistFieldEdit (the proven two-way-
  binding fallback; FIFO wire + monotonic msgId carry the ordering; blast radius
  honestly ZERO historical loss — two-way binding covered every real path until
  moveRow became the first statement-form caller; also unifies dict-entry statement
  writes — broader, strictly more correct; client-only, C# untouched, no conformance
  owed; the sandbox wire-leak question closed by structural trace — every branch
  terminates in wsHooks?., which the dispatch bracket nulls).
- **S5a ledger (ux find, HIGH-VALUE follow-up):** object FIELD reorder — `type.props`
  order drives the generated form's field order (arguably worth more than node
  reorder); needs the props display foreach sorted first (the same task_d7c6ed6a-family
  precondition check), then the same moveRow helper applies.
- **S5b — palette + insert-at-selection. ✅ DONE 2026-07-11** (reviews arch SHIP +
  ui-arch/ux SHIP-WITH-FIXES; seven-fold + a widening round; `80e410d`-family, three
  commits). The seam: the language has NO dict enumeration, so `ctx.libNames` ships from
  BuildEvalContext (rule-12 shape — data already built, reshaped; zero new Code
  surface), FILTERED BY STRUCTURAL SHAPE: `ComponentReturnsElement` = every reachable
  return path of the relevant body (own body or the nested `fn render()`) yields a
  literal tag (var/fn decs ignored; if-chains + tag ternaries recursed) — reflection
  over the code itself, NO registry/denylist (the foreclosure guard's spirit; 16 lib
  fns list incl. SetTable/ObjectForm/Input/UserMenu; route/render/InputType/boolGlyph
  excluded for provable reasons). The palette: a native `<details>` (the details-open
  reconciler rule covers its post-insert survival — pinned) with This design / Library
  groups + a TARGET CAPTION ("Inserts into <main>") — the ux-praised standout. Insert
  rules: "into" ONLY for PLAIN elements (the chain-nest trap fix — component-call rows
  take sibling insert, so repeated palette clicks build SIBLINGS and nothing vanishes
  into a children-dropping component); a selected `for` inserts INTO its body; `if` =
  sibling (branch ambiguity, honest caption — the false "can't hold children" reason
  killed); nothing-selected → the sole element root's children, a `for` root's
  children, a bare-leaf root = honest no-target caption (the second-root invalid-design
  edge closed BOTH ways — the builder self-caught the element case via live tracing,
  the reviews caught the non-element case); complete-row minting; the new row becomes
  the selection + REVEAL-SCROLLS into view (a `revealSelected` bump var + ~15 client
  lines reusing S4a's scroll consumer — the S4b no-scroll rule stays for row clicks;
  remote inserts are the case it deliberately excluded). RODE ALONG (two pre-existing
  client bugs the palette's insert-select-synchronously shape hit first, arch-affirmed):
  (1) registerRemap now patches writable top-scope int vars holding a just-remapped
  transient id — the collision fear was DEFUSED mechanically (the shared page counter
  is deep-negative before any handler runs; -1/-2 sentinels are never live tempIds;
  comment states the real probabilistic bound, not a false invariant); (2) the
  action-miss REINVOKE now re-arms graceful VNA recovery (a second un-shipped field
  was an uncaught JS error; termination proven — the shipped-field set grows
  monotonically). PLUS a third find in the fold: resolving the insert target inside
  the handler ran evalContext under memoBypass = every insert a guaranteed multi-round
  action-miss; target now resolves at render time into the button closures.
- **S5c — wrap/unwrap. ✅ DONE 2026-07-11.** Product ops live as `wrapNode` / `unwrapNode`
  in `instances/1/app.deenv` (mint wrapper, splice children, renumber, identity-preserving).
  **Mechanism note (audit 2026-07-16):** the original write-up described MOVE + nested
  `{refId}` + a two-FIFO commit window. Post–unified-commit (2026-07-12+), wrap/unwrap
  ride ordinary `commit` / set membership — capability unchanged; do not re-introduce a
  NestedSetLinks specialization. Tests pin identity preservation, rejection without an
  orphan, reload, and canonical projection.

**Open decisions.** None for S5 core. Drag-and-drop is explicitly NOT v1 (buttons/menus
first, DnD is polish). **S5a ledger still open:** object FIELD reorder on `type.props`
(needs props display foreach sorted first — same `task_d7c6ed6a` precondition family).

## 3. Per-use ambients (closes the last preview boundary) ✅ DONE 2026-07-16

**Status: DONE** — plan `docs/plans/2026-07-16-per-use-ambients.md`.

- `MetaUse.ambients set of MetaAttr` + Configurations UI (`.use-args` / `.use-ambients`)
- `SchemaBridge.CollectUseAmbientSources` → `ctx.exprs`
- Workbench `bindUseAmbients` + `useConfigSignature` remount on ambient edit
- Static `expandFn` / `ExpandFn` optional `ambients` on synth use-preview nodes
- Isolation pins kept (unseeded → error); happy path: currentUser fake → `Admin`
- Binds as RO scope vars (not `evalContext.ambients` bag, not language ambient frames)
- `status` skipped as non-fakeable; free-form names otherwise

**Goal.** A configuration can specify its ambients — `currentUser`, `path` — so
components reading them preview instead of erroring; the user's original vision
statement ("configurable self contained ambients and params and fake db") fulfilled.

**Grounded mechanism.** MetaUse grows an `ambients` container (schema decision — the
encapsulate-user-namespace rule says a dedicated shape, e.g. `ambients set of MetaAttr`
reusing the attr row again: name = ambient name, value = expression source). The
workbench sandbox binds them like params (evaluated via the existing machinery, bound
read-only into the sandbox scope root); the static canvas binds them as root bindings
the same way design vars bind (V1b idiom). The retired-scenario pins (currentUser error
cards) flip to evaluated values — the same pin-flip pattern V1b used.

**Open decisions (small, ask at build):** which ambients are legal to fake
(`currentUser`/`path` yes; `status` is save-lifecycle — probably not fakeable
honestly); whether an unset ambient still errors (yes — canvas-never-lies) or defaults.

**Effort class:** small — one schema addition + two binding sites + pin flips.

**Do not confuse with:** language-level `ambient name = value` (data-context / staging
`ctx` — already shipped) or live-app SSR `currentUser` (real system var on real pages).
This rung is **per MetaUse configuration** fakes for designer preview only.

## 4. W2 — the state-changes list ✅ DONE 2026-07-16

**Status: DONE** (W2a+W2b together). WorkbenchInstance carries `history[]` + `cursor`;
after each successful handler, snapshot (locals+db+lastId) appends with forward truncate
+ cap 50; toolbar ‹ › + `i/n`; throw restores the pre-handler snapshot (auto-rollback).
Reset still full dispose. Pin: DesignerLibrary scrub scenario.

**Goal (user's words).** "later even with state changes list that would allow moving
back and forth" — per-instance history of state snapshots, scrub back/forth.

**Grounded mechanism (recorded in component-workbench.md, feasibility verified):**
- After each handler transaction in a card, deep-copy the instance's state (the setup
  closure's object graph) + optionally its db copy — plain object graphs, no registry
  entanglement (`deepCopySeed` is the landed copier; state objects are the same shape).
- History = a per-instance array + cursor on the WorkbenchInstance record (client-only,
  workbench-owned lifecycle like the private cache).
- The card toolbar (driver-owned, W1b precedent) gains ‹ › controls + a position
  indicator; jumping = write the snapshot back into the closure scope (the applySeed
  idiom, codeExec.ts:2334) + invalidate + repaint this instance.
- Moving back then acting = the future forks: TRUNCATE forward history on a new handler
  run (the standard undo model) — recommend truncate v1.
- This also retires the recorded no-rollback divergence: a THROWING handler can restore
  the pre-handler snapshot automatically (the rollback the sandbox couldn't do via the
  journal) — fold that in as the natural bonus.

**Slices.** W2a snapshot-on-handler + ‹ › scrub (state only); W2b db-copy snapshots +
throw-auto-restore. **Open decisions:** snapshot db too by default or state-only
(recommend state+db together — the user's mental model is "the instance's state");
history depth cap (a constant, e.g. 50).

**Effort class:** small-medium — composition of landed pieces (the copier, the toolbar,
applySeed, the bracket).

## 5. The hybrid text/AST editor (+ the client parser) — the big rung

**Status (2026-07-16 audit): NOT STARTED.** No client parser, WASM CodeParse bundle, or
text↔row tether. Landed bridge only: remote `parseExprs` WS op (expression auto-live).
Fn-body call-position values still fingerprint-banner-gate ("Components changed — call
results may be stale. Refresh values.") — that dies with this rung, not a separate build.

**Goal.** The code editor becomes a REAL text editor (real caret/selection/paste/
transient garbage, syntax + semantic coloring, error underlines) whose text is
continuously tethered to the rows — the settled TEXT-FIRST premise (tree-sitter model;
the projected-cells model is rejected — the MPS caret-wall graveyard).

**Settled premises (recorded 2026-07-09):** rows stay stored truth; the text buffer is
the working medium; incremental local reparse tethers spans↔rows (identity by locality);
an unparseable stretch = a first-class TEXT-ISLAND state that re-tethers when it parses;
cross-span damage degrades to island, never blocks typing; full re-import remains only
for pasted foreign blobs. Coloring/underlines fall out of the span↔node tether (token
kind → CSS class; semantic kinds — param vs var vs fn — for free; colors land with the
S7 theming seam). deenv's head start: canonical formatting + no comments = two
projectional pain classes pre-dodged.

**The enabling layer — the client parser (REQUIRED at this rung, not before):**
per-keystroke feedback needs local parsing. Two routes, DECIDE AT ENTRY:
(a) a hand-written TS parser twin + a PARSE-CONFORMANCE suite (every text-form
conformance case pins parseJS(text) ≡ parseC#(text) as AST JSON — hundreds of pins for
free; permanent 3rd lockstep artifact, tax on every grammar change);
(b) the REAL CodeParse compiled to WASM (zero drift by construction; ~1-2MB lazy-loaded
bundle on designer pages only). The WS parseExprs op (landed) is the fallback/bridge
either way.

**Pre-work at entry:** the deep literature pass (tylr papers, Darklang fluid
postmortems) + a dedicated design session (this rung gets the full design-grill
treatment; do NOT slice it from this spec alone).

**Sequencing:** after S4/S5 — it subsumes the old "text pane = re-import boundary" model
and wants selection + spans (S2's row→span emission from the canonical printer becomes
part of this rung's foundation rather than a separate S2 slice).

## 6. Independent tracks (not sequenced, grab when relevant)

Statuses as of 2026-07-16 audit unless noted.

- **S1c — MergeTags** — **NOT STARTED.** `DesignMerger` does types/props lineage + whole
  fn/var/access/initialData by name-keyed **printed text**, not per MetaNode/MetaAttr/
  MetaFn/MetaVar/MetaUse row merge or conflict-capable child order. Needed before two
  branches edit the same structured render.
- **S2 — click-to-source on REAL pages** — **NOT STARTED** (canvas spine only).
  `data-node` + `selecttarget` exist on the design canvas; live app/SSR tag render does
  not stamp provenance. Debugging win; partially subsumed by hybrid-editor spans; keep
  deferred until wanted.
- **S7 — styling/theming seam** — **NOT STARTED.** Single hardcoded `ViewChromeCss` in
  `SsrRenderer`. Own design session first (per-app tokens); inspector can edit class/
  style through S4 with zero new machinery meanwhile.
- **Lib payload shipped once** (W1c note) — **NOT STARTED.** `ctx.lib` + `libNames` still
  embed per `BuildEvalContext` / (design, refreshKey). Move to shared/once when it
  matters.
- **Auto-live for fn bodies** — **NOT STARTED** as a separate product (expression
  auto-live via `parseExprs` **is** landed). Call-position values still banner-gate on
  fn-body edits; dies with the client parser — don't build separately.
- **S5a ledger — type.props field reorder** — **NOT STARTED** (see §2).
- **Extent-removal cascade pin** (W1c cheap-pin ledger); **same-name nested-loop
  shadowing + scalar-collection degrade conformance pins** (S6b ledger); **empty-canvas
  hint + sticky/side-by-side canvas layout** (UX ledger); **validator-message
  user-vocabulary pass** (one deliberate sweep); **`common` canonicalization** (S0
  ledger, one-liner when needed).

## 7. Owned by chips (not this spec)

- **task_074f6ccf — component-local scalar `var` reactivity — ✅ DONE** (`d1f8205` and
  follow-through: both twins + `ScalarVarReactivityTests` + SelfHostedUi browser pin).
- **task_43f1c4e3 — WhenAddField / publish-caption flakes — PARTIAL.** Test-layer
  hardening only (retries, locators, convert-click workarounds in DesignerSteps). No
  product fix proving the intercept/layout root cause is gone; may still flake under
  peak suite load.
- **task_d7c6ed6a — foreach-over-`.orderBy` VNA crash tolerance — NOT STARTED.** Still
  blocks restoring `orderBy` on the vars/fns (and props) display foreach; designer
  features still skip ordered display "pending" this chip.

## Recommended order

~~S4a → S4b (+page reorder) → S5a-c~~ **DONE** → **per-use ambients (NEXT)** →
[W2 whenever the user calls it] → the hybrid-editor design session (+ client-parser
decision) → S1c/S2/S7 as their tracks demand. Every rung keeps the standing process:
worktree per slice, Gherkin first, twin lockstep where evaluation is touched, the
three-review gate for UI slices, interleaved-rate flake judgment, docs + memory synced
at landing.
