# The `password` type — set-password as a field, not an action

**Status: spec'd 2026-06-26 (rev 4 — a single built-in `password` type; the general `secret`+`hash` modifiers
explored in rev 3 are deferred to the day a second secret/hash field exists — YAGNI), not built.** Supersedes
the M-auth follow-up "set-password feedback (reply↔control correlation)" and the explored-and-rejected
"action half / effectful `server fn`" direction. Design record: the memory `project_persistence_modes` ("THE
EFFECTS / SERVER-MUTATION MODEL") and DECISIONS "Data-context (`ctx`)".

## Problem

Setting a password is the only "write" that isn't an ordinary field edit. Today it's a bespoke control
(`<SetPasswordControl>` + a fire-and-forget `sys.setPassword` whose `{ok}` reply is uncorrelated — only a
global error surfaces, nothing on success). The ux-reviewer flagged it twice: an immediate-action button
among Save-gated fields, with **no success feedback**.

Chasing the feedback led down a long path — server-authoritative action execution, reply↔control correlation,
an intent-id idempotency key — all **rejected**, because it fights `ctx`: writes stage and commit on Save. A
password is a *write*, and writes belong to the data model. So it's a **field**.

## The design — a built-in `password` type

A built-in scalar type **`password`**: *write-only (hidden / read-excluded) + PBKDF2-hashed on write*. One
type, the one behavior a password needs.

- **Why a type, not the general `secret`+`hash` modifiers (rev 3):** there's exactly one consumer (the
  password). The modifiers were a general mechanism for a single use — speculative. `password` is honest
  (a password genuinely *is* hidden and hashed — no ambiguity, unlike rev-2's `hash`, whose name didn't
  connote "hidden") and minimal. **When a second secret/hash field appears**, generalize to `secret`
  (read-excluded) + `hash` (hash-on-write, *requires* `secret`) — that rev-3 design, deferred, not deleted.

- **Implicit on User.** The framework bakes the `User` type into every instance (M-auth). `name` and
  `password` are **framework-provided — the app author declares neither**; the app description never mentions
  them (the author may extend `User` with their own fields + the role enum):

  ```
  # the framework supplies, implicitly, the equivalent of:
  #     name text
  #     password password
  # the app writes only its own additions (role, profile fields, …)
  ```

The payoff: it reuses the field / `ctx` / Save machinery wholesale; the new code is one built-in type threaded
through the stack, plus making the User credential field implicit (it mostly already is — M-auth bakes User).

## Walk down the stack

1. **Type system / parser.** A built-in scalar type `password` (alongside `text`/`int`/…). Semantics: stored
   as a PBKDF2 string, **write-only**, **hash-on-write**. Algorithm is the framework's (`AuthCrypto`).

2. **Read exclusion (THE security invariant).** A `password`-typed field's *value* never enters the shipped
   graph — the read floor / `DbBridge` excludes it **by type**, generalizing today's
   `UserConvention.IsHiddenField` from the `passwordHash` *name* to the `password` *type*. **A `password` value
   that ever ships is the whole failure mode.**

3. **Descriptor.** A `password` field IS in the type descriptor (so the form renders an input), value-excluded
   — contrast today, where `passwordHash` is excluded from the descriptor entirely.

4. **Generic UI — `Input` (`GenericUi.cs` ~:127).** `baseType: "password"` → `<input type="password">`,
   masked + **write-only** (no `value={sys.field(...)}` binding — the value never ships anyway). Renders only
   when the field is writable (the form's existing `canEdit` — a write-only field you can't write is hidden).
   `Field` otherwise unchanged.

5. **`ctx` staging + commit.** `user.password = "…"` stages by prop name; **Save** flushes via the normal
   `objectPropChange` path (`persistFieldEdit`, id>0 — `codeExec.ts` ~:467). No special path; Discard/nav
   drops the overlay (plaintext gone).

6. **Server write (by type) — both edit AND create.** A shared "hash `password`-typed fields before store"
   step runs in **both** write paths: `HandleObjectPropChange` (edit, `WsHandler.cs` ~:294) and
   `HandleArrayAdd` (create — the new object's field values). Each applies `AuthCrypto.Hash`, driven by the
   field's TYPE, floor-gated identically to any write. Plaintext hashed and dropped; never echoed (the create
   reply carries the new id, not the values). So a password can be set when **editing** an existing user *or*
   **creating** a new one — the create-form renders the `password` field generically, the draft holds the
   typed value, `arrayAdd` sends it, the server hashes it.

7. **Login.** Verifies a sign-in against `User`'s `password`-typed field (the credential) — found by type
   (`User`'s `password` field), so even "which field authenticates" stops being a name convention.

8. **Implicit User fields.** `name` + `password` are injected into the baked-in `User` type by the framework;
   the app declares neither. (Rides the existing M-auth User-baking seam.)

## Security invariants (a credential feature — load-bearing)

- **A `password`-typed field's value never enters the shipped graph** (read floor + descriptor + Input all
  exclude it) — the one invariant that must be airtight.
- **Hashed server-side, never stored plaintext:** the plaintext is PBKDF2'd on write and dropped.
- **Plaintext transient + in-transit only:** `ctx` overlay (client memory) → `objectPropChange` (WS, TLS) →
  hashed → dropped. Never shipped back.
- **Floor-gated** by the `objectPropChange` write floor — identical to today.
- **Discarded on Discard/nav** with the overlay.

## What it replaces / deletes

- `<SetPasswordControl>`; `sys.setPassword` (twin builtin + the `setPassword` WS op + `SetPasswordResponse` +
  the client reply handler).
- `UserConvention.PasswordHashField` / `IsHiddenField` (name-convention) → the `password` type's by-type
  read-exclusion.
- The baked-in `User` type's `passwordHash` → an implicit `password`-typed field. **Migration: rename — existing
  hashes drop, the admin re-seeds on boot (`AdminSeed` env var) and UI-created users re-set. Flag for the
  deploy.**
- The M-auth e2e retargets: set the password as a **field on `/users/<id>`**, then **Save**.

## Decided

- **`password` type** (not the general `secret`/`hash` modifiers — deferred to a second use).
- **Field visibility = the form's `canEdit`** (a write-only field renders only if writable, else hidden; the
  read floor already blocks non-admins from the User page; the write floor re-decides at commit).
- **Migration = rename** `passwordHash → password` (passwords reset; flag for deploy).
- **Implicit User fields** — `name` + `password` not declared in the app.

## Slices

**Slice 1 — the `password` type (set-password becomes a field), edit AND create.** The built-in type end to
end (parse → type-system → by-type read-exclusion → descriptor → `Input` → server hash-on-write in **both**
`objectPropChange` and `arrayAdd` → login by type), the implicit `User.password`, the deletions, the e2e
retarget. Set/change a password works through the form; re-login proves it.
- *Gherkin (edit):* an admin types a new password on an existing user's page, clicks **Save**, the user logs
  in with it; AND the page never ships a password value (empty on load, never in the document).
- *Gherkin (create):* an admin creates a user with a name + password in one form, and that user can log in —
  no separate set-password step.

**Slice 2 — form Save feedback.** "Saving… → Saved / Couldn't save" by rendering the commit lifecycle (the
journal drains on ack / rolls back on reject — a render over existing state, NOT a new async channel). Covers
the password's write-only confirmation and every form.
- *Gherkin:* a successful Save shows a confirmation; a floor-denied write shows an error, store unchanged.

## Open questions

- **Save-feedback signal (slice 2)** — observe the journal drain/rollback (preferred) vs a `ctx.commit`
  lifecycle.
