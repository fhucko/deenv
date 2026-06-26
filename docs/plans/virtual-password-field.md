# Virtual password field — set-password as a field, not an action

**Status: spec'd 2026-06-26, not built.** Supersedes the M-auth follow-up "set-password feedback
(reply↔control correlation)" and the explored-and-rejected "action half / effectful `server fn`"
direction. Design record: the memory `project_persistence_modes` ("THE EFFECTS / SERVER-MUTATION MODEL")
and DECISIONS "Data-context (`ctx`)".

## Problem

Setting a user's password is the only "write" in the system that isn't an ordinary field edit. Today it's
a bespoke control (`<SetPasswordControl>`: an input + a button that fires `sys.setPassword`, a fire-and-forget
WS op whose `{ok}` reply is uncorrelated — only a global error surfaces, nothing on success). The
ux-reviewer flagged this twice: an immediate-action button sitting among Save-gated fields, with **no success
feedback**.

Chasing the feedback led down a long path — server-authoritative action execution, reply↔control
correlation, an intent-id idempotency key — all **rejected**, because it fights `ctx`: writes don't save
immediately, they **stage and commit on Save**. The resolution: a password is a *write*, and writes belong
to the data model. So it should be a **field**, not an action.

## The design

`password` is a **virtual, write-only field on `User`** (a `UserConvention` field, like `passwordHash` is a
`UserConvention` field today — not a general "virtual field" feature; the only one is the password):

- **Virtual** — not stored. `passwordHash` is the stored field (system-only, never shipped, unchanged).
  `password` is a write-only setter that the server hashes into `passwordHash`.
- **Write-only** — the input renders empty and never reads back a value (there is none to read; the stored
  hash stays system-only). Typing it stages a value; Save sends it; it never returns.
- **An ordinary field otherwise** — it renders in the User form, stages in `ctx`, and commits on **Save**,
  exactly like `name`/`role`. The feedback is the form's Save feedback. No `sys.setPassword`, no special
  control, no action-half.

The whole point: it reuses the field/`ctx`/commit machinery wholesale; the only genuinely new code is a
descriptor injection, an `Input` case, and a one-line server-side transform.

## Walk down the stack (each piece, and how little is new)

1. **Convention + descriptor.** `UserConvention` gains the virtual field name `password` and the mapping
   `password → passwordHash`. The descriptor builder (`GenericUi.Descriptors`/`TypeDescriptor`) **injects** a
   synthetic `password` prop into the `User` type descriptor (today it *excludes* `passwordHash` via
   `IsHiddenField`; this adds a sibling), flagged `secret: true` on the prop descriptor. *New: ~the injection
   + the flag.*

2. **Generic UI — `Input` (`GenericUi.cs` ~:127).** A prop with `secret: true` renders
   `<input type="password">` and is **write-only**: it does not bind `value={sys.field(obj, name)}` to the
   stored value. (It may bind the *staged* value so typing persists across re-renders — `sys.field` reads the
   `ctx` overlay first, and the only thing there is the client's own in-progress entry; the stored hash is a
   different field and system-only, so this never reads the secret.) `Field` is otherwise unchanged. *New: one
   `Input` branch.*

3. **`ctx` staging.** `user.password = x` stages in the `ctx` overlay by prop name, like any scalar edit —
   no special path. Discard/nav drops the overlay (the plaintext is gone). *New: nothing.*

4. **Commit → wire.** On **Save**, `ctx.commit()` flushes staged scalar edits via `persistFieldEdit` →
   `objectPropChange` (`codeExec.ts` ~:467; id>0 User). So `password` flushes as an ordinary
   `objectPropChange { id, field: "password", value: <plaintext> }`. *New: nothing.*

5. **Server transform (`HandleObjectPropChange`, `WsHandler.cs` ~:294, already floor-gated).** When the field
   is `User.password`, the server **hashes** the value into `passwordHash` (the existing `HandleSetPassword`
   logic — `AuthCrypto.Hash` + `_store.WriteField(passwordHash, …)`) instead of writing a literal `password`
   field, gated by the **same** write floor (`User edit`) as any prop change. The plaintext is hashed and
   dropped; never stored, never echoed (the `objectPropChange` reply is `{ok}` only — no value back). *New: a
   `UserConvention.IsHiddenField`-style branch in the write path.*

