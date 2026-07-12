using System.Text.Json;
using DeEnv.Code;
using DeEnv.Code.Parsing;
using DeEnv.Designer;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Kernel;

// The kernel's implementation of the host-action seam for ONE hosted instance. Its actions either
// project a DESIGN (passed by its id — the designer passes one `Design` out of its `db.designs`
// set) into an app document, or address an instance by its id:
//   • publish(design, targetId, dryRun?) — VERSIONED when the design has a committed head (M13 slice 4):
//     diffs the target's STAMPED commit against the design's HEAD commit by IDENTITY (renames carry data
//     — see DesignDiff), materializes the changeset as ONE boundary-marked log entry (history preserved),
//     stamps the target to the new head, and returns a structured PublishReport. UNVERSIONED fallback when
//     the design has no commits yet, or the target was never stamped (one-time name-match apply, then
//     stamped) — the pre-slice-4 by-name apply (SchemaBridge.WriteDocument).
//   • create(design, name) — project the design into a NEW instance with the given display NAME, via
//     the kernel create delegate (the C# create mechanism: write the doc, load, append the registry,
//     refresh the live set). NO ports — addressing is by path (`/apps/<name>` derives from the name).
//   • cloneInstance(sourceId, atSeq?) — copy an existing instance's app doc AND data into a NEW instance,
//     via the kernel clone delegate. NO ports — the clone gets a unique mount name derived from the
//     source. `atSeq` (M13 slice 7, OPTIONAL — omitted is byte-identical to the pre-slice-7 clone)
//     materializes the source's data as it stood at that log seq, under the SCHEMA in force then (the
//     latest publish boundary at-or-before atSeq, else the source's current app.deenv) — time travel as a
//     fresh fork with its OWN history (design doc §0/§6): "the app as of Tuesday," one op.
//   • delete(targetId) — remove an existing instance, via the kernel delete delegate.
//   • setDesign(design, targetId) — record (on the target's registry entry) that it now runs the
//     passed design, AND deploy it onto the target — the IDE's "Apply" (remember-then-publish). It is
//     publish + the registry write: the registry write half (via the kernel recordDesign delegate)
//     makes the reference explicit so the dropdown can pre-select it later.
//   • commitDesign(design, message, migration) — snapshot the design at its CURRENT log position into an
//     immutable Commit row chained onto its main Branch (M13 slice 3 — DECISIONS "App versioning").
//     Acts purely on the CALLER's own store (no cross-instance kernel delegate needed, unlike
//     create/delete/clone). No write grants exist on Commit/Branch rows (the designer's own `access`
//     section denies create/edit/delete on both), so this is the ONLY path that can ever write one.
//   • createBranch(design, name) — clone a working copy's whole subgraph (Design + its MetaTypes +
//     MetaProps) into a NEW Branch { name, head = source branch's head, workingCopy = the clone } (M13
//     slice 5). The clone is linked ONLY into db.branches, never db.designs — so the app list stays main
//     working copies while the clone's history stays GC-reachable via the branch. See CreateBranch's own
//     doc for the origin-flattening rule that keeps N-deep branches anchored to the ORIGINAL rows.
//   • mergeBranch(source, target, resolutions?) — a lineage-keyed three-way structural merge (DesignMerger)
//     of `source`'s branch into `target`'s, both addressed by their CURRENT working-copy id. A clean merge
//     applies to the target working copy and creates a two-parent merge Commit; any conflict makes NO
//     writes and returns a MergeReport instead — re-run with `resolutions: [{id, take}]` to apply
//     per-conflict picks. See MergeBranch's own doc for the LCA/drift/apply rules.
// The kernel constructs one of these per instance and threads it into WsHandler, so an action acts
// with the CALLING instance's own data as the design source (its data is the IDE holding the set of
// Designs it edits). A publish/create RESOLVES the passed design's id → its Design subtree in the
// caller's store (through IInstanceStore — never a raw file read) and projects it via SchemaBridge;
// an id that is not a member of the caller's `designs` is rejected, never a write to the wrong app.
// SchemaBridge surfaces its validation failure as a reject (it throws, WsHandler catches). The
// delete / clone delegates are type-distinct (Func<int,Task> vs Func<int,int,int,Task>) so positional
// mix-ups are compile errors; same-typed delegates (deleteInstance / restartInstance) use named args.
public sealed class KernelHostActions(
    // The CALLING instance's OWN live store, resolved lazily at call time (M13 mirror-clobber fix — one
    // store instance per data file within a kernel process). Every action here acts on the caller's own
    // data (the designer's `db.designs`/`db.commits`/`db.branches`); before the fix each opened its OWN
    // fresh `new JsonFileInstanceStore` per call over the same file — safe ONLY because nothing else wrote
    // between open and use, which the design host constantly violated (a fresh-store commit followed by the
    // boot-cached store's mirror write clobbered the snapshot AND collided WAL seqs). Now all five actions
    // share the ONE live store the kernel already hosts this instance on, so its `_sync`/`_db`/version/WAL
    // are the single authority again. Resolved via a Func (not a field) because the HostedInstance — and
    // thus its Store — is built AFTER this seam is constructed (see KernelHost.HostActionsFor).
    Func<IInstanceStore> resolveStore,
    // The CALLING instance's own id (the design host — HostActionsFor wires real actions only there).
    // Exists for ONE guard: the design host must never be its own publish TARGET. The single-writer
    // model publish rests on (offline-rewrite the target's files, then restart the TARGET) assumes the
    // caller and target are different instances; publishing onto the caller would rewrite the designer's
    // own schema out from under its live store — and destroy the IDE. Structurally nothing else prevents
    // it (resolveTarget serves every registered instance, including this one), so the guard is load-bearing
    // against operator error, not decoration.
    int callerId,
    Func<int, InstanceSpec?> resolveTarget,
    Func<string, string, int?, Task> createInstance,
    Func<int, Task> deleteInstance,
    Func<int, int?, Task> cloneInstance,
    Func<int, int, Task> recordDesign,
    Func<int, Task> restartInstance,
    Func<int, string, Task> renameInstance,
    // M13 slice 4 — the instance's versioning stamp (kernel.json's RegistryEntry.PublishedCommitId): read
    // the CURRENT stamp for a target id (null = unstamped), and persist a NEW one after a successful
    // versioned publish. Mirrors recordDesign's shape (a plain per-target registry read/write pair).
    Func<int, int?> readPublishedCommitId,
    Func<int, int, Task> stampPublishedCommit,
    // M13 Track-B B3 addendum — the preview→apply consistency guard: the TARGET's own live store (by id),
    // resolved lazily like `resolveStore` (mirror-clobber fix — never open a second store over a live
    // instance's file). Used ONLY to read `.CurrentVersion` when a guarded publish checks that the target's
    // data hasn't moved since the operator's preview. Optional (defaults null) so a caller built before this
    // addendum keeps compiling; a guarded publish call against a null-supplied resolver simply cannot enforce
    // the target-version half of the guard (the head-commit half still runs) — never reached in practice
    // since HostActionsFor always supplies a real one.
    Func<int, IInstanceStore?>? resolveTargetStore = null) : IHostActions
{
    public object? Run(string action, JsonElement args) => action switch
    {
        "publish"      => Publish(args),
        "create"       => Create(args),
        "cloneInstance" => Clone(args),
        "delete"       => Delete(args),
        "setDesign"    => SetDesign(args),
        "rename"       => Rename(args),
        "commitDesign" => CommitDesign(args),
        "revertCommit" => RevertCommit(args),
        "importRender" => ImportRender(args),
        "createBranch" => CreateBranch(args),
        "mergeBranch"  => MergeBranch(args),
        _ => throw new InvalidOperationException($"Unknown host action '{action}'."),
    };

    // publish(design, targetId, dryRun?) OR publish(design, targetId, expectedHeadCommit, expectedTargetVersion):
    // project the design's committed HEAD (never its working copy — design doc §4) onto an EXISTING target.
    // arg 0 the design object's id (resolved against the caller's store — see ResolveDesign), arg 1 the
    // target id.
    //
    // Two DIFFERENT optional trailing shapes share positions 2/3, disambiguated by ARG COUNT (never
    // combined — no test or caller ever needs both at once):
    //   • 3 args, a bare bool at position 2 — the pre-existing `dryRun` (a dry run computes the SAME report
    //     and changes NOTHING).
    //   • 4 args, two ints at positions 2/3 — the preview→apply CONSISTENCY GUARD token (M13 Track-B B3
    //     addendum): `expectedHeadCommit`/`expectedTargetVersion`, exactly what `sys.publishPreview` handed
    //     the operator as `report.targetCommit`/`report.targetVersion`. Splitting preview from apply opens a
    //     TOCTOU window — the operator approves a SPECIFIC plan (the preview), but an unguarded apply
    //     recomputes fresh and could execute a DIFFERENT plan if the design gained a new commit or the
    //     target's data moved in between. Passing the token back closes it: the design editor's Apply button
    //     always supplies it (see BOTH-OR-NEITHER below); a 2-arg caller (every existing test, any future
    //     unguarded caller) stays exactly as unguarded as today.
    // BOTH-OR-NEITHER: there is no 3-arg "one guard int" shape — an arg count of exactly 4 is what selects
    // the guard path; 2 or 3 args never attempt to read it.
    private PublishReport Publish(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var design = ResolveDesign(designId);
        var targetId = ArgInt(args, 1);
        var argCount = args.ValueKind == JsonValueKind.Array ? args.GetArrayLength() : 0;
        var dryRun = argCount == 4 ? false : ArgBoolOptional(args, 2, defaultValue: false);
        int? expectedHeadCommit = argCount == 4 ? ArgInt(args, 2) : null;
        int? expectedTargetVersion = argCount == 4 ? ArgInt(args, 3) : null;
        if (targetId == callerId)
            throw new InvalidOperationException(
                "The design host cannot be its own publish target — publish deploys a design onto an app instance.");
        var target = resolveTarget(targetId)
            ?? throw new InvalidOperationException(
                $"No instance with id {targetId} to publish to.");

        var store = resolveStore();
        var stampedCommitId = readPublishedCommitId(targetId);

        // The consistency guard — checked BEFORE ANY MATERIALIZATION, including the versioned leg's
        // destructive boundary (which PublishReportComputer.Compute(..., dryRun:false) WRITES to
        // target.DataPath as a side effect of computing the plan, not as a separate later step — see its own
        // doc). Reviewer fix: a naive "compute-then-check" ordering let a STALE guarded apply migrate the
        // target's data file BEFORE the guard threw, leaving DataPath migrated but SchemaPath/the stamp/the
        // live store all still on the OLD schema — exactly the mirror-clobber/schema-mismatch class slice 708
        // killed, just reached via a different door. So this reads the SAME two identity signals
        // Compute would land on (the design's current head commit id, the target's live store version)
        // WITHOUT materializing anything, checks them, and ONLY THEN calls Compute to do the real (writing)
        // work. Enforced ONLY on a REAL apply with the token supplied (dryRun is unaffected — a 3-arg dry run
        // never reaches here with a non-null expected*, and a 2-arg unguarded call skips this entirely).
        //
        // DEFERRED (design doc §4, NOT implemented here — inherited from the pre-slice concurrency model): the
        // "take the store lock → briefly reject incoming commits with 'updating' → … → bump the schema epoch"
        // queueing step. The offline boundary rewrite acts on the target's files directly while its instance
        // is unmounted (single-operator: no concurrent publisher); an in-flight draft that DOES race between
        // THIS check and the write below is caught after the fact by the existing baseVersion guard.
        // Publish-queueing lands with the real-time milestone; it is inherited-as-deferred here, not built.
        // This guard is the cheap correctness floor that IS built: a narrower, synchronous check that the plan
        // about to be applied is still the plan that was previewed.
        if (!dryRun && (expectedHeadCommit is not null || expectedTargetVersion is not null))
        {
            // The design's CURRENT head commit id — 0 when the design has no commits yet (the NoHead leg),
            // mirroring PublishPlan.HeadCommitId exactly (same FindHeadCommit lookup Compute itself makes),
            // so an expected-head guard on a headless design can never match and always rejects.
            var currentHeadCommitId = FindHeadCommit(store, designId)?.Id ?? 0;
            if (expectedHeadCommit is { } expHead && currentHeadCommitId != expHead)
                throw new InvalidOperationException(
                    "The design or target changed since the preview — re-preview before applying.");
            if (expectedTargetVersion is { } expVersion
                && resolveTargetStore?.Invoke(targetId) is { } targetStore
                && targetStore.CurrentVersion != expVersion)
                throw new InvalidOperationException(
                    "The design or target changed since the preview — re-preview before applying.");
        }

        // ONE report-computing core, shared with sys.publishPreview (M13 Track-B B3 — PublishReportComputer):
        // it decides the leg and, on a REAL run (dryRun:false), MATERIALIZES the versioned leg's destructive
        // boundary onto the target's data file. So a preview (dryRun:true) reports EXACTLY what this apply
        // does, never a second copy of the conversion rules. The guard above already ran, so by the time this
        // is reached (on a real, guarded apply) the plan is KNOWN fresh — nothing rejects after this point.
        var plan = PublishReportComputer.Compute(store, designId, design, target.DataPath, stampedCommitId, dryRun);

        if (dryRun) return plan.Report; // prove-it: no file write, no log, no stamp, no restart below

        // ── apply-only side effects (a preview does NONE of these) ──
        switch (plan.Leg)
        {
            case PublishLeg.NoHead:
                // No commit yet — project the CURRENT working copy and apply by name (pre-slice-4 behavior).
                SchemaBridge.WriteDocument(plan.WorkingDoc, target.SchemaPath, target.DataPath);
                _ = restartInstance(targetId);
                break;

            case PublishLeg.Fallback:
                // One-time by-name apply of the head text (carrying whatever a by-name apply can), then stamp
                // so the NEXT publish is identity-diffed and rename-safe.
                SchemaBridge.WriteDocument(plan.HeadText, target.SchemaPath, target.DataPath);
                stampPublishedCommit(targetId, plan.HeadCommitId).GetAwaiter().GetResult();
                _ = restartInstance(targetId);
                break;

            case PublishLeg.Versioned:
                // The destructive boundary was already materialized inside Compute (dryRun:false). Write the
                // target's app document FROM the commit's cached text (the publish artifact) — always, even
                // when the diff itself was empty (the design may have gained sections/commits with no
                // structural change; the target must still run the head's exact document).
                File.WriteAllText(target.SchemaPath, plan.HeadText);
                stampPublishedCommit(targetId, plan.HeadCommitId).GetAwaiter().GetResult();

                // ONE-STORE-PER-FILE invariant for the TARGET (mirror-clobber fix): ApplyPublishBoundary just
                // rewrote target.DataPath OFFLINE — directly on the file, NOT through the target's live hosted
                // store, whose in-memory `_db` is now stale. Safe because (a) publish is one SYNCHRONOUS
                // host-action step (single-operator — no concurrent writer to the target between the offline
                // rewrite and the restart), and (b) restartInstance re-OPENS the target's store fresh from the
                // rewritten file and hot-swaps it in, discarding the now-stale one. So the offline writer and a
                // live store never both write the file.
                _ = restartInstance(targetId);
                break;
        }

        return plan.Report;
    }

    // `internal` (was private) so PublishReportComputer reuses the SAME report-shaping — one
    // implementation of the destructive-cell mapping (M13 Track-B B3).
    internal static PublishReport BuildReport(
        DesignDiff diff, BoundaryApplyResult boundaryResult, bool applied, bool dryRun, int? baseCommit,
        int targetCommit, bool uncommittedDrift, bool fallbackNameMatched,
        IReadOnlyList<MigrationRunReport>? migrations = null, bool migrationsSkipped = false) => new()
    {
        Applied = applied,
        DryRun = dryRun,
        BaseCommit = baseCommit,
        TargetCommit = targetCommit,
        UncommittedDrift = uncommittedDrift,
        Renames = [
            .. diff.TypeRenames.Select(r => new RenameReportItem(r.FromName, r.ToName)),
            .. diff.PropRenames.Select(r => new RenameReportItem($"{r.TypeName}.{r.FromProp}", $"{r.TypeName}.{r.ToProp}")),
        ],
        Adds = [.. diff.TypeAdds.Select(a => a.TypeName), .. diff.Adds.Select(a => $"{a.TypeName}.{a.PropName}")],
        Removes = [
            .. diff.Removes.Select(r => new RemoveReportItem($"{r.TypeName}.{r.PropName}")),
            .. diff.TypeRemoves.Select(r => new RemoveReportItem(r.TypeName)),
        ],
        Conversions = [.. diff.Conversions.Select(c =>
        {
            var path = $"{c.TypeName}.{c.PropName}";
            var unconvertible = boundaryResult.UnconvertibleCells.Where(cell => CellMatches(cell, c.TypeName, c.PropName)).ToList();
            return new ConversionReportItem(path, c.FromType, c.ToType, unconvertible);
        })],
        Cardinality = [.. diff.CardinalityChanges.Select(c =>
        {
            // An un-carriable reshape is BOTH unsupported (no data-carry) AND dropped (old value dropped to
            // the new shape's default so the instance still loads) — they move together (see
            // JsonFileInstanceStore.ApplyPublishBoundary's cardinality block), surfaced separately so a
            // report reader never infers the destruction.
            var unsupported = boundaryResult.UnsupportedReshapes.Any(cell => CellMatches(cell, c.TypeName, c.PropName));
            return new CardinalityReportItem(
                $"{c.TypeName}.{c.PropName}", c.FromCardinality.ToString(), c.ToCardinality.ToString(),
                Unsupported: unsupported, Dropped: unsupported);
        })],
        FallbackNameMatched = fallbackNameMatched,
        Migrations = migrations ?? [],
        MigrationsSkipped = migrationsSkipped,
        Restorations = boundaryResult.RestoredCells ?? [],
    };

    // Whether a BoundaryApplyResult cell string ("TypeName/objectId.propName" — see
    // JsonFileInstanceStore.ApplyPublishBoundary's unconvertibleCells/unsupportedReshapes) belongs to
    // exactly this (typeName, propName) pair. An EXACT split (never EndsWith/StartsWith substring
    // matching), so a prop named e.g. "qty" is never conflated with one named "bigQty" on the same type.
    private static bool CellMatches(string cell, string typeName, string propName)
    {
        var slash = cell.IndexOf('/');
        if (slash < 0 || cell[..slash] != typeName) return false;
        var dot = cell.IndexOf('.', slash + 1);
        return dot >= 0 && cell[(dot + 1)..] == propName;
    }

    // setDesign(design, targetId): the IDE's "Apply" — record (in the registry) that the target now runs
    // the passed design AND deploy it. arg 0 is the design object's id (a member of the caller's
    // `db.designs`, resolved against the caller's store), arg 1 the target instance id. Project FIRST so
    // an invalid design throws before any registry write or document overwrite (records nothing, writes
    // nothing); then record the reference (the kernel rewrites kernel.json's designId + refreshes the live
    // view); then write the projected doc, PRESERVING the target's data — exactly Publish (non-destructive
    // apply), with the registry write in front. An unknown target id is rejected before any work.
    private object? SetDesign(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var targetId = ArgInt(args, 1);
        var design = ResolveDesign(designId);
        var target = resolveTarget(targetId)
            ?? throw new InvalidOperationException(
                $"No instance with id {targetId} to set a design on.");

        var appDoc = SchemaBridge.ProjectDesignDocument(design); // throws on an invalid design
        recordDesign(targetId, designId).GetAwaiter().GetResult();
        SchemaBridge.WriteDocument(appDoc, target.SchemaPath, target.DataPath);
        _ = restartInstance(targetId);
        return null;
    }

    // create(design, name): project the PASSED design into a NEW instance with the given display NAME —
    // the sibling of publish (spawn rather than replace). arg 0 is the design object id, arg 1 the
    // display name (which ALSO becomes the mount path `/apps/<name>` — addressing is by path now, so
    // there are NO port args). ProjectDesignDocument validates the design first (throws, spawning
    // nothing, on an invalid one); then the kernel create delegate writes + loads it, recording the
    // design's id on the new instance's registry entry (so its dropdown pre-selects that design). The
    // delegate is async; we block on it (the WS dispatch is synchronous, no synchronization context to
    // deadlock on — a single-operator devops action).
    private object? Create(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var design = ResolveDesign(designId);
        var name = ArgText(args, 1);

        var appDoc = SchemaBridge.ProjectDesignDocument(design); // throws on an invalid design
        createInstance(appDoc, name, designId).GetAwaiter().GetResult();
        return null;
    }

    // cloneInstance(sourceId, atSeq?): copy an existing instance (app doc + data) into a NEW one — the
    // data-carrying sibling of create. arg 0 is the SOURCE instance id (a bare int, not a schema
    // object); there are NO port args (the clone gets a unique mount name derived from the source —
    // addressing is by path). arg 1 is the OPTIONAL time-travel seq (M13 slice 7) — omitted (the default
    // path) is byte-identical to the pre-slice-7 clone; when given, the clone gets what the source's data
    // looked like at that log seq, under the schema in force then, instead of the source's current head.
    // The kernel clone delegate resolves the id, materializes if needed, and copies the files; an unknown
    // id or an invalid atSeq throws (surfaced as the reject, nothing created). Blocked on for the same
    // reason as create.
    private object? Clone(JsonElement args)
    {
        var sourceId = ArgInt(args, 0);
        var atSeq = ArgIntOptional(args, 1);
        cloneInstance(sourceId, atSeq).GetAwaiter().GetResult();
        return null;
    }

    // delete(targetId): remove an existing instance. arg 0 is the instance id (a bare int). The kernel
    // delete delegate resolves the id and stops + forgets it (removing its id-dir + data); an unknown
    // id throws, surfaced as the reject. Blocked on like create/clone.
    private object? Delete(JsonElement args)
    {
        var id = ArgInt(args, 0);
        deleteInstance(id).GetAwaiter().GetResult();
        return null;
    }

    // Resolve the passed design's id → its `Design` subtree in the CALLER's store (the IDE holds a
    // `db.designs` set of Designs). Reads through IInstanceStore (the model's terms — never a raw file
    // call): a design is `db.designs[id]`, so an id that is not a member of `designs` resolves to no
    // node and is rejected here, before any projection or write. Reads the caller's ONE live store (the
    // single writer to that file — see the ctor's `resolveStore` doc); this action only READS it.
    private ObjectValue ResolveDesign(int designId)
    {
        var store = resolveStore();
        var designPath = NodePath.Root.Field("designs").Key(designId.ToString());
        return store.ReadNode(designPath) as ObjectValue
            ?? throw new InvalidOperationException(
                $"No design with id {designId} in the designer's `designs` set.");
    }

    // commitDesign(design, message, migration): snapshot the SHARED working copy (Figma model — no per-user
    // staging) at a marked log position and record it as an immutable Commit row, chaining onto the
    // design's main Branch. arg 0 the design's id (a member of the caller's `db.designs`), arg 1 the
    // commit message text.
    //
    // Consistency discipline (docs/plans/app-versioning-design.md §0 "commits are Figma-model
    // snapshots"): the store interface has no multi-read transaction, so text/logSeq is captured via an
    // OPTIMISTIC retry loop — read the head version, read + snapshot the design, re-read the head
    // version; if it moved, someone edited the design mid-read (a genuinely concurrent designer session,
    // near-zero on a solo operator) — retry. A bounded few attempts, then fail loudly (never silently
    // commit a torn text/logSeq pair). An interleaved edit landing AFTER the final re-check is fine — it
    // lands after this commit's mark, and the NEXT commit picks it up.
    //
    // Atomicity: the WHOLE commit is ONE CommitBatch (review fix 3) — the Commit row create + its
    // `design`/`parent` ref-links + its link into `db.commits` + EVERY idMap dict entry + the branch-head
    // advance, all-or-none under the store's single lock and ONE log entry (a design commit IS a single
    // atomic changeset in the data log). The idMap rides the batch via DictWriteMutation (server-side-only
    // vocabulary — see its doc); there is no longer a follow-up write phase and thus no crash window where
    // a commit is observable in db.commits with a partial idMap or no head. The batch's own all-or-none
    // guarantee (throws untouched-on-failure) is the linearization point — atomicity is structural now.
    private object? CommitDesign(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var message = ArgText(args, 1);
        var migration = ArgText(args, 2);
        var revertMigration = ArgTextOptional(args, 3, "");

        var store = resolveStore();

        // Advance the OWNING branch's head (M13 slice 5) — the branch whose workingCopy IS this design,
        // whether that is "main" or a branch clone. Was FindMainBranch (main only) pre-slice-5; widened so a
        // branch commit advances its OWN head, not main's (a commit onto a design no branch owns is rejected).
        var branch = FindOrCreateMainBranchByWorkingCopy(store, designId)
            ?? throw new InvalidOperationException($"Design {designId} has no owning branch to commit onto.");
        NodeValue? headField = branch.Fields.Fields.GetValueOrDefault("head");
        int? parentHeadId = headField is ReferenceValue { TargetId: { } h } ? h : null;

        CaptureAndCommit(store, designId, message, migration, parentHeadId, mergeParentHeadId: null, branch.Id, revertMigration);
        return null;
    }

    private object? RevertCommit(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var targetCommitId = ArgInt(args, 1);
        var store = resolveStore();
        var branch = FindOrCreateMainBranchByWorkingCopy(store, designId)
            ?? throw new InvalidOperationException($"Design {designId} has no owning branch to revert.");
        var headId = branch.Fields.Fields.GetValueOrDefault("head") is ReferenceValue { TargetId: { } h } ? h
            : throw new InvalidOperationException("Cannot revert: the design has no head commit.");
        var head = FindCommit(store, headId)
            ?? throw new InvalidOperationException($"Commit {headId} referenced by the branch head no longer exists.");
        var parentId = head.Fields.GetValueOrDefault("parent") is ReferenceValue { TargetId: { } p } ? p
            : throw new InvalidOperationException("Cannot revert the root commit.");
        if (targetCommitId != parentId)
            throw new InvalidOperationException("Only the last commit can be reverted in this version.");
        var target = FindCommit(store, targetCommitId)
            ?? throw new InvalidOperationException($"Commit {targetCommitId} no longer exists.");

        RestoreWorkingCopyToCommit(store, designId, target);
        CaptureAndCommit(
            store, designId, $"Revert to '{TextOf(target, "message")}'",
            TextOf(target, "revertMigration"), parentHeadId: headId, mergeParentHeadId: null, branch.Id);
        return null;
    }

    // importRender(design): convert the design's text `ui` render into structured MetaNode rows (M12 X2a).
    // arg 0 is the design's id (a member of the CALLER's db.designs — resolved the same way ResolveDesign
    // does, so a non-design id is rejected before any write). Acts purely on the CALLER's own store (no
    // cross-instance delegate — the design is one of the designer's own rows), so it shares the ONE live
    // store like commitDesign/revertCommit. SchemaBridge.ImportRender does the whole conversion as ONE
    // atomic CommitBatch (mint the MetaNode/MetaAttr rows, link them, clear `ui`), refusing (throwing) an
    // un-importable render (foreach/if, helpers, an already-structured tree) with nothing written; the
    // throw surfaces as the WsHandler `{ error }` reply. Returns null → the plain { ok:true } reply.
    private object? ImportRender(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var store = resolveStore();
        SchemaBridge.ImportRender(store, designId);
        return null;
    }

    // The shared capture-and-commit core behind BOTH sys.commitDesign and the merge commit
    // (KernelHostActions.MergeBranch) — factored out (review fix, M13 slice 5) so a merge commit rides the
    // SAME optimistic capture bracket an ordinary commit always has, rather than a second, unguarded
    // read-snapshot-stamp sequence. Docs/plans/app-versioning-design.md §0 "commits are Figma-model
    // snapshots": the store interface has no multi-read transaction, so text/logSeq is captured via an
    // OPTIMISTIC retry loop — read the head version (s1), read + snapshot the design, re-read the head
    // version (s2); if it moved, someone edited the design mid-read (a genuinely concurrent designer
    // session, near-zero on a solo operator, and no rarer for a merge than an ordinary commit) — retry. A
    // bounded few attempts, then fail loudly (never silently commit a torn text/logSeq pair). This is
    // CONSISTENCY BY CONSTRUCTION, not a scenario to prove: sharing one helper means a merge commit CANNOT
    // observe a design mid-edit any more than an ordinary commit can — the race class this bracket
    // eliminates needs no dedicated regression test, only that both call sites route through it (which the
    // existing DesignCommit.feature + DesignMerge.feature suites already exercise on the happy path).
    //
    // Atomicity: the WHOLE commit is ONE CommitBatch (slice-3 review fix 3, carried forward here) — the
    // Commit row create + its `design`/`parent`/optional `mergeParent` ref-links + its link into
    // `db.commits` + EVERY idMap dict entry + the branch-head advance, all-or-none under the store's single
    // lock and ONE log entry. Returns the minted commit's own intrinsic id (the merge caller reports it on
    // the MergeReport; an ordinary commit has no caller that needs it).
    private static int CaptureAndCommit(
        IInstanceStore store, int designId, string message, string migration, int? parentHeadId, int? mergeParentHeadId, int branchId,
        string revertMigration = "")
    {
        if (!string.IsNullOrWhiteSpace(migration) && parentHeadId is null)
            throw new InvalidOperationException("A root commit cannot carry a migration.");

        const int maxAttempts = 5;
        DesignSnapshot snap = null!;
        int logSeq = 0;
        for (var attempt = 1; ; attempt++)
        {
            var s1 = store.CurrentVersion;
            // Resolve the WORKING COPY by id directly (M13 slice 5): a Design row reachable either through
            // db.designs (a main working copy) OR only through a Branch.workingCopy (a branch clone, NOT in
            // db.designs) — so this works unchanged on a branch (ReadById finds any Design row; the caller's
            // own FindBranchByWorkingCopy already enforced it IS a working copy of some branch).
            if (store.ReadById(designId) is not (var tn, var design) || tn != "Design")
                throw new InvalidOperationException($"No design with id {designId} to commit.");
            snap = SchemaBridge.Snapshot(design); // throws SchemaValidationException on an invalid design
            ValidateMigration(migration, snap.Text);
            if (!string.IsNullOrWhiteSpace(revertMigration))
            {
                if (parentHeadId is null) throw new InvalidOperationException("A root commit cannot carry a revert migration.");
                var parent = FindCommit(store, parentHeadId.Value)
                    ?? throw new InvalidOperationException($"Commit {parentHeadId.Value} no longer exists.");
                ValidateMigration(revertMigration, TextOf(parent, "text"));
            }
            var s2 = store.CurrentVersion;
            if (s1 == s2) { logSeq = s1; break; }
            if (attempt >= maxAttempts)
                throw new InvalidOperationException(
                    $"commit: the design kept changing while snapshotting it ({maxAttempts} attempts) — try again.");
        }

        var commitsSetId = (store.ReadNode(NodePath.Root.Field("commits")) as SetValue)?.Id
            ?? throw new InvalidOperationException("The designer's `db.commits` set is missing.");

        const int commitTemp = -1;
        var commitFields = new Dictionary<string, NodeValue>
        {
            ["message"] = new TextValue(message),
            ["at"]      = new DateTimeValue(DateTimeOffset.UtcNow),
            ["logSeq"]  = new IntValue(logSeq),
            ["text"]    = new TextValue(snap.Text),
            ["migration"] = new TextValue(migration),
        };
        if (store.DeclaresField("Commit", "revertMigration"))
            commitFields["revertMigration"] = new TextValue(revertMigration);
        var creates = new List<CommitCreate>
        {
            new CommitCreate(commitTemp, "Commit", new ObjectValue(commitFields)),
        };
        // The whole changeset, referencing the not-yet-minted commit by its tempId (the store resolves it
        // after minting): design + parent (+ optional mergeParent) refs, the db.commits link, one dict
        // write per idMap entry, and the branch-head advance — issued as ONE batch so the commit is never
        // observable half-written.
        var mutations = new List<CommitMutation>
        {
            new RefLinkMutation(commitTemp, "design", designId, "Design"),
        };
        if (store.SingleReferenceTargetType("Commit", "by") == "User"
            && StoreWriteContext.Get().Who is { } authorId
            && store.ReadById(authorId) is ("User", _))
            mutations.Add(new RefLinkMutation(commitTemp, "by", authorId, "User"));
        if (parentHeadId.HasValue)
            mutations.Add(new RefLinkMutation(commitTemp, "parent", parentHeadId, "Commit"));
        if (mergeParentHeadId.HasValue)
            mutations.Add(new RefLinkMutation(commitTemp, "mergeParent", mergeParentHeadId, "Commit"));
        mutations.Add(new SetLinkMutation(commitsSetId, commitTemp));
        foreach (var (path, id) in snap.IdMap)
            mutations.Add(new DictWriteMutation(commitTemp, "idMap", new TextValue(path), new IntValue(id)));
        mutations.Add(new RefLinkMutation(branchId, "head", commitTemp, "Commit"));

        var result = store.CommitBatch(creates, mutations);
        return result.Creates.First(c => c.TempId == commitTemp).RealId;
    }

    private static void RestoreWorkingCopyToCommit(IInstanceStore store, int designId, ObjectValue targetCommit)
    {
        var targetText = TextOf(targetCommit, "text");
        var targetDesc = InstanceDescriptionLoader.Load(targetText);
        var idMap = IdMapOf(targetCommit);
        var design = ReadDesign(store, designId);
        var typesSet = design.Fields.GetValueOrDefault("types") as SetValue
            ?? throw new InvalidOperationException($"Design {designId} has no `types` set.");
        var typeIds = idMap.Where(kv => !kv.Key.Contains('.')).ToDictionary(kv => kv.Key, kv => kv.Value);
        var propIds = idMap.Where(kv => kv.Key.Contains('.')).ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var currentTypeId in typesSet.Members.Keys.ToList())
            if (!typeIds.ContainsValue(currentTypeId))
                store.RemoveFromSet(typesSet.Id, currentTypeId);

        var typeCreates = new List<CommitCreate>();
        var typeMutations = new List<CommitMutation>();
        foreach (var (index, type) in targetDesc.AllTypes().Select((t, i) => (i, t)))
        {
            if (BaseTypes.IsName(type.Name)) continue;
            var id = typeIds[type.Name];
            var fields = new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["name"] = new TextValue(type.Name),
                ["baseType"] = new TextValue(BaseTypeWordOf(type.BaseType)),
                ["values"] = new TextValue(string.Join(",", type.Values ?? [])),
                ["order"] = new IntValue(index * 10),
            });
            if (store.ReadById(id) is ("MetaType", _))
            {
                typeMutations.Add(new FieldWriteMutation(id, "name", new TextValue(type.Name)));
                typeMutations.Add(new FieldWriteMutation(id, "baseType", new TextValue(BaseTypeWordOf(type.BaseType))));
                typeMutations.Add(new FieldWriteMutation(id, "values", new TextValue(string.Join(",", type.Values ?? []))));
                typeMutations.Add(new FieldWriteMutation(id, "order", new IntValue(index * 10)));
            }
            else
                typeCreates.Add(new CommitCreate(-id, "MetaType", fields, id));
        }
        var typeResult = store.CommitBatch(typeCreates, typeMutations);
        foreach (var created in typeResult.Creates)
            store.AddToSet(typesSet.Id, created.RealId);

        foreach (var type in targetDesc.AllTypes().Where(t => !BaseTypes.IsName(t.Name)))
        {
            var typeId = typeIds[type.Name];
            if (store.ReadById(typeId) is not ("MetaType", var typeFields)) continue;
            var propsSet = typeFields.Fields.GetValueOrDefault("props") as SetValue
                ?? throw new InvalidOperationException($"MetaType {typeId} has no `props` set.");
            var wantedPropIds = (type.Props ?? []).Select(p => propIds[$"{type.Name}.{p.Name}"]).ToHashSet();
            foreach (var currentPropId in propsSet.Members.Keys.ToList())
                if (!wantedPropIds.Contains(currentPropId))
                    store.RemoveFromSet(propsSet.Id, currentPropId);

            var propCreates = new List<CommitCreate>();
            var propMutations = new List<CommitMutation>();
            foreach (var (index, prop) in (type.Props ?? []).Select((p, i) => (i, p)))
            {
                var id = propIds[$"{type.Name}.{prop.Name}"];
                var fields = new ObjectValue(new Dictionary<string, NodeValue>
                {
                    ["name"] = new TextValue(prop.Name),
                    ["type"] = new TextValue(prop.Type),
                    ["cardinality"] = new TextValue(CardinalityWordOf(prop.Cardinality)),
                    ["keyType"] = new TextValue(prop.KeyType ?? ""),
                    ["multiline"] = new BoolValue(prop.Multiline),
                    ["order"] = new IntValue(index * 10),
                });
                if (store.ReadById(id) is ("MetaProp", _))
                {
                    propMutations.Add(new FieldWriteMutation(id, "name", new TextValue(prop.Name)));
                    propMutations.Add(new FieldWriteMutation(id, "type", new TextValue(prop.Type)));
                    propMutations.Add(new FieldWriteMutation(id, "cardinality", new TextValue(CardinalityWordOf(prop.Cardinality))));
                    propMutations.Add(new FieldWriteMutation(id, "keyType", new TextValue(prop.KeyType ?? "")));
                    propMutations.Add(new FieldWriteMutation(id, "multiline", new BoolValue(prop.Multiline)));
                    propMutations.Add(new FieldWriteMutation(id, "order", new IntValue(index * 10)));
                }
                else
                    propCreates.Add(new CommitCreate(-id, "MetaProp", fields, id));
            }
            var propResult = store.CommitBatch(propCreates, propMutations);
            foreach (var created in propResult.Creates)
                store.AddToSet(propsSet.Id, created.RealId);
        }

        var sections = DesignerSeed.SplitSections(targetText);
        store.WriteField(designId, "initialData", new TextValue(sections.GetValueOrDefault("initialData", "")));
        store.WriteField(designId, "access", new TextValue(sections.GetValueOrDefault("access", "")));
        store.WriteField(designId, "common", new TextValue(sections.GetValueOrDefault("common", "")));
        store.WriteField(designId, "ui", new TextValue(sections.GetValueOrDefault("ui", "")));
    }

    // createBranch(design, name): clone a WORKING COPY's whole subgraph (Design + its MetaTypes + its
    // MetaProps) into a NEW Branch. `design` is a working-copy row — main's own Design row, or another
    // branch's clone (branching off a branch is allowed) — resolved to its OWNING Branch by workingCopy
    // match (the same idiom commitDesign's FindMainBranch uses, widened to ANY branch, not just "main").
    //
    // ORIGIN FLATTENING (pinned): every clone's `origin` = the source row's OWN `origin` if it is already
    // non-zero, else the source row's id. So a branch-of-a-branch's clone anchors to the SAME lineage
    // origin as its grandparent — N-deep branching never chains ("origin of an origin"), which is exactly
    // what lets DesignMerger join three arbitrarily-deep branches' rows on one shared lineage key.
    //
    // The clone is ONE atomic CommitBatch (creates + set-links + ref-links) — the SAME atomicity discipline
    // commitDesign uses. The new Design is linked NOWHERE except the new Branch.workingCopy — critically,
    // NOT into db.designs (the app list stays main working copies only); its GC-reachability instead rides
    // Branch.workingCopy, with the Branch itself linked into db.branches (a root set) — verified below by
    // the Gherkin's "clone renders / stays alive" scenario, since GC walks the root Db's own fields
    // (JsonFileInstanceStore.CollectGarbage marks from _db.Root, and db.branches is one of Db's own set
    // props) exactly the same as it walks db.designs.
    private object? CreateBranch(JsonElement args)
    {
        var sourceDesignId = ArgInt(args, 0);
        var name = ArgText(args, 1);

        var store = resolveStore();

        var sourceDesign = ReadDesign(store, sourceDesignId);
        var sourceBranch = FindOrCreateMainBranchByWorkingCopy(store, sourceDesignId)
            ?? throw new InvalidOperationException($"Design {sourceDesignId} has no owning branch to branch from.");

        // Name uniqueness among the APP's branches — resolved via lineage (the app's ORIGIN anchor), so a
        // branch name collides with another branch of the SAME app, never a different app's branch of the
        // same name.
        var appLineage = LineageOf(sourceDesign, sourceDesignId);
        foreach (var (_, branch) in store.ReadExtent("Branch"))
        {
            if (branch.Fields.GetValueOrDefault("name") is not TextValue { Text: var existingName } || existingName != name) continue;
            if (branch.Fields.GetValueOrDefault("workingCopy") is not ReferenceValue { TargetId: { } wcId }) continue;
            if (store.ReadById(wcId) is not (var wtn, var wf) || wtn != "Design") continue;
            if (LineageOf(wf, wcId) == appLineage)
                throw new InvalidOperationException(
                    $"A branch named '{name}' already exists for this app — branch names must be unique per app.");
        }

        var branchesSetId = (store.ReadNode(NodePath.Root.Field("branches")) as SetValue)?.Id
            ?? throw new InvalidOperationException("The designer's `db.branches` set is missing.");

        // ── batch 1: mint the clone (Design + its MetaTypes + their MetaProps), unlinked ────────────
        // CommitBatch has no "link a member into a set this SAME batch just minted" primitive that doesn't
        // require knowing the set's id upfront (a set's id is assigned only AT mint time) — so the clone
        // is TWO batches: mint everything first (reading back each create's own minted collection ids via
        // CommitCreateResult.Collections), then a second batch links every member into its owner's
        // freshly-known collection id + creates the Branch. Both batches are still "ordinary store writes"
        // (no new CommitMutation shape), and each is independently atomic; a crash between them leaves an
        // orphan Design/MetaType/MetaProp subgraph reachable from NOWHERE (not yet linked to any Branch),
        // so it is inert dead data a future GC pass collects — never a half-wired live branch.
        const int designTemp = -1;
        var creates = new List<CommitCreate>
        {
            new CommitCreate(designTemp, "Design", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["label"]       = new TextValue(TextOf(sourceDesign, "label")),
                ["initialData"] = new TextValue(TextOf(sourceDesign, "initialData")),
                ["access"]      = new TextValue(TextOf(sourceDesign, "access")),
                ["common"]      = new TextValue(TextOf(sourceDesign, "common")),
                ["ui"]          = new TextValue(TextOf(sourceDesign, "ui")),
                ["origin"]      = new IntValue(appLineage),
            })),
        };
        // typeTempToProps[typeTemp] = the prop temp-ids owned by that type — remembered while building the
        // create list (one pass, no re-walk needed).
        var typeTempToProps = new List<(int TypeTemp, List<int> PropTemps)>();
        var nextTemp = -2;

        if (sourceDesign.Fields.GetValueOrDefault("types") is SetValue sourceTypesSet)
            foreach (var (typeId, typeVal) in sourceTypesSet.Members)
                if (typeVal is ObjectValue typeObj)
                {
                    var typeTemp = nextTemp--;
                    creates.Add(new CommitCreate(typeTemp, "MetaType", new ObjectValue(new Dictionary<string, NodeValue>
                    {
                        ["name"]     = new TextValue(TextOf(typeObj, "name")),
                        ["baseType"] = new TextValue(TextOf(typeObj, "baseType")),
                        ["values"]   = new TextValue(TextOf(typeObj, "values")),
                        ["order"]    = new IntValue(IntOf(typeObj, "order")),
                        ["origin"]   = new IntValue(LineageOf(typeObj, typeId)),
                    })));

                    var propTemps = new List<int>();
                    if (typeObj.Fields.GetValueOrDefault("props") is SetValue propsSet)
                        foreach (var (propId, propVal) in propsSet.Members)
                            if (propVal is ObjectValue propObj)
                            {
                                var propTemp = nextTemp--;
                                creates.Add(new CommitCreate(propTemp, "MetaProp", new ObjectValue(new Dictionary<string, NodeValue>
                                {
                                    ["name"]        = new TextValue(TextOf(propObj, "name")),
                                    ["type"]        = new TextValue(TextOf(propObj, "type")),
                                    ["cardinality"] = new TextValue(TextOf(propObj, "cardinality")),
                                    ["keyType"]     = new TextValue(TextOf(propObj, "keyType")),
                                    ["multiline"]   = new BoolValue(BoolOf(propObj, "multiline")),
                                    ["order"]       = new IntValue(IntOf(propObj, "order")),
                                    ["origin"]      = new IntValue(LineageOf(propObj, propId)),
                                })));
                                propTemps.Add(propTemp);
                            }
                    typeTempToProps.Add((typeTemp, propTemps));
                }

        var mintResult = store.CommitBatch(creates, []);
        var byTemp = mintResult.Creates.ToDictionary(c => c.TempId);
        var designRealId = byTemp[designTemp].RealId;
        var typesSetId = byTemp[designTemp].Collections["types"].Id;

        // ── batch 2: link every minted row into its owner's freshly-known collection + the new Branch ──
        var linkMutations = new List<CommitMutation>();
        foreach (var (typeTemp, propTemps) in typeTempToProps)
        {
            linkMutations.Add(new SetLinkMutation(typesSetId, byTemp[typeTemp].RealId));
            var propsSetId = byTemp[typeTemp].Collections["props"].Id;
            foreach (var propTemp in propTemps)
                linkMutations.Add(new SetLinkMutation(propsSetId, byTemp[propTemp].RealId));
        }

        const int branchTemp = -1;
        var branchCreates = new List<CommitCreate>
        {
            new CommitCreate(branchTemp, "Branch", new ObjectValue(new Dictionary<string, NodeValue> { ["name"] = new TextValue(name) })),
        };
        linkMutations.Add(new SetLinkMutation(branchesSetId, branchTemp));
        linkMutations.Add(new RefLinkMutation(branchTemp, "workingCopy", designRealId, "Design"));
        // The new branch STARTS at the source branch's current head (a fresh branch is a checkout of
        // whatever the source already built) — a source branch with no commits yet leaves `head` unset,
        // same as EnsureMainBranches leaves a design's first main branch before its baseline commit.
        if (sourceBranch.Fields.Fields.GetValueOrDefault("head") is ReferenceValue { TargetId: { } sourceHeadId })
            linkMutations.Add(new RefLinkMutation(branchTemp, "head", sourceHeadId, "Commit"));

        store.CommitBatch(branchCreates, linkMutations);
        return null;
    }

    // mergeBranch(source, target, resolutions?): a lineage-keyed three-way structural merge of `source`'s
    // branch into `target`'s (M13 slice 5). `source`/`target` are WORKING-COPY ids (main's own Design row,
    // or a branch's clone), resolved to their owning Branch by workingCopy match.
    //
    // DRIFT REFUSAL (pinned): if EITHER working copy's current Snapshot(wc).Text differs from its branch
    // head's cached text, refuse — merge computes over COMMITTED heads only, never a dirty working copy.
    //
    // LCA (v1, pinned): walk the parent+mergeParent DAG from both heads, base = the common ancestor with
    // the MAX logSeq. base == source.head → nothing to merge (no-op). base == target.head → the merge
    // STILL produces a two-parent merge commit (no ref-only fast-forward — simpler, honest per the
    // orchestrator's settled model).
    //
    // CLEAN path: DesignMerger.Compute over the three commits' cached {Text, IdMap}; apply the merged
    // result to the TARGET working copy via ORDINARY store writes (a CommitBatch for creates/renames/
    // field-writes, plain RemoveFromSet calls for existence-removals — CommitBatch has no remove case, and
    // per the orchestrator's accepted v1 gap a crash between these writes and the merge commit below is a
    // recoverable, documented residual, not required to be one giant atomic unit), THEN commit through the
    // SAME atomic path commitDesign uses (parent = target's head, mergeParent = source's head).
    //
    // The code/access sections are REPRINTED (AppPrint.Print over the merged Common/Ui/Rules), which
    // canonicalizes the target's own section formatting on this first merge — harmless, since the Code
    // language has no comments to lose and the printer is already canonical, so every merge AFTER this one
    // reprints byte-stable (nothing left to canonicalize a second time).
    //
    // CONFLICT path: NO writes at all — returns a MergeReport whose `conflicts` list the caller re-runs
    // against with `resolutions: [{id, take: "source"|"target"}]`; any conflict without a resolution
    // refuses again (the remainder is reported).
    private object? MergeBranch(JsonElement args)
    {
        var sourceDesignId = ArgInt(args, 0);
        var targetDesignId = ArgInt(args, 1);
        var resolutions = ArgResolutionsOptional(args, 2).ToDictionary(r => r.Id, r => r.Take);

        // The caller's ONE live store (the single writer). Concretely a JsonFileInstanceStore — the clean
        // apply (ApplyMergeToTarget) needs the concrete type for its batch/remove writes; every kernel-hosted
        // store IS one, so the cast never fails.
        var store = (JsonFileInstanceStore)resolveStore();

        // The SHARED compute (drift/no-op/conflict decision + the clean MergeComputation), IDENTICAL to what
        // sys.mergePreview reads — so preview and apply can never diverge (M13 Track-B B4). A plan that is
        // not clean-appliable (drift, no-op, or unresolved conflicts) already carries its final MergeReport
        // and made NO writes; only a CLEAN plan reaches the apply below.
        var plan = ComputeMergePlan(store, sourceDesignId, targetDesignId, resolutions);
        if (!plan.CleanAppliable)
            return plan.Report;

        // ── clean apply: ordinary store writes onto the TARGET working copy, then the merge commit ──
        ApplyMergeToTarget(store, targetDesignId, plan.Computation!);

        var mergeCommitId = CreateMergeCommit(store, targetDesignId, plan.TargetBranchId, plan.TargetCommit, plan.SourceCommit,
            plan.Report.SourceBranch, plan.Report.TargetBranch);

        return plan.Report with { Merged = true, MergeCommit = mergeCommitId };
    }

    // The result of the shared three-way merge COMPUTE (M13 Track-B B4): the MergeReport an operator sees
    // PLUS the extra state a clean apply needs. `CleanAppliable` is true iff the merge would apply with NO
    // conflicts (not drift, not a no-op, no unresolved conflicts) — the ONLY case where `Computation` is
    // non-null and the caller may write. In every other case `Report` is the final answer and NO write may
    // happen. `sys.mergePreview` returns `Report` verbatim; `sys.mergeBranch` applies `Computation` when
    // `CleanAppliable`, then stamps `Report.Merged = true` with the new merge commit.
    internal sealed record MergePlan(
        MergeReport Report, bool CleanAppliable, MergeComputation? Computation,
        int TargetBranchId, int TargetCommit, int SourceCommit);

    // Compute the three-way merge of `source` into `target` WITHOUT writing — the shared core behind both
    // sys.mergeBranch (apply) and sys.mergePreview (read). Everything through the conflict/clean decision is
    // here; the ONLY thing MergeBranch adds is the actual write (ApplyMergeToTarget + CreateMergeCommit). A
    // clean plan carries its Computation so the apply path reuses the EXACT merged result the preview showed
    // — preview and apply cannot drift because they run the identical compute over the identical committed
    // heads. `internal static` so the SsrRenderer-built preview delegate (same assembly) reaches it directly
    // WITHOUT a KernelHostActions instance, like the Publish path shares the static PublishReportComputer.
    internal static MergePlan ComputeMergePlan(
        JsonFileInstanceStore store, int sourceDesignId, int targetDesignId, IReadOnlyDictionary<string, string> resolutions)
    {
        var sourceDesign = ReadDesign(store, sourceDesignId);
        var targetDesign = ReadDesign(store, targetDesignId);
        var sourceBranch = FindOrCreateMainBranchByWorkingCopy(store, sourceDesignId)
            ?? throw new InvalidOperationException($"Design {sourceDesignId} has no owning branch.");
        var targetBranch = FindOrCreateMainBranchByWorkingCopy(store, targetDesignId)
            ?? throw new InvalidOperationException($"Design {targetDesignId} has no owning branch.");
        var sourceBranchName = TextOf(sourceBranch.Fields, "name");
        var targetBranchName = TextOf(targetBranch.Fields, "name");

        MergePlan Refused(MergeReport report) => new(report, false, null, 0, 0, 0);

        var sourceAppLineage = LineageOf(sourceDesign, sourceDesignId);
        var targetAppLineage = LineageOf(targetDesign, targetDesignId);
        if (sourceAppLineage != targetAppLineage)
            throw new InvalidOperationException(
                "Cannot merge: the source and target branches belong to different apps.");

        var sourceHeadRef = sourceBranch.Fields.Fields.GetValueOrDefault("head") as ReferenceValue;
        var targetHeadRef = targetBranch.Fields.Fields.GetValueOrDefault("head") as ReferenceValue;
        if (sourceHeadRef?.TargetId is not { } sourceHeadId)
            throw new InvalidOperationException($"Branch '{sourceBranchName}' has no commits to merge.");
        if (targetHeadRef?.TargetId is not { } targetHeadId)
            throw new InvalidOperationException($"Branch '{targetBranchName}' has no commits to merge into.");

        var sourceHeadFields = FindCommit(store, sourceHeadId)!;
        var targetHeadFields = FindCommit(store, targetHeadId)!;

        // Drift refusal: the working copy's CURRENT text must equal its own branch head's cached text.
        var sourceWorking = SchemaBridge.Snapshot(sourceDesign).Text;
        if (sourceWorking != TextOf(sourceHeadFields, "text"))
            return Refused(new MergeReport { Merged = false, NoOp = false, SourceBranch = sourceBranchName, TargetBranch = targetBranchName,
                BaseCommit = 0, SourceCommit = sourceHeadId, TargetCommit = targetHeadId,
                Conflicts = [], AccessChanges = [], DriftRefusal = "source" });
        var targetWorking = SchemaBridge.Snapshot(targetDesign).Text;
        if (targetWorking != TextOf(targetHeadFields, "text"))
            return Refused(new MergeReport { Merged = false, NoOp = false, SourceBranch = sourceBranchName, TargetBranch = targetBranchName,
                BaseCommit = 0, SourceCommit = sourceHeadId, TargetCommit = targetHeadId,
                Conflicts = [], AccessChanges = [], DriftRefusal = "target" });

        var baseCommitId = FindLca(store, sourceHeadId, targetHeadId);
        if (baseCommitId == sourceHeadId)
            return Refused(new MergeReport { Merged = false, NoOp = true, SourceBranch = sourceBranchName, TargetBranch = targetBranchName,
                BaseCommit = baseCommitId, SourceCommit = sourceHeadId, TargetCommit = targetHeadId,
                Conflicts = [], AccessChanges = [] });

        var baseFields = FindCommit(store, baseCommitId)!;
        var baseSnapshot = new DesignSnapshot(TextOf(baseFields, "text"), IdMapOf(baseFields));
        var sourceSnapshot = new DesignSnapshot(TextOf(sourceHeadFields, "text"), IdMapOf(sourceHeadFields));
        var targetSnapshot = new DesignSnapshot(TextOf(targetHeadFields, "text"), IdMapOf(targetHeadFields));

        int LineageOfRowId(int rowId)
        {
            var row = store.ReadById(rowId);
            if (row is null) return rowId; // the row no longer exists — it can only anchor to itself
            return LineageOf(row.Value.Fields, rowId);
        }

        // `resolutions` is threaded straight into Compute (not a post-process): a resolved conflict never
        // becomes a MergeConflict at all — its picked side's value is spliced in AT THE EXACT POINT the
        // conflict would otherwise be recorded (see DesignMerger.Resolve*/TryTakeSide), so everything
        // downstream that depends on a resolved value (e.g. a type rename feeding its still-owned props)
        // sees the resolved value, not a placeholder. Any conflict WITHOUT a matching resolution id still
        // ends up in `computation.Conflicts` and blocks the merge.
        var computation = DesignMerger.Compute(baseSnapshot, sourceSnapshot, targetSnapshot, LineageOfRowId, resolutions);

        if (computation.Conflicts.Count > 0)
            return Refused(new MergeReport
            {
                Merged = false, NoOp = false, SourceBranch = sourceBranchName, TargetBranch = targetBranchName,
                BaseCommit = baseCommitId, SourceCommit = sourceHeadId, TargetCommit = targetHeadId,
                Conflicts = [.. computation.Conflicts.Select(c => new ConflictItem(c.Id, c.Kind, c.Path, c.Field, c.Base, c.Source, c.Target))],
                AccessChanges = [.. computation.AccessChanges.Select(a => new AccessChangeReportItem(a.RuleKey, a.Change, a.Condition))],
            });

        // CLEAN: no conflicts, not drift, not a no-op. The report is `Merged = false` here (nothing has been
        // written yet — this is exactly what the PREVIEW shows: "ready to merge, N access changes"); the apply
        // path stamps `Merged = true` + the merge commit id after ApplyMergeToTarget/CreateMergeCommit succeed.
        var cleanReport = new MergeReport
        {
            Merged = false, NoOp = false, SourceBranch = sourceBranchName, TargetBranch = targetBranchName,
            BaseCommit = baseCommitId, SourceCommit = sourceHeadId, TargetCommit = targetHeadId, Conflicts = [],
            AccessChanges = [.. computation.AccessChanges.Select(a => new AccessChangeReportItem(a.RuleKey, a.Change, a.Condition))],
        };
        return new MergePlan(cleanReport, true, computation, targetBranch.Id, targetHeadId, sourceHeadId);
    }

    // Apply a CLEAN MergeComputation onto the TARGET working copy — "ordinary store writes" (per the
    // orchestrator's settled model), NOT one giant atomic unit with the merge commit below (that crash
    // window is an accepted, documented v1 gap — see MergeBranch's own doc). Three steps:
    //   1. Types/props: per LINEAGE, update the target's EXISTING row's fields if it already has one for
    //      that lineage, else MINT a new MetaType/MetaProp (origin = that lineage) and link it in; a
    //      lineage the merge DROPPED (present in target's own set, absent from the merged result) is
    //      REMOVED from that set (RemoveFromSet — CommitBatch has no remove case, so removals run as their
    //      own small store calls, GC-collecting the dropped row afterward).
    //   2. Order: written directly onto each row's `order` FIELD from the merged type/prop list's target-
    //      spine position (FieldWriteMutation) — the actual renormalization, not just an in-memory order.
    //   3. Code/access/initialData: reprint the merged Common/Ui/Rules into text (AppPrint.Print over a
    //      types-less/initialData-less description, then DesignerSeed.SplitSections pulls just
    //      common/ui/access) and write all four Design text fields via WriteField.
    private static void ApplyMergeToTarget(JsonFileInstanceStore store, int targetDesignId, MergeComputation computation)
    {
        var targetTypesSet = store.ReadNode(NodePath.Root.Field("designs").Key(targetDesignId.ToString()).Field("types")) as SetValue
            ?? (ReadDesign(store, targetDesignId).Fields.GetValueOrDefault("types") as SetValue)
            ?? throw new InvalidOperationException($"Design {targetDesignId} has no `types` set.");
        var typesSetId = targetTypesSet.Id;

        // The target's OWN MetaType rows (this design's live `types` set membership), keyed by lineage —
        // "does the target already have a row for lineage L" must be scoped to THIS design's own set (a
        // MetaType extent is shared across every design/branch in the store).
        var existingTypesByLineage = new Dictionary<int, int>(); // lineage -> row id
        foreach (var memberId in targetTypesSet.Members.Keys)
        {
            var fields = store.ReadById(memberId)!.Value.Fields;
            existingTypesByLineage[LineageOf(fields, memberId)] = memberId;
        }
        var mergedTypeLineages = computation.Types.Types.Select(t => t.Lineage).ToHashSet();
        var toRemoveTypeIds = existingTypesByLineage
            .Where(kv => !mergedTypeLineages.Contains(kv.Key)).Select(kv => kv.Value).ToList();

        // ── pass 1: mint every NEW type in one batch (+ field-write EXISTING ones' meta-fields) ──
        var typeCreates = new List<CommitCreate>();
        var typeMutations = new List<CommitMutation>();
        var typeTempByLineage = new Dictionary<int, int>();
        var typeNextTemp = -1;

        for (var i = 0; i < computation.Types.Types.Count; i++)
        {
            var mt = computation.Types.Types[i];
            var order = i * 10;
            if (existingTypesByLineage.TryGetValue(mt.Lineage, out var existingId))
            {
                typeMutations.Add(new FieldWriteMutation(existingId, "name", new TextValue(mt.Type.Name)));
                typeMutations.Add(new FieldWriteMutation(existingId, "baseType", new TextValue(BaseTypeWordOf(mt.Type.BaseType))));
                typeMutations.Add(new FieldWriteMutation(existingId, "values", new TextValue(string.Join(",", mt.Type.Values ?? []))));
                typeMutations.Add(new FieldWriteMutation(existingId, "order", new IntValue(order)));
            }
            else
            {
                var temp = typeNextTemp--;
                typeCreates.Add(new CommitCreate(temp, "MetaType", new ObjectValue(new Dictionary<string, NodeValue>
                {
                    ["name"]     = new TextValue(mt.Type.Name),
                    ["baseType"] = new TextValue(BaseTypeWordOf(mt.Type.BaseType)),
                    ["values"]   = new TextValue(string.Join(",", mt.Type.Values ?? [])),
                    ["order"]    = new IntValue(order),
                    ["origin"]   = new IntValue(mt.Lineage),
                })));
                typeTempByLineage[mt.Lineage] = temp;
            }
        }

        if (typeCreates.Count > 0 || typeMutations.Count > 0)
        {
            var typeMintResult = store.CommitBatch(typeCreates, typeMutations);
            var typeByTemp = typeMintResult.Creates.ToDictionary(c => c.TempId);
            foreach (var (lineage, temp) in typeTempByLineage)
            {
                var realId = typeByTemp[temp].RealId;
                store.AddToSet(typesSetId, realId);
                existingTypesByLineage[lineage] = realId;
            }
        }
        foreach (var removeId in toRemoveTypeIds)
            store.RemoveFromSet(typesSetId, removeId); // GC then collects the row and its own props set

        // ── pass 2: per SURVIVING merged type, mint/update/remove its OWN `props` set the same way ──
        foreach (var mt in computation.Types.Types)
        {
            var ownerId = existingTypesByLineage[mt.Lineage];
            var ownerFields = store.ReadById(ownerId)!.Value.Fields;
            var propsSet = ownerFields.Fields.GetValueOrDefault("props") as SetValue
                ?? throw new InvalidOperationException($"MetaType {ownerId} has no `props` set.");

            var existingPropsByLineage = new Dictionary<int, int>();
            foreach (var memberId in propsSet.Members.Keys)
            {
                var fields = store.ReadById(memberId)!.Value.Fields;
                existingPropsByLineage[LineageOf(fields, memberId)] = memberId;
            }
            var mergedPropLineages = mt.Props.Select(p => p.Lineage).ToHashSet();
            var toRemovePropIds = existingPropsByLineage
                .Where(kv => !mergedPropLineages.Contains(kv.Key)).Select(kv => kv.Value).ToList();

            var propCreates = new List<CommitCreate>();
            var propMutations = new List<CommitMutation>();
            var propTempByLineage = new Dictionary<int, int>();
            var propNextTemp = -1;

            for (var i = 0; i < mt.Props.Count; i++)
            {
                var mp = mt.Props[i];
                var order = i * 10;
                if (existingPropsByLineage.TryGetValue(mp.Lineage, out var existingPropId))
                {
                    propMutations.Add(new FieldWriteMutation(existingPropId, "name", new TextValue(mp.Prop.Name)));
                    propMutations.Add(new FieldWriteMutation(existingPropId, "type", new TextValue(mp.Prop.Type)));
                    propMutations.Add(new FieldWriteMutation(existingPropId, "cardinality", new TextValue(CardinalityWordOf(mp.Prop.Cardinality))));
                    propMutations.Add(new FieldWriteMutation(existingPropId, "keyType", new TextValue(mp.Prop.KeyType ?? "")));
                    propMutations.Add(new FieldWriteMutation(existingPropId, "multiline", new BoolValue(mp.Prop.Multiline)));
                    propMutations.Add(new FieldWriteMutation(existingPropId, "order", new IntValue(order)));
                }
                else
                {
                    var temp = propNextTemp--;
                    propCreates.Add(new CommitCreate(temp, "MetaProp", new ObjectValue(new Dictionary<string, NodeValue>
                    {
                        ["name"]        = new TextValue(mp.Prop.Name),
                        ["type"]        = new TextValue(mp.Prop.Type),
                        ["cardinality"] = new TextValue(CardinalityWordOf(mp.Prop.Cardinality)),
                        ["keyType"]     = new TextValue(mp.Prop.KeyType ?? ""),
                        ["multiline"]   = new BoolValue(mp.Prop.Multiline),
                        ["order"]       = new IntValue(order),
                        ["origin"]      = new IntValue(mp.Lineage),
                    })));
                    propTempByLineage[mp.Lineage] = temp;
                }
            }

            if (propCreates.Count > 0 || propMutations.Count > 0)
            {
                var propMintResult = store.CommitBatch(propCreates, propMutations);
                var propByTemp = propMintResult.Creates.ToDictionary(c => c.TempId);
                foreach (var (_, temp) in propTempByLineage)
                    store.AddToSet(propsSet.Id, propByTemp[temp].RealId);
            }
            foreach (var removeId in toRemovePropIds)
                store.RemoveFromSet(propsSet.Id, removeId);
        }

        // ── code/access/initialData: reprint the merged Common/Ui/Rules, write the four Design text fields ──
        var mergedDesc = new InstanceDescription(
            Types: null,
            Ui: new InstanceUi(computation.Code.UiVars, computation.Code.UiFunctions, computation.Code.Render),
            Common: new InstanceCommon(computation.Code.CommonFunctions),
            InitialData: null,
            Rules: computation.Access.Rules);
        var printed = AppPrint.Print(mergedDesc);
        var sections = DesignerSeed.SplitSections(printed);

        store.WriteField(targetDesignId, "initialData", new TextValue(computation.InitialData));
        store.WriteField(targetDesignId, "access", new TextValue(sections.GetValueOrDefault("access", "")));
        store.WriteField(targetDesignId, "common", new TextValue(sections.GetValueOrDefault("common", "")));
        store.WriteField(targetDesignId, "ui", new TextValue(sections.GetValueOrDefault("ui", "")));
    }

    // Create the TWO-PARENT merge Commit after a clean apply, via the SAME CaptureAndCommit core
    // sys.commitDesign uses (review fix — a merge commit no longer stamps logSeq/snapshots the design with
    // no s1/s2 optimistic-capture bracket; see CaptureAndCommit's own doc). `parent` = the TARGET's
    // PRE-merge head (the branch this commit lands on); `mergeParent` = the SOURCE's head (the branch
    // merged in). CaptureAndCommit re-reads the target design fresh under the bracket — the apply above
    // already wrote it through this SAME `store` instance, so its in-memory copy reflects the merge.
    private static int CreateMergeCommit(
        IInstanceStore store, int targetDesignId, int targetBranchId, int targetPreMergeHeadId, int sourceHeadId,
        string sourceBranchName, string targetBranchName) =>
        CaptureAndCommit(
            store, targetDesignId, $"Merged '{sourceBranchName}' into '{targetBranchName}'",
            migration: "", parentHeadId: targetPreMergeHeadId, mergeParentHeadId: sourceHeadId, targetBranchId);

    private static void ValidateMigration(string migration, string committedText)
    {
        if (string.IsNullOrWhiteSpace(migration)) return;

        ICodeStatement[] items;
        try
        {
            items = Parse.Run(CodeParse.Section("migration"), "migration\n" + IndentForSection(migration));
        }
        catch (CodeParseException ex)
        {
            throw new InvalidOperationException($"Invalid migration: {UnwrapMigrationParseError(ex.Message)}", ex);
        }

        var desc = InstanceDescriptionLoader.Load(committedText);
        foreach (var item in items)
        {
            if (item is not CodeFunction fn)
                throw new InvalidOperationException("Migration may only contain functions.");
            if (fn.Name is null || desc.FindType(fn.Name) is null)
                throw new InvalidOperationException($"Migration function '{fn.Name}' does not match a committed type.");
            if (fn.Params.Length != 1)
                throw new InvalidOperationException($"Migration function '{fn.Name}' must take exactly one argument.");
            RejectMigrationShadow(fn);
        }
    }

    private static string IndentForSection(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Length == 0 ? "" : "    " + line));

    // ValidateMigration parses the author's text wrapped in a synthetic "migration\n" header line plus a
    // 4-space indent (IndentForSection above), so Parse.Run's positioned error — message-only, no
    // structured line/column (CodeParseException) — reports coordinates shifted by +1 line and +4
    // columns relative to what the author actually typed in the commit-bar textarea. Rewrite the
    // embedded "line N, column M" and the echoed source line's leading indent back to the author's
    // own coordinates before this ever reaches the operator.
    private static string UnwrapMigrationParseError(string message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            message, @"^Parse error at line (\d+), column (\d+):\n(.*)\n( *)\^$",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success) return message;

        var line = int.Parse(match.Groups[1].Value) - 1;
        var column = int.Parse(match.Groups[2].Value) - 4;
        var sourceLine = match.Groups[3].Value;
        var caretIndent = match.Groups[4].Value.Length - 4;
        if (sourceLine.StartsWith("    ")) sourceLine = sourceLine[4..];
        var caret = new string(' ', Math.Max(0, caretIndent)) + "^";
        return $"Parse error at line {line}, column {column}:\n{sourceLine}\n{caret}";
    }

    private static void RejectMigrationShadow(CodeFunction fn)
    {
        foreach (var p in fn.Params)
            if (p.Name is "new" or "oldDb")
                throw new InvalidOperationException($"Migration function '{fn.Name}' may not shadow '{p.Name}'.");
        if (fn.Name is "new" or "oldDb")
            throw new InvalidOperationException($"Migration may not declare '{fn.Name}'.");
        RejectMigrationShadow(fn.Body, fn.Name ?? "(anonymous)");
    }

    private static void RejectMigrationShadow(CodeBlock block, string owner)
    {
        foreach (var statement in block.Statements)
            RejectMigrationShadowStatement(statement, owner);
    }

    private static void RejectMigrationShadowStatement(ICodeStatement statement, string owner)
    {
        switch (statement)
        {
            case CodeBlock block:
                RejectMigrationShadow(block, owner);
                break;
            case CodeFunction fn:
                RejectMigrationShadow(fn);
                break;
            case CodeIf codeIf:
                RejectMigrationShadowValue(codeIf.Condition, owner);
                RejectMigrationShadowStatement(codeIf.Body, owner);
                if (codeIf.ElseBody != null) RejectMigrationShadowStatement(codeIf.ElseBody, owner);
                break;
            case CodeVarDec { Name: "new" or "oldDb" } v:
                throw new InvalidOperationException($"Migration function '{owner}' may not shadow '{v.Name}'.");
            case CodeVarDec v:
                if (v.Value != null) RejectMigrationShadowValue(v.Value, owner);
                break;
            case CodeReturn ret:
                RejectMigrationShadowValue(ret.Value, owner);
                break;
            case CodeAssignment assign:
                RejectMigrationShadowValue(assign.Target, owner);
                RejectMigrationShadowValue(assign.Value, owner);
                break;
            case CodeCall call:
                RejectMigrationShadowValue(call, owner);
                break;
            case CodeAmbient { Name: "new" or "oldDb" } ambient:
                throw new InvalidOperationException($"Migration function '{owner}' may not shadow '{ambient.Name}'.");
            case CodeAmbient ambient:
                RejectMigrationShadowValue(ambient.Value, owner);
                break;
        }
    }

    private static void RejectMigrationShadowValue(ICodeValue value, string owner)
    {
        switch (value)
        {
            case CodeFunction fn:
                RejectMigrationShadow(fn);
                break;
            case CodeArray array:
                foreach (var item in array.Items) RejectMigrationShadowValue(item, owner);
                break;
            case CodeObject obj:
                foreach (var prop in obj.Props) RejectMigrationShadowValue(prop.Value, owner);
                break;
            case CodeInfixOp infix:
                RejectMigrationShadowValue(infix.Left, owner);
                RejectMigrationShadowValue(infix.Right, owner);
                break;
            case CodeNot not:
                RejectMigrationShadowValue(not.Operand, owner);
                break;
            case CodeTernary ternary:
                RejectMigrationShadowValue(ternary.Condition, owner);
                RejectMigrationShadowValue(ternary.Then, owner);
                RejectMigrationShadowValue(ternary.Else, owner);
                break;
            case CodeCall call:
                RejectMigrationShadowValue(call.Fn, owner);
                foreach (var param in call.Params) RejectMigrationShadowValue(param, owner);
                break;
            case CodeAssignment assign:
                RejectMigrationShadowValue(assign.Target, owner);
                RejectMigrationShadowValue(assign.Value, owner);
                break;
            case CodeTag tag:
                foreach (var attr in tag.Attributes) RejectMigrationShadowValue(attr.Value, owner);
                foreach (var child in tag.Children) RejectMigrationShadowTagChild(child, owner);
                break;
        }
    }

    private static void RejectMigrationShadowTagChild(ICodeTagChild child, string owner)
    {
        switch (child)
        {
            case ICodeValue value:
                RejectMigrationShadowValue(value, owner);
                break;
            case CodeTagForEach { Item.Name: "new" or "oldDb" } loop:
                throw new InvalidOperationException($"Migration function '{owner}' may not shadow '{loop.Item.Name}'.");
            case CodeTagForEach loop:
                RejectMigrationShadowValue(loop.Collection, owner);
                foreach (var nested in loop.Body) RejectMigrationShadowTagChild(nested, owner);
                break;
            case CodeTagIf tagIf:
                RejectMigrationShadowValue(tagIf.Condition, owner);
                foreach (var nested in tagIf.Body) RejectMigrationShadowTagChild(nested, owner);
                foreach (var nested in tagIf.ElseBody) RejectMigrationShadowTagChild(nested, owner);
                break;
        }
    }

    // A Design row by id (main's own row, or a branch clone) — the merge/branch actions address a
    // WORKING COPY directly (not "a design in db.designs"), so this reads by raw id rather than requiring
    // set membership (createBranch's sibling ResolveDesign DOES require db.designs membership, since it
    // is the publish/create/setDesign convention of "one design out of the app list" — a working-copy
    // clone is deliberately NOT in that list).
    private static ObjectValue ReadDesign(IInstanceStore store, int designId) =>
        store.ReadById(designId) is (var typeName, var fields) && typeName == "Design" ? fields
            : throw new InvalidOperationException($"No design with id {designId}.");

    // Any Branch whose `workingCopy` points at `designId` — the widened sibling of commitDesign's
    // FindMainBranch (which only matches the "main"-named one). A branch's working copy is 1:1 (createBranch
    // mints exactly one Branch per clone), so at most one match.
    private static (int Id, ObjectValue Fields)? FindBranchByWorkingCopy(IInstanceStore store, int designId)
    {
        foreach (var (id, branch) in store.ReadExtent("Branch"))
            if (branch.Fields.GetValueOrDefault("workingCopy") is ReferenceValue { TargetId: var t } && t == designId)
                return (id, branch);
        return null;
    }

    // Runtime-created designs enter db.designs after boot, so EnsureMainBranches has never seen them.
    // Lazily mint the same empty `main` branch shape the boot path gives a design before its first commit.
    private static (int Id, ObjectValue Fields)? FindOrCreateMainBranchByWorkingCopy(IInstanceStore store, int designId)
    {
        if (FindBranchByWorkingCopy(store, designId) is { } existing) return existing;
        if (store.ReadNode(NodePath.Root.Field("designs").Key(designId.ToString())) is not ObjectValue)
            return null;
        var branchesSetId = (store.ReadNode(NodePath.Root.Field("branches")) as SetValue)?.Id
            ?? throw new InvalidOperationException("The designer's `db.branches` set is missing.");

        const int branchTemp = -1;
        var result = store.CommitBatch(
            [new CommitCreate(branchTemp, "Branch", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["name"] = new TextValue("main"),
            }))],
            [
                new SetLinkMutation(branchesSetId, branchTemp),
                new RefLinkMutation(branchTemp, "workingCopy", designId, "Design"),
            ]);
        var branchId = result.Creates.First(c => c.TempId == branchTemp).RealId;
        return store.ReadById(branchId) is ("Branch", var fields) ? (branchId, fields) : null;
    }

    // A row's lineage anchor: its own `origin` field if non-zero, else its own id (it IS its own lineage
    // origin — the base case every un-branched row starts as). Works for Design/MetaType/MetaProp alike
    // (all three carry the same `origin int` field per M13 slice 5's schema addition).
    private static int LineageOf(ObjectValue fields, int ownId) =>
        fields.Fields.GetValueOrDefault("origin") is IntValue { Value: var o } && o != 0 ? o : ownId;

    private static int IntOf(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is IntValue i ? i.Value : 0;

    private static bool BoolOf(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is BoolValue b && b.Value;

    // The designer's stored `baseType`/`cardinality` field WORD for a typed BaseType/Cardinality — the
    // same mapping SchemaBridge.Project reads back (mirrors DesignMerger's own private BaseTypeWord/
    // CardinalityWord, kept local here since this file writes the designer's raw text fields directly).
    private static string BaseTypeWordOf(BaseType bt) => bt switch
    {
        BaseType.Object => "object",
        BaseType.Enum => "enum",
        _ => Instance.BaseTypes.NameOf(bt),
    };
    private static string CardinalityWordOf(Cardinality c) => c switch
    {
        Cardinality.Set => "set",
        Cardinality.Dictionary => "dictionary",
        _ => "single",
    };

    // Walk the parent+mergeParent DAG from BOTH heads and return the common ancestor with the MAX logSeq
    // (v1's settled LCA — accepts extra conflicts on a genuine criss-cross; not built here). Every ancestor
    // reachable from a commit (following `parent` AND `mergeParent`, since a prior merge commit has two
    // parents) is collected into that head's ancestor set (INCLUDING the head itself — a head can be its
    // own base when one side is a straight descendant of the other); the intersection's max-logSeq member
    // is the LCA.
    private static int FindLca(IInstanceStore store, int sourceHeadId, int targetHeadId)
    {
        var sourceAncestors = AncestorsOf(store, sourceHeadId);
        var targetAncestors = AncestorsOf(store, targetHeadId);
        var common = sourceAncestors.Keys.Intersect(targetAncestors.Keys).ToList();
        if (common.Count == 0)
            throw new InvalidOperationException("The two branches share no common commit history — cannot merge.");
        return common.OrderByDescending(id => sourceAncestors[id]).First();
    }

    // commitId → its logSeq, for every commit reachable by walking `parent`/`mergeParent` from `headId`
    // (inclusive of headId itself).
    private static Dictionary<int, int> AncestorsOf(IInstanceStore store, int headId)
    {
        var seqs = new Dictionary<int, int>();
        var stack = new Stack<int>();
        stack.Push(headId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (seqs.ContainsKey(id)) continue;
            var fields = FindCommit(store, id) ?? throw new InvalidOperationException($"Commit {id} referenced by the DAG no longer exists.");
            seqs[id] = IntOf(fields, "logSeq");
            if (fields.Fields.GetValueOrDefault("parent") is ReferenceValue { TargetId: { } p }) stack.Push(p);
            if (fields.Fields.GetValueOrDefault("mergeParent") is ReferenceValue { TargetId: { } mp }) stack.Push(mp);
        }
        return seqs;
    }

    private readonly record struct Resolution(string Id, string Take);

    private static IReadOnlyList<Resolution> ArgResolutionsOptional(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index) return [];
        var arg = args[index];
        // A Code array ships as { type:"array"|"set"|..., items:[...] } wrapping tagged scalars/objects,
        // OR a bare JSON array (defensive, matching the file's existing Arg* leniency). Each item is an
        // object literal `{ id, take }` — Code object values ship as { type:"object", props:{...} } with
        // each prop itself a tagged scalar, so read defensively through both shapes.
        var items = arg.ValueKind switch
        {
            JsonValueKind.Array => arg.EnumerateArray(),
            JsonValueKind.Object when arg.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array => arr.EnumerateArray(),
            _ => Enumerable.Empty<JsonElement>(),
        };
        var result = new List<Resolution>();
        foreach (var item in items)
        {
            var obj = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object
                ? v : item;
            var propsHolder = obj.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object ? props : obj;
            var id = ScalarText(propsHolder, "id");
            var take = ScalarText(propsHolder, "take");
            if (id.Length > 0 && take.Length > 0) result.Add(new Resolution(id, take));
        }
        return result;
    }

    private static string ScalarText(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return "";
        if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner))
            return inner.ValueKind == JsonValueKind.String ? inner.GetString() ?? "" : "";
        return "";
    }

    // The design's `main` Branch row (its `workingCopy` reference points at the design) — returned WITH
    // its own intrinsic id (ReadExtent keys the extent by id) so the caller can advance its `head` by id.
    private static (int Id, ObjectValue Fields)? FindMainBranch(IInstanceStore store, int designId)
    {
        foreach (var (id, branch) in store.ReadExtent("Branch"))
            if (branch.Fields.GetValueOrDefault("name") is TextValue { Text: "main" }
                && branch.Fields.GetValueOrDefault("workingCopy") is ReferenceValue { TargetId: var t } && t == designId)
                return (id, branch);
        return null;
    }

    // The design's `main` Branch's HEAD commit — its (id, fields), or null when the branch has no head yet
    // (a design with zero commits — the pre-slice-4 fallback path in Publish). Reads the branch, then
    // resolves its `head` reference into the Commit extent.
    // `internal` (was private) so PublishReportComputer — the shared publish/preview report core (M13
    // Track-B B3) — reuses the SAME commit lookups this action does, rather than a second copy that could
    // drift. Still assembly-private; only the extracted computer (same assembly) reaches them.
    internal static (int Id, ObjectValue Fields)? FindHeadCommit(IInstanceStore store, int designId)
    {
        if (FindMainBranch(store, designId) is not { } branch) return null;
        if (branch.Fields.Fields.GetValueOrDefault("head") is not ReferenceValue { TargetId: { } headId }) return null;
        return FindCommit(store, headId) is { } fields ? (headId, fields) : null;
    }

    // A Commit row by its own intrinsic id, or null if no such commit exists (a stale stamp naming a
    // commit this store no longer has — defensive, since Commit rows are never deleted by any grant).
    internal static ObjectValue? FindCommit(IInstanceStore store, int commitId) =>
        store.ReadById(commitId) is (var typeName, var fields) && typeName == "Commit" ? fields : null;

    internal static string TextOf(ObjectValue fields, string prop) =>
        fields.Fields.GetValueOrDefault(prop) is TextValue t ? t.Text : "";

    // The Commit's cached idMap dict field ("name-path" → intrinsic id) reconstructed into the plain
    // Dictionary<string,int> DesignSnapshot/DesignDiffer expect.
    internal static IReadOnlyDictionary<string, int> IdMapOf(ObjectValue fields)
    {
        var map = new Dictionary<string, int>();
        if (fields.Fields.GetValueOrDefault("idMap") is DictionaryValue dict)
            foreach (var (key, value) in dict.Entries)
                if (key is TextValue { Text: var path } && value is IntValue { Value: var id })
                    map[path] = id;
        return map;
    }

    // rename(id, name): update an instance's display label in the registry. arg 0 is the instance id,
    // arg 1 the new label text. The rename delegate updates the live spec and rewrites kernel.json.
    private object? Rename(JsonElement args)
    {
        var id = ArgInt(args, 0);
        var name = ArgText(args, 1);
        renameInstance(id, name).GetAwaiter().GetResult();
        return null;
    }

    // Read a required int argument from the evaluated-args JSON array. Code scalars ship as
    // { type, value }; accept a bare JSON number too (defensive). Anything else is a bad call.
    private static int ArgInt(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
            throw new InvalidOperationException($"host action expects an argument at position {index}.");
        var arg = args[index];
        if (arg.ValueKind == JsonValueKind.Number)
            return arg.GetInt32();
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        throw new InvalidOperationException($"host action expects an integer argument at position {index}.");
    }

    // Read a required text argument from the evaluated-args JSON array. Code scalars ship as
    // { type, value }; accept a bare JSON string too (defensive). Anything else is a bad call.
    private static string ArgText(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
            throw new InvalidOperationException($"host action expects an argument at position {index}.");
        var arg = args[index];
        if (arg.ValueKind == JsonValueKind.String)
            return arg.GetString()!;
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;
        throw new InvalidOperationException($"host action expects a text argument at position {index}.");
    }

    private static string ArgTextOptional(JsonElement args, int index, string defaultValue)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
            return defaultValue;
        var arg = args[index];
        if (arg.ValueKind == JsonValueKind.String)
            return arg.GetString()!;
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;
        throw new InvalidOperationException($"host action expects a text argument at position {index}.");
    }

    // Read an OPTIONAL int argument (M13 slice 7 — cloneInstance's `atSeq`), mirroring ArgBoolOptional's
    // shape: a call that omits it gets null (the default path, byte-identical to the pre-slice-7 clone —
    // minimal by default). Code scalars ship as { type, value }; a bare JSON number is accepted too
    // (defensive, matching ArgInt/ArgBoolOptional's leniency).
    private static int? ArgIntOptional(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
            return null;
        var arg = args[index];
        if (arg.ValueKind == JsonValueKind.Number)
            return arg.GetInt32();
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        throw new InvalidOperationException($"host action expects an integer argument at position {index}.");
    }

    // Read an OPTIONAL bare bool argument (M13 slice 4 — publish's `dryRun`): a call that omits the
    // argument gets `defaultValue`, matching "minimal by default" (dryRun is opt-in). Code scalars ship as
    // { type, value }; a bare JSON bool is accepted too (defensive, matching ArgInt/ArgText's leniency).
    private static bool ArgBoolOptional(JsonElement args, int index, bool defaultValue)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
            return defaultValue;
        var arg = args[index];
        if (arg.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return arg.GetBoolean();
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("value", out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return v.GetBoolean();
        throw new InvalidOperationException($"host action expects a boolean argument at position {index}.");
    }
}
