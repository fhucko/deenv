# Plan: app routing by path + domain (mount-aware, self-sufficient instances)

## Goal
Replace per-instance **ports** with **path**-based addressing (+ optional **domain**), so the kernel
hosts many self-sufficient apps behind **one app port + one shared asset port** — easy to put behind a
single nginx / domain in production. Each instance is **mount-aware**: it doesn't know *where* it lives,
so the *same* instance is reachable BOTH at `host/apps/<name>/…` AND at the root of its own domain
`todo.com/…`.

Two payoffs this unlocks (the *why*, beyond deploy convenience):
- **A domain makes an app a completely independent, standalone site** — no coupling to its siblings.
  Self-sufficiency is what lets one instance own a whole domain.
- **Per-app asset paths are the foundation for per-app assets** — later, each app can carry its OWN
  client bundle / styling. Build the per-app *routing* now; the per-app *content* is a later swap, not
  built here.

## Scope refinement (2026-06-21) — domains are nginx's job
Domains are handled **externally by nginx** (a wildcard cert), in a **separate deploy session** — the
kernel does **NOT** route by `Host` and carries **no `domain` field**. Instead, instances are
**mount-aware via a base** that defaults to the `/apps/<name>` path but can be overridden by an
**`X-Forwarded-Prefix`** request header, so nginx can proxy `<name>.<domain>/…` → the kernel's
`/apps/<name>/…` and set the prefix to `""` to serve the app at the domain ROOT (clean, fully
independent).

- **In scope (ONE coupled code slice — no no-op "default base /" step):** path routing `/apps/<name>` +
  mount-awareness (base from the path OR `X-Forwarded-Prefix`) + the shared path-routed asset port + the
  registry shape + the designer ops.
- **Out of scope (your separate nginx session):** the wildcard cert, the per-domain server blocks, the
  `X-Forwarded-Prefix` wiring. No kernel `Host` routing, no registry `domain` field.

So **slice 4 (kernel domain routing) is DROPPED**; **slices 1–3 + 5 collapse into the one code slice**;
**slice 6 (deploy) is your nginx session.** The slice list below is kept for the per-area detail.

Driver: **gate #2 (minimal real deploy)**. Path/domain routing dissolves the two-public-TLS-port +
per-instance-port nginx complexity. Vision check (this session): **ALIGNS, gate-#2-enabling**; its one
caution — keep framework paths out of the data URL space — is satisfied for free by keeping the asset
port separate.

## Target model
- **Two shared ports (keep the 2-port split):** an **app port** (SSR + the data URL space) and an
  **asset port** (`/ws` + `/js`). Both are now KERNEL-level (one each), not per-instance, and **both
  route by path**.
- **Path access:** `appPort/apps/<name>/…` → instance `<name>` (base `/apps/<name>`);
  `assetPort/apps/<name>/ws` + `/apps/<name>/js` → its WS / bundle.
- **Domain access:** `Host: todo.com` → the instance whose registry `domain` is `todo.com` (base `/`);
  assets at `todo.com` on the asset port, base `/`.
- **Mount-awareness (the primitive):** each instance is told its **base** (`/apps/<name>` or `/`).
  Internally the app stays base-UNAWARE — its `path` var and links are root-relative — and the base is
  applied only at the EDGES (SSR output, client nav, asset URLs). So an instance is self-sufficient:
  drop it at a domain root or under a path, unchanged.
- **Registry:** per-instance `{ id, name, domain?, designId? }` (name → `/apps/<name>`, optional
  domain → `Host`); the two ports move to kernel-level config. Per-instance `appPort`/`infraPort` retire.

## Current model (what changes) — seams
- `HostedInstance.StartAsync` builds TWO GenHTTP hosts **per instance** — `appHost.Port(appPort)` +
  `infraHost.Port(infraPort)` (`DeEnv/Kernel/HostedInstance.cs:62-74`). → ONE app host + ONE asset host
  at the kernel, each a path Layout dispatching `apps/<name>` → that instance's handlers.
- `InstanceApp.Build` returns `(app, infra)` per instance (`DeEnv/Http/InstanceApp.cs:58-85`): app =
  `ContentHandler` (SSR), infra = `.Add("ws", ws).Add("js", bundle)`. → these become the per-instance
  LEAVES the kernel mounts under `apps/<name>` in the two shared hosts.
