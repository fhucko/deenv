using DeEnv.Code;
using DeEnv.Designer;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Kernel;

// Which of the three publish legs a plan resolved to — so the caller (KernelHostActions.Publish) knows
// which side effects to perform, WITHOUT re-deciding the leg (the leg is decided once, in Compute).
//   • NoHead        — the design has zero commits: project the CURRENT working copy by name (WorkingDesign).
//   • Fallback      — the design has commits but the target was never stamped: one-time by-name apply of
//                     the head text (HeadText), then stamp.
//   • Versioned     — the identity-diff path: the boundary plan is already computed (dryRun-safe), the
//                     caller writes the head text + stamps + restarts on a real run.
public enum PublishLeg { NoHead, Fallback, Versioned }

// The full outcome of computing what a publish WOULD do — the structured PublishReport plus the leg and
// the artifacts the real-publish side effects need (M13 Track-B B3). This is the SINGLE report-computing
// core shared by the `sys.publish` host action (dryRun:false, then it performs the side effects) and the
// `sys.publishPreview` server-backed read (dryRun:true, returns the report only). ONE implementation, so a
// preview reports EXACTLY what an apply would do — never a second copy of the conversion rules that could
// drift from the real one (the intent the pre-extraction code already commented at Publish's dry-run leg).
//
// `Report` is what a preview surfaces and what a real publish returns. `Leg`/`HeadText`/`HeadCommitId`/
// `Diff` let Publish carry out the leg-specific writes (the boundary itself is already applied inside
// Compute on a real run — see the note there).
public sealed record PublishPlan(
    PublishReport Report, PublishLeg Leg, string WorkingDesign, string HeadText, int HeadCommitId);

