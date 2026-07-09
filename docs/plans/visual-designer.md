# Visual UI designer — rough map (M12, build later)

*2026-07-06. Triggered by a planning interview with the user. Status: ROUGH MAP ONLY —
direction + sequencing + foreclosure guards, NOT scoped for build. M12 stays deferred
until after the MVP (AGENTS.md, ROADMAP.md "Future work — do not build yet"). The core
architecture was already settled 2026-06-16 (DECISIONS.md "Visual component designer",
~line 1087); this doc grounds it against today's code and cuts it into a slice order.
Same-day user constraint: the designer is built almost entirely IN DEENV CODE (the
self-hosting precedent — M4's schema designer); the framework's contribution is storing
render code structurally + the seams listed in §Server-side inventory.
Reviewed same day: adversarial grill = SHIP-WITH-FIXES (record at bottom, fixes folded
into the body), vision-keeper = ALIGNS-WITH-NOTES (guard additions folded in).*

Companions: DECISIONS.md (the settled decision), ROADMAP.md ~line 370 (M12 future-work
entry), docs/plans/component-library.md (M11 — the palette), docs/plans/generic-ui-defaults.md.

## The one-sentence position

The visual designer is **the designer app editing render code stored as ordinary
structured data** — the tag tree lives as designer rows (like MetaType/MetaProp),
expression/handler leaves stay text, and the canonical `fn render()` text is a
projection (the same authority-inversion move already approved for schema: structure =
truth, printed text = artifact); there is no design-time engine (preview = the real
interpreter in a Stage-2 mini-instance) and no hidden regions (show-all, XAML-style:
declarative nodes get canvas handles, imperative bits are edited as text but still
live-rendered).

## Storage shape (settled direction, user 2026-07-06)

**Structured render code, at canvas granularity — "B-hybrid".** The fork was: (A) text
stays the stored form and the canvas works on transient parse trees via sys builtins, vs
(B) render code stored structurally with text as projection. User leaning = B, with the
framework's role limited to the storage itself. The granularity rule that keeps B small:
**structure exactly what the canvas manipulates, nothing more.**

- Rows: fn, tag node (component or html element), attribute slot, `for … in` loop,
  `if` branch; child ORDER carried on the parent. **Child order is SEMANTIC, not
  cosmetic** (DOM order = layout): the type/prop merge's `order`-never-conflicts
  cosmetic policy (OrderBySpine) must NOT be inherited — UI reorders must be able to
  conflict (grill #2).
- Text scalars on rows: attribute value expressions, handler bodies, `if` conditions —
  the imperative bits the show-all decision already says are edited as text. NO
  expression meta-schema.
- This lands the "structured fns" move the versioning design scheduled "with M12" (fn
  identity for merge) — same milestone, same mechanism (designer-schema migrations the
  versioning machinery absorbs). Nuance: structured FNS (identity-grade code merge) and
  the TAG TREE inside them are two migrations' worth of structure, not one (grill #7).

Why B-hybrid beats A: every canvas interaction becomes ordinary data editing in deenv
app code — selection = row navigation, inspector = generic UI over the row,
insert/reorder/delete = set ops. Genuinely FREE from existing machinery: ctx drafts,
atomic commit, field-level DATA conflicts, and the `origin`/`LineageOf` identity
primitive. **NOT free (grill #1): structural MERGE of the tag tree** — DesignMerge.cs is
hand-specialized per row kind (MergeTypes hardcodes MetaType/MetaProp fields), so tags
need their own MergeTags pass + apply loop + a tree-generalized OrderBySpine: the same
per-type cost type/prop merge already paid, paid again for tags. Still decisively better
than A, where ALL of it (plus drafts/conflicts/identity) is custom transient-AST
plumbing, node identity is reborn on every parse, and versioning sees UI edits as one
opaque text diff.

Execution is unchanged: project structure → canonical text → parse → run (the path
publish already exercises). Direct structural execution = later optimization, not a need.
Hand-written text (and the text pane) enters through IMPORT — parse + identity-match vs
the current rows. **Honest scoping (grill #3): no re-import path exists today**
(DesignerSeed.AdoptInto is a one-time fresh mint, no matching), and tag nodes are
ANONYMOUS (twenty sibling `<div>`s), so re-import matching is a structural TREE-ALIGNMENT
problem, categorically harder than the by-name matching types would use — S1's hardest
sub-problem. Import is a BOUNDARY operation (save/blur), never per-keystroke (grill #4).

## Server-side inventory (everything else is deenv code in the designer app)

1. **Tag-tree meta-schema + projection/import in SchemaBridge** — the "db stores the
   AST" piece; extends the exact pattern types already use (structure ↔ text).
2. **Provenance stamping in the interpreter twins** — printed text is canonical so
   row→span is known at print time; the interpreter stamps span/node identity onto
   rendered DOM so click→row resolves. Cheaper than it sounds (grill #5): `SlotPath` is
   already computed per render in BOTH twins in lockstep and already reaches the DOM as
   `data-key` (foreach) and `handlerSlot` (clicks) — this extends live plumbing, not new
   invention. (Also useful standalone for text-mode apps.)
3. **Preview mini-instance mount** — one host action; forks are ms-cheap in-process
   mounts per the versioning work.
4. **`for … in` grammar + printer + twins** — when that slice comes.

## Settled premises (interview 2026-07-06 + decision 2026-06-16)

- **Audience: both, code-visible.** App author and Stage-2 user see the same tool; anyone
  can drop to code at any time. The visual surface is a convenience, never a cage.
- **Truth: the code itself — stored structurally, projected as text** (§Storage shape).
  No SEPARATE design-data layer, no third UI mode (rejected in interview — a distinct
  layout representation consumed by the generic UI would sit between "fully auto" and
  "fully custom" and violate the two-modes decision). Structured rows ARE the code, the
  way structured types already are; printed `fn render()` is the artifact.
- **Show all — nothing hidden.** The earlier "opaque code islands" framing is DROPPED.
  An arbitrary imperative bit is edited as text but still live-rendered in preview.
- **Live preview = the Stage-2 inner-loop mini-instance**: the real interpreter against
  representative data. No design/runtime divergence — deenv's structural edge over XAML.
- **`for … in` declarative render keyword** (paren-free, desugars to declarative keyed
  iteration) is what makes show-all work for loops: the canvas renders the loop body as a
  repeated template (the XAML `ItemsControl`/`DataTemplate` role). Must express
  key-by-identity (default) vs key-by-position.
- **First-scope bar (interview):** arrange/compose lib components, live preview on real
  data, styling controls, and click-any-element select-to-edit.

## What exists today (grounded 2026-07-06)

- **Printer round-trip: exists, C#-only, canonical-form fixpoint.** CodePrint.cs /
  AppPrint.cs; tests assert `parse(print(desc)) ≡ desc` and print-twice stability
  (AppPrintTests.cs:12-29). NOT byte-preserving of hand-written text — the first
  round-trip normalizes formatting. No comment syntax exists, so nothing is lost.
- **No provenance from rendered output to source.** `CodeTag` (CodeAst.cs:215) and
  `ExecTag` (ExecValues.cs:173) carry zero position metadata. `slotPath`
  (position-in-code) exists but only keys COMPONENT setup/state and handler identity
  (`HandlerKey(slotPath, fn.Id)`, CodeExecutor.cs:1565) — it is a memoization mechanism,
  not a node-to-source map. Click→source is new work.
- **Editing surface today: raw `<textarea>`** over the design's `ui`/`common`/
  `initialData`/`access` text fields, under "Advanced (code)" (instances/1/app.deenv:234-248);
  `types` is already structured data edited by generic forms.
- **Propagation today is blunt:** setDesign/publish → full instance restart
  (KernelHost.cs:1073) → open tabs see `sessionAlive === false` and hard-reload
  (ws.ts:1027). Fine for publish; unusable as an edit-time preview loop.
- **Vocabulary is open by construction:** a tag resolving to an in-scope `fn` is a
  component, anything else is an HTML element (CodeExecutor.cs:1595-1606). The lib scope
  is GenericUi.cs (Input, Field, ObjectForm, RefSelect, SetTable, ConflictBar, …).
  `foreach`/`if` are dedicated tag-child AST forms.
- **Styling: one hardcoded global stylesheet** (`SsrRenderer.ViewChromeCss`,
  SsrRenderer.cs:500) inlined into every page; `class`/`style` are ordinary attributes;
  NO per-app theming seam of any kind.

## The gaps (what is genuinely new)

1. **Structured render-code storage** — the tag-tree meta-schema + SchemaBridge
   projection/import (§Storage shape). The designer-schema migration that carries it is
   the "structured fns" move versioning already scheduled with M12. Hardest sub-problems
   (per the grill): tree-alignment re-import for anonymous nodes + the MergeTags pass.
2. **Provenance spine** — row→span is known at print time (canonical text); the
   interpreter twins stamp span/node identity onto rendered DOM so click→row resolves —
   an extension of the existing twin-synced SlotPath/`data-key`/`handlerSlot` plumbing.
   AST→DOM is NOT 1:1 (components splice/expand, `if` is absorbed): click→row resolution
   across component expansion (call site vs callee body) is an open UX decision.
3. **Live-preview mini-instance** — render an edited design in-designer against
   representative data WITHOUT publish/restart. The big shared infra piece (Stage-2
   inner loop wants it independently of the canvas).
4. **`for … in` keyword** — new grammar + desugaring + twin execution + printability.
5. **Styling seam** — the inspector can edit `class`/`style` attributes with zero new
   machinery, but real theming (per-app palette/spacing) has NO home today; needs its own
   design session before the styling slice.
6. **M11 public component library** — the palette IS the component library; the canvas
   discovers it by reflecting over the lib scope (no separate canvas registry — a
   component is visual-designer-ready because it is an `fn` in scope, nothing more).

(There is NO "AST edit operations" gap: once code is rows, edits are ordinary data
mutations the designer app performs in deenv code — that machinery already exists.)

## Rough slice order (each thin; Gherkin at build time)

- **S0 — canonicalize the `ui` section on projection. ✅ DONE 2026-07-08** (arch review
  SHIP; suite 753). Built as canonicalize-on-PROJECT, not on-save: `ProjectDesignDocument`
  re-prints the `ui` section (parse∘print) instead of passing it verbatim, so the
  commit/publish artifact is byte-stable regardless of render-code formatting — the M13
  diff-stability win, without touching the live textarea save path or adding a `sys`
  builtin. `AppPrint.PrintUi` ↔ `CodeParse.ParseUiSection` (reuses the same
  `Section("ui")+MapUi` the document parser uses). Scoped to `ui` only: `initialData`
  would be dict-reordered by the printer; `common` (also code) is a trivial symmetric
  follow-up, deliberately deferred (ledgered in Open questions).
- **S1a — structured storage + one-way projection. ✅ DONE 2026-07-08** (arch review
  SHIP; suite 756). A Design carries its render as structured rows: `render set of
  MetaNode` (the root lives in a SET — like `types set of MetaType` — so `ReadNode`
  resolves the whole tree recursively, no resolver plumbing, every caller unchanged),
  `MetaNode {tag, expr, attrs set of MetaAttr, children set of MetaNode, order}`,
  `MetaAttr {name, value, order}`. `ProjectDesignDocument` projects the tree to a
  canonical `fn render()` (element → `CodeTag`; leaf → `ParseExpression(expr)`; attr
  value → `ParseExpression`; root wrapped in a render fn; printed via `AppPrint.PrintUi`)
  that runs through the UNCHANGED parse→run pipeline — no interpreter/grammar/conformance
  change. Precedence gate (user-decided): structured render valid ONLY when `ui` text is
  empty (both → throw); >1 root → throw; non-element root → throw. Proven by a `@m12` SSR
  scenario + projection/gate unit tests. Deferred to S1b/S1c below.
  - Known limits ledgered (S1a review): (1) a malformed-but-non-empty leaf/attr
    expression still surfaces as a raw `CodeParseException` (empty is now guarded with a
    designer-facing message) — the authoring slice (S4) should wrap it and point at the
    offending node. (2) Store GC sweeps a transiently-unlinked node, so a tree must be
    built top-down (link parent before child); the authoring/import slices must order
    create-then-link deliberately.
- **S1b — import (existing `ui` text → rows). ✅ DONE 2026-07-08** (arch review
  SHIP-WITH-FIXES, fixes applied; suite 763). `SchemaBridge.ImportRender(store, designId)`
  is the inverse of S1a's projection: parses the design's `ui` `fn render()`, builds the
  MetaNode/MetaAttr tree top-down (link parent before children — the store GC gotcha),
  clears the `ui` text so the S1a gate accepts the structured render. Import then project
  is the IDENTITY on the render (proven by a `@m12` round-trip scenario + walk/handler
  unit tests). One-time FRESH MINT only. Refuses (imports nothing): a `for`/`if` form
  anywhere (no structured shape until S6); a render body that isn't a single `return
  <element>`; and — the review-caught data-loss guard — a `ui` section with `var`s or
  HELPER functions besides `fn render()` (clearing `ui` would silently drop them, so it
  stays as text). Leaf/attr values round-trip via `CodePrint.Value` ↔ `ParseExpression`.
  - ~~Known limit (S1b review): `ImportRender` is NON-ATOMIC~~ **✅ RESOLVED by X1
    2026-07-08** (arch review SHIP-WITH-FIXES, fixes applied; suite 765). `ImportRender`
    is now ONE `CommitBatch` (all creates + links + the `ui` clear, all-or-none), so a
    mid-import crash can no longer brick a design. The enabling change: **`SetLinkByPropMutation
    (OwnerRef, Prop, MemberRef)`** added to the store's `CommitMutation` union (user-approved)
    — the set analog of `RefLinkMutation`, addressing a set by `(owner, prop)` so a child
    links into its just-minted tempId-parent's `children` set within the one batch. Server-side
    vocabulary only (never from a wire commit); twin-free. Its "prop is a set" precondition is
    checked in CommitBatch PRE-validation (the store's all-or-none guard), not mid-apply.
- **X2a — `sys.importRender` host action. ✅ DONE 2026-07-08** (arch review SHIP,
  security-focused; suite 770). Wires `sys.importRender(design)` as a server-only host action
  across the five lockstep sites (HostActionScan recognize, CodeValidator arity `[1,2]`,
  CodeExecutor no-op, codeExec.ts `execImportRender` fires the WS op, KernelHostActions handler
  → `resolveStore()` + `SchemaBridge.ImportRender`). Admin-gated with NO new access rule — the
  designer's `sys * where currentUser.role == "Admin"` covers it (`CanHostAction` is
  action-agnostic, deny-unless-granted, gate-before-run; the sole `Run` call site). Reject
  scenarios (non-admin, anonymous) prove the design is NOT converted with disk-level teeth.
  Twin-lockstep, no conformance case. Note for X2b: the refusal messages become user-facing
  copy when the button lands — ux-review them then.
- **X2b — designer "Convert to structured" button + render view. ✅ DONE 2026-07-08**
  (ui-architecture + ux review SHIP-WITH-FIXES, fixes applied; suite 771). In app.deenv: a
  text-authored design shows the `ui` textarea + a "Convert render to structured" button
  (`onClick sys.importRender(design)`, shown only when `ui` is non-empty); once structured,
  a FIRST-CLASS "Structured render" section (outside the collapsing Advanced disclosure, so a
  convert visibly lands) shows `design.render` via the generic `SetTable`. **This is where the
  S1a→X2a foundation became USABLE end-to-end** (import a text render → edit as data via the
  generic UI). Review-caught fixes applied: the view is genuinely READ-ONLY — added a general
  `readOnly` param to the lib `SetTable` (suppresses the create + per-row Remove controls
  regardless of `sys.canWrite`; the first caller to need it) and pass `readOnly={true}`, because
  the default write controls would corrupt the single-root tree (add a stray 2nd root / delete
  the root). Ledgered as acceptable-interim / out-of-scope: the view is root-row-only, no
  drill-in (the tree editor is a later slice — captioned "read-only preview"); the global banner
  prefix "Change rejected:" mis-frames a convert-decline (shared save-rejection chrome, ui.ts —
  a banner-copy pass, not this slice); no "convert back to text" / no confirm (fine while
  conversion is lossless for the accepted plain-tag subset). *(The read-only SetTable view +
  its `readOnly` param were superseded/deleted by E1 below.)*
- **E1 — editable recursive tree editor. ✅ DONE 2026-07-08** (ui-arch SHIP + ux
  SHIP-WITH-FIXES, fixes applied; suite 772). Replaces X2b's read-only SetTable with an
  EDITABLE tree: a self-recursive `fn renderNodeEditor(node)` renders each MetaNode and
  recurses into `node.children.orderBy(order)` to arbitrary depth (element → editable `tag`
  input + attr name/value inputs + nested children; leaf → editable `expr` input). All scalars
  two-way-bound (ordinary ctx field writes) → editing tag/expr/attr persists + round-trips
  through projection. **LOAD-BEARING RESULT: a self-recursive render component WORKS** (keyed by
  the full ancestor-id chain via the foreach slot path — no collision, no caret-drop since
  object-field writes don't hit the dt.ts scalar-var race) — the green light the S4/S5 canvas
  track depended on. Review fixes: expression-hint placeholders on the expr/attr-value inputs
  (they're Code exprs — a string needs quotes) + a leaf anchor glyph + deleted the now-dead
  `readOnly` SetTable param. Ledgered for E2: (a) NO operator-visible signal when an edit makes
  the render un-projectable (e.g. emptying a `tag` → empty-expr leaf projection refuses); (b) no
  add/remove/reorder of nodes/attrs yet (both covered by the caption). A framework bug found +
  routed around + spawned as a task: `.single()` over a set freshly repopulated by a host-action
  ack drops to a stale client render (E1 uses `foreach` over the one-member root set instead).
  **✅ FIXED 2026-07-08** — root cause was server-only: `single`/`any` recorded only a membership
  *dep* (`RecordMembership`) but never recorded their scanned items as accessed *leaves* the way
  `foreach` does, so a set consumed ONLY by `single`/`any` in output position never harvested its
  membership into the client state and the client replayed the scan over an empty array. Fix:
  a shared `RecordScannedItem` (CodeExecutor.cs) now used by foreach + single + any; regression
  tests in CodeExecutorTests. `.single`/`.any` over a host-action-repopulated set are now safe
  (E1's `foreach` workaround still works and was left as-is).
- **E2 — add/remove nodes + attributes. ✅ DONE 2026-07-08** (ux SHIP-WITH-FIXES, fixes
  applied; suite 773). The tree editor is now STRUCTURALLY editable: each element node has
  `+ element` / `+ text/expr` / `+ attr` controls; each non-root node + each attr has a remove
  `×` (anchored in the node's own tag row via an `onRemove` handler passed down the recursion —
  the ux-review fix; the `×` was floating to the far-right margin detached from its node).
  New members APPEND (`orderForAppend` = max sibling `order` + 1, NOT `order:0` which would sort
  to the front under E1's `orderBy(order)`); add-defaults are projectable (`<div>`, empty-string
  leaf, `class=""`). Root keeps no `×` (single-root invariant); leaves get no add-row. All ops
  are ctx-staged `set.add({...})`/`set.remove` (atomic; GC reclaims removed subtrees). Twin-free
  (existing collection primitives only). Ledgered: subtree-delete has no confirm/undo (consistent
  with the type editor's remove-type/prop; versioning backstops it); a newly-added deep child
  appends last with no scroll-into-view. **Editable tree editor COMPLETE — convert a text render,
  then fully build/prune its structure.** Still deferred: REORDER (E3) + the server-backed
  un-projectable indicator. NEXT toward the WYSIWYG canvas = S3 live-preview (design-first).
- **S1c — MergeTags.** A per-row-kind 3-way merge + apply loop for MetaNode/MetaAttr with
  CONFLICT-CAPABLE child order (grill #1/#2 — do NOT inherit the cosmetic
  `order`-never-conflicts policy). Makes render rows branch/mergeable like types.
- **S2 — provenance + click-to-source (read-only).** Twins stamp node identity onto
  DOM; click an element in a rendered page → the designer navigates to the producing
  row + highlights its span in the text pane. Standalone debugging win.
- **S3a — inline preview (`sys.previewRender`). ✅ built 2026-07-08 → ⛔ REMOVED same day** (arch + ux review
  SHIP-WITH-FIXES, fixes applied; suite 779). The design being edited renders INLINE in the
  designer as regular content — no iframe, no kernel mount (the user's requirement; the
  earlier "mini-instance mount" framing is superseded — full record + the settled two-view
  architecture in docs/plans/s3-live-preview.md). Mechanism: **rendered-tree-AS-DATA + twin
  revival** — the server renders the projected design headlessly over a throwaway
  `initialData`-seeded store, strips handlers, ships the tree as plain `{tag,attrs,children}`
  data (trees have no wire form; data does), and BOTH twins revive data→tags at the
  `sys.previewRender(design[, refreshKey])` call site. Liveness = explicit Refresh (auto-live
  raced the tree editor's optimistic mutations — reproduced, dropped, ledgered; returns once
  optimistic mutations are refetch-race-safe). Preview is display-inert + anonymous-principal,
  fail-soft on invalid designs. *(SUPERSEDED same day: the user ordered the server preview
  REMOVED — "too static if it's rendered on server"; the canvas below is the one preview
  surface, with the future client-side evaluated preview (configurable ambients + params +
  fake db per stored uses) replacing this entirely. Removal slice follows the canvas landing.
  SALVAGE for the eval-context slice: lift the seeding machinery from `3f40bc7`'s
  BuildPreviewRenderData — it IS the fake-db builder.)*
- **CANVAS-1 — `sys.renderTree`, the client-computed live canvas. ✅ DONE 2026-07-08**
  (arch review SHIP-WITH-FIXES, fixes applied + conformance-pinned; suite 787).
  `renderTree(node[, ctx])` turns the MetaNode rows into a live tag tree, computed by BOTH
  twins from row data the client already holds — no server call, no memo, no refetch. The
  walk dep-records every read, so a tree-editor edit re-renders the canvas SAME-FRAME
  (browser-proven: rename tag → canvas flips; add child → appears; no reload). Every element
  + chip carries `data-node={rowId}` (real store ids — S4's click-to-select spine;
  `data-node` is a RESERVED attr name, user attrs can't clobber it). Literal attr/leaf values
  render (strict twin-identical rules: one-complete-string-literal regex; Int32-range ints —
  out-of-range = non-literal, conformance-pinned after review caught C#'s int.Parse overflow
  crash); non-literal exprs = `.expr-chip` placeholders (until the eval-context slice);
  empty nodes = `.is-empty` chips. First tree-returning client-computable builtin: has a
  CONFORMANCE CASE (harness gained a reusable `tag` expect kind + canonical serializer).
  Review also caught + fixed: C# ReadNodeProp swallowed absent props (now throws like TS +
  the standard read). GUARDS live in the contract: the reserved `ctx` second arg = the
  (design, state)-keyed eval context's slot; the hand-walk is a STRUCTURAL INTERIM — the
  evaluation slice pivots to assemble-real-AST + run the twin (no-second-engine guard);
  chips never lie (canvas-never-lies invariant).
- **CANVAS-EVAL-1 — the canvas EVALUATES. ✅ DONE 2026-07-08** (arch review SHIP; suite
  785; design docs/plans/canvas-eval.md). `sys.evalContext(design[, refreshKey])` ships the
  FAKE DB (initialData → throwaway store → LoadRoot → re-mint negative+Constant) + the
  content-addressed text→AST map; renderTree's ctx pivot delegates every non-literal
  leaf/attr to the REAL interpreter's ExecuteValue in an isolated parent-less {db} scope,
  per-node try/catch → value or chip (tiers 0–3; no-ctx byte-identical). The S3a-race
  INVERSION held: empty-deps (design,refreshKey) key — optimistic edits chip honestly, no
  refetch; "Refresh values" re-evaluates; structural edits same-frame, no chip flicker (all
  browser-pinned). Review highlights: the TS memoBypass closed a REAL canvas-lies class
  (lambda memo-key collisions caching wrong values — conformance-pinned); the re-mint
  preserves shared/cyclic structure. Caveats: sys.id shows synthetic negative ids; the eval
  scope is parent-less (path/status/helpers chip — safe). v1 surface = db-rooted exprs;
  widens with S6 + uses/params per the north star.
- **S6b — row-scope evaluation. ✅ DONE 2026-07-08** (arch review SHIP; suite 805; 6
  conformance cases both twins). The canvas EVALUATES `for` collections against the seed
  graph and instantiates the body PER ITEM with the loop var bound (`EvaluateCtxExpr` gained
  an ambient-bindings param; bindings STACK for nested loops — an inner collection can
  reference the outer item); `if` evaluates its condition (with bindings) and renders only
  the taken branch. Instances splice flat as REAL content (no template chrome), each carrying
  the body row's data-node (N:1 per the S6a decision). ANY failure — non-bool condition,
  unevaluable/non-collection result, eval throw — degrades to the S6a template mode
  identically on both twins (never guesses); one item's bad leaf chips THAT leaf only.
  Race-guard browser-pinned: editing the collection falls to the template with the tree
  editor undisturbed until Refresh. Ledgered follow-up pins (review, optional): a same-name
  nested-loop shadowing conformance case (inner shadows outer — last-write-wins by
  construction on both twins; pin it as a contract) + a scalar-collection degrade case.
  Ledger (builder-found, PRE-EXISTING, orthogonal): client-added prop rows' type/cardinality
  selects draw options from module-var arrays that only populate on a server render — the
  module-var client-shipping gap.
- **INTERPRETER FIX (same day, via real use): `&&`/`||` now SHORT-CIRCUIT** (main `a79f19f`;
  3 red-proven conformance pins incl. the `null != null && sys.id(null) == 1` guard idiom).
  Both twins were eagerly evaluating both operands, making every `x != null && f(x)` guard
  decoration — found because a real dev store held design-less instances (a shape no test
  fixture ever minted) and every design page threw "id() expects an object" on refetch.
  Post-hoc review: SHIP (dep-recording argument verified: a skipped right side can't affect
  the result while the left gates it; the left's own deps re-trigger on flip). The deeper
  lesson → the aged-store test-harness task (real-data shapes driven through the refetch path).
- **F1 — structured fns: rows + import + projection + editor. ✅ DONE 2026-07-09**
  (design docs/plans/structured-fns.md, grilled ×1 SHIP-WITH-FIXES all folded; build
  reviews arch + ui-arch + ux, all SHIP-WITH-FIXES, all five fixes applied; suite 818
  effective — the one red is the known CANVAS-EVAL contention flake, green in isolation).
  `MetaFn {name, params text, body set of MetaNode, order}` + `Design.fns` (additive,
  mirrors the language's OWN `InstanceUi(Vars, Functions, Render)` split — render stays
  distinguished); projection assembles Functions+Render with refusals (empty name,
  `"render"` reserved, duplicates, no/multi/for-if body root; fns-require-render gate =
  INTERIM); import lifts the S1b helper-fn refusal for single-`return` top-level fns and
  refuses — pre-batch, all-or-nothing — lambda-returns (stateful components stay text
  until MetaVar; no re-import means a blob would be locked in), `server fn` (MetaFn has
  no serverOnly flag — projecting it back would ship a server fn to the client), and
  duplicate names (a shape that imports must project). Editor: a "Components" area —
  per-fn name input + `(params)` framed comma-text input + the SAME recursive tree editor
  + remove `×` + "+ Component" minting an EMPTY body (upheld over the default-`<div>`
  sketch: the root add-row is discoverable and keeps the element-or-helper duality open;
  root-position add offers only element/text since a for/if body root is a projection
  refusal); inline client-computed name hints (empty/reserved/duplicate) since the commit
  banner is coarse and remote. Builder-found + chipped (task_d7c6ed6a, PRE-EXISTING):
  `foreach` over `.orderBy(...)` in tag position hard-crashes the client when the sort-key
  read hits a VNA race after a host-action refetch — the Components foreach ships unsorted
  until that interpreter tolerance fix lands (projection order unaffected: OrderedObjects
  sorts). NEXT: F2 canvas expansion → FG call-depth guard → F3 call-position eval (per
  structured-fns.md).
- **F2 — canvas expansion of design-component invocations. ✅ DONE 2026-07-09** (arch
  review SHIP-WITH-FIXES, the one fix applied: TS shadow check `in`→own-key — a component
  named like an Object.prototype member would have twin-diverged; suite 824 effective,
  conformance 166 both runners, 4 new cases: param-binding trio, bindings-shadow,
  duplicate tie-break, depth-cap chip). `sys.renderTree(node[, ctx[, fns]])` (arity
  discrete set [1,2,3], three lockstep sites); the walk resolves an element tag against
  the fns rows — bindings SHADOW fns (E1), duplicate names tie-break last-in-`order` —
  and EXPANDS: attrs evaluate under the CALLER's bindings, params bind BY NAME faithful
  to runtime BindParams (missing → null; present-but-unevaluable → UNBOUND → body chips —
  the one designed divergence; extra attrs drop; reserved `key` never binds — builder
  caught that one itself; children dropped as runtime does), body walks with params-ONLY
  bindings (no caller leak), splice = ExecArray w/ invocation-row id (callee-only
  selection, decided), depth cap 32 + 10k node budget → honest component chip.
  Collector walks fns bodies same-slice (invariant test extended). Browser-pinned: the
  canvas shows real expanded `<li>` content and a component-body edit repaints every
  expansion SAME-FRAME. Reviewer-accepted without a case: empty (non-null) fns set is
  logic-proven a no-op; fn-RENAME liveness rests on the shared recordProp mechanism
  (scenario pins body-edit). NEXT: FG interpreter call-depth guard, then F3.
- **FG — interpreter call-depth guard, both twins. ✅ DONE 2026-07-09** (arch review
  SHIP-WITH-FIXES, all three applied; full suite 832/832 CLEAN; conformance 167 both
  runners incl. a named-self-binding recursion case — the harnesses gained a generic
  `"error"` expect kind, message pinned twin-identically). `RunBody`/`runBody` = the ONE
  chokepoint every fn-body invocation funnels through (named calls, component setup+view
  closures, where/orderBy/single/any lambdas — all traced); `CallDepthLimit = 256`,
  "Call depth exceeded 256 — runaway recursion?", a NORMAL catchable
  CodeRuntimeException/Error → SSR renders the error page (browser-pinned), the canvas's
  per-node catch chips it. The F2 expansion walk stays separately bounded by its own
  depth-32/10k budget (correct — row-walk, not fn invocation). Review catches (the
  headroom-masks-divergence class, closed by construction pre-F3): guard-trip frame's
  increment was unbalanced (TS's module-global counter ERODED by 1 per caught runaway —
  legal renders would eventually throw; check moved inside the try/finally, both twins,
  leak-tested on both incl. a TS `__callDepthForTest` accessor); TS evalCtxExpr now
  saves/resets/restores callDepth like memoBypass (the isolated eval gets its own fresh
  budget, matching C#'s fresh ExecContext); the SSR-vs-client zero-point 1-off is
  documented at both entries (not worth a synthetic wrapper). Depth-100 legal recursion
  proven green. UNBLOCKS F3: runaway designer data can no longer kill the kernel —
  the crash-loop class is closed.
- **F3 — call-position evaluation of design fns, both twins. ✅ DONE 2026-07-09** (design
  docs/plans/structured-fns.md "Call-position evaluation (F3)"; arch review SHIP with one
  REAL reachable bug + four nits, all fixed same day; full suite 848/848 CLEAN;
  conformance 176 both runners, 9 new cases). `sys.evalContext`'s payload gains `fns` — a
  name → `{ast, fp}` map: `ast` is
  each design fn REUSED from the F1 projection already run to build `appDoc` (not
  re-projected), serialized the SAME wire format `exprs` uses (CodeFunction IS in the
  ICodeValue union, discriminator "fn"); `fp` is a per-fn CONTENT FINGERPRINT
  (SchemaBridge.FnFingerprints — a name/params/body-tree canonical walk over the RAW store
  rows, NOT a hash — plain separator-joined concatenation, since it's only ever compared
  for equality). `EvaluateCtxExpr`/`evalCtxExpr` deserialize `ctx.fns` on EVERY call
  (matching `exprs`' own no-cache pattern — consistency over a new cache layer) and bind
  each as an `ExecFunction`/`{type:"fn",...}` closure whose `Scope` IS the eval scope
  itself, so ALL fn names are mutually visible before any call runs (self/mutual
  recursion resolves at call time; FG's guard catches a runaway → a normal error → the
  leaf's chip). Bound BEFORE the row `bindings` so a same-named loop var/param SHADOWS a
  same-named fn — mirrors runtime scoping, pinned by a conformance case. A leaf eval
  result that is a TAG (or an array whose items are all tags — a helper/component called
  by plain call syntax, legal at runtime) SPLICES as content — an array riding the leaf
  row's own id (the F2 idiom) — instead of ChildText's usual (empty) non-scalar fallback;
  every OTHER non-scalar result is byte-identical to before (never widened). **F3b
  staleness affordance:** ctx.fns is a snapshot (empty-deps, per CANVAS-EVAL-1's S3a-race
  inversion), so editing a fn's BODY changes no call-site text — the canvas walk
  recomputes the SAME per-fn fingerprint from the LIVE `fns` rows (dep-recorded — same-
  frame) via a TWIN-IDENTICAL `FnFingerprint`/`fnFingerprint` (a PARALLEL walk of
  SchemaBridge's, the RenderExprSources/CollectExprSources law) and, on ANY name/content
  mismatch, splices ONE `div.stale-fns-banner` ahead of the tree (coarse — any-fn-changed
  → one banner — never silent); "Refresh values" recomputes ctx and clears it. Deviation
  (flagged, fixed): the TS conformance harness (`runConformance`) classified EVERY
  top-level `ExecArray` result as `intList` — broke on the new banner/splice cases whose
  root is now an array of tags; fixed to classify by CONTENT (every item an int →
  intList, else → tag via `serializeTree`, which already flattens arrays recursively) —
  a genuine pre-existing harness gap the feature exposed, not a new special case; the C#
  side needed no change (`AssertExpectation` is driven by the case's DECLARED kind, not
  the runtime type). Browser-pinned: a helper called in a leaf evaluates for real
  alongside an UNRELATED F2 component expansion on the same canvas; editing the helper's
  body shows the banner same-frame while the F2 expansion keeps updating live (proving
  the two mechanisms are independently reactive); Refresh clears the banner and updates
  the value together. Ledger: lib fns stay out of `ctx.fns` (design-local only, per
  scope) — lib-fn call-position eval is the same deferred lib-expansion follow-up F2 left.
  **Review fix (the real bug):** F1's "+ Component" mints a MetaFn with `name:""` — the
  NORMAL mid-authoring state, not an error — but the fingerprint comparison keyed it
  under `""` while ctx.fns (which can never ship an unnamed entry — an unnamed fn also
  blocks the WHOLE design's projection) never had that key, so the freshly-minted
  component showed a staleness banner Refresh could NEVER clear (a rebuilt ctx still
  can't ship the unnamed row, so the mismatch persisted forever) — violating the
  affordance's own contract. Fixed by the principled rule, applied SYMMETRICALLY at all
  three comparison sites (SchemaBridge.FnFingerprints, CodeExecutor.FnsStale, codeExec.
  ts fnsStale): an unnamed fn has no call sites, so it cannot make any call result stale
  — skip it. Conformance-pinned (a fns set with an unnamed row alongside a matching named
  row → no banner) + browser-pinned (click "+ Component" → no banner; name it → banner
  appears correctly, a new callable ctx doesn't know yet; Refresh → clears). Plus four
  nits: a cross-ref line at all three fingerprint walks (the fingerprint must cover every
  field the render walk reads, the collector-law pattern); the banner doc-comments said
  "span", code builds a `div` — aligned; an order-tie honesty comment at the exec-side
  `OrderedMembers`/`orderedMembers` (stable-sort-by-order only, unlike SchemaBridge's
  explicit `ThenBy(Id)` — not reachable today, flagged for the future); this map entry's
  counts corrected (114 was the raw JSON case count, not the reported "conformance N"
  convention every other M12 entry uses — the ConformanceTests total).
- **V1 — MetaVar rows: component state + top-level ui vars. ✅ DONE 2026-07-09** (design
  docs/plans/component-workbench.md, grilled; reviews arch SHIP + ui-arch SHIP + ux
  SHIP-WITH-FIXES, batch applied; suite 862 effective; conformance 180 both runners).
  `MetaVar {name, init, order}` on MetaFn (component state) + Design (top-level ui vars);
  import lifts the LAST two refusals — `ui var`s → Design.vars, and stateful components
  via the CONFIRMED canonical shape `[var…*, nested fn render(), return render]` (parsed
  from designEditor + all 12 stateful lib components; the doc's guessed return-lambda
  form exists nowhere) → MetaFn.vars + view-tree body. Import∘project identity pinned;
  empty init = legal bare `var x` (grammar-confirmed — the proposed refusal was wrong);
  a zero-var setup/view fn imports as stateless (acknowledged: behavior-preserving).
  Projection refusals: empty/duplicate var names, var-shadows-fn, fn-level var named
  "render" (LOAD-BEARING — the stateful projection synthesizes a nested `fn render()`;
  ui-arch called it spurious, it isn't). Collector collects inits both levels (invariant
  test extended); fingerprint gains vars in the 3-walk lockstep with EMPTY-VARS
  BYTE-IDENTITY (pre-V1 pins unchanged — deliberate; law comments reworded to "every
  field affecting projected/evaluated behavior"); new `OrderedMembersOptional` twins
  (absent-tolerant, used ONLY for vars — pre-V1 fixtures omit the prop). Editors: per-fn
  "State" rows + a design-level State area (F1/E2 idioms; inline hints incl. the
  ux+arch-converged param-shadow hint via a new `sys.hasParam` builtin, 5-site lockstep
  + 2 conformance cases; hint-rendering browser-asserted). STILL TEXT-ONLY (real gap,
  ledgered): stateful components with nested HELPER fns (ConfirmButton/KebabMenu class)
  — MetaVar has no helper-fn row; a later rung. USER DECISION same day: state vars will
  EVALUATE in the canvas (V1b below) — the "state chips until W1" caption work was
  dropped mid-batch as instantly-obsolete.
- **V1b — init-evaluated state in the static canvas. ✅ DONE 2026-07-09** (arch review
  SHIP-WITH-FIXES, applied same day; suite 870/870 then re-verified green after the fix;
  conformance 187 both runners — 7 new cases net + the V1 pin flipped + the review-fix
  flip). A static canvas can only ever show INITIAL state, so binding each var's init IS
  the truth (what a fresh live instance shows at mount) — chips for state vars were
  unnecessary conservatism. `sys.renderTree(node, ctx, design)` — arg 3 is now the DESIGN
  row (was the bare `fns` set, F2): the walk reads BOTH `design.fns` (unchanged, F2) and
  `design.vars` (new) from it, both via the V1 absent-tolerance precedent
  (`ReadNodePropOptional`/`readNodePropOptional`, a new shared helper
  `OrderedMembersOptional` now sits on top of), so a fixture predating V1b that omits
  either prop still reads as "none" rather than throwing — the ONE app.deenv call site +
  all 9 F2/F3 conformance fixtures passing `fns` as arg 3 directly migrated to wrap it in
  `{fns: [...]}`. `BindVars`/`bindVars` (one shared routine, twin-identical) binds each
  MetaVar row's init value SEQUENTIALLY (each init evaluated via EvaluateCtxExpr under
  {db, ...bindings accumulated so far} — so a later var's init can reference an earlier
  one) at TWO sites: the walk ROOT (`design.vars`, seeding the top-level bindings every
  node inherits — row bindings, a for/if loop var, still SHADOW a same-named design var
  on top) and `ExpandFn`/`expandFn` (a MetaFn's OWN vars, bound AFTER its params). An
  EMPTY init (bare `var x`) binds `ExecNull` directly, no eval needed (matches the
  runtime's `ExecuteVarDec` null-default) — a legitimate null, not an unevaluable miss; a
  non-empty init that misses `ctx.exprs` or throws leaves the var OUT of bindings
  entirely, so its references chip (never guess) while sibling vars/leaves are
  unaffected. `SchemaBridge.RenderExprSources`' existing var-init collection (landed
  inert at V1) is now LOAD-BEARING — its own doc comment updated, since `BindVars` has NO
  literal shortcut (unlike a param's tier-0 `LiteralValue`), so even a plain literal init
  like `0` depends on the collector shipping an AST for it.
  **Review fix (arch, MUST-FIX, canvas-never-lies):** the FIRST landing let a var
  OVERWRITE a same-named param/earlier-var on collision ("last wins") — WRONG: the
  runtime's `ExecuteVarDec` (CodeExecutor.cs / codeExec.ts) THROWS "already exists" for
  exactly this condition (a param and a `var` share ONE function-call scope), so a real
  live instance CRASHES on that component's first mount; projection does NOT refuse the
  var-vs-param case (only var-vs-fn and var-vs-var); the designer's `fnVarNameHint` only
  WARNS ("shadows a parameter", advisory) — so the collision passes every gate and would
  have rendered GREEN in the canvas while being fatal at publish. Fixed: `BindVars`/
  `bindVars` now DEGRADES on collision instead of overwriting — a name already present in
  `bindings` (a param, or an earlier var) is REMOVED (not reassigned) and the routine
  returns `true`; at the walk ROOT this alone achieves the correct per-name "unbound → chip"
  degrade (siblings unaffected); at `ExpandFn`/`expandFn` (fn-level vars) a collision
  aborts the WHOLE expansion — the caller renders the SAME `component-chip` an
  unnamed/bodyless/depth-capped fn already uses (never guess which binding, or the crash,
  the runtime would produce). A `poisoned`/`Set` guard makes a 3-way name collision fully
  inert too (not just the first pair). The design-level-duplicate question the review
  raised was checked against source: `ProjectRenderUi` (SchemaBridge.cs ~378-380) ALREADY
  refuses two `design.vars` sharing a name (a `DesignerSourceTests` case already pinned
  it) — the V1 report's claim was RIGHT, no new refusal needed; the walk-root degrade
  above still matters for the LIVE mid-edit window before that refusal fires (one new
  conformance case). Runtime-visibility check (DefineFunction + `TryFindScope`'s parent-
  chain walk, SsrRenderer.cs:989-997/347-363): a component fn's closure Scope descends
  from the SAME `app` scope top-level `ui var`s bind into, so a REAL runtime component
  body LEXICALLY SEES design-level vars (ordinary parent-scope lookup) — the canvas does
  NOT model this (`ExpandFn`'s isolated eval only ever holds {db, fns, params, own vars}),
  so **a component body referencing a design var chips in the canvas today: a SAFE
  under-approximation (never wrong, sometimes conservative) — candidate follow-up: seed
  design-var bindings into `ExpandFn`'s body bindings.** Conformance: the V1 "state var
  chips" case FLIPPED (evaluates, V1b's premise) then the "var-shadows-param" case FLIPPED
  AGAIN (degrades to a component chip, the review fix); new cases pin a design-level var
  evaluating in a leaf, sequential dependency between two design vars, bare-var→ExecNull
  rendering empty (not a chip), an unevaluable init leaving ONLY that var unbound
  (siblings fine), a for-loop var shadowing a same-named design var, and the two collision
  degrades (fn-level → component chip; root-level mid-edit dup → both definitions unbound).
  Browser-pinned (Designer.feature @m12): an imported design-level var + an INVOKED
  stateful Counter component both show real evaluated content (no chips) on first render;
  editing the var's init text chips its leaf (the leaf's own source never changed, so it
  falls back to the stale binding's absence) until "Refresh values" re-ships `ctx.exprs`
  with the new source and the leaf shows the new value. Real bug found + fixed en route
  (not a V1b defect, but blocked writing its own browser proof): a bare root type with
  ZERO props fails `InstanceDescriptionLoader.Validate` ("Type 'X' has baseType 'object'
  but no props"), degrading `evalContext` to a PERMANENTLY EMPTY payload for that design
  (its memo key has empty deps — only an explicit Refresh recomputes) — every browser
  fixture that exercises real evaluation must give its root type at least one field,
  matching the pattern CANVAS-EVAL-1/F3/S6b already established; a fixture that skips
  real evaluation (checks only DOM structure/hints) can still get away with a bare type
  (ledgered separately, deliberately NOT fixed here — orthogonal — **fixed 2026-07-09, see
  eval-degrade-banner below**). Deferred chip classes
  (user-confirmed trajectory, unchanged): edited-unrefreshed (transient by design; auto-
  live stays ledgered), store-backed builtins (dies with cache seeding, the scheduled
  fast-follow), ambients (dies with per-use ambients), genuine errors (SHOULD chip — the
  per-node error display), component-body references to design vars (see the safe-
  under-approximation note above).
- **eval-degrade-banner — an honest notice when evalContext itself fails to build. ✅ DONE
  2026-07-09** (branch `claude/eval-degrade-banner`; fixes the V1b-ledgered gap above).
  `BuildEvalContext`'s catch arm (an invalid design — e.g. the bare-root-type repro) now
  ships a non-empty `error` prop carrying the REAL exception message (never a paraphrase)
  alongside its empty db/exprs/fns/ambients/params payload; the success path never sets it.
  `ExecuteRenderTree`/`execRenderTree` splice ONE `div.eval-degrade-banner` ahead of the
  tree whenever the shipped ctx carries a non-empty error — the F3b stale-fns-banner idiom
  reused. Decision: when BOTH a degrade AND a live-fns staleness mismatch apply (a
  degraded ctx's empty `ctx.fns` makes any live fns row look stale too — both true
  statements), BOTH banners render, degrade-cause first; conformance-pinned (2 new cases:
  error-only, error+stale-fns-mismatch — every pre-existing populated-ctx case, none of
  which carry `error`, stays byte-identical, proving no regression). The type-card editor
  gets a matching inline hint ("needs at least one field") for a baseType "object" type
  with zero props — the fnNameHint idiom (`typeHint` in app.deenv), so the operator sees
  the cause before ever hitting Convert/the canvas. Browser-pinned (Designer.feature @m12):
  a fieldless "Db" type shows the hint and, after Convert, the canvas's degrade notice with
  the real message; adding a field and clicking Refresh clears both and the canvas
  evaluates normally.
- **UX checkpoint ledger (2026-07-08, composed-page review after CANVAS-1 + the preview
  removal; the canvas↔tree divider must-fix is DONE — one `render-section` grouping):**
  (a) page order splits the authoring pair (types … render) with publish/branches between —
  the one high-value reorder when composition is next touched: types → render → publish →
  branches; (b) canvas-above-tree loses same-frame feedback on DEEP trees (the update scrolls
  off-screen) — the concrete reason the eval-era canvas wants sticky/side-by-side, premature
  while chips-only; (c) an empty-but-tagged root renders a blank canvas card — wants a thin
  empty-state hint; (d) heading-size nit was resolved by the grouping fix.
- **S4 — inspector edits (first write).** Select a node → its row in a generic-UI-grade
  form (attribute slots, expression leaves as text inputs) → ordinary data commit →
  projection + preview update. Canvas v1 = the live preview itself with selection
  handles — NOT a separate canvas widget (keeps one renderer, zero divergence). Almost
  entirely deenv code.
- **S5 — structure ops + palette.** Insert a lib component from the palette, reorder,
  delete, wrap — set ops on rows. Palette = reflection over the lib scope + the app's
  own `fn`s. Deenv code.
- **S6 — `for … in` + template rendering.** New keyword lands (grammar, twins, printer);
  the canvas renders loop bodies as repeated templates with an "edit the template"
  affordance. `foreach` remains valid text; only `for … in` gets canvas handles.
- **S7 — styling.** Inspector edits `class`/`style` day one; the theming seam (per-app
  tokens over ViewChromeCss) is designed as its own session first.

Dependencies: S0 ✅, S1a ✅, and S1b ✅ are the landed foundation; S1c (merge) extends it;
S2 needs S1a's row identity; S3 gates the writes (S4+). M11 (component
library) matures in parallel and feeds S5's palette. Nothing here blocks on versioning
Track C.

## Foreclosure guards (what near-term work must NOT do)

- **Never break the printer fixpoint.** Every grammar addition ships with
  `parse(print(x)) ≡ x` + print-stability tests (already the norm — keep it absolute).
- **If comment syntax is ever proposed, it must be AST-attached** (comments as tree
  nodes the printer emits), or the visual↔text sync engine is dead on arrival. Today's
  "no comments" is load-bearing; changing it is a visual-designer decision, not a
  syntax nicety.
- **Keep `CodeTag`/`ExecTag` reshapeable.** They will grow an id/span field. Don't let
  intervening work assume their shape is closed (wire serialization included).
- **Keep component resolution as plain fn-in-scope.** No component registry, no
  canvas-only metadata requirement. Anything that would make a component need
  registration to appear in a palette forecloses "the library IS the palette".
- **Don't grow app-specific CSS into ViewChromeCss.** Every ad-hoc rule added there is
  debt the theming seam has to absorb later.
- **Child order stays conflict-capable.** If type/prop merge is ever refactored into a
  generic row-merge helper, do NOT bake in the cosmetic `order`-never-conflicts policy —
  tag-children order is semantic and must be able to conflict (grill #2/#8).
- **Keep `SlotPath`/`HandlerKey` deterministic and twin-locked.** They are the provenance
  seam S2 extends; anything making slot paths non-deterministic across re-renders or
  breaking the C#/TS lockstep kills click→row (grill #8).
- **Designer render-code rows are ordinary meta-data behind the storage interface.** No
  special-cased persistence — Track C compaction and the setDesign log-wipe obligation
  must treat them like any other designer rows (vision-keeper).
- **Asset refs stay ordinary attribute values.** The assets design must not invent an
  out-of-band binding the inspector can't reach — an `image` in render code is a normal
  attribute expression (vision-keeper).

## Open questions (deferred to build time)

- `for … in` surface syntax for the key-by-position variant.
- Mini-instance data source: clone of real instance data (interview said "real data")
  vs. synthesized representative data — likely "clone, time-travel machinery already
  knows how", but that's S3's design call.
- Whether S4's handler editing (onClick etc.) is name-pick-only or inline-code — start
  name-pick, the text pane covers the rest (show-all makes this safe to defer).
- Exact row shapes for the tag-tree meta-schema (attribute slots as rows vs a dict;
  where child order lives) — S1's design call, with the granularity rule as the guard.
- Click→row resolution across component expansion: a click inside `<MyComponent>`'s
  rendered subtree lands in the callee's body slot path — should selection resolve to
  the CALL SITE or the callee body (and when)? S2's UX call; resolving to the call site
  needs the crossed component-boundary chain carried too (grill #5).
- Which sections go structured: `ui` fns first; `common`/`initialData`/`access` stay
  text until something needs them structured (YAGNI).
- Follow-up (from S0 review): canonicalize `common` symmetrically on projection — a
  one-line change mirroring `ui` (`common` is code with the same printer fixpoint). Not
  built in S0 (render, not helpers, is the visual designer's concern); do it when a
  `common` diff-stability need actually shows up.

## Grill record (2026-07-06, adversarial pass — verdict SHIP-WITH-FIXES, all folded in)

1. "M13 merge FOR FREE" — **BREAKS as written** → fixed: merge is hand-specialized per
   row kind (DesignMerge.cs); tags need MergeTags + apply loop + tree-generalized
   OrderBySpine. Free = ctx drafts, atomic commit, data conflicts, origin/LineageOf.
2. Child order vs unordered sets — **DOC FIX** → order is semantic; the cosmetic
   `order`-never-conflicts policy must not be inherited by tag children.
3. Import identity-matching for anonymous nodes — **DOC FIX** → no re-import path exists
   (AdoptInto = one-time mint); tag re-import = structural tree alignment, S1's hardest
   sub-problem, not the already-solved by-name move.
4. Import-per-keystroke breaks identity — **REFUTED** → import is a boundary op
   (save/blur); printer fixpoint + no comments make the round-trip lossless. Coupled to #3.
5. Provenance through non-1:1 AST→DOM — **DOC FIX** → feasible + cheaper than stated
   (extends the live twin-synced SlotPath/`data-key`/`handlerSlot` seam), but call-site
   vs callee-body resolution is an open UX decision.
6. Slice order / S1 standalone value — **REFUTED** → order sound; S1 rescoped honestly
   (carries #1+#3).
7. Consistency with versioning's fn-identity plan — **REFUTED (consistent)** → same
   milestone/mechanism; nuance folded: structured fns + tag tree = two migrations' worth.
8. Guard completeness — **DOC FIX** → added: conflict-capable child order,
   deterministic twin-locked SlotPath/HandlerKey.

Vision-keeper (same day): **ALIGNS-WITH-NOTES** — structure-as-truth reconciled with the
2026-06-16 "synced view" framing (the sync engine and no-design-time-engine premises
survive; only which side is stored-canonical moved, the schema authority-inversion
precedent); scope discipline clean; both load-bearing claims verified against source.
Added guards: storage-seam ordinariness, assets as ordinary attribute values; S0
milestone-planner note.
