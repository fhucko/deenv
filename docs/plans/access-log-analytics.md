# Access log + analytics — design

2026-07-07. Triggered by user request: built-in access log + analytics covering the common
ERP/CRM audit suite. **Status: design draft, grilled ×1 (refutations folded in below), user
decisions marked; NOT scheduled — the design queue (assets → compaction/§0b) comes first, and
this design deliberately hands its retention knobs to the compaction pass.**

## Settled by the user (before drafting — do not re-litigate)

- Recording is kernel-side and **transparent to apps**: zero app code, tracked apps' stores
  and namespaces untouched. An app cannot tell it is being logged, opt out, or forge entries.
- Raw events are **kernel-owned, append-only, immutable** — explicitly NOT ordinary app
  writes ("option 2": raw events riding the store log would bloat versioning with machine
  noise).
- Daily roll-ups ARE ordinary data, written into an **analytics app's** store; the dashboard
  is a deenv app built almost entirely on the generic lib (SetTable first; a chart primitive
  goes into the lib later only if needed).
- Audit/timeline reads of raw entries = a server READ builtin (AGENTS.md rule 12 shape).

## Scope — the ERP/CRM audit suite, mapped to deenv

| Suite item | Status |
|---|---|
| 1. Write-audit trail (who changed what, when) | **Already exists.** `AppLog.LogEntry` carries `Seq, At, Who, MsgId, Writes` per write (AppLog.cs; `Who` from `StoreWriteContext`, the AsyncLocal set at the WS boundary). Design-history `Commit.by User` landed 2026-07-05. Nothing to build except later surfacing. |
| 2. Record access log (who VIEWED what) | **New** — the core of this design. Rides the render footprint. |
| 3. Per-record timeline | A **query** over 1+2 at render time, not a new store. Late slice. |
| 4. Auth events (login/logout/failed) | **New instrumentation** — confirmed zero auth-event logging exists today. |
| 5. Admin/host-action log | Host-action *writes* already land attributed in AppLog; a dedicated "action X ran by Y" ledger does not exist. v1: the recorder logs the op name + principal at the WS chokepoint; arg capture is a later slice. |
| 6. Usage analytics | **New**: roll-ups + dashboard app. |

