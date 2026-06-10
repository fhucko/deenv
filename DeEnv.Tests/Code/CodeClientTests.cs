using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Microsoft.Playwright;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Stage 3 client-runtime tests: the code-owned UI is served, hydrated by the new
// TypeScript client (codeExec + dt + ui + init), and driven through a real browser.
// Covers two-way binding, dependent re-render, checkbox toggle, identity-keyed
// reconciliation (focus survives an orderBy reorder), and local transient construction.
public sealed class CodeClientTests
{
    // ── the tasks UI (two-way text + checkbox + dependent where list) ───────────────

    [Test]
    public async Task Client_hydrates_and_renders_the_list()
    {
        await WithPageAsync(InstanceContext.TasksUiDb(), SeedTasks, async page =>
        {
            var titles = page.Locator("#all input[type='text']");
            await Assert.That(await titles.CountAsync()).IsEqualTo(3);
            await Assert.That(await titles.Nth(0).InputValueAsync()).IsEqualTo("Alpha"); // ordered by priority
        });
    }

    [Test]
    public async Task Typing_into_a_bound_input_updates_dependent_ui()
    {
        await WithPageAsync(InstanceContext.TasksUiDb(), SeedTasks, async page =>
        {
            // Beta (All row 1, ordered by priority) also appears in the open list.
            await page.Locator("#all input[type='text']").Nth(1).FillAsync("BetaX");
            await Assert.That(await page.Locator(".open-title").AllInnerTextsAsync()).Contains("BetaX");
        });
    }

    [Test]
    public async Task Toggling_a_checkbox_filters_the_dependent_list()
    {
        await WithPageAsync(InstanceContext.TasksUiDb(), SeedTasks, async page =>
        {
            var open = page.Locator(".open-title");
            await Assert.That(await open.AllInnerTextsAsync()).Contains("Beta");

            await page.Locator("#all input[type='checkbox']").Nth(1).CheckAsync(); // Beta → done
            var after = await open.AllInnerTextsAsync();
            await Assert.That(after).DoesNotContain("Beta");
            await Assert.That(after).Contains("Gamma");
        });
    }

    // ── the interactive UI (identity-keyed reorder + transient add) ─────────────────

    [Test]
    public async Task Reordering_via_orderBy_keeps_focus_on_the_moved_row()
    {
        await WithPageAsync(InstanceContext.InteractiveUiDb(), s => { SeedItem(s, "b"); SeedItem(s, "a"); }, async page =>
        {
            var names = page.Locator("input.name");
            await Assert.That(await names.Nth(0).InputValueAsync()).IsEqualTo("a"); // ordered by name

            // Rename "a" → "z": the list reorders (b, z). With identity-keyed
            // reconciliation the focused input moves with its object and keeps focus.
            await names.Nth(0).FocusAsync();
            await names.Nth(0).FillAsync("z");

            await Assert.That(await page.Locator("input.name").Nth(0).InputValueAsync()).IsEqualTo("b");
            await Assert.That(await page.Locator("input.name").Nth(1).InputValueAsync()).IsEqualTo("z");
            var focused = await page.EvaluateAsync<string>(
                "() => document.activeElement instanceof HTMLInputElement ? document.activeElement.value : ''");
            await Assert.That(focused).IsEqualTo("z");
        });
    }

    [Test]
    public async Task A_transient_object_is_built_and_added_locally()
    {
        await WithPageAsync(InstanceContext.InteractiveUiDb(), s => { SeedItem(s, "b"); SeedItem(s, "a"); }, async page =>
        {
            await page.Locator("input.new-name").FillAsync("c");
            await page.Locator("button.add").ClickAsync();

            var names = page.Locator("input.name");
            await Assert.That(await names.CountAsync()).IsEqualTo(3);
            await Assert.That(await names.Nth(2).InputValueAsync()).IsEqualTo("c"); // ordered a, b, c
            await Assert.That(await page.Locator("input.new-name").InputValueAsync()).IsEqualTo("");
        });
    }

    // ── harness ─────────────────────────────────────────────────────────────────────

    private static async Task WithPageAsync(InstanceDescription desc, Action<IInstanceStore> seed, Func<IPage, Task> body)
    {
        var dataPath = Path.GetTempFileName();
        await using var server = new TestInstanceServer();
        await server.StartAsync(desc, dataPath);
        seed(server.Store!);

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { BaseURL = server.BaseUrl });
        var logs = new List<string>();
        page.Console += (_, m) => logs.Add($"[{m.Type}] {m.Text}");
        page.PageError += (_, e) => logs.Add($"[pageerror] {e}");
        await page.GotoAsync("/");
        try
        {
            await page.WaitForSelectorAsync("[data-key]"); // wait for the client to hydrate
        }
        catch (TimeoutException)
        {
            var content = await page.ContentAsync();
            throw new Exception("Hydration failed. Console:\n" + string.Join("\n", logs) +
                "\n--- content (first 1500) ---\n" + content[..Math.Min(1500, content.Length)]);
        }

        try { await body(page); }
        finally { try { File.Delete(dataPath); } catch { /* best-effort */ } }
    }

    private static void SeedTasks(IInstanceStore store)
    {
        SeedTask(store, "Beta", done: false, priority: 2);
        SeedTask(store, "Alpha", done: true, priority: 1);
        SeedTask(store, "Gamma", done: false, priority: 3);
    }

    private static void SeedTask(IInstanceStore store, string title, bool done, int priority)
    {
        var id = store.CreateObject("Task", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["title"] = new TextValue(title),
            ["done"] = new BoolValue(done),
            ["priority"] = new IntValue(priority),
        }));
        store.AddToSet(NodePath.Root.Field("tasks"), id);
    }

    private static void SeedItem(IInstanceStore store, string name)
    {
        var id = store.CreateObject("Item", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name),
        }));
        store.AddToSet(NodePath.Root.Field("items"), id);
    }
}
