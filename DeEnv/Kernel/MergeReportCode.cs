using DeEnv.Code;

namespace DeEnv.Kernel;

// Render a computed MergeReport as a Code value tree (M13 Track-B B4) — the object the design editor's merge
// preview reads (`.merged`/`.noOp`/`.driftRefusal`/`.conflicts[].base`/`.accessChanges[].ruleKey`/…). The
// read-side sibling of PublishReportCode: `sys.mergePreview(source, target)` computes a MergeReport with NO
// write (the shared ComputeMergePlan, minus the apply) and ships it via the memo cache, exactly like
// sys.publishPreview ships a PublishReport.
//
// Lives in the KERNEL next to MergeReport, not SsrRenderer, so the Http→Kernel type cycle the render layer
// forbids never forms (SsrRenderer's self-built preview delegate CALLS this — it does not re-implement it).
// It mirrors PublishReportCode / SsrRenderer.BuildCommitDiffReport idioms EXACTLY (the same shipped-whole
// discipline): every node is marked Constant so ClientState ships the WHOLE tree (nested arrays + their
// items), never privacy-filtered to empty — a merge report is provably user-data-free structural metadata
// (type/prop/fn/rule names + old/new values, no row data). Each object/array is minted with a DISTINCT
// transient (negative) id off context.LastId — like an evaluated object literal — so ClientState's
// identity-dedup ships each node once, not collapsing the tree onto a single shared id.
//
// `Base` on a conflict may be null (an add-vs-add existence conflict has no base value); it maps to ExecNull
// so the Code reads `conflict.base == null` cleanly. The `MergeCommit` is null until a clean APPLY lands, so
// the preview always ships it as null (the preview never applies) — the editor keys "merged" off `.merged`,
// not the commit id.
public static class MergeReportCode
{
    public static IExecValue Build(MergeReport report, ExecContext context)
    {
        // Local constructors mirroring PublishReportCode: mint a distinct negative id + stamp Constant.
        ExecText T(string v) => new() { Value = v };
        ExecBool B(bool v) => new() { Value = v };
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
        // A conflict's `base` may be null (no common ancestor value); ship it as ExecNull so `== null` reads clean.
        IExecValue TextOrNull(string? v) => v is null ? new ExecNull() : T(v);

        // conflicts: { id, kind, path, field, base, source, target }. `id` is the stable string the operator's
        // take-source/take-target pick names back on the Apply (mergeBranch's resolutions arg).
        var conflicts = Arr(report.Conflicts.Select(c => (IExecValue)Obj(
            ("id", T(c.Id)), ("kind", T(c.Kind)), ("path", T(c.Path)),
            ("field", TextOrNull(c.Field)),
            ("base", TextOrNull(c.Base)), ("source", T(c.Source)), ("target", T(c.Target)))));
        // accessChanges: { ruleKey, change, condition } — ALWAYS surfaced (never silently folded in), so this
        // ships even on a perfectly clean merge; the editor renders it as its must-see security block.
        var accessChanges = Arr(report.AccessChanges.Select(a => (IExecValue)Obj(
            ("ruleKey", T(a.RuleKey)), ("change", T(a.Change)), ("condition", T(a.Condition)))));

        // driftRefusal is "source"/"target" or null (no drift) — the editor shows "commit <side> first".
        var driftRefusal = report.DriftRefusal is null ? (IExecValue)new ExecNull() : T(report.DriftRefusal);

        return Obj(
            ("merged", B(report.Merged)),
            ("noOp", B(report.NoOp)),
            ("sourceBranch", T(report.SourceBranch)),
            ("targetBranch", T(report.TargetBranch)),
            ("driftRefusal", driftRefusal),
            ("conflicts", conflicts),
            ("accessChanges", accessChanges));
    }
}
