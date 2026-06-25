# Client data layer — render-as-planner (spec, DRAFT 2026-06-25)

**Successor to [data-context-refactor.md](data-context-refactor.md)** — which landed the `ctx`
overlay and deferred the "render-as-planner fetch," with the note *"keep the transfer/fetch seam
clean so it re-layers later."* **This is that re-layer.** Sculpted with the user 2026-06-25; vetted by
vision-keeper (verdict below). Status: **SPEC settled 2026-06-25 — not built (build after M-auth).**

## The problem

Refetch is **URL-keyed**. `maybeRefetch` ([ws.ts](../../DeEnv/Instance/ws.ts)) sends
`location.pathname`; the server re-renders THAT url (`HandleRefetch` → `RenderState`). But a
client-only component — `<UserAdmin>` behind `if state.managing`
([GenericUi.cs](../../DeEnv/Instance/GenericUi.cs)) — carries open-state the server never sees, so the
server renders a **different (smaller) footprint** than the client's actual view. Structural privacy
then correctly ships only what the *server* render touched → the component's data (`db.users`) never
ships → it renders empty forever. **Data loading is keyed on the URL, not on what the view demands.**

## The principle (user, 2026-06-25)

> The server needs to know **exactly** the client's state to respond properly.

The **view is the query**: run the app render over the client's *actual* state, harvest its dep
footprint (structural privacy already does this — [ClientState.cs](../../DeEnv/Code/ClientState.cs)),
ship exactly that. URL becomes a projection of view-state, not its key. **v1 may be slow and dumb**
(ship the whole state, reproduce the whole render every time) — optimize later.

## Scope / framing (vision-keeper, 2026-06-25 — *aligned-with-conditions*)

This is the **client transfer/runtime layer** (M11 altitude, the continuation of `ctx`) — **NOT** the
server-side render-coupled storage engine (VISION pillar 5, deferred: the trust floor, undesignable
until later). The render-coupled DB is the **destination this moves toward**; this spec must not
authorize building the engine, a fetch DSL, or a server warm graph. **Its own milestone, the next
substantial one after M-auth (DONE 2026-06-25)**; the empty `<UserAdmin>` popup is the documented
trigger. M-auth's small follow-ups (deploy env-var, remove-user, inline role-edit) are independent
ROADMAP "Near-future" items that can precede or interleave — they don't gate this.

## Invariants (must hold across every slice)

- **I1 — seam.** The server-side fetch reads through `IInstanceStore` in model terms (paths/nodes).
  `DbBridge.LoadRoot`/`LoadExtent` already do; **no side-channel read path** around the store.
- **I2 — no server warm graph.** The server reproduces a *render* per request over (fresh canonical
  load + the shipped view-state) and discards it after harvesting. No per-client server graph — the
  thing M9 deleted, which goes stale across sessions and would also collapse structural privacy.
