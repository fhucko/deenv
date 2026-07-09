---
name: design
description: Draft a design/plan doc for a non-trivial direction — analysis grounded in the real code, then an adversarial self-grill before anything counts as settled. Produces docs/plans/<name>.md (+ a companion grill-<name>.md for a large multi-topic grill). Use for a milestone-shaped question, an architecture decision, or a direction with real unknowns — NOT for something you can just answer or build directly. Args: the design question or direction.
---

# Design a plan (analysis + self-grill)

This project's actual practice for a non-trivial direction: draft a position
grounded in the real code, then adversarially interrogate that position
before treating any of it as settled. See `docs/plans/app-versioning-design.md`,
`docs/plans/grill-results-8-topics.md`, and `docs/plans/security-review-pre-public.md`
for what this looks like landed. Output is a **design doc, not a build
plan** — it gets handed off, not implemented by this skill.

## 1. Is this actually plan-worthy?

A full draft-then-grill is for a direction with real unknowns — spans
milestones, changes a model/schema/wire/interface shape, or has more than
one defensible approach. If the answer is already obvious from AGENTS.md,
DECISIONS.md, or a quick code check, just answer or build it — don't
manufacture ceremony for a question that isn't actually open (Ponytail:
rung 1 of the ladder is "does this need to exist at all"). If the direction
itself might pull in a future milestone or foreclose one, run
**vision-keeper** first — scoping the direction comes before designing it.

## 2. Draft the position

Read what's actually relevant before proposing anything: AGENTS.md's linked
docs, DECISIONS.md for prior precedent on the same ground, and any existing
`docs/plans/*` that already touch this area (cross-reference them as
companions rather than re-deriving). Then state the proposed approach —
and be explicit, inline, about which claims are **confirmed** (cite
file:line) versus **assumed**. If there's more than one defensible
approach, sketch the real candidates rather than silently picking one and
presenting it as the only option.

## 3. Self-grill it

Hand the drafted position to a **fresh `Agent` call on `fable`** — not the
same context that wrote it. The grill is the project's highest-leverage
judgment task and runs rarely (once or twice per design), so the top tier's
2× cost over opus is trivial next to a design flaw that gets built on for
weeks. **Exception: security-focused designs/reviews grill on `opus`** —
Fable's cyber-domain safety classifiers can false-positive
(`stop_reason: refusal`) on legitimate vulnerability analysis, which is
exactly the territory deenv security grills work in.
Adversarial distance is the point: brief it to
try to REFUTE the position, not bless it, and to check claims against the
actual code rather than reasoning about them in the abstract (a grill that
never opens a file is worth less than one that verified even two or three
claims). Pick the shape by scope:

- **Topic-by-topic, 10-round Q&A** (`grill-results-8-topics.md`) — for a
  broad pass with several open topics. One round each: a question sharpens
  an assumption/edge/risk, the answer grounds it or names it unverified,
  ending in a per-topic verdict.
- **Inline position → "Grilled." bullets → verdict** (`app-versioning-design.md`)
  — for a single coherent draft with a handful of sharp concerns; more
  compact than full Q&A per topic.
- **Cross-cluster grill** (`security-review-pre-public.md`) — when several
  independent analyses (parallel agents, each owning one angle) already
  fed into the draft; this pass specifically hunts the seams BETWEEN them,
  not any one angle's own gaps.

A design can be grilled more than once as it evolves — number the rounds
and reference prior ones explicitly (e.g. "closes self-grill #2") rather
than re-litigating settled ground from scratch.

## 4. Mark status, not just conclusions

For each topic/finding: **settled** (and by whom — a user decision reads
differently from a default judgment call) or **open/deferred**. Name every
known ceiling explicitly — "works at today's scale, breaks at N" or "solved
on paper, untested" must say so in those words. A cheap-but-limited verdict
that reads as fully solved is a false claim (this project's own bar: don't
let a real gap hide behind a confident-sounding conclusion).

## 5. Write it to docs/plans/

- `docs/plans/<slug>.md` — a provenance line up top (date, what triggered
  it, current status — e.g. "design draft, not accepted, nothing
  scheduled" if that's true), then settled foundation, then the
  topic-by-topic passes.
- A large topic-by-topic grill gets its own `docs/plans/grill-<slug>.md`,
  cross-referenced as a companion from the main doc — don't inline ten
  rounds of Q&A into the main doc if it dominates the page.

## 6. Hand off — this skill does not build

Follow-ups from here: **milestone-planner** to slice a settled direction
into a current-milestone-shaped first step, **vision-keeper** if scope is
still contested, `/build` once an actual slice is ready to implement.

---

## Gotchas

- Don't skip grilling because the position "feels" right — confidence is
  exactly what the grill exists to test.
- Brief the grill agent as a skeptic, not a reviewer looking for a reason to
  approve — default to refuting.
- An ungrounded grill (pure back-and-forth reasoning, no file reads) is much
  weaker than one that checked even a few claims against real code.
- If grilling reveals the "direction" is actually trivial or already
  answered, say so and stop — don't force a doc into existing just because
  the skill was invoked.
- Isolated worktree, local main, same as `/build` — this repo runs multiple
  concurrent Claude sessions on the shared tree.
