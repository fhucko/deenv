# The `password` type — set-password as a field, not an action

**Status: slice 1 DONE 2026-06-26 (`d2e1503`/`79bc1b1`).** The `password` type (`BaseType.Password`) +
the load-blank / WS-layer hash chokepoints + the `dict of password` value-blank + the
`password`-as-dict-key forbid + the `initialData` forbid + the descriptor/`Input` masking + login-by-type
+ the deletions (`sys.setPassword`, the `setPassword` WS op + `SetPasswordResponse` + client handler,
`<SetPasswordControl>`, `UserConvention.PasswordHashField`/`IsHiddenField`, the `.set-password` CSS) all
landed; the `dict` blank + key-forbid were folded in during review. **Slice 2 (the form-Save feedback —
"Saving… → Saved / Couldn't save") is the remaining piece.** (Spec'd 2026-06-26, rev 6 — both review
passes folded in.) Supersedes the
M-auth follow-up "set-password feedback" and the explored-and-rejected "action half / effectful `server fn`".
Design record: memory `project_persistence_modes` ("THE EFFECTS / SERVER-MUTATION MODEL"), DECISIONS
"Data-context (`ctx`)". Rev history: 1 secret-flag → 2 hash-type → 3 hash+secret modifiers → 4 password-type
→ 5 text-based + 2 chokepoints (dissolved the binding crash) → **6 BaseType.Password + WS-layer hash +
complete blank coverage** (this rev).

## Problem

