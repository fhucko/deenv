using TUnit.Core;
using TUnit.Core.Interfaces;

// Cap concurrent tests assembly-wide. Unbounded TUnit default piles many kernels + Playwright
// pages onto shared I/O queues and stretches multi-hop waits (wall-clock and flaky timeouts).
// Bench on Access+Designer+AgedStore (72 tests): maxParallel 3 beat unlimited; pool size matched
// to 3 was best, 6/6 was slower — so the concurrency knob alone is enough (single shared browser).
[assembly: ParallelLimiter<DeEnv.Tests.TestSupport.GlobalParallelLimit>]

namespace DeEnv.Tests.TestSupport;

public sealed class GlobalParallelLimit : IParallelLimit
{
    public int Limit => 3;
}
