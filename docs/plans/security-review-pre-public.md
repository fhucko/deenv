# Adversarial security review — pre/at-public (devlog goes live)

Run 2026-07-02, triggered by `devlog.deenv.org` going public. Three read-only adversarial analysts
(opus), each owning one cluster of deenv's unusual surface, then a cross-cluster self-grill + live
verification. Companion to the versioning grills. **One CRITICAL found, contained, and fixed; the rest
are lower-severity notes and follow-ups.**

## Headline — V1 (CRITICAL): anonymous host-action on a public instance's WebSocket

**Confirmed from code + deployed config.** Host actions (`create`/`delete`/`cloneInstance`/`publish`/
`rename`) run with kernel authority OUTSIDE the per-instance access floor: `WsHandler.ProcessMessage`
dispatches `"hostAction" => HandleHostAction` (`WsHandler.cs:363`) which calls `_hostActions.Run(action,
args)` with **no session, principal, or instance check** (`WsHandler.cs:927-936`); `KernelHostActions.
Delete/Clone/Rename` take a bare instance id and don't check the caller (`KernelHostActions.cs:125-163`);
and `KernelHost.HostActionsFor` wired a **real** `KernelHostActions` into **every** hosted instance
(`KernelHost.cs:97-115`), despite the comment "ASSUMPTION: sys.publish is only meaningful from the
DESIGNER." Because this session had ungated `devlog`'s public `/ws`, one anonymous frame —
`{"op":"hostAction","action":"delete","args":[{"type":"int","value":1}]}` — would delete the designer
(then 2–6, every app). Full multi-instance compromise from an unauthenticated visitor.

