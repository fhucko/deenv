# S6 — `foreach`/`if` as structured rows (design)

*2026-07-08. Design pass (grounded + self-grilled) for M12 S6 — control flow becomes rows.
USER DECISION same day: NO new `for … in` keyword — the 2026-06-16 DECISIONS.md entry is
SUPERSEDED (note added there). Today's `foreach` already IS the paren-free declarative keyed
iteration that decision specced (identity-keyed DOM reconciliation ui.ts:484/534, `key=`
reset, membership deps); the decision predated M11 keying. Key-by-position = the one true
gap, DEFERRED until a real case (additively addable as a foreach variant). Companions:
docs/plans/visual-designer.md, docs/plans/canvas-eval.md.*

## Position

S6 = "for/if AS ROWS": **zero grammar/parser/printer/twin-execution change.** MetaNode grows
a `kind` discriminator + control-flow fields; the five row-walk consumers update in lockstep;
the canvas gains DataTemplate mode (template when unevaluated, per-item instantiation when
the eval context can evaluate the collection). Lifts the biggest S1b import refusal on the
road to full language support.

## The row shape (one additive designer-schema migration)

MetaNode += `kind text` ("" = legacy element/leaf discrimination via tag — back-compat with
zero conversion; "for"; "if"), `item text` (loop var), `collection text` (expr source),
`condition text` (expr source), `elseChildren set of MetaNode` (the else branch; `children`
is reused as the for-body / if-then branch). NO MetaFor/MetaIf types: deenv sets are
single-typed — separate types would force a heterogeneous children set. Merge-later note:
`elseChildren` is a SECOND semantic child-order set — S1c must make it conflict-capable too.

## The five consumers (same-slice, per the collector cross-ref comment)

1. Projection `ProjectNode` (SchemaBridge.cs:257) → CodeTagForEach/CodeTagIf branches.
2. Import `ImportNode` + the refusal lift (SchemaBridge.cs:394/:446 — for/if refusal :451
   LIFTS; helper/var refusals STAY — later rungs). Import stays one-time fresh mint.
3. `CollectExprSources` (SchemaBridge.cs:313) — collect `collection` + `condition`, recurse
   `children` AND `elseChildren` (under-collecting = a wrong chip; the easy-to-forget branch
   is elseChildren — test it).
4. Canvas walk ×2 twins (CodeExecutor.cs:769 / codeExec.ts:1087) — DataTemplate mode below.
5. Tree editor `renderNodeEditor` (app.deenv:136) — for/if row editors (+ `+ for`/`+ if` add
   controls via orderForAppend; "add else" button when elseChildren empty; remove = subtree
   GC as in E2).

## Canvas semantics (canvas-never-lies applied to control flow)

- NO ctx: a `for` renders its body ONCE as a marked repeating template (badge: item var +
  collection chip); body leaves referencing the item var chip (unbound — honest). An `if`
  renders BOTH branches, each marked then/else — never guesses.
- WITH ctx: a `for` evaluates `collection` via EvaluateCtxExpr against the seed db and
  renders the body PER ITEM with the item var bound — the ROW SCOPE (EvaluateCtxExpr grows
  an ambient-bindings param layered onto {db}; nested loops = stacked bindings; ctx.exprs
  stays text→AST — parsing is item-independent, N values per shared source come from the N
  bindings, resolving the content-addressing question). An `if` evaluates `condition` →
  the taken branch; ANY eval failure degrades to the no-ctx both-branches mode — never
  chip-guesses a branch.
- Provenance: template + every per-item instance carry the SAME body-row `data-node` (N DOM
  elements : 1 row). Correct for S4 (click any instance → select the template row) — S4
  must NOT assume data-node→element is 1:1.
- Keying in the canvas is a non-issue (display-inert, no state/focus) — reinforcing that
  key-by-position isn't needed here.

## Slices

- **S6a — rows** (this build): the MetaNode migration; projection/import/collector; tree
  editor for/if editing; canvas NO-CTX template mode (both twins + conformance for the
  template shape). Gherkin: import a render with `foreach note in db.notes` + an `if` →
  rows → project back ≡ canonical original; the tree editor edits the loop; the canvas
  shows the marked template.
- **S6b — row-scope eval**: the with-ctx mode (collection iteration + item binding via the
  EvaluateCtxExpr ambient param; if-condition taken-branch + degrade-on-failure); conformance
  for per-item evaluation; Gherkin: two seeded notes → two rendered rows with real titles.

## Defaults picked (user can override)

Key-by-position deferred; if-eval-failure → both branches; "add else" affordance = a button
when empty; loop provenance = shared template row id (flagged to S4).

## Self-grill (folded)

Keyword refutation grounded (4/5 requirements already true of foreach; the 5th is canvas
work either way); residual caveat stated honestly — if the north star ever wants FINER
per-row reactive independence, that's a reactivity-model question, not for-vs-foreach.
Twin surface = canvas walk + EvaluateCtxExpr param only (smaller than any keyword path).
Printer fixpoint untouched. Five-consumer ripple enumerated; the collector invariant test
(every source the canvas evaluates is collected) guards under-collection. kind=""
back-compat = no data conversion.
