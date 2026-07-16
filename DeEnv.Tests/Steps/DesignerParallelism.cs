using TUnit.Core;
using TUnit.Core.Interfaces;

namespace DeEnv.Tests.Features;

// The operator-IDE scenarios are the suite's heaviest: each boots a REAL kernel hosting THREE
// instances (six GenHTTP hosts) plus a browser page. Letting them all run at TUnit's default
// parallelism spikes a typical box hard enough that WS autosaves (MetaProp name after "+ Field")
// never land within the store Eventually window — load-induced timeouts, not data races (ports are
// collision-free via PortAllocator). Cap concurrent heavy designer scenarios at 1: even 2 still
// starves MetaProp arrayAdd/propChange under load on a typical box (repro: Field-over-schema /
// stateful-config add-field hanging 120s while a sibling designer scenario runs).
//
// Class names MUST match the Reqnroll-generated partials, which derive from each Feature TITLE.
// If a title changes, regenerate the name here (else this becomes a dead orphan and the limiter
// silently stops applying).

public sealed class DesignerScenarioLimit : IParallelLimit
{
    public int Limit => 1;
}

[ParallelLimiter<DesignerScenarioLimit>]
public partial class TheOperatorIDEDesignsLibraryInstanceDesignSelectorFeature
{
}

[ParallelLimiter<DesignerScenarioLimit>]
public partial class Designer_LibraryAndNavigationFeature
{
}

[ParallelLimiter<DesignerScenarioLimit>]
public partial class Designer_TypesAndPropsEditorFeature
{
}

[ParallelLimiter<DesignerScenarioLimit>]
public partial class Designer_CommitPublishBranchesMergeFeature
{
}

[ParallelLimiter<DesignerScenarioLimit>]
public partial class Designer_ComponentsAndLivePreviewsFeature
{
}

[ParallelLimiter<DesignerScenarioLimit>]
public partial class Designer_StructuredRenderTreeAndCanvasFeature
{
}

[ParallelLimiter<DesignerScenarioLimit>]
public partial class Designer_RenderTreeAndCanvasFeature
{
}

// AgedStore.feature's designer scenario boots the SAME heavy shape (a real kernel hosting three
// instances), so it must share the operator-IDE cap.
[ParallelLimiter<DesignerScenarioLimit>]
public partial class AgedStoreReal_WorldDataShapesFreshSeedsNeverHoldFeature
{
}
