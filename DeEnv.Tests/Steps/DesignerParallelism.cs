using TUnit.Core;
using TUnit.Core.Interfaces;

namespace DeEnv.Tests.Features;

// The operator-IDE scenarios are the suite's heaviest: each boots a REAL kernel hosting THREE instances
// (six GenHTTP hosts) plus a browser page. The dozen of them used to be [NotInParallel] — run strictly
// one-at-a-time against the whole suite — which is a ~6.5s hard-serial floor on the run. They were
// serialized because letting them all loose spikes a 6-core box hard enough to tip the suite's tight
// fail-fast waits over (a load-induced timeout flake, not a data race; ports are collision-free via
// PortAllocator). The fix is TWO parts that have to go together:
//
//   1) This limiter BOUNDS the heavy scenarios to a few concurrent, so the load spike stays in check
//      (no oversubscription of the 6 cores) while still folding them into the parallel run — no serial
//      floor. 2 is the empirical sweet spot: it keeps concurrent publish/deploy round-trips low enough
//      that the kernel's fire-and-forget restart isn't starved under real machine load (3 occasionally
//      let a deploy poll time out during a heavy spike), while still removing the serial floor.
//   2) The fail-fast timeouts were raised from 5s to load-tolerant values (SharedBrowser page waits,
//      KernelSteps' HTTP probe, DesignerSteps' EventuallyAsync) — 5s sat BELOW the loaded worst-case once
//      these scenarios overlap the pool, so it produced false-timeout flakes. The new values sit above it.
//
// NOTE: this class name MUST match the Reqnroll-generated class, which derives from the Feature TITLE in
// Designer.feature. If that title changes, regenerate the name (else this becomes a dead orphan partial and
// the real tests silently lose the limiter — they then run fully parallel and the load flake returns).
public sealed class DesignerScenarioLimit : IParallelLimit
{
    public int Limit => 2;
}

[ParallelLimiter<DesignerScenarioLimit>]
public partial class TheOperatorIDEDesignsLibraryInstanceDesignSelectorFeature
{
}

// AgedStore.feature's designer scenario boots the SAME heavy shape (a real kernel hosting three
// instances), so it must share the operator-IDE cap — without this partial it would escape the
// limiter and let a third heavy kernel overlap the two Designer.feature allows. Same generated-name
// caveat as above: derived from the feature TITLE in AgedStore.feature.
[ParallelLimiter<DesignerScenarioLimit>]
public partial class AgedStoreReal_WorldDataShapesFreshSeedsNeverHoldFeature
{
}