6. **Feedback = the form's Save feedback.** A write-only field has no visible value to revert, so the form
   must show whether the commit landed: "Saving…" → "Saved" / "Couldn't save." This is **general form-Save
   feedback** (every form wants it; the password merely *requires* it), and it observes the existing commit
   lifecycle (the journal drains on ack / rolls back on reject) — it is a render over state the client already
   tracks, NOT a new async channel. *New: the form renders the commit lifecycle.* (See slice 2 + Open
   questions — the exact signal is the one real design decision left.)

## Security invariants (a credential feature — these are load-bearing)

- **Plaintext is transient + in-transit only.** It lives in the `ctx` overlay (client memory) until Save,
  rides the `objectPropChange` (WS, TLS in prod), is hashed server-side, and dropped. **Never stored
  plaintext; never shipped back.**
- **`passwordHash` stays system-only — unchanged.** Still excluded from every descriptor and the data graph
  (`IsHiddenField`). The virtual `password` field carries no readable value either (write-only).
- **Floor-gated.** The `objectPropChange` write floor (`User edit`) gates it — identical to today's
  `HandleSetPassword` gate. "Who may set a password" stays an ordinary access rule.
- **Discarded on Discard/nav** with the rest of the overlay.

## What it replaces / deletes

Set-password stops being its own mechanism:
- `<SetPasswordControl>` — deleted (the field renders via the generic `Field`/`Input`).
- `sys.setPassword` builtin (twin: `execSetPassword`/`CodeExecutor` branch) + the `setPassword` WS op +
  `SetPasswordResponse` + the client `setPassword` reply handler — deleted, OR the `HandleSetPassword` hash
  logic is moved into the `objectPropChange` transform and the op retired.
- The M-auth e2e (`Access.feature` "creates a user and sets a password") retargets: set the password as a
  **field on `/users/<id>`**, then **Save**.

## Slices

**Slice 1 — the virtual field (set-password becomes a field).** Descriptor injection + the `secret` `Input`
case + the server transform + the deletions. Set-password works through the form: type it, Save, it persists;
re-login proves it. (No explicit Save *confirmation* yet — same feedback gap as today, but now on-model.)
- *Gherkin:* an admin opens a user's page, types a new password into the password field, clicks **Save**, and
  the user can then log in with it. And: the page never ships the stored hash (the field is empty on load).

**Slice 2 — form Save feedback (closes the original gap, generally).** The form shows Saving… → Saved /
Couldn't-save by rendering the commit lifecycle. Covers the password's write-only confirmation and every other
form.
- *Gherkin:* a successful Save shows a "Saved" confirmation; a floor-denied write shows "Couldn't save" and
  the store is unchanged.

Slice 1 is worth landing on its own (it kills the bespoke control and is fully on-model); slice 2 is the
feedback the whole thread was originally about.

## Open questions

- **Who sees the field?** Gate the field's render on `canManageUsers` (admin-only, matching today), or let the
  write floor decide (the field shows whenever you can edit the User)? Self-service "change my own password"
  (which wants current-password confirmation) is a separate, later thing — keep the first slice admin-only.
- **Create-with-password.** A *new* user (transient id<0, staging gated off → writes go live) setting a
  password at creation needs handling the edit path doesn't. Defer to a follow-on; first slice is **edit** an
  existing user.
- **The Save-feedback signal (slice 2).** Observe the journal drain/rollback, or have `ctx.commit` expose a
  pending→ok/error lifecycle the form renders? The journal-observation route adds no new async channel and is
  preferred; confirm during the build.
- **`secret` field flag — name + shape.** A prop-descriptor `secret: true` (this spec) vs a `password`
  baseType. The flag is more general (any future write-only secret) and doesn't pretend "password" is a scalar
  type.
