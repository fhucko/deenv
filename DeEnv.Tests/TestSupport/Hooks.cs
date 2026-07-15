using DeEnv.Storage;
using Microsoft.Playwright;
using Reqnroll;

namespace DeEnv.Tests.TestSupport;

[Binding]
public sealed class Hooks(InstanceContext ctx)
{
    [AfterScenario]
    public async Task TeardownAsync()
    {
        // Close the scenario's browser context(s) (and their pages with them) — the browser + driver are
        // shared across the run and torn down once at the end (see SharedBrowser). Page2 (a SECOND real
        // browser session — Concurrency.feature / DataConflict.feature's two-tab scenarios) MUST be closed
        // here too, same as Page. Leaving Page2 unclosed leaked its context for the REST of the process's
        // life (a genuine bug on its own — found alongside, and fixed alongside, the M13 slice 6 root-cause
        // below). Context cleanup is still required for proper resource release.
        if (ctx.Page != null) await ctx.Page.Context.CloseAsync();
        if (ctx.Page2 != null) await ctx.Page2.Context.CloseAsync();

        if (ctx.Server != null) await ctx.Server.DisposeAsync();

        // Kernel-host scenarios (milestone 10): stop every hosted instance, then drop the temp
        // directory holding the fixtures, registry, and derived data files.
        if (ctx.Kernel != null) await ctx.Kernel.DisposeAsync();
        try { if (ctx.KernelDir != null && Directory.Exists(ctx.KernelDir)) Directory.Delete(ctx.KernelDir, recursive: true); }
        catch { /* best-effort */ }

        // The M13 append-only log + genesis snapshot ride BESIDE the data file (AppPaths.LogPathForDataPath
        // / GenesisPathForDataPath) — a scenario that opened a store over DataFilePath may have created
        // them, and they must be cleaned up WITH it. Without this, Path.GetTempFileName()'s limited name
        // space can eventually recycle a deleted scenario's ".tmp" name for an unrelated later scenario,
        // which would then find that earlier scenario's STALE log/genesis still on disk beside its own
        // fresh (different-content) data file — a real version mismatch the store correctly refuses to
        // silently trust (JsonFileInstanceStore.ReconcileLogOnBoot's "AHEAD" guard), surfacing as a
        // flaky, seemingly-unrelated test failure. Deleting all three together closes that gap at the root.
        try { if (File.Exists(ctx.DataFilePath)) File.Delete(ctx.DataFilePath); }
        catch { /* best-effort */ }
        try
        {
            var logPath = AppPaths.LogPathForDataPath(ctx.DataFilePath);
            var genesisPath = AppPaths.GenesisPathForDataPath(ctx.DataFilePath);
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(genesisPath)) File.Delete(genesisPath);
        }
        catch { /* best-effort */ }
    }
}