// The report-computing core behind BOTH `sys.publish` (KernelHostActions.Publish) and `sys.publishPreview`
// (the kernel-wired preview delegate). Extracted from KernelHostActions.Publish so the two never diverge:
// the preview computes with `dryRun:true` (changing NOTHING — ApplyPublishBoundary's own dryRun flag skips
// its two disk-touching side effects) and returns the report; the apply computes with `dryRun:false` (so
// the boundary IS materialized here) and then performs the app-document write / stamp / restart itself.
//
// It reads the design's committed head from the CALLER's own store (the design is a row in the designer's
// store — the same store the diff/commit paths read), and the target's stamped commit id from the caller.
// It never writes the target's app document, never stamps, never restarts — those are the apply-only side
// effects the host action still owns (a preview must have none of them).
public static class PublishReportComputer
{
    // Compute the plan. `store` is the CALLER's (designer's) own live store; `design` the resolved Design
    // row (`ObjectValue`); `targetDataPath` the target instance's data file (the boundary rewrite target on
    // a real run); `stampedCommitId` the target's current versioning stamp (null = unstamped). On a real
    // run (`dryRun:false`) the versioned leg MATERIALIZES the boundary here (the destructive apply); on a
    // dry run it computes the boundary plan without writing. The leg-and-artifacts the caller needs for the
    // remaining apply-only side effects (app-doc write, stamp, restart) ride the returned plan.
    public static PublishPlan Compute(
        IInstanceStore store, int designId, ObjectValue design, string targetDataPath,
        int? stampedCommitId, bool dryRun)
    {
        var head = KernelHostActions.FindHeadCommit(store, designId);

        if (head is null)
        {
            // No commit exists for this design yet — nothing to diff against. The pre-slice-4 behavior:
            // project the CURRENT working copy and apply by name. Not reported as a "fallback" (that term
            // is reserved for an unstamped TARGET against a design that DOES have commits) — there is no
            // identity diff possible here at all.
            var workingDesign = SchemaBridge.ProjectDesignDb(design, store); // throws on an invalid design
            var noHeadReport = new PublishReport
            {
                Applied = !dryRun, DryRun = dryRun, BaseCommit = null, TargetCommit = 0,
                UncommittedDrift = false, Renames = [], Adds = [], Removes = [], Conversions = [],
                Cardinality = [], FallbackNameMatched = false,
            };
            return new PublishPlan(noHeadReport, PublishLeg.NoHead, workingDesign, "", 0);
        }

        var (headCommitId, headFields) = head.Value;
        var headText = KernelHostActions.TextOf(headFields, "text");
        var headIdMap = KernelHostActions.IdMapOf(headFields);
        var headSnapshot = new DesignSnapshot(headText, headIdMap);

        // Uncommitted working-copy drift: the design's LIVE state may have moved past its own head commit
        // (an edit made after the last commit) — Snapshot(workingCopy).Text != head.text. Reported, never
        // published: publish always deploys the committed head, never the working copy.
        var workingSnapshot = SchemaBridge.Snapshot(design, store); // throws on an invalid design
        var uncommittedDrift = workingSnapshot.Text != headText;

        var stampedFields = stampedCommitId is { } stamped ? KernelHostActions.FindCommit(store, stamped) : null;

        if (stampedFields is null)
        {
            // Unstamped (or a stamp naming a commit this store no longer has — defensive): the one-time
            // name-match fallback — the pre-slice-4 by-name apply (WriteDesign), carrying whatever a
            // by-name apply can, then stamping so the NEXT publish is identity-diffed and rename-safe.
            var fallbackReport = new PublishReport
            {
                Applied = !dryRun, DryRun = dryRun, BaseCommit = null, TargetCommit = headCommitId,
                UncommittedDrift = uncommittedDrift, Renames = [], Adds = [], Removes = [], Conversions = [],
                Cardinality = [], FallbackNameMatched = true,
                MigrationsSkipped = HasMigrationInDag(store, headCommitId),
            };
            return new PublishPlan(fallbackReport, PublishLeg.Fallback, "", headText, headCommitId);
        }

        // ── the versioned path: diff the STAMPED commit against the HEAD commit by identity ──
        var baseSnapshot = new DesignSnapshot(
            KernelHostActions.TextOf(stampedFields, "text"), KernelHostActions.IdMapOf(stampedFields));
        var diff = DesignDiffer.Compute(baseSnapshot, headSnapshot);
        var targetDesc = InstanceDescriptionLoader.Load(headText);

        var latestBoundary = JsonFileInstanceStore.LatestBoundary(targetDataPath);
        if (latestBoundary is { } latest && latest.DesignId == designId && latest.CommitId == headCommitId)
        {
            var alreadyReport = KernelHostActions.BuildReport(
                diff, new BoundaryApplyResult(false, [], []), applied: false, dryRun,
                stampedCommitId, headCommitId, uncommittedDrift, fallbackNameMatched: false);
            return new PublishPlan(alreadyReport, PublishLeg.Versioned, "", headText, headCommitId);
        }

        var baseCommitId = stampedCommitId ?? throw new InvalidOperationException("Stamped commit id is missing.");
        var boundaryResult = ComputeBoundary(
            store, targetDataPath, designId, baseCommitId, stampedFields, headCommitId, headFields,
            targetDesc, dryRun, out var migrations);

        var report = KernelHostActions.BuildReport(
            diff, boundaryResult, applied: !dryRun, dryRun, stampedCommitId, headCommitId,
            uncommittedDrift, fallbackNameMatched: false, migrations);

        return new PublishPlan(report, PublishLeg.Versioned, "", headText, headCommitId);
    }

