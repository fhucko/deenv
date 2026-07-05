namespace DeEnv.Kernel;

using DeEnv.Code;

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
// Destructive items are unambiguous in shape — never folded into a generic "changes" list — so a report
// reader can flag them without inspecting text: a `Removes` entry (a dropped field), an unconvertible cell
// inside a `Conversions` entry (`Unconvertible` non-empty), and a `Cardinality` entry that could not be
// carried (`Unsupported` true, which ALWAYS means `Dropped` true — an un-carriable reshape drops the old
// value to the new shape's default so the instance still loads; the old value survives in the log).
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
    public IReadOnlyList<MigrationRunReport> Migrations { get; init; } = [];
    public bool MigrationsSkipped { get; init; }
}

public sealed record RenameReportItem(string From, string To);
public sealed record RemoveReportItem(string Path);
public sealed record ConversionReportItem(string Path, string From, string To, IReadOnlyList<string> Unconvertible);
// `Unsupported` = this slice cannot carry the reshape's data; `Dropped` = the old value was dropped to the
// new shape's default so the instance still loads (recoverable from the log). An unsupported reshape ALWAYS
// drops (they move together), but both are surfaced so a report reader never has to infer the destruction.
public sealed record CardinalityReportItem(string Path, string From, string To, bool Unsupported, bool Dropped);
