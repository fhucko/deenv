using Microsoft.Playwright;
using Reqnroll;

namespace DeEnv.Tests.TestSupport;

[Binding]
public sealed class Hooks(InstanceContext ctx)
{
    [AfterScenario]
    public async Task TeardownAsync()
    {
        if (ctx.Page != null)    await ctx.Page.CloseAsync();
        if (ctx.Browser != null) await ctx.Browser.CloseAsync();
        ctx.Playwright?.Dispose();

        if (ctx.Server != null) await ctx.Server.DisposeAsync();

        // Kernel-host scenarios (milestone 10): stop every hosted instance, then drop the temp
        // directory holding the fixtures, registry, and derived data files.
        if (ctx.Kernel != null) await ctx.Kernel.DisposeAsync();
        try { if (ctx.KernelDir != null && Directory.Exists(ctx.KernelDir)) Directory.Delete(ctx.KernelDir, recursive: true); }
        catch { /* best-effort */ }

        try { if (File.Exists(ctx.DataFilePath)) File.Delete(ctx.DataFilePath); }
        catch { /* best-effort */ }
    }
}