**Response (2026-07-02, in order):**
1. **Contained immediately** — restored the gated nginx config (reverted the session's devlog ungate).
   `devlog` and its `/ws` return 401 to anonymous clients again. Verified. Public config preserved on
   the box as `deenv.conf.ungated` to re-apply after the fix.
2. **Verified empirically** the raw app/asset ports (8080/8081) are NOT publicly reachable
   (loopback bind + ufw) — so nginx is the only public door and re-gating fully contains V1; there is no
   direct-to-asset-port bypass.
3. **Fixed** (branch `hostaction-operator-gate`): `KernelHost.HostActionsFor(spec)` now returns
   `NoHostActions` for any instance that is not the design host (`!IsDesignHost(spec)`), and the real
   `KernelHostActions` only for the design host; `NoHostActions` gained a `reason` for a clear reject/log.
   Enforces the invariant the code already assumed. Regression test (real kernel-boot harness) +
   architecture review + redeploy + re-open devlog to follow.

## Full findings

| # | Cluster | Finding | Severity | Status |
|---|---------|---------|----------|--------|
| **V1** | floor/auth | anonymous host-action via public `/ws` deletes any instance | **CRITICAL** | **contained + fixed** |
| V2 | wire/floor | ruled **dictionary** entries skip read+write floor | LOW (latent MED) | inert in devlog (no dicts); documented constraint |
| V3 | auth | `clientId` is an unauthenticated bearer token (in page HTML) | LOW | hardening later (bind principal to connection) |
| V4 | auth | login **timing** side-channel — unknown-user skips PBKDF2 → username enumeration | LOW | fix = dummy-hash the miss path |
| V5 | perimeter | committed `DEENV_ADMIN_PASSWORD: "admin"` in `DeEnv/Properties/launchSettings.json:8` (public repo) | LOW (dev-only) | **user decided 2026-07-02: LEAVE** — accepted dev-only default (prod unaffected; VS-launch/localhost only) |
| — | perimeter | cross-instance path traversal past the gate | REFUTED | structural (instance bound at path segment 2; nginx forwards raw `$request_uri`; GenHTTP doesn't normalize but can't cross) |
| — | perimeter | WS cross-instance targeting | REFUTED | socket→instance binding is structural, not message-driven |
| — | client-data | crafted `(action,state)` intent harvests denied data | REFUTED | floor applied at graph-LOAD; footprint can't contain what the graph lacks |
| — | client-data | password hash/plaintext ships on any outbound path | REFUTED | blanked at every load seam, keyed on the `password` type |
| — | client-data | wire IDOR / type-confusion / negId | REFUTED (in devlog) | every relation drives a floor check; type derived from the join, never wire-asserted |
| — | perimeter | box IP/keys, error traces, dir listings, source maps | REFUTED | nothing sensitive leaked; errors return message-only, no traces |

**The strategic residual (not a bug — the acknowledged interim posture):** the designer has NO app-level
auth of its own; the whole gated subdomain set rides on ONE shared nginx basic-auth password. That
htpasswd is the only thing between the internet and the designer's create/delete. The durable fix is
wiring the designer's own login (the deferred M-auth follow-up), not the proxy gate. V1's fix removes the
*orthogonal* hole (host actions were reachable from ordinary apps too, entirely outside the gate).

## Self-grill (cross-cluster, adversarial) — the seams the per-cluster agents couldn't see

1. **Is V1's fix complete, or is there a sibling floor-bypass op?** Dispatch ops: data ops
   (objectPropChange/commit/arrayAdd/arrayRemove/setReferenceField/removeEntry) are all floor-gated;
   refetch is read-gated; login/logout are authentication; hello/ackRemap are transport. `hostAction`
   was the sole floor-external op. No sibling. ✓
2. **Is the fix's gate spoofable?** `IsDesignHost` is a **schema-shape** check (`Db { designs: set of
   Design }`), not an identity check. Consequence: any instance whose schema declares that shape gets
   host actions. On the box only `instances/1` does (verified). An attacker cannot create such an
   instance without designer access (already game-over). So **V1's anonymous path is fully closed**;
   the schema-shape-as-identity is a hardening nuance → FOLLOW-UP: designate the operator instance by
   identity (kernel.json / registry flag), not schema shape.
3. **Does re-gating actually contain V1 pre-deploy, or is there a port bypass?** Raw ports 8080/8081
   probed from off-box → connection refused (loopback + ufw). nginx is the only public door; re-gating
   `/ws` fully contains. ✓ (empirically verified, not just config-trusted)
4. **Cross-cluster interaction (host-action path × footprint):** host actions return only ok/error and
   harvest no data — independent of the footprint/floor path. No leak via the seam. ✓
5. **Does the fix break legitimate use?** `instances/1` passes `IsDesignHost` (verified) → designer
   keeps create/delete/publish. `HostActionSteps` construct `KernelHostActions` directly (unaffected).
   Legitimate host actions all originate from the designer. Regression test confirms both directions.
6. **Perimeter traversal empirical confirm:** deferred — devlog is re-gated (401 blocks before routing);
   run the traversal probes after re-opening devlog post-deploy to confirm the structural REFUTED
   verdict in practice.

## Follow-ups (ranked)

1. **Designer app-level auth** — the durable fix for the strategic residual (replaces the shared
   basic-auth gate). Deferred M-auth follow-up; the real answer, bigger than a slice.
2. **Identity-based operator designation** — harden V1's fix: the kernel should know WHICH instance is
   its operator host by identity (kernel.json), not by schema shape. Small, closes the round-2 nuance.
3. **V5 — remove committed `admin`/`admin`** from `launchSettings.json` (user's call; deliberate dev
   convenience vs public-repo optics). Move to user-secrets / gitignored local override.
4. **V2 — dict floor** (read+write) — land the deferred gate; until then, a hard constraint: no ruled
   dictionary in a public app.
5. **V4 — login timing** — verify unknown-user against a fixed dummy hash so both branches run PBKDF2.
6. **PathRouter defense-in-depth** — reject `..`/`.`/empty path segments before resolving (closes the
   traversal class permanently regardless of proxy-config drift; not needed today).
7. **V3 — clientId** — bind the principal to the connection rather than a replayable id.
