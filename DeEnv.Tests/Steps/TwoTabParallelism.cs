using TUnit.Core;
using TUnit.Core.Interfaces;

namespace DeEnv.Tests.Features;

// A "two-tab" scenario (Concurrency.feature's ONE, DataConflict.feature's THREE — M13 slice 6) opens TWO
// browser pages SEQUENTIALLY (ctx.Page THEN ctx.Page2), each a SEPARATE await on
// SharedBrowser.NewPageAsync → PageGate.WaitAsync() (a SemaphoreSlim(4,4) — see SharedBrowser). That is a
// textbook HOLD-AND-WAIT: a scenario holds permit #1 (its own Page) while blocking on permit #2 (Page2), and
// does not release #1 until the WHOLE scenario ends (Hooks.TeardownAsync's [AfterScenario], which cannot run
// while the scenario body itself is still stuck awaiting #2).
//
// TUnit's default parallelism lets MORE THAN FOUR two-tab scenarios start concurrently across the run.  If
// four (or more) of them each grab their FIRST page at once, all 4 pool permits are consumed — and every one
// of those scenarios' SECOND NewPageAsync call blocks FOREVER: nobody holding a permit can make progress
// (each is itself waiting on a second permit), so the pool never drains. This is a genuine DEADLOCK, not a
// leak — proven by a from-scratch repro (a throwaway MaxConcurrentPages=1 build hangs the SAME way, at the
// SAME step, in under two minutes instead of 30 — see the M13 slice 6 root-cause commit) that fails BEFORE
// any conflict-resolution/production code runs (GivenS2Opens's NewPageAsync itself never returns), which
// rules out a client-reconcile bug in ws.ts/CommitBatch: the hang is purely in test-harness scheduling.
//
// Fix (this class, mirroring DesignerParallelism's DesignerScenarioLimit — the project's OWN established
// idiom for exactly this class of problem: bound the heavy sub-population, don't serialize or widen
// timeouts): cap CONCURRENT two-tab scenarios at 2. Two two-tab scenarios can hold at most 2 permits
// (their firsts) while waiting for 2 more (their seconds) — 4 permits total, which the pool has, so 2
// concurrent two-tab scenarios ALWAYS complete; a 3rd or 4th queues behind the limiter instead of racing
// into a 3-or-4-way hold-and-wait that can starve the whole pool. This bounds the heavy sub-population
// without a serial floor (single-page scenarios, and other two-tab scenarios up to the cap, still overlap).
//
// NOTE: these class names MUST match the Reqnroll-generated class, which derives from the Feature TITLE. If
// either title changes, regenerate the name here (else this becomes a dead orphan partial and the real
// tests silently lose the limiter — full parallelism returns, and so does the deadlock risk).
public sealed class TwoTabScenarioLimit : IParallelLimit
{
    public int Limit => 2;
}

[ParallelLimiter<TwoTabScenarioLimit>]
public partial class Optimistic_ConcurrencyAnti_ClobberBaseVersionFeature
{
}

[ParallelLimiter<TwoTabScenarioLimit>]
public partial class DataConflictsField_LevelOverlapDisjointAuto_MergeAndTheCoarseResolutionUIFeature
{
}
