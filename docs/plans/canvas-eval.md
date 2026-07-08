# Canvas evaluation — the eval context (design)

*2026-07-08. Design pass (grounded + self-grilled ×8, refutations folded) for the M12 canvas
evaluation ladder — the machinery that makes `sys.renderTree` EVALUATE expressions with the
real interpreter. Companions: docs/plans/visual-designer.md (CANVAS-1 + guards),
docs/plans/s3-live-preview.md (banked lessons + the salvage pointer). Build slice below.*

## Position (one sentence)

A single server-backed read **`sys.evalContext(design[, state])`** ships the two things the
client cannot make itself — a **synthetic `db` graph** (the design's `initialData` seeded
into a throwaway store, read back, re-minted report-style with distinct negative ids +
`Constant`) and a **content-addressed map of parsed expression ASTs** (source text → AST;
there is no TS parser, deliberately) — which `renderTree`'s reserved `ctx` slot consumes to
replace each `.expr-chip` with the REAL interpreter's evaluation over the seed graph,
falling back to the identical chip whenever evaluation isn't faithful.

## The grounded mechanics (anchors)

- **ASTs ship, never parse, client-side:** the app's own render AST already ships via
  `SchemaJson.Options` polymorphic serialization (SsrRenderer.cs:146-158 → window.initUi;
  init.ts:46-54 executes it). `CodeParse.ParseExpression` (CodeParse.cs:210) is pure +
  store-free. Per-expression ASTs ride the memo data channel as serialized JSON strings —
  the SAME wire format the app render trusts, zero new ClientState machinery (code has no
  memo wire form — ClientState.cs:129-131 — hence string-encoded).
- **The synthetic-graph precedent:** publish/merge reports are hand-built ExecObject graphs
  with distinct negative ids + `Constant=true` (PublishReportCode.cs:34-98) — `Constant` is
  what makes ClientState ship the WHOLE node (ClientState.cs:70,101). The seed graph reuses
  the idiom. The re-mint is REQUIRED: memo-result nodes register in the client's GLOBAL
  object registry by id (dt.ts:111-112) — positive ids would collide with live designer data.
- **The fake-db builder (salvage of `3f40bc7`):** project (validates) → Load → throwaway
  initialData-seeded JsonFileInstanceStore (self-seeds: JsonFileInstanceStore.cs:131,139-148)
  → `DbBridge.LoadRoot` (DbBridge.cs:17-80 — the SAME builder that binds live `db`,
  SsrRenderer.cs:324-325) → re-mint negative+Constant. (`git show
  3f40bc7:DeEnv/Http/SsrRenderer.cs` BuildPreviewRenderData ~:1288-1328 for the seeding.)
- **Collections over a synthetic graph WORK** (the verified caveat): the client's collection
  methods operate on the value in hand, never the global registry (codeExec.ts:492-493,
  523-526, 1407-1442, 1545-1561); sets resolve to direct shared object refs at merge
  (dt.ts:53-80). A self-contained graph bound as `db` in an isolated scope evaluates
  .where/.orderBy/.any/.single/foreach faithfully.
- **Execution entry points are pure + isolatable:** ExecuteValue (CodeExecutor.cs:210-235)
  is the single value dispatch; ExecuteTag/ExecuteTagChildren (:1781-1852) evaluate attrs +
  leaves THROUGH ExecuteValue (:1783, :1820). A fresh ExecScope{Parent=libScope,
  Items:{db: seedGraph}} + fresh ExecContext{HandlerIndex=null} runs an assembled tree with
  no scope pollution and no handler wiring (handlers index only when HandlerIndex non-null,
  :1790-1798). Watch-point: TS module-level slotPath (codeExec.ts:316) vs C#
  context.SlotPath — safe under synchronous balanced push/pop; noted for reentrancy.
- **v1 scope needs are tiny:** S1b import REFUSES helpers/ambient vars (SchemaBridge.cs:
  329-332), so structured designs' expressions reference only `db` + literals/operators +
  faithful sys builtins. Builtin partition AS BUILT (review-corrected): FAITHFUL = db
  navigation + collections, arithmetic/logic/ternary, sys.humanize/id/nest/segment/toInt/
  field. CHIP = sys.extent/schema/canWrite/canRead/resolve/new + the preview reads +
  ALL ambients incl. path/status (the eval scope is deliberately PARENT-LESS with only
  `db` — safer than the earlier Parent=libScope sketch; anything unbound chips, never
  guesses). CAVEAT: `sys.id` over the seed graph returns the re-minted synthetic NEGATIVE
  id (twin-stable, but not a production id — a v1 display quirk, not a data lie). Client
  store-backed builtins already throw VNA on miss — try/catch converts to chips for free.

