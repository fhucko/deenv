using DeEnv.Designer;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Kernel;

// Which of the three publish legs a plan resolved to — so the caller (KernelHostActions.Publish) knows
// which side effects to perform, WITHOUT re-deciding the leg (the leg is decided once, in Compute).
//   • NoHead        — the design has zero commits: project the CURRENT working copy by name (WorkingDoc).
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
    PublishReport Report, PublishLeg Leg, string WorkingDoc, string HeadText, int HeadCommitId);

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
            var workingDoc = SchemaBridge.ProjectDesignDocument(design); // throws on an invalid design
            var noHeadReport = new PublishReport
            {
                Applied = !dryRun, DryRun = dryRun, BaseCommit = null, TargetCommit = 0,
                UncommittedDrift = false, Renames = [], Adds = [], Removes = [], Conversions = [],
                Cardinality = [], FallbackNameMatched = false,
            };
            return new PublishPlan(noHeadReport, PublishLeg.NoHead, workingDoc, "", 0);
        }

        var (headCommitId, headFields) = head.Value;
        var headText = KernelHostActions.TextOf(headFields, "text");
        var headIdMap = KernelHostActions.IdMapOf(headFields);
        var headSnapshot = new DesignSnapshot(headText, headIdMap);

        // Uncommitted working-copy drift: the design's LIVE state may have moved past its own head commit
        // (an edit made after the last commit) — Snapshot(workingCopy).Text != head.text. Reported, never
        // published: publish always deploys the committed head, never the working copy.
        var workingSnapshot = SchemaBridge.Snapshot(design); // throws on an invalid design
        var uncommittedDrift = workingSnapshot.Text != headText;

        var stampedFields = stampedCommitId is { } stamped ? KernelHostActions.FindCommit(store, stamped) : null;

        if (stampedFields is null)
        {
            // Unstamped (or a stamp naming a commit this store no longer has — defensive): the one-time
            // name-match fallback — the pre-slice-4 by-name apply (WriteDocument), carrying whatever a
            // by-name apply can, then stamping so the NEXT publish is identity-diffed and rename-safe.
            var fallbackReport = new PublishReport
            {
                Applied = !dryRun, DryRun = dryRun, BaseCommit = null, TargetCommit = headCommitId,
                UncommittedDrift = uncommittedDrift, Renames = [], Adds = [], Removes = [], Conversions = [],
                Cardinality = [], FallbackNameMatched = true,
            };
            return new PublishPlan(fallbackReport, PublishLeg.Fallback, "", headText, headCommitId);
        }

        // ── the versioned path: diff the STAMPED commit against the HEAD commit by identity ──
        var baseSnapshot = new DesignSnapshot(
            KernelHostActions.TextOf(stampedFields, "text"), KernelHostActions.IdMapOf(stampedFields));
        var diff = DesignDiffer.Compute(baseSnapshot, headSnapshot);
        var targetDesc = InstanceDescriptionLoader.Load(headText);

        // Compute the boundary plan EVEN ON A DRY RUN — one code path for both (ApplyPublishBoundary's own
        // `dryRun` flag skips its two disk-touching side effects, so a preview reports the SAME
        // unconvertible/unsupported cells a real publish would produce, never a second implementation of
        // the same conversion rules that could drift from the real one). On a REAL run this is where the
        // destructive boundary is actually materialized onto the target's data file (the apply); the caller
        // then writes the head app-document text + stamps + restarts.
        var boundaryResult = diff.IsEmpty
            ? new BoundaryApplyResult(false, [], [])
            : JsonFileInstanceStore.ApplyPublishBoundary(
                targetDataPath, diff, targetDesc,
                new BoundaryMarker(designId, headCommitId, BaseCommitId: stampedCommitId), dryRun);

        var report = KernelHostActions.BuildReport(
            diff, boundaryResult, applied: !dryRun, dryRun, stampedCommitId, headCommitId,
            uncommittedDrift, fallbackNameMatched: false);

        return new PublishPlan(report, PublishLeg.Versioned, "", headText, headCommitId);
    }
}
