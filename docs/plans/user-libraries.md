# User libraries — the local library mechanism (pre-ecosystem)

*2026-07-10. Triggered by the user asking to plan/spec the library ecosystem
("GitHub + Visual Studio + npm style"): lib-only projects, using a library from
another library/app. Status: **design draft, grilled ×2 (fable) + vision-keeper
scoping — not accepted, nothing scheduled.** Build is gated on (a) M12's
scope-chain-sensitive work closing (W2 → S4 → hybrid editor sit on the very
`system ← lib ← app` chain this touches) and (b) a REAL cross-app duplication
existing — a survey of every tracked app document found **zero** duplicated
custom fns across instances today, so the first consumer is still speculative
(the project's own <3× abstraction guard applies).*

## Scope fence (vision-keeper, settled)

This designs the **local library mechanism only**: libraries inside your own
kernel, consumed by your own apps/libs. The registry / sharing / marketplace /
trust-and-sandbox / payments story stays the STAGES.md ecosystem capstone
(STAGES.md:168-177), untouched — the local mechanism crosses none of its gates
(all code is yours and trusted, single-operator). Two hard constraints so the
capstone stays open:

1. **Pin-and-link, never vendor.** A published design that physically contains
   lib code dissolves the library's identity (nothing to register), would
   redistribute a future paid artifact in cleartext, and discards the
   dependency graph. Pins + load-time linking give reproducibility without any
   of that; a future registry becomes "resolve the same dependency ref from a
   remote source" — a pluggable resolver, app document unchanged.
2. **Libraries live image-side** — design-host Designs like any app design,
   never more kernel-hardcoded sources. This is the path by which even the
   stdlib eventually becomes an image lib (kernel-vs-image north star).

## Settled foundation (confirmed against code)

- **A user library = exactly what the stdlib already is.** Today's "library"
  is a source text parsed into a scope between system and app: the chain is
  built per render (SsrRenderer.cs:268-270); fns are placed into the `lib`
  scope iff their name is in the stdlib's name set (SsrRenderer.cs:353,
  GenericUi.cs:786); the stdlib is ONE hardcoded string (`StdlibSource`,
  GenericUi.cs:147) parsed fresh per call, and `GenericUi.Effective` merges
  library-then-app fn lists and renumbers ids (GenericUi.cs:762-780). The
  design generalizes 1 hardcoded string → N user-authored library documents.
- Name resolution is first-hit-wins walking scope parents (CodeExecutor.cs:
  299-304, 2674-2680); in practice app-over-lib shadowing is implemented as
  **last-wins replacement in one dictionary** (an app fn named `Field` lands
  in the lib scope and overwrites the stdlib entry — SsrRenderer.cs:353,
  DefineFunction :1061-1069). The client is the same shape: no document
  parser at all, it receives the already-merged `initUi` fn list and defines
  everything last-wins into one flat scope (SsrRenderer.cs:148-160,
  init.ts:46-47). **User-lib fns therefore ship to the browser for free.**
- The app document grammar is a closed section set with `types` REQUIRED
  (AppParse.cs:259-270); a types-less doc is rejected at THREE layers —
  parser, validator (root `Db` required, InstanceDescriptionLoader.cs:62-73),
  and projection (fns-without-render refused, already marked interim:
  SchemaBridge.cs:105-116). There is NO import/module syntax in the grammar.
- Designs are rows in the design-host with M13 commit history; instances pin
  `DesignId` + `PublishedCommitId` in kernel.json (Registry.cs:27-39);
  time-travel resolves era schema via publish **boundary markers**
  (`BaseCommitId`, retrofitted additively with tolerant reads —
  KernelHost.cs:946-1000).
- The evalContext already reserves an empty `uses` slot "for the follow-ups"
  (SsrRenderer.cs:1393) — the design-time seam was left open on purpose.

## The design

### 1. A library is a Design with no instance *(settled)*

New design kind `library`: a code-only document — `common` and/or `ui` fns.
No `types`, no `initialData`, no `access` in v1. **Top-level `var` is
FORBIDDEN in a v1 lib document** (parser/validator error): grill #1 proved the
supposed machinery doesn't exist — `Effective` silently drops lib vars today
(takes only Functions+Render, GenericUi.cs:762-777) and the renderer's lib-var
placement branch is dead code. Components' per-slot `var state` (fn-body vars)
is unaffected. Lib-scope vars = their own later slice.

A lib lives in the design-host like any design and inherits M13 commit history
for free. Nothing boots; its preview surface is the component workbench (§8).
The document pipeline changes are real and named: a parser branch for
kind=library (types optional), a validation branch (no-Db legal), and lifting
the projection refusal (already marked interim in its own comment).

### 2. Dependencies: `uses` is structured truth, printed as `use` lines *(settled, grill #1 flipped the draft)*

A consumer (app OR library — transitive) declares deps in a `uses` field on
the structured Design row; projection PRINTS them as a `use markdown, charts`
header in the document — the same authority inversion as M12 types
(SchemaBridge: "structure = truth, printed text = artifact"), so round-trip
falls out of the existing pattern.

