# Transparent client mutations — design draft

*2026-07-11. Triggered by the M12 wrap/unwrap review: Code can mutate a fresh
client object immediately, but persistence still leaks the negative→real-id
round trip into what one handler can express. Status: design revised after one
adversarial self-grill; direction proposed, nothing scheduled or accepted yet.*

## Outcome

A cleanly completed supported client handler persists as one semantic changeset. Code operates
only on the optimistic object graph, including fresh negative-id objects. The
transport maps those identities when it commits; Code never waits for an ack to
continue composing mutations.

When that holds, `wrap` and `unwrap` need no bespoke persistence path. They are
ordinary composition of create/link/unlink/move. The dedicated buttons and the
S5c-only nested-`refId` wire shape can then be deleted if dogfooding shows the
generic structure controls are sufficient.

## Confirmed foundation

- Client handlers already have a commit-on-success bracket in `ws.ts`:
  optimistic writes are journaled, outgoing sends are buffered, and a throw
  rolls the journal back. On success `flushHandlerTx` currently sends each
  buffered JSON message separately. Thus “one handler = one client transaction”
  exists, but “one handler = one server commit” does not.
- `IInstanceStore.CommitBatch` already mints every create first, maps negative
  temp ids to real ids, applies relations/writes, then saves once. It already
  supports relations whose owner/member references are fresh temp ids through
  `SetLinkByPropMutation` internally.
- The existing `commit` wire path already carries creates, edits and set/ref
  relations to that store batch. This is the mechanism to extend, not a new
  endpoint.
- `CommitBatch` lacks unlink/remove. Standalone `arrayRemove` therefore saves and
  runs GC independently; this is why move/wrap currently link first and unlink
  second, leaving a brief double-membership window.
- The canvas and designer are client-live, not client-only. Mutations must reach
  the server for durable storage, access enforcement, versioning and later
  refetches. The desired abstraction is transparent persistence, not removal of
  the server.

## Proposed model

### 1. Buffer semantic mutations, not serialized sends

Inside the existing handler transaction, retain the structured mutation intent
needed to build one existing `commit` request. The first vertical slice supports
only the operations the designer re-parent flow actually needs: set add/link,
set unlink, and scalar field writes (`order`). Non-mutating messages remain
ordinary sends. A clean completion emits one commit; a non-VNA throw aborts as
today. A handler that mixes an unsupported mutation kind with this aggregate
must throw before sending anything — never silently split its transaction.

The existing post-write `Value not available` behavior is deliberately different:
it flushes work already performed and rethrows. The first slice preserves and
pins that contract; it does not claim every thrown handler leaves zero commits.

Do not expose a new Code API, flag or explicit transaction object. The DOM
handler boundary is already the transaction boundary and is the zero-config
common case.

The client journal remains the optimistic rollback/ack unit, but one batch gets
one correlation id and one aggregate journal entry whose undo replays the
constituent entries in reverse. This avoids teaching one server ack to retire N
independent messages.

### 2. Complete the existing commit vocabulary

Add only the missing semantic mutations:

- set relation addressed by `(ownerRef, prop, memberRef)` so a set belonging to
  a fresh owner can be targeted before its real set id exists;
- set unlink, supporting either an existing set id or `(ownerRef, prop)`, with
  member refs using the same positive-or-temp convention;
- GC once after the complete batch, never between link and unlink.

The server derives types from the schema/store and applies both standalone access
decisions over pre-mutation snapshots: `create` on the member for the new
membership and `delete` on the member for the old membership. A fresh-owner
relation derives the owner type, declared set prop and element type; the wire
never gets to assert trusted types. The store does not enforce element-type
compatibility itself, so this caller validation is load-bearing.
Prevalidation must resolve every temp reference, owner/prop/set, member type and
unlink membership before the first mutation because `CommitBatch` has no rollback
after mutation begins. This provides reject-before-mutate; it does not fix the
existing limitation that an exceptional physical Save failure may leave the
in-memory document changed.

### 3. Preserve client remapping as an implementation detail

The commit response already returns the full `idMap`, including collection ids.
Apply all remaps to the optimistic graph before retiring the aggregate journal,
then send the existing remap acknowledgement. `HandleCommit` must first register
every returned temp→real mapping in the server session, matching `arrayAdd`;
today it does not. This makes a follow-up message using the still-negative id
resolve correctly even before the client processes the commit reply. A reconnect
does not preserve that table, so the client-side remap remains authoritative.