Not a VISION pillar — a feature serving the ERP/CRM template goal (the ui-workflow-catalog
files audit-trail under the versioning/temporal pillar). The cookieless posture was already
committed to by login-persistence ("anonymous visitors get NO Set-Cookie … the
cookieless-analytics posture", login-persistence.md:85).

## P1. The raw event log — a new mechanism, stated honestly

**Day-segmented JSONL files per instance:** `instances/<id>/access/<yyyy-mm-dd>.jsonl`,
kernel-owned, NOT behind `IInstanceStore`.

**Honesty note:** earlier framing called this "like the migration boundary log" — wrong. The
boundary entry is a tagged `LogEntry` in the SAME AppLog (ApplyPublishBoundary). **No separate
kernel side-log exists today; this is a new mechanism with no sibling.** What keeps it small:

- Events are **loss-tolerant** (unlike data): buffered appends, flushed on interval/shutdown,
  no fsync per event, no WAL. Crash loses the last few seconds of views — an accepted, stated
  ceiling. The AppLog's WAL+fsck rigor is exactly what this does not need.
- **Day segmentation** (grill #4 fix) makes every mechanic trivial: each file is truly
  append-only for its one day; **prune = `File.Delete` of whole day-files** (atomic,
  crash-safe — no tail-rewrite of an "immutable" file); newest-first reads = today's small
  file, walking back a day at a time (no reverse-scan of one big file); roll-up scans exactly
  one day's file; salt rotation shares the boundary.
- Immutability is absolute: no edit API exists; deletion of whole expired days is the only
  mutation.
- Reader tolerates a truncated final line (skip) — the only fsck needed.
- Format: JSONL, camelCase via the shared JsonSerializerOptions style.

Per-instance (not global) because retention is per-app policy, **clones must NOT inherit
access logs** (a time-travel clone carrying visitor logs would be wrong + a privacy leak),
and instance delete cleanly deletes its telemetry.

**Rule-6 check (grilled, survives):** the storage-interface rule scopes the app-data *model*
seam; kernel telemetry sits beside it like kernel.json (kernel-read/written directly), not
inside it. Placement: the recorder is **constructed by the kernel** (which knows the instance
directory) and threaded into `ContentHandler`/`WsHandler` exactly like `TokenAuth`
(InstanceApp.Build), behind a minimal interface with a no-op for the test host. Whether the
class lives in `DeEnv/Kernel` or beside `AppLog` in `Storage` is the builder's call — either
keeps Code free of it; it must NOT go behind `IInstanceStore`.

## P2. Event record

```jsonc
{ "at": "…", "kind": "view" | "fetch" | "supplement" | "auth" | "action",
  "visitor": "u:42" | "a:<dailyHash>",     // bound user id OR anonymous daily hash
  "clientId": "…",                          // the SSR-minted session id (= the view-id)
  "path": "/orders/17",
  "touched": ["Order:17", "Customer:3"],   // displayed (type,id) set — LEAF set only
  "via": "ws" | "session",                  // auth events: which login path
  "auth": "login" | "logout" | "loginFailed",
  "action": "publish" | "commitDesign" | "…" }  // op name only v1; args later
```

- **`touched` = the leaf set** (`ExecContext.AccessedObjectProps` keys, filtered to
  `Id > 0 && TypeName != null` — transients/constants/dict descriptors excluded), NOT the
  memo-dependency set. The leaf set is what structural privacy already defines as "what this
  client was shown"; logging dependencies would record more than the user saw. Grill #3
  verified the semantics: a refetch's footprint is a full fresh per-view render (not
  cumulative), and leaves are genuinely *displayed* — so "who read Order 17 = everyone who
  viewed /orders" is honest was-shown semantics (a list view really shows every row). Broad
  for lists; that is the true meaning of a list view. Props are not logged v1 (volume;
  marginal audit value over type+id) — stated ceiling.
- **Anonymous uniques**: `hash(IP + UA + dailyKey)` — the Plausible/GoatCounter trick. Raw
  IP/UA are hashed immediately and never stored. **Confirmed gap: no code reads client IP or
  User-Agent today** — both are new reads at SSR/`/session` (GenHTTP exposes them;
  X-Forwarded-For behind nginx). Daily key = `HMAC(persistedSecret, utcDate)` — ONE persisted
  kernel secret, zero rotation code (grill #10 trim).
- **Failed logins**: log the attempted username only if it resolves to an existing user;
  unknown-name failures log visitor hash + "unknown user" WITHOUT the typed string (users
  fat-finger passwords into username fields; never put those in a log).

## P3. View identity + idempotency

**Confirmed: the view-id already exists.** `clientId` is minted once per SSR page load
(SsrRenderer.cs:139), rides `window.initClientId` → WS hello → every subsequent op. A reload
mints a new one; refetches reuse it; **a WS reconnect re-hellos with the SAME clientId**
(grill-verified) — so reconnects don't inflate. No new wire field anywhere.

**A view = the first event for `(clientId, path)`.** SSR logs it; SPA navigations log on
their first refetch (every refetch carries `path`); repeated same-key refetches don't
re-count. The SSR+refetch double-fire collapses under the same key.

Grill #1/#2 refuted the naive "seen-set on ClientSession" dedup — sessions die (unclaimed
10s; idle 30min; LRU cap 500) long before a page does, and refetches keep flowing with the
old clientId (null session renders anonymous). Fixes folded:

- **The count authority is roll-up-side dedup by `(clientId, path, day)`** — the key is in
  every event, so aggregates are correct regardless of recorder-side duplicates. "Recomputed
  from the deduped event set" MEANS this. A recorder-side seen-cache (small capped map owned
  by the recorder, not ClientSession) is only a volume optimization.
