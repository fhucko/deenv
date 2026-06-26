# `hash` + `secret` field modifiers — set-password as a field, not an action

**Status: spec'd 2026-06-26 (rev 3 — two orthogonal modifiers `hash` + `secret`, replacing rev-2's single
`hash` type that wrongly bundled "hidden"), not built.** Supersedes the M-auth follow-up "set-password
feedback (reply↔control correlation)" and the explored-and-rejected "action half / effectful `server fn`"
direction. Design record: the memory `project_persistence_modes` ("THE EFFECTS / SERVER-MUTATION MODEL") and
DECISIONS "Data-context (`ctx`)".

## Problem

Setting a password is the only "write" that isn't an ordinary field edit. Today it's a bespoke control
(`<SetPasswordControl>` + a fire-and-forget `sys.setPassword` whose `{ok}` reply is uncorrelated — only a
global error surfaces, nothing on success). The ux-reviewer flagged it twice: an immediate-action button
among Save-gated fields, with **no success feedback**.

Chasing the feedback led down a long path — server-authoritative action execution, reply↔control correlation,
an intent-id idempotency key — all **rejected**, because it fights `ctx`: writes stage and commit on Save, so
a password is a *write*, and writes belong to the data model. So it's a **field**.

## The design — two orthogonal field modifiers, `hash` + `secret`

The behavior decomposes into two independent, composable modifiers:

- **`hash`** — *hash-on-write*: the written value is PBKDF2-hashed server-side (`AuthCrypto`); the stored value
  is the hash. Says nothing about visibility — a `hash` value still ships unless also `secret`.
- **`secret`** — *hidden / read-excluded*: the value never ships to the client (write-only from the client's
  side). Generalizes today's system-only / `UserConvention.IsHiddenField` hiding. Says nothing about
  transformation.

A password is **both**. The framework already bakes the `User` type into every instance (M-auth), so the
baked-in schema is just:

```
User
    name text
    role Role
    password hash secret
```

**Composition (why two, not one):**
- `secret` alone → a hidden plaintext stored as-is (an **API token**).
- `hash` alone → a hashed value that *does* ship (a **content fingerprint / etag**).
- `hash secret` → a **password** (hashed *and* hidden).

The app author never writes the `User` line (baked in), but the modifiers are general — any app can mark its
own fields `hash` and/or `secret`. Password management stops being password-specific; `User` just *uses* the
modifiers. (Grammar — whether these are flags on a `text` base, e.g. `password text hash secret`, or `hash`
is a scalar type carrying a `secret` flag — is a parser detail to settle in the build; the model is the two
orthogonal concerns.)

The payoff stays: it reuses the field/`ctx`/commit machinery wholesale; the new code is the two modifiers
threaded through the stack.

## Walk down the stack

1. **Type system / parser.** A `text` field gains two optional modifiers, `hash` and `secret` (composable).
   `hash` = hash-on-write; `secret` = read-excluded. Algorithm is the framework's (`AuthCrypto`, PBKDF2) — not
   parameterized (YAGNI).

2. **Read exclusion = `secret` (THE security invariant).** A `secret` field's *value* never enters the shipped
   graph — the read floor / `DbBridge` excludes it by the modifier, generalizing today's `IsHiddenField`
   (name-convention) into a field rule. **A `secret` field that ever ships is the whole failure mode.**

3. **Descriptor.** A `secret` field IS in the descriptor (so the form renders an input) but value-excluded; a
   `hash` field carries its write-transform marker. The descriptor exposes the field's *existence + modifiers*
   (schema), never a value.

4. **Generic UI — `Input` (`GenericUi.cs` ~:127).** A `secret` field → `<input type="password">`, masked +
   **write-only** (no `value={sys.field(...)}` binding — the value never ships anyway). `hash` does not affect
   the input (it drives the server). For a password (`hash secret`): masked write-only input, hashed on the
   server. `Field` otherwise unchanged.

5. **`ctx` staging + commit.** `user.password = "…"` stages by prop name; **Save** flushes via the normal
   `objectPropChange` path (`persistFieldEdit`, id>0 — `codeExec.ts` ~:467). No special path; Discard/nav
   drops the overlay (plaintext gone).

6. **Server write = `hash` (by modifier).** `HandleObjectPropChange` (`WsHandler.cs` ~:294, already
   floor-gated), for a `hash` field, applies `AuthCrypto.Hash` and stores the hash — driven by the field's
   modifier, not `UserConvention`. Floor-gated identically to any prop change. Plaintext hashed and dropped;
   reply is `{ok}` (no value back).

