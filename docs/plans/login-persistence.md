# Plan: Login persistence — sessions that survive page loads

**Status:** design **ACCEPTED 2026-07-03** — grilled once (fresh-opus adversarial pass, verdict
**SHIP-WITH-AMENDMENTS**, all folded below) and all four decision points settled by the user (see
"Decisions" near the end). **Mechanism slice DONE 2026-07-04** (stateless HttpOnly cookie,
`/session` cookie endpoint, GET-side principal stamp; suite 685/685). Next: §9 slice 2, the
designer access-rule flip. Triggered by the user picking this as the next design session: it is the convergence
point of every recorded flag outside versioning — the designer's `sys *` rule flips to admin-gated
(closing the security review's strategic residual, `docs/plans/security-review-pre-public.md`), the
clone-the-designer caveat closes, the dict READ floor decision unblocks, versioning's deferred
commit-author `by` unblocks, and ROADMAP's "deploy login wiring" follow-on lands on it.

Companions: `docs/plans/m-auth.md` (login-as-state, the model this extends),
`docs/plans/security-review-pre-public.md` (V1–V5 + the residual), DECISIONS.md "Host-action
AUTHORITY" (the temporary unconditional rule this retires).

## The gap (confirmed)

Login today binds the principal to an **in-memory warm session only**: `sys.login` is a WS op
(`ws.ts:554-557`) → `WsHandler.HandleLogin` verifies (PBKDF2, `AuthCrypto.cs`) and sets
`ClientSession.PrincipalUserId` (`WsHandler.cs:1251`, `ClientSession.cs:32`). The session is keyed
by a `clientId` GUID **minted fresh at every SSR render** (`SsrRenderer.cs:122-125`) and held only
in page-injected JS (`init.ts:41`) — never localStorage, never a cookie (zero cookie/Authorization
code exists repo-wide; grep-confirmed). A full page GET therefore always renders anonymous:
`ContentHandler.HandleAsync` (`ContentHandler.cs:40`) never passes a principal to
`SsrRenderer.Render` — whose `principalUserId` parameter **already exists** (`SsrRenderer.cs:91-94`)
and is simply never fed. The gap is structural: no channel carries "this browser is authenticated"
into a cold GET. A brief WS drop-and-reconnect within the same tab *does* survive (same clientId,
30-min idle TTL, `ClientSession.cs:101-116`); anything that re-runs SSR does not.

## The design

### 1. Carrier: a cookie — forced, not preferred *(settled by requirement)*

A cold document GET carries exactly one durable browser artifact: a cookie. localStorage is
JS-only and cannot ride the initial request. Grill: HOLDS.

### 2. Content: a stateless signed token, no server-side session store *(settled, grilled)*

Payload `{i: instanceId, u: userId, exp, pw: pwStamp}`, HMAC-SHA256 (BCL, one call) with a kernel
secret, base64url — a ~40-line `TokenAuth` helper beside `AuthCrypto` (PBKDF2 is deliberately NOT
reused for signing — wrong tool, too slow). Verification order at GET: signature → expiry →
instanceId match → **live-load the User row** (`AccessFloor.LoadPrincipal` path). Live-loading is
the revocation story: a removed user's token resolves to anonymous instantly; a role edit takes
effect on the next request.

- **`pwStamp`** = a *truncated HMAC/SHA of* the stored password hash (grill amendment: never a raw
  prefix — that would leak hash bytes into the cookie). Password change or admin reset rotates the
  stored hash (fresh salt, `AuthCrypto.cs:33-37`) → all that user's tokens die. Kept (vs the
  grill's defer-suggestion) because admin set-password on `/users/<id>` is a landed feature and
  "reset revokes sessions" is cheap correctness (~5 lines) — the difficulty-vs-correctness call.
- **Rejected: server-side sessions.** In-memory dies on every kernel restart/deploy (the exact
  annoyance being killed); persisting into the instance store would spray session churn into the
  M13 append-only log (every store write is a logged entry — history pollution); a separate kernel
  session file is more machinery than the HMAC helper.
- **Honesty:** logout clears *this browser's* cookie only; a stolen token lives until expiry or
  password change. Ceiling named: per-user tokenVersion ("log out everywhere") when multi-user
  usage is real.

### 3. Secret: one kernel-level file in the data home *(settled, grilled)*

32 random bytes, auto-minted on first boot if missing, never committed. **Explicit path on the
box: `/var/lib/deenv/kernel-secret`** — the dir that survives deploys (`DEPLOY.md:14`; verified
`update.sh` wipes only `/opt/deenv`). Dev: beside the working-dir `kernel.json`. Documented
property (grill amendment): losing/regenerating the secret invalidates every token — a global
logout, fail-safe, not data loss. Per-instance isolation comes from the signed `instanceId`
payload, not per-instance secrets (instances are co-tenants of one trusted process).

### 4. Cookie shape: raw `Set-Cookie` header — GenHTTP's Cookie type cannot do this *(settled by grill refutation)*

`deenv_session_<instanceId>=<token>; HttpOnly; Secure; SameSite=Lax; Path=/; Max-Age=<30d>`.

**Grill REFUTED the naive build:** it decompiled `GenHTTP.Engine.Internal` — the built-in `Cookie`
type carries only Name/Value/MaxAge and the engine's `WriteCookie` emits **no HttpOnly / Secure /
SameSite** tokens at all. The flags must be set via a raw `.Header("Set-Cookie", "…")` string
(verified: `Set-Cookie` is not on the engine's reserved-header list; one cookie per response —
fine, we set one). Read side is normal: `IRequest.Cookies` exists.

Per-instance cookie **names** matter locally: all instances share `localhost` on the two shared
kernel ports (path-routed `/apps/<name>`; cookies are host-scoped, port-blind), so distinct names
stop a devlog login from overwriting a designer login. The signed instanceId kills cross-instance
replay regardless; deploy subdomains isolate naturally. Per-app user sovereignty preserved: a
token names (instance, user); there is no kernel-wide principal. `Secure` on localhost: modern
browsers treat localhost as trustworthy; if one balks, the dev fallback is omit-when-non-TLS —
build detail. Anonymous visitors get **no** Set-Cookie at all (the cookieless-analytics posture
holds for logged-out traffic).

### 5. One `/session` endpoint on the asset tree; the endpoint touches NO session *(settled; fixation redesign from grill)*

A new framework endpoint beside `/ws` + `/js` (the sanctioned framework URL space; the app port
stays clean, `m-auth.md` "custom UI reserves nothing in URL space") is added **solely to set/clear
the persistent cookie**. It does NOT replace WS login — deenv is a warm-channel app, so the live
login flip stays over the socket, in place, no reload (user decision 2026-07-03). **Three clean
roles, one for each timescale:**

- **HTTP `/session` = cookie I/O only, never touches a session.** POST `{name, password}` → the
  same verify path (`FindUserByName` + `AuthCrypto.Verify`) → on success, raw `Set-Cookie` (above)
  + `{ok:true}`; on failure `{ok:false}`. `DELETE /session` → expired `Set-Cookie`. **No clientId
  in the body; the endpoint never reads or mutates `ClientSessionStore`.** This is what keeps the
  grill's P4 fixation edge from ever arising: that edge was an HTTP endpoint binding a
  *body-supplied clientId's* live session across origins (an attacker plants their clientId in a
  victim's page, binds their own creds to the victim's session — the V1 authority-confusion shape).
  A pure cookie-setter binds no session, so there is nothing to fix.
