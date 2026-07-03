namespace DeEnv.Kernel;

// The structured report the `publish` host action returns (M13 slice 4) — the ONE approved wire widening
// (IHostActions.Run → object?, carried onto the hostAction reply's `report` field). Camelcase via the
// shared serializer options (WsHandler._jsonOpts), no per-property attributes — matches the project's
// plain-data serialization convention.
//
// `Applied`/`DryRun`: a dry-run computes the SAME report and changes NOTHING (no file write, no log
// entry, no stamp, no restart) — `Applied` is false only when the diff was genuinely empty (nothing to
// carry) OR this was a dry run; a real publish that actually wrote a boundary entry reports `Applied:true,
// DryRun:false`.
//
// `BaseCommit`/`TargetCommit`: the identity-diff's two endpoints — the instance's STAMPED commit id (null
// when unstamped — the fallback path ran instead) and the design's HEAD commit id this publish targets.
//
// `UncommittedDrift`: the design's WORKING COPY differs from its head commit's cached text (an edit made
// after the last commit) — reported, never published; publish always deploys the committed HEAD, never
// the working copy (design doc §4).
//
// `Renames`/`Adds`/`Removes`/`Conversions`/`Cardinality`: the identity diff's ops, one list entry per op.
// Destructive items (`Removes`, an unconvertible cell inside `Conversions`) are unambiguous in shape —
// never folded into a generic "changes" list — so a report reader can flag them without inspecting text.
//
// `FallbackNameMatched`: true when the target had no prior stamp (a pre-versioning instance) and this
// publish ran the EXISTING by-name apply (SchemaBridge.WriteDocument) instead of the identity diff — the
// one-time fallback the design doc names; the NEXT publish (once stamped) is rename-safe.
public sealed record PublishReport
{
    public required bool Applied { get; init; }
    public required bool DryRun { get; init; }
    public int? BaseCommit { get; init; }
    public required int TargetCommit { get; init; }
    public required bool UncommittedDrift { get; init; }
    public required IReadOnlyList<RenameReportItem> Renames { get; init; }
    public required IReadOnlyList<string> Adds { get; init; }
    public required IReadOnlyList<RemoveReportItem> Removes { get; init; }
    public required IReadOnlyList<ConversionReportItem> Conversions { get; init; }
    public required IReadOnlyList<CardinalityReportItem> Cardinality { get; init; }
    public required bool FallbackNameMatched { get; init; }
}

public sealed record RenameReportItem(string From, string To);
public sealed record RemoveReportItem(string Path);
public sealed record ConversionReportItem(string Path, string From, string To, IReadOnlyList<string> Unconvertible);
public sealed record CardinalityReportItem(string Path, string From, string To, bool Unsupported);