- **I3 — shipped state is transient + floor-gated.** The intent ships whatever client state the render
  reads — **including drafts/overlays when a data-load depends on them** (a draft can drive a query). The
  server treats ALL shipped state as **transient per-request input: reproduce → harvest → discard** (never
  persisted, never held across requests — that's I2). A draft-driven read still passes the **access floor**
  (I1), so a crafted draft can never widen what the principal may read. (Supersedes the earlier
  "view-state, not draft data" — drafts CAN ship; "draft vs non-draft" was never a clean axis.)
- **I4 — single-client.** No cross-session / conflict / live-propagation awareness (Stage 3, deferred).
- **I5 — apply-iff-current.** An async fetch reply is merged only if the client-state *generation* it
  was computed under is still current; else it is discarded and re-fetched (see *Async — the in-flight
  window*). Generalizes the existing login/logout epoch guard — the one rule that makes the round-trip
  safe.

## Intent = (action, state)

The client→server unit is **(action, state)**, where **action is a fn id** — *"the top-level fn that
leads to a server data fetch"* (user, 2026-06-25), the fn the server replays over `state` to reproduce
the client's computation. **Fn ids are already a ready substrate:** `CodeIds.Assign`
([CodeIds.cs](../../DeEnv/Code/CodeIds.cs)) gives every `CodeFunction` — app + common + lib + the
synthesized render + where/orderBy lambdas — a deterministic, **twin-stable** id that rides in
`initUi`, so client and server resolve the same id to the same fn (the memo cache already depends on
this). Nothing to build here.

- **v1 (dumb):** the action is **whichever fn triggered the fetch** — usually the render fn (a render
  miss), but a **button-click handler** when a click reads unloaded data. The server runs the fn the
  intent names over the full state. "Dumb" = ship the whole state + reproduce the whole thing, **NOT
  "renders only"** — a click that needs data is core, not a follow-on (see *Atomic, isolated eval*).
- **General (the path to not-dumb):** any top-level fn is addressable by id, so a later version replays
  just the *specific* fn that led to the fetch (over its slot-state only — minimal footprint), and the
  SAME `(fn-id, state)` shape carries a **mutation** too (the action = a handler fn the server
  *applies*) — unifying the render-intent with the existing mutation journal. This is why the spec
  names the unit (action, state) now — even v1 varies the action (render fn vs click handler).

## v1 mechanism — "the server reproduces the exact render" (slice 1)

1. **Client ships full view-state.** Extend the refetch payload with component UI-state, **keyed by
   render-slot**, alongside the existing top-scope `vars`.
   - **Ship-rule (extends `sessionVars`):** scalars by value; **in-store objects (positive id) by ref**
     (the server resolves them against its canonical load); **transient objects (negative id) by VALUE**
     — the full object, which the server reconstructs as a transient and discards after harvesting. The
     clean axis is *does the server already have this object* (= id sign), NOT "is it a draft": a draft is
     just one kind of transient, so it ships by value when the render reads it — no draft classification.
   - **Source:** walk the `comp:<slot>` memo entries whose result is a render closure; serialize the
     setup-scope locals (the component's `var state …`) by the ship-rule.
2. **Server seeds + reproduces.** `RenderState` threads the `slot → state` map in.
   `ExecuteComponentValue` ([CodeExecutor.cs](../../DeEnv/Code/CodeExecutor.cs)), after running a
   component's setup, overwrites the seeded locals for its `slotKey` (before invoking the view). So
   `<UserMenu>`'s slot is seeded `managing:true` → its `if state.managing` renders `<UserAdmin>` →
   reads `db.users` → harvests it (structural privacy) → ships.
   - **Top-down composition (the subtle part that makes it work):** a component's slot path is its
     *static* tree position (AST child indices + foreach row-identity), independent of its own state —
     so the server reaches `<UserMenu>`'s slot, applies the seed, and the seed's effect (rendering the
     popup) only changes what's *below*, never the slots themselves. Seeds compose top-down; no
     chicken-and-egg.
3. **Client merges + re-renders.** `mergeState` is additive; the demanded data lands; the popup paints.
   **Correct by construction:** server render == client render → harvested footprint == demanded
   footprint.

**Twin + conformance:** seeding is deterministic and twin-shared → a conformance case ("a component
with seed `{x}` renders the seeded view," both twins). Slot paths are already twin-identical (the M11
slot mechanism); the server reproduces the same tree → the same slots → the seed lands.

**Dumb-on-purpose in v1:** full state every refetch, whole-render reproduction, no diffing, no GC —
all deferred to "later improvements."

## Async — the in-flight window (the intent round-trip is asynchronous)

Client state moves while the reply is in flight. One mechanism covers all three questions — a **state
generation** that generalizes the existing login/logout epoch guard (`sessionEpoch`/`inFlightEpoch`,
ws.ts) from "the session changed" to "the reply is stale."

**The generation** bumps on the changes that would make an in-flight reply *wrong*: a **data mutation**
(optimistic edit/add/remove — its value could be clobbered) and a **session change** (login/logout —
different principal; the existing epoch folds in). The intent stamps `inFlightGen`; the reply carries it
back. (A pure view-state toggle does NOT bump — its stale reply is harmless same-principal data, absorbed
by the additive merge + the trailing re-fetch; nav rides `mergeState`'s existing `path`-skip. A draft edit
that **drives a query** can stale an in-flight draft-driven reply, so **v1 conservatively bumps the gen on
ANY draft edit made while a fetch is in flight** — over-bump = an extra re-fetch (the dumb tax),
under-bump = silent wrong data (the unsafe direction). Narrow to demand-feeding-only later.)

**Q1 — before the reply: non-blocking, best-effort.** The view renders over the data already held;
not-yet-shipped data reads as empty (the swallowed-VNA `nothing`) — a loading placeholder is a later
nicety. Interactions continue. Fetches stay **single-in-flight** (existing `refetchInFlight`); a demand
raised during one is coalesced and re-fired on its reply.

**Q2 — after the reply, generation current (happy path): the reply is DATABASE DATA ONLY; everything
else is untouched; the computation finishes.** (User, 2026-06-25.) The merge applies ONLY the canonical,
floor-gated database data the fn was missing — it never writes client-owned state:
- *component / UI-state* — never shipped back (the intent ships it TO the server; the reply is data
  only), so it's untouched in the slot-keyed memo;
- *drafts / `ctx` overlays* — negative-id, already skipped by `mergeState`;
- *the pending journal* — un-acked optimistic mutations still stand (Q3's ordering protects them).
With the missing data now present, the render **finishes**: a re-render where memo reuse means only the
computation that depended on the just-loaded data recomputes — effectively the suspended read resuming to
completion, not a fresh whole-page render. (Bonus: the transitional cleanup that drops `incomplete`
`comp:` entries becomes mostly moot — the server reproduced the exact state, so the shipped data is
complete and nothing is flagged incomplete. A simplification the refactor *enables*.)

**Q3 — state mutated while fetching: stale reply → discard + re-fetch.** If `inFlightGen !== currentGen`
the reply was computed over a superseded state → **v1 dumb: discard it whole, re-fetch under the current
state** (today's epoch guard, now also fired by data mutations). This dissolves the **optimistic-clobber**
hazard for free: a data mutation during the fetch bumps the generation, so the pre-mutation reply (which
would revert the value) is discarded; the WS is FIFO-ordered, so the re-fetch is processed *after* the
mutation and its reply reflects it — the merge never clobbers an optimistic edit. **Self-terminating:**
when state settles, the last fetch's generation matches and merges. (Continuous rapid mutation
re-fetches — the dumb tax; the *fine* version merges only the still-valid data + re-applies the journal,
re-fetching just the delta — later.)

## Atomic, isolated eval (the sync-interpreter constraint)

The Code interpreter is **synchronous** and must stay so — a fn cannot `await` a fetch mid-run, and the
twin C#/TS **conformance compares deterministic synchronous execution**, so async lives only in the
client orchestration *around* the fn (ws.ts/ui.ts — no twin), never inside the interpreter. A fn that
needs un-shipped data is therefore **abandoned and re-run**, not paused (a missing read throws VNA). That
is safe only if abandoning leaves no partial trace — trivial for one kind, a transaction for the other:

- **Render = pure.** Abandoning a partial render discards a throwaway view; nothing leaked. Re-run on the
  reply; memo dedupes. (The async window above.)
- **Action = effectful → run it in a `ctx` transaction (the isolation), so abandon = atomic rollback.** A
  handler stages all its writes in a `ctx` (data-context-refactor slice 3) and **commits only on clean
  completion** — nothing is sent mid-run. So a missing read mid-action throws → the `ctx` is **discarded**
  (atomic: zero partial effects, nothing sent) → fetch → **re-run the action in a fresh `ctx`** over the
  now-present data → commit. The action runs exactly once *for real* (abandoned attempts are effect-free),
  atomically, in isolation. **This is why the unit is `(action, state)`:** the recorded action fn id +
  state IS the re-run target — the same handle that re-renders a render re-runs an action.

**Immutability is the unifier (user, 2026-06-25).** Eval is over **immutable data — writes are a
discardable `ctx` overlay, never an in-place mutation** (both twins). So "read-only" is just *drop the
overlay*, and the SAME move covers two places: the **client** abandoning an action on a miss (discard
the overlay = atomic rollback), and the **server** harvesting an action's read-footprint (run it over
the shipped state, writes stage into a throwaway overlay, **discard it**, keep the reads). Nothing is
ever "undone" because nothing was mutated in place. ⇒ running an action on the server to plan it is
**effect-free by construction** (it does NOT put effects server-side — my earlier worry was wrong). And
since drafts now ship too (I3 reframed), the server can reproduce **any** fn — render or action — over the
full state, so **server-harvest is the uniform planner**. The client-side abandon (discard the `ctx` on a
miss) is then purely for **atomicity**, not an alternative planner: the flow is *client tries → misses →
abandons (atomic) → ships the intent → server harvests the full footprint → client re-runs atomically over
the present data → commits*. (The earlier server-harvest-vs-client-abandon fork dissolves — it rested on
drafts not shipping.)

**v1 covers BOTH paths** — "dumb" means inefficient, not renders-only. The render-miss path is the first
build *slice* (the `<UserAdmin>` footgun), but the **action-miss path** (a button click reading unloaded
data) is in the SAME milestone, NOT punted — a click that silently dies on missing data is the same class
of bug. The action path needs:
- handlers run in an **implicit `ctx`** (commit-on-success) so an abort is atomic rollback — a behavior
  change for all handlers, but a good one (atomic handlers; writes batch to handler-end, same net effect);
- a VNA inside a handler is **caught → abort → a pending-action record** (handler fn-id + slot + state) →
  fetch → **re-invoke on the reply** (the new client machinery);
- the **server invokes the named fn**: it reproduces the render (binding the handler's closure — its
  captured `row`/locals), then invokes that handler-at-its-slot read-only to harvest (writes → throwaway
  overlay, discarded). One generalization of the render-reproduce path, not a separate mechanism.
The `ctx` primitive already exists; the genuinely new bits are catch/record/re-invoke + invoke-named-fn.

## Build sequence (reviewed by architecture-reviewer + milestone-planner, 2026-06-25)

**Verdicts:** architecture-reviewer — *sound-with-conditions* (I2 no-warm-graph + I1 seam verified to
hold in the actual code; fn-id substrate + gen-guard correctly reuse working mechanisms).
milestone-planner — a clean thin decomposition; slice 1a handed off ready for slice-builder. The *model*
is confirmed; what they refined is the SLICING + a few correctness commitments.

**The sequence** (each one scenario; twin changes land conformance-first):

| # | Slice | Twin/conf? | The one proof |
|---|---|---|---|
| 1a | Component-state ship + seed (the mechanism) | **YES** — 1 conformance case | server-render Gherkin: render `/` as admin WITH a seeded slot → assert `db.users` harvested |
| 1b | View-state toggle demands a fetch + popup | no (client only) | browser: admin → "Manage users" → `.user-admin` rows populate |
| 1c | Generation guard (optimistic-clobber) | no (edits the epoch guard) | mutate mid-fetch → the optimistic edit survives the stale reply |
| 3 | Implicit-`ctx` handlers (atomic, commit-on-success) | no | a throwing handler leaves zero partial writes (+ full suite = regression) |
| 4 | Action-miss round-trip (catch→record→fetch→re-invoke + server-invokes-named-fn) | **YES** (server invokes a named handler) | a button whose handler reads unloaded data completes its action |
| 5 | Client reachability GC | no | nav away from a data-heavy view → pulled data dropped |

**Key catch (milestone-planner):** a pure view-state toggle does NOT trigger a refetch today
(`maybeRefetch` only fires on stale / `needsServerData`) — so **1b** must wire "a view-state change that
alters the demand sets `needsServerData`." Without it, 1a is correct but the popup never invokes it.

**Correctness commitments (architecture-reviewer):**
- **Twin risk is in the SHIP, not the seed.** The conformance case proves the server *consuming* a seed;
  the new code is the client *serializing* component state (closures are skipped by `ClientState.Serialize`
  today — no wire form). 1a must cover the ship/reconstruct round-trip (or mark it explicitly client-only
  orchestration, like `sessionVars`).
- **Seed granularity = a CORRECTNESS fork.** Whole-`state`-object overwrite changes the setup's object
  identity mid-render; per-field preserves it. **v1 = whole-object** (the `<UserMenu>` `{managing}` toggle
  is a scalar, so it suffices); revisit when a draft-bearing component (`<SetPasswordControl>` `{password}`,
  nested create-drafts) needs seeding. Decide before writing the conformance case.
- **Gen-bump: conservative over-bump is the v1 floor** — bump on data-mutation + session-change AND **any
  draft edit made while a fetch is in flight** (over-bump = an extra re-fetch; under-bump = silent wrong
  data, the unsafe direction). Narrow to demand-feeding-only later.
- **Bound "whole state"** to the live render's MOUNTED `comp:` slots (what's on screen), never the
  historical cache — so "ship the whole state" can't grow unbounded.
- **Action-harvest stays floor-gated:** the server invoking a handler read-only harvests through the SAME
  read floor; the discarded write-overlay is never an un-floored read source. (The inherited dict floor
  gap is pre-existing, not introduced here.)

**Atomic handlers — DECIDED** (user directed it: *"it should be atomic, we need to do the eval in
isolation"*). Slice 3 makes EVERY handler run in an implicit commit-on-success `ctx`; "writes batch to
handler-end" is inherent to atomicity, not a separate choice. It's a separate *slice* purely for
**build-ordering** — it lands + stabilizes under the full existing suite (the regression proof) before
the action-miss fetch machinery rides on it — NOT because it's open. (The architecture-reviewer flagged
it as a rule-10 change to confirm; redundant here — atomicity was already the agreed model.)

**Slice 1a — DONE** (built + reviewed *sound*, suite **530/530**, on branch `client-data-layer`,
2026-06-25): `ExecContext.Seed` (slot-key → var → value) + twin `ApplySeed`/`applySeed` (overwrite the
render-closure's setup-scope locals between the setup and `:view` memoizes; whole-object, ignore an
absent var) + `SsrRenderer.Render(seed)` (optional, null = today) + a conformance case + a server-render
Gherkin over a controlled `panel` fixture. Seed-consumption ONLY; the client ship + refetch threading
are 1b.

**TRACKED (1a review, for 1b/4):** the *server* memoize is write-only so the seed always takes effect,
but the *client* memoize short-circuits on a `:view` hit — so IF a later slice ever makes the **client**
consume a seed across a re-render, `applySeed` must also stale the `slotKey + ":view"` entry (mirror
`rebindComponentArgs`) or add a multi-render seeded conformance case. Dormant in 1a (the client never
sets `context.seed`). And: whole-object seed overwrite is fine for scalar toggles — revisit per-field
before seeding a draft-bearing component (`<SetPasswordControl>` `{password}`).

**Slice 1b — DONE** (built + reviewed *sound-with-conditions*, suite **531/531**, 2026-06-25): the CLIENT
SHIP + server reconstruct → the full round-trip. `ws.ts` `slotState()` serializes mounted `comp:` slots'
writable view-state by the `sessionVars` id-axis rule (scalar by value, positive-id by ref, transient
object by its scalar props — **all scalars incl. text**); `WsHandler` (`WsRequest.SlotState` +
`SlotStateFromWire`) rebuilds it and threads it into 1a's `seed`. Proven by a browser e2e over a controlled
`<panel>` fixture (data reaches the toggled-open component ONLY via the round-trip; fail-before/pass-after
both halves). Client-only TS + C# wire-handler — NO twin/conformance change.

**RESOLVED (1b review):** a typed `text` view-state value (e.g. `<SetPasswordControl>`'s `state.password`)
rides the refetch payload to the authenticated server. ACCEPTED (user 2026-06-25: *"it's just a data field,
sent anyways later"*) — the client's own input, bound for the same authenticated server `setPassword`
already sends it to; no new exposure, not a floor breach. **LESSON: do NOT omit `text` from the ship-rule**
— a tried "ship bool/int only" fix broke a legitimate case (per-row component TEXT state must survive a
refetch; it hung 3/3). ALL scalar view-state ships by value. The genuinely-deferred I3 case is narrower: a
draft whose *values DRIVE a query* (`where x == draft.field`), which 1b does not enable (the harvest depends
on which BRANCH renders, not on field values).

## GC — the LAST slice (renamed from "Slice 2": it must ship after the round-trip can re-pull)

The client graph (`uiStatic.state.objects/arrays`) grows as views pull data and never shrinks. Add the
server store-GC's **dual**: roots = scope vars (`db`, `currentUser`, selection, drafts) + the journal's
pending mutations + **what the current render reaches**; mark-and-sweep the unreachable; sweep on
nav / state-change. **Safe only because slice 1 can re-pull anything by intent** — vision-keeper's
ordering constraint (can't drop what you can't re-fetch).

## Later improvements (the "make it not-dumb" backlog)

Incremental/diff state shipping (don't re-send unchanged state) · minimal-footprint fetch (fetch the
delta a state-change demands, not a whole re-render) · **action-replay unification** (every
client→server message becomes *action + state*; the mutation journal and the render-intent merge) ·
privacy re-layer over the clean seam.

## Open to sculpt (most now resolved in *Build sequence*)

1. **GC roots + sweep timing** (the GC slice).
2. The eventual **narrowing of the gen predicate** (v1 floor = conservative over-bump on any in-flight
   draft edit; narrow to demand-feeding-only later).

(Resolved: seed granularity = **whole-object for v1**; atomic handlers = **DECIDED** — both in *Build
sequence*.)

## Test plan

- **Gherkin (slice 1):** the `<UserAdmin>` popup — sign in as admin, toggle "Manage users," assert the
  user rows render (`.user-admin` populated). The real-browser proof the footgun memo's throwaway test
  foreshadowed.
- **Conformance (slice 1):** a component-with-seed renders the seeded view, both twins.
- **Gherkin (slice 2):** navigate away from a data-heavy view → the pulled data is dropped
  (unreachable-graph assertion).

## Guardrails

Tests-first per slice; both twins in lockstep via `conformance.json`; storage seam untouched (fetch
through `IInstanceStore`). Hold **I1–I5**. M-auth is **DONE (2026-06-25)** — this is cleared to build as
the next substantial milestone (its small follow-ups don't gate it).