- **WS `login`/`logout` = the live in-place flip, UNCHANGED from today.** The existing ops still
  set `session.PrincipalUserId` on the caller's **own socket** → `currentUser` flips → reactive
  re-render, no reload. Fixation-safe because the socket IS the authenticated channel (an attacker
  can't post onto the victim's socket) — today's accepted posture, not widened. The Code surface
  `sys.login`/`sys.logout` and the twin no-ops (`CodeExecutor.cs:1050`, `codeExec.ts:1295-1296`)
  are untouched → **conformance untouched**.
- **GET mint-stamp (§6) = cross-load persistence.** The cookie only matters on the NEXT cold GET
  (new tab, F5, return visit): the GET reads it and stamps the principal on the freshly-minted
  session. The live flip handles the current page; the cookie handles every future page.
- **Login click fires both, from the in-hand form creds:** the WS `login` op (live flip) and the
  `/session` POST (persist). Credentials are therefore verified **twice** server-side — one extra
  PBKDF2 on a rare path, the deliberate cost of keeping the endpoint a pure cookie-setter with zero
  session authority. *(Single-verify alternative, NOT chosen: `/session` sets the cookie, the client
  reconnects the WS, the server reads the cookie at the upgrade to bind the socket — elegant, but
  needs the GenHTTP upgrade-cookie capability the grill left UNVERIFIED plus a reconnect blip;
  not worth trading a free second PBKDF2 for an unverified dependency. Spike only if the double
  call ever bothers us.)*
