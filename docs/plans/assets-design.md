# Assets — content-addressed blob pool + `image` scalar (design)

*2026-07-06. Triggered by the Track C brief (post-m13-backlog.md "Assets — FIRST in the
design queue"; serves the dogfood gate — devlog wants images). Status: design draft,
self-grilled ×1 (grill record at bottom; its refutations are folded into the body).
The one open question — prod URL shape — was settled by the user 2026-07-06: asset-port
paths like /ws and /js (§3). No open questions remain; ready for milestone-planner
slicing.*

Companions: docs/plans/app-versioning-design.md (§0b non-temporal, §6 compaction),
docs/plans/post-m13-backlog.md (the brief), docs/plans/login-persistence.md (the
/session + cookie precedent), docs/plans/security-review-pre-public.md (the floor).

## The one-sentence position

A per-instance, append-only, content-addressed blob pool at `instances/<id>/blobs/`
beside the JSON store; app data holds only the blob name (`<sha256-hex>.<ext>`) in a new
text-shaped `image` scalar; the ONLY new machinery is the pool and its two HTTP edges
(POST upload in, GET serve out) — history, time-travel, merge, publish, revert, and
migrations all carry the hash string through the machinery M13 already built, unchanged.

**Drift guard (user, 2026-07-05/06):** if this design starts specifying asset
*versioning*, it has drifted. Hashes ride the existing commit/log machinery; there is no
blob history, no blob diff, no blob merge. A changed image is a changed string in the
data log, nothing more.

## Why content-addressing is the load-bearing move (not a storage trick)

- **Time-travel is free.** The log/commits store only the hash string. Materializing an
  old era (MaterializeAtSeq, JsonFileInstanceStore.cs:1944) yields old hash values, and
  because the pool is append-only, those blobs still exist. No era-aware blob logic
  anywhere.
- **Erasure stays lawful inside immutable history.** The append-only log can never hold
  erasable bytes (GC ≠ erasure: `Remove` entries carry the full prior object forever,
  AppLog.cs:87-90). Blobs never enter the log, so erasing a person's photo = deleting
  one file from the pool. History keeps a dangling hash (the UI shows a placeholder);
  the preimage is gone. This converges with the §0b non-temporal question without
  needing §0b built first. *Qualifier (grill #1): erasure deletes the ORIGIN bytes;
  browser/proxy caches that honored the immutable Cache-Control may serve stale copies
  up to max-age (see ceilings). Origin deletion is the guarantee, cache recall is not.*
- **Revert-publish needs nothing.** A revert is a normal forward publish
  (semantic-migrations decision); the reverted data re-references old hashes that are
  still in the pool. No special case.
- **Idempotent, lock-free writes.** Write temp file → hash → rename to
  `blobs/<hash>.<ext>`; a concurrent identical upload races benignly (same name, same
  bytes). Re-upload of an existing blob is a no-op. Dedup within an instance is
  automatic.

## Settled foundation (confirmed against code by the exploration pass)

- Per-instance layout is flat under `instances/<id>/`, ALL path derivation lives in
  `AppPaths` (Storage/AppPaths.cs:9-50) — the pool adds one rule there
  (`BlobsDirFor(id)` → `instances/<id>/blobs/`), keeping the one-home-for-paths seam.
- **fsck ignores the pool by construction** — it reads only genesis + log and replays
  (JsonFileInstanceStore.cs:1910-1920); a sibling directory is inert.
- **`Reset()` (setDesign/fallback wipe) ignores the pool by construction** — it deletes
  only `_logPath` + `_genesisPath` (JsonFileInstanceStore.cs:1676-1697). Orphaned blobs
  after a reseed are harmless in an append-only pool; reclaim belongs to compaction.
  (Both of these are "inert by construction, not designed to coexist" — the build slice
  adds a comment at both sites naming the pool as a deliberate non-participant.)
- The save path is scalar-blind: staging/ctx/atomic-commit/3-way-merge move opaque
  `NodeValue`s; the ONE type-aware step today is `HashPasswordFields`
  (WsHandler.cs:794). An image value is a `TextValue` end to end — zero new code in the
  write path.
- The `password` precedent shows exactly what a text-shaped special scalar touches:
  `BaseType` enum + `BaseTypes.ByName` (BaseTypes.cs:9-22), store tag mapping
  (`LeafBase`, JsonFileInstanceStore.cs:1577), **`DefaultBase` — the one switch that
  THROWS on an unmapped BaseType** (JsonFileInstanceStore.cs:2228-2243, needs an
  explicit `Image => new TextValue("")` arm), wire `DeserializeLeaf`
  (WsHandler.cs:1443), `ScalarBaseOf` (InstanceDescriptionQuery.cs:33), generic-UI
  dispatch points, and the designer's own `scalarTypes` picker array
  (instances/1/app.deenv:88). The TS twin needs **zero** interpreter changes (it
  defaults any non-bool/int scalar to text, codeExec.ts:679-683).
- HTTP: GenHTTP, two shared ports; per-instance asset tree already mounts `ws`, `js`,
  `session` (InstanceApp.cs:85-91). `/session` is the exact precedent for a non-WS
  endpoint; `PrincipalFromCookie` → `TokenAuth.Verify` gives any handler a verified
  userId statelessly (ContentHandler.cs:61-68, TokenAuth.cs:37-54). The app tree stays
  reserved-path-free (the project rejects framework-reserved app URL space) — both new
  edges go on the **asset tree**.
- There is NO multipart/binary handling anywhere today (only the /session JSON body
  read) — but none is needed, see Upload below.
- nginx (deploy/DEPLOY.md:136-171) proxies `= /ws` and `= /js` to the asset port; the
  new edges add a `location /assets/` block to the same port (user-settled, §3) and
  **`client_max_body_size` must be raised** (absent today → nginx's 1 MB default would
  reject uploads).

## The design

### 1. The pool

`instances/<id>/blobs/<sha256-hex-lowercase>.<ext>` — flat directory, per-instance
(data sovereignty per id; nothing in the codebase shares files cross-instance, and this
design doesn't start). Append-only: **no code path deletes a blob** except (future)
compaction §6 and (future) explicit erasure. That single invariant is what makes
time-travel/revert free.

Write protocol: stream request body to `blobs/.tmp-<random>`, hash while streaming
(SHA-256), rename to final name (no-op if it exists), return the name. Never buffer the
file in RAM (the prod box has 1 GB total).

The extension is captured at upload from the declared Content-Type via a fixed
allowlist table (see security), and lives IN the value string — so serving derives
Content-Type from the name alone, no sidecar metadata, no store lookup on the serve
path.

### 2. Upload edge (bytes in)

`POST /assets` on the per-instance asset tree (beside `session`). **Raw body, not
multipart**: the browser posts the `File` object directly
(`fetch(assetBase + '/assets', {method:'POST', body: file})`) and its `Content-Type`
header carries the MIME type. This deletes the entire multipart-parser problem — there
are no other form fields to carry.

Response: `200 {"name": "<hash>.<ext>"}`. The client puts that string into the draft
prop; the ordinary save path commits it. After the 200, the upload IS just a string —
everything downstream (staging, conflict UI, log entry, snapshot, merge base) is the
existing machinery verbatim.

An uploaded-but-never-saved blob is simply an orphan in the append-only pool —
identical in kind to an orphan left by Reset(), reclaimed by future compaction. No
quarantine, no reference counting, no cleanup timer.

**Auth:** mirror the floor's own posture — if the instance's floor is Dormant (no
access rules, AccessFloor.cs:52), upload is open, exactly as every data write is open
on a dormant instance today; if any rules exist, upload requires a verified cookie
principal (`PrincipalFromCookie` — the asset tree gains cookie reading, which TokenAuth
doesn't care about). Finer per-type gating is impossible at upload time (the blob isn't
attached to a type yet) and unnecessary: an unreferenced blob is inert, and the typed
WRITE that references it is still governed by the ordinary edit floor. Wiring cost
(grill #1): the upload handler must construct an AccessFloor itself (rules + principal)
— the asset tree builds none today; small but new, not free.

**Origin/CSRF (grill #1 — was missing):** the upload edge mirrors `SessionHandler.Cors`
/ `SameHostOrigin` (ContentHandler.cs:193-206) exactly: same-host `Origin` echo only,
plus the `SameSite=Lax` cookie already blocks credentialed cross-site POSTs. In prod
(nginx) the page and the upload URL share one origin, so this is belt-and-braces; in
local two-port dev the app page fetches the asset port cross-origin, so the CORS echo
is load-bearing there — the same dance `/session` already does, copied verbatim.

**Limits:** kernel-side size cap, default 10 MB, checked while streaming (abort + delete
temp past the cap). Deploy: `client_max_body_size 12m;` in the nginx block.

### 3. Serve edge (bytes out)

**URL shape — SETTLED (user, 2026-07-06): assets ride the asset port like `/ws` and
`/js`, instance prefix included.** `/ws`/`/js` are per-instance routes on the asset
port (each instance mounts its own asset tree, InstanceApp.cs:85-91; PathRouter routes
`/apps/<name>/...` to it) — only nginx makes them look bare by re-adding the instance
from the subdomain (`location = /ws` → `8081/apps/$deenv_app/ws`). Assets take the
identical shape:

- kernel/dev: `GET <assetPort>/apps/<name>/assets/<hash>.<ext>` — the per-instance
  tree gains `assets` beside `ws`/`js`/`session`; the instance prefix scopes the URL
  to that instance's own pool.
- prod: `GET https://<app>.deenv.org/assets/<hash>.<ext>` — one nginx
  `location /assets/` block proxying to `8081/apps/$deenv_app/assets/...`, beside the
  existing `= /ws` and `= /js` blocks (deploy/DEPLOY.md:136-170).

Grill #1 raised that the prefix location reserves the `/assets/*` subtree from apps on
every subdomain — the user's ruling: that's the same already-accepted infra class as
`/ws` and `/js` (asset-port namespace, not app namespace), not the rejected kind of
app-URL reservation. The query-param and separate-hostname alternatives the grill
offered are dropped.

`GET /assets/<name>`: validate the name shape strictly
(`^[0-9a-f]{64}\.[a-z0-9]+$` — this alone kills path traversal; verified against
Windows ADS/reserved-name tricks in grill #1), open the file, stream it with:

- `Content-Type` from the extension allowlist,
- `Cache-Control: public, max-age=31536000, immutable` (content-addressed = the URL's
  bytes can never change — free CDN-grade caching, and it makes time-travel browsing
  cheap; the erasure-vs-cache trade this buys is named in ceilings),
- `X-Content-Type-Options: nosniff`.

404 on a missing blob (dangling hash after erasure → the generic UI's `<img>` shows its
placeholder/alt state; that IS the erasure UX, no extra code).

**Access position (the brief's read-floor-vs-unguessable-hash question): the 256-bit
hash is the boundary in v1 — a capability URL.** Reasons, in order:

1. The object read floor is object-scoped (`Can(verb, type, obj)`), and a blob can be
   referenced by several objects across eras; mapping a GET of a bare hash back to "the
   set of objects referencing it, in any era" is a reverse-index scan the store doesn't
   have. Building it would be asset-versioning-adjacent machinery — the drift the brief
   warns about.
2. A capability leaks only by someone who could already read the referencing object
   sharing the URL — at which point they could share the image itself. (Verified in
   grill #1: DbBridge applies the read floor while building the shipped graph — denied
   objects are omitted, denied refs nulled — DbBridge.cs:71-74, 161-164, 219-221; so a
   hash never reaches the SSR HTML of a non-reader.) The residual (URL sticks around
   after access is later revoked / object deleted) is real and named as a ceiling
   below.
3. It keeps `<img src>` plain — no auth ceremony in SSR output, no signed-URL expiry
   machinery.

The serve handler still runs behind the deploy-layer htpasswd gate wherever that gate
applies (only devlog is exempted today), so "unguessable" is the boundary only on
deliberately public instances.

### 4. The `image` scalar

Follows `password` exactly as the text-shaped template:

- `BaseType.Image` + `"image"` in `BaseTypes.ByName`; store tag `"text"` (LeafBase);
  `DefaultBase` arm `new TextValue("")`; `DeserializeLeaf` arm; `ScalarBaseOf` → Text.
- Excluded as a dict KEY, like password — but NOT "the same clause": the existing check
  is a password-specific equality (`== BaseType.Password`,
  InstanceDescriptionLoader.cs:222-229) and must be WIDENED to a two-member set (grill
  #1 correction). Legal as a dict VALUE and everywhere else text is.
- **Migration `fn` writes — grill #1 refuted the draft's "works day one":**
  `ScalarForDeclared` (MigrationRunner.cs:128-131) switches on the DECLARED type name
  and only accepts literal `"int"/"text"/"bool"` — an image prop is `image`-declared
  (like password is password-declared), so a compute-`fn` writing it THROWS today.
  Pure RENAME migrations still carry image data fine (the identity diff, no fn).
  Resolution: add `"image"` to the accepted set in the build slice (one text-shaped
  arm — cheap, and the correctness gap shouldn't ship as an accepted limit); decimal/
  date/datetime stay behind the existing ceiling. A migration fn writing a hash never
  uploaded to this pool yields a dangling reference → 404 placeholder; accepted, same
  class as erasure.
- Generic UI: form branch = current image thumbnail (or empty state) + the upload
  control + a clear button; table cell = small thumbnail (NOT excluded from columns
  like password — a thumbnail column is the point); excluded from labelProp candidacy
  (a hash is not a label).
- The upload control is the one new client primitive: an `<input type=file>` variant
  that POSTs the file and writes the returned name into the draft prop (client-side
  transient, like any other input edit).
- **`sys.assetUrl(name)` — real new plumbing the draft hand-waved (grill #1):** the
  asset authority is JS-only today (`initAssetAuthority` window global consumed by the
  client bootstrap and ws.ts — SsrRenderer.cs:481) and does NOT reach the Code render
  scope, so GenericUi cannot compose an `<img src>` without a new builtin. Both twins
  implement `sys.assetUrl(name)` → the absolute serve URL (C# SSR knows the authority
  server-side; TS reads the existing global). Pure function of session-known state —
  no refetch machinery, not a server-data builtin, so AGENTS.md rule 12 isn't in play.
  One conformance-adjacent check that both twins emit the same URL for the same name.
- Designer picker: add `"image"` to the `scalarTypes` array (instances/1/app.deenv:88)
  — not optional: `DesignerVocabularyTests.Scalar_types_match_the_leaf_base_types`
  pins the array to the BaseType enum, so the build goes red until it's added.
- Conformance: one defensive `sys.new` default case (both twins already default it to
  `""` generically).

`file` (arbitrary downloadable attachment) is explicitly a **follow-up scalar**, not
v1: same pool, same edges, plus Content-Disposition + an original-filename question
that image doesn't have. Designing it now would be scope creep on the dogfood need
(devlog wants pictures).

### 5. Versioning composition (verification that "free" is actually free)

- **Log/commits/merge:** hash strings are ordinary text field writes. Field-level data
  conflicts on an image prop get the existing ConflictBar (base/mine/theirs are three
  hash strings). Named UX ceiling (grill #1): ALL history surfaces — ConflictBar,
  `sys.diffCommits`, `sys.publishPreview` — render image values as raw 64-hex strings
  in v1; thumbnails there are a cosmetic follow-up, consistently deferred.
- **Time-travel clone (`atSeq`) and plain clone: copy the whole `blobs/` directory —
  an EXPLICIT NEW STEP in both clone branches, not free composition (grill #1
  correction).** Today both branches copy/write named files only (File.Copy of
  schema+data at KernelHost.cs:877-880; WriteAllText+SaveRaw at 863-865) — no sibling
  dir rides along. The new step needs a missing-dir guard (`if
  Directory.Exists(srcBlobs)`) because pre-pool instances have no `blobs/`. Clones stay
  fresh sovereign forks (866-868). Whole-pool copy is chosen over referenced-only copy
  (needs a schema-aware scan of the materialized doc — more code for the rare case)
  and over cross-instance sharing (breaks sovereignty; nothing else shares files).
  Ceiling: a clone of an instance with a huge pool duplicates it — accepted at today's
  scale, revisit with compaction.
- **Instance delete:** `DeleteAsync` removes the id-dir recursively
  (KernelHost.cs:1031-1033) — the pool dies with the instance, nothing to add.
  Boot/registry enumeration only int-parses top-level `instances/<n>/` names
  (KernelHost.cs:1104-1107) — `blobs/` one level down is never seen. (Both verified in
  grill #1.)
- **Publish:** touches nothing. Publish transforms the target's data file in place and
  never moves data between instances (KernelHostActions.cs:131-232) — the target's
  hashes already point at the target's own pool.
- **setDesign/fallback reseed:** pool untouched (orphans accepted, see above). The
  wipe-the-log invariant obligation is unaffected because the pool is not part of the
  genesis+log replay equivalence.
- **fsck:** unchanged in v1. An optional later check ("every image-typed value in the
  live doc resolves to a pool file") is nice-to-have, not correctness — a dangling
  hash is a legal state (erasure).
- **Backup/deploy:** any recursive copy of `instances/` (the DEPLOY.md process) carries
  `blobs/` automatically — nothing to wire, but backups now grow with media; interacts
  with the 1 GB box ceiling below.
- **Which instance's pool does an upload hit?** Always the instance serving the page —
  the upload control POSTs to the same per-instance asset tree the page already talks
  to for `/ws`. No cross-instance data-editing surface exists today (the designer edits
  DESIGNS; app data is edited in the app's own UI), so "editing app N's data from
  elsewhere" has no home to upload from. If such a surface ever appears, its upload
  must target the data-owning instance's pool — noted for that future design, not
  solved here.

### 6. Security posture

- **SVG is excluded from the v1 allowlist.** Inline-served SVG executes scripts on the
  instance's origin = stored XSS for anyone allowed to upload. Allowlist:
  `image/png, image/jpeg, image/gif, image/webp` (+ `nosniff` on every response).
  Revisit only with the `file` scalar, where Content-Disposition: attachment changes
  the calculus.
- Name-shape validation on GET (regex above) is the entire path-traversal surface.
- Upload floor: dormant-open mirrors the existing write posture (a public dormant app
  is already fully writable — blobs add disk-fill to an exposure class that already
  exists; both are capped only by the size limit). Public instances with rules (devlog)
  require a logged-in principal to upload.
- The hash is computed server-side from the received bytes — a client cannot claim a
  name it didn't earn, so the pool cannot be poisoned with mismatched content.

## Explicitly out of scope (drift guards, all named)

- Asset **versioning** of any kind (the brief's own warning).
- Blob GC / unreferenced-blob retention — **compaction §6's problem**, sequenced right
  after this per the brief. This design hands compaction a clean definition:
  "referenced = the name appears as an image-typed value anywhere in genesis + log +
  live doc"; sweep = pool files not in that set.
- The non-temporal field flag (§0b) — erasure of blob BYTES works without it; erasure
  of the hash string itself from history would need §0b.
- `file`/attachment scalar, original filenames, Content-Disposition.
- Thumbnails / image processing / EXIF stripping (EXIF: named ceiling — uploaded photos
  may carry GPS metadata and v1 serves bytes verbatim; strip-on-upload is a cheap
  later hardening).
- Signed/expiring URLs, per-object blob read floor (the reverse-index ceiling above).
- Cross-instance dedup or a global pool.
- Upload progress UI beyond the browser's native behavior.

## Known ceilings (stated as required)

- **Works at today's scale, breaks at pool ≫ disk**: append-only pool on a 1 GB Linode;
  nothing reclaims space until compaction lands. Acceptable because nothing reclaims
  LOG space yet either — same maturity, same fix.
- **Capability-URL residual**: a leaked/bookmarked blob URL keeps working after the
  referencing object is deleted or access is tightened, until erasure/compaction
  removes the blob. Deliberate v1 trade, revisit if a real app needs revocable media.
- **Cache defeats erasure recall (grill #1)**: `immutable, max-age=1y` means a browser
  or intermediary that cached an image may keep serving it up to a year after the pool
  file is erased. Origin deletion is the guarantee this design makes; recalling cached
  copies is not achievable from the origin under ANY header once served — shortening
  max-age only shrinks the window at the cost of the caching win. Named, accepted.
- **Raw hashes in history UIs**: diff/preview/conflict surfaces show 64-hex strings for
  image fields in v1 (thumbnails = cosmetic follow-up).
- **Whole-pool clone copy**: O(pool) disk per clone.
- **Solved on paper, untested**: the raw-body upload path is asserted from the /session
  body-read precedent — which exposes `request.Content` as a Stream but itself buffers
  to a string (ContentHandler.cs:150-154); whether GenHTTP delivers a large binary body
  incrementally (enabling the streaming hash + mid-stream cap abort) is UNPROVEN.
  First build task is a spike proving streamed body → temp file under GenHTTP; if
  GenHTTP buffers upstream, the size cap still holds but the no-RAM-buffering claim
  degrades and the cap may need lowering on the 1 GB box.

## Grill record — self-grill #1 (2026-07-06, fresh opus, briefed to refute, code-grounded)

Every cited file was opened. Verdicts, all folded into the body above:

**Held (verified against code):**
- Pool inert to `Reset()`/`Fsck()`/boot enumeration/`DeleteAsync` — all four checked at
  their sites; boot only int-parses top-level instance dirs, delete is recursive.
- Read floor keeps hashes from non-readers in SSR output (DbBridge floors the shipped
  graph — denied objects omitted, denied refs nulled).
- Name regex sufficient on Windows (ADS/reserved names excluded by charset).
- `image` survives client-side as `baseType:"image"` (GenericUi reads the declared
  name `p.Type`, not `ScalarBaseOf` — the draft's own worry was unfounded).
- `DefaultBase` AND `DeserializeLeaf` both throw on unmapped BaseType — two mandatory
  arms, not one. Dormant-open matches the real write-floor posture; devlog is
  non-dormant.
- `scalarTypes` picker edit is guard-forced (DesignerVocabularyTests).

**Refuted → fixed in body:**
1. Migration compute-`fn` on image would THROW (`ScalarForDeclared` hardcodes declared
   names; image is image-declared) → build slice adds the `"image"` arm; rename-only
   already worked.
2. `location /assets/` prefix reserves the `/assets/*` subtree from every app in prod
   → raised to the user; RULED 2026-07-06: asset-port paths are the same accepted
   infra class as `/ws`/`/js`, keep `GET /assets/<name>` with a prefix location.
3. Clone does NOT copy sibling dirs — pool copy is an explicit new step in both
   branches, with a missing-dir guard for pre-pool instances.
4. Upload edge was silent on CSRF/Origin → mirrors SessionHandler.Cors + SameSite=Lax,
   now stated; load-bearing in two-port dev.
5. `<img>` base URL needs new plumbing (asset authority is JS-only today) →
   `sys.assetUrl(name)` builtin on both twins, specified.
6. Dict-key exclusion is a password-specific equality, must be widened, not "reused."
7. Immutable caching partially defeats erasure → named as a ceiling with the honest
   scope (origin deletion guaranteed, cache recall not).

**Left open by the grill:** GenHTTP streaming behavior (the named spike);
image-migration-write unblocking scope (resolved here as: add image now, decimal/date
stay deferred — default judgment call, cheap-gap rule). The URL-shape question went to
the user and was settled same day (§3).

## Status summary

| Topic | Status |
|---|---|
| Content-addressed per-instance append-only pool | Settled (design; grill-verified inert to store machinery) |
| Upload edge: raw-body POST, dormant-open/cookie auth, Cors mirror, 10 MB cap | Settled (design) — GenHTTP streaming = named spike |
| Serve edge: capability-URL boundary, immutable caching, SVG-less allowlist | Settled (design; default judgment calls) |
| Prod URL shape | Settled (USER 2026-07-06): `/assets/<name>` on the asset port, prefix nginx location — same infra class as /ws, /js |
| `image` scalar (password-template, text-shaped) | Settled (design) |
| Clone = whole-pool copy (explicit step + guard) | Settled (design) |
| Publish/revert/setDesign: no blob handling needed | Settled (grill-verified) |
| Blob GC / retention | Deferred to compaction §6 (per the brief; handed a clean "referenced" definition) |
| `file` scalar, thumbnails, EXIF strip, per-object blob floor | Deferred, named |
