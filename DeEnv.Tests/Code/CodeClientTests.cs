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

    // Client data layer, slice 1c — the GENERATION GUARD (optimistic-clobber safety for the async window).
    // A refetch is asynchronous: client state can move while its reply is in flight. The hazard: a refetch is
    // computed over the PRE-mutation store; an optimistic mutation lands while it is in flight; the stale
    // (pre-mutation) reply then arrives and mergeState OVERWRITES the just-edited object prop — reverting the
    // optimistic value. The fix (ws.ts) GENERALIZES the existing login/logout epoch guard from "the session
    // changed" to "the state changed": every journaled data mutation bumps `stateGen` (recordMutation), so a
    // reply whose in-flight generation no longer matches is DISCARDED + re-fetched. The WS is FIFO-ordered, so
    // the re-fetch is processed AFTER the mutation and reflects it — the merge never clobbers the edit.
    //
    // DETERMINISM (the project rule for client-runtime ordering — same approach as the negative→real remap
    // reconciler test above and LoginViewSwapTests): the real-world race is timing-dependent, but this FORCES
    // the interleaving by driving the real client directly — stamp a refetch in flight over the pre-mutation
    // state, apply a real optimistic edit (the production objectPropChange hook → recordMutation → stateGen++),
    // then deliver the STALE reply by invoking the real onWsMessage with a {op:"refetch", state} carrying the
    // IN-FLIGHT generation. No real WS reply can race it: the in-flight refetch is only stamped (not sent), and
    // the page has fully settled (hydration gate), so the only refetch reply is the one this test injects.
    // BEFORE the fix (recordMutation not bumping the gen) the injected reply's generation still matches → it
    // merges → the optimistic "z" is clobbered back to "a" and the model assertion fails (fail-before).
    [Test]
    public async Task An_optimistic_edit_survives_a_stale_in_flight_refetch_reply()
    {
        await WithPageAsync(InstanceContext.InteractiveUiDb(), s => { SeedItem(s, "a"); SeedItem(s, "b"); }, async page =>
        {
            // The "a" row (ordered first by name) is rendered with its db id; capture that id so the stale
            // reply can target the same object. Also stamp a refetch IN FLIGHT over the current (pre-mutation)
            // state — exactly what maybeRefetch does, but without sending, so no real reply can race the
            // injected one. `inFlightGen`/`refetchInFlight`/`stateGen` are the production ws.ts module globals.
            var itemId = await page.EvaluateAsync<int>(
                """
                () => {
                    const obj = Object.values(uiStatic.state.objects)
                        .find(o => o.props.name && o.props.name.value === "a");
                    inFlightGen = stateGen;     // the refetch was computed under the pre-mutation generation
                    refetchInFlight = true;     // …and is awaiting its reply
                    return obj.id;
                }
                """);

            // The optimistic mutation: type "z" into the "a" row's bound input. This fires the REAL
            // objectPropChange hook → recordMutation → stateGen++ (the bump under test), applies "z"
            // optimistically to the model + DOM, and sends an objectPropChange the server will commit.
            await page.Locator("input.name").Nth(0).FillAsync("z");

            // Deliver the STALE refetch reply (computed over the pre-mutation store, so it ships name "a"),
            // stamped with the captured in-flight generation. Because the mutation bumped stateGen,
            // inFlightGen !== stateGen → onWsMessage DISCARDS the reply (no mergeState) and re-fetches. Assert
            // synchronously, in the same evaluate, that the optimistic value was NOT reverted and a re-fetch
            // was re-armed. (Before the fix the generations match, mergeState overwrites name → "a", and the
            // model name below is "a".)
            var diag = await page.EvaluateAsync<string>(
                $$"""
                () => {
                    const staleReply = {
                        op: "refetch",
                        state: {
                            leaves: {
                                objects: { {{itemId}}: { props: { name: { type: "simple", value: { type: "text", value: "a" } } } } },
                                arrays: {}
                            },
                            scope: {},
                            cache: []
                        }
                    };
                    onWsMessage(staleReply);
                    const modelName = uiStatic.state.objects[{{itemId}}].props.name.value;
                    return `model=${JSON.stringify(modelName)} refetching=${refetchInFlight}`;
                }
                """);

            // (a) the optimistic edit survived the stale reply (NOT reverted to "a"); (b) a re-fetch was fired
            // (refetchInFlight re-armed) — the discard self-heals by re-asking under the current state.
            await Assert.That(diag).IsEqualTo("model=\"z\" refetching=true");

            // And the live input still shows the optimistic value — the discard path did not re-render it back.
            await Assert.That(await page.Locator($"[data-key=\"{itemId}\"] input.name").InputValueAsync()).IsEqualTo("z");
        });
    }

    // Client data layer, slice 3 — ATOMIC, COMMIT-ON-SUCCESS HANDLERS. A handler runs as a transaction:
    // its writes apply optimistically but are SENT only on clean completion; if it throws, the staged
    // effects are rolled back (model restored, NOTHING sent) — atomic, zero partial trace. Before slice 3
    // each write applied + sent per statement (objectPropChange fires immediately in the ws hook), so a
    // handler that wrote `a` and `b` and THEN threw leaked both writes locally AND to the server.
    //
    // The fixture's `bumpThenThrow(c)` writes c.a=1, c.b=2, then calls `c.name.add(c)` (a collection method
    // on a text — a runtime "cannot read 'add' on a non-object"), which throws AFTER both writes. This is a
    // NON-VNA throw (a genuine bug), so the handler transaction takes the atomic-rollback branch (a VNA —
    // missing data — is the separate slice-4 action-miss path and is left as today). The two writes hit the
    // bound db object via objectProp assignment → objectPropChange. BEFORE slice 3 those fired + sent per
    // statement, so a throw after them left the writes locally AND on the server. The browser scenario drives
    // a real click; the assertions on the model value, the journal length, and the store all catch the leak
    // (fail-before, proven: `a=1 b=2 journal=2`).
    [Test]
    public async Task A_throwing_handler_rolls_back_all_its_writes_atomically()
    {
        await WithPageAsync(InstanceContext.AtomicHandlerUiDb(), s => SeedCounter(s, "x", 0, 0), async (page, store) =>
        {
            // The single Counter row is rendered (a=0, b=0); capture its db id so we can read its model
            // object back after the click, and assert the starting state: a=0, b=0, an empty journal, gen 0.
            var counterId = await page.EvaluateAsync<int>(
                """() => Object.values(uiStatic.state.objects).find(o => o.props.name && o.props.name.value === "x").id""");
            var probe = await page.EvaluateAsync<string>(
                $$"""
                () => {
                    const c = uiStatic.state.objects[{{counterId}}];
                    return `a=${c.props.a.value} b=${c.props.b.value} journal=${journal.length} gen=${stateGen}`;
                }
                """);
            await Assert.That(probe).IsEqualTo("a=0 b=0 journal=0 gen=0");

            // Click the Bump button: its handler writes a=1, b=2, then throws (a collection method on a text).
            // The throw propagates out of the handler (re-thrown after the atomic rollback) as a page error —
            // capture it so the click itself does not fail, and so we can confirm the handler DID run far
            // enough to throw (proving the two writes were attempted, then undone).
            var errored = new TaskCompletionSource<bool>();
            page.PageError += (_, _) => errored.TrySetResult(true);
            await page.Locator("button.bump").ClickAsync();
            await Task.WhenAny(errored.Task, Task.Delay(2000));
            await Assert.That(errored.Task.IsCompleted).IsTrue();

            // The model object's a/b are back to 0 (the writes were rolled back), the journal is empty
            // (neither write was left pending — i.e. NOTHING was sent), and stateGen returned to 0 (the
            // mutations were unwound, so the generation reflects no standing optimistic change).
            var after = await page.EvaluateAsync<string>(
                $$"""
                () => {
                    const c = uiStatic.state.objects[{{counterId}}];
                    return `a=${c.props.a.value} b=${c.props.b.value} journal=${journal.length} gen=${stateGen}`;
                }
                """);
            await Assert.That(after).IsEqualTo("a=0 b=0 journal=0 gen=0");

            // And nothing reached the server: a brief settle, then the store still holds a=0, b=0. (Before
            // the fix the per-statement objectPropChange sends persisted a=1, b=2 even though the handler
            // threw.) Read the Counter extent's single object directly.
            await Task.Delay(150);
            var stored = store.ReadExtent("Counter").Values.Single();
            await Assert.That(IntField(stored, "a")).IsEqualTo(0);
            await Assert.That(IntField(stored, "b")).IsEqualTo(0);

            // The rolled-back values also re-render to the DOM (the abort path re-renders), so the view is
            // not left showing the partially-applied state.
            await Assert.That(await page.Locator("span.a").InnerTextAsync()).IsEqualTo("0");
            await Assert.That(await page.Locator("span.b").InnerTextAsync()).IsEqualTo("0");
        });
    }

    // Client data layer, slice 3 (review fix) — a throwing handler that did a REFERENCE SET must roll back
    // the OUT-OF-JOURNAL side effects too, so the abort leaves ZERO trace and triggers NO refetch.
    //
    // THE GAP THIS PROVES: the setRef hook (ws.ts) stages the prop in the journal but ALSO, outside the
    // journal entry, sets needsServerData = true + invalidateExtents() (coarse-staling every `extent:` memo
    // entry). The journal `undo` only restores the prop — it does NOT reverse those two. So a handler that
    // does sys.setRef(...) and THEN throws a genuine (non-VNA) bug took the atomic-rollback branch, reverted
    // the model + discarded the buffered send — but LEFT needsServerData set + the extent entries stale. The
    // abort's trailing renderUi → maybeRefetch then SENT a refetch (and armed refetchInFlight), violating the
    // slice's own guarantee ("zero partial trace, nothing sent") and leaving a half-state: stale extent memos
    // over a rolled-back model. The fix captures needsServerData + a snapshot of every cache entry's stale
    // flag at transaction BEGIN and restores them on the non-VNA abort (after the journal undo).
    //
    // The fixture's render iterates sys.extent("Counter"), so a real `extent:Counter` memo entry is present
    // + fresh at click time — invalidateExtents() actually STALES it during the handler, so this exercises
    // the stale-flag restore (not just needsServerData). Both halves are asserted via the `staleExtents`
    // count + the needsServerData/refetchInFlight probe + the WS send-spy.
    //
    // FAIL-BEFORE / PASS-AFTER: before the fix the post-abort probe reads needsServerData=true /
    // refetching=true / staleExtents>0 and the send-spy records a "refetch" op; after the fix all of those
    // are clean (false / false / 0 / no refetch). DETERMINISTIC: the abort path (undo + restore + renderUi +
    // maybeRefetch) is fully SYNCHRONOUS inside the click's handler, so reading the module globals + the
    // send-spy immediately after the throw observes the settled post-abort state with no race.
    [Test]
    public async Task A_throwing_handler_after_a_setRef_rolls_back_its_out_of_journal_effects()
    {
        await WithPageAsync(InstanceContext.AtomicHandlerUiDb(), s => SeedCounter(s, "x", 0, 0), async (page, store) =>
        {
            // FULL readiness: the WS is open + claimed and the connect-time refetch has applied, so
            // needsServerData has settled to false and no refetch is in flight — the clean baseline the abort
            // must return to. (Without this the connecting-window state could mask the leak.)
            await page.WaitReadyAsync();

            var counterId = await page.EvaluateAsync<int>(
                """() => Object.values(uiStatic.state.objects).find(o => o.props.name && o.props.name.value === "x").id""");

            // Baseline: the `link` reference is unset, the model is settled (empty journal, gen 0), and —
            // critically for this test — needsServerData is false, no refetch is in flight, and no `extent:`
            // memo entry is stale. An unset single reference ships as {type:"null"}, so normalize null/nothing/
            // absent → "unset" — the post-abort assertion proves it was restored to exactly that.
            var before = await page.EvaluateAsync<string>(
                $$"""
                () => {
                    const c = uiStatic.state.objects[{{counterId}}];
                    const lv = c.props.link;
                    const link = (!lv || lv.type === "null" || lv.type === "nothing") ? "unset" : "set";
                    let staleExtents = 0;
                    for (const [k, e] of uiStatic.cache) if (k.startsWith("extent:") && e.stale) staleExtents++;
                    return `link=${link} journal=${journal.length} gen=${stateGen} needs=${needsServerData} `
                         + `refetching=${refetchInFlight} staleExtents=${staleExtents}`;
                }
                """);
            await Assert.That(before).IsEqualTo("link=unset journal=0 gen=0 needs=false refetching=false staleExtents=0");

            // Spy on the live WS send: record every sent frame's `op` so the test can prove NOTHING was sent
            // by the aborted handler — neither the setReferenceField the setRef staged (discarded) nor a
            // refetch leaked by un-reverted needsServerData/stale extents. codeWs is the production module
            // global; the page has settled so the socket is open and this send is the real one.
            await page.EvaluateAsync(
                """
                () => {
                    window.__sentOps = [];
                    const orig = codeWs.send.bind(codeWs);
                    codeWs.send = (text) => { try { window.__sentOps.push(JSON.parse(text).op); } catch {} return orig(text); };
                }
                """);

            // Click SetRef: the handler does sys.setRef(c, "link", c) — which sets c.props.link, stages a
            // journal entry, BUFFERS a setReferenceField send, and (outside the journal) sets needsServerData
            // + coarse-stales the extents — then throws (a collection method on a text). The throw re-surfaces
            // as a page error after the atomic rollback; capture it so the click does not fail the test.
            var errored = new TaskCompletionSource<bool>();
            page.PageError += (_, _) => errored.TrySetResult(true);
            await page.Locator("button.setref").ClickAsync();
            await Task.WhenAny(errored.Task, Task.Delay(2000));
            await Assert.That(errored.Task.IsCompleted).IsTrue();

            // Post-abort: the reference is back to unset (the journal undo), AND the out-of-journal state is
            // back to the begin values — needsServerData false, NO refetch armed, NO `extent:` entry stale.
            // Before the fix this read `needs=true refetching=true staleExtents>0`. (gen restored to 0 too.)
            var after = await page.EvaluateAsync<string>(
                $$"""
                () => {
                    const c = uiStatic.state.objects[{{counterId}}];
                    const lv = c.props.link;
                    const link = (!lv || lv.type === "null" || lv.type === "nothing") ? "unset" : "set";
                    let staleExtents = 0;
                    for (const [k, e] of uiStatic.cache) if (k.startsWith("extent:") && e.stale) staleExtents++;
                    return `link=${link} journal=${journal.length} gen=${stateGen} needs=${needsServerData} `
                         + `refetching=${refetchInFlight} staleExtents=${staleExtents}`;
                }
                """);
            await Assert.That(after).IsEqualTo("link=unset journal=0 gen=0 needs=false refetching=false staleExtents=0");

            // And NOTHING was put on the wire by the aborted handler: no setReferenceField (the staged send
            // was discarded) and — the crux — NO refetch (the un-reverted needsServerData/stale extents would
            // have triggered one through the abort's renderUi → maybeRefetch). A short settle lets any
            // erroneously-armed refetch actually flush before we read the spy.
            await Task.Delay(150);
            var sentOps = await page.EvaluateAsync<string[]>("() => window.__sentOps");
            await Assert.That(sentOps).DoesNotContain("refetch");
            await Assert.That(sentOps).DoesNotContain("setReferenceField");

            // The server never saw the ref either: the stored Counter's `link` is still an UNSET reference
            // (TargetId null) — the setReferenceField the handler staged was discarded by the abort, so it
            // never reached storage. (An unset single reference loads as ReferenceValue(null, ...).)
            var stored = store.ReadExtent("Counter").Values.Single();
            var link = stored.Fields.GetValueOrDefault("link") as ReferenceValue;
            await Assert.That(link?.TargetId).IsNull();
        });
    }

    // Client data layer, slice 4 (the `didWork` DISCRIMINATOR — the guard that protects the write-then-VNA
    // case from the action-miss round-trip). A click handler's VNA throw is one of two very different things,
    // told apart by whether the handler had already BUFFERED a send (done real work) before it threw:
    //   • NO buffered send → a genuine ACTION-MISS: the handler READ un-shipped data first → abort, record a
    //     pending action, fetch (the server harvests + re-invokes). Proven in a real browser by
    //     A_button_handler_that_reads_unshipped_data_round_trips (ActionMissFixture).
    //   • a send ALREADY buffered → the handler DID its real work and the VNA is INCIDENTAL (a trailing read
    //     over un-shipped data). It must take the FLUSH + RE-THROW path: the real write persists, NOTHING is
    //     planned as an action. Re-running it on the server read-only would mis-harvest (it reads client-only
    //     draft state the server cannot reproduce — "Unknown field" — and would re-do the mutation).
    //
    // THIS pins the second branch deterministically. The fixture's `bumpThenSchemaMiss(c)` writes c.a = 1 (a
    // real objectPropChange → a BUFFERED send) and THEN reads `sys.schema("Counter")`. A handler runs under
    // memoBypass, and under bypass the client's execSchema memoize calls its compute DIRECTLY (no cache
    // consult), whose body unconditionally throws "Value not available" — so this is the SPURIOUS post-write
    // VNA the slice-3 note names (the descriptor IS shipped; the bypass forces a re-compute that throws). The
    // button is in the render's foreach, so its onClick closure carries a render-slot → an `action` IS passed
    // to runHandlerTransaction; with `didWork == true` the VNA branch FLUSHES the buffered write + re-throws
    // (does NOT arm pendingAction, does NOT send an action-carrying refetch).
    //
    // FAIL-BEFORE / PASS-AFTER: reverting the `didWork` guard (so the write-then-VNA wrongly takes the
    // action-miss path — `if (action != null)` without `&& !didWork`) ABORTS the write and arms pendingAction
    // + fires an action-carrying refetch. The assertions then fail three ways: pendingAction is non-null, a
    // `refetch` frame carrying `handlerFn` was sent, and no `objectPropChange` reached the wire/store. With
    // the guard they are clean: pendingAction null, no action-refetch, the objectPropChange sent + c.a = 1
    // in the store, and the spurious VNA re-throws as a page error.
    //
    // DETERMINISTIC: the VNA branch (flush + re-throw, or — fail-before — abort + arm + refetch) runs fully
    // SYNCHRONOUSLY inside the click's handler, so reading pendingAction + the send-spy immediately after the
    // throw observes the settled state with no race; the store readback polls the async WS round-trip.
    [Test]
    public async Task A_write_then_spurious_VNA_handler_flushes_and_does_not_arm_an_action()
    {
        await WithPageAsync(InstanceContext.AtomicHandlerUiDb(), s => SeedCounter(s, "x", 0, 0), async (page, store) =>
        {
            // FULL readiness: the WS is open + claimed and the connect-time refetch has applied, so no refetch
            // is in flight and pendingAction is clean — the baseline the discriminator acts from. (Without
            // this the connecting-window state could mask which refetch the spy records.)
            await page.WaitReadyAsync();

            var counterId = await page.EvaluateAsync<int>(
                """() => Object.values(uiStatic.state.objects).find(o => o.props.name && o.props.name.value === "x").id""");

            // Baseline: a=0, an empty journal, gen 0, and NO pending action. (a real objectPropChange will be
            // staged + sent by the handler before the VNA; the discriminator keeps it.)
            var before = await page.EvaluateAsync<string>(
                $$"""
                () => {
                    const c = uiStatic.state.objects[{{counterId}}];
                    return `a=${c.props.a.value} journal=${journal.length} gen=${stateGen} pending=${pendingAction == null ? "none" : "set"}`;
                }
                """);
            await Assert.That(before).IsEqualTo("a=0 journal=0 gen=0 pending=none");

            // Spy on the live WS send: record every sent frame's `op` AND whether it carries a `handlerFn`
            // The pass case sends an objectPropChange (the real write, buffered then flushed by the VNA branch) and NO
            // action-carrying refetch; the fail-before case sends a `refetch` WITH handlerFn and NO
            // objectPropChange. codeWs is the production module global; the page has settled so this send is
            // the real one.
            await page.EvaluateAsync(
                """
                () => {
                    window.__sent = [];
                    const orig = codeWs.send.bind(codeWs);
                    codeWs.send = (text) => {
                        try { const m = JSON.parse(text); window.__sent.push({ op: m.op, action: m.handlerFn != null }); } catch {}
                        return orig(text);
                    };
                }
                """);

            // Click SchemaMiss: the handler writes c.a = 1 (an objectPropChange → a BUFFERED send), then reads
            // sys.schema("Counter") which throws a SPURIOUS VNA under memoBypass. With didWork == true the VNA
            // branch flushes the buffered write + re-throws — the re-throw surfaces as a page error after the
            // flush; capture it so the click does not fail the test (and it proves the handler ran to the VNA).
            var errored = new TaskCompletionSource<bool>();
            page.PageError += (_, _) => errored.TrySetResult(true);
            await page.Locator("button.schemamiss").ClickAsync();
            // The page error IS guaranteed here (it is the VNA re-throw), so just wait for it with a generous
            // ceiling — a tight 2s ceiling raced the click→re-throw under full-suite browser oversubscription
            // (the documented flake: failed ~2/5, passed in isolation). WaitAsync throws TimeoutException on a
            // genuine miss, so no separate IsCompleted assert is needed.
            await errored.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Post-throw: the optimistic write STANDS (a=1, gen bumped), NO pending action was armed.
            // The journal entry exists briefly then retires when the server acks the objectPropChange; checking
            // journal.length is inherently racy (ack may arrive before EvaluateAsync). We check the observable
            // OUTCOMES: stateGen was bumped (gen=1), no pending action, and the store eventually holds a=1.
            var after = await page.EvaluateAsync<string>($$"""
                () => {
                    const c = uiStatic.state.objects[{{counterId}}];
                    return `a=${c.props.a.value} gen=${stateGen} pending=${pendingAction == null ? "none" : "set"}`;
                }
                """);
            await Assert.That(after).IsEqualTo("a=1 gen=1 pending=none");

            // The wire: the real objectPropChange WAS sent, and NO action-carrying refetch was. A short settle
            // lets any erroneously-armed action refetch actually flush before we read the spy. Before the fix
            // the spy would show a `refetch` with handlerFn and NO objectPropChange.
            await Task.Delay(150);
            var sentOps = await page.EvaluateAsync<string[]>("() => window.__sent.map(s => s.op)");
            var actionRefetches = await page.EvaluateAsync<int>(
                """() => window.__sent.filter(s => s.op === "refetch" && s.action).length""");
            await Assert.That(sentOps).Contains("commit");
            await Assert.That(actionRefetches).IsEqualTo(0);

            // And the write reached the server: the stored Counter's `a` is 1 (the flushed objectPropChange
            // persisted). Before the fix the aborted write never reached storage — `a` would still be 0.
            await AssertEventuallyAsync(() => IntField(store.ReadExtent("Counter").Values.Single(), "a") == 1);
        });
    }

    // Client data layer, the LAST slice — the CLIENT REACHABILITY GC (the dual of the server store GC). The
    // client data graph (uiStatic.state.objects/arrays) GROWS as views pull data over the round-trip
    // (mergeState merges by id on every refetch; old views' rows + transient extents/where-results/descriptors
    // linger) and never shrinks. A mark-and-sweep on NAVIGATION (resetViewState → sweepUnreachable) drops the
    // entries no live root reaches. SAFE only because the round-trip can re-pull anything (slices 1a–4), so a
    // conservative sweep is fine — but a FALSE-sweep (dropping a still-reachable entry) is NOT, so this proves
    // BOTH directions: the unreachable data is GONE after a nav, and EVERY root class (scope, the memo cache,
    // the pending journal) protects its data from the sweep.
    //
    // DETERMINISM (the project rule for client-runtime state, as in the slice-1c/slice-3 tests above): drive
    // the REAL client directly — plant the four cases into the production uiStatic.state / cache / journal
    // exactly as mergeState + the mutation hooks would, then invoke the PRODUCTION resetViewState() (the nav
    // choke point that calls sweepUnreachable) and inspect the graph synchronously. No server timing.
    //
    // FAIL-BEFORE / PASS-AFTER: without sweepUnreachable() wired into resetViewState, the DEAD detached entries
    // linger after the nav and the "gone" assertion fails; the three kept-cases pass either way (they prove the
    // sweep is conservative — that it did NOT over-collect). With the sweep, dead is dropped + reachable kept.
    [Test]
    public async Task Navigating_away_drops_unreachable_data_but_keeps_every_root()
    {
        await WithPageAsync(InstanceContext.InteractiveUiDb(), s => { SeedItem(s, "a"); SeedItem(s, "b"); }, async page =>
        {
            await page.WaitReadyAsync();

            // Plant the four cases into the LIVE client graph the way an accumulation of past views would, then
            // run the production resetViewState() (→ sweepUnreachable) and report what survived. All ids are
            // distinct positives well above the seeded data so they can't collide.
            //
            //  • DEAD — a detached object (7000) + a detached array (7001) referenced by NOTHING: the lingering
            //    cross-view data the GC exists to drop. Must be GONE after the nav.
            //  • SCOPE root — a db item ("a"), reachable from the `db` var (scope → items array → item). Kept.
            //  • CACHE root — a detached array (7010) holding a detached object (7011), installed as a memo
            //    entry's `result` (a where/orderBy-style cached computation). The walk descends the result, so
            //    both are kept — proving a surviving cache result protects its data.
            //  • JOURNAL root — a detached object (7020) pinned ONLY by a pending journal entry's `roots` (an
            //    optimistic arrayRemove's detached item is reachable nowhere else). Kept — proving the rollback's
            //    data survives the sweep.
            var report = await page.EvaluateAsync<string>(
                """
                () => {
                    const st = uiStatic.state;
                    const aId = Object.values(st.objects).find(o => o.props.name && o.props.name.value === "a").id;

                    // DEAD: detached object + array, referenced by nothing.
                    st.objects[7000] = { type: "object", id: 7000, props: { tag: { type: "text", value: "dead" } } };
                    st.arrays[7001] = { type: "array", id: 7001, kind: "list", items: [] };

                    // CACHE root: a detached array (7010) → detached object (7011), held by a memo entry result.
                    const cacheObj = { type: "object", id: 7011, props: { tag: { type: "text", value: "cache" } } };
                    const cacheArr = { type: "array", id: 7010, kind: "list", items: [{ key: 1, value: cacheObj }] };
                    st.objects[7011] = cacheObj;
                    st.arrays[7010] = cacheArr;
                    uiStatic.cache.set("test:cacheResult", { result: cacheArr, deps: { props: [], members: [], vars: [] }, stale: false });

                    // JOURNAL root: a detached object (7020) pinned ONLY by a pending journal entry (an
                    // arrayRemove's removed item — in no array). Pushed via the production recordMutation so its
                    // `roots` is what sweepUnreachable reads.
                    const journalObj = { type: "object", id: 7020, props: { tag: { type: "text", value: "journal" } } };
                    st.objects[7020] = journalObj;
                    recordMutation({ msgId: nextWsMsgId++, undo: () => {}, redo: () => {}, roots: [journalObj] });

                    // The production navigation choke point — drops `comp:` entries then sweeps.
                    resetViewState();

                    const has = (id) => st.objects[id] != null;
                    const hasArr = (id) => st.arrays[id] != null;
                    return [
                        `deadObj=${has(7000)}`, `deadArr=${hasArr(7001)}`,   // expect false, false (swept)
                        `scope=${has(aId)}`,                                  // expect true  (db graph)
                        `cacheArr=${hasArr(7010)}`, `cacheObj=${has(7011)}`, // expect true  (cache result)
                        `journalObj=${has(7020)}`,                            // expect true  (journal root)
                    ].join(" ");
                }
                """);

            // Dead detached entries are GONE; every root class kept its data (no false-sweep).
            await Assert.That(report).IsEqualTo(
                "deadObj=false deadArr=false scope=true cacheArr=true cacheObj=true journalObj=true");
        });
    }

    // ── T4: the client `CommitRelation.wire` union RECOGNIZES the `dictRemove` shape ───────────────────
    //
    // A `dictRemove` relation carries exactly { kind: "dictRemove", ownerRef, prop, key } — drop ONE dict
    // entry (mirrors dict.Remove(k), a targeted unlink, not a bulk detach). The server already accepts it
    // (T3); this task extends only the CLIENT union's awareness of the shape. Client dict-write support is
    // DEFERRED (no ctx.commit dict hook exists yet), so there is NO client call site that buffers a
    // dictRemove — this test drives the EXISTING commit-flush path (flushHandlerTx maps each r.wire
    // verbatim into the `commit` frame's `relations`) with a hand-built `dictRemove` CommitRelation and
    // proves the emitted wire carries the shape UNCHANGED (no field dropped, no name remapped). This is the
    // green half of the TDD red→green: the union comment in ws.ts is what makes the shape legitimate here.
    [Test]
    public async Task A_dictRemove_commit_relation_is_emitted_verbatim()
    {
        await WithPageAsync(InstanceContext.InteractiveUiDb(), s => { SeedItem(s, "a"); }, async page =>
        {
            await page.WaitReadyAsync();

            // Hand a `dictRemove` CommitRelation to the production flush path and capture the `commit`
            // frame's `relations` off the wire. undo/redo are no-ops; roots empty (no model effects to
            // keep alive — a dict entry carries no extent id). flushHandlerTx + the wire map are unchanged
            // by T4; only the union comment is updated, so the dictRemove shape must pass through verbatim.
            var diag = await page.EvaluateAsync<string>(
                """
                () => {
                    let relationsWire = null;
                    const orig = codeWs.send.bind(codeWs);
                    codeWs.send = (text) => {
                        try { const m = JSON.parse(text); if (m.op === "commit") relationsWire = m.relations; } catch {}
                        return orig(text);
                    };

                    commitRelations = [{
                        wire: { kind: "dictRemove", ownerRef: 1, prop: "meta", key: "fav" },
                        journalMsgId: nextWsMsgId++,
                        undo: () => {}, redo: () => {}, roots: [],
                    }];
                    flushHandlerTx();

                    return JSON.stringify(relationsWire);
                }
                """);

            // The flushed commit carries EXACTLY the dictRemove relation — verified verbatim, proving the
            // client union now includes it and the flush path forwards it without alteration.
            await Assert.That(diag).IsEqualTo(
                "[{\"kind\":\"dictRemove\",\"ownerRef\":1,\"prop\":\"meta\",\"key\":\"fav\"}]");
        });
    }

    // ── atomic-commit Step B: the generic create DEFERS / persists IMMEDIATELY by context ──────────────
    //
    // A generic create under an OBJECT's form (a nested inline set) STAGES into that form's ctx — it does NOT
    // persist on the create card's Add; the enclosing form's Save (ctx.commit) is what persists it. The Order
    // (/orders/2) has a scalar `title` (so its ObjectForm has a Save) plus an inline `lines` SetTable. Adding a
    // Line through the inline create form must leave the store unchanged UNTIL the Order form's Save. This is
    // the heart of B2's generic-UI change; driven through the real client over a real server (the store is the
    // observable). NB the top-level case is the OPPOSITE — proven by the existing /notes & /tasks scenarios,
    // where the SetTable sits under the LIVE page ctx and its creates persist on Add.
    [Test]
    public async Task A_create_under_an_object_form_defers_to_that_forms_save()
    {
        await WithPageAsync(InstanceContext.NestedSetCreateDb(), _ => { }, async (page, store) =>
        {
            await page.GotoContentAsync("/orders/2");
            await page.WaitForSelectorAsync(".object-form");     // the Order's edit form (title + inline lines)
            await page.WaitReadyAsync();

            // Open the inline lines create form, fill a Line, and Add — the create STAGES into the Order
            // form's ctx (the card's Add does not persist).
            await page.Locator(".set-table .new-btn").ClickAsync();
            await page.Locator(".create-form input.label").FillAsync("First line");
            await page.Locator(".create-form button.create-save").ClickAsync();

            // The optimistic row appears…
            await page.WaitForFunctionAsync(
                "() => [...document.querySelectorAll('.set-table .row-link')].some(a => a.textContent.includes('First line'))");

            // …but NOTHING is persisted yet: the create is staged in the form's ctx, not on the wire. Give any
            // erroneous live send time to land, then assert the store still has no Line.
            await Task.Delay(250);
            await Assert.That(store.ReadExtent("Line").Values.Any(o => Label(o) == "First line")).IsFalse();

            // Save the Order form — its ctx.commit flushes the staged Line as one atomic commit, which persists.
            await page.Locator(".object-form .form-actions button.save").ClickAsync();
            await AssertEventuallyAsync(() => store.ReadExtent("Line").Values.Any(o => Label(o) == "First line"));

            // And it is LINKED into the Order's lines set (not just minted): the Order's lines set contains it.
            await AssertEventuallyAsync(() =>
            {
                var order = store.ReadById(2);
                var lines = order?.Fields.Fields.GetValueOrDefault("lines") as SetValue;
                var lineIds = store.ReadExtent("Line").Where(kv => Label(kv.Value) == "First line").Select(kv => kv.Key).ToHashSet();
                return lines != null && lines.Members.Keys.Any(lineIds.Contains);
            });
        });
    }

    // The contrast (atomic-commit Step B point 4): a create under a SAVE-LESS container — a TOP-LEVEL set
    // route — persists IMMEDIATELY on Add (the SetTable sits under the LIVE page ctx, so nothing defers). The
    // Db's `orders` set at /orders is exactly that. Proven directly here so the immediate path is pinned next
    // to the deferred one (the existing /notes & /tasks scenarios also exercise it).
    [Test]
    public async Task A_create_under_a_save_less_container_persists_immediately()
    {
        await WithPageAsync(InstanceContext.NestedSetCreateDb(), _ => { }, async (page, store) =>
        {
            await page.GotoContentAsync("/orders");
            await page.WaitForSelectorAsync(".set-table");       // the top-level orders set route (no Save)
            await page.WaitReadyAsync();

            await page.Locator(".set-table .new-btn").ClickAsync();
            await page.Locator(".create-form input.title").FillAsync("Second order");
            await page.Locator(".create-form button.create-save").ClickAsync();

            // No enclosing form Save exists — the create persists on Add (the live page ctx).
            await AssertEventuallyAsync(() => store.ReadExtent("Order").Values.Any(o => Title(o) == "Second order"));
        });
    }

    private static string? Name(ObjectValue o) =>
        o.Fields.TryGetValue("name", out var n) && n is TextValue t ? t.Text : null;

    private static string? Label(ObjectValue o) =>
        o.Fields.TryGetValue("label", out var n) && n is TextValue t ? t.Text : null;

    private static string? Title(ObjectValue o) =>
        o.Fields.TryGetValue("title", out var n) && n is TextValue t ? t.Text : null;

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

    private static void SeedCounter(IInstanceStore store, string name, int a, int b)
    {
        var id = store.CreateObject("Counter", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name),
            ["a"] = new IntValue(a),
            ["b"] = new IntValue(b),
        }));
        store.AddToSet(NodePath.Root.Field("items"), id);
    }

    private static int? IntField(ObjectValue o, string field) =>
        o.Fields.TryGetValue(field, out var v) && v is IntValue i ? i.Value : null;
}
