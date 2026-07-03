namespace DeEnv.Kernel;

// The structured report the `mergeBranch` host action returns (M13 slice 5) — rides the SAME
// hostAction `Report` field PublishReport already widened (IHostActions.Run → object?; no new wire
// shape). Camelcase via the shared serializer options, no per-property attributes.
//
// `Merged`/`NoOp`: a clean merge sets `Merged: true` and `MergeCommit` names the new two-parent commit.
// `NoOp: true` means the source is already fully contained in the target (base == source's head) — no
// merge commit is created, nothing changes. Neither set (both false) means the merge did NOT apply —
// either a drift refusal (`DriftRefusal` names which side) or unresolved conflicts (`Conflicts`
// non-empty); NO writes happened in either case.
//
// `BaseCommit`/`SourceCommit`/`TargetCommit`: the three-way merge's endpoints — the max-logSeq common
// ancestor and the two heads being merged (BaseCommit is 0 on a drift refusal, where the LCA was never
// computed — refused before it mattered).
//
// `Conflicts`: one item per still-unresolved conflict (kind = meta|existence|fn|initialData|access), each
// carrying its stable `Id` (the SAME string a `resolutions` entry names to resolve it on a re-run) plus
// the three raw values so an operator can read what each side did. Empty on a clean merge OR a no-op.
//
// `AccessChanges`: EVERY access-rule difference the merge introduces relative to the target — populated
// even on a perfectly clean merge (settled: access changes are always surfaced, never silently folded in
// without a trace).
//
// `DriftRefusal`: "source" or "target" — set only when that side's WORKING COPY has uncommitted edits
// past its own branch head (merge computes over committed heads only); null otherwise.
public sealed record MergeReport
{
    public required bool Merged { get; init; }
    public required bool NoOp { get; init; }
    public required string SourceBranch { get; init; }
    public required string TargetBranch { get; init; }
    public required int BaseCommit { get; init; }
    public required int SourceCommit { get; init; }
    public required int TargetCommit { get; init; }
    public int? MergeCommit { get; init; }
    public required IReadOnlyList<ConflictItem> Conflicts { get; init; }
    public required IReadOnlyList<AccessChangeReportItem> AccessChanges { get; init; }
    public string? DriftRefusal { get; init; }
}

// One still-unresolved conflict. `Id` is the stable string a `resolutions` entry names
// (`{ id, take: "source"|"target" }`) to pick a side on a re-run.
public sealed record ConflictItem(string Id, string Kind, string Path, string? Field, string? Base, string Source, string Target);

// One access-rule difference the merge introduces, relative to the target (see MergeReport.AccessChanges).
public sealed record AccessChangeReportItem(string RuleKey, string Change, string Condition);