Setting a password is the only "write" that isn't an ordinary field edit. Today it's a bespoke control
(`<SetPasswordControl>` + a fire-and-forget `sys.setPassword` whose `{ok}` reply is uncorrelated — only a
global error surfaces, nothing on success). The ux-reviewer flagged it twice: an immediate-action button
among Save-gated fields, with **no success feedback**. Chasing the feedback as a server-action was rejected
(it fights `ctx`'s stage-and-commit-on-Save). A password is a *write* → it's a **field**.

## The design — a `password` type that behaves as text, with two chokepoints

`password` is a scalar type whose value **behaves exactly like `text` everywhere** — parser, binding,
descriptor, validator, storage, transfer — **except two interception points**:

1. **Read → UI: ship `""` (blank), not the stored hash** — at the load boundary.
2. **Save → DB: PBKDF2-hash it, skip empty** — at the WS-handler layer (above the store).

A field is a `password` by its declared type (`password password` on User). **Shipping a present `""`
(rather than omitting the field) is the key move: it makes the input bind as ordinary text — no crash, no
write-only-lvalue primitive, no scratch-var bridge** (the rev-4 A/B question is moot; confirmed twin-clean).

### Mechanism: `password` is a `BaseType.Password` member that maps to `text`

Not "a magic text type-name" (rev 5 overstated this — a text-alias breaks `AppPrint` round-trip; a
`BaseTypes` registration makes `NameOf(Text)` ambiguous). The honest analogue of `enum`:
- A new **`BaseType.Password` member** (`InstanceDescription.cs`), declared `name password` (`AppParse`/
  `AppPrint` handle it, round-trips).
- It **maps to `Text`** in the value switches — `ScalarBaseOf`/`LeafBase`/`BaseTag` each get one
  `Password → Text` arm (exactly the `enum → Text` pattern, `InstanceDescriptionQuery.cs:31`). So every
  existing `text`/`Text` arm (coercion, validation, the stored leaf tag, `ConvertScalar`) covers it — **no
  crashes**, no per-switch password logic.
- The two chokepoints key on `BaseType.Password`; everything else sees text. ~3-4 mapping arms + parse/print
  — small, but real (not zero).

## The two chokepoints (the security surface — must be COMPLETE)

**Read → UI: blank every leaf the load materializes for a `password` field.** Express as "any
`password`-typed leaf the load turns into a shipped value," ideally one low-level `leaf→exec` helper. The
four points today (the prior 3 missed the dict-entry branch):
- `DbBridge.LoadObject` scalar branch + **dict-entry branch** (`Code/AccessFloor.cs` ~:117-119,168).
- `DbBridge.LoadExtent` (~:202).
- `AccessFloor.ScalarObject` (~:243) — the single shared builder for `currentUser` AND every read/write
  access-rule **candidate** (10+ call sites route through it), so one blank here covers principal + all
  conditions.
Today these *omit* `passwordHash` by name; rev 6 *blanks* by type. **A single uncovered path ships the hash.**

**Save → DB: hash at the WS layer (one helper every write handler calls), skip empty.** A
`HashPasswordFields(typeName, fields)` / `HashLeaf(typeName, prop, value)` helper, called by the WS write
handlers — `objectPropChange`, `arrayAdd`, `addEntry`, path-`write`, `setReferenceField` create-new,
`addSetMember` — which already resolve the type and call the access floor. Keeps `IInstanceStore` **dumb**
(CLAUDE rule 6; a future pillar-5 storage engine inherits the hashing for free). The two non-WS write paths
get explicit rules:
- **`AdminSeed`** writes the *already-hashed* value directly to the store → **bypasses the helper** (the one
  legitimate pre-hashed write; a store-level hash would double-hash it and break login — the decisive reason
  the hash lives at the WS layer, not the store).
- **`password` in `initialData` is FORBIDDEN** (a load/validate error) — seeding a literal password would put
  plaintext in the app document (the source), which is never wanted.
An empty value writes nothing (= "no change").

## Walk down the stack

1. **Parser / type system.** `BaseType.Password`, `name password`; maps to `Text` in the scalar-base switches.
2. **Descriptor + Input (~5 touch-points, not "a marker"):** (a) `VisibleProps` *includes* the password field
   (today excluded by `IsHiddenField`); (b) a `PropDesc` branch emits the password marker; (c) it stays *out*
   of `Scalars` → not a column / labelProp; (d) a new `Input` branch renders `<input type="password">`
   (masked), binding `value={sys.field(obj,"password")}` = `""` or the typed value (plain text binding); (e)
   `isPrincipal` — which today keys on the hidden field *existing* — moves onto the `password`-type test.
3. **`ctx` + commit.** Unchanged: the typed value stages; Save flushes via `objectPropChange`. Discard/nav
   drops it.
4. **Read chokepoint** — blank (above).
5. **Write chokepoint** — WS-layer hash + skip-empty (above).
6. **Login.** Reads the **raw store** field (the real hash — the store keeps it; only the value headed to a
   client/condition is blanked). Found by the User's `password`-typed field.
7. **Create + edit.** Both plain text (the `sys.new` draft starts `""`, the edit ships `""`); both bindable;
   both hashed by the WS helper. Set-password works in the **create form** and on the **edit page**.

## Security invariants (load-bearing)

- **The hash never reaches the client:** the load chokepoints ship `""`; "blank" is security-equivalent to
  today's "omit" and also makes the input bind for free. *Every* leaf-materialization for a `password` field
  must blank — the dict-entry branch included.
- **No plaintext stored:** the WS-layer hash covers every client write path; `initialData` forbids a literal;
  `AdminSeed` is pre-hashed. No path stores plaintext.
- **No double-hash:** the hash is on client-plaintext write paths only; `AdminSeed`'s finished hash and the
  migrate/clone raw-data carries (`SaveRaw`, `File.Copy`) never re-hash (verified).
- **Plaintext transient + in-transit only:** ctx / create draft (client) → WS op (TLS) → hashed → dropped.
  The create draft holds plaintext **client-only** (never an SSR-shipped value).
- **Floor-gated:** the write floor gates the write; visibility = the form's `canEdit`.
- **No echo:** create/edit replies carry ids, not values (verified).

## Decided

- `password` = a **`BaseType.Password` member** mapping to `text` (not the general `secret`/`hash` modifiers —
  deferred to a second use, per the `Multiline` precedent).
- **Two chokepoints:** read-blank at the **load** boundary (all leaf-materializations); hash at the **WS
  layer** (one helper). Binds as plain text — the A/B binding question is moot.
- **`User` stays explicitly declared** (`passwordHash text` → `password password` in `instances/5/app.app`);
  implicit User fields need a non-existent inject/merge seam → deferred.
- **Visibility = `canEdit`** (write-only field shown only if writable).
- **Migration = a destructive, MANUAL credential reset** — a hand-edited `passwordHash → password` line in the
  committed app doc + accepting that existing hashes drop (rename rides M13, *not* the migration engine).
  Justified only because instance-5 re-seeds its admin from `DEENV_ADMIN_PASSWORD` on boot and has no seeded
  user passwords (no live human credentials are lost). **Flag loudly for any deploy that has real passwords**
  (deploy path = edit the doc, delete the data file, re-seed — the strict startup guard won't carry the stale
  `passwordHash`).
- **Excluded from table columns / labelProp.**

## What it replaces / deletes

- `<SetPasswordControl>`; `sys.setPassword` (twin builtin + `setPassword` WS op + `SetPasswordResponse` +
  client reply handler); the orphaned `.set-password` CSS (`Http/SsrRenderer.cs`).
- `UserConvention.PasswordHashField` / `IsHiddenField` (name) → the by-type blank chokepoints + the WS hash.
- The regression test asserting no `passwordHash` in `currentUser` (vacuous after rename) → assert the
  `password` field ships `""` / no `pbkdf2$` marker (in `currentUser` AND the graph).
- M-auth e2e retargets: set the password as a **form field** (edit or create), Save.

## Slices

**Slice 1 — the `password` type + the two chokepoints (edit AND create).** `BaseType.Password` (+ the
`→Text` mapping arms + parse/print) → load-blank (all 4 points) → WS-layer hash (+ `AdminSeed` bypass +
`initialData` forbid) → descriptor/Input (the 5 touch-points) → login-by-type → the deletions → the manual
migration → the e2e retarget. Set/change a password through the form; re-login proves it; the page ships `""`.
- *Gherkin (edit):* an admin types a new password on a user's page, Saves, the user logs in with it; AND the
  rendered document ships `""`/no hash for the field (in the graph AND `currentUser`).
- *Gherkin (create):* an admin creates a user with name + password in one form, and that user logs in.
- After slice 1, throw a quick **ui-architecture-reviewer** at the Input-masking + columns-exclusion (the UI
  half neither prior pass fully owned).

**Slice 2 — form Save feedback.** "Saving… → Saved / Couldn't save" by rendering the commit lifecycle (journal
drain on ack / rollback on reject — a render over existing state, not a new async channel). Closes the
original feedback gap, for every form.

## Caveats to document (raised in review, accepted)

- **Empty-on-create = a credential-less user.** A create that omits the password yields a user with no hash →
  can't log in until one is set, with no error. Intended as "create-then-set," but state the contract so it
  isn't mistaken for a set credential.
- **`currentUser.password` becomes `""`, not absent,** in the condition scope (was omitted; now present as
  `""`). Harmless (a rule reading it gets `""` instead of null), but a behavior note.

## Open questions

- **Save-feedback signal (slice 2)** — observe the journal drain/rollback (preferred) vs a `ctx.commit`
  lifecycle.
