# Component workbench — live configurations (design)

*2026-07-09. Design pass (grounded via a client-runtime trace + adversarial grill ×1,
verdict SHIP-WITH-FIXES — architecture upheld, all fixes folded below; the grill's core
finding: the isolation bracket must wrap HANDLER DISPATCH, not just the mount) for the
M12 rungs after the structured-fns arc — MetaVar top-level/component state + the
uses/states workbench, merged by the user's stated end goal. Companions:
docs/plans/structured-fns.md (F1–F3, landed), docs/plans/canvas-eval.md,
docs/plans/visual-designer.md.*

## The end goal (user, 2026-07-09)

The designer displays a component with its SPECIFIED CONFIGURATIONS; each displayed
configuration is an INDEPENDENT live instance with a reset button; later, a per-instance
state-changes list allows moving back and forth (time-travel scrubbing).

**The mandate: preview must be THE SAME as live.** Reuse the real client runtime with
ISOLATION — fakes only at the seams (db, wire). Nothing reimplemented: the same
component invocation, the same state lifecycle, the same handler dispatch, the same
reconciler. Preview cannot drift from live because preview IS live, sandboxed. (This is
the state-era restatement of no-second-engine; it also retires chips inside the
workbench — a card either RUNS its component or shows the real error, as a page would.)

## Grounded mechanics (client-runtime trace 2026-07-09 — the isolation is nearly free)

- **Local state writes are ALREADY local-only.** `state.count = state.count + 1` is a
  plain in-memory prop write + `invalidateProp` (codeExec.ts:194-218); the wire mirror
  fires only for `obj.id > 0` (:216) — and component state minted by `executeObject`
  gets a NEGATIVE id. Zero new code for state isolation.
- **`wsHooks == null` is the documented no-backend mode** (codeExec.ts:2535-2537,
  :2587): all wire sends route through one nullable module hook. State/db writes are
  id-gated before it; `sendHostAction` + the ctx-commit bracket call it unconditionally
  but no-op silently on null. Isolation = save/null/restore `wsHooks` around instance
  execution — the exact idiom evalCtxExpr already uses for `memoBypass`/`callDepth`
  (:1500-1512).
- **No handler table to collide with.** Dispatch is a direct closure call (`el.onclick`
  wraps the ExecFunction, ui.ts:633-706); `handlerSlot` is a field on the closure, used
  only for server harvest. Two render passes cannot collide on dispatch.
- **A private memo cache = automatic state isolation.** Component state persists as the
  setup closure inside `memoCache` (module-level, codeExec.ts:309, normally pointed at
  `uiStatic.cache`). A workbench instance rendered against its OWN `Map` under its own
  slot prefix is invisible to `slotState()` shipping (ws.ts:1303), `resetViewState`'s
  page-wide wipe (ui.ts:394-406), and the GC sweep (dt.ts:145-203) — isolated by
  construction; the workbench owns the cleanup (drop the Map on card removal).
- **The isolated-real-execution idiom is shipped and proven**: evalCtxExpr builds a
  parent-less scope, binds a local `db` + ctx.fns callables, runs the REAL interpreter
  under a fresh ExecContext with globals saved/restored (codeExec.ts:1464-1513).
  `executeComponentValue`/`runBody` take (node, scope, context) — nothing assumes the
  app root scope. Invoking a real stateful component this way is composition.
- **State snapshot/restore is feasible**: state is a plain ExecObject graph, no registry
  entanglement (objects function unregistered; dt.ts registration is only for
  server-merge identity-sharing). Deep-copy the setup scope's object trees; restore =
  write back + invalidate (the applySeed mechanism, codeExec.ts:2334) — this is RESET,
  and later each history entry.