    private static BoundaryApplyResult ComputeBoundary(
        IInstanceStore store, string targetDataPath, int designId, int stampedCommitId, ObjectValue stampedFields,
        int headCommitId, ObjectValue headFields, InstanceDescription headDesc, bool dryRun,
        out IReadOnlyList<MigrationRunReport> migrations)
    {
        var firstParent = FirstParentChainTo(store, headCommitId, stampedCommitId);
        var headAncestors = DagAncestors(store, headCommitId);
        if (firstParent is null)
        {
            if (headAncestors.Any(id => HasMigrationText(store, id)))
                throw new InvalidOperationException("cannot establish a migration path");
            var direct = DesignDiffer.Compute(Snapshot(stampedFields), Snapshot(headFields));
            if (direct.IsEmpty)
            {
                migrations = [];
                return new BoundaryApplyResult(false, [], []);
            }
            migrations = [];
            var restorations = BuildRestorationPlan(store, targetDataPath, direct);
            return JsonFileInstanceStore.ApplyPublishBoundary(
                targetDataPath, direct, headDesc,
                new BoundaryMarker(designId, headCommitId, BaseCommitId: stampedCommitId), dryRun, restorations);
        }

        var chainIds = firstParent.ToHashSet();
        var stampedAncestors = DagAncestors(store, stampedCommitId);
        var rangeIds = headAncestors.Where(id => !stampedAncestors.Contains(id)).ToList();
        if (rangeIds.Any(id => !chainIds.Contains(id) && HasMigrationText(store, id)))
            throw new InvalidOperationException("publish range contains a merged migration — not supported yet");

        var steps = firstParent.AsEnumerable().Reverse()
            .Where(id => id != stampedCommitId && HasMigrationText(store, id))
            .ToList();
        if (steps.Count == 0)
        {
            var direct = DesignDiffer.Compute(Snapshot(stampedFields), Snapshot(headFields));
            if (direct.IsEmpty)
            {
                migrations = [];
                return new BoundaryApplyResult(false, [], []);
            }
            migrations = [];
            var restorations = BuildRestorationPlan(store, targetDataPath, direct);
            return JsonFileInstanceStore.ApplyPublishBoundary(
                targetDataPath, direct, headDesc,
                new BoundaryMarker(designId, headCommitId, BaseCommitId: stampedCommitId), dryRun, restorations);
        }

        var doc = JsonFileInstanceStore.LoadRaw(targetDataPath);
        var startVersion = doc.Version;
        var writes = new List<LogWrite>();
        var unconvertible = new List<string>();
        var unsupported = new List<string>();
        var restored = new List<string>();
        var migrationReports = new List<MigrationRunReport>();
        var prevId = stampedCommitId;
        var prevFields = stampedFields;

        foreach (var stepId in steps)
        {
            var stepFields = FindCommitOrThrow(store, stepId);
            var oldDb = JsonFileInstanceStore.CloneDb(doc);
            var prevDesc = InstanceDescriptionLoader.Load(KernelHostActions.TextOf(prevFields, "text"));
            var stepDesc = InstanceDescriptionLoader.Load(KernelHostActions.TextOf(stepFields, "text"));
            var stepDiff = DesignDiffer.Compute(Snapshot(prevFields), Snapshot(stepFields));
            var transformed = JsonFileInstanceStore.TransformDb(doc, stepDiff, stepDesc, writes);
            unconvertible.AddRange(transformed.UnconvertibleCells);
            unsupported.AddRange(transformed.UnsupportedReshapes);
            migrationReports.Add(MigrationRunner.Run(
                KernelHostActions.TextOf(stepFields, "migration"), stepId,
                KernelHostActions.TextOf(stepFields, "message"), oldDb, prevDesc, doc, stepDesc, writes));
            prevId = stepId;
            prevFields = stepFields;
        }

        var finalDiff = DesignDiffer.Compute(Snapshot(prevFields), Snapshot(headFields));
        var final = JsonFileInstanceStore.TransformDb(
            doc, finalDiff, headDesc, writes, BuildRestorationPlan(store, targetDataPath, finalDiff));
        unconvertible.AddRange(final.UnconvertibleCells);
        unsupported.AddRange(final.UnsupportedReshapes);
        restored.AddRange(final.RestoredCells ?? []);

        if (writes.Count > 0 && !dryRun)
            JsonFileInstanceStore.SaveBoundary(
                targetDataPath, doc, startVersion, writes,
                new BoundaryMarker(designId, headCommitId, BaseCommitId: stampedCommitId));

        migrations = migrationReports;
        return new BoundaryApplyResult(writes.Count > 0, unconvertible, unsupported, restored);
    }

    private static DesignSnapshot Snapshot(ObjectValue commitFields) =>
        new(KernelHostActions.TextOf(commitFields, "text"), KernelHostActions.IdMapOf(commitFields));