## Invalidation — the deliberate inversion of the S3a race

The eval context is CONSUMED inside renderTree's already-dep-recorded walk, NOT dep-recorded
itself. Key = `evalContext:{designId}:{stateKey}:{refreshKey}`, EMPTY deps — never keyed on
the design subgraph (S3a's auto-live mistake: subgraph-keyed → every optimistic edit forced a
refetch that clobbered optimistic nodes). Consequences:
- Structural/literal edits: same-frame canvas re-render off the SAME shipped context (cache
  hit, no round-trip). The liveness CANVAS-1 proved is untouched.
- An edited non-literal expression: its new text misses the shipped AST map → falls to its
  chip (honest) until an explicit **Refresh** bumps refreshKey (S3a's proven race-free
  mechanism). Same for initialData edits.
- Memo-leak discipline: prune the prior `evalContext:<designId>` generation at the real miss
  for a new refreshKey (the shipped execPreviewRender pruning pattern); the GC sweep reclaims
  the orphaned seed graph.

## The renderTree pivot (no-second-engine, honored precisely)

The walk SURVIVES as orchestration + provenance (`data-node` injection) + PER-NODE error
isolation — because the language has no `try`, a single whole-tree execute cannot stop one
deep failure from blanking the canvas. But the walk evaluates NOTHING itself: every
non-literal leaf/attr is `ctx.exprs[text]` → deserialize → `ExecuteValue(ast, evalScope,
evalCtx)` under a local try/catch — the IDENTICAL dispatch ExecuteTag itself uses. Chip
tiers: (0) literal → value (unchanged); (1) AST + clean eval → the value; (2) AST + throw →
chip (raw source); (3) no AST (edited-unrefreshed) → chip. Tiers 2/3 render today's exact
chip — the fallback is already visually defined and provably never guesses.

## Thin slice — CANVAS-EVAL-1

`sys.evalContext(design[, refreshKey])` (both twins, publishPreview wiring, memo + pruning) +
the fake-db builder + the renderTree leaf/attr pivot + a Refresh control + a conformance case
(populated ctx: db-rooted expr → value; store-backed/unknown → chip; edited-text → chip).
Gherkin: given a structured design whose render has a leaf `{db.settings.single(s =>
true).title}` and initialData seeding one settings row, the canvas shows the seeded title
(not a chip); retyping the leaf falls to a chip until Refresh, then evaluates.

**Honest scoping:** v1's evaluable surface is NARROW — no foreach/row vars (S6) and no params
(uses) yet, so leaf exprs reference `db` directly. The deliverable is the machinery proven
end-to-end on both twins; the surface widens with S6 + uses/params.

## Follow-ups (order)

component expansion (needs schema descriptors seeded into the context memo — the biggest
fidelity jump) → S6 `for … in` + row scope (makes `{note.title}`-class exprs evaluable —
most real renders) → uses/states {name, args, ambient, data, order} via the `state` arg →
params with structured fns (BindParams idiom, CodeExecutor.cs:1868-1922) → auto-live (gated
on refetch-race-safe optimistic mutations — the ledgered precondition) → real-data seed
(access-scoped shipping) → in-memory throwaway store; move BuildEvalContext's concrete-store guard inside its try (uniform degrade-to-chips — review trivial note).

## Self-grill record (all HOLD; refutations folded above)

1 AST-as-JSON-string = the existing wire format riding the data channel, not a second format.
2 seed-graph id collision = real → neutralized by negative+Constant re-mint (required step).
3 deep-eval-error blanking = why the walk survives (per-node try/catch; no `try` in the
language). 4 the S3a race = inverted by (design,state,refreshKey) keying with empty deps.
5 memo leak = the shipped pruning pattern. 6 big seeds = author-controlled representative
data; real-data source ships access-scoped later; cyclic graphs fine (LoadRoot shares via
`loaded`; DtValue ships refs). 7 twin drift = one interpreter both sides + the conformance
case extends to populated ctx; slotPath asymmetry noted. 8 no-second-engine = the walk
delegates every evaluation to ExecuteValue; the forbidden hand-rolled evaluator is not built.