Resolution: `use <label>` resolves label → DesignId **once**, at first
resolve; the stored ref is the id, the label display-only thereafter
(rename-safe). Head = main-branch head (`FindMainBranch` precedent).
Duplicate labels = resolve-time error. The dep graph is a DAG; cycle = error.
Same lib reachable twice = one copy.

VS project-reference semantics, not npm pinning: design-time always resolves
against the lib's current head (you own the libs); PUBLISH pins. Semver ranges
and lockfiles layer onto the same `use` line when the registry exists —
absent, not foreclosed.

### 3. Pin at publish — two channels *(settled, grill #2)*

Publish resolves each dep to `(libDesignId, commitId)` and stamps the pins in
two homes with one writer:

- **History channel: the publish boundary marker**, beside `BaseCommitId` —
  the field invented for exactly this historical-resolution gap, with a proven
  additive-retrofit path (tolerant reads, KernelHost.cs:968-971). Written only
  on the versioned leg, so pin history degrades **in lockstep with era-schema
  precision** — never worse (`ResolveEraDoc` already walks the same
  no-boundary cases).
- **Current channel: a pins mirror on `RegistryEntry`** (kernel.json) — rides
  the existing `StampPublishedCommitAsync` read-modify-write pattern
  (KernelHostActions.cs:209,219); the JSON reader is tolerant of the new
  field.

**The invariant, pinned by test: no path writes a schema file containing `use`
lines without also writing the current-pins mirror.** That covers ALL doc-
writing paths, not just Publish: the NoHead publish leg and `Create` become
first-time registry writers; `SetDesign` (projects the working copy) must
resolve + mirror too. Pins never live in the doc text (breaks the HeadText
byte-identical write + canonical-text stamping) and never registry-only
(loses time-travel).

