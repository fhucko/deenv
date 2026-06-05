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

        try { if (File.Exists(ctx.DataFilePath)) File.Delete(ctx.DataFilePath); }
        catch { /* best-effort */ }
    }
}
