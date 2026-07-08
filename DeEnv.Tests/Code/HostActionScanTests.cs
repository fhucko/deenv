using DeEnv.Code;
using DeEnv.Instance;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// HostActionScan drives which instances get a REAL KernelHostActions seam wired: an app whose Code
// CALLS a `sys.<hostAction>(...)` builtin returns UsesHostActions true (the kernel then builds the
// seam), one that never does returns false (an unwired seam → a hostAction frame errors closed).
//
// This is the WIRING half of the two-part host-action safety model (the AUTHORITY half is the `sys`
// access rule, AccessFloor.CanHostAction). A newly added host-action builtin must be recognized here or
// the seam is never wired and its Code call silently no-ops — so each new builtin (M12 X2a's
// importRender) carries a scan-recognition guard.
public sealed class HostActionScanTests
{
    // A minimal designer-shaped app whose `ui` render carries a button whose onClick is the given call
    // expression — the same call-in-a-handler shape the kernel wiring fixtures use (KernelSteps'
    // DesignShapedCommitDesignNoRuleApp). The static AST scan walks the render tree (attributes included)
    // and never runs it, so a call site anywhere is enough.
    private static InstanceDescription AppWithOnClick(string callExpr) => AppParse.Parse(
        $$"""
        types
            Db
                designs set of Design
            Design
                label text

        ui
            fn render()
                return <button class="go" onClick={() => {{callExpr}}}>
                    "Go"
        """);

    [Test]
    public async Task ImportRender_call_is_recognized_as_a_host_action()
    {
        // sys.importRender(design) in the render → the app AST-wires to a REAL KernelHostActions seam.
        await Assert.That(HostActionScan.UsesHostActions(AppWithOnClick("sys.importRender(db.designs)"))).IsTrue();
    }

    [Test]
    public async Task A_render_with_no_host_action_is_not_recognized()
    {
        // A control: a `sys.` call that is NOT a host action (schema descriptor read) → no seam wired
        // (fails closed), proving the scan keys on the host-action builtin set, not on any `sys.` call.
        await Assert.That(HostActionScan.UsesHostActions(AppWithOnClick("sys.schema(\"Design\")"))).IsFalse();
    }
}
