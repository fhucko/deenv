namespace DeEnv.Tests.Features;

// The operator-IDE scenarios are the suite's heaviest: each boots a REAL kernel hosting THREE
// instances (the designer + two targets = six GenHTTP hosts) plus a Playwright browser. Run amid the
// suite's default test parallelism, three of those at once starve the thread pool (GenHTTP host start
// blocks a thread), which both slows them to minutes and times out the publish round-trip. Marking the
// generated feature class [NotInParallel] serializes them against the rest of the suite, so each runs
// with the machine to itself — fast and deterministic. (A partial declaration of the Reqnroll-generated
// class; the attribute applies to its discovered tests.)
[TUnit.Core.NotInParallel]
public partial class TheOperatorIDERoutedMulti_InstanceDesignerFeature
{
}