- **HttpOnly is load-bearing, not optional** — the grill's XSS probe (P1) found the SSR escape
  chokepoint (`SsrRenderer.cs:1049-1050` + no innerHTML client-side) was **incomplete**: no
  `javascript:`-scheme guard on `href`/`src` and string-valued `on*` attributes rendered as inline
  handlers. An app binding user data there yields script execution that could read a non-HttpOnly
  cookie. So the `document.cookie` variant (zero endpoints, zero CORS) is **rejected on evidence**.
  The gap itself is now **CLOSED** (`0aceb0a`, 2026-07-03): both render twins drop dangerous-scheme
  `href`/`src` and scalar `on*` attributes at the attribute-emit chokepoint. HttpOnly stays anyway —
  the guard is defense-in-depth, not a reason to revisit the cookie choice.
- **CORS:** local dev is genuinely cross-origin (app port → asset port; two ports confirmed in
  the kernel + test harness). The endpoint hand-emits exact-origin echo + allow-credentials +
  OPTIONS preflight (no GenHTTP CORS module exists in use — grill-verified; a few `.Header(...)`
  lines). The deploy is same-origin (`DEENV_PUBLIC_ASSET_PORT=0`, nginx maps assets under the app
  host) → no CORS, but nginx needs a `location = /session` → asset port block (DEPLOY.md).
- CSRF: the POST authenticates by body credentials, not by an ambient cookie → CSRF-inert.
  Logout-CSRF is nuisance-only and SameSite=Lax blocks cross-site sends anyway.

### 6. GET-side resolution + the mint-stamp — NEW wiring, not current behavior *(settled by grill refutation)*

`ContentHandler.HandleAsync`: read `IRequest.Cookies` → `TokenAuth.Verify` → `principalUserId` →
pass to `_renderer.Render(...)` (the parameter exists today, unfed). **Grill REFUTED the draft's
claim that the minted session then carries the principal — it does not:** `_sessions.Create()`
(`SsrRenderer.cs:125`, `ClientSession.cs:87-96`) mints `PrincipalUserId = null` and nothing copies
the GET principal onto it. **The design adds the stamp**: mint-with-principal, so the page's WS
`hello` claim (`ClientSession.cs:101-112`) hands the write floor (`WsHandler.cs:308-309`) an
authenticated session. Invalid/expired/foreign token = anonymous render, never an error.

**Claim-miss fallback (grill P3):** if the WS connects >10s after the GET (claim window,
`ClientSession.cs:69`), the session is gone and `hello` finds nothing — pre-existing edge, but
under auth it would mean *silently anonymous*. Fix rides the cookie: on claim-miss the client
reloads **once** (guarded against loops) — the reload GET re-derives everything from the cookie.
The cookie makes "just reload" a correct recovery for every session-loss mode; that's the point
of the design. (Reading the cookie at WS-upgrade time was deliberately avoided — GenHTTP's
upgrade-request header access is unverified; the GET-only read needs no spike.)