- **Genuinely new (the two real pieces)**:
  1. **The per-instance render driver** — the runtime's first second render root. Today
     `renderUi()` re-invokes the whole page render into the hardcoded `#app`
     (ui.ts:9-17, :59), and `wireEvents` calls the global `renderUi()` at three sites
     (:643, :668, :701). The driver: invoke the instance's component directly, reconcile
     against the CARD's container via the existing `updateChildren`, and thread itself as
     the re-render callback through an event-wiring variant.
  2. **N independent mutable seed graphs** — the shipped eval-context db is one immutable
     snapshot; each instance deep-copies it client-side so instance A's db writes never
     bleed into instance B.
- **THE GRILL'S CORE FIX — the isolation bracket wraps HANDLER DISPATCH, not just the
  mount.** Handlers fire from DOM events long after the mount bracket restored the
  globals. The instance event-wiring variant must bracket EVERY dispatch with: wsHooks
  nulled (`sendLogin`/`sendLogout` are NOT id-gated — codeExec.ts:1863/:1872 — a
  login-form component in a card would otherwise REALLY re-bind the page session),
  the private memoCache installed (else instance writes invalidate nothing in the
  instance and scan the page cache), `needsServerData` saved/restored (memoize's VNA
  swallow sets it module-globally — an always-missing store-backed read would otherwise
  arm PERMANENT page-refetch chatter; `vnaSwallows` is delta-read, not reset), and the
  instance driver as the re-render callback.
- **Instance handlers do NOT reuse `runHandlerTransaction` as-is** (ws.ts:345-415 is
  page-entangled five ways): it hardcodes `uiStatic.cache` stale-flag snapshots
  (:355,:411), rolls back via the wire journal (empty under null wsHooks → a THROWING
  instance handler would keep its partial writes where live rolls back — a fidelity
  divergence), calls the global `renderUi()` on abort paths, and its ACTION-MISS path
  (:380-386) would ship a `workbench:<useId>/…` slot the server can never reproduce and
  block the GC sweep awaiting a reply. The sandbox gets its own thin transaction
  wrapper: dispatch with `action: undefined` (a VNA mid-handler → rethrow → the real
  error renders in the card — honest), no journal rollback v1 (STATED divergence:
  a throwing preview handler keeps partial writes; Reset is the recovery; snapshot-
  rollback rides the W2 history machinery naturally), instance re-render on completion.
