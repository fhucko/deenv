# The `hash` type — set-password as a field, not an action

**Status: spec'd 2026-06-26 (rev 2 — a `hash` type, replacing the rev-1 `secret` flag), not built.**
Supersedes the M-auth follow-up "set-password feedback (reply↔control correlation)" and the
explored-and-rejected "action half / effectful `server fn`" direction. Design record: the memory
`project_persistence_modes` ("THE EFFECTS / SERVER-MUTATION MODEL") and DECISIONS "Data-context (`ctx`)".

## Problem

Setting a password is the only "write" that isn't an ordinary field edit. Today it's a bespoke control
(`<SetPasswordControl>`: an input + a button firing `sys.setPassword`, a fire-and-forget WS op whose `{ok}`
reply is uncorrelated — only a global error surfaces, nothing on success). The ux-reviewer flagged it twice:
an immediate-action button among Save-gated fields, with **no success feedback**.

Chasing the feedback led down a long path — server-authoritative action execution, reply↔control
correlation, an intent-id idempotency key — all **rejected**, because it fights `ctx`: writes don't save
immediately, they **stage and commit on Save**. A password is a *write*, and writes belong to the data
model. So it should be a **field**.

## The design — a `hash` type

The behavior is a **new built-in scalar type, `hash`**: *write-only + PBKDF2-hashed on write*. Declared in
the schema. The framework already bakes the `User` type into every instance (M-auth), so the baked-in schema
is just:

```
User
    name text
    role Role
    password hash
```

- **`password hash`** = write-only (never reads back), hashed server-side on write, stored in place as the
  PBKDF2 string. One field — no separate `passwordHash`.
- **Why a type, not a `secret` flag:** a flag conflates two unrelated things — "sensitive/write-only" and
  "PBKDF2-hashed" (a stored API token is the former but must NOT be the latter). The `hash` type *names* the
  behavior; a non-hashed write-only secret would be a *different* type, later. No ambiguity.
- **Generic + implicit.** The app author never writes this line — it's in the framework's baked-in `User`
  schema. But the mechanism is general: any app could declare a `hash` field for its own secret. Password
  management stops being password-specific; `User` just *uses* the type.

The payoff stays: it reuses the field/`ctx`/commit machinery wholesale. The genuinely new thing is one
built-in type, threaded through the stack.

## Walk down the stack

1. **Type system / parser.** A new base type `hash` alongside `text`/`int`/`bool`/`decimal`/`date`/`enum`.
   `password hash` parses as a `hash`-typed prop. The type carries three semantics: stored as a text PBKDF2
   string, **write-only** (never shipped), **hash-on-write**. Algorithm is the framework's (`AuthCrypto`,
   PBKDF2) — not parameterized (YAGNI).

2. **Read exclusion — BY TYPE.** A `hash` field's *value* never enters the shipped graph. The read floor /
   `DbBridge` excludes it because the field is type `hash` — generalizing today's
   `UserConvention.IsHiddenField` exclusion of `passwordHash` from a name-convention to a type rule.

3. **Descriptor.** A `hash` field IS in the type descriptor (`baseType: "hash"`) so the form renders an input
   — contrast today, where `passwordHash` is excluded from the descriptor entirely. The descriptor exposes the
   field's *existence + type* (schema), never its value.

4. **Generic UI — `Input` (`GenericUi.cs` ~:127).** `baseType: "hash"` → `<input type="password">`,
   **write-only**: it does not bind `value={sys.field(obj, name)}` (the stored hash never ships anyway).
   Typing stages into `ctx`. `Field` otherwise unchanged.

5. **`ctx` staging + commit.** `user.password = "…"` stages by prop name like any scalar; **Save**
   (`ctx.commit()`) flushes via the normal `objectPropChange` path (`persistFieldEdit`, id>0 User —
   `codeExec.ts` ~:467). No special path; Discard/nav drops the overlay (plaintext gone).