**Never-committed lib: publish REFUSES**, per-dep, with the remedy ("commit
'<lib>' first — consumers pin lib commits"). The check consults the LIB's
commits, so an app's own zero-commit NoHead publish is untouched. The
asymmetry is deliberate and named: an app may publish its uncommitted working
copy; its libs must be committed. `sys.publishPreview` REPORTS the missing lib
commit as a report row rather than throwing (the preview's no-side-effects /
full-honesty contract, Track-B UX). Auto-snapshot was rejected — a hidden
commit is invisible history. *(Default judgment call, not a user decision.)*

### 4. Link at load — through a per-instance lib cache *(settled, grill #2)*

At load, the kernel parses each pinned lib source fresh — the StdlibSource
pattern — and hands the texts to the loader. But NOT read live from the
design-host store at boot: that would make one instance's data file a boot
dependency of every consuming app, recreating the 2026-07-02 outage class the
per-instance boot guards exist to prevent (KernelHost.cs:376-393, individually
try/caught 503s).

Instead: **`instances/<id>/libs/<designId>-<commitId>.deenv`**, written at
publish, read at boot. Immutable content addressed by pin = a cache, not
vendoring — identity and the dep graph live in the pins. Consequences,
verified: cache-miss lands in the existing per-instance boot guard (that
instance 503s loudly with a remedy; every other app boots); `Delete` takes the
dir for free (recursive); **`Clone` must add an explicit `CopyLibsDir` step**
— the blob-pool comment pins the rule verbatim ("clone = whole-pool copy, an
EXPLICIT step, NOT free composition", KernelHost.cs:883-884); nothing else
enumerating `instances/` sees the subdir (`blobs/` is the working precedent).
A deleted lib design degrades from boot-failure to can't-republish. The
designer additionally REFUSES deleting a design that any pin references (the
consumers-of-a-lib query is local design-host data); a pruned pinned commit
fails LOUDLY at republish (the `TryReadCommitText` stance).

### 5. Runtime composition: flat lib scope, ordered merge *(settled)*

`InstanceDescription` gains a libs member (lib sources ride the description —
`Effective` and its seven store-less test call sites can't reach any store);
`Effective` merges **stdlib → libs (declaration order) → app** into the one
fn list it already builds, and lib fn names join the set that places them in
the `lib` scope. ONE flat lib scope — no N-deep chain (M12's canvas eval sits
on the 3-level chain; don't disturb it).

**Zero change to the two executors** — but that claim is now scoped honestly:
real changes land in InstanceDescription, the loader, Effective's inputs, and
the document pipeline (§1). The merge order is WIRE-visible semantics (client
init is last-wins overwrite, init.ts:47) — **pinned by a conformance test on
both twins.** Named ceiling: link-at-load parses N+1 documents per WsHandler
refetch (the renderer is rebuilt per mutation round-trip, WsHandler.cs:1173);
linear on the hot path, acceptable at v1 scale; caching parsed fns is blocked
by mutable CodeIds — the fix is an Id-assignment rework, named, not scheduled.
Second footnote: `initUi` payload grows by every lib's full fn set per page.

### 6. Collision policy *(settled)*

- App-over-lib shadowing stays (existing last-wins replacement).
- A user lib MAY shadow the stdlib — same right the app already has; it's
  merge order, silent by mechanism, made non-mysterious by the designer
  surfacing effective bindings.
- **Two different deps exporting the same name = error at RESOLVE time,
  surfaced live in the designer** — not a publish-time surprise. The resolver
  runs for evalContext anyway (§7), so the check is free at design time
  (canvas-never-lies guard honored).

### 7. Design-time resolution: one shared resolver *(settled)*

One function — name/id → Design row → text → parsed fns — living beside
SchemaBridge (Designer layer, reachable from both Kernel and Http),
parameterized ONLY by which text it fetches: pinned commit text (publish/load)
or head text (evalContext/canvas/workbench). No second resolution engine
(project guard). The evalContext's reserved-empty `uses` slot
(SsrRenderer.cs:1393) is where the resolved lib ASTs join the canvas.

### 8. Workbench as the lib's runnable form *(scoped honestly, grill #1)*

A lib has no instance; workbench configurations ARE its component gallery
(VS-style). True in v1 for literal-args / presentational components — the
actual shape of the first real libs (markdown, charts, date helpers). FALSE
for descriptor/extent-driven components (SetTable-class): with no types and no
initialData the sandbox seeds empty schema/extents. The fix is designed and
deferred: a lib-only **preview-fixtures section** (types + initialData
consumed ONLY by evalContext seeding, never merged into any consumer). Until
it lands, the flagship "stdlib becomes an image lib" north star cannot
fully preview in its own workbench — stated so nobody believes otherwise.

## Deliberately NOT in v1 (fenced, not foreclosed)

- Registry, remote resolution, sharing beyond your kernel, marketplace,
  payments, trust/sandboxing of foreign code — the ecosystem capstone, gated
  on multi-user + the sandbox problem, exactly where STAGES.md puts it.
- Semver ranges + lockfiles (meaningless while all libs are local head).
- Qualified names / namespaces (`markdown.Table`) — flat + collision-error
  holds until real collisions hurt.
- **Types/schema in libraries** (the ERP/eshop domain-template ambition) —
  drags in schema-merge + migrations + data ownership; own design session.
  App templates ≠ libraries: cloning a design already covers "start from an
  eshop". The designer's own fns are NOT extractable (they manipulate
  designer types) — don't cite it as a future consumer.
- Lib-scope `var`s (§1), preview-fixtures (§8), bulk-republish of consumers
  (staleness is explicit: a lib fix reaches a consumer when it republishes —
  npm/VS behavior; the consumers-list query is cheap later).
- Portable-image export materializes the dependency closure at EXPORT time
  (app + pinned lib designs travel together) — an export-time bundle is not
  publish-time vendoring; noted for the image story, not designed here.

## Open

- **Time-travel clone era-pins**: a clone at a NoHead-era boundary has no
  pin history — inherit current pins or fail loudly; same choice class as the
  existing unstamped-clone rule (KernelHost.cs:909-915). Decide at build time.
- Preview-fixtures section shape (sketched §8).
- Id-assignment rework if the N+1-parse refetch cost ever hurts (§5).
- The stdlib-as-image-lib migration itself — the mechanism opens the path;
  nothing scheduled.

## Grill record

- **Vision-keeper (scoping):** aligns if split local-mechanism-now /
  capstone-untouched; caught the original draft's publish-time **vendoring as
  a capstone foreclosure** → flipped to pin-and-link; libs must stay
  image-side; sequencing = design now, build after M12 + a real second
  consumer.
- **Grill #1 (fable, 10 seams):** REFUTED "zero change" as stated (lib
  sources must ride InstanceDescription; N-parse refetch ceiling; boot
  coupling = the 2026-07-02 outage shape → the lib cache), REFUTED doc-text /
  registry-only pin homes (→ boundary marker + registry mirror), REFUTED lib
  vars ("the stdlib pattern" has no vars; Effective drops them silently),
  half-hollowed the workbench claim (→ scoped + fixtures), flipped `uses` to
  structure-is-truth, pinned merge order as wire-visible semantics, and
  confirmed: client twin free, flat scope matches real shadowing, evalContext
  seam purpose-built, no real second consumer exists today.
- **Grill #2 (same agent, pin-home + boot-cache + refuse-publish):** all
  three HOLD with amendments folded above (leg coverage incl. NoHead/Create
  as new registry writers; the pins-mirror invariant test; `CopyLibsDir`;
  era-pins open item; per-dep check can't block an app's own NoHead publish;
  preview reports rather than throws). Verdict: ready to write up.

## Handoff

Not scheduled. When the gate opens (M12 closed + a real duplicated component
exists), hand to **milestone-planner**; the natural slice order:

1. kind=library Design + lib-only document (parser/validator/projection
   branches; `var` forbidden) + designer create/list badge.
2. `uses` field + shared resolver + design-time head resolution into
   evalContext (collision surfaced live).
3. Publish pins (both channels + invariant test) + lib cache + loader/
   Effective merge + the merge-order conformance test — one app consuming one
   lib end-to-end.
4. Transitive deps + cycle/dup-label errors + clone `CopyLibsDir` +
   delete-guard.
5. (Later, separately gated) preview-fixtures; lib-scope vars; types-in-libs
   design session.
