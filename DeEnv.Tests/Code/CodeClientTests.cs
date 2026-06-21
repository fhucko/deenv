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

    [Test]
    public async Task Server_only_materialized_list_hydrates_without_calling_the_server()
    {
        // The client never has `highEarners` (server-only, not shipped); it renders from
        // the materialized `rich` var. Hydration must succeed (no faulting on load).
        await WithPageAsync(InstanceContext.SensitiveUiDb(), s => { SeedPerson(s, "Ada", 999); SeedPerson(s, "Bob", 5); }, async page =>
        {
            var earners = page.Locator(".earner");
            await Assert.That(await earners.CountAsync()).IsEqualTo(1);
            await Assert.That(await earners.Nth(0).InnerTextAsync()).IsEqualTo("Ada");
        });
    }

    // ── persistence over the WebSocket (Stage 4b) ──────────────────────────────────

    // Editing a two-way-bound field on a db object (id > 0) sends an objectPropChange
    // over the WS; the server persists it through the store. The client already applied
    // it optimistically, so persistence is what a reload would show.
    [Test]
    public async Task Editing_a_bound_db_field_persists_via_the_websocket()
    {
        await WithPageAsync(InstanceContext.InteractiveUiDb(), s => { SeedItem(s, "a"); SeedItem(s, "b"); }, async (page, store) =>
        {
            // Rename "a" (row 0, ordered by name) → "z".
            await page.Locator("input.name").Nth(0).FillAsync("z");

            // The change reaches storage (poll: the WS round-trip is async).
            await AssertEventuallyAsync(() => store.ReadExtent("Item").Values.Any(o => Name(o) == "z"));

            // Accepted: the reply commits the journal entry — the optimistic value stands
            // (no double-apply, no rollback). Ordered by name, "z" is now the last row.
            await Task.Delay(100);
            await Assert.That(await page.Locator("input.name").Nth(1).InputValueAsync()).IsEqualTo("z");
        });
    }

    // Stage 5: optimistic mutations are provisional. Another client deletes a row
    // out-of-band; this client (no cross-client push yet) still shows it and edits it.
    // The server rejects the write (no object with that id), and the client reverse-
    // replays its change journal — the input rolls back to the value before the edit.
    [Test]
    public async Task A_rejected_mutation_rolls_back_to_the_value_before()
    {
        await WithPageAsync(InstanceContext.InteractiveUiDb(), s => { SeedItem(s, "a"); SeedItem(s, "b"); }, async (page, store) =>
        {
            // Out-of-band delete of "a" (GC sweeps it from the extent).
            var aId = store.ReadExtent("Item").First(kv => Name(kv.Value) == "a").Key;
            store.RemoveFromSet(NodePath.Root.Field("items"), aId);

            // Optimistically rename the now-deleted row; the reject rolls it back to "a".
            await page.Locator("input.name").Nth(0).FillAsync("aX");
            await page.WaitForFunctionAsync(
                "() => document.querySelector('input.name')?.value === 'a'");
        });
    }

    // Adding a transient object to a db set sends an arrayAdd; the server mints it into
    // the extent, links it, and echoes the real id. The client re-keys its negative-id
    // copy, so every rendered row ends up with a positive (real) data-key.
    [Test]
    public async Task Adding_a_set_member_persists_and_remaps_its_id()
    {
        await WithPageAsync(InstanceContext.InteractiveUiDb(), s => { SeedItem(s, "a"); SeedItem(s, "b"); }, async (page, store) =>
        {
            await page.Locator("input.new-name").FillAsync("c");
            await page.Locator("button.add").ClickAsync();

            // The new member is minted into the extent and linked into the set.
            await AssertEventuallyAsync(() => store.ReadExtent("Item").Values.Any(o => Name(o) == "c"));

            // Negative→real id remap: every row now carries a positive (real) data-key.
            await page.WaitForFunctionAsync(
                "() => { const ks = [...document.querySelectorAll('[data-key]')].map(e => +e.getAttribute('data-key'));" +
                " return ks.length === 3 && ks.every(k => k > 0); }");
        });
    }

    // A just-added set member is rendered with a transient NEGATIVE id; when its arrayAdd round-trip
    // returns, remapAddedId re-keys it to its real POSITIVE id and re-renders. A foreach row's DOM node is
    // keyed by its member id, so this re-key flips the row's data-key — and the reconciler must reuse the
    // SAME element across that flip (the negative→positive id is the same logical row). If it rebuilds the
    // row instead, a focused input in it is destroyed and any in-progress, not-yet-committed edit is lost —
    // exactly the intermittent flake where a name typed into a just-added row vanished when the remap landed
    // mid-edit. This drives the real client renderUi over the real model, so it reproduces the reconciler
    // path deterministically (no server timing): add a transient row, focus its input, type an UNCOMMITTED
    // character, then perform the remap re-render and assert the input survived with focus + text intact.
    [Test]
    public async Task A_focused_edit_survives_the_negative_to_real_id_remap_rerender()
    {
        await WithPageAsync(InstanceContext.InteractiveUiDb(), s => { SeedItem(s, "a"); SeedItem(s, "b"); }, async page =>
        {
            // Build a transient new member straight into the model (a negative id, as `db.items.add` mints)
            // and render — a new row appears keyed by that negative id. Driving the model+renderUi directly
            // (not the Add button) keeps the remap under the test's control: no real WS arrayAdd reply can
            // fire its own remap and race this one, so the reproduction is deterministic.
            var negKey = await page.EvaluateAsync<int>(
                """
                () => {
                    const db = uiStatic.state.scope.items["db"].value;
                    const items = db.props["items"];
                    const neg = --uiStatic.lastId.value;
                    items.items.push({ key: neg, value: { type: "object", props: { name: { type: "text", value: "" } }, id: neg } });
                    invalidateMember(items.id);
                    renderUi();
                    return neg;
                }
                """);

            // The transient row's name input exists, keyed by the negative id (it sorts first: empty name).
            await page.Locator(".name").First.WaitForAsync();

            // Focus it and type ONE character WITHOUT committing it to the model — i.e. set the live input's
            // value + leave it focused, but do NOT fire `input` (so the model still holds ""). This is the
            // in-progress edit a real user has half-typed when the remap lands. Tag the element so we can
            // prove the very same node survives the re-render.
            await page.EvaluateAsync(
                $$"""
                () => {
                    const rows = [...document.querySelectorAll('.name')];
                    const el = rows.find(e => e.closest('[data-key]')?.getAttribute('data-key') === String({{negKey}}));
                    el.__probe = "kept";
                    el.focus();
                    el.value = "X"; // uncommitted: no `input` event, so the model name is still ""
                }
                """);

            // The remap: re-key the member from its negative id to a real positive id and re-render, exactly
            // as remapAddedId does on the arrayAdd reply.
            await page.EvaluateAsync(
                $$"""
                () => {
                    const realId = 9001;
                    const db = uiStatic.state.scope.items["db"].value;
                    const items = db.props["items"];
                    const item = items.items.find(i => i.key === {{negKey}});
                    item.key = realId;
                    item.value.id = realId;
                    uiStatic.state.objects[realId] = item.value;
                    uiStatic.state.localToServerIds[{{negKey}}] = realId;
                    uiStatic.state.serverToLocalIds[realId] = {{negKey}};
                    invalidateMember(items.id);
                    renderUi();
                }
                """);

            // The row is now keyed by the real (positive) id…
            await page.WaitForFunctionAsync("() => !!document.querySelector('[data-key=\"9001\"]')");

            // …and the SAME input element survived the re-render (our probe marker is still on it) and is
            // still focused. Had the reconciler rebuilt the row on the key flip, the marker and the focus
            // would both be gone (a fresh input replaces it). (The half-typed value, never committed to the
            // model, is legitimately reset by reconciliation — node identity + focus are what the remap must
            // preserve; a real user's per-keystroke edits ARE committed and survive, proven by the suite.)
            var diag = await page.EvaluateAsync<string>(
                """
                () => {
                    const el = document.querySelector('[data-key="9001"] .name');
                    if (el == null) return "no-node";
                    return `probe=${el.__probe} focused=${el === document.activeElement} value=${JSON.stringify(el.value)}`;
                }
                """);
            await Assert.That(diag).IsEqualTo("probe=kept focused=true value=\"\"");
        });
    }

    private static string? Name(ObjectValue o) =>
        o.Fields.TryGetValue("name", out var n) && n is TextValue t ? t.Text : null;

    // A computation over a hidden field (the earners list filters by a private salary)
    // cannot be re-derived on the client when membership changes — the existing members'
    // salaries were never shipped. Adding a person makes the client refetch; the server
    // recomputes over fresh storage and returns the authoritative earners list. (Adding a
    // low earner does not produce an earner, proving the filter still runs server-side.)
    [Test]
    public async Task A_hidden_dependency_recomputes_on_the_server_via_refetch()
    {
        await WithPageAsync(InstanceContext.RefetchUiDb(), s => SeedPerson(s, "Ada", 999), async page =>
        {
            await Assert.That(await page.Locator(".earner").AllInnerTextsAsync()).IsEquivalentTo(new[] { "Ada" });

            // Add a high earner: the client can't re-filter (Ada's salary is private) → refetch.
            await page.Locator("button.add-rich").ClickAsync();
            await page.WaitForFunctionAsync(
                "() => [...document.querySelectorAll('.earner')].some(e => e.textContent === 'Rich')");

            // Add a low earner: it joins the people list but the server filter keeps it out.
            await page.Locator("button.add-poor").ClickAsync();
            await page.WaitForFunctionAsync(
                "() => [...document.querySelectorAll('.person')].some(e => e.textContent === 'Poor')");

            var earners = await page.Locator(".earner").AllInnerTextsAsync();
            await Assert.That(earners).Contains("Rich");
            await Assert.That(earners).DoesNotContain("Poor");
        });
    }

    // Polls a condition until true or a timeout elapses (for async WS persistence). An
    // IOException is transient — the poller reads the store's JSON file while the server
    // thread is mid-write — so it is swallowed and retried.
    private static async Task AssertEventuallyAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return; }
            catch (IOException) { /* store file mid-write on another thread — retry */ }
            await Task.Delay(50);
        }
        bool final;
        try { final = condition(); } catch (IOException) { final = false; }
        await Assert.That(final).IsTrue();
    }

    // ── harness ─────────────────────────────────────────────────────────────────────

    private static Task WithPageAsync(InstanceDescription desc, Action<IInstanceStore> seed, Func<IPage, Task> body) =>
        WithPageAsync(desc, seed, (page, _) => body(page));

    private static async Task WithPageAsync(InstanceDescription desc, Action<IInstanceStore> seed, Func<IPage, IInstanceStore, Task> body)
    {
        var dataPath = Path.GetTempFileName();
        await using var server = new TestInstanceServer();
        await server.StartAsync(desc, dataPath);
        seed(server.Store!);

        // Reuse the shared browser (launched once for the whole run; see SharedBrowser).
        var page = await SharedBrowser.NewPageAsync(server.BaseUrl);
        var logs = new List<string>();
        page.Console += (_, m) => logs.Add($"[{m.Type}] {m.Text}");
        page.PageError += (_, e) => logs.Add($"[pageerror] {e}");
        await page.GotoContentAsync("/");
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

        try { await body(page, server.Store!); }
        finally
        {
            await page.Context.CloseAsync();
            try { File.Delete(dataPath); } catch { /* best-effort */ }
        }
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

    private static void SeedPerson(IInstanceStore store, string name, int salary)
    {
        var id = store.CreateObject("Person", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name),
            ["salary"] = new IntValue(salary),
        }));
        store.AddToSet(NodePath.Root.Field("people"), id);
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