7. **Login.** Verifies a sign-in against `User`'s stored credential field. It needs to know *which* field —
   name it (`User.password`) or find `User`'s single `hash secret` field by type (more generic, mirrors the
   by-type `usersPath` fix). Only "which field authenticates" stays a `User` convention; the modifiers are
   generic.

## Security invariants (a credential feature — load-bearing)

- **`secret` is the read-exclusion invariant:** a `secret` field's value **never enters the shipped graph**
  (read floor + descriptor + Input all exclude it). This is the one that must be airtight.
- **`hash` is the write transform + defense-in-depth:** the plaintext is PBKDF2'd server-side and dropped, so
  the stored value is a hash, never plaintext. (Even if a `secret hash` value somehow leaked, it's a hash, not
  the password — but `secret` is what actually hides it.)
- **Plaintext transient + in-transit only:** `ctx` overlay (client memory) → `objectPropChange` (WS, TLS) →
  hashed server-side → dropped. Never stored plaintext, never shipped back.
- **Floor-gated** by the `objectPropChange` write floor — identical to today.
- **Discarded on Discard/nav** with the overlay.

## What it replaces / deletes

- `<SetPasswordControl>`; `sys.setPassword` (twin builtin + the `setPassword` WS op + `SetPasswordResponse` +
  the client reply handler).
- `UserConvention.PasswordHashField` / `IsHiddenField` (name-convention) → generalized into the `secret`
  modifier (read-excluded) + the `hash` modifier (transform) + a thin "which field authenticates" convention.
- The baked-in `User` type gains `password hash secret`; the old `passwordHash` stored field is gone.
- The M-auth e2e retargets: set the password as a **field on `/users/<id>`**, then **Save**.

## Slices

**Slice 1 — the `hash` + `secret` modifiers (set-password becomes a typed field).** Both modifiers end to
end (parse → type-system → `secret` read-exclusion → descriptor → `Input` → `hash` server hash-on-write →
login reads the credential), the baked-in `User` schema using them, the deletions, the e2e retarget.
Set-password works through the form; re-login proves it.
- *Gherkin:* an admin types a new password into the User form's password field, clicks **Save**, the user logs
  in with it; AND the page never ships a password value (the field is empty on load, the value never in the
  document).
- Chunky but coherent. Could split "`secret` (read-exclusion, the security half)" from "`hash` (the
  transform)" if it needs thinning — `secret` is the harder, security-critical half.

**Slice 2 — form Save feedback.** "Saving… → Saved / Couldn't save" by rendering the commit lifecycle (the
journal drains on ack / rolls back on reject — a render over existing state, NOT a new async channel). Covers
the password's write-only confirmation and every other form.
- *Gherkin:* a successful Save shows a confirmation; a floor-denied write shows an error and leaves the store
  unchanged.

## Decided

- **Field visibility = the form's existing `canEdit`.** A `secret` (write-only) field renders only if you can
  write it (`sys.canWrite("User","edit")`), else it's **hidden** (a disabled write-only input is pointless —
  unlike a normal field, which goes readonly). This subsumes "admin-only," reuses the form's existing check,
  and is robust if rules ever split read-from-edit (a read-only User viewer correctly sees no password field).
  The non-admin case is moot anyway — the read floor blocks them from the User page entirely. The
  `objectPropChange` write floor re-decides at commit regardless (the real boundary). No separate
  `canManageUsers` check. *(Per-field "edit user but not password" gating is a later refinement; the form gates
  type-level today.)*
- **Migration = rename `passwordHash → password`.** Existing hashes drop; the admin re-seeds on boot
  (`AdminSeed` env var) and UI-created users re-set their passwords. Acceptable at the dev/dogfood stage —
  **flag it for the deploy** (set `DEENV_ADMIN_PASSWORD`, re-set any other users).

## Open questions

- **Which field authenticates** — name (`User.password`) or by type (`User`'s `hash secret` field)? Lean
  by-type if cheap (mirrors the `usersPath` fix).
- **Create-with-password** — a *new* user (transient id<0) setting a password at creation; defer (edit-only
  first).
- **Save-feedback signal (slice 2)** — observe the journal drain/rollback (preferred) vs a `ctx.commit`
  lifecycle.