### 7. Lifetime: fixed 30-day expiry, nothing else *(default judgment, grill-trimmed)*

One constant. No idle timeout, no per-use rotation, no refresh tokens — solo-operator MVP. The
draft's sliding half-life re-issue was **dropped on grill advice** (minimal-by-default: it's a
nicety, and dropping it keeps ContentHandler read-only for cookies). Escape valve: bump the
constant. Ceilings named: sliding re-issue, tokenVersion. `pwStamp` (§2) covers the revocation
that matters now.

### 8. Login-as-state survives unchanged *(settled)*

`currentUser == null` stays the reactive gate; LoginForm/SignInBar UX unchanged (submit flips live
over the existing WS; the cookie only affects future page loads); no reserved app URL; `accessActive`/`anonymousLockedOut` untouched
(`SsrRenderer.cs:262-304`). The cookie is the state's **browser-durable materialization**. The
principal becomes browser-scoped while view-state stays per-tab: "sessions are users" evolves to
**"sessions are tabs; the browser is the user."** Multi-tab: tab B picks the principal up on its
next reload/refetch — live cross-tab push is the real-time milestone, not this. Existing e2e is
safe: scenarios run in isolated Playwright browser contexts (`SharedBrowser.cs:89-90`), and
`Concurrency.feature`'s two-context setup keeps distinct cookies (grill-verified).

### 9. The payoff sequence *(slice map — hand to milestone-planner/build)*

1. **Mechanism slice — DONE 2026-07-04**: `TokenAuth` + secret file, `/session` on the asset tree
   (+ CORS + OPTIONS), ContentHandler read + mint-stamp, ws.ts persists the cookie while the existing
   WS login/logout ops keep the live no-reload flip, and claim-miss reloads once. Proven: login →
   fresh page → still authenticated; logout → fresh page → anonymous; token tamper/expiry/
   foreign-instance/password-change → anonymous. Suite 685/685.
2. **Designer flip slice — DONE 2026-07-04**: the designer app doc gains a `User` type (+ role enum), access rules on
   its data types (floor is allow-when-unruled — `sys` alone would leave anonymous data-edit
   open), **a hand-rolled login gate in its custom `fn render()`** (grill P8: the generic
   `anonymousLockedOut → LoginForm` auto-gate applies only to the generic render; without the
   hand-rolled branch, anonymous would see *empty tables*, not a login prompt), then the flip:
   `sys *` → `sys * where currentUser.role == "Admin"`. Commit/Branch stay client-write-locked
   via explicit `create edit delete where false` so their reads can also be admin-gated. `AdminSeed`
   then seeds it (requires rules + User type — satisfiable, `AdminSeed.cs:116-123`). Closes the
   strategic residual AND the clone-the-designer caveat (the clone carries the conditional rule).
