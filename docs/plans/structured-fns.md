# Structured fns + params (design)

*2026-07-08. Design pass (grounded via code-navigator trace + self-grilled + adversarial
grill ×1, verdict SHIP-WITH-FIXES, all six fixes folded in below) for the M12
ladder rung after S6: component/helper FUNCTIONS become designer rows, and the canvas
EXPANDS design-component invocations with params bound. Companions:
docs/plans/visual-designer.md (the map + guards), docs/plans/canvas-eval.md (the eval
context this extends), docs/plans/forif-rows.md (the row-migration pattern this follows).
This also lands the "structured fns" move the versioning design scheduled with M12
(fn identity for merge).*

## Position

Structured fns = "fns AS ROWS + the canvas expands them": **zero grammar/parser/printer
change** (named fns are block-bodied `fn Name(params)` + `return` — the exact single-return
shape render import already handles; arrow-form named fns don't exist, confirmed
CodeParse.cs:235-244). A new `MetaFn` row type mirrors the language's OWN ui shape —
`InstanceUi(Vars, Functions, Render)` (InstanceDescription.cs:60-63) distinguishes render
from functions, so the design does too: `render` stays as-is, `fns` is additive. Lifts the
S1b helper-fn import refusal; `var`s stay refused (the MetaVar rung).

## Grounded mechanics (navigator trace 2026-07-08)

- **Component vs element = runtime SCOPE LOOKUP, not capitalization** — `<NoteCard/>` and
  `<div/>` are the identical `CodeTag`; `TryResolveComponent` walks the scope chain and
  falls back to a plain element (CodeExecutor.cs:2150-2156 / codeExec.ts:1980-1984). So
  the canvas resolving a tag by name against the fns ROWS is the faithful analog — a
  design fn shadows an HTML tag name exactly as the runtime would.
- **BindParams** (CodeExecutor.cs:2216-2226; the canvas-eval.md `1868-1922` anchor is
  stale): attrs evaluate in the caller's scope, collected BY NAME; each declared param
  takes its matching attr or **ExecNull**; extra attrs are DROPPED; `key` is a reserved
  slot-identity directive, not a param.
- **Children are NOT supported on component tags** — `ExecuteComponentValue` never reads
  `tag.Children` (CodeExecutor.cs:2172-2203); nested markup is silently dropped. The
  canvas mirrors runtime by dropping them too — showing them would be the lie.
- **Setup/view split is a RUNTIME check**, not an AST shape: a stateful component's body
  `return c => <div>…` returns a closure that persists under a second memo key
  (CodeExecutor.cs:2158-2214). For import that means: a stateful component is still a
  single-`return` fn — the discriminator is the returned VALUE being a lambda.
- **Printer canonical order: vars, functions (list order), render last** (AppPrint.cs:8-10,
  :50-71); fn declaration order carries no resolution semantics for UNIQUE names (one-pass
  DefineFunction, SsrRenderer.cs:989-997) — but with DUPLICATE names, last-wins silently
  everywhere (DefineFunction :992, validator Declare :327, MapUi :416-417 all overwrite)
  — which is why projection must refuse duplicates (grill S3, folded below).
- **A user ui fn with a lib component's name REPLACES the lib member** (not lexical
  shadowing): GenericUi.Effective merges library-then-ui (GenericUi.cs:744-746) and
  SsrRenderer.cs:351 routes it INTO the lib scope — the observable "the design fn wins"
  is the same, so the canvas's fns-row-wins prediction is faithful.
- **Today's canvas** renders a component-named row as a literal DOM element — no
  resolution, no binding (BuildRenderTree, CodeExecutor.cs:780-818). Lib components
  (SetTable, RefSelect, …) are ordinary fns in the lib scope parsed from
  `GenericUi.StdlibSource` — no rows exist for them (the lib-expansion follow-up).

## The row shape (one additive designer-schema migration)

```
Design += fns set of MetaFn
MetaFn: name text, params text, body set of MetaNode, order int
```

