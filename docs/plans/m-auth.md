# Plan: M-auth — access control (the auth milestone)

**Status:** designed 2026-06-24, **DONE 2026-06-25** (core delivered; recorded in DECISIONS.md;
pulled ahead of M12 visual designer and M13 versioning). The engine (read floor + write enforcement +
floor-hardening), self-hosted password login/logout (login-as-state, no reserved URL), the `devlog`
**public-roadmap** dogfood (public read + admin-only write, via an `accessActive` system var + a
`<SignInBar>`), the **first-admin bootstrap** (env-var auto-seed on kernel boot), and **multi-user
management** (`<UserAdmin>` create + per-row set-password, gated on a derived `canManageUsers`, with a
real-browser e2e) all landed. Both original open threads are **resolved** (see
[Open questions](#open-questions)). The first sliver dovetailed with the `devlog` dogfood as planned.

**Follow-ups deferred to Near-future** (ROADMAP.md "Near-future"): wiring login on the deenv.org deploy
(set `DEENV_ADMIN_PASSWORD`, drop the basic-auth gate); **remove-user** + **inline role-edit** on the
generic `/users` list (remove-user) / `/users/<id>` page (role-edit, already works) — inline-on-the-list
is the convenience; the **Users-twice dedup** is **DONE (`b06b532`)** — solved not by the round-trip but
by deleting the client-toggled popup and relocating set-password to the generic `/users/<id>` page (the
menu links to `/users`); and set-password success feedback + broader auth styling.

> **Superseded (2026-06-26): the credential field.** What this plan calls `passwordHash` (a reserved
> field name) is now a `password`-typed field, found **by the `password` type** (not a reserved name).
> Set-password is a **masked `password`-type field committed on the form's Save** — NOT a `setPassword`
> action/builtin (the `sys.setPassword` op, `<SetPasswordControl>`, and the WS `setPassword` handler are
> deleted). References below to `passwordHash` / `sys.setPassword` / a set-password control are
> historical. See `docs/plans/virtual-password-field.md`.

## Goal

Give a deenv app real **users and access control** without bolting on a separate auth system, and
without making the app author a security engineer. The model is **maximally flexible underneath**
(rules + conditions, per-type and per-field) and **simplified by UI on top** — the same auto-vs-custom
split the rest of deenv already runs. Users and role management are **baked into every instance** but
**dormant** until an app uses them, so a solo app pays nothing.

## The model

Access is decided by a **deny-by-default ruleset**. A rule is:

```
{ type, field?, verbs, condition }
```

- **verbs** = `read | create | edit | delete`.
- **type / field** — the rule's subject is a node in the object model (`Task`, or `Task.budget`).
  Per-field rules tighten specific fields; a field inherits its type's access otherwise.
- **condition** — an optional predicate gating the rule (below). `null` = always applies.

**There is no "role" primitive.** A role is just an **enum field on `User`**
(`role enum Admin Member Guest = Guest`, reusing the existing enum type). A rule's "who" is a
condition like `currentUser.role == "Admin"`. Everything special collapses:

| Was a special concept | Now |
|---|---|
| roles list | the values of `User.role` (designer already edits enum values) |
| default role | the enum's default |
| role assignment | setting a field at **runtime**, itself gated by a rule (`.role write where currentUser.role == "Admin"`) |
| anonymous | `currentUser == null` → role conditions false → denied (deny-by-default) |

### Conditions

A condition is a **pure Code expression** — the same Code-as-data (AST) deenv already parses, prints,
and interprets. There is **no special little language and no codegen**: the condition *is* an AST node
stored in the rule; it is evaluated by the **existing interpreter** with an injected scope.

Injected inputs (v1): **db data** (the object's fields, other rows, `currentUser`) + **`now`** +
**client/request context** (IP, headers, device) + literals. Shape is close to AWS IAM condition keys.

```
condition: { op:"==", left:{field:"status"}, right:{lit:"published"} }
```

- **"their own" is not magic** — there is no owner field and no automatic "connected-to-user"
  meaning. You express it as an explicit condition (`assignee == currentUser`). The engine only ever
  evaluates conditions; the user is just another value a condition can read.
- **Anonymous = no `currentUser`; null must fail *closed*.** Anonymous access isn't a feature — it's
  the absence of a principal. A rule grants it when its condition holds with `currentUser == null`:
  bare `read` (no condition) or `read where status == "published"` = public; `where currentUser != null`
  = any logged-in user; `where currentUser.role == X` = role-gated. **Eval obligation:** a
  currentUser-dependent condition must evaluate to **false (deny)** when `currentUser` is null —
  property access on a null user is not-satisfied, never an error. The designer grid surfaces this as a
  per-rule **who** choice (Anyone / Any logged-in / Role = X / Custom) that generates the condition, so
  "public" is a deliberate pick, not an accidental bare verb.
- **Graph position is testable — set membership.** A condition can ask which **set/collection** holds
  an object (`customer in customers`, not in `deletedCustomers`) — a plain data fact, computable from
  the db. This is the **soft-delete / archive / state-as-collection** pattern: "deleted" = moved to a
  `deletedCustomers` set, **same type**, no field flag, no separate type. The `in` / membership
  operator is the one new condition primitive (also how multi-role works:
  `"Admin" in currentUser.roles`). NB this is graph *position* (data) — distinct from URL routing
  (rejected: route-dependent) and "reachable-from-user" (rejected: connection has no special meaning).
- **Externals are a deferred, additive capability — not a permanent ban.** Because a condition is
  Code over an injected context, allowing external lookups later = adding **one** guard-railed builtin
  (a `fetch`) to that context. Purely additive, nothing to undo; v1 simply doesn't ship it. **When**
  added it must be **controlled** (timeout, **fail-closed**, result caching, ideally an allowlist) —
  the check sits in the request hot path, it's where SSRF/exfil would live, and it makes "who can see
  X?" no longer locally computable.

## The principle that settled it

**Flexible base, simple UI on top.** Don't pick a point on the simple↔complex line. The engine is
fully expressive; the friendly **role×verb grid / condition builder** is just *one* UI over it
(trivial app → grid; gnarly app → raw rules; same engine). This is deenv's auto-vs-custom UI story
applied to access — and the designer is already a custom UI over a flexible substrate, so an
access-setup screen is just another one.

## Enforcement (the invariant)

The non-bypassable check lives at the **kernel floor, below Code, on the store/wire seam** — it gates
**reads** (what ships to the client) and **writes** (what mutations are accepted). No app path
(custom `fn render`, a where-query, a mutation) can route around it.

This is **orthogonal** to today's structural privacy (the memo cache ships dependency refs, not input
values). That protects computation *inputs*; this is access-*by-principal*. Both live on the same seam.
Getting this seam right is the whole game.

## Identity & users

- A **`User` is an object**, stored as the app's own data. **Password** authentication
  (salted hash in a `passwordHash` field). Per-app **sovereign** — each instance owns its users; the
  operator designer is just another instance with its own.
- A session **binds to a `currentUser`** principal (extends today's "sessions are users" from an
  anonymous handle to a real identity). `currentUser` is exposed to Code as a **new system var**,
  beside `db` / `path` / `status`.
- **User type by convention.** The framework knows `User` + `name` + `passwordHash` by name; on
  **publish** it **injects** them if the app didn't define them, and **merges** with an author's own
  `User` (keep their extra fields, guarantee the required ones, **reject** a wrong-typed `passwordHash`
  at publish with a clear error). Rides the existing non-destructive-apply substrate.
- **`passwordHash` is never serialized** to the client and never set from it — enforced by convention
  (the framework knows the field). Any *other* sensitive field is protected by an ordinary rule
  (`.ssn read where false`). (An earlier per-field `system` attribute was dropped — convention + rules
  cover it.)
- **Auth crypto is kernel code, not app Code.** A small set of **kernel builtins** —
  `setPassword` / `verifyPassword` (hash & compare server-side) and the **session→principal bind** —
  is the only way passwords are handled. Code stays pure; app authors never touch crypto.
- **Baked in but dormant.** Every instance has the User type + login + user/role management at zero
  config. A solo app never sees it; a multi-user app activates it by adding access rules. "Baked in"
  means always-there-and-free, **not** forced-login-on-everything.

## UI & URLs

- **Auth UI = `lib` components** (`LoginForm`, `UserAdmin`, `UserMenu`) in the M11 public component
  library, composed in a custom `fn render()` at any URL the operator chooses. `UserAdmin` ≈ the
  generic `User` table + a `setPassword` action + the role-enum editor — mostly reused parts.
- **Security lives *under* the components.** `<LoginForm>` just calls the kernel `verifyPassword` /
  bind builtin; enforcement is the floor. So relocating, restyling, or wrapping login **cannot weaken
  the boundary** — the operator moves the door's picture, not the lock.
- **Custom UI reserves nothing in URL space.** Login is a **state**, not a route: `currentUser == null`
  (a reactive system value — logging in flips it and the UI re-renders) + a component the operator
  places wherever (page / overlay / nowhere) + a login/logout **action over the existing WS channel**
  (no endpoint; the bind happens on the connection). Enforcement is the data floor, which is
  URL-independent anyway. In **custom** mode the operator owns the unauthenticated state outright (no
  framework fallback). In **auto** mode the framework handles it as a state-*gate* on whatever URL was
  requested, then continues — still reserving no `/login`. (Extends the existing "the app owns a clean
  URL space" rule — the reason `/ws` and `/js` sit on a separate port.)

## Delivery

- The **ruleset is a section of the published app document** (alongside `types` / `initialData` /
  `common` / `ui`), authored in the **designer** (simple grid first; raw-Code escape hatch for the
  exotic), and **constant between publishes** — it rides the existing
  designer → SchemaBridge → app-doc → publish/restart pipeline. ≈ no new authoring infra.
- This reconciles "computable from the db": the **rules** are publish-fixed config; their
  **conditions** still evaluate against **live** `{db, currentUser, now, client, object}` per request.
  Different layers.
- **At publish** the kernel **validates** (every condition parses; every referenced type/field exists)
  and may pre-build the evaluator. It does **not** bake answers — those depend on live inputs.
- **Role assignment stays runtime** — it's just a field value, gated by a field-write rule. (Onboarding
  a user must not require a republish.)

### Worked example

```
types
  Task
    title   text
    status  text
    budget  number
  User
    name          text
    role          enum Admin Member Guest = Guest
    passwordHash  text                 # never serialized (framework convention)

access                                 # rules = verbs + condition
  Task
    *               where currentUser.role == "Admin"
    read create edit where currentUser.role == "Member"
    read            where status == "published"
    .budget read    where currentUser.role == "Admin"     # field tighten
  User
    .role write     where currentUser.role == "Admin"     # only admins reassign roles
```

Parses to `rules: [{type, field?, verbs, when:<AST>}]`. The designer shows the role×verb grid when it
sees a `role` enum on `User`; each checkbox emits a `currentUser.role == X` condition.

## What it reuses

The point of the design: almost no new core concepts.

- **enum + fields** — roles, the User shape, `system`-free sensitive handling.
- **Code / the twin interpreter** — conditions are Code-as-data, evaluated by the existing executor.
- **the generic UI components + the `lib` scope (M11)** — login/user/role UI and the setup grid.
- **the designer → SchemaBridge → app-document → publish/restart pipeline** — authoring and delivery.
- **non-destructive apply** — injecting/merging the User shape on publish.
- **the WS session + the store/wire seam** — session→principal, and the enforcement floor.

New surface is small: the kernel **auth builtins** (`setPassword`/`verifyPassword`/bind), the
`currentUser` system var, the floor's per-request **rule evaluation**, the **access section** of the
app document (parse/print/validate), a few **`lib` components**, and the **seed-admin** step.

## Explicitly out

Owner fields; URL-based access (it's data-model-based); graph reachability / ReBAC; machine/instance
identity; kernel-wide SSO. (Time + client context are **in**; only externals are deferred, and even
those are not forbidden — see Conditions.) These were all visited and dropped during the design; do
not resurrect them as the model.

## Open questions

**Both resolved 2026-06-24** — they collapse into one activation moment.

1. **Dormant → active trigger — the rules *are* the switch (no flag).** No access rules → **dormant**:
   the app behaves as today (reachable, no login, the operator just uses it; conditions never run).
   **≥ 1 rule → active**: deny-by-default among the rules, login required to be a principal. Writing
   the first rule flips it on — minimal-by-default, nothing to configure or delete.
2. **Bootstrap — IMPLEMENTED as env-var auto-seed on kernel boot** (`AdminSeed.SeedFromEnv` /
   `SeedIfRuled`, commit `c0065e5`). On boot the kernel seeds a `User` (role=Admin, hashed password) into
   every **ruled** instance from the operator's `DEENV_ADMIN_PASSWORD` (+ optional `DEENV_ADMIN_USER` /
   `DEENV_ADMIN_ROLE`), **once, idempotently** — so a fresh deploy of a ruled app is loginable. A dormant
   app or an unset password is a no-op. This **supersedes** the originally-sketched publish-time prompt +
   designer-scaffold override (env-var auto-seed is simpler for the deploy; the operator owning the boot
   secret makes the can't-lock-yourself-out scaffold moot). *Caveat:* "no rules = allow all" means a
   publicly-deployed app with zero rules is open — status quo and the operator's call; a deploy-time lint
   is an easy later guard, not MVP. Password **rotation** via the env var is NOT supported (idempotent →
   no re-seed); rotate via the in-app `setPassword` path later.

Minor (build-time, not model forks): the exact verb set; field tighten-only vs also-loosen; the
designer builder's preset vocabulary; whether `AppPrint` sugars role-conditions back into a column.

## First buildable sliver

One type, **read** enforcement only, **equality** conditions, evaluated at the floor, with password
login + session→principal on one app. Proves authn + the floor + condition evaluation on real data.
Fields, more operators, the raw-Code box, writes, the grid, and externals all layer on after.

Possible dovetail: fold the first sliver into the dogfood app (`devlog`).