3. **Deploy wiring**: `DEENV_ADMIN_PASSWORD` already on the box; nginx `location = /session`;
   deploy; live-verify login on the designer subdomain; **then drop the designer htpasswd**
   (keep it briefly as belt-and-braces if preferred — operator's call).

Ride-along candidate: **V4 login timing** (dummy-hash the unknown-user path — ~3 lines in the
endpoint's verify, closes username enumeration). Unblocked follow-ons (NOT this scope): dict READ
floor half (V2), versioning's commit-author `by` stamp, V3 claim hardening (narrowed by this
design — the page carrying the clientId is itself principal-gated once the designer is ruled —
but `Get()` still re-claims; single-use claim is the later fix).

## Grill record (2026-07-03, fresh opus, refute-briefed, code-grounded)

Verdicts: D1 HOLDS · D2 HOLDS-amended (pwStamp = truncated HMAC, never a prefix) · D3
HOLDS-amended (explicit `/var/lib/deenv/kernel-secret`; lost-secret = global logout, documented) ·
**D4 REFUTED as drafted** (GenHTTP `Cookie` type can't express HttpOnly/Secure/SameSite —
engine decompiled; → raw `Set-Cookie` header, folded into §4) · D5 HOLDS-amended (endpoint lean
**upgraded to decided** by the XSS probe; CORS hand-rolled) · **D6 REFUTED as described**
(mint-stamp is unbuilt wiring, not current behavior — folded into §6 as explicit new work) ·
D7 HOLDS (sliding dropped) · D8 HOLDS · D9 HOLDS-amended (designer needs the hand-rolled gate).
Probes: P1 XSS chokepoint incomplete (`javascript:` hrefs, string `on*` — separate follow-up;
decides D5) · P2 GenHTTP capabilities confirmed from the package API + engine binary, no spike
needed · P3 mint→claim handoff traced, claim-miss edge + reload fix · P4 fixation edge in the
draft's live-session flip → **endpoint-touches-no-session redesign** · P5 e2e fallout = transport
swap budget, no principal-isolation breakage · P6 **M13 slices 5–7 collision check: SOUND** —
disjoint files; only shared file is `instances/1/app.deenv`, and the flip edits its `access`
section while slice 5/7 add type meta-fields — trivial rebase · P7 lazier-hunt: sliding dropped,
pwStamp kept (stated requirement), per-instance names justified, HMAC-not-PBKDF2 right-sized ·
P8 designer-gate work item surfaced. Overall: **SHIP-WITH-AMENDMENTS** — all folded above.

## Decisions (all settled by the user, 2026-07-03)

1. **SETTLED (user, 2026-07-03): live WS flip stays, no reload.** The `/session` endpoint is added
   *only* to set/clear the persistent cookie and touches no session; the existing WS `login`/`logout`
   ops keep doing the in-place reactive flip. Fixation edge is closed by the endpoint having zero
   session authority (not by deleting the live flip). Cost: creds verified twice on login (one extra
   PBKDF2 — accepted). deenv is a warm-channel app, so login flips in place; it does NOT reload.
2. **SETTLED: HttpOnly cookie accepted.** It's the textbook secure-session recipe (HttpOnly +
   Secure + SameSite=Lax + HMAC-signed) and beats the JS-readable alternatives. Security does NOT
   rest on HttpOnly alone — it defeats token *theft* via XSS, but XSS-on-an-authenticated-page can
   still *act* in-session; the real boundary is the access floor + the landed XSS guard, with
   HttpOnly as defense-in-depth. Named residuals (all deliberate, MVP-appropriate): no instant
   server-side revocation — a stolen token is replayable until 30-day expiry or password change
   (pwStamp); the tradeoff vs. a server-side session store was taken to survive restarts and stay
   off the M13 log; per-user tokenVersion ("log out everywhere") is the ceiling. **Secure/TLS is
   mandatory in prod** (nginx), relaxed only on localhost.
3. **SETTLED: 30-day fixed default now, configurable later** (user). No sliding, pwStamp kept for
   MVP; the lifetime constant becomes an operator/kernel config knob in a later pass (named future,
   not MVP).
4. **SETTLED: full flip — add the access rules** (user: "no-brainer"). The flip slice gates the
   designer's data types (Design/Instance/User/Commit/Branch) AND adds the hand-rolled login gate to
   its custom render AND flips `sys` to the admin condition — then the nginx htpasswd drops. This is
   what actually closes the strategic residual; `sys`-only was the rejected half-measure.

## Explicitly out

Cross-tab live principal push (real-time milestone) · reading cookies at WS upgrade (unneeded;
unverified GenHTTP surface) · per-verb `sys` granularity (existing deliberate cut) · tokenVersion
/ logout-everywhere · sliding re-issue · single-use session claim (V3's full fix) · any
schema/`Session`-type addition (sessions deliberately do NOT enter the object model or the M13
log). *(The `javascript:`/`on*` XSS guard that was flagged here as a follow-up is now DONE —
`0aceb0a` — see the HttpOnly bullet above.)*