- `params` = **comma-separated text** (the enum-`values` precedent: no per-param identity;
  params bind BY NAME at invocations and a rename is a call-site text edit everywhere
  anyway, so param identity buys no merge precision worth a MetaParam type).
- `body` = a single-root `set of MetaNode`, exactly like `Design.render` — same recursive
  ReadNode resolution, same tree editor, same single-root invariant. The root MAY be a
  LEAF row (a helper fn returning a scalar expression, e.g. `propRowClass(prop)`'s
  ternary) — only RENDER's root must be an element (a page); a fn's root just must
  project (non-empty tag or non-empty expr).
- `render` does NOT migrate into `fns`: the language itself distinguishes them
  (InstanceUi.Render is a singleton field), so keeping them separate is mirroring the
  general primitive, not special-casing one.
- Merge note: S1c's MergeTags must extend to MetaFn (name/params scalar fields + the
  body tree + conflict-capable `order` — same policy as render rows).

## The consumers (the S6a five, plus the editor section)

1. **Projection** (SchemaBridge.ProjectRenderUi → grows into ProjectUi): assemble
   `InstanceUi(Functions: [each MetaFn in order], Render: render)`; each MetaFn →
   `CodeFunction{Name, Params: split(params), Body: [return ProjectNode(root)]}`.
   Designer-facing refusals: empty fn name; empty body; >1 body root; **name `"render"`**
   (grill S2 — MapUi routes any fn named render into the discarded Render slot, so the
   component would silently vanish from the projected document); **duplicate fn names**
   (grill S3 — every resolution site is silent last-wins, and S1c set-union merge will
   ROUTINELY produce duplicates, so this refusal is load-bearing for merge, like type/prop
   name uniqueness). Gate updates: `fns` non-empty requires a structured render root
   (INTERIM, not law — `InstanceUi` legally allows Functions with no Render, and S5's
   palette may want a generic-UI app + component fns; revisit then); the both-present
   (`ui` text + structured) refusal already fires.
2. **Import** (SchemaBridge.ImportRender): LIFT the Functions refusal — each ui fn whose
   body is a single `return` imports to a MetaFn (name, joined params, body root via the
   existing ImportNode; order = list order). REFUSE the whole import (unchanged all-or-
   nothing law) when ANY fn: has statements besides the single return, or returns a
   LAMBDA (a stateful setup/view component — importing it would collapse the component
   into one opaque leaf blob, and with no re-import path the blob is locked in; it stays
   text until the MetaVar rung imports it properly), **or is `ServerOnly`** (grill S1 —
   MetaFn carries no serverOnly flag, so a `server fn` would project back as a plain fn
   and be SHIPPED TO THE CLIENT: a silent security downgrade of exactly the class the
   ServerOnly marker exists to prevent; it stays text until MetaFn carries the flag),
   **or duplicates another fn's name** (F1 arch review — MapUi appends without dedup, so
   without this the dup would IMPORT, clear the text, and then fail PROJECTION: a shape
   that imports must project, and the source is already gone). `var` refusal stays. Lambda detection is a complete pre-batch pattern:
   `Statements is [CodeReturn { Value: CodeFunction }]` (return→lambda parses to exactly
   that, CodeParse.cs:219-220/:287-294). An INDIRECT closure (`return makeCounter()`)
   slips through as a leaf-root helper — no data loss (text round-trips), the canvas
   chips it: honest degrade, accepted.
3. **CollectExprSources**: walk every `fns` body too (their leaf/attr/collection/condition
   sources need ASTs for expansion). Changed in the SAME slice as the walk (F2), per the
   collector cross-ref law.