- **The reconciler treats instance containers as OPAQUE — via `applyNode`.** Grill-
  grounded specifics: the skip lives in applyNode (an early return keeping attributes,
  skipping child reconciliation + wireEvents) keyed on a marker ATTRIBUTE the render
  emits; the container must NOT carry `data-key` (an attribute does not key the
  reconciler — desired-child keys come only from foreach-stamped `child.key`, ui.ts:513
  — a data-key'd live node would be rebuilt every render); container identity comes
  from the enclosing keyed `foreach use in fn.uses` row, and within the row the
  container must be SHAPE-STABLE among unkeyed same-tag siblings (no conditionally-
  rendered sibling divs before it). SSR renders it empty; hydration reconciles
  empty-to-empty; the mount is a client post-pass — no clobber window. Portal-style
  was checked and is NOT safer (updateChildren drops non-reused children; inline cards
  need the skip).
- **The mount hook = the end of `commitRender`** (ui.ts:55-65, the single DOM-commit
  chokepoint for both render paths, module state balanced-empty there — the
  syncBreadcrumbs precedent). Synchronous, IDEMPOTENT (a page render triggered from
  within an instance event re-enters it; existing cards are left alone). The driver
  saves/restores memoCache, slotPath, needsServerData around each instance render;
  instance renders never run interleaved with a page pass.
- **Use-args edits remount via a PAGE-side signature, not mount-pass deps** (grill: deps
  recorded during mount land in the PRIVATE cache, which a page-side arg edit never
  invalidates — the implied mechanism doesn't work). The args rows are already
  dep-recorded by the page render (the card's args editor shows them), so every arg
  edit re-renders the page; the commitRender hook recomputes a per-card args signature
  and remounts that instance on change. Same cadence, no cache cross-talk.
- **Host actions in a sandbox silently never ack** (`hostActionCallbacks` fire only on a
  WS ack, ws.ts:76-79): with wsHooks null the action no-ops, any success callback never
  runs, and `ctx.status` never completes a Save lifecycle (commit acks never arrive) —
  Save chrome is dead in preview. Must be SURFACED, not silent (flag-band-aids): v1 = a
  card-footer note ("host actions and saves are disabled in preview"); a per-card event
  log is the later upgrade.
- Caveat inherited: `sys.evalContext` is designer-store-gated (SsrRenderer.cs:1327) —
  the workbench is a designer facility, consistent with today.

## The v1 fidelity boundary (stated, per the grill — not silent)

- **Store-backed builtins always miss in a fresh private cache** — `sys.schema`/
  `sys.extent`/`sys.new`/`sys.canWrite`/`sys.canRead` revive from the shipped cache and
  throw VNA on miss (codeExec.ts:903-955) → a component using them renders its real
  error in the card. v1 previews components free of store-backed builtins; the natural
  fast-follow (LEDGERED): seed the private cache with `schema:`/`extent:` entries
  computed from the design's own rows — the eval context already holds everything
  needed — which unlocks the generic patterns (SetTable/ObjectForm-class) in cards.
- **Lib components render as empty literal elements** (client-side there is ONE flat
  top scope, init.ts:44-47 — no separable lib scope; ctx.fns carries design fns only;
  parenting the sandbox to the page top scope would leak page vars with split
  invalidation — rejected). Same stated asymmetry F2 already has for the canvas.
  Lib support arrives WITH the store-backed seeding above (lib components are exactly
  the sys.schema-dependent ones).
- **`path`/`currentUser`/ambient reads** → real "Variable not found" error cards until
  per-use ambients land (open question 5 → a later rung).
- **Staging/reactive-props fidelity depends on the copy id-sign decision** (user fork,
  below): NEGATIVE copy ids keep the wire gate as a second safety net but silently
  disable the staging gate (`obj.id > 0`, codeExec.ts:205/:568 — `ctx` form staging
  never stages, Save is dead) and the reactive-props rebind (`objectArgKey` tracks
  positive ids only, :2073-2080 — a sub-component keeps showing object A when the
  parent passes B). FAKE-POSITIVE (out-of-range) copy ids restore both semantics and
  are safe GIVEN the handler-dispatch bracket (the wsHooks null carries the isolation
  the id gate no longer does — and that bracket is mandatory anyway).
  RECOMMENDATION: fake-positive — it is the "preview = live" choice.

## Storage (structural — needs approval)

```
MetaFn  += vars set of MetaVar, uses set of MetaUse
Design  += vars set of MetaVar
MetaVar:  name text, init text, order int
MetaUse:  name text, args set of MetaAttr, order int
```

- **A use IS a stored invocation.** Its `args` reuse the existing `MetaAttr`
  {name, value, order} — arg name = param name, value = an expression source, exactly
  the shape an invocation's attrs already have. No new leaf type; the F2 binding code
  reads it natively; the tree editor's attr editing knowledge transfers 1:1.
- **MetaVar carries `var name = <init>`** — `init` is an expression source (text, like
  every other row expression; its AST ships via the collector/exprs map). Fn-level vars
  = the component's persistent state (the setup/view split); Design-level vars = the
  top-level `ui var`s (the north-star "top level state"; also what the module-var
  client-shipping ledger item wants structured eventually).
- **Import lifts the LAST two refusals**: `ui.Vars` (→ Design.vars) and lambda-returning
  fns (a stateful component's canonical shape imports as vars + the view tree; the exact
  accepted statement shape — `[CodeVarDec*, CodeReturn{lambda → tag tree}]` and/or the
  nested-`fn render()` form — is confirmed against ParseUiSection's real output at build
  time, with anything outside it still refusing whole-import as today). After this, a
  design whose ui is entirely components/helpers/vars imports COMPLETELY.
- Projection: Design.vars → InstanceUi.Vars (printer order: vars, fns, render — already
  canonical); MetaFn.vars project into the fn body before the view return. Same refusal
  style (empty/duplicate names; a var shadowing a fn name refuses — resolution is
  silent-last-wins territory again).
- Merge note: S1c extends to MetaVar/MetaUse (scalar fields + conflict-capable order).

## The workbench (per component card)

- The Components card (F1) gains a **Configurations** area: rows of MetaUse — a name
  ("empty", "long list"), args edited like attrs (param name + value expression), add /
  remove / (reorder later, with E3).
- Per configuration, a **live instance panel**: the real component invoked via the
  REAL runtime in a sandbox = fresh parent-less scope {db: the instance's OWN deep copy
  of the seed graph, the design's fns as callables (ctx.fns), params bound from the
  use's args via the real BindParams}, private memoCache under slot prefix
  `workbench:<useId>`, fresh ExecContext, wsHooks nulled during execution, handlers
  wired to the INSTANCE driver (events re-render this card only). Independence is
  structural: own scope, own cache, own db copy, own DOM container.
- **Reset** (per instance): discard the whole sandbox — private cache entry (state) AND
  the db copy — and re-mount from the shipped context. Predictable "as first rendered".
- **Errors are real**: an instance that throws renders the real error in the card (the
  page-level error surface, not a chip). FG's depth guard already makes runaway
  recursion catchable.
- **Liveness = refresh-gated, honestly**: instances run the ctx-shipped fn ASTs, so a
  body/vars edit shows the F3 staleness banner (same fingerprint machinery — extend the
  fingerprint to cover MetaVar rows) until "Refresh values" re-ships and re-mounts.
  The main canvas keeps same-frame STRUCTURE; the workbench gives REAL BEHAVIOR at
  refresh cadence. (Upgrade path, ledgered: assemble the component AST client-side from
  live rows + the exprs map — orchestration, not parsing — for same-frame workbench
  structure too.)
- **db writes inside an instance mutate its LOCAL graph** (self-contained, per the
  original vision statement); Reset restores the pristine copy. Host actions no-op with
  the card-footer note.
- **Later — the state-changes list** (the user's stated follow-up, design note only):
  after each handler transaction, deep-copy the instance's state (+ db graph) into a
  per-instance history list; render the list on the card; clicking an entry restores
  that snapshot (write back + invalidate — the applySeed idiom). Move back AND forth =
  the list is an array with a cursor, not an undo stack. Snapshots are plain object
  graphs — feasibility grounded above.

## Slices

- **V1 — MetaVar rows.** Storage (MetaFn.vars + Design.vars) + import lift (ui vars +
  stateful components) + projection + refusals + vars editors (per-design and per-fn
  rows: name + init inputs). Round-trip Gherkin incl. a real stateful component
  (Counter). The collector gains var-init sources; the F3 fingerprint gains MetaVar
  fields (all three walks, same slice — the fingerprint law).
- **U1 — MetaUse rows + Configurations editor + STATIC preview.** Storage + the
  args-as-attrs editor + each configuration rendered via the EXISTING F2 expansion —
  concretely (grill fix; NOT zero machinery): the designer synthesizes a transient
  INVOCATION node per use (`{kind:"", tag: fn.name, expr:"", attrs: use.args}` — the
  walk's readers tolerate absent children/kind) and feeds it to `sys.renderTree(node,
  ctx, design.fns)`; PLUS the collector walks MetaUse.args values (else every
  non-literal arg is an exprs-map miss → chips) — a small C#+TS collector change under
  the fingerprint/collector lockstep law, in THIS slice. Conformance-pins the
  synthesized-node walk. Display-inert, chips where honest; proves the uses schema
  before the sandbox exists.
- **W1 — the live instance.** The sandbox driver (private cache, wsHooks save/null/
  restore, per-instance db deep-copy, own DOM container + opaque-container reconciler
  contract, event wiring → instance re-render), real invocation via ctx.fns, Reset.
  Browser-pinned: two configurations of a Counter; click one → only it increments;
  Reset → back to initial; the OTHER instance untouched; a db-writing handler mutates
  only its own card; page re-render does not clobber instances. The big slice —
  split W1a (mount + state init render, no events) / W1b (events + reset) if the build
  wants it.
- **W2 — the state-changes list** (LATER, user-gated): per-instance snapshot history +
  back/forth cursor. Not scheduled by this design; the snapshot mechanics above are its
  design record.

## Defaults picked (user can override)

Uses args as MetaAttr rows; reset = whole sandbox (state + db copy), not state-only;
db writes in preview = local mutation (not read-only); host-action/save no-op surfaced
as a card-footer note (event log later); workbench liveness = refresh-gated behind the
existing staleness banner (same-frame assembly ledgered); preview-handler throw keeps
partial writes v1 (stated; Reset recovers; snapshot-rollback rides W2's history);
v1 fidelity boundary as stated above (store-backed builtins + lib components + ambients
excluded, cache-seeding ledgered as the fast-follow).

## User forks (the ones that are his)

1. **Schema approval** (structural): MetaVar {name, init, order} on MetaFn + Design;
   MetaUse {name, args set of MetaAttr, order} on MetaFn.
2. **Copy id-sign**: fake-positive (RECOMMENDED — full staging/reactive-props fidelity,
   "preview = live"; safe under the mandatory handler bracket) vs negative (belt-and-
   suspenders wire gate, but staging forms and reactive props silently dead in preview).
3. **v1 boundary priority**: ship W1 with the stated boundary, or pull the private-cache
   schema/extent seeding INTO W1 so generic-pattern components (SetTable/ObjectForm
   class) preview from day one (bigger slice).

## Grill record (2026-07-09, verdict SHIP-WITH-FIXES — all folded above)

Core finding: the isolation bracket covered render time; handlers fire at event time —
sendLogin/sendLogout are un-gated (a preview login form would re-bind the real session),
invalidations would target the wrong cache, VNA misses would arm needsServerData and the
action-miss path would ship irreproducible workbench slots to the server. Fixed: the
dispatch-wide bracket + a sandbox transaction wrapper (action: undefined, no journal).
Also: copy id-sign is a REAL semantics fork (staging gate + objectArgKey are id-sign-
gated) — surfaced as user fork 2; store-backed builtins/lib components/ambients = the
stated v1 boundary (cache-seeding ledgered); opaque container = applyNode skip, no
data-key on the container, shape-stable position under the keyed use row (portal
checked, rejected); mount hook = end of commitRender, idempotent; args-edit remount =
page-side signature (mount-pass dep-recording refuted — wrong cache); U1 needs the
synthesized-invocation node + collector/fingerprint extension (zero-machinery claim
refuted); Constant does not survive the wire (copies are plain graphs — deep-copy
HOLDS); MetaAttr reuse HOLDS (E2 remove-cascade + S1c args-as-attrs noted); fingerprint
law comment needs rewording (vars aren't "fields the render walk reads") + a
design-level fingerprint slot is reserved if Design.vars ever bind into instances.

## Open questions (narrowed by the grill)

1. ~~Opaque container~~ ANSWERED (applyNode skip; constraints folded above).
2. The exact canonical stateful-component statement shape import accepts (confirm
   against ParseUiSection on the known-good Counter/designEditor patterns at V1 build).
3. ~~Re-entrancy~~ ANSWERED (commitRender-end hook, idempotent, save/restore set).
4. ~~Args-edit remount~~ ANSWERED (page-side signature).
5. Design.vars binding into instances (local init-evaluated copies) — deferred past W1;
   reserve the design-level fingerprint slot when it lands.
