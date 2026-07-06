# Stages

The **product-stage** lens on the mission: coarse tiers of operator value, from
"build a small app" to the full distributed vision. This is *not* a replacement
for the other docs — it sits across them:

- **VISION.md** is the destination (the nine pillars). **ROADMAP.md** sequences
  *implementation* milestones. **This** groups the experience into a few
  user-facing stages so the endgame is legible and each stage's edges are drawn
  against the next. Stages reference pillars and milestones; they don't restate
  them.

A stage is defined by *what an operator can do*, not by which code exists.

---

## Stage 1 — MVP: build a small app (single operator)  ← settled

**Goal.** A data-model-literate operator (not necessarily a developer) can build
and run a small database-backed web app, hand-authored, and it persists and
reacts. This is the "build simple apps for one or few users" tier.

**Single operator, small data.** The operator builds the app *and* is its user;
the built app is **not** concurrent multi-user. No live cross-user updates, no
per-user permissions, no concurrent-write safety. The moment "a few users" means
*simultaneous, live, per-user* access, that is Stage 3 territory (the real-time /
multi-user pillar), not Stage 1.

**Capability bar** — the MVP must express these with **zero config in the common
case** (minimal by default):

1. Objects with typed scalar fields — text, number, bool, **date/time**,
   **money**, and a **status enum**.
2. **Sets** of objects with identity.
3. **References** — shared identity, including two-hop (e.g. Appointment → Dog →
   Owner).
4. The **line-item / association object** — quantity *on* a relationship (order
   lines, ingredient lines, parts used). The most recurring non-trivial shape.
5. A **status/lifecycle** field driving the app (e.g. lead → quoted → scheduled →
   done → paid).
6. **Light computed values** — line-item totals, sums, hours (object code, no
   server calls in user code — pillar 2).
7. **Dictionaries** where the key is meaningful chosen data (plans, settings).

Top-end stress cases (the line between "capable" and "toy"): the **attendance
grid** (many-to-many over time) and an **invoice summing referenced time
entries**. If these fit Stage 1, the MVP is genuinely capable.

**Target apps** (variety, each stressing a different shape): masseur/therapist
booking, interior-painter jobs, handyman work orders, dog groomer, private tutor,
photographer, freelance CRM + invoicing, recipe box, small eshop + orders, class
roster + attendance, vehicle-maintenance log, home inventory, gym membership.
These map to VISION.md operator situations 1, 2, 3, 5.

**Explicitly out of Stage 1** (so scope stays honest): concurrent/multi-user,
operator self-service schema editing, git-style versioning, live preview / test
instances, crash durability. These are Stage 2+ and are real future milestones —
not details to bolt on (AGENTS.md rules 2 + 10).

---

## Stage 2 — Operator self-service & safe evolution  ← DRAFT (open questions)

**Goal.** The operator can *safely evolve* an app he's already running — change
things himself, without a developer and without fear of breaking live data.

**The unifying mechanism (sketch).** Versioning, test instances, and live preview
are one mechanism wearing three hats — git's model applied to schema (and data):

| Operator sees | Git equivalent |
|---|---|
| a new instance for testing | a **branch** |
| changes immediately, without making them real | the **working copy** (dirty) |
| making it real | **merge** to main |

**The precise model (this table is the operator analogy).** An **instance is not a branch** —
it *pins a specific commit* and holds a branch ref as a *guide* (a **checkout**, not a branch).
So "a test instance ≈ a branch" here, and "test instance (branch)" below, are the operator's
mental model; the literal data model is the checkout-at-a-commit one in **DECISIONS** —
"Versioning — unified schema+data, Git-for-data, the instance pins a commit."

Two loops at different scopes (they stack — not either/or):
- **Inner loop — mini-instance.** While editing one form/type, just that slice
  renders live against representative data, updating as he edits. Instant,
  focused.
- **Outer loop — test instance (branch).** A full fork, **snapshot-seeded** from
  real data by default, where the whole flow (incl. destructive/structural
  changes + migration) is validated before promoting.