4. **Canvas walk ×2 twins**: expansion (below).
5. **Tree editor** (app.deenv): a "Components" area after the render tree — foreach fn in
   `design.fns`: name input + params input (comma-separated, one field) + its body tree
   via the existing recursive `renderNodeEditor` + remove `×` (subtree GC) + a
   "+ Component" add (mints `MetaFn{name:"", params:"", order:append}` with an EMPTY
   body — SUPERSEDES the earlier default-`<div>` sketch, upheld by both F1 UI reviews:
   the empty card shows the tree editor's own root add-row (discoverable, not a strand),
   and since a body root has no remove `×`, a defaulted `<div>` could never become a
   HELPER (expression-leaf root) — the empty state is what keeps the element-or-expr
   duality open. The root-position add-row offers ONLY `+ element`/`+ text/expr`
   (a for/if body root is a projection refusal, so offering it would strand). Inline
   client-computed name hints on the card cover the static refusal causes — empty,
   `"render"`, duplicate — since the commit-time banner is coarse and remote).
6. **sys.renderTree arity**: `sys.renderTree(node[, ctx[, fns]])` — the designer passes
   `design.fns`. The fns ride as a THIRD ARG (live rows), NOT inside ctx: ctx is
   refresh-gated, rows are client-live — passing rows is what makes editing a component
   body repaint every expansion SAME-FRAME. Validator arity = the discrete SET
   `[1, 2, 3]` (grill T2 — the validator's arity value enumerates allowed counts, it is
   NOT a range; `[1,3]` would break every shipped two-arg call), kept in lockstep at the
   three sites (CodeValidator.cs:84, CodeExecutor.cs:762, codeExec.ts:1069).

## Canvas expansion semantics (F2 — runtime-faithful, canvas-never-lies)

At an element row whose tag matches a `fns` row by name (dep-recorded name reads — a
rename re-renders; a design fn shadows an HTML tag AND replaces a lib component,
both runtime-faithful). **Resolution order pins two grill fixes:**

- **Walk-local bindings SHADOW the fns rows** (grill E1): runtime resolution stops at the
  first scope binding and a non-function binding makes the tag a plain element
  (TryResolveComponent :2150-2156) — so a loop var or param named like a design fn must
  suppress expansion (`for note in db.notes` + `fn note(…)` + a `<note/>` body row is an
  ELEMENT at runtime; expanding it would be a canvas lie). One bindings-dict check before
  the fns match, conformance-pinned. The dual — a function-VALUED param invoked by tag
  (a render prop) — has no rows to walk and falls to the literal-element no-match arm,
  same honesty class as lib components.
- **Duplicate-name tie-break = last-in-`order`** (mirrors DefineFunction's last-wins) for
  the mid-edit window before projection's duplicate refusal fires — pinned so the twins
  can't diverge.

Then:

- **Bind params from attrs BY NAME, faithful to BindParams**: attr evaluates via the
  existing `EvaluateCtxExpr` under the CALLER's current bindings (literal attrs bind
  even with NO ctx — tier-0); a param with NO matching attr binds **ExecNull** (runtime
  behavior, not a lie); a param whose attr is PRESENT but unevaluable (handler lambda,
  eval throw, map miss) is left **UNBOUND** so body references chip — the one deliberate
  divergence from runtime, because binding a guessed value would lie. Extra attrs drop;
  `key` is ignored (display-inert canvas); event attrs are by nature unevaluable.
- **Walk the fn's body rows with bindings = the params ONLY** — the caller's loop-var
  bindings do NOT leak in (runtime scoping: a component body sees its params, not caller
  locals; a body leaf referencing a caller var chips, correctly).
- **Children on the invocation row are dropped** (runtime drops them — mirroring).
- **Splice**: the body walk result rides an ExecArray carrying the INVOCATION row's id
  (inert — the BuildFor idiom); every expanded element keeps its OWN body-row
  `data-node`, so S4 clicking inside an expansion selects the component's row (editable
  in the Components area). data-node stays N:1-capable (already law). **Stated decision
  (grill E5): this makes canvas selection CALLEE-ONLY — the invocation row itself has no
  clickable canvas representation (its id rides only the inert array). S4 either accepts
  callee-only or adds a call-site affordance then; decided here, not by omission.**
- Asymmetry stated (grill E3): a NO-match component-ish tag (lib/text fn) renders its
  row children literally (the CANVAS-1 element arm) where the runtime would drop them —
  pre-existing behavior, now visibly inconsistent next to expansions that drop children;
  acceptable until lib expansion lands.
