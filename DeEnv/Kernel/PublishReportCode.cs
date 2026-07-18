using DeEnv.Code;

namespace DeEnv.Kernel;

// Render a computed PublishReport as a Code value tree (M13 Track-B B3) — the object the design editor's
// Publish section reads (`.isEmpty`/`.removes`/`.conversions[].unconvertible`/…). Lives in the KERNEL, not
// SsrRenderer, because it bridges a Kernel type (PublishReport) to Code values, and the kernel is the one
// layer that references BOTH (Http → Kernel would be a cycle; the render layer already forbids it, which is
// exactly why the preview compute is a kernel-built delegate rather than SsrRenderer-built like B2's diff).
//
// It mirrors SsrRenderer.BuildCommitDiffReport's idioms EXACTLY (the same shipped-whole discipline): every
// node is marked Constant so ClientState ships the WHOLE tree (nested arrays + their items), never privacy-
// filtered to empty — the report is provably user-data-free structural metadata. Each object/array is minted
// with a DISTINCT transient (negative) id off context.LastId — like an evaluated object literal — so
// ClientState's identity-dedup ships each node once, not collapsing the tree onto a single shared id.
//
// The shape is a SUPERSET of the diff report: the publish-only apply signals ride along —
// applied/dryRun/uncommittedDrift/fallbackNameMatched, plus the destructive-cell DETAIL a dry run computes
// (a conversion's `unconvertible` cell list, a cardinality's `unsupported`/`dropped` bools) — these are the
// "data will be lost" signals the editor surfaces LOUDLY before an Apply. `isEmpty` is diff-empty AND no
// drift (nothing to publish AND nothing uncommitted to warn about) — computed here from the report, since
// PublishReport carries the op lists rather than a precomputed flag.
//
// `targetCommit` (the design's HEAD commit id this preview computed against — PublishReport.TargetCommit)
// and `targetVersion` (the M13 Track-B B3 ADDENDUM — the target instance's LIVE data-store version at
// preview time, passed in by the caller since it is not itself part of PublishReport: that record is
// store-agnostic w.r.t. the target's live version) TOGETHER are the preview→apply CONSISTENCY GUARD token:
// what the operator implicitly approves by previewing. The design editor's Apply button passes both straight
// back to `sys.publish(design, targetId, report.targetCommit, report.targetVersion)`, which rejects if
// either has moved since — closing the TOCTOU window between "operator saw this plan" and "server applies a
// plan" (see KernelHostActions.Publish's own guard-check doc).
public static class PublishReportCode
{
    public static IExecValue Build(PublishReport report, int targetVersion, ExecContext context)
    {
        // Local constructors mirroring BuildCommitDiffReport: mint a distinct negative id + stamp Constant.
        ExecText T(string v) => new() { Value = v };
        ExecBool B(bool v) => new() { Value = v };
        ExecInt I(int v) => new() { Value = v };
        IExecCollection Arr(IEnumerable<IExecValue> items)
        {
            var list = items.ToList();
            return new ExecList {
                Id = --context.LastId.Value, Constant = true,
                Items = [.. list.Select((v, i) => new ExecItem { Key = i, Value = v })],
            };
        }
        ExecObject Obj(params (string Name, IExecValue Value)[] props) =>
            new() { Id = --context.LastId.Value, Constant = true, Props = props.ToDictionary(p => p.Name, p => p.Value) };

        // renames: { from, to } — safe (a rename carries data). Paths are "Type" or "Type.prop", as built.
        var renames = Arr(report.Renames.Select(r => (IExecValue)Obj(("from", T(r.From)), ("to", T(r.To)))));
        // adds: bare path strings "Type.prop" — safe (a new field defaults).
        var adds = Arr(report.Adds.Select(a => (IExecValue)T(a)));
        // removes: bare path strings ("Type.prop" or "Type") — ALWAYS destructive (the field/type is dropped).
        var removes = Arr(report.Removes.Select(r => (IExecValue)T(r.Path)));
        // conversions: { path, from, to, unconvertible:[cell] } — a scalar retype; a NON-EMPTY `unconvertible`
        // list means those cells WILL be lost (defaulted), the loud "data will be lost" signal.
        var conversions = Arr(report.Conversions.Select(c => (IExecValue)Obj(
            ("path", T(c.Path)), ("from", T(c.From)), ("to", T(c.To)),
            ("unconvertible", Arr(c.Unconvertible.Select(cell => (IExecValue)T(cell)))))));
        // cardinality: { path, from, to, unsupported, dropped } — a single/set/dict reshape; unsupported ⇒
        // this slice cannot carry it and the old value is dropped to the new shape's default (both loud).
        var cardinality = Arr(report.Cardinality.Select(c => (IExecValue)Obj(
            ("path", T(c.Path)), ("from", T(c.From)), ("to", T(c.To)),
            ("unsupported", B(c.Unsupported)), ("dropped", B(c.Dropped)))));
        var migrations = Arr(report.Migrations.Select(m => (IExecValue)Obj(
            ("commitId", I(m.CommitId)), ("message", T(m.Message)),
            ("types", Arr(m.Types.Select(t => (IExecValue)T(t)))),
            ("objectsMigrated", I(m.ObjectsMigrated)))));
        var restorations = Arr(report.Restorations.Select(r => (IExecValue)T(r)));

        // isEmpty: nothing to publish (an empty diff, no migrations) AND no uncommitted drift to warn about.
        // Fallback is itself a real publish (a first stamp), so a fallback is NOT empty.
        var isEmpty = report.Renames.Count == 0 && report.Adds.Count == 0 && report.Removes.Count == 0
            && report.Conversions.Count == 0 && report.Cardinality.Count == 0
            && report.Migrations.Count == 0 && report.Restorations.Count == 0
            && !report.UncommittedDrift && !report.FallbackNameMatched;

        return Obj(
            ("isEmpty", B(isEmpty)),
            ("applied", B(report.Applied)),
            ("dryRun", B(report.DryRun)),
            ("uncommittedDrift", B(report.UncommittedDrift)),
            ("fallbackNameMatched", B(report.FallbackNameMatched)),
            ("migrationsSkipped", B(report.MigrationsSkipped)),
            ("targetCommit", I(report.TargetCommit)),
            ("targetVersion", I(targetVersion)),
            ("renames", renames),
            ("adds", adds),
            ("removes", removes),
            ("conversions", conversions),
            ("cardinality", cardinality),
            ("migrations", migrations),
            ("restorations", restorations));
    }
}