**Foundations already in place.** Identity on every non-constant (M5) makes the
schema diff *exact* (renames detected, not guessed); a render-coupled storage
engine (pillar 5) is what would make live preview / branches cheap (copy-on-write,
instantiate only what's on screen).

**Maps to pillars.** 1 (visual data design), 3 (git-style schema versioning), 9
(IDE-grade environment); 5 as the enabler.

**The boundary to hold.** "Change some things himself" = **data + schema** edits
(add field, add status option, rename, edit records) — squarely the operator's
job. **Behavior / code** edits route through the AI-assisted-operator +
**developer-review** path (per VISION.md "where code fits"); self-service must not
quietly come to mean "a non-dev ships unreviewed code."

**Open questions** (to settle before this firms up):
- Scope of "some things" — exactly which schema edits are operator-self-service
  vs. developer-reviewed? (Proposal: additive/rename data+schema = self-service;
  behavior = reviewed.)
- Branch data seeding — snapshot (diverges, simple) vs. live-link (current, harder
  merge)? (Proposal: snapshot-first.)
- Is the inner/outer-loop + "branch = test instance" framing the right shape, or
  is something lighter wanted?

---

## Architectural north star — deenv as a self-hosted image

Cuts *across* the stages rather than being one of them: the endgame where **deenv
runs itself**. A thin, trusted, compiled **kernel** (interpreter, storage,
multi-instance/port supervisor, boot) hosts a malleable **image** — the IDE, the
designer, and every app as data + Code. The IDE is just an instance, seeded via
`initialData`; the kernel runs it and spawns more instances on more ports. "deenv
changes itself" = the *image* changes, safely, via the Stage 2 branch→preview→
promote loop; the kernel evolves by recompile/redeploy, never live
self-modification (the recovery floor).

This is the terminus of the self-hosting path (M4 designer-as-instance, M9
self-hosted UI) and the concrete form of pillar 9. It is reached incrementally —
multi-instance hosting, then versioning (Stage 2) as the self-edit safety net — and
reaches full form alongside the distributed runtime (Stage 5). The data
architecture it implies (per-instance sovereign dbs, a kernel-owned data layer, the
kernel as a *restricted instance*, cross-instance ops without distributed ACID) is
recorded in **DECISIONS.md → "The self-hosted image — kernel, instances, and
cross-instance data."**

**The devops thesis — operations as a single act.** This north star is also how
deenv answers the *devops* half of the mission (VISION.md). All of operating the
system collapses toward **one irreducible act: place the kernel on a machine and
start it once.** From that seed the running system does the rest *from within* —
it hosts, it spawns instances, and later it versions and repairs itself. Devops
does not vanish (someone still places the seed); it **shrinks to a single step**,
and how small that step becomes (one binary, one command) is real design work,
not a given. This is the concrete form of VISION.md's "Positioning &
sustainability": collapse the stack, run on ordinary hardware, no ops team —
cheaper than cloud *by construction*.

**Fractal hosting (late).** Because an image is an image — the kernel does not
care whose — the same hosting primitive runs at every scale: your own app, your
team's, or *other people's*. A self-hosted kernel can therefore *become* a
hosting provider, and "self-host" vs. "let someone host for you" stops being a
fork. This is a real destination but a **late** one: hosting strangers demands
**fault + resource isolation** (one tenant cannot crash or starve another) and
**untrusted-code sandboxing** (a tenant's Code is now a threat model) — both
currently out of scope — and, at scale, the distributed runtime. Written here as
a capability the architecture *enables*, not near-term work.

**Recovery floor (open question).** The section above names *the recovery
floor* — the guarantee that a botched self-edit can be undone. Its likely shape:
a **"safe mode" in which the kernel mints a fresh, known-good default IDE
instance** (recovery as just another instance spawn) from a default that lives
where a failing image cannot corrupt it. The hard parts are deferred and
unsolved: *where* that trustworthy default lives (compiled into the kernel? a
read-only kernel seed? the git-committed known-good, re-checked-out?), and the
trade between a **frozen** default (always safe, but stale — it loses the IDE's
own self-hosted improvements) and a **last-known-good** snapshot (fresher, but it
must be proven uncorrupted). Named here so the self-operation promise stays
honest, not solved.

**The ecosystem (capstone).** The furthest destination: an **npm- and
GitHub-for-apps** — a registry and collaboration surface for libraries and whole
apps (portable images: data + Code), with a **marketplace where creators are
paid for their work.** It is VISION.md's community-and-network moat made real. It
is also a *second product layered on the first*, lighting up only **downstream of
almost everything**: a registry needs schema versioning (pillar 3); collaboration
needs multi-user (Stage 3); sharing libraries means running others' code (the
trust/sandbox problem above); "getting paid" needs a payments + licensing layer.
The latest of the late — and the point at which the community, not the code,
becomes the durable advantage.

## Later stages — not yet drawn

Placeholders, defined by the remaining pillars (see VISION.md / ROADMAP.md):

- **Stage 3 — Real-time / multi-user.** The built app serves concurrent users
  with live updates and conflict resolution; concurrent-write safety; needs schema
  versioning for structural conflicts.
- **Stage 4 — Custom render-coupled storage engine; data-level temporal
  versioning** (time-travel over live data).
- **Stage 5 — Multi-device / distributed runtime + distributed ACID.** A
  **single-system-image**: seamless, "same as one machine," because pillar 2 (no
  server calls in user code) made user code distribution-blind — only reference
  *resolution* changes, not user code; M5 identity (resolvable anywhere) is the
  other half. The kernel gains a **fabric** (transport/membership/coordination) in
  the trusted floor; the **image configures topology** as kernel-owned data via the
  privileged control-plane path. **Destination upgraded 2026-07-06** (vision
  change; docs/plans/distributed-acid-design.md): not only placing and
  replicating whole instances, but **sharding a single app's data across
  machines** with cross-shard ACID transactions and auto-rebalancing (the
  Spanner/CockroachDB class) — reached through the replication-first ladder
  (instance = the first, coarsest shard granularity), with the **deterministic
  fault-injecting simulation harness as this stage's first brick** (it is what
  makes AI-speed implementation verifiable). The unavoidable cost is **CAP** — a
  strongly-consistent single-system-image is unavailable under partition;
  versioning (3+4) reconciles where divergence is allowed, and render-coupled
  storage (5) hides latency by remote preload. The hardest part of the mission,
  and it stays last.

These stay in the vision; they are simply not next. Stage boundaries here are
provisional and will be redrawn as the earlier stages land.