- **Depth cap 32 AND a total expanded-node budget** (grill E4: a cap bounds depth, not
  WORK — a recursive component looping seed data is N^depth and both twins would hang
  before depth 32; the budget, ~10k expanded nodes per walk, bounds the whole canvas).
  Either limit → the component renders as a component CHIP (name badge, honest degrade).
  Still allows genuinely recursive components over finite seed data (the tree editor
  itself is the proof case). Twin-identical, both conformance-pinned.
- **No match** (lib component, text-mode fn, unknown): literal element exactly as today.
  Ledger: lib/text components could later expand via ctx-shipped fn ASTs assembled into a
  real `CodeTag` + `ExecuteValue` — full runtime fidelity (BindParams/setup-view) at
  refresh-gated liveness, the right trade for fns that have NO rows to walk. For DESIGN
  fns the row-walk wins: same-frame body edits, per-leaf error isolation.

## Call-position evaluation (F3)

Helper calls inside expressions (`propRowClass(prop)` in an attr, `{fmtDate(n.at)}` in a
leaf) evaluate by the eval context gaining a `fns` prop: the server projects each MetaFn
to its CodeFunction and ships it serialized (SchemaJson, the exact AST wire format the
exprs map already uses); `EvaluateCtxExpr` binds each into the eval scope as a callable
ExecFunction whose captured scope IS the eval scope (all names bound before any eval →
mutual recursion works — serialization verified: CodeFunction IS in the ICodeValue
polymorphic union, CodeAst.cs:29, discriminator "fn"; MemoBypass short-circuits the
id=0 memo paths). A call result that is a TAG (a component called in expression
position — legal at runtime) splices as content instead of `ChildText`.

**Edited-fn-body staleness (grill F3b — stated, with an affordance, per flag-band-aids):**
editing a fn BODY changes no call-site text, so `{fmtDate(n.at)}` still hits its exprs-map
entry and evaluates against the STALE ctx-shipped fn AST — a confidently rendered wrong
value — while an F2 EXPANSION of the same fn (live rows) updates same-frame beside it.
The client cannot detect the drift (no TS printer). F3 must ship a VISIBLE affordance,
not silence: the eval context also ships each fn's source-text fingerprint (the
content-addressing idiom); the walk compares it against a fingerprint computed from the
live rows... which needs a client-side printer — NOT available. So the honest v1
affordance is coarser: any edit to a `fns` row (dep-recorded reads the walk already
makes) sets a client-computed "components changed — call values may be stale, Refresh"
banner state on the canvas (the walk knows the rows changed; it cannot know WHICH call
values drifted). Coarse but never silent; per-call precision arrives when re-import/
row-hashing exists.

