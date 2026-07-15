namespace DeEnv.Tests.Features;

// IParallelLimit removed (per request). No page concurrency limit (PageGate removed).
// Two-tab scenarios use TUnit default parallelism. Historical deadlock analysis on limited permits
// is in git history / Hooks.cs comments.

public partial class Optimistic_ConcurrencyAnti_ClobberBaseVersionFeature
{
}

public partial class DataConflictsField_LevelOverlapDisjointAuto_MergeAndTheCoarseResolutionUIFeature
{
}
