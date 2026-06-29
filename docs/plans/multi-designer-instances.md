# Multi-designer instances — milestone plan

> **Status: DEFERRED (2026-06-29).** Fully planned, not being built. It serves the
> github-style multi-developer pillar + the self-hosted-image north star, but it's
> **Stage-2 — ahead of the MVP "presentable with one app" bar**, and barely advances the
> self-editing-ERP dogfood (already done single-designer). Picked up when independent
> designers actually matter. Near-term designer-cleanup uses the general lib extractions
> in `designer-lib-collapse.md` instead. Memory: `project_multi_designer_instances`.

Instances become first-class designer-**stored** objects; the kernel inverts to an
**id-keyed runtime-projection** target; URLs/domains use **names** as a facade over
ids. This removes the foreign-kernel-id fragility (the cross-id-space match
`sys.id(d) == i.designId`, kernel ids carried in app-visible props) and enables
**independent designers**. Chosen 2026-06-29 as the root-cause fix over the `idProp`
interim. Memory: `project_multi_designer_instances`. *Stage-2 milestone, not the MVP-dogfood path.*

## Model (locked)

- **Identity = ids, everywhere internal** (references, hosting, the kernel registry).
  **Names = URL/domain facade**, resolved name→id at the edge. **Rename changes the URL,
  never identity or references.**
- **The designer stores its instances:** `Db { designs, instances }`;
  `Instance { name text, design → single Design }` (a real intra-store object reference).
  `db.instances` = the desired-state **source of truth**; the kernel registry = the
  **runtime projection** (what is actually hosted). Ops write `db.instances`, then project
  to the kernel — the same dataflow as design→app-doc (`SchemaBridge`).
- **A designer IS an instance** (it has a `db.instances` of what it manages). The **root
  designer** is the bootstrap.
- **D1 (locked) — storage collapse:** `instances/<designerId>/<instanceId>/`. The path IS
  the `(owner, instance)` identity; one id system, designer-namespaced on disk. Near-term
  `ownerDesignerId` = the root designer; designer-under-designer deep nesting is deferred.
- **D2 (locked) — designer names in the stores:** `designerName → designerId` resolves via
  the **root designer's `db.instances`** (sub-designers are its instances). The root
  designer's name is a **bootstrap constant**. Names live in stores, never the kernel.
- **URLs:** path `/<designerName>/<instanceName>`; subdomain
  `<designerName>-<instanceName>.deenv.org` (an nginx rewrite). **Uniqueness:** instance
  name unique within a designer; designer name unique globally (GitHub `owner/name`).

## Routing reality today (what changes)

A request hits a shared `KernelHost` fronted by `PathRouter` ([PathRouter.cs:42](DeEnv/Kernel/PathRouter.cs:42)):
it matches the literal `apps` segment, takes the **next segment as the instance name**, and
`ByName`-scans the live set matching `Spec.App` ([KernelHost.cs:84](DeEnv/Kernel/KernelHost.cs:84)).
Storage is already id-only (`AppPaths.*ForId`); only **addressing** uses the name. The
subdomain is a pure nginx rewrite (`<sub>.deenv.org` → `/apps/<app>`) — the kernel has no
`Host` routing. This milestone flips the routing key from a single global `name` to a
`(designerName, instanceName)` pair resolved name→id at the edge, and collapses storage to
the per-designer path.

## Slices (locked sequence, smallest-dependency-first)

1. **Foundation — stored `db.instances`.** Add `Db { designs, instances }` +
   `Instance { name, design → ref Design }`; seed `db.instances` from the registry at boot
   (`SyncDesignHost`/`DesignerSeed`); verify by WS read. The **visible list stays on
   `sys.instances`** (no create-staleness regression). *Independent of every decision below.*
2. **Kernel registry reshape + storage collapse** *[wire/storage shape change]*. Drop the
   `app` name; key by `(designerId, instanceId)` + `ownerId`; storage →
   `instances/<designerId>/<instanceId>/` (touches `AppPaths`, every id-dir, `NextInstanceId`,
   `kernel.json` migration). **Heaviest slice** — a bad migration orphans every instance;
   keep the old reader until the migration test is green, then delete it.
3. **Invert the host actions.** `create`/`setDesign`/`delete`/`rename`/`clone` write
   `db.instances` (desired state) then **project to the kernel by id** (mirrors `SchemaBridge`).
   `rename` becomes store-only (the name left the registry in slice 2).
4. **Name→id at the path edge** *[routing change]*. Replace `PathRouter`'s single-`apps`
   logic with a two-segment resolver: `/<designerName>/<instanceName>` → (designerName→id via
   the root's `db.instances`, D2) → (instanceName→id in that designer's `db.instances`) → the
   hosted instance. Retire the `apps` literal.
5. **Subdomain facade** `<designerName>-<instanceName>.deenv.org` — an nginx dash-rewrite to
   the slice-4 path resolver; the kernel stays `Host`-unaware.
6. **Uniqueness enforcement** — instance name unique within a designer (a `db.instances` store
   invariant at create/rename); designer name unique globally.
7. **Bootstrap** — the root designer hosts itself (owner = self; name + id are the bootstrap
   constants); reachable on a fresh boot with no prior registry writes.
8. **Shrink `sys.instances` + the SetTable collapse** *(the payoff)*. The visible list moves
   onto `db.instances` via the generic `SetTable` (no `idProp`, no transient-id addressing);
   `sys.instances` shrinks to a runtime-only cell. Closes the slice-1 staleness gap.

**No interpreter change anywhere → no twin/conformance case.** The write path is
`IInstanceStore`; the registry stays the dependency-free bootstrap floor.

## Deferred routing decisions (settle at slices 4–5)

- **Subdomain resolver** — lean: nginx rewrites the dash to the path form; the kernel stays
  `Host`-unaware (consistent with the dropped `domain`-field direction).
- **`/apps/<name>` migration** — lean: hard cut, retire `apps`, one migration commit (updates
  the kernel/designer/host-action features + `MountBaseTests` + `InstanceContext`/`KernelSteps`
  + `DEPLOY.md`).
- **Dash-in-names ambiguity** (subdomain only) — lean: forbid `-` in names (a routing-facade
  validation rule), OR a non-dash subdomain separator. Revisit at slice 5.

## Out of scope

- **Designer-under-designer deep nesting** — near-term is the root designer + its instances.
- **Concurrent-write / store-vs-registry locking** — single-operator assumption holds (the
  ghost-write caveat stays the deferred concurrent-write milestone).
- **Per-designer access-control enforcement** — `ownerId` lands the *data*; enforcing it
  against the M-auth principal is M-auth follow-up.
- **Cross-machine / multi-kernel ownership** — Stage 2+.