No Code branch may use `id > 0` as a proxy for “functional.” Positive-id checks
remain only at true persistence boundaries. Existing gates are audited rather
than globally deleted: sandbox objects and client-only objects deliberately use
non-positive ids.

### 4. Retire S5c specialization

Once one handler commits create/link/unlink atomically:

1. Express the behavior using ordinary graph operations in Code.
2. Delete `NestedSetLinks`, array-valued `objectOf` mint payloads and
   `ArrayAddNestedRefTests`; the general commit scenario replaces them.
3. Remove the dedicated wrap/unwrap buttons and helpers if the generic structure
   editing surface can perform add/move/remove without editing text. If it cannot,
   keep the buttons as thin UI macros — they are then ergonomics only, with zero
   special protocol.

This last gate matters: removing a shortcut is good; removing the only usable
way to re-parent a node is not.

## Slice plan

### T1 — Atomic set move in `CommitBatch`

Add `SetUnlinkMutation`, exhaustively prevalidate the whole changeset, apply
link-before-unlink, then GC once at the end. One store test: link an existing
member into B and unlink it from A in one batch; after success it is reachable
only from B; a malformed later mutation leaves the store document/version
unchanged. No client change yet. Keep the Save-failure limitation stated rather
than overclaiming rollback.

### T2 — Commit wire vocabulary + session remap

Extend relation parsing with schema-derived `(ownerRef, prop, memberRef)` set
link/unlink and preserve the existing one-relation-per-create type-safety rule:
outgoing child links must not ambiguously type the fresh owner. Apply the exact
standalone create/delete floors before calling the store. Register every commit
result mapping in the session before replying. Server tests cover forged type,
missing owner/prop/member, access denial on either half, and use of the temp id
by an immediately following request.

### T3 — Designer client vertical slice: one handler → one commit

Aggregate only set add/remove plus scalar field writes used by the designer into
one commit request and one aggregate journal/msg id. Its undo is the constituent
undos in reverse; one reply retires the whole entry. Unsupported mixed mutation
kinds loudly abort before any send. Browser scenarios observe one version
advance, rejection rollback, and explicitly pin the existing post-write-VNA
flush behavior.

Scenario: create a wrapper object, link an existing node into its fresh
`children`, and replace the old parent membership in one handler/one commit. The
handler reads and writes the fresh object before the ack; no special nested
payload appears on the wire.

Audit the known negative-id gates (`persistFieldEdit`, staging, collection add,
reactive object keys) and pin only the paths this scenario reaches. Do not turn
this into a speculative rewrite of every id comparison.

### T4 — Delete specialization; decide the UI macros by dogfood

Delete nested-`refId` mint support and its tests. Reimplement wrap/unwrap on the
general mutations, verify behavior, then check whether palette + structure
controls expose an understandable generic re-parent operation:

- if yes, delete wrap/unwrap UI and browser scenarios;
- if no, keep them as tiny macros and record that the remaining reason is UX,
  not persistence.

No drag-and-drop, generalized command system or multi-user real-time work rides
these slices.

## Acceptance laws

- A cleanly completed handler using the supported designer mutations creates
  exactly one durable store commit.
- A non-VNA failure creates none and restores the optimistic graph; post-write
  VNA retains its explicitly tested compatibility behavior.
- Fresh objects are fully usable in the same handler that creates them.
- Temp-id remapping changes identity representation, never behavior.
- Access checks and type derivation are at least as strict as every standalone
  operation replaced.
- Link-before-unlink plus GC-after-batch cannot expose an intermediate
  unreachable object.
- No S5c-specific wire shape remains after T4.

## Explicit non-goals

- Real-time propagation or multi-user live rebasing.
- A new public transaction syntax in Code.
- Offline mutation queues.
- Replacing the storage interface or append-only log.
- Drag-and-drop or a general command framework.

## Self-grill #1 — 2026-07-11

Verdict: **REVISE; fixes folded above.** Refuted: the original “every handler”
atomicity claim (post-write VNA intentionally flushes); the assumption that
`commit` already registers session temp-id mappings; and the cheapness of
covering every mutation hook (dict/path/ref are outside the current commit
vocabulary). Security correction: atomic move needs both create and delete
floors, derived types, and full prevalidation because the store has no rollback.
Journal correction: N sends cannot share one msg id; the supported slice needs
one aggregate journal entry. Scope verdict: build the designer set/scalar
vertical slice only, reject unsupported mixtures loudly, and generalize later
only when another real consumer demands it.