- **The base seam** (today implicit in the port):
  - *Asset URL:* `ws.ts:99` `${proto}//${location.hostname}:${initInfraPort}/ws` + the `/js` loader
    `SsrRenderer.cs:282`. → `…:${assetPort}${base}/ws` and `${base}/js` (inject base + asset authority,
    not a bare port).
  - *`path` var ↔ location:* SSR parses `urlPath`; client `ui.ts:39` `history.pushState(…, path)` +
    `init.ts:61` popstate. → strip the base from `location.pathname` to get the app's root-relative
    `path`; prepend the base on pushState.
  - *Links:* root-relative hrefs via `sys.nest(base,…)` + literals (`GenericUi.cs:380` `href="/"`,
    breadcrumbs `SsrRenderer.cs:705-710`). → prepend the instance base to emitted root-relative
    `href`/`src`, centralized at the SSR attribute emission + the client reconciler, so app Code keeps
    writing `/designs/3` unchanged.
- `InstanceInfo` (`InstanceApp.cs:31`, Port/AssetsPort) + the designer instances list +
  `sys.create`/`sys.cloneInstance` (ports) → name/domain.

## Slices (tests-first)
1. **Base seam (mount-awareness plumbing), default base `/` — behavior-preserving.** Inject a `base`
   (alongside the asset authority); apply it at the three edges above. With base `/`, output is
   byte-identical (the whole suite stays green). Tests: assert base-prefixing (base `/x` → `/x/…` links
   + `/x/ws`; base `/` → unchanged) — a focused SSR/C# test + a `CodeClientTests` assert for the
   nav/href edge.
2. **One app port + `/apps/<name>` routing.** Kernel builds ONE app host: a front Layout mapping
   `apps/<name>` → that instance's `ContentHandler` with base `/apps/<name>`. Registry gains per-instance
   `name`; the app port moves to kernel config. Gherkin (`Kernel.feature`): `/apps/todo` + `/apps/crm`
   serve the right instances with base-correct links/path; the sovereignty assertion still holds.
3. **One shared asset port + `/apps/<name>/ws` + `/apps/<name>/js`.** Kernel builds ONE asset host
   (front Layout: `apps/<name>/ws` + `…/js` → that instance's ws/bundle). `ws.ts` builds the URL from
   asset authority + base. **`/apps/<name>/js` resolves to THAT instance's bundle handler (per-app
   routing), not a single shared `/js`** — the foundation for per-app assets (today every instance
   serves the same bundle; per-app CONTENT is a later swap). Gherkin: a path-mounted instance's WS
   connects + a save persists; `/js` loads. *(After 2+3: instances fully path-mounted on two shared
   ports — no per-instance ports.)*
4. **Domain routing.** Registry optional `domain`; the app + asset front routers check `Host:` first
   (→ instance, base `/`), else fall through to `/apps/<name>`. Gherkin: `Host: todo.com` → todo at
   root; its WS works; an unknown host → not found.
5. **Registry + designer-ops cleanup.** Retire per-instance `appPort`/`infraPort` (kernel-level shared
   ports); `sys.create`/`sys.cloneInstance` take a name (+ optional domain), not ports; the instances
   list shows name/domain. Update `KernelHostActions`, the designer `app.app`, `DesignerSeed`, and
   `kernel.json`'s shape.
6. **Deploy: nginx simplification (user-side, on the box).** One server block:
   `location /apps/ → appPort`, the asset paths → `assetPort` with the `/ws` upgrade; optional per-domain
   server blocks proxying to the kernel (Host-routed); one cert (or per-domain certs). Rewrite
   `deploy/DEPLOY.md` slice 2 — this REPLACES the two-public-TLS-port dance.

## Invariants / non-goals
- **Sovereignty unchanged** — front-edge addressing only; each instance keeps its own `IInstanceStore`
  (the `Kernel.feature` "a change to one store leaves the other untouched" assertion stays valid).
- **Isolation unchanged** — still one process; a path front-router is equivalent to today's port
  multiplexing. Don't let "one domain" imply isolation. Keep prefix→instance resolution KERNEL-owned
  over the registry, so a future reverse-proxy-to-per-instance-process stays open.
- **No twin/conformance change** — the client edits (`ws.ts`, `ui.ts`, `init.ts`) are client-only (no C#
  twin); the SSR is C#. Evaluation semantics untouched.
- **Self-sufficiency:** the app's Code stays mount-UNAWARE (root-relative `path` + links); the base lives
  only at the edges. That is exactly what lets one instance serve a domain root.

## Local dev
One app port + one asset port (e.g. `localhost:8080/apps/todo`, asset `localhost:8081/apps/todo/ws`)
instead of N port pairs. `kernel.json` carries the two kernel ports + per-instance `{id, name, domain?}`.
