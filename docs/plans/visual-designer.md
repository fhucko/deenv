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
- **S3a — inline preview (`sys.previewRender`). ✅ DONE 2026-07-08** (arch + ux review
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
  fail-soft on invalid designs. **The settled destination is TWO VIEWS: this truth view
  (real render, evaluated) + the client-only CANVAS (`sys.renderTree` over the MetaNode rows
  — instant, provenance-stamped, the surface S4 turns into the editor), with evaluation
  arriving in the canvas via shipped eval contexts (parsed ASTs + seed graph) and stored
  per-component STATES ("main uses") as the workbench — recorded in the S3 doc + memory.**
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