6. **Server write — BY TYPE (`HandleObjectPropChange`, `WsHandler.cs` ~:294, already floor-gated).** For a
   `hash`-typed field, the server applies `AuthCrypto.Hash` and stores the hash — driven by the field's TYPE,
   not `UserConvention`. Floor-gated identically to any prop change (the same `User edit` gate as today's
   `HandleSetPassword`). Plaintext hashed and dropped; the reply is `{ok}` (no value back).

7. **Login.** Verifies a sign-in against `User`'s stored `hash` field. It needs to know *which* field is the
   credential — a thin convention (name it `User.password`, or find `User`'s single `hash`-typed field by
   type, mirroring the by-type `usersPath` fix). The credential *mechanism* (the `hash` type) is fully
   generic; only "which field is the login secret" stays a `User` convention.

## Security invariants (a credential feature — load-bearing)

- **Plaintext transient + in-transit only:** the `ctx` overlay (client memory) → the `objectPropChange` (WS,
  TLS in prod) → hashed server-side → dropped. **Never stored plaintext; never shipped back.**
- **A `hash` field's value never enters the shipped graph** (read-excluded by type) — generalizes the
  `passwordHash` exclusion. Write-only end to end.
- **The descriptor exposes the field's existence + type, never a value** (schema, like any field name).
- **Floor-gated** by the `objectPropChange` write floor — identical to today.
- **Discarded on Discard/nav** with the overlay.

## What it replaces / deletes

- `<SetPasswordControl>`; `sys.setPassword` (twin builtin + the `setPassword` WS op + `SetPasswordResponse` +
  the client reply handler).
- `UserConvention.PasswordHashField` / `IsHiddenField` (name-convention) → generalized into "a `hash` field is
  write-only + read-excluded **by type**" + a thin "which field authenticates" convention.
- The baked-in `User` type gains `password hash`; the old `passwordHash` stored field is gone.
- The M-auth e2e retargets: set the password as a **field on `/users/<id>`**, then **Save**.

## Slices

**Slice 1 — the `hash` type (set-password becomes a typed field).** The `hash` base type end to end (parse →
type-system → read-exclusion-by-type → descriptor → `Input` → server hash-on-write → login reads the
credential), the baked-in `User` schema using it, the deletions, the e2e retarget. Set-password works through
the form; re-login proves it.
- *Gherkin:* an admin types a new password into the User form's password field, clicks **Save**, and the user
  can then log in with it; AND the page never ships a password value (the field is empty on load).
- Chunky but coherent — a half-built `hash` type isn't useful. Could split "the type itself" from "`User`
  uses it" if it needs thinning.

**Slice 2 — form Save feedback.** The form shows "Saving… → Saved / Couldn't save" by rendering the commit
lifecycle (the journal drains on ack / rolls back on reject — a render over existing state, NOT a new async
channel). Covers the password's write-only confirmation and every other form.
- *Gherkin:* a successful Save shows a confirmation; a floor-denied write shows an error and leaves the store
  unchanged.

Slice 1 is worth landing alone (kills the bespoke control, fully on-model); slice 2 is the feedback the whole
thread was originally about.

## Open questions

- **Who sees the field?** admin-only (`canManageUsers`) for the first slice; self-service "change my own"
  (which wants current-password confirmation) is separate and later.
- **Which field authenticates** — name it (`User.password`) or find `User`'s single `hash` field by type
  (more generic, mirrors `usersPath`)? Lean by-type if cheap.
- **Create-with-password** — a *new* user (transient id<0, staging gated off) setting a password at creation
  needs handling the edit path doesn't. Defer; first slice is **edit** an existing user.
- **The Save-feedback signal (slice 2)** — observe the journal drain/rollback (preferred), or a `ctx.commit`
  lifecycle? Confirm during the build.
- **Migration.** Renaming the stored field `passwordHash → password` drops existing hashes; the admin
  re-seeds on boot (`AdminSeed` env var) and any UI-created users re-set their passwords — fine for the
  single-operator MVP, but flag it for the deploy. (Alternative: keep the stored name `passwordHash` typed as
  `hash` to avoid the reset — a build-time call.)