- **Bot/claim gating (grill #1):** every curl/crawler GET renders SSR and would count as a
  view — on a public instance crawlers would dominate. Fix: buffer the SSR view event on the
  session; **flush as `"view"` on WS claim** (hello, ≤10s), **flush as `"fetch"` on unclaimed
  sweep** — the audit record survives (data WAS shipped to that curl) while roll-ups count
  only claimed views. Consistent with the buffered-appends ceiling.
- **Supplemental events (grill #3 — the audit-integrity fix):** later same-path refetches are
  exactly how NEW data ships (slotState popup-open, action-miss harvest). Dedup the *count*,
  not the record: a same-key refetch whose `touched` has pairs not yet logged for this
  (clientId, path) appends a count-neutral `"supplement"` event. Without this, the audit
  claim is false for the client-data-layer's flagship paths.

**Stated ceiling — purely client-side navigations are invisible.** Confirmed
(`maybeRefetch` no-ops when nothing is stale and no server data is needed): a navigation to a
view fully renderable from shipped data never hits the server. The audit story survives —
everything shown was footprint-logged when shipped (view + supplement events) — but the
page-view *count* undercounts such navigations. Accepted v1; the alternative is a nav beacon,
i.e. new client chatter for a count, explicitly not wanted.

**Recording points: exactly three** (grill #8 corrected the draft's two):
`ContentHandler.HandleAsync` (SSR views), `WsHandler.ProcessMessage` (refetch views +
supplements; WS login/logout; host actions), and **`SessionHandler` on `/session`**
(cookie-path login/logout/failed — outside ProcessMessage; also where scripted brute-force
lands, which is what an auth log exists to catch). The client fires BOTH login paths on every
interactive login (`persistLogin(...).finally(wsSend login)`), hence the `via` tag; roll-ups
count one mechanism. No server-push re-render path exists today (real-time is a future
milestone — nothing to instrument, and this design must not smuggle it in).

**Visitor identity (grill #7 — the WS layer has no IP/UA):** `ProcessMessage(string json)` is
transport-agnostic; the upgrade request is discarded at the WS boundary. Fix: compute the
anonymous daily hash **once at SSR** and stash it on `ClientSession` beside
`PrincipalUserId`; WS-logged events read `u:<principal>` else the stashed hash. Ceilings,
stated: after session expiry, refetch-logged events have unknown visitor (the refetch already
renders anonymous today — the log inherits that, doesn't worsen it); a session spanning UTC
midnight keeps its minting-day hash.

## P4. Roll-ups — ordinary data, no scheduler, no config

Daily aggregates are ordinary objects in the analytics app's store, written by the kernel
through the caller's own `IInstanceStore` (`CreateObject`/`WriteFieldBatch`/`CommitBatch` —
the AdminSeed shape; `Who = null`). Ordinary commits, access rules, GC.

- v1 rows: per (sourceInstance, day, path): `views`, `uniques`; per (sourceInstance, day):
  `logins`, `loginFailures`; per (sourceInstance, day, type): `recordReads`.
- **Idempotent by construction**: a roll-up UPSERTS the day's row keyed by
  (source, day, path), recomputed from the deduped event set (P3's dedup definition) — never
  incremented. Re-running is a no-op.
- **Trigger = a host action, no scheduler** (hidden-scope guard: deenv has no scheduler and
  this design does not add one). `sys.rollupAnalytics(sourceInstance)` invoked from the
  analytics app's UI ("Roll up now" button; success-callback confirmation line). Stated
  ceiling: manual. A future scheduler calls the same action.
- **No kernel.json config, no reserved names**: the roll-up writes into the CALLING
  instance's store — the analytics app IS wherever the action is called from. It enumerates
  sources via the existing `sys.instances` and loops, or the action defaults to all
  instances via a new list delegate — builder names the choice up front (grill #5: today
  KernelHostActions has no list-all delegate; don't let it appear silently). Reading a source
  instance's `access/` dir = the resolveTarget/DataPath delegate seam publish already uses.
- **Convention types, loud-fail (grill #6):** the kernel writes only types/fields the
  analytics app declares. The two existing precedents disagree — AdminSeed throws loudly on a
  missing/mistyped field; Commit.by silently skips. **Copy AdminSeed**: an operator-invoked
  button must surface drift in the click's error banner, not let dashboards quietly stop
  updating after a rename. Pin the convention names in the system layer with a guard test
  (the system/user-separation rule, UserConvention precedent).
- **Authority (grill #5, flagged not fixed):** the action is gated by the calling app's own
  `sys` access rule — and a `sys`-granted app already holds full kernel authority (it can
  delete instances), so "reads every app's access log" is below the existing bar. Fine for
  Stage-1 single-operator; **Stage-2 multi-tenant must revisit this alongside the privacy
  gradient** (a finer-than-`sys` grant class).

## P5. Reads — the rule-12 builtin

`sys.accessLog(type?, id?, limit)` — C#-only compute, memoized empty-deps cache entry, TS
twin stub throws "Value not available" → refetch (the `sys.diffCommits` shape; no conformance
case). **Own-instance log only** — the grill (#10) caught that an `instance?` arg would
smuggle in cross-instance render-time reads from ordinary apps, a wiring class that exists
today only design-host-gated; dropped. The audit/timeline view lives on the instance it
concerns; cross-instance raw access exists only inside the roll-up host action.

Always bounded: server-capped `limit`, newest-first, served from the day files walking
backward — cheap because of segmentation; no pagination/cursor v1 (stated ceiling). Gating:
the app's own `access` section (the general mechanism — the settled kernel.json-identity-flag
rejection applies).

Per-record timeline (suite item 3) = a later slice composing `sys.accessLog(type, id)` with
commit/AppLog history for the same object at render time. A query, not a store.

## P6. Retention + prune

- `sys.pruneAccessLog(instance, olderThan)` host op beside delete/cloneInstance; deletes
  whole expired day-files. Manual v1 (same no-scheduler honesty). **No stored retention
  default** — the draft's "default 90 days" was a phantom policy with no scheduler to apply
  it (grill #10 trim); the explicit arg is the whole surface. A stored retention *policy*
  belongs to the compaction design pass (same log-policy domain as assets §6 retention) —
  handed off, not invented twice.
- Aggregates persist past pruning (they're ordinary data) — pruning raw loses detail, never
  history.

## P7. Privacy posture

- Cookieless, IP-less at rest, salt-derived daily hashing → no cross-day correlation of
  anonymous visitors, no consent banner.
- The gradient, stated plainly: anonymous counts (benign) → per-user page views (moderate) →
  per-user record-access (surveillance-grade in multi-user apps). v1 records the full
  gradient because Stage 1 = a single operator self-monitoring their own apps. The Stage-2
  multi-tenant story — per-app collect-less knobs AND the roll-up authority note (P4) — is
  deliberately deferred and flagged, not hidden.

## Named ceilings (one place)

- Buffered appends: a crash loses the last seconds of events (loss-tolerant by design).
- Purely client-side navigations undercount the view count (audit record unaffected).
- Expired-session refetches log with unknown visitor (inherits existing anonymous-refetch
  behavior).
- Roll-up + prune are manual host actions (no scheduler exists).
- `sys.accessLog` is bounded newest-first, no pagination; own instance only.
- `touched` is type+id (no props), leaf set only.
- Host-action events record op name only (no args) v1.
- Volume, concretely: ~200B/event + ~15B per touched id; a public instance at ~5k events/day
  ≈ 2–5MB/day raw — disk is fine on the deploy box; the 1GB RAM constraint is respected by
  day-segmented bounded reads (never parse the whole history in a render). Works at
  today's scale; a high-traffic multi-tenant future re-opens storage (the same future that
  re-opens the store engine).

## Grill #1 — record (2026-07-07, fresh-context adversarial pass, code-grounded)

Ten attacks; per-attack verdicts, all fixes folded into the sections above:

1. clientId lifecycle — reconnect reuse CONFIRMED; expired-session dedup REFUTED → roll-up-side
   dedup is the count authority (P3). Bots have no story → claim-gated `view` vs `fetch` (P3).
2. Seen-set on ClientSession REFUTED (10s/30min/LRU-500 expiry) → recorder-owned cache =
   optimization only (P3).
3. Leaf-set audit semantics SURVIVES (per-view, genuinely displayed); dedup-drops-new-data
   corner REFUTED → `supplement` events (P3).
4. One-file JSONL mechanics REFUTED (prune=rewrite; reverse-scan) → day-segmented files (P1);
   volume numbers survive at target scale (ceilings).
5. Roll-up-from-analytics-app SURVIVES (delegate seam + sys.instances verified); enumeration
   delegate + Stage-2 authority note must be explicit (P4).
6. Convention-type drift: two contradictory precedents exist → loud-fail (AdminSeed) chosen,
   guard test pins names (P4).
7. WS layer has no IP/UA REFUTED the draft → visitor hash stashed on ClientSession at SSR (P3).
8. "Two chokepoints" REFUTED → third recording point at `/session`, `via` tag, count-one rule
   (P3).
9. Kernel-owned file vs rule 6 SURVIVES (telemetry ≠ model data; kernel.json precedent);
   threading = the TokenAuth shape (P1).
10. Trims: retention default deleted; salt = derived key, one secret. Named-scope items:
    RenderState must surface the ExecContext to the recorder (small interface change);
    touched filter; enumeration choice (P4).

## Open (not solved here)

- Recorder class placement: `DeEnv/Kernel` vs beside `AppLog` in Storage — builder's call,
  both clean (never behind `IInstanceStore`).
- Stage-2 multi-tenant: collect-less gradient knobs + a finer-than-`sys` grant for roll-up
  reads.
- Whether expired-session events deserve better attribution than "unknown" (accepted v1).
- Stored retention policy — handed to the compaction design pass.

## Rough slices (for milestone-planner when scheduled)

1. Recorder seam + view events: interface, kernel construction, TokenAuth-style threading;
   SSR view events with claim-gated buffer/flush (`view`/`fetch`), refetch views +
   `supplement`s, roll-up-side dedup definition; visitor hash at SSR on ClientSession.
2. Auth events at all three points (`via` tag) + host-action op events.
3. `touched` footprint capture (RenderState context surfacing + filter).
4. `sys.accessLog` READ builtin + `sys.pruneAccessLog` host op.
5. `sys.rollupAnalytics` + convention types + guard test + the analytics app (generic-lib
   dashboard, SetTable).
6. Per-record timeline; host-action arg capture. (Each its own gate.)

## Deliberately out of scope (guard rails)

No scheduler. No real-time/live dashboards (future milestone; nothing to instrument today —
verified no server-push path exists). No client beacon / new wire fields. No chart primitive
until SetTable proves insufficient. No per-app opt-out flags v1 (minimal-by-default; Stage-2).
No pagination on the READ builtin v1. No cross-instance `sys.accessLog`.