**Recursion (grill F3c — the guard lands FIRST):** a runaway-recursive helper evaluated
during the C# canvas walk is an UNCATCHABLE StackOverflowException. The earlier "exact
parity with runtime" claim UNDERSTATED the blast radius: under F3 the runaway fn is
DESIGNER DATA evaluated while rendering the designer itself — kernel dies, restarts, and
the next load of that design page kills it again, a crash LOOP in the tool that would fix
the data. It is also twin-divergent (TS catches RangeError → chip; C# dies), so
conformance cannot even pin the case. Therefore **FG below precedes F3**: an
interpreter-level call-depth guard in both twins (a counted CallFunction depth, threshold
well above any real app, throw a normal interpreter error at the limit — catchable,
chips in the canvas, benefits runtime SSR identically).

## Slices

- **F1 — rows + import + projection + editor.** The MetaFn migration (additive);
  ProjectUi assembling Functions+Render; import lifting the fn refusal (lambda-return +
  var refusals stay); the Components editor area. Gherkin: import a `ui` with
  `fn render()` + a helper fn + a component fn → rows; project back ≡ canonical original
  (round-trip identity); the editor renames a param and the projection carries it.
- **F2 — canvas expansion.** The walk ×2 twins (resolve tag → fns row, bind, expand,
  depth cap, chip degrade) + CollectExprSources over fns bodies (same slice — the
  collector law) + renderTree arity [1,3] + the designer passes `design.fns` +
  conformance case (an invocation with a bound param evaluating; a missing param =
  null; an unevaluable attr → body chips; depth cap). Gherkin: a design with
  `fn NoteCard(note)` returning `<li>{note.title}</li>` and a render looping
  `<NoteCard note={n}/>` over two seeded notes → the canvas shows two real `<li>` titles.
- **FG — interpreter call-depth guard** (precedes F3, per grill F3c): both twins count
  CallFunction depth and throw a normal (catchable) interpreter error past the limit;
  a conformance case pins the twins at the threshold. Small, benefits runtime SSR.
- **F3 — call-position eval.** ctx ships projected fn ASTs; EvaluateCtxExpr binds them
  ×2 twins; tag-result splice; the stale-call-values banner affordance (F3b above);
  conformance (helper call evaluates; recursive helper over finite data evaluates;
  runaway recursion chips via FG; unknown call chips).

## Defaults picked (user can override)

`fns` as the prop/type naming (holds both components and scalar helpers); params as
comma-separated text (split/trim/drop-empties, the enum-values behavior); import refuses
lambda-returns + ServerOnly; expansion depth cap 32 + ~10k node budget; F1→F2→FG→F3
order; canvas selection callee-only (E5, S4 revisits); lib-component expansion deferred
(ledgered mechanism above).

## Self-grill (folded)

1. **params text vs MetaParam rows** — by-name binding + rename-is-textual-anyway means
   param identity buys nothing; enum-values precedent; additively upgradable if merge
   ever wants it. HOLD.
2. **fns separate from render** — mirrors InstanceUi's own shape; the LANGUAGE
   distinguishes them, so this is following the general primitive. HOLD.
3. **Row-walk vs assembled-AST expansion** — row-walk keeps same-frame liveness +
   per-leaf isolation and covers exactly what rows can express; assembled-AST is
   reserved for row-less fns (lib) where there's no liveness to lose. No second engine:
   the walk evaluates nothing new (attrs via EvaluateCtxExpr, body via the existing
   walk); the by-name/null-default binding mirrors BindParams (anchor kept: 2216) —
   a semantic twin-of-a-twin kept faithful by the conformance case. HOLD.
4. **ExecNull for missing vs unbound for unevaluable** — missing-param-null IS runtime
   truth; unevaluable-attr-null would be a canvas lie. The split is principled. HOLD.
5. **Caller bindings don't leak into the body** — runtime scoping, and the chip on a
   caller-var reference is the honest signal the fn doesn't close over the caller. HOLD.
6. **Recursion** — F2 tag recursion bounded by the pinned depth cap + node budget; F3
   call recursion resolved by FG-first (the grill refuted the bare parity claim:
   designer-self-render crash loop + twin divergence). RESOLVED.
7. **Import lambda-return refusal** — a fresh-mint-only world must not mint shapes the
   MetaVar rung will want restructured. HOLD.
8. **Children silently dropped on expansion** — runtime parity; an editor hint for
   children-under-a-component-tag is a cheap later nicety. HOLD.

## Adversarial grill record (2026-07-08, verdict SHIP-WITH-FIXES — all folded above)

REFUTED + fixed: S1 ServerOnly import downgrade; S2 fn-named-render silent vanish; S3
duplicate names (merge makes them routine — refusal + pinned tie-break); E1 bindings must
shadow fns rows; T2 arity is a discrete set `[1,2,3]`; F3b edited-fn-body staleness needs
a visible affordance; F3c depth guard lands first (blast radius + twin divergence).
RISKs stated: E4 node budget added; E5 callee-only selection decided explicitly; E3
no-match children asymmetry; Y2 fns-require-render gate is interim. Grounding: every
cited anchor verified against both twins; G2/G3 nuances (duplicate last-wins, lib
"replaces" not "shadows") folded into the mechanics section.