    private static RestorationPlan? BuildRestorationPlan(IInstanceStore store, string targetDataPath, DesignDiff diff)
    {
        if (diff.Adds.Count == 0 && diff.TypeAdds.Count == 0) return null;
        var wantedProps = diff.Adds.ToDictionary(a => a.PropId);
        var wantedTypes = diff.TypeAdds.ToDictionary(a => a.TypeId);
        var restoredProps = new Dictionary<int, IReadOnlyDictionary<int, StoredValue>>();
        var restoredTypes = new Dictionary<int, IReadOnlyList<StoredObject>>();
        foreach (var entry in JsonFileInstanceStore.LoadEntries(targetDataPath).Reverse())
        {
            if (wantedProps.Count == 0 && wantedTypes.Count == 0) break;
            if (entry.Boundary is not { } boundary) continue;
            if (boundary.BaseCommitId is not { } baseCommitId) break;

            var baseMap = KernelHostActions.IdMapOf(FindCommitOrThrow(store, baseCommitId));
            var commitMap = KernelHostActions.IdMapOf(FindCommitOrThrow(store, boundary.CommitId));
            var commitIds = commitMap.Values.ToHashSet();

            foreach (var (typeId, add) in wantedTypes.ToList())
            {
                var oldType = baseMap.FirstOrDefault(kv => kv.Value == typeId).Key;
                if (oldType is null || oldType.Contains('.') || commitIds.Contains(typeId)) continue;
                var objects = entry.Writes.OfType<Remove>()
                    .Where(w => w.Old.TypeName == oldType)
                    .Select(w => w.Old)
                    .ToList();
                if (objects.Count > 0) restoredTypes[typeId] = objects;
                wantedTypes.Remove(typeId);
            }

            foreach (var (propId, add) in wantedProps.ToList())
            {
                var oldPath = baseMap.FirstOrDefault(kv => kv.Value == propId).Key;
                if (oldPath is null || commitIds.Contains(propId)) continue;
                var dot = oldPath.IndexOf('.');
                if (dot < 0) continue;
                var oldProp = oldPath[(dot + 1)..];
                var values = entry.Writes.OfType<FieldWrite>()
                    .Where(w => w.Prop == oldProp && w.New is null && w.Old is not null)
                    .ToDictionary(w => w.ObjectId, w => w.Old!);
                if (values.Count > 0) restoredProps[propId] = values;
                wantedProps.Remove(propId);
            }
        }
        return restoredProps.Count == 0 && restoredTypes.Count == 0
            ? null
            : new RestorationPlan(restoredProps, restoredTypes);
    }

    private static string MigrationOf(IInstanceStore store, int commitId) =>
        KernelHostActions.TextOf(FindCommitOrThrow(store, commitId), "migration");

    private static bool HasMigrationText(IInstanceStore store, int commitId) =>
        !string.IsNullOrWhiteSpace(MigrationOf(store, commitId));

    private static ObjectValue FindCommitOrThrow(IInstanceStore store, int commitId) =>
        KernelHostActions.FindCommit(store, commitId)
        ?? throw new InvalidOperationException($"Commit {commitId} referenced by publish history no longer exists.");

    private static List<int>? FirstParentChainTo(IInstanceStore store, int headId, int stampedId)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        for (var id = headId; seen.Add(id);)
        {
            result.Add(id);
            if (id == stampedId) return result;
            var fields = FindCommitOrThrow(store, id);
            if (fields.Fields.GetValueOrDefault("parent") is not ReferenceValue { TargetId: { } parent })
                return null;
            id = parent;
        }
        return null;
    }

    private static HashSet<int> DagAncestors(IInstanceStore store, int headId)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(headId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            var fields = FindCommitOrThrow(store, id);
            if (fields.Fields.GetValueOrDefault("parent") is ReferenceValue { TargetId: { } p }) stack.Push(p);
            if (fields.Fields.GetValueOrDefault("mergeParent") is ReferenceValue { TargetId: { } mp }) stack.Push(mp);
        }
        return seen;
    }

    private static bool HasMigrationInDag(IInstanceStore store, int headId) =>
        DagAncestors(store, headId).Any(id => HasMigrationText(store, id));
}
