# The `password` text type — set-password as a field, not an action

**Status: spec'd 2026-06-26 (rev 5 — a `text`-based `password` type with two interceptions: blank-on-read,
hash-on-save. The rev-4 write-only-binding problem is DISSOLVED by shipping blank instead of excluding. The
general `secret`+`hash` modifiers (rev 3) stay deferred to a second secret/hash use — YAGNI). Not built;
re-review of this rev pending.** Supersedes the M-auth follow-up "set-password feedback" and the
explored-and-rejected "action half / effectful `server fn`". Design record: memory
`project_persistence_modes` ("THE EFFECTS / SERVER-MUTATION MODEL") and DECISIONS "Data-context (`ctx`)".

## Problem

Setting a password is the only "write" that isn't an ordinary field edit. Today it's a bespoke control
(`<SetPasswordControl>` + a fire-and-forget `sys.setPassword` whose `{ok}` reply is uncorrelated — only a
global error surfaces, nothing on success). The ux-reviewer flagged it twice: an immediate-action button
among Save-gated fields, with **no success feedback**.

Chasing the feedback was rejected as the "action half" (it fights `ctx`'s stage-and-commit-on-Save). A
password is a *write*, and writes belong to the data model. So it's a **field**.

## The design — a `text`-based `password` type, two chokepoints

`password` is a **`text` field everywhere** — same parser, type system, descriptor, binding, validator,
storage — **except two interception points**:

1. **Value read → UI: ship `""` (blank), not the stored hash.** The read-exclusion, expressed as
   *replace-with-empty* rather than *omit*.
2. **Value save → DB: hash it, skip empty.** PBKDF2 the typed value before storing; an empty value = no
   write = "no change."

A field is a `password` by its declared type (`password password` on User — a text-based type name the two
chokepoints key on).

**Why this is the clean version:**
- **The binding problem evaporates.** Rev 4 *excluded* the value (absent from the client), which crashes the
  input binding (`<input value={sys.field(user,"password")}>` → "Value not available" → form crash) and
  forced a write-only-lvalue interpreter primitive or a scratch-var bridge. Shipping a present `""` makes the
  password an **ordinary text input**: reads `""`, you type, it stages, on Save it's hashed. No crash, no
  workaround, no A/B decision.
- **No new base-type machinery.** It *is* `text`, so every existing text arm (coercion, validation, the
  stored leaf tag, `ConvertScalar`) already covers it — none of the `password`-case-missing crashes the
  architecture review flagged.

## The two chokepoints (precisely — this IS the security surface)

**Read → UI: blank password values.** Every path that builds a client- or condition-facing object from
stored data replaces a `password` field's value with `""`:
- `DbBridge.LoadObject` / `LoadExtent` (the data graph) — `Code/AccessFloor.cs` ~:168,202.
- `AccessFloor.ScalarObject` (`Code/AccessFloor.cs` ~:243) — builds BOTH `currentUser` (shipped in wire
  scope) AND the access-rule **candidate** at create/edit, so a rule condition never sees the password
  (plaintext OR hash).
Today these *omit* `passwordHash` by name (`UserConvention.IsHiddenField`); rev 5 *blanks* by type. **A single
uncovered path ships the hash — the load-bearing invariant.**

**Save → DB: hash, skip empty.** Put the transform at the **store seam** (`JsonFileInstanceStore.WriteField`/
`CreateObject`, keyed on the field's declared type), so EVERY write path — `objectPropChange`, `arrayAdd`, the
path-addressed `write`, `setReferenceField` create-new, set-member create, `AdminSeed` — funnels through one
hash step. The security review found three write seams that would otherwise store **plaintext**; the
store-seam chokepoint makes "no un-hashed write" structural, not enumerated. An empty value writes nothing.

## Walk down the stack

1. **Parser / type system.** `password` is a type name whose base is `text` (`ScalarBaseOf("password") →
   Text`, exactly like `enum`). Everything text-shaped already works.
2. **Descriptor.** The `password` field IS in the descriptor (text + a password marker) → renders an input.
   **Excluded from table columns + labelProp** (it ships `""`, never meaningfully displayed).
3. **Input.** A password-marked field → `<input type="password">` (masked), binds
   `value={sys.field(obj,"password")}` = `""` or the typed value — **plain two-way text binding**.
4. **ctx + commit.** Unchanged: the typed value stages; Save flushes via `objectPropChange` (`persistFieldEdit`,
   id>0). Discard/nav drops the overlay.
5. **Read chokepoint** — blank (above).
6. **Write chokepoint** — hash + skip-empty at the store seam (above).
7. **Login.** Reads the **raw store** field (the real hash — the store keeps it; only the value *headed to a
   client/condition* is blanked). Found by the User's password-typed field.
8. **Create + edit.** Both plain text: the create draft (`sys.new`) starts `""`, the edit ships `""`; both
   bindable; both hashed at the store seam. Set-password works in the **create form** and on the **edit page**.

## Security invariants (a credential feature — load-bearing)

- **The hash never reaches the client:** the read chokepoints ship `""`. "Blank" is **security-equivalent** to
  today's "omit" — neither ships the hash, both require every value-shipping path to handle the field — and
  blank also makes the input bind for free.
- **No plaintext stored:** the store-seam hash covers every write path; an un-hashed write is structurally
  impossible.
- **Plaintext transient + in-transit only:** typed → ctx / create draft (client) → `objectPropChange`/
  `arrayAdd` (WS, TLS) → hashed at the store seam → dropped. The create draft holds plaintext **client-only**
  (never an SSR-shipped value — note for any future SSR-seeded-draft feature).
- **Floor-gated:** the write floor gates the write; visibility = the form's `canEdit`.
- **No echo:** create/edit replies carry ids, not values (verified in review).

## Decided

- `password` = a **text-based type** (not a new base-type *kind*; not the general `secret`/`hash` modifiers —
  deferred to a second use, per the `Multiline` "single bool, generalize on a second use" precedent).
- **Two chokepoints**; the field **binds as plain text** — the rev-4 A/B binding question is moot.
- **`User` stays explicitly declared** — swap `passwordHash text` → `password password`. (Implicit User fields
  need a non-existent inject/merge seam — a separate later slice.)
- **Visibility = `canEdit`** (write-only field shown only if writable; non-admins can't reach the page anyway;
  the write floor re-decides at commit).
- **Migration = rename** `passwordHash → password` (text→text, no new stored tag; hashes drop, admin re-seeds
  on boot, UI users re-set; the committed instance-5 `app.app` line changes in the same commit; flag for
  deploy).
- **Excluded from table columns / labelProp.**

## What it replaces / deletes

- `<SetPasswordControl>`; `sys.setPassword` (twin builtin + `setPassword` WS op + `SetPasswordResponse` +
  client reply handler); the orphaned `.set-password` CSS (`Http/SsrRenderer.cs`).
- `UserConvention.PasswordHashField` / `IsHiddenField` (name) → the by-type blank chokepoints + the store-seam
  hash.
- The regression test asserting no `passwordHash` in `currentUser` (goes vacuous on rename) → re-point to
  assert the `password` field ships `""` / no `pbkdf2$` marker.
- M-auth e2e retargets: set the password as a **form field** (edit or create), Save.

## Slices

**Slice 1 — the `password` text type + the two chokepoints (edit AND create).** End to end: the text-based
type → blank-on-read (`DbBridge` + `ScalarObject`) → hash-on-save-skip-empty (store seam) → descriptor/Input
(masked, columns-excluded) → login-by-type → the deletions → migration → e2e retarget.
- *Gherkin (edit):* an admin types a new password on a user's page, Saves, and the user logs in with it; AND
  the rendered document ships `""`/no hash for the field.
- *Gherkin (create):* an admin creates a user with name + password in one form, and that user logs in.

**Slice 2 — form Save feedback.** "Saving… → Saved / Couldn't save" by rendering the commit lifecycle (journal
drain on ack / rollback on reject — a render over existing state, not a new async channel). Closes the
original feedback gap, for every form.
- *Gherkin:* a successful Save shows a confirmation; a floor-denied write shows an error, store unchanged.

## Open questions

- **Save-feedback signal (slice 2)** — observe the journal drain/rollback (preferred) vs a `ctx.commit`
  lifecycle.
