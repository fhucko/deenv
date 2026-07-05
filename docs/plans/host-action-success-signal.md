# Host-action success signal — design (completion callback)

Drafted 2026-07-05, triggered by the slice-1 ux review's stale-migration trap meeting the
<3× rule: THREE surfaces now want "app Code reacts to a host action SUCCEEDING" —
B3's ledgered "Last published: … · time" line, B4's ledgered "Merged X into this design"
confirmation, and the commit bar's clear-inputs-on-success (ux HIGH finding, 2026-07-05).
Status: **ACCEPTED — grilled 2026-07-05 (verdict SOUND-WITH-FIXES, all four fixes folded in
below); user approved building the mechanism 2026-07-05. Build sequenced AFTER the slice-1
review-fix batch (shared ws.ts/app.deenv).**

## The gap

Code fires a host action inside a handler (`sys.commitDesign(design, msg, migration)`) via
the client send-hook → WS `hostAction` op. The reply is ok/error. On ERROR the framework
surfaces the global banner and the app's inputs are retained. On OK a refetch refreshes
data-derived renders ("Last commit:" updates) — but app Code cannot RUN anything: it cannot
clear an input, cannot render a transient "Published ✓", cannot compose any post-success
behavior. B3/B4 both ledgered this as "an apply-success signal Code can't cheaply read".

## Position — an optional trailing completion fn on host-action builtins

`sys.commitDesign(design, message, migration, () => { commitMigration = ""; commitMessage = "" })`

- **Shape:** every host-action builtin accepts ONE optional trailing fn argument. If present,
  the client stores it when sending the `hostAction` op and INVOKES it when the matching
  reply arrives with ok — and only on ok (the error path is unchanged: banner + retained
  inputs; the callback never fires).
- **Why a callback and not a readable status var** (`sys.lastAction`-style): the three
  consumers split into imperative effects (clear inputs) and renderable state ("Merged X").
  A status var serves only the latter and CANNOT serve the former (renders must not mutate).
  A callback serves both — renderable state is just the app setting its OWN ui var in the
  callback (`lastMergeNote = "Merged " + name`), which keeps post-success state app-owned
  and app-shaped instead of inventing a framework vocabulary for it (system/user separation;
  no special-case primitive where a general one works).
- **Client-only, ctx.status precedent:** the C# twin's host-action cases are already
  `ExecNothing` on SSR/refetch (the send-hook never fires server-side) — the callback simply
  never runs there. No conformance case (host actions are outside the conformance contract —
  slice-8 precedent). No wire change: replies already correlate by msgId (§8 idempotency
  machinery), so the callback registry is keyed by the sent op's msgId.
- **Ordering:** on an ok reply, run the callback FIRST, then the normal refetch. Clearing a
  ui var then refetching must not resurrect stale text — the A2 scalar carve-out
  (dt.ts mergeState, landed 2026-07-04) is the guard rail; the grill must verify the
  interaction concretely.
- **Arity/validation:** `CodeValidator.BuiltinArities` already supports arity LISTS and is
  the SINGLE source (no TS mirror — grill-verified), so each host action gains its +1
  variant. The universal executor rule: **a CodeFunction in LAST position = the callback**,
  everywhere. Two ambiguous builtins, both reasoned (grill fix 1):
  - `mergeBranch [2,3] → [2,3,4]`: arg 3 = resolutions array OR callback → type-disambiguate.
  - `publish [2,4] → [2,3,4,5]` (the doc's earlier "only mergeBranch" was WRONG): publish's
    optional pair is both-or-neither, so 3 args = 2+callback (arg 2 MUST be a fn — a non-fn
    third arg stays invalid), 5 args = 4+callback. The fn-in-last-position rule holds for the
    guarded-4 form (arg 3 is the guard token, never a fn).
- **Plumbing honestly stated (grill fix 4):** the callback must be SPLIT OUT of the wire
  args BEFORE `args.map(scalarOf)` — `sendHostAction`/the hostAction hook signature grows an
  optional callback slot (codeExec.ts:1897, ws.ts:722); the registry lives beside the hook.
- **Reply dispatch (grill fix 2):** today's ok-branch matches on op only (ws.ts:1055) —
  the callback lookup must key on the reply's `msg.id` (already on the wire via WithId,
  WsHandler.cs:446) so two in-flight actions each run only their own callback.
- **Callback robustness (grill fix 3):** the callback runs in try/finally — a throwing
  callback must never skip the resetViewState+refetch of a SUCCESSFULLY applied action.
- **Lifecycle edges:** navigate-away before the reply → registry entry dropped, callback
  silently never runs (same class as any stale handler); multiple in-flight actions →
  independent id-keyed entries.
- **Callbacks ARE full handlers (review fix, closes the open question):** the callback runs
  through the EXACT SAME idiom `onClick` uses (ui.ts:687) — memo-bypassed
  (`runWithMemoBypass`) and wrapped in a commit-on-success handler transaction
  (`runHandlerTransaction`), not a bare `callFunction`. Consistency-by-construction: a
  callback that only clears a ui var behaves identically whether reached via `onClick` or via
  a host-action reply, and a FUTURE callback that stages a write or fires a nested host
  action gets the same atomicity/VNA handling any other handler gets — there is no separate,
  weaker invocation path to keep in sync. No `action` (the VNA action-miss re-invoke identity,
  keyed by a render-slot closure's `(fnId, slot)`) is passed — a callback isn't reached via a
  render-slot the way `onClick` is, so a VNA inside one falls back to
  `runHandlerTransaction`'s un-recorded flush-and-rethrow leg, same as any handler built
  outside a render. The outer try/finally around resetViewState/refetch stays: it guards
  against the transaction itself re-throwing (the genuine-bug and non-recorded-VNA legs both
  re-throw after cleanly rolling back or flushing).

## Consumers (immediate)

1. Commit bar: clear `commitMigration` + `commitMessage` on success (the ux HIGH fix —
   both inputs; a committed message is done, retaining it invites stale re-commits).
2. B3 ledgered: `lastPublishNote` set in publish's callback → "Last published …" line.
3. B4 ledgered: `lastMergeNote` in mergeBranch's callback → post-merge confirmation.
   (2 and 3 are follow-ups riding the mechanism, not part of its landing slice — the
   landing slice ships the mechanism + consumer 1.)

## Self-grill (2026-07-05) — SOUND-WITH-FIXES, integrated above

Fresh-context opus, refute-briefed, verified against code. Confirmed sound: msgId correlation
already on the wire (WithId stamps every reply — no wire change); fn values capture the
PERSISTENT top scope by reference and `callFunction` is invocable from ws.ts with a fresh
context (exactly how onClick already runs); `resetViewState` never touches app scope; ui vars
round-trip refetch and the A2 carve-out is inert-safe for the clear (same-scalar → no-op
write); C# host-action cases never evaluate args; HostActionScan is callee-shaped AND
recurses into callback bodies (a nested host action still wires the floor); judgment upheld
callback-on-ok over a status var (can't express imperative effects), a framework-clear
convention (special-case where a general one works), and handler re-run (double-applies).
Fixes folded in: publish arity reasoning, id-keyed reply dispatch, try/finally, the
sendHostAction signature change stated honestly.

## Explicitly not

- Not a promise/async surface in the language (no await, no chaining — one fn, fire-once).
- Not an error callback (errors stay on the banner path — one error surface).
- Not a server-side hook (host actions remain client-triggered, kernel-authorized).
