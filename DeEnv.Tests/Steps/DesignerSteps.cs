using DeEnv.Kernel;
using DeEnv.Tests.TestSupport;
using DeEnv.Instance;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for Designer.feature — the operator IDE (the REAL DeEnv/instances/1/app.deenv), a URL-routed
// multi-instance designer driven end-to-end through a real browser. Unlike the rest of the
// browser-driven suite it runs against a REAL KernelHost (InstanceContext.StartKernelDesignerBrowserAsync):
// the IDE renders `sys.instances` (the kernel's hosted set), which is empty under the kernel-less
// TestInstanceServer, so the designer can only be exercised against a live kernel. The kernel hosts the
// designer (id 1) plus the named target instances; the browser is pointed at the designer's app port.
//
// The IDE's surfaces are SEPARATE: `/designs` (the design library) + `/designs/<designId>` (the design
// EDITOR — type editor + code areas, no publish), and `/instances` (instances + their current design)
// + `/instances/<id>` (a design SELECTOR — a <select> dropdown + Apply). An instance references its
// design by an EXPLICIT `designId` (seeded by the fixture so the dropdowns start correct).
//
// Cross-route navigation is an <a href> per route. A DIRECT GET (or a reload) server-renders the route
// from the store, but since round-1 an in-app link CLICK is intercepted and handled CLIENT-SIDE (the
// target re-renders over the warm session via a refetch — same as the generic UI); the steps click the
// links, and for a forward click that lands the editor a pushState updates the URL (no Load event).
[Binding]
public sealed class DesignerSteps(InstanceContext ctx)
{
    private const string DesignerAdminName = "admin";
    private const string DesignerAdminPassword = "hunter2";

    // The kernel-hosted designer instance (id 1); its targets are reached via ctx.Kernel.Instances by
    // their registry label (Spec.App).
    private HostedInstance _designer = null!;

    // The name the create-instance form was filled with — used to locate the spawned instance (its
    // display label AND its mount, since addressing is by path now; there are no per-instance ports).
    private string _newInstanceName = "";

    // The kernel id minted for an instance created via a direct host-action step (Slice 2 store assertions).
    private int _lastCreatedInstanceId;

    // The name given to a just-added type (so the base-type / values steps relocate that row once it is
    // no longer the empty-name one).
    private string _justAddedTypeName = "";

    // The preview→apply consistency-guard scenario (M13 Track-B B3 addendum): the target's ON-DISK log line
    // count captured right after the target's data was bumped (simulating "data moved since the preview"),
    // so a later assertion can prove a REJECTED apply appended NO FURTHER log entries — i.e. the versioned
    // leg's destructive boundary was never materialized onto the target's data file (the review fix this
    // guards against). Deliberately the ON-DISK log, not the live store's CurrentVersion — the boundary
    // write is OFFLINE (bypasses the live store until a restart), so CurrentVersion would never move either
    // way and the check would be vacuous.
    private int _todoTargetLogLinesAfterStaleness;

    // ── Given ───────────────────────────────────────────────────────────────────

    [Given("the operator IDE is running on a kernel hosting instances {string} and {string}")]
    public async Task GivenIdeRunning(string firstLabel, string secondLabel)
    {
        // Boot a kernel hosting the real designer (id 1) + two target instances labelled to match the
        // designer's two seeded designs (the fixture seeds each target's designId to the matching
        // design), then point the browser at the designer's app port.
        _designer = await ctx.StartKernelDesignerBrowserAsync((5, firstLabel), (6, secondLabel));
        SeedDesignerAdmin();
        await LoginDesignerAdminAsync();
    }

    [Given("the anonymous operator IDE is running on a kernel hosting instances {string} and {string}")]
    public async Task GivenAnonymousIdeRunning(string firstLabel, string secondLabel)
    {
        _designer = await ctx.StartKernelDesignerBrowserAsync((5, firstLabel), (6, secondLabel));
        SeedDesignerAdmin();
    }

    // ── When: navigation ─────────────────────────────────────────────────────────

    [When("I open the designer designs route")]
    public async Task WhenOpenDesignerDesignsRoute() =>
        await ctx.Page!.GotoReadyAsync(ctx.DesignerUrl("/designs"));

    [When("I open the designs list")]
    public async Task WhenOpenDesignsList()
    {
        await ctx.Page!.GotoReadyAsync(ctx.DesignerUrl("/designs"));
        // The designs list now renders via the generic <SetTable> (a .set-row per design, the label in
        // a stretched a.row-link, with a per-row action cell carrying the Edit link + Delete button).
        await ctx.Page!.WaitForSelectorAsync("main.ide-designs .set-row");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I open the instances list")]
    public async Task WhenOpenList()
    {
        await ctx.Page!.GotoReadyAsync(ctx.DesignerUrl("/instances"));
        // Hydration checkpoint: the SSR instance rows are present AND the client bundle has bootstrapped
        // (window.initUi set), so the hand-rolled links/handlers are attached before we interact.
        await ctx.Page!.WaitForSelectorAsync("main.ide-list .set-row");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I edit the design {string}")]
    public async Task WhenEditDesign(string label)
    {
        // The Edit link is an <a href="/designs/<designId>"> on the matching design row. Since round-1 the
        // in-app click is INTERCEPTED and handled CLIENT-SIDE (no full reload — the deep editor re-renders
        // over the warm session; a full SSR still happens on a DIRECT GET of the URL, e.g. a refresh).
        // Wait for the editor SECTION (always present once the design resolves) rather than a type row —
        // a freshly-added design has no types yet, so .type-name would never appear for it.
        await DesignRowFor(label).Locator("a.edit-design").ClickAsync();
        await ctx.Page!.WaitForSelectorAsync("main.ide-design-edit .design-editor");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I open the instance {string}")]
    public async Task WhenOpenInstance(string label)
    {
        // The Open link is a fresh-SSR <a href="/instances/<id>"> now living in the row's kebab (overflow)
        // menu — all row actions were consolidated there. Reaching the instance page can start from the
        // instances list OR directly (after editing a design) — go to the list first so the row is present,
        // open its kebab so the Open link is visible, then click it.
        if (await ctx.Page!.Locator($"main.ide-list .set-row").CountAsync() == 0)
            await WhenOpenList();
        await RowFor(label).Locator("td.row-action button.kebab-toggle").ClickAsync();
        await RowFor(label).Locator(".kebab-menu.open a.open-instance").ClickAsync();
        await ctx.Page!.WaitForSelectorAsync("main.ide-instance select.design-pick");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I open that new instance")]
    public async Task WhenOpenNewInstance()
    {
        // The just-created instance is the one carrying the name we typed (its mount + display label).
        // The selector route is keyed by the design-host's stored Instance OBJECT id (what the generic
        // <SetTable>'s row-link emits via sys.nest(setPath, member)), so look that id up from db.instances
        // by the runtime id — then navigate exactly as the row-link / Open link would. The Slice-2 mirror
        // writes the row inside CreateAsync (before the WS reply), so it is present by now; poll defensively.
        var created = ctx.Kernel!.Instances.Single(i => i.Spec.App == _newInstanceName);
        int objId = 0;
        await EventuallyAsync(() =>
        {
            var match = _designer.Store.ReadExtent("Instance")
                .FirstOrDefault(kv => kv.Value.Fields.TryGetValue("runtimeId", out var rv)
                    && rv is DeEnv.Storage.IntValue ri && ri.Value == created.Spec.Id);
            if (match.Value is null) return false;
            objId = match.Key;
            return true;
        });
        await ctx.Page!.GotoReadyAsync(ctx.DesignerUrl($"/instances/{objId}"));
        await ctx.Page!.WaitForSelectorAsync("main.ide-instance select.design-pick");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    // ── When: creating (the GENERIC SetTable create) ─────────────────────────────

    // Both phrasings drive the SAME generic-create flow (the designer no longer has a bespoke Add box):
    // the SetTable's "New" button reveals its create form, which the designs list customizes to a
    // LABEL-ONLY field (the `createForm` slot), then Save runs the generic set.add(draft).
    [When("I create a design named {string}")]
    public async Task WhenCreateDesign(string label) => await CreateDesignViaGenericNew(label);

    [When("I add a design named {string}")]
    public async Task WhenAddDesign(string label) => await CreateDesignViaGenericNew(label);

    private async Task CreateDesignViaGenericNew(string label)
    {
        // Click the SetTable's "New " button to reveal its create form (the table → create-form swap),
        // then fill the customized label-only field and Save. Save runs db.designs.add(draft) — a
        // journaled mutation. The new row appears immediately via the client re-render (no nav —
        // race-free), first carrying the draft's NEGATIVE transient id; the WS persist then remaps it.
        await ctx.Page!.Locator("main.ide-designs .new-btn").ClickAsync();
        await ctx.Page.Locator("main.ide-designs .create-form input.label").FillAsync(label);
        await ctx.Page.Locator("main.ide-designs .create-form button.create-save").ClickAsync();
        // Confirm the new row shows in the list (the race-free client re-render). The list renders via
        // the generic <SetTable>, so a row is .set-row and the label is the stretched a.row-link.
        await ctx.Page.WaitForSelectorAsync(
            $".set-row:has(a.row-link:text-is({CssString(label)}))");
        // Then wait for the persist+remap to land on the client — the row's Edit link must point at the
        // real (positive) id, so a later Edit click navigates to the now-persisted design, not its
        // transient negative id. The href is /designs/<id> via sys.nest, so match a positive trailing id.
        await ctx.Page.WaitForFunctionAsync(
            $$"""
            () => {
                const rows = [...document.querySelectorAll('.set-row')];
                const row = rows.find(r => { const l = r.querySelector('a.row-link'); return l && l.textContent.trim() === {{JsString(label)}}; });
                if (!row) return false;
                const a = row.querySelector('a.edit-design');
                return a != null && /\/designs\/[0-9]+$/.test(a.getAttribute('href') || '');
            }
            """);
    }

    [When("I create an instance named {string} from the design {string}")]
    public async Task WhenCreateInstance(string name, string designLabel)
    {
        // The instances list is the generic <SetTable>: its "New Instance" button reveals the create
        // form. The `createForm` slot self-hosts a focused body — the generic <RefSelect> (a bare
        // ref-binding <select> over db.designs) plus the generic name <Field>. It is ONE step: pick the
        // design, type the name, Save. The native SelectOption fires the select's `onChange` (RefSelect's
        // applyPick) which does sys.setRef on the draft — NO extra "Set"/"Use" click. Save runs SetTable's
        // `onCreate` override → sys.create(draft.design, name), a host action; the reply triggers a WS
        // refetch (+ resetViewState) so the new row appears via the db.instances mirror, in place, no reload.
        await ctx.Page!.Locator("main.ide-list .new-btn").ClickAsync();
        var form = ctx.Page.Locator("main.ide-list .create-form");
        await form.Locator("select.ref-select").SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Label = designLabel });
        _newInstanceName = name;
        // The name field is the generic <Field> for Instance.name, so its input carries class `name`.
        await form.Locator("input.name").FillAsync(name);
        await form.Locator("button.create-save").ClickAsync();
    }

    [When("I reveal the instance create form")]
    public async Task WhenRevealInstanceCreateForm()
    {
        await ctx.Page!.Locator("main.ide-list .new-btn").ClickAsync();
        await ctx.Page.Locator("main.ide-list .create-form").WaitForAsync();
    }

    [Then("the instance create form has a bare design ref-select with no Set button")]
    public async Task ThenBareRefSelect()
    {
        var form = ctx.Page!.Locator("main.ide-list .create-form");
        // The generic RefSelect is the bare ref-binding <select> — present, with the "(choose…)" placeholder.
        // It renders only once `candidates` (db.designs) is available: with no footprint anchor, that data
        // arrives on the toggle REFETCH (the nested-draft round-trip reproducing the open form), so wait for
        // the select to attach rather than asserting its count synchronously (which would race the refetch).
        await form.Locator("select.ref-select").WaitForAsync(
            new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        await Assert.That(await form.Locator("select.ref-select").CountAsync()).IsEqualTo(1);
        // No per-candidate Set/Use button (the old picker pattern) — the native pick is the whole control.
        await Assert.That(await form.Locator(".ref-set, button:has-text(\"Set\")").CountAsync()).IsEqualTo(0);
    }

    [Then("the instance create form's design ref-select offers the design {string}")]
    public async Task ThenRefSelectOffersDesign(string designLabel)
    {
        // The picker is the generic <RefSelect> whose `foreach c in db.designs` builds one <option> per
        // candidate. Those candidates are harvested by the toggle refetch reproducing the OPEN form on the
        // server — which only works if the SetTable's nested transient `draft` round-tripped (slotState
        // ships it by value; the server rebuilds it so RefSelect's `parent` is non-null and db.designs is
        // read). An auto-waiting locator: the option appears once the refetch reply fills the picker.
        var option = ctx.Page!.Locator("main.ide-list .create-form select.ref-select option")
            .Filter(new() { HasTextString = designLabel });
        // An <option> inside a closed <select> is never "visible", so wait for ATTACHED (it exists in the
        // DOM once the refetch reply re-renders the populated picker), not for visibility.
        await option.First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        await Assert.That(await option.CountAsync()).IsGreaterThanOrEqualTo(1);
    }

    [When("I pick the design {string} in the create form and name it {string} and save")]
    public async Task WhenPickViaRefSelect(string designLabel, string name)
    {
        var form = ctx.Page!.Locator("main.ide-list .create-form");
        // SelectOptionAsync fires the native change → RefSelect.applyPick → sys.setRef on the draft. NO
        // extra "Set"/"Use" click — the single native pick is the whole bind.
        await form.Locator("select.ref-select").SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Label = designLabel });
        _newInstanceName = name;
        await form.Locator("input.name").FillAsync(name);
        await form.Locator("button.create-save").ClickAsync();
    }

    // ── When: editing a design (on /designs/<id>) ────────────────────────────────

    [When("I rename the type {string} to {string}")]
    public async Task WhenRename(string from, string to)
    {
        await TypeNameInput(from).FillAsync(to);
        // The bound input re-renders the model name to the new value (the client edit landed)…
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.type-card input.type-name')].some(e => e.value === {JsString(to)})");
        // …then wait for the autosave (objectPropChange) to reach the designer's sovereign store, so a
        // later apply projects the renamed design (the apply reads the store fresh).
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaType").Values
            .Any(o => o.Fields.TryGetValue("name", out var v)
                && v is DeEnv.Storage.TextValue t && t.Text == to));
    }

    [When("I retype the prop {string} to {string}")]
    public async Task WhenRetypeProp(string propName, string newType)
    {
        // Renaming a referenced type requires retyping the prop that points at it, so the projected app
        // stays valid (a prop whose `type` names a missing type is rejected at deploy). The prop's type is
        // a <select> (built-in scalars + this design's own types) in the `.prop-row` whose `.prop-name`
        // currently holds `propName`; selecting the target type writes prop.type through the binding.
        await PropTypeSelect(propName).SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Value = newType });
        // The bound select reflects the new value (the client edit landed)…
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.prop-row select.prop-type')].some(e => e.value === {JsString(newType)})");
        // …then wait for the autosave to reach the designer's store, so a later apply projects the
        // retyped prop (the apply reads the store fresh).
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values
            .Any(o => o.Fields.TryGetValue("type", out var v)
                && v is DeEnv.Storage.TextValue t && t.Text == newType));
    }

    [When("I set the prop {string} cardinality to {string}")]
    public async Task WhenSetCardinality(string propName, string cardinality)
    {
        // The cardinality <select> in the prop's row (single / set / dictionary). Selecting an option
        // writes prop.cardinality through the two-way <select> binding and autosaves it. Options come from
        // the system `cardinalities` vocab — their VALUE is the raw word, the visible label is humanized —
        // so select by value. The designer now stores "single" explicitly (so the value matches its
        // dropdown option), hence the stored value is the word itself for every cardinality.
        await PropCardinalitySelect(propName).SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Value = cardinality });
        // Wait for THIS prop's autosave (matched by name, so a same-cardinality prop in another seeded
        // design doesn't satisfy it early), so a later apply projects this prop's new cardinality.
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values
            .Any(o => o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == propName
                && o.Fields.TryGetValue("cardinality", out var v) && v is DeEnv.Storage.TextValue t && t.Text == cardinality));
    }

    [When("I set the prop {string} key type to {string}")]
    public async Task WhenSetKeyType(string propName, string keyType)
    {
        // The key-type field now renders only for a dictionary prop (progressive disclosure); this step
        // runs after the prop's cardinality has been set to dictionary, so the field is present (FillAsync
        // auto-waits for it).
        await PropKeytypeInput(propName).FillAsync(keyType);
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.prop-row input.prop-keytype')].some(e => e.value === {JsString(keyType)})");
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values
            .Any(o => o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == propName
                && o.Fields.TryGetValue("keyType", out var v) && v is DeEnv.Storage.TextValue t && t.Text == keyType));
    }

    [When("I toggle multiline on the prop {string}")]
    public async Task WhenToggleMultiline(string propName)
    {
        // The multiline checkbox in the prop's row (shown only for a single text prop). Check it — the
        // two-way `checked` binding writes prop.multiline = true and autosaves. Wait for THIS prop's
        // autosave (matched by name + multiline) so the designer's store has captured the flag.
        await PropMultilineInput(propName).CheckAsync();
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values
            .Any(o => o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == propName
                && o.Fields.TryGetValue("multiline", out var v) && v is DeEnv.Storage.BoolValue b && b.Value));
    }

    [When("I name the just-added type {string}")]
    public async Task WhenNameJustAddedType(string name)
    {
        // The just-added row is the one whose type-name input is still empty (the client mirrors the model
        // name into the `value` attribute). Fill ITS name input, then wait for the autosave to reach the
        // designer's sovereign store (so a later apply projects the named type). Remember the name so the
        // base-type / values steps can locate the same row once it is no longer the empty one.
        _justAddedTypeName = name;
        await ctx.Page!.Locator(".design-editor .type-card:has(input.type-name[value=\"\"]) input.type-name")
            .First.FillAsync(name);
        await ctx.Page.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.type-card input.type-name')].some(e => e.value === {JsString(name)})");
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaType").Values
            .Any(o => o.Fields.TryGetValue("name", out var v)
                && v is DeEnv.Storage.TextValue t && t.Text == name));
    }

    [When("I set the just-added type's base type to {string}")]
    public async Task WhenSetJustAddedBaseType(string baseType)
    {
        // The kind <select> of the row we just named (located by its now-known name) — Object / Enum,
        // sourced from the system `typeKinds` vocab (option VALUE is the raw word, label humanized), so
        // select by value. For "enum" this flips the projection branch in SchemaBridge; wait for the
        // autosave so a later apply sees it.
        await JustAddedTypeRow().Locator("select.type-kind").SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Value = baseType });
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.type-card select.type-kind')].some(e => e.value === {JsString(baseType)})");
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaType").Values
            .Any(o => o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == _justAddedTypeName
                && o.Fields.TryGetValue("baseType", out var v) && v is DeEnv.Storage.TextValue t && t.Text == baseType));
    }

    [When("I set the just-added type's values to {string}")]
    public async Task WhenSetJustAddedValues(string values)
    {
        // The always-rendered `type-values` input (a comma-separated enum value list). It is meaningful
        // only for an enum; SchemaBridge ignores it on non-enum types. Wait for THIS type's autosave so
        // a later apply projects the value list.
        await JustAddedTypeRow().Locator("input.type-values").FillAsync(values);
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.type-card input.type-values')].some(e => e.value === {JsString(values)})");
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaType").Values
            .Any(o => o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == _justAddedTypeName
                && o.Fields.TryGetValue("values", out var v) && v is DeEnv.Storage.TextValue t && t.Text == values));
    }

    [When("I add a type to the design")]
    public async Task WhenAddType()
    {
        // "Add type" runs addType(design): design.types.add({ name: "", baseType: "object", ... }) -- a
        // journaled add to the design's NESTED types set. The new (empty-name) row appears immediately via
        // the client re-render, first keyed by its transient negative id; the WS persist then remaps it. The
        // next steps may edit/remove the row by that STILL-negative id -- the server resolves it through its
        // per-session transient-id remap (see TransientId.feature), so no wait for the round-trip is needed.
        var before = await ctx.Page!.Locator(".design-editor .type-card").CountAsync();
        await ctx.Page.Locator("button.add-type").ClickAsync();
        await ctx.Page.WaitForFunctionAsync(
            $"() => document.querySelectorAll('.design-editor .type-card').length === {before + 1}");
    }

    [When("I remove the just-added unnamed type")]
    public async Task WhenRemoveUnnamedType() =>
        // The just-added row is the one whose type-name input is still empty (the client mirrors the model
        // name into the `value` attribute, so the attribute selector matches it). Clicking ITS Remove type
        // button drives arrayRemove on the nested types set -- the path that runs the store's GC.
        await ctx.Page!.Locator(".design-editor .type-card:has(input.type-name[value=\"\"]) button.remove-type")
            .First.ClickAsync();

    // ── When: the instance selector (on /instances/<id>) ─────────────────────────

    [When("I pick the design {string} in the dropdown")]
    public async Task WhenPickDesign(string designLabel)
    {
        // Pick the option whose visible label is the design's name (its value is the design id). The
        // onchange binding writes the picked id back to the selector's state and re-renders, so the Apply
        // button below resolves to the newly-picked design.
        await ctx.Page!.Locator("select.design-pick").SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Label = designLabel });
        // The selection lands (the bound state reflects the new pick) before we apply.
        await ctx.Page.WaitForFunctionAsync(
            $"() => {{ const s = document.querySelector('select.design-pick'); return s != null && s.options[s.selectedIndex] != null && s.options[s.selectedIndex].textContent.trim() === {JsString(designLabel)}; }}");
    }

    [When("I apply the design")]
    public async Task WhenApply() =>
        await ctx.Page!.Locator("button.apply-design").ClickAsync();

    // ── The per-row kebab (overflow) actions menu on the instances list ──────────

    [Then("the instance {string} row actions are hidden behind a kebab")]
    public async Task ThenRowActionsBehindKebab(string label)
    {
        // The row carries a single "⋯" toggle in its trailing actions cell (the generic <SetTable>'s
        // rowActions slot), and the menu items (Open/Clone/Delete) start HIDDEN — they live in the DOM
        // (the menu container is always rendered, only toggled by a class), so this asserts hidden, not
        // absent. Proves the actions are consolidated behind the kebab rather than spread across columns.
        var row = RowFor(label);
        await Assert.That(await row.Locator("td.row-action button.kebab-toggle").CountAsync()).IsEqualTo(1);
        await row.Locator(".kebab-menu a.open-instance").WaitForAsync(Hidden);
        await row.Locator(".kebab-menu button.clone-instance").WaitForAsync(Hidden);
        await row.Locator(".kebab-menu button.delete-instance").WaitForAsync(Hidden);
    }

    [When("I open the actions menu for instance {string}")]
    public async Task WhenOpenActionsMenu(string label) =>
        // Click the row's "⋯" toggle — the component flips its own open state and re-renders, so the
        // menu (class .kebab-menu.open) becomes visible. State is keyed to this row's slot, so only this
        // row's menu opens.
        await RowFor(label).Locator("td.row-action button.kebab-toggle").ClickAsync();

    [Then("the instance {string} actions menu shows Open, Clone, and Delete")]
    public async Task ThenActionsMenuShowsAll(string label)
    {
        // Opened, the LIST menu reveals Open / Clone / Delete (gathered in one place). Rename is NOT here
        // — <SetTable> owns the name cell, so inline in-row rename can't be driven from a rowActions cell;
        // rename lives on the detail page. WaitForAsync (default: Visible) proves each is displayed.
        var menu = RowFor(label).Locator(".kebab-menu.open");
        await menu.Locator("a.open-instance").WaitForAsync();
        await menu.Locator("button.clone-instance").WaitForAsync();
        await menu.Locator("button.delete-instance").WaitForAsync();
        await Assert.That(await menu.Locator("button.rename-instance").CountAsync()).IsEqualTo(0);
    }

    [Then("the instance {string} actions menu stays closed")]
    public async Task ThenActionsMenuClosed(string label)
    {
        // Opening one row's kebab must NOT open another's — each row's menu has independent state keyed
        // to its instance identity. So this row has no .open menu and its items stay hidden.
        var row = RowFor(label);
        await Assert.That(await row.Locator(".kebab-menu.open").CountAsync()).IsEqualTo(0);
        await row.Locator(".kebab-menu button.delete-instance").WaitForAsync(Hidden);
    }

    // ── The same kebab on the instance DETAIL page (/instances/<id>) — no Open item ──

    [When("I open the actions menu on the instance page")]
    public async Task WhenOpenActionsMenuOnDetail() =>
        // The detail page carries the SAME instanceActions component in its head; click its "⋯" toggle.
        await ctx.Page!.Locator("main.ide-instance .kebab button.kebab-toggle").ClickAsync();

    [Then("the instance page actions menu has no Open item")]
    public async Task ThenDetailMenuHasNoOpen()
    {
        // The component is called with showOpen=false here, so the Open item is not in the tree at all
        // (the only place "Open" would point is this very page). The menu IS open and still offers the
        // other actions — assert one is visible to prove the menu opened, and that Open is absent.
        var menu = ctx.Page!.Locator("main.ide-instance .kebab-menu.open");
        await menu.Locator("button.rename-instance").WaitForAsync();
        await Assert.That(await menu.Locator("a.open-instance").CountAsync()).IsEqualTo(0);
    }

    [When("I choose Rename from the instance page kebab")]
    public async Task WhenChooseRenameOnDetail() =>
        // Rename in the detail kebab runs the same start-rename handler, setting renameId to this instance.
        await ctx.Page!.Locator("main.ide-instance .kebab-menu.open button.rename-instance").ClickAsync();

    [Then("the instance page shows the inline rename editor")]
    public async Task ThenDetailShowsRenameEditor()
    {
        // start-rename flips the page head to its inline rename conditional (input + Save + Cancel), the
        // same pattern as the list row. While renaming, the head's .instance-app name span is gone.
        var head = ctx.Page!.Locator("main.ide-instance .instance-head");
        await head.Locator("input.rename-input").WaitForAsync();
        await Assert.That(await head.Locator("button.rename-save").CountAsync()).IsEqualTo(1);
        await Assert.That(await head.Locator("button.rename-cancel").CountAsync()).IsEqualTo(1);
    }

    // ── When: deleting a design (the two-step inline confirm) ────────────────────

    [When("I click Delete on the design {string}")]
    public async Task WhenClickDelete(string label) =>
        // The plain (un-armed) Delete button on the design's action cell. Clicking it does NOT remove the
        // design; it arms the inline confirm (sets the designer's `confirmDeleteId` ui var to this design's
        // id), so the row re-renders to show Delete? [Yes] [Cancel].
        await DesignRowFor(label).Locator("button.delete-design").ClickAsync();

    [When("I cancel the delete of the design {string}")]
    public async Task WhenCancelDelete(string label) =>
        // The Cancel button in the armed confirm clears `confirmDeleteId`, restoring the plain Delete.
        await DesignRowFor(label).Locator("button.delete-cancel").ClickAsync();

    [When("I confirm the delete of the design {string}")]
    public async Task WhenConfirmDelete(string label) =>
        // The Yes button in the armed confirm runs db.designs.remove(d) — a journaled mutation that drops
        // the design (and persists over the WS, running the store GC).
        await DesignRowFor(label).Locator("button.delete-yes").ClickAsync();

    // ── When: a non-existent design id ───────────────────────────────────────────

    [When("I open a non-existent design")]
    public async Task WhenOpenMissingDesign()
    {
        // Navigate straight to a design-editor URL whose id resolves to no design in db.designs (a high id
        // that the seeded library never reaches). The editor page renders its heading + Back link, then a
        // not-found message because the foreach finds no match.
        await ctx.Page!.GotoReadyAsync(ctx.DesignerUrl("/designs/999999"));
        await ctx.Page!.WaitForSelectorAsync("main.ide-design-edit");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    // ── Then ────────────────────────────────────────────────────────────────────

    [Then("the designs list shows a design {string}")]
    public async Task ThenDesignsListShows(string label) =>
        // The designs list renders via the generic <SetTable>: each design is a .set-row whose label
        // is the stretched a.row-link (label-only column), with an Edit link + Delete button per row.
        await Assert.That(await ctx.Page!.Locator($".set-row a.row-link:text-is({CssString(label)})").CountAsync())
            .IsGreaterThanOrEqualTo(1);

    [Then("the design {string} row has an Edit link and a Delete button")]
    public async Task ThenDesignRowHasActions(string label)
    {
        // The action cell is the SetTable's per-row `rowActions` slot (the designer's `designActions`
        // local fn): an Edit link to /designs/<id> + a Delete button. Asserting both render proves the
        // SetTable `rowActions` opt-in works through tag-invocation AND that the page hydrated (the
        // WhenOpenDesignsList step already gated on window.initUi).
        var row = DesignRowFor(label);
        var edit = row.Locator("a.edit-design");
        await Assert.That(await edit.CountAsync()).IsEqualTo(1);
        var href = await edit.GetAttributeAsync("href") ?? "";
        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(href, @"/designs/[0-9]+$")).IsTrue();
        await Assert.That(await row.Locator("button.delete-design").CountAsync()).IsEqualTo(1);
    }

    // ── Then: the single create control is the generic New (the blocker fix) ─────

    [Then("the designs list shows the generic SetTable New as its only create control")]
    public async Task ThenListHasGenericNew()
    {
        // The designs list now uses the generic create: the SetTable's own "New " button is present and is
        // the SINGLE create affordance. (Its create form is hidden until clicked, so .create-form is not
        // shown on load.)
        await Assert.That(await ctx.Page!.Locator("main.ide-designs .new-btn").CountAsync()).IsEqualTo(1);
        await Assert.That(await ctx.Page!.Locator("main.ide-designs .create-form").CountAsync()).IsEqualTo(0);
    }

    [Then("the designs list does not show a bespoke Add box")]
    public async Task ThenListNoBespokeAdd()
    {
        // The old bespoke .new-design "Add" box (a label input + an Add button) is gone — the generic New
        // is the only create control, so neither the box nor its Add button is in the DOM.
        await Assert.That(await ctx.Page!.Locator("main.ide-designs .new-design").CountAsync()).IsEqualTo(0);
        await Assert.That(await ctx.Page!.Locator("main.ide-designs button.add-design").CountAsync()).IsEqualTo(0);
    }

    [When("I reveal the generic create form")]
    public async Task WhenRevealCreateForm()
    {
        // Click the SetTable's "New " button to reveal its create form (the table → create-form swap).
        await ctx.Page!.Locator("main.ide-designs .new-btn").ClickAsync();
        await ctx.Page.WaitForSelectorAsync("main.ide-designs .create-form");
    }

    [Then("the create form shows no code-section textareas")]
    public async Task ThenCreateFormNoCodeSections()
    {
        // The designs list's createForm slot renders a LABEL-ONLY field, so the create form must NOT expose
        // a Design's code sections (ui/common/initialData) — neither as the editor's <textarea>s nor as the
        // default all-scalars form's raw <input>s for those props. Their absence proves the slot replaced
        // the default per-scalar form (which WOULD render them).
        await Assert.That(await ctx.Page!.Locator("main.ide-designs .create-form textarea").CountAsync()).IsEqualTo(0);
        await Assert.That(await ctx.Page!.Locator(
            "main.ide-designs .create-form input.ui, main.ide-designs .create-form input.common, main.ide-designs .create-form input.initialData").CountAsync()).IsEqualTo(0);
    }

    // ── Then: Edit/Delete are clickable (no whole-row overlay) ───────────────────

    [Then("the design {string} Edit link receives the click")]
    public async Task ThenEditClickable(string label) =>
        // A trial click performs ALL of Playwright's actionability checks — including that THIS element (not
        // an overlay) would receive the event — WITHOUT actually clicking. It throws if the row-link overlay
        // sits over the Edit link. Passing proves the action-managed table suppresses the overlay, so the
        // band-aid z-index rule is unnecessary.
        await DesignRowFor(label).Locator("a.edit-design").ClickAsync(new() { Trial = true });

    [Then("the design {string} Delete button receives the click")]
    public async Task ThenDeleteClickable(string label) =>
        // Same hit-test for the always-visible Delete button: it must receive the click, not the overlay
        // (a mis-click on a stretched overlay would navigate to the editor — or worse, the overlay over the
        // button would let the row-link swallow a Delete). Trial = actionability only, no real click.
        await DesignRowFor(label).Locator("button.delete-design").ClickAsync(new() { Trial = true });

    // ── Then: the two-step delete confirm ────────────────────────────────────────

    [Then("the design {string} shows a delete confirmation")]
    public async Task ThenShowsConfirm(string label)
    {
        // Armed, the action cell shows the confirm: a Yes and a Cancel button (replacing the plain Delete).
        var row = DesignRowFor(label);
        await row.Locator("button.delete-yes").WaitForAsync();
        await Assert.That(await row.Locator("button.delete-yes").CountAsync()).IsEqualTo(1);
        await Assert.That(await row.Locator("button.delete-cancel").CountAsync()).IsEqualTo(1);
        // The plain (un-armed) Delete is gone while armed.
        await Assert.That(await row.Locator("button.delete-design").CountAsync()).IsEqualTo(0);
    }

    [Then("the design {string} shows no delete confirmation")]
    public async Task ThenShowsNoConfirm(string label)
    {
        // Cancelled, the row reconciles back to the plain Delete with no Yes/Cancel.
        var row = DesignRowFor(label);
        await row.Locator("button.delete-design").WaitForAsync();
        await Assert.That(await row.Locator("button.delete-design").CountAsync()).IsEqualTo(1);
        await Assert.That(await row.Locator("button.delete-yes").CountAsync()).IsEqualTo(0);
        await Assert.That(await row.Locator("button.delete-cancel").CountAsync()).IsEqualTo(0);
    }

    [Then("the design {string} is still listed")]
    public async Task ThenStillListed(string label) =>
        // Clicking the plain Delete (and Cancel) must NOT remove the design — only Yes does.
        await Assert.That(await ctx.Page!.Locator($".set-row a.row-link:text-is({CssString(label)})").CountAsync())
            .IsGreaterThanOrEqualTo(1);

    [Then("the designs list eventually drops the design {string}")]
    public async Task ThenEventuallyDropped(string label)
    {
        // Yes runs db.designs.remove(d) — the row disappears client-side (the re-render), and the WS persist
        // commits it to the designer's sovereign store (GC included). Confirm both: the row leaves the DOM…
        await ctx.Page!.Locator($".set-row:has(a.row-link:text-is({CssString(label)}))")
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        // …and the design is gone from the store (no Design object with that label survives).
        await EventuallyAsync(() => !_designer.Store.ReadExtent("Design").Values
            .Any(o => o.Fields.TryGetValue("label", out var v) && v is DeEnv.Storage.TextValue t && t.Text == label));
    }

    // ── Then: nav active-state ───────────────────────────────────────────────────

    [Then("the nav {string} link is active")]
    public async Task ThenNavActive(string label) =>
        await Assert.That(await ctx.Page!.Locator($"nav.ide-nav a.is-active:text-is({CssString(label)})").CountAsync())
            .IsEqualTo(1);

    [Then("the nav {string} link is not active")]
    public async Task ThenNavNotActive(string label) =>
        await Assert.That(await ctx.Page!.Locator($"nav.ide-nav a:not(.is-active):text-is({CssString(label)})").CountAsync())
            .IsEqualTo(1);

    // ── Then: a non-existent design id ───────────────────────────────────────────

    [Then("the design editor shows a not-found message")]
    public async Task ThenEditorNotFound() =>
        await ctx.Page!.WaitForSelectorAsync("main.ide-design-edit .not-found");

    [Then("the design editor keeps its Back link")]
    public async Task ThenEditorKeepsBack() =>
        await Assert.That(await ctx.Page!.Locator("main.ide-design-edit a.back").CountAsync()).IsEqualTo(1);

    [Then("the design editor shows a type named {string}")]
    public async Task ThenEditorShowsType(string name) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.design-editor .type-card input.type-name')].some(e => e.value === {JsString(name)})");

    [Then("the design editor shows the design's label {string}")]
    public async Task ThenEditorShowsLabel(string label) =>
        // The editor's label is now an editable two-way-bound <input> (input.design-label = design.label);
        // a freshly-created design opens here with its label and otherwise-empty fields (an empty types
        // list, empty code areas) — a valid library entry, only invalid to DEPLOY until it gains types.
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const e = document.querySelector('.design-editor input.design-label'); return e != null && e.value === {JsString(label)}; }}");

    // ── Then/When: the editable design label (rename in the editor) ──────────────

    [When("I rename the design's label to {string}")]
    public async Task WhenRenameDesignLabel(string newLabel)
    {
        // The editor's label input is two-way-bound to design.label; filling it edits the model and
        // autosaves a journaled scalar change (objectPropChange) to the designer's sovereign store.
        await ctx.Page!.Locator(".design-editor input.design-label").FillAsync(newLabel);
        await ctx.Page.WaitForFunctionAsync(
            $"() => {{ const e = document.querySelector('.design-editor input.design-label'); return e != null && e.value === {JsString(newLabel)}; }}");
        // Wait for the autosave to reach the store, so a fresh server render (a reload) shows the new label.
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values
            .Any(o => o.Fields.TryGetValue("label", out var v) && v is DeEnv.Storage.TextValue t && t.Text == newLabel));
    }

    [When("I reload the design editor")]
    public async Task WhenReloadEditor()
    {
        // A fresh server render of the SAME editor URL (the design's label comes from the store), so the
        // input's value is the persisted label — proving the rename survived as data, not just in the DOM.
        await ctx.Page!.ReloadAsync();
        await ctx.Page.WaitForSelectorAsync("main.ide-design-edit .design-editor");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [Then("the design editor's label input holds {string}")]
    public async Task ThenEditorLabelInputHolds(string label) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const e = document.querySelector('.design-editor input.design-label'); return e != null && e.value === {JsString(label)}; }}");

    // ── When/Then: the Commit-button UX slice (M13's last piece) ─────────────────

    [When("I type {string} into the commit message")]
    public async Task WhenTypeCommitMessage(string message)
        => await ctx.Page!.Locator(".design-editor input.commit-message").FillAsync(message);

    // Just clicks — the commit message is NEVER cleared client-side (a UX review fix: a synchronous
    // clear both faked "done" before the server ack and destroyed the typed message on a rejected
    // commit), so this step makes no assumption about success. The positive confirmation is the
    // "Last commit:" line (updates on the success ack's refetch); a rejection surfaces as the global
    // error banner with the input untouched. Callers assert whichever leg they are testing.
    [When("I click Commit")]
    public async Task WhenClickCommit() =>
        await ctx.Page!.Locator(".design-editor button.commit-design").ClickAsync();

    // The positive confirmation: the design editor's "Last commit:" line is pure Code reading the
    // design's main branch head, so it updates only once the success ack's refetch lands (ws.ts:947) —
    // poll, no fixed sleep.
    [Then("the last-commit line eventually shows message {string}")]
    public async Task ThenLastCommitLineShowsMessage(string message) =>
        await ctx.Page!.Locator($".design-editor p.last-commit:has-text({CssString("\"" + message + "\"")})").WaitForAsync();

    // The bare-text variant (no quote-wrapping) — used for the "(no message)" placeholder, which the
    // Code renders WITHOUT the quote marks (only a real message gets wrapped in quotes).
    [Then("the last-commit line eventually shows {string}")]
    public async Task ThenLastCommitLineShowsText(string text) =>
        await ctx.Page!.Locator($".design-editor p.last-commit:has-text({CssString(text)})").WaitForAsync();

    [Then("the global error banner is shown mentioning {string}")]
    public async Task ThenGlobalErrorBannerMentioning(string phrase)
    {
        var banner = ctx.Page!.Locator("#__error");
        await banner.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 10000 });
        await Assert.That(await banner.InnerTextAsync()).Contains(phrase);
    }

    [Then("the commit message input still holds {string}")]
    public async Task ThenCommitMessageInputStillHolds(string message) =>
        await Assert.That(await ctx.Page!.Locator(".design-editor input.commit-message").InputValueAsync())
            .IsEqualTo(message);

    // Host-action success callback (docs/plans/host-action-success-signal.md) — the commit bar's
    // afterCommit clears commitMessage on the ok reply's refetch, which lands asynchronously (poll,
    // don't assert immediately after the click).
    [Then("the commit message input eventually holds {string}")]
    public async Task ThenCommitMessageInputEventuallyHolds(string message) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const e = document.querySelector('.design-editor input.commit-message'); return e != null && e.value === {JsString(message)}; }}");

    [Then("the migration textarea eventually holds {string}")]
    public async Task ThenMigrationTextareaEventuallyHolds(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const e = document.querySelector('.design-editor textarea.commit-migration-input'); return e != null && e.value === {JsString(text)}; }}");

    // The rejection leg's retained-migration proof: the callback never ran, so the textarea still
    // holds exactly what "I type a migration for ... into the migration textarea" typed.
    [Then("the migration textarea still holds the migration for {string}")]
    public async Task ThenMigrationTextareaStillHoldsMigrationFor(string typeName) =>
        await Assert.That(await ctx.Page!.Locator(".design-editor textarea.commit-migration-input").InputValueAsync())
            .IsEqualTo($"fn {typeName}(old)\n    new.text = old.text");

    [When("I open the commit history")]
    public async Task WhenOpenCommitHistory()
    {
        await ctx.Page!.Locator(".design-editor a.view-history").ClickAsync();
        await ctx.Page.WaitForSelectorAsync("main.ide-commits");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [Then("the commit history shows a commit with message {string}")]
    public async Task ThenCommitHistoryShowsMessage(string message) =>
        await ctx.Page!.Locator($"main.ide-commits .set-row:has-text({CssString(message)})").WaitForAsync();

    // An empty-message commit still creates a real row (the label column — Commit.message, the type's
    // labelProp — renders empty text), so assert the store directly: a Commit exists whose message is "".
    // The row IS in the DOM (a .set-row per member — SetTable never skips a member for an empty label),
    // just with no visible text to locate it by, so the browser-visible proof is the row COUNT increasing.
    [Then("the commit history shows a commit with an empty message")]
    public async Task ThenCommitHistoryShowsEmptyMessage() =>
        await EventuallyAsync(() => _designer.Store.ReadExtent("Commit").Values
            .Any(o => o.Fields.TryGetValue("message", out var v) && v is DeEnv.Storage.TextValue { Text: "" }));

    // UX review FIX 2 (newest-first): the FIRST row in the table (the generic SetTable's iteration
    // order, now driven by commitsPage's orderBy descending on logSeq) carries the given message.
    [Then("the commit history's first row has message {string}")]
    public async Task ThenCommitHistoryFirstRowHasMessage(string message) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const r = document.querySelector('main.ide-commits .set-row'); return r != null && r.textContent.includes({JsString(message)}); }}");

    // B1: a history row is now a real link (linked restored) — clicking it navigates client-side to the
    // commit-detail page (/commits/<id>). Locate the row by its message and click its row-link.
    [When("I open the commit {string} from the history")]
    public async Task WhenOpenCommitFromHistory(string message)
    {
        await ctx.Page!.Locator($"main.ide-commits .set-row a.row-link:text-is({CssString(message)})").ClickAsync();
        await ctx.Page.WaitForSelectorAsync("main.ide-commit-detail");
    }

    [When("I open the commit detail for {string}")]
    public async Task WhenOpenCommitDetailFor(string message)
    {
        var commitId = _designer.Store.ReadExtent("Commit")
            .Where(kv => kv.Value.Fields.TryGetValue("message", out var v)
                && v is DeEnv.Storage.TextValue t && t.Text == message)
            .OrderByDescending(kv => kv.Value.Fields.TryGetValue("logSeq", out var v) && v is DeEnv.Storage.IntValue i ? i.Value : 0)
            .Select(kv => kv.Key)
            .First();
        var page = ctx.Page!;
        await page.GotoReadyAsync(ctx.DesignerUrl($"/commits/{commitId}"));
        await page.WaitForSelectorAsync("main.ide-commit-detail");
    }

    // B1: the commit-detail page renders each field as a .commit-field with a .field-value; a field-value
    // equal to the message/design proves the right commit resolved (values are distinct across fields).
    [Then("the commit detail page shows message {string}")]
    public async Task ThenCommitDetailShowsMessage(string message) =>
        await ctx.Page!.Locator($"main.ide-commit-detail .field-value:text-is({CssString(message)})").WaitForAsync();

    [Then("the commit detail page shows design {string}")]
    public async Task ThenCommitDetailShowsDesign(string design) =>
        await ctx.Page!.Locator($"main.ide-commit-detail .field-value:text-is({CssString(design)})").WaitForAsync();

    [Then("the commit detail page shows author {string}")]
    public async Task ThenCommitDetailShowsAuthor(string author) =>
        await ctx.Page!.Locator($"main.ide-commit-detail .commit-field:has(.field-label:text-is({CssString("By")})) .field-value:text-is({CssString(author)})").WaitForAsync();

    // Review fix 5 — the textarea→commitDesign→detail round-trip. The Migration input lives inside a
    // collapsed-by-default <details class="commit-migration">; click its <summary> to expand before
    // the textarea is fill-able (Playwright refuses to type into a hidden element).
    [When("I expand the Migration disclosure")]
    public async Task WhenExpandMigrationDisclosure() =>
        await ctx.Page!.Locator(".design-editor details.commit-migration summary").ClickAsync();

    [When("I type a migration for {string} into the migration textarea")]
    public async Task WhenTypeMigrationTextarea(string typeName) =>
        await ctx.Page!.Locator(".design-editor textarea.commit-migration-input")
            .FillAsync($"fn {typeName}(old)\n    new.text = old.text");

    [Then("the commit detail page shows the migration source for {string}")]
    public async Task ThenCommitDetailShowsMigration(string typeName) =>
        await ctx.Page!.Locator(
            $"main.ide-commit-detail .commit-migration-text:has-text({CssString($"fn {typeName}(old)")})").WaitForAsync();

    // B2 — the "Changes since parent" section. sys.diffCommits(parent, this) is a server-backed READ builtin
    // (computed server-side, shipped via the memo cache, reused by the client twin — like sys.schema). A
    // rename renders as ONE rename row ("From → To"), the identity-diff payoff — never a remove+add.
    [Then("the changes-since-parent shows a rename from {string} to {string}")]
    public async Task ThenChangesSinceParentRename(string from, string to) =>
        await ctx.Page!.Locator($"main.ide-commit-detail .commit-diff .diff-rename:has-text({CssString(from + " → " + to)})").WaitForAsync();

    // The other half of the rename proof: a renamed type must NOT also surface as a removal — the diff joins
    // by intrinsic id, so the old name never appears in the "Removed" group.
    [Then("the changes-since-parent shows no removal of {string}")]
    public async Task ThenChangesSinceParentNoRemoval(string name)
    {
        // The rename row must be present first (proves the diff section rendered — otherwise "no removal"
        // could pass vacuously on a not-yet-hydrated page).
        await ctx.Page!.Locator("main.ide-commit-detail .commit-diff").WaitForAsync();
        await Assert.That(await ctx.Page!.Locator($".commit-diff .diff-remove:has-text({CssString(name)})").CountAsync())
            .IsEqualTo(0);
    }

    // ── B3 — Publish + dry-run from the designer ──────────────────────────────────

    // Remove a leaf field from a type in the design editor (the prop-row's "×" remove button). Drives
    // arrayRemove on the type's nested props set; wait for the client edit AND the autosave to the designer's
    // store (a later commit snapshots the design, so the removal must have landed).
    [Then("the changes-since-parent shows an add of {string}")]
    public async Task ThenChangesSinceParentAdd(string path) =>
        await ctx.Page!.Locator($"main.ide-commit-detail .commit-diff .diff-add:has-text({CssString(path)})").WaitForAsync();

    [When("I remove the field {string} from the type {string}")]
    public async Task WhenRemoveField(string propName, string typeName)
    {
        var row = ctx.Page!.Locator(
            $".design-editor .type-card:has(input.type-name[value={CssString(typeName)}]) " +
            $".prop-row:has(input.prop-name[value={CssString(propName)}])");
        await row.Locator("button.remove-prop").ClickAsync();
        // The row disappears from the DOM (the prop is gone client-side)…
        await row.WaitForAsync(Hidden);
        // …then the removal reaches the designer's sovereign store (no MetaProp named propName on that type).
        await EventuallyAsync(() => !_designer.Store.ReadExtent("MetaProp").Values
            .Any(o => o.Fields.TryGetValue("name", out var v) && v is DeEnv.Storage.TextValue t && t.Text == propName));
    }

    // Open the toggle-gated Preview for an instance in the design editor's Publish section: click its row's
    // "Preview publish" button, then wait for the report (server-backed read → shipped via the memo cache, so
    // the preview populates after the toggle-driven refetch lands).
    [When("I preview the publish for the instance {string}")]
    public async Task WhenPreviewPublish(string label)
    {
        await PublishRowFor(label).Locator("button.preview-publish").ClickAsync();
        await PublishRowFor(label).Locator(".publish-preview .publish-report").WaitForAsync();
    }

    // The dry-run report must surface a removal in the LOUD destructive class (.publish-remove — red).
    [Then("the publish preview flags {string} as removed loudly")]
    public async Task ThenPreviewFlagsRemoved(string path) =>
        await PublishRowFor("todo").Locator($".publish-preview .publish-remove:has-text({CssString(path)})").WaitForAsync();

    [Then("the publish preview asks me to commit before publishing")]
    public async Task ThenPreviewAsksCommitFirst() =>
        await ctx.Page!.Locator(".publish-preview .publish-blocked:has-text(\"commit before publishing\")").WaitForAsync();

    [Then("the publish preview for the instance {string} shows no Apply button")]
    public async Task ThenPreviewShowsNoApply(string label) =>
        await Assert.That(await PublishRowFor(label).Locator(".publish-preview button.apply-publish").CountAsync()).IsEqualTo(0);

    // The dry-run changed NOTHING: the target instance's own app document still declares the field the
    // designer removed (the preview never republished). A store/file read of the LIVE target's schema.
    [Then("the {string} instance's app document still describes the field {string}")]
    public async Task ThenTargetStillDescribesField(string label, string field)
    {
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await Assert.That(File.ReadAllText(target.Spec.SchemaPath)).Contains(field);
    }

    [Then("the publish preview shows a rename from {string} to {string}")]
    public async Task ThenPreviewShowsRename(string from, string to) =>
        await PublishRowFor("todo").Locator($".publish-preview .publish-rename:has-text({CssString(from + " → " + to)})").WaitForAsync();

    // The preview→apply CONSISTENCY GUARD (addendum): bump the TARGET's own live store version by a direct
    // field write (through the live hosted store — never a second store over its file), simulating "the
    // target's data moved after the preview was taken." A plain re-write of a field to its OWN current value
    // is enough (WriteField bumps CurrentVersion regardless of whether the value actually changed) — the
    // lightest possible version bump, with no schema/design involvement.
    //
    // Also captures the target's ON-DISK log line count right after the bump (NOT the live store's
    // CurrentVersion — see the hardening step below for why that would be a VACUOUS check: the versioned
    // leg's boundary write, JsonFileInstanceStore.ApplyPublishBoundary, is an OFFLINE write straight to the
    // target's DataPath/log file, bypassing the live hosted store entirely until a restart re-opens it — so
    // the live store's CurrentVersion never observes it, guarded or not).
    [Then("the {string} target's data changes since the preview")]
    public async Task ThenTargetDataChangesSincePreview(string label)
    {
        var store = ctx.Kernel!.Instances.Single(i => i.Spec.App == label).Store;
        var (listId, listFields) = store.ReadExtent("TodoList").First();
        var name = listFields.Fields.TryGetValue("name", out var v) && v is DeEnv.Storage.TextValue t ? t.Text : "";
        store.WriteField(listId, "name", new DeEnv.Storage.TextValue(name));
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        _todoTargetLogLinesAfterStaleness = TargetLogLineCount(target.Spec.DataPath);
        await Task.CompletedTask;
    }

    // The rejected apply performed NO side effect: the target's app document still does not describe the
    // renamed type (the guard fired before any file write/stamp/restart).
    [Then("the {string} instance's app document does not describe the type {string}")]
    public async Task ThenTargetDoesNotDescribeType(string label, string typeName)
    {
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await Assert.That(File.ReadAllText(target.Spec.SchemaPath)).DoesNotContain(typeName);
    }

    // Review hardening (M13 Track-B B3 addendum fix): the rejected apply must not have MATERIALIZED the
    // versioned leg's destructive boundary onto the target's DATA file — the class of bug where the guard
    // fired too late (after ApplyPublishBoundary had already written the DataPath + a WAL entry, leaving
    // DataPath migrated but SchemaPath/the stamp/the live store all still on the OLD schema). This checks the
    // ON-DISK log file (never the live store's CurrentVersion — see the note above: an offline boundary write
    // never bumps that) grew by EXACTLY ZERO entries since the staleness bump — the boundary apply's first
    // act is to append a WAL entry BEFORE it rewrites the snapshot, so "the log did not grow at all" is the
    // most direct proof available that NO write of any kind (not even a partial/crashed one) reached the file.
    [Then("the {string} target's data is unchanged by the rejected apply")]
    public async Task ThenTargetDataUnchangedByRejectedApply(string label)
    {
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        var actual = TargetLogLineCount(target.Spec.DataPath);
        await Assert.That(actual).IsEqualTo(_todoTargetLogLinesAfterStaleness);
    }

    private static int TargetLogLineCount(string dataPath)
    {
        var logPath = DeEnv.Storage.AppPaths.LogPathForDataPath(dataPath);
        return File.Exists(logPath) ? File.ReadAllLines(logPath).Length : 0;
    }

    // Apply the previewed publish: click the row's Apply button (or the ConfirmButton's Yes when the report
    // was destructive — a rename is non-destructive, so a plain button; handle both to keep the step general).
    [When("I apply the publish for the instance {string}")]
    public async Task WhenApplyPublish(string label)
    {
        var row = PublishRowFor(label);
        // A destructive report routes Apply through the two-step ConfirmButton; a safe one is a plain button.
        var confirm = row.Locator(".publish-preview .confirm-button .apply-publish");
        if (await confirm.CountAsync() > 0)
        {
            await confirm.ClickAsync();
            await row.Locator(".publish-preview .confirm-button button.delete-yes").ClickAsync();
        }
        else
            await row.Locator(".publish-preview button.apply-publish").ClickAsync();
    }

    // After Apply, the target is published + stamped to the design's head, so a fresh Preview reads "up to
    // date" — the operator-visible success signal (the diff is now empty). The host-action ack ran
    // resetViewState (closing the prior open preview) AND dropped the stale `publishPreview:` read (ws.ts), so
    // the re-opened preview recomputes fresh over the now-stamped target rather than reusing the pre-publish
    // report. Wide window: the re-preview rides a value-not-available refetch.
    [Then("the publish preview for the instance {string} reads up to date")]
    public async Task ThenPreviewReadsUpToDate(string label) =>
        await PublishRowFor(label).Locator(".publish-preview .publish-uptodate").WaitForAsync(
            new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 45000 });

    [Then("the publish row for instance {string} eventually shows {string}")]
    public async Task ThenPublishRowShows(string label, string text) =>
        await ctx.Page!.Locator($".publish-section .last-publish:has-text({CssString(text)})").WaitForAsync();

    // Seed a TodoItem into the LIVE target instance's store (never a second store over its file — the
    // single-store invariant): create the item and add it into the existing TodoList's `items` set.
    [Given("the {string} target holds a TodoItem with text {string}")]
    public async Task GivenTargetHoldsTodoItem(string label, string text)
    {
        var store = ctx.Kernel!.Instances.Single(i => i.Spec.App == label).Store;
        var (listId, listFields) = store.ReadExtent("TodoList").First();
        var itemsSet = listFields.Fields.GetValueOrDefault("items") as DeEnv.Storage.SetValue
            ?? throw new InvalidOperationException("The target TodoList has no `items` set.");
        var itemId = store.CreateObject("TodoItem", new DeEnv.Storage.ObjectValue(new Dictionary<string, DeEnv.Storage.NodeValue>
        {
            ["text"] = new DeEnv.Storage.TextValue(text),
            ["checked"] = new DeEnv.Storage.BoolValue(false),
        }));
        store.AddToSet(itemsSet.Id, itemId);
        _ = listId;
        await Task.CompletedTask;
    }

    // The rename carried the target's data: after the publish + restart, the renamed type "Task" holds the
    // object whose `text` survived. Re-resolve the LIVE hosted instance each poll (restart hot-swaps the
    // store), and read the renamed extent — proving the designer's Publish UI reached the rename-safe publish.
    [Then("the {string} instance eventually holds a {string} with text {string}")]
    public async Task ThenTargetHoldsRenamed(string label, string typeName, string text) =>
        await EventuallyAsync(() =>
        {
            var store = ctx.Kernel!.Instances.Single(i => i.Spec.App == label).Store;
            return store.ReadExtent(typeName).Values.Any(o =>
                o.Fields.TryGetValue("text", out var v) && v is DeEnv.Storage.TextValue t && t.Text == text);
        }, timeoutMs: 45000);

    // ── B4 — branches + merge from the design editor ─────────────────────────────

    // Create a branch: type the name into the Branches-section input, click "+ Branch" (sys.createBranch —
    // a host action), then wait for the branch link to appear via the ack's refetch (the new Branch row is
    // GC-reachable via db.branches, and branchSection lists branches whose workingCopy shares the app's
    // lineage). Poll the DOM for the link — the refetch is async.
    [When("I create a branch named {string}")]
    public async Task WhenCreateBranch(string name)
    {
        await ctx.Page!.Locator(".branch-section input.branch-name").FillAsync(name);
        await ctx.Page.Locator(".branch-section button.create-branch").ClickAsync();
        await BranchLinkFor(name).WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 15000 });
    }

    [Then("the Branches section lists a branch link {string}")]
    public async Task ThenBranchesListsLink(string name) =>
        await BranchLinkFor(name).WaitForAsync();

    // Switch to a branch's editor. A branch working copy is a Design row at its OWN URL (/designs/<wcId>) —
    // "switching branches" is navigation, the settled model. The Branches section renders the branch as a
    // real <a href="/designs/<wcId>"> link (asserted by the "lists a branch link" scenario); here we OPEN
    // that URL directly (a fresh SSR load — a direct visit / reload is navigation too), which gives a clean
    // data-hydrated barrier. This avoids the editor→editor client-side-nav sync problem: both the source and
    // branch editors share `.design-editor`/`.type-card` markup with the same label, so a link CLICK alone
    // cannot be reliably awaited from an already-open editor (the old DOM lingers until the refetch swaps it).
    [When("I open the branch {string} from the Branches section")]
    public async Task WhenOpenBranch(string name)
    {
        var wcId = BranchWorkingCopyId(name);
        var page = ctx.Page!;
        await page.GotoReadyAsync(ctx.DesignerUrl($"/designs/{wcId}"));
        await page.WaitForSelectorAsync("main.ide-design-edit .design-editor .type-card");
        await page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    // Add a field to a named type: click that type-card's "+ Field", then name the just-added (empty-name)
    // prop. Waits for the client edit AND the autosave to the designer's store (a later commit snapshots it).
    [When("I add a field {string} to the type {string}")]
    public async Task WhenAddField(string propName, string typeName)
    {
        var card = ctx.Page!.Locator($".design-editor .type-card:has(input.type-name[value={CssString(typeName)}])");
        await card.Locator("button.add-prop").ClickAsync();
        var newRow = card.Locator(".prop-row:has(input.prop-name[value=\"\"])").First;
        await newRow.Locator("input.prop-name").FillAsync(propName);
        await ctx.Page.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.prop-row input.prop-name')].some(e => e.value === {JsString(propName)})");
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values
            .Any(o => o.Fields.TryGetValue("name", out var v) && v is DeEnv.Storage.TextValue t && t.Text == propName));
    }

    // Rename a prop on a named type via its bound name input; wait for the client edit + the autosave, so a
    // later commit snapshots the renamed prop (the commit reads the store fresh).
    [When("I rename the prop {string} to {string} on the type {string}")]
    public async Task WhenRenameProp(string from, string to, string typeName)
    {
        var input = ctx.Page!.Locator(
            $".design-editor .type-card:has(input.type-name[value={CssString(typeName)}]) " +
            $".prop-row input.prop-name[value={CssString(from)}]");
        await input.FillAsync(to);
        await ctx.Page.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.prop-row input.prop-name')].some(e => e.value === {JsString(to)})");
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values
            .Any(o => o.Fields.TryGetValue("name", out var v) && v is DeEnv.Storage.TextValue t && t.Text == to));
    }

    // Grant a read rule on a type for the branch (reusing the slice-5 store-level access mutation): append an
    // access rule onto the branch working copy's Design record in the designer's own store BEFORE the branch
    // is committed via the UI, so sys.commitDesign snapshots it into the branch head. A store write on the ONE
    // live store (never a second store over the file — the single-store invariant).
    [When("I grant read on {string} to everyone on the branch {string}")]
    public async Task WhenGrantReadOnBranch(string typeName, string branchName)
    {
        var wcId = BranchWorkingCopyId(branchName);
        var current = (_designer.Store.ReadById(wcId)!.Value.Fields.Fields.GetValueOrDefault("access") as DeEnv.Storage.TextValue)?.Text ?? "";
        var body = (current.Length == 0 ? "access\n" : current) + $"    {typeName}\n        read\n";
        _designer.Store.WriteField(wcId, "access", new DeEnv.Storage.TextValue(body));
        await Task.CompletedTask;
    }

    // Open the toggle-gated merge Preview for a branch: click its "Preview merge", then wait for the report
    // (server-backed read → shipped via the memo cache, so it populates after the toggle-driven refetch).
    [When("I preview the merge of branch {string}")]
    public async Task WhenPreviewMerge(string name)
    {
        await MergeRowFor(name).Locator("button.preview-merge").ClickAsync();
        await MergeRowFor(name).Locator(".merge-preview .merge-report").WaitForAsync(
            new Microsoft.Playwright.LocatorWaitForOptions { Timeout = 15000 });
    }

    [Then("the merge preview reports a clean merge")]
    public async Task ThenMergePreviewClean() =>
        await ctx.Page!.Locator(".merge-preview .merge-clean").WaitForAsync();

    [Then("the merge preview reports already up to date")]
    public async Task ThenMergePreviewUpToDate() =>
        await ctx.Page!.Locator(".merge-preview .merge-uptodate").WaitForAsync();

    [Then("the Branches section eventually shows {string}")]
    public async Task ThenBranchesShows(string text) =>
        await ctx.Page!.Locator($".branch-section:has-text({CssString(text)})").WaitForAsync();

    // The conflict's source row is labeled with the SOURCE BRANCH's real name (review fix — "source:"/
    // "target:" named the internal marker, not a branch, so the UI now reads "<branchName>: <value>" /
    // "this design: <value>"); the branch under test is always "feature" here.
    [Then("the merge preview shows a conflict with source {string} and target {string}")]
    public async Task ThenMergeConflictSourceTarget(string source, string target)
    {
        await ctx.Page!.Locator($".merge-conflict:has(.merge-conflict-source:text-is({CssString("feature: " + source)})) " +
            $".merge-conflict-target:text-is({CssString("this design: " + target)})").WaitForAsync();
    }

    // Apply is gated until every conflict is resolved: with an unresolved conflict, no .merge-apply button
    // renders (a "resolve every conflict" hint shows instead).
    [Then("the merge preview shows no Merge button")]
    public async Task ThenNoMergeButton() =>
        await Assert.That(await ctx.Page!.Locator(".merge-preview button.merge-apply").CountAsync()).IsEqualTo(0);

    // Pick "source" for the first (only) conflict — clicks its Take source button, which accumulates the pick
    // in the merge component's client state; the Apply button then appears (all conflicts resolved).
    [When("I take source for the first conflict")]
    public async Task WhenTakeSourceFirstConflict()
    {
        await ctx.Page!.Locator(".merge-conflict button.take-source").First.ClickAsync();
        await ctx.Page.Locator(".merge-preview button.merge-apply").WaitForAsync();
    }

    // Apply the previewed merge: click the row's Merge button (sys.mergeBranch with the assembled
    // resolutions). On the ack's refetch the merge commit lands and the target working copy carries the merge.
    [When("I apply the merge of branch {string}")]
    public async Task WhenApplyMerge(string name) =>
        await MergeRowFor(name).Locator(".merge-preview button.merge-apply").ClickAsync();

    [Then("the merge preview's access block mentions {string}")]
    public async Task ThenMergeAccessMentions(string phrase) =>
        await ctx.Page!.Locator($".merge-preview .merge-access .merge-access-row:has-text({CssString(phrase)})").WaitForAsync();

    // A prop by name is stored on a named type of the design in db.designs (the MAIN working copy, NOT a
    // branch clone — a clone shares the label, so scope to the design reachable from db.designs).
    [Then("the design {string} eventually has a stored prop named {string} on {string}")]
    public async Task ThenDesignHasStoredPropOnType(string designLabel, string propName, string typeName) =>
        await EventuallyAsync(() =>
        {
            var designsSet = _designer.Store.ReadNode(DeEnv.Storage.NodePath.Root.Field("designs")) as DeEnv.Storage.SetValue;
            if (designsSet is null) return false;
            var metaTypes = _designer.Store.ReadExtent("MetaType");
            var metaProps = _designer.Store.ReadExtent("MetaProp");
            foreach (var designId in designsSet.Members.Keys)
            {
                var design = _designer.Store.ReadById(designId);
                if (design is null || design.Value.TypeName != "Design") continue;
                if (design.Value.Fields.Fields.GetValueOrDefault("label") is not DeEnv.Storage.TextValue { Text: var lbl } || lbl != designLabel) continue;
                if (design.Value.Fields.Fields.GetValueOrDefault("types") is not DeEnv.Storage.SetValue typesSet) continue;
                foreach (var typeId in typesSet.Members.Keys)
                {
                    if (!metaTypes.TryGetValue(typeId, out var mt)) continue;
                    if (mt.Fields.GetValueOrDefault("name") is not DeEnv.Storage.TextValue { Text: var tn } || tn != typeName) continue;
                    if (mt.Fields.GetValueOrDefault("props") is not DeEnv.Storage.SetValue propsSet) continue;
                    if (propsSet.Members.Keys.Any(pid => metaProps.TryGetValue(pid, out var mp)
                        && mp.Fields.GetValueOrDefault("name") is DeEnv.Storage.TextValue { Text: var pn } && pn == propName))
                        return true;
                }
            }
            return false;
        }, timeoutMs: 30000);

    // The branch working copy's Design id, resolved from the designer's store: a Branch named `name` whose
    // workingCopy reference names a Design row.
    private int BranchWorkingCopyId(string name)
    {
        foreach (var (_, branch) in _designer.Store.ReadExtent("Branch"))
            if (branch.Fields.GetValueOrDefault("name") is DeEnv.Storage.TextValue { Text: var bn } && bn == name
                && branch.Fields.GetValueOrDefault("workingCopy") is DeEnv.Storage.ReferenceValue { TargetId: { } wcId })
                return wcId;
        throw new InvalidOperationException($"No branch named '{name}' with a working copy in the designer store.");
    }

    // The Branches-section link for a branch (its <a class="branch-link"> naming the branch).
    private Microsoft.Playwright.ILocator BranchLinkFor(string name) =>
        ctx.Page!.Locator($".branch-section .branch-list a.branch-link:text-is({CssString(name)})");

    // The merge row for a branch in the Branches section (its head shows Merge "<name>").
    private Microsoft.Playwright.ILocator MergeRowFor(string name) =>
        ctx.Page!.Locator($".branch-section .branch-row:has(.branch-link:has-text({CssString("Merge \"" + name + "\"")}))");

    // The design editor's Publish-section row for an instance, located by its target name.
    private Microsoft.Playwright.ILocator PublishRowFor(string label) =>
        ctx.Page!.Locator($".publish-section .publish-row:has(.publish-target:text-is({CssString(label)}))");

    // B1 ride-along: the newest-first FIRST row's label cell is a real <a class="row-link"> (linked
    // restored) whose text is the "(no <humanized labelProp>)" placeholder — "(no Message)" here (the
    // generic empty-label fallback humanizes the prop name, matching the library convention) — proving both
    // that the row links and that an empty message is not a phantom empty anchor.
    [Then("the commit history's first row link reads {string}")]
    public async Task ThenCommitHistoryFirstRowLinkReads(string text)
    {
        var link = ctx.Page!.Locator("main.ide-commits .set-row").First.Locator("a.row-link");
        await link.WaitForAsync();
        await Assert.That((await link.InnerTextAsync()).Trim()).IsEqualTo(text);
    }

    [Then("the design {string} has a stored type named {string}")]
    public async Task ThenDesignHasStoredType(string designLabel, string typeName)
    {
        // The type added on the edit page (a nested types.add) persists to the designer's sovereign store
        // with a real positive id (the nested object round-tripped through its OWN arrayAdd, not the create
        // draft's). Confirm a MetaType named `typeName` exists, reachable from the named design's types set.
        await EventuallyAsync(() =>
        {
            var design = _designer.Store.ReadExtent("Design").Values.FirstOrDefault(o =>
                o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue lt && lt.Text == designLabel);
            if (design is null || !design.Fields.TryGetValue("types", out var tv) || tv is not DeEnv.Storage.SetValue set)
                return false;
            var metaTypes = _designer.Store.ReadExtent("MetaType");
            return set.Members.Keys.Any(id => metaTypes.TryGetValue(id, out var mt)
                && mt.Fields.TryGetValue("name", out var nv) && nv is DeEnv.Storage.TextValue nt && nt.Text == typeName);
        });
    }

    [Then("the design editor shows the design's UI text in a textarea")]
    public async Task ThenEditorShowsUiText() =>
        // The design's `ui` section text is bound into the code-area <textarea> (a real multi-line
        // editor); the seeded design's UI is a custom `fn render()`, so its text contains "fn render".
        await ctx.Page!.WaitForFunctionAsync(
            "() => { const e = document.querySelector('.design-editor textarea.design-ui'); return e != null && e.value.includes('fn render'); }");


    [When("I expand the Advanced code disclosure")]
    public async Task WhenExpandAdvancedCode() =>
        await ctx.Page!.Locator(".design-editor details.code-areas summary").ClickAsync();

    [When("I type this access section:")]
    public async Task WhenTypeAccessSection(string accessSection)
    {
        await ctx.Page!.Locator(".design-editor textarea.design-access").FillAsync(accessSection);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "todo" }
            && o.Fields.TryGetValue("access", out var av) && av is DeEnv.Storage.TextValue at && at.Text == accessSection));
    }

    // ── M12 X2b — the Convert-to-structured button + the structured render view ──────────────────

    // A SIMPLE convertible render: a `fn render()` returning a `<main>` with an attribute and a nested
    // element whose child is a text literal — the exact shape S1b's ImportRender accepts (no foreach / if /
    // helper components, which it refuses). Filled into the editable `ui` textarea (bound to
    // sys.field(design,"ui"), a journaled scalar autosave like the access textarea); polled on the store so
    // the write has landed before we convert.
    // The stored `ui` field carries the `ui` SECTION (header + indented body) — the exact text
    // AppPrint.PrintUi emits and SchemaBridge.ImportRender re-parses via ParseUiSection (which expects the
    // `ui` header). So author the whole section, render body indented under `fn render()`.
    private const string SimpleConvertibleRender =
        "ui\n    fn render()\n        return <main class=\"greeting\">\n            <h1>\n                \"Hi\"\n";

    [When("I author a simple convertible render into the design's UI")]
    public async Task WhenAuthorSimpleRender()
    {
        await ctx.Page!.Locator(".design-editor textarea.design-ui").FillAsync(SimpleConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "convertme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == SimpleConvertibleRender));
    }

    // The convert button lives inside the Advanced (code) <details> disclosure. Its open/closed state is
    // uncontrolled DOM (not model-bound), so the autosave re-render after authoring the render rebuilds the
    // disclosure CLOSED — the button is present but hidden. Assert it is ATTACHED (the mode-conditional
    // rendered it), independent of the disclosure's transient open state.
    [Then("the design editor shows the Convert-to-structured button")]
    public async Task ThenShowsConvertButton() =>
        await ctx.Page!.Locator(".design-editor button.convert-render")
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });

    [When("I click Convert to structured")]
    public async Task WhenClickConvert()
    {
        // The convert button lives under the Advanced (code) disclosure (a text design's `ui` is "advanced
        // code"); the authoring autosave re-render collapses it (uncontrolled DOM open state) — re-open it
        // if closed so the button is visible and hit-testable, then click it. (The RESULT — the structured
        // render section — is first-class, OUTSIDE this disclosure, so it needs no such dance to observe.)
        if (await ctx.Page!.Locator(".design-editor details.code-areas[open]").CountAsync() == 0)
            await ctx.Page!.Locator(".design-editor details.code-areas summary").ClickAsync();
        await ctx.Page!.Locator(".design-editor button.convert-render").ClickAsync();
    }

    // ── M12 E1 — the structured-render TREE EDITOR (recursive renderNodeEditor) ──────────────────

    // A NESTED convertible render: <main class="x"><h1>{leaf}</h1></main> — an element with an attribute,
    // a nested ELEMENT child (h1), whose own child is a text-EXPRESSION leaf ({leaf}). Its structure
    // forces the recursion to descend a level (main → h1) and to render both an element and a leaf node,
    // so the tree editor's nesting + leaf handling are both exercised. Same authoring plumbing as the
    // simple render: fill the `ui` textarea, poll the store for the write.
    private const string NestedConvertibleRender =
        "ui\n    fn render()\n        return <main class=\"x\">\n            <h1>\n                leaf\n";

    [When("I author a nested convertible render into the design's UI")]
    public async Task WhenAuthorNestedRender()
    {
        await ctx.Page!.Locator(".design-editor textarea.design-ui").FillAsync(NestedConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "treeme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == NestedConvertibleRender));
    }

    // A PROJECTABLE nested convertible render for E2: like NestedConvertibleRender, but the leaf is a REAL
    // bound expression (`db.greeting`) rather than the bare undefined symbol `leaf` — so once the design
    // carries a Db root type with a `greeting` field, the whole document PROJECTS to a valid design document
    // (the bare `leaf` cannot resolve, which is fine for E1's tree-recursion proof but blocks a projection
    // check). Same authoring plumbing (fill the ui textarea, poll the store).
    private const string ProjectableNestedRender =
        "ui\n    fn render()\n        return <main class=\"x\">\n            <h1>\n                db.greeting\n";

    [When("I author a projectable nested render into the design's UI")]
    public async Task WhenAuthorProjectableNestedRender()
    {
        await ctx.Page!.Locator(".design-editor textarea.design-ui").FillAsync(ProjectableNestedRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "treeme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == ProjectableNestedRender));
    }

    // After the import host action's ack refetch re-renders the editor, the mode flips: a first-class
    // "Structured render" section (OUTSIDE the collapsing Advanced disclosure) appears, holding the
    // recursive tree editor over design.render. Plain visible wait — no fixed sleep, no disclosure dance.
    // After the import host action's ack refetch re-renders the editor, the mode flips: a first-class
    // "Structured render" section (OUTSIDE the collapsing Advanced disclosure) appears, holding the
    // recursive tree editor over design.render. Wait for the ROOT element's own tag input — proof the
    // recursive renderNodeEditor ran at least once. No fixed sleep, no disclosure dance.
    [Then("the design editor eventually shows the structured render tree editor")]
    public async Task ThenShowsTreeEditor() =>
        await ctx.Page!.WaitForSelectorAsync(".design-editor .render-tree > .node-element > .node-tag-row > input.node-tag");

    // The tree editor renders element nodes outermost-first; the ROOT is the first .node-element, so its
    // direct `input.node-tag` (not a descendant's) reads the root's tag. Scoped to the first element's own
    // tag row so a nested node's input can't satisfy it.
    [Then("the tree editor's root node tag input reads {string}")]
    public async Task ThenRootTagInput(string tag) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const r = document.querySelector('.design-editor .render-tree > .node-element > .node-tag-row > input.node-tag'); return r != null && r.value === {JsString(tag)}; }}");

    // Recursion proof: a NESTED element (h1) must appear as its OWN .node-element nested UNDER the root's
    // .node-children — i.e. the component recursed a level deep, rendering a child element with its own tag
    // input. Assert some node-tag input inside .node-children reads the child's tag.
    [Then("the tree editor shows a nested node with tag input {string}")]
    public async Task ThenNestedTagInput(string tag) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.design-editor .render-tree .node-children input.node-tag')].some(e => e.value === {JsString(tag)})");

    // A LEAF node (empty tag) renders only its `expr` input. The nested h1's text child {leaf} imports as a
    // leaf whose expr source is `leaf`; assert some node-expr input reads it (proving leaves render too).
    [Then("the tree editor shows a leaf expr input reading {string}")]
    public async Task ThenLeafExprInput(string expr) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.design-editor .render-tree input.node-expr')].some(e => e.value === {JsString(expr)})");

    // Edit the ROOT's tag input (an ordinary two-way-bound MetaNode.tag write, like type.name): fill the
    // first .node-element's own tag input with the new value.
    [When("I edit the root node's tag input to {string}")]
    public async Task WhenEditRootTag(string tag) =>
        await ctx.Page!.Locator(".design-editor .render-tree > .node-element > .node-tag-row > input.node-tag").FillAsync(tag);

    // The edit is a journaled scalar autosave; poll the store: the root MetaNode is the one whose tag is
    // the new value AND that is not a child of any other node (a root). Simpler: assert SOME MetaNode now
    // carries the new tag and the OLD root tag is gone — a rename, not an add.
    [Then("the stored render root node has tag {string}")]
    public async Task ThenStoredRootTag(string tag) =>
        await EventuallyAsync(() =>
        {
            var nodes = _designer.Store.ReadExtent("MetaNode").Values;
            return nodes.Any(o => o.Fields.TryGetValue("tag", out var tv) && tv is DeEnv.Storage.TextValue t && t.Text == tag)
                && !nodes.Any(o => o.Fields.TryGetValue("tag", out var tv) && tv is DeEnv.Storage.TextValue { Text: "main" });
        });

    // ── M12 E2 — structural editing (add/remove child nodes + attributes, appending in order) ────────

    // The ROOT node is the first .node-element directly under .render-tree; its OWN controls are direct
    // children (`>`) so a nested node's identically-classed controls can't satisfy the locator. Its add-row
    // holds "+ element" / "+ text/expr" / "+ attr"; its direct children live in its own .node-children, one
    // per child node — no wrapper (the E2 ux fix dropped the .node-child sibling wrapper so each child's
    // remove × lives INSIDE that child's own tag-row/leaf-row instead of floating beside the whole subtree).
    private const string RootNode = ".design-editor .render-tree > .node-element";
    // The root's LAST direct child's editor (the appended element must be LAST under .orderBy(order)).
    private const string RootLastChildElement = RootNode + " > .node-children > :last-child.node-element";

    [When("I add a child element to the root node")]
    public async Task WhenAddChildElement() =>
        await ctx.Page!.Locator(RootNode + " > .node-add-row > button.add-element").ClickAsync();

    // The appended element sorts LAST (order = max sibling order + 1). Assert the root's LAST child is an
    // element whose own tag input reads the expected default/edited tag — proving both that it landed and
    // that it landed at the END (a naive order:0 would sort it to the FRONT, ahead of the imported <h1>).
    [Then("the root node's last child is an element with tag {string}")]
    public async Task ThenRootLastChildTag(string tag) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const e = document.querySelector({JsString(RootLastChildElement + " > .node-tag-row > input.node-tag")}); return e != null && e.value === {JsString(tag)}; }}");

    [When("I edit the root node's last child tag input to {string}")]
    public async Task WhenEditLastChildTag(string tag) =>
        await ctx.Page!.Locator(RootLastChildElement + " > .node-tag-row > input.node-tag").FillAsync(tag);

    [When("I add an attribute to the root node's last child")]
    public async Task WhenAddAttrToLastChild() =>
        await ctx.Page!.Locator(RootLastChildElement + " > .node-add-row > button.add-attr").ClickAsync();

    [When("I add a text child to the root node's last child")]
    public async Task WhenAddTextToLastChild() =>
        await ctx.Page!.Locator(RootLastChildElement + " > .node-add-row > button.add-text").ClickAsync();

    // The added element now carries a real attribute row (name/value inputs) and a nested text-leaf child
    // (a .node-leaf with an expr input). Both appended controls prove add-attr and add-text landed.
    [Then("the root node's last child element has an attribute input and a text-leaf child")]
    public async Task ThenLastChildHasAttrAndText()
    {
        await ctx.Page!.Locator(RootLastChildElement + " > .node-attr > input.node-attr-name").First.WaitForAsync();
        await ctx.Page!.Locator(RootLastChildElement + " > .node-children .node-leaf > input.node-expr").First.WaitForAsync();
    }

    // The × now lives INSIDE the last child's own tag-row (the E2 ux fix), not beside a .node-child wrapper.
    [When("I remove the root node's last child")]
    public async Task WhenRemoveLastChild() =>
        await ctx.Page!.Locator(RootLastChildElement + " > .node-tag-row > button.remove-node").ClickAsync();

    // The removed subtree is gone from the store: no MetaNode carries the removed element's tag any more
    // (GC reclaims the detached subtree on the remove mutation).
    [Then("the root node no longer has a child element with tag {string}")]
    public async Task ThenNoChildWithTag(string tag) =>
        await EventuallyAsync(() =>
            !_designer.Store.ReadExtent("MetaNode").Values.Any(o =>
                o.Fields.TryGetValue("tag", out var tv) && tv is DeEnv.Storage.TextValue t && t.Text == tag));

    // The whole point of a structured render: after every structural edit it must still PROJECT to a valid
    // app document. Read the "treeme" Design node (resolved recursively) from the store and run the real
    // SchemaBridge.ProjectDesignDocument — an un-projectable node (an empty-nothing node, or an attribute
    // with an empty value expression) throws a SchemaValidationException here. Polled (the add is a staged
    // ctx mutation flushed over the WS, so there is a brief async window); on timeout the LAST projection
    // error is surfaced (a bare EventuallyAsync would only say "expected true").
    [Then("the stored render projects to a valid design document")]
    public async Task ThenProjectsValid()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (TryProject()) return;
            await Task.Delay(200);
        }
        throw new Exception("Projection never became valid. Last error: " + _lastProjectError);
    }

    private bool TryProject()
    {
        var designId = DesignIdByLabel("treeme");
        if (designId == 0) { _lastProjectError = "design 'treeme' not found"; return false; }
        var design = _designer.Store.ReadNode(
            DeEnv.Storage.NodePath.Root.Field("designs").Key(designId.ToString()));
        if (design is null) { _lastProjectError = "design node null"; return false; }
        try
        {
            // ProjectDesignDocument builds + validates the whole document, including the render tree: an
            // un-projectable node (an empty-nothing node, or an attribute with an empty value expression)
            // throws a SchemaValidationException here. That is the E2 correctness bar — the STRUCTURAL
            // projectability of the edited render. (We deliberately do NOT then interpreter-LOAD the doc:
            // the imported fixture render references a bare symbol `leaf` that a running app has no binding
            // for — a symbol-resolution concern orthogonal to whether the render tree projects.)
            var doc = DeEnv.Designer.SchemaBridge.ProjectDesignDocument(design);
            return doc.Contains("fn render()");
        }
        catch (Exception ex) { _lastProjectError = ex.Message; return false; }
    }

    private string _lastProjectError = "";

    // ── M12 CANVAS-1 — the client-computable canvas (sys.renderTree) ────────────────────────────
    //
    // The canvas (.design-canvas) renders the design's MetaNode rows into a live tag tree via
    // sys.renderTree — computed on the CLIENT from the row data it already holds. An element node emits its
    // real tag carrying data-node=<row id> (the provenance spine); a non-literal expression leaf emits a
    // span.expr-chip. These steps assert the canvas's rendered DOM directly (a REAL element, not an input),
    // and — crucially — that it updates LIVE (no reload) after a tree-editor edit, proving dep-recording
    // fires through the builtin's walk.

    // A real rendered element of the given tag inside the canvas, stamped with data-node. Auto-waits, so a
    // tree-editor edit that flips a tag (main → section) or an add that introduces a new element (div) is
    // observed WITHOUT any reload — the liveness proof: the edit alone re-rendered the canvas.
    [Then("the design canvas shows a {string} element with a data-node attribute")]
    public async Task ThenCanvasShowsElement(string tag) =>
        // Attached (presence in the DOM), not Visible — a freshly-added empty element (e.g. an empty <div>)
        // has zero size and would fail a visibility check, but it IS in the canvas; presence is the assertion.
        await ctx.Page!.Locator($".design-canvas {tag}[data-node]").First
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });

    // A non-literal expression leaf renders as a visible span.expr-chip placeholder carrying the raw source.
    [Then("the design canvas shows an expression chip reading {string}")]
    public async Task ThenCanvasShowsChip(string source) =>
        await ctx.Page!.Locator($".design-canvas span.expr-chip[data-node]:has-text({CssString(source)})").First.WaitForAsync();

    // The Design id of the (main working-copy) design with the given label, or 0 if not yet present.
    private int DesignIdByLabel(string label)
    {
        var designs = _designer.Store.ReadNode(DeEnv.Storage.NodePath.Root.Field("designs")) as DeEnv.Storage.SetValue;
        if (designs is null) return 0;
        foreach (var id in designs.Members.Keys)
        {
            var d = _designer.Store.ReadById(id);
            if (d is { } dv && dv.Fields.Fields.GetValueOrDefault("label") is DeEnv.Storage.TextValue { Text: var l } && l == label)
                return id;
        }
        return 0;
    }

    [Then("the design editor no longer shows the UI textarea")]
    public async Task ThenNoUiTextarea() =>
        await Assert.That(await ctx.Page!.Locator(".design-editor textarea.design-ui").CountAsync()).IsEqualTo(0);

    [Then("the design editor no longer shows the Convert-to-structured button")]
    public async Task ThenNoConvertButton() =>
        await Assert.That(await ctx.Page!.Locator(".design-editor button.convert-render").CountAsync()).IsEqualTo(0);

    [Then("the instances list shows the instance {string} running design {string}")]
    public async Task ThenListShows(string label, string designLabel)
    {
        var row = RowFor(label);
        await Assert.That(await row.CountAsync()).IsEqualTo(1);
        // The list is the generic <SetTable> with columns ["name", "design"]: the `design` column is an
        // object-ref cell that SetTable renders as the referenced Design's label text (a plain <td>, not
        // the row-id identity cell). Assert that cell holds the expected design label.
        await Assert.That(await row.Locator($"td:not(.row-id):text-is({CssString(designLabel)})").CountAsync())
            .IsGreaterThanOrEqualTo(1);
    }

    [Then("a new instance {string} running design {string} appears in the instances list")]
    public async Task ThenNewInstanceAppears(string name, string designLabel)
    {
        // The hostAction reply triggers a WS refetch + resetViewState (ws.ts), so the list re-renders
        // IN PLACE — no page reload. Wait for the new row to appear in the CURRENT DOM. 45s covers the
        // async create (doc write + handler build) plus the round-trip refetch.
        // KNOWN ISSUE: under FULL-SUITE peak load the new instance's host can be spawn-starved and
        // the row never appears (a hang, not slowness — raising this to 90s still failed 3/3). Passes
        // in isolation. Tracked as a deploy/host-spawn-starvation issue, NOT a timeout to bump.
        await ctx.Page!.WaitForSelectorAsync(
            $"main.ide-list .set-row a.row-link:text-is({CssString(name)})",
            new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 45000 });
        // The DESIGN CELL must populate IN PLACE too — no reload. This is the load-bearing assertion:
        // the kernel mirror writes the new Instance's `design` reference AFTER adding it to the set (a GC
        // ordering constraint), so the row could momentarily render with an empty design cell; the
        // in-place refetch must show the design label. WaitForSelector (auto-waiting) proves it appears
        // without racing the row's first paint — if it never populates in place, this fails (a real
        // refetch-timing bug), rather than a count that might pass on a stale/empty cell.
        await ctx.Page.WaitForSelectorAsync(
            $"main.ide-list .set-row:has(a.row-link:text-is({CssString(name)})) td:not(.row-id):text-is({CssString(designLabel)})",
            new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 45000 });
    }

    [Then("the design dropdown has the design {string} selected")]
    public async Task ThenDropdownSelected(string designLabel) =>
        // The <select>'s pre-selected option (driven by the instance's designId through the <select>
        // binding) is the instance's current design — its visible text is the design's label.
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const s = document.querySelector('select.design-pick'); return s != null && s.options[s.selectedIndex] != null && s.options[s.selectedIndex].textContent.trim() === {JsString(designLabel)}; }}");

    [Then("the instance {string} records the design {string}")]
    public async Task ThenInstanceRecordsDesign(string instanceLabel, string designLabel)
    {
        // Apply (setDesign) records the chosen design on the target's registry entry — the kernel updates
        // the live spec's DesignId (and rewrites kernel.json). Poll the live spec (the WS host-action is
        // async) until it carries the picked design's id.
        var designId = ctx.DesignIdForLabel(designLabel);
        await EventuallyAsync(() =>
            ctx.Kernel!.Instances.Single(i => i.Spec.App == instanceLabel).Spec.DesignId == designId);
    }

    [Then("the {string} instance's app document describes the type {string}")]
    public async Task ThenTargetDescribesType(string label, string typeName)
    {
        // Apply also deployed: it wrote the projected app document onto the target instance's app doc (its
        // own sovereign id-dir). Poll it (the WS host-action + file write is async) until the type appears.
        // The deploy projects the WHOLE app and resets the target store, so it can run long at the tail of
        // a saturated full suite — a wide window keeps it deterministic (this feature's 8 kernel-backed
        // browser scenarios run [NotInParallel], so the last one's deploy lands under peak load).
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await EventuallyAsync(() => File.Exists(target.Spec.SchemaPath)
            && File.ReadAllText(target.Spec.SchemaPath).Contains(typeName), timeoutMs: 45000);
    }

    [Then("the {string} instance's app document declares {string}")]
    public async Task ThenTargetDeclares(string label, string declaration)
    {
        // Apply deployed the projected app document; assert it contains the given prop declaration
        // (e.g. "checked set of TodoList" / "text dict of text by text") -- the canonical AppPrint
        // form of a collection-shaped prop, proving cardinality + key type flowed through projection.
        // Wide window: the deploy projects the WHOLE app + resets data, run under peak full-suite load.
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await EventuallyAsync(() => File.Exists(target.Spec.SchemaPath)
            && File.ReadAllText(target.Spec.SchemaPath).Contains(declaration), timeoutMs: 45000);
    }


    [Then("the {string} instance's app document has an access rule for {string} granting {string}")]
    public async Task ThenTargetHasAccessRule(string label, string subject, string verb)
    {
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await EventuallyAsync(() =>
        {
            if (!File.Exists(target.Spec.SchemaPath)) return false;
            var source = File.ReadAllText(target.Spec.SchemaPath);
            return source.Contains($"    {subject}\n        {verb}", StringComparison.Ordinal)
                || source.Contains($"    {subject}\r\n        {verb}", StringComparison.Ordinal);
        }, timeoutMs: 45000);
    }

    [Then("the design's type {string} is an enum with values {string}")]
    public async Task ThenDesignerTypeIsEnum(string typeName, string values)
    {
        // Split-target (no apply/deploy): assert the designer captured the enum in its OWN sovereign
        // store — base type "enum" + the values input. Applying a design
        // is proven by "Applying a different design ... deploys it" + HostAction — so there is no kernel
        // deploy, no second instance's schema file, no 45s poll here, just the UI-authoring seam. The
        // values input persists over an async WS round-trip, so poll the (fast, local) MetaType extent.
        var expected = values.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaType").Values.Any(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == typeName
            && o.Fields.TryGetValue("baseType", out var b) && b is DeEnv.Storage.TextValue bt && bt.Text == "enum"
            && o.Fields.TryGetValue("values", out var vv) && vv is DeEnv.Storage.TextValue vt
            && vt.Text.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).SequenceEqual(expected)));
    }

    [Then("the design's prop {string} is multiline")]
    public async Task ThenDesignerPropMultiline(string propName) =>
        // The designer captured the multiline flag in its OWN sovereign store (the prop's bound checkbox
        // wrote prop.multiline = true). Poll the (fast, local) MetaProp extent — the toggle persists over
        // an async WS round-trip.
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values.Any(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == propName
            && o.Fields.TryGetValue("multiline", out var v) && v is DeEnv.Storage.BoolValue b && b.Value));

    [Then("the design's prop {string} is not multiline")]
    public async Task ThenDesignerPropNotMultiline(string propName) =>
        // A non-text prop never gets the flag (its toggle is hidden). Its stored multiline reads false —
        // the store defaults a declared bool to false, so the field is present and false, never errors.
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values.Any(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == propName
            && (!o.Fields.TryGetValue("multiline", out var v) || v is not DeEnv.Storage.BoolValue { Value: true })));

    [Then("the design {string} has no unnamed type")]
    public async Task ThenNoUnnamedType(string label)
    {
        // The remove must actually delete the empty type from the designer's sovereign store -- GC included.
        // A failed remove (the GC-crash regression) rejects server-side and the client journal re-inserts the
        // row, leaving the empty MetaType stranded in the extent (its set member removed in memory but never
        // saved, since the GC threw before SaveDoc). Poll until no empty-name MetaType survives.
        await EventuallyAsync(() => !_designer.Store.ReadExtent("MetaType").Values
            .Any(o => o.Fields.TryGetValue("name", out var v) && v is DeEnv.Storage.TextValue t && t.Text == ""));
        // ...and it stays gone across a fresh server render of the editor (no reappearance on reload).
        await ctx.Page!.ReloadAsync();
        await ctx.Page.WaitForSelectorAsync("main.ide-design-edit .design-editor");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
        await Assert.That(
            await ctx.Page.Locator(".design-editor .type-card input.type-name[value=\"\"]").CountAsync())
            .IsEqualTo(0);
    }

    // ── Then: progressive disclosure (fields hidden until their shape is chosen) ──

    [Then("the prop {string} shows no key-type field")]
    public async Task ThenPropNoKeyType(string propName) =>
        // A single/set prop's key-type field is hidden (it is meaningful only for a dictionary). The field
        // stays in the DOM — progressive disclosure flips visibility via the row's class — so assert it is
        // HIDDEN, not absent.
        await PropKeytypeInput(propName).First.WaitForAsync(Hidden);

    [Then("the prop {string} shows a key-type field")]
    public async Task ThenPropKeyType(string propName) =>
        // Set to dictionary, the key-type field becomes visible via the row's class change — wait for it
        // (proving the disclosure reconciles when cardinality changes).
        await PropKeytypeInput(propName).First.WaitForAsync();

    [Then("the prop {string} shows a multiline toggle")]
    public async Task ThenPropMultilineToggle(string propName) =>
        // A single text prop's row shows the multiline checkbox (visible via the row's is-text-single
        // class). Wait for it visible (the field is always in the DOM; disclosure flips visibility).
        await PropMultilineInput(propName).First.WaitForAsync();

    [Then("the prop {string} shows no multiline toggle")]
    public async Task ThenPropNoMultilineToggle(string propName) =>
        // A non-text (or non-single) prop's multiline checkbox is hidden — multiline is valid only on a
        // single text prop. The field stays in the DOM; assert it is HIDDEN, not absent.
        await PropMultilineInput(propName).First.WaitForAsync(Hidden);

    [Then("the just-added type shows a props editor")]
    public async Task ThenJustAddedPropsEditor() =>
        await JustAddedTypeRow().Locator(".props-editor").First.WaitForAsync();

    [Then("the just-added type shows no props editor")]
    public async Task ThenJustAddedNoPropsEditor() =>
        await JustAddedTypeRow().Locator(".props-editor").First.WaitForAsync(Hidden);

    [Then("the just-added type shows a values field")]
    public async Task ThenJustAddedValuesField() =>
        await JustAddedTypeRow().Locator("input.type-values").First.WaitForAsync();

    [Then("the just-added type shows no values field")]
    public async Task ThenJustAddedNoValuesField() =>
        await JustAddedTypeRow().Locator("input.type-values").First.WaitForAsync(Hidden);

    // ── Then: the grouped prop-type picker ───────────────────────────────────────

    [Then("the prop {string} type picker offers the built-in type {string}")]
    public async Task ThenPickerOffersBuiltin(string propName, string typeName) =>
        await Assert.That(await PropTypeSelect(propName)
            .Locator($"optgroup[label=\"Built-in\"] option[value={CssString(typeName)}]").CountAsync())
            .IsGreaterThanOrEqualTo(1);

    [Then("the prop {string} type picker offers the design type {string}")]
    public async Task ThenPickerOffersDesignType(string propName, string typeName) =>
        await Assert.That(await PropTypeSelect(propName)
            .Locator($"optgroup[label=\"This design\"] option[value={CssString(typeName)}]").CountAsync())
            .IsGreaterThanOrEqualTo(1);

    [Then("the prop {string} type picker keeps built-in and design types in separate groups")]
    public async Task ThenPickerGrouped(string propName)
    {
        // The system scalars and the user's own types live in SEPARATE <optgroup>s — not flatly intermixed.
        var select = PropTypeSelect(propName);
        await Assert.That(await select.Locator("optgroup[label=\"Built-in\"]").CountAsync()).IsGreaterThanOrEqualTo(1);
        await Assert.That(await select.Locator("optgroup[label=\"This design\"]").CountAsync()).IsGreaterThanOrEqualTo(1);
    }

    // ── Then: client-side (SPA) navigation in the custom designer ────────────────

    // The browser URL after the client-side Edit-link nav: the designer is kernel-mounted at
    // /apps/designer, so its emitted href + the pushState target carry that mount — the URL becomes
    // /apps/designer/designs/<designId>. Polled (a client-side nav updates location via pushState with
    // no Load event); the dynamic design id is matched with a regex.
    [Then("the browser URL is the mounted design editor")]
    public async Task ThenBrowserUrlIsEditor() =>
        await ctx.Page!.WaitForUrlContentAsync(
            new System.Text.RegularExpressions.Regex(@"/apps/designer/designs/[0-9]+$"));

    // Structural-privacy pin for the custom designer's first paint: the designs LIST ships only what it
    // displays (design labels), NOT every design's full source. The todo design's `ui` section is the real
    // todo app's custom render; it contains the class token "user-chip", which the list never displays — so
    // that token must NOT appear anywhere in the first paint's shipped client state (window.initData). Reads
    // the WHOLE document (the head data island included), so it catches a value leaking through initData even
    // though it is never in the visible body. (Mirrors CodeSteps' window.initData privacy assertions.)
    [Then("the designs list first paint does not ship the design's UI source token {string}")]
    public async Task ThenListDoesNotShipToken(string token)
    {
        var html = await ctx.Page!.ContentAsync();
        const string marker = "window.initData=";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        await Assert.That(start).IsGreaterThanOrEqualTo(0);
        start += marker.Length;
        var end = html.IndexOf(";window.initUi=", start, StringComparison.Ordinal);
        var initData = html[start..end];
        await Assert.That(initData.Contains(token, StringComparison.Ordinal)).IsFalse();
    }

    // ── Then: no partial-content FLASH on the deep editor (round-2) ──────────────

    // Arm a MutationObserver that flips window.__sawBlankEditor the instant the editor PAGE
    // (`main.ide-design-edit` — its heading + Back link) appears under #app WITHOUT any `.type-card` in it:
    // the empty/partial editor state. (The optimistic round-1 paint rendered the page chrome but the
    // deep `designEditor(d)` call read the design's UNSHIPPED types and was swallowed to nothing, so the
    // `.design-editor` body — and its type cards — were absent for a frame.) The todo design always has
    // the TodoItem type, so a COMPLETE editor always carries ≥1 `.type-card`; only a partial paint shows
    // the page with none. This is state-agnostic to HOW the body is missing (no `.design-editor` at all,
    // OR a `.design-editor` with an empty type list). A post-hoc check would miss a transient flash the
    // refetch then fills, so the observer watches every intermediate mutation; the current state is
    // recorded too (a synchronous paint could beat the observer). The held view shows the LIST
    // (`main.ide-designs`), not `main.ide-design-edit`, so the detector stays false while holding and only
    // fires on a partial EDITOR paint. Wait for readiness first so it is armed on the fully-settled list,
    // before the Edit click that triggers the client-side navigation into the editor.
    [When("I arm the blank-editor detector")]
    public async Task WhenArmBlankEditorDetector()
    {
        await ctx.Page!.WaitReadyAsync();
        await ctx.Page!.EvaluateAsync(
            """
            () => {
                const partial = () => document.querySelector('#app main.ide-design-edit') != null
                    && document.querySelector('#app .type-card') == null;
                window.__sawBlankEditor = partial();
                const obs = new MutationObserver(() => { if (partial()) window.__sawBlankEditor = true; });
                obs.observe(document.getElementById('app'), { childList: true, subtree: true });
            }
            """);
    }

    // The blank/partial editor NEVER rendered during the navigation: the detector flag stayed false. The
    // preceding populated assertions (the TodoItem type card + the settled UI text) already waited for the
    // COMPLETE editor to appear, so by now any transient blank-editor flash would have been observed. This
    // is the decisive flash assertion — without the speculative guard, the optimistic paint renders the
    // editor section with an empty type list here and the flag is true.
    [Then("the blank design editor never appeared during the navigation")]
    public async Task ThenBlankEditorNeverAppeared() =>
        await Assert.That(await ctx.Page!.EvaluateAsync<bool>("() => window.__sawBlankEditor === true")).IsFalse();

    // Scroll the page down so a following forward-nav can prove it resets scroll. Appends a tall spacer to
    // document.body (OUTSIDE #app, so the editor's #app rebuild leaves it in place — the page stays
    // scrollable across the nav), scrolls to a positive offset, and asserts the scroll actually moved
    // (non-zero scrollY) — otherwise the later "scrolled to the top" assertion would be vacuously true on a
    // page too short to scroll. (scrollTo is sync; the assert reads the settled scroll position.)
    [When("I scroll the page down")]
    public async Task WhenScrollDown()
    {
        await ctx.Page!.EvaluateAsync(
            """
            () => {
                const sp = document.createElement('div');
                sp.id = '__scroll_spacer';
                sp.style.height = '3000px';
                document.body.appendChild(sp);
                window.scrollTo(0, 1200);
            }
            """);
        await Assert.That(await ctx.Page!.EvaluateAsync<double>("() => window.scrollY")).IsGreaterThan(0d);
    }

    // The window is scrolled back to the top after a forward client-side nav (the SPA twin of the full
    // reload's scroll reset). Polled — the reset fires when the TARGET view paints, which (for a held
    // incomplete target like the deep editor) is the refetch reply's render, slightly after the URL change.
    [Then("the page is scrolled to the top")]
    public async Task ThenScrolledTop() =>
        await ctx.Page!.WaitForFunctionAsync("() => window.scrollY === 0");

    // ── Then: db.instances seeded from registry (slice 1) ────────────────────────

    [Then("the design-host has a stored Instance for each hosted instance")]
    public async Task ThenDesignHostHasStoredInstances()
    {
        // The design-host's `db.instances` extent must hold one Instance per hosted kernel instance.
        // The designer itself (id 1) is also a hosted instance and must be present; the targets (todo,
        // crm) each get their own entry. So the total is 3 (designer + 2 targets).
        var instances = _designer.Store.ReadExtent("Instance");
        await Assert.That(instances.Count).IsGreaterThanOrEqualTo(3);
    }

    [Then("the stored Instance for {string} has runtimeId matching the kernel")]
    public async Task ThenStoredInstanceRuntimeId(string label)
    {
        // The Instance whose `name` == label must have a `runtimeId` that matches the live kernel
        // instance's spec.Id — the stable link from the stored mirror back to the runtime row.
        var kernelId = ctx.Kernel!.Instances.Single(i => i.Spec.App == label).Spec.Id;
        var instances = _designer.Store.ReadExtent("Instance");
        var match = instances.Values.FirstOrDefault(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == label);
        await Assert.That(match).IsNotNull();
        await Assert.That(
            match!.Fields.TryGetValue("runtimeId", out var rv) && rv is DeEnv.Storage.IntValue ri
                ? ri.Value : -1)
            .IsEqualTo(kernelId);
    }

    [Then("the stored Instance for {string} has its design resolved to the {string} design")]
    public async Task ThenStoredInstanceDesign(string instanceLabel, string designLabel)
    {
        // The Instance for `instanceLabel` must have its `design` reference pointing at the Design
        // whose label == `designLabel`. The `design` field is a stored bare id (a single reference);
        // the Design extent holds objects at those ids — resolves by construction.
        var instances = _designer.Store.ReadExtent("Instance");
        var designs = _designer.Store.ReadExtent("Design");

        var instance = instances.Values.FirstOrDefault(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == instanceLabel);
        await Assert.That(instance).IsNotNull();

        // The `design` field is a ReferenceValue (a single-object ref stored as an id).
        await Assert.That(instance!.Fields.ContainsKey("design")).IsTrue();
        var designRef = instance.Fields["design"];
        await Assert.That(designRef).IsTypeOf<DeEnv.Storage.ReferenceValue>();
        var refValue = (DeEnv.Storage.ReferenceValue)designRef;
        await Assert.That(refValue.TargetId).IsNotNull();
        var designId = refValue.TargetId!.Value;

        // That id must resolve to a Design with the expected label.
        await Assert.That(designs.ContainsKey(designId)).IsTrue();
        var design = designs[designId];
        await Assert.That(
            design.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue lt
                ? lt.Text : "")
            .IsEqualTo(designLabel);
    }

    // ── When/Then: db.instances mirror (Slice 2 — direct host-action calls, no browser) ────────────

    // Create a new instance via the kernel host action directly (not through the browser UI).
    // Uses the named design's existing app document as the new instance's source, and passes
    // the design's id so the mirror can link the Instance row's `design` reference.
    [When("a new instance named {string} is created from the {string} design via host action")]
    public async Task WhenCreateInstanceViaHostAction(string name, string designLabel)
    {
        var registryPath = Path.Combine(ctx.KernelDir!, "kernel.json");
        // Borrow the design's existing app document (the kernel already hosts it).
        var designSource = ctx.Kernel!.Instances.Single(i => i.Spec.App == designLabel);
        var appDoc = File.ReadAllText(designSource.Spec.SchemaPath);
        var designId = ctx.DesignIdForLabel(designLabel);
        var created = await ctx.Kernel.CreateAsync(appDoc, name, ctx.KernelDir!, registryPath, designId);
        _lastCreatedInstanceId = created.Spec.Id;
    }

    // Delete an existing instance via the kernel host action directly.
    [When("the {string} instance is deleted via host action")]
    public async Task WhenDeleteInstanceViaHostAction(string label)
    {
        var registryPath = Path.Combine(ctx.KernelDir!, "kernel.json");
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await ctx.Kernel.DeleteAsync(target, registryPath);
    }

    // Rename an instance via the kernel host action directly.
    [When("the {string} instance is renamed to {string} via host action")]
    public async Task WhenRenameInstanceViaHostAction(string label, string newName)
    {
        var registryPath = Path.Combine(ctx.KernelDir!, "kernel.json");
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await ctx.Kernel.RenameAsync(target.Spec.Id, newName, registryPath);
    }

    [Then("the design-host has a stored Instance named {string}")]
    public async Task ThenDesignHostHasStoredInstanceNamed(string name)
    {
        var instances = _designer.Store.ReadExtent("Instance");
        var match = instances.Values.FirstOrDefault(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == name);
        await Assert.That(match).IsNotNull();
    }

    [Then("the stored Instance {string} has a runtimeId that matches the new kernel instance")]
    public async Task ThenStoredInstanceRuntimeIdMatchesNew(string name)
    {
        var instances = _designer.Store.ReadExtent("Instance");
        var match = instances.Values.FirstOrDefault(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == name);
        await Assert.That(match).IsNotNull();
        var actual = match!.Fields.TryGetValue("runtimeId", out var rv) && rv is DeEnv.Storage.IntValue ri
            ? ri.Value : -1;
        await Assert.That(actual).IsEqualTo(_lastCreatedInstanceId);
    }

    [Then("the design-host has no stored Instance named {string}")]
    public async Task ThenDesignHostHasNoStoredInstanceNamed(string name)
    {
        var instances = _designer.Store.ReadExtent("Instance");
        var match = instances.Values.FirstOrDefault(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == name);
        await Assert.That(match).IsNull();
    }

    [Then("the committed designer login gate is shown")]
    public async Task ThenCommittedDesignerLoginGateShown()
    {
        await ctx.Page!.Locator(".login-form").WaitForAsync();
        await Assert.That(await ctx.Page.Locator("main.ide-designs, main.ide-list").CountAsync()).IsEqualTo(0);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private void SeedDesignerAdmin()
    {
        var desc = InstanceDescriptionLoader.LoadFile(_designer.Spec.SchemaPath);
        AdminSeed.Seed(_designer.Store, desc, DesignerAdminName, DesignerAdminPassword, "Admin");
    }

    private async Task LoginDesignerAdminAsync()
    {
        var page = ctx.Page ?? throw new InvalidOperationException("Designer browser was not started.");
        await page.GotoReadyAsync(ctx.DesignerUrl("/designs"));
        await page.WaitReadyAsync();
        await page.Locator(".login-form input.name").FillAsync(DesignerAdminName);
        await page.Locator(".login-form input.password").FillAsync(DesignerAdminPassword);
        await page.Locator(".login-form button.login-submit").ClickAsync();
        await page.Locator("main.ide-designs .set-row").First.WaitForAsync();
    }

    // The instances-list row for an instance, located by its app-name cell (exact match, so "todo"
    // never matches "designer"/"crm").
    private Microsoft.Playwright.ILocator RowFor(string label) =>
        ctx.Page!.Locator($"main.ide-list .set-row:has(a.row-link:text-is({CssString(label)}))");

    // The designs-list row for a design, located by its label (the <SetTable> stretched row link,
    // exact match). The list renders via the generic <SetTable>, so a row is .set-row and the label
    // lives in a.row-link.
    private Microsoft.Playwright.ILocator DesignRowFor(string label) =>
        ctx.Page!.Locator($".set-row:has(a.row-link:text-is({CssString(label)}))");

    // A design-editor type-name input currently holding `name` (the type being renamed).
    private Microsoft.Playwright.ILocator TypeNameInput(string name) =>
        ctx.Page!.Locator($".design-editor .type-card input.type-name[value={CssString(name)}]");

    // The type-row of the just-added type, located by the name we gave it (used by the base-type / values
    // steps after it is no longer the empty-name row).
    private Microsoft.Playwright.ILocator JustAddedTypeRow() =>
        ctx.Page!.Locator($".design-editor .type-card:has(input.type-name[value={CssString(_justAddedTypeName)}])");

    // The prop-type <select> of the `.prop-row` whose `.prop-name` currently holds `propName` (the prop
    // being retyped). Scoped to that row so it targets the right prop across all the types' prop rows.
    private Microsoft.Playwright.ILocator PropTypeSelect(string propName) =>
        ctx.Page!.Locator($".design-editor .prop-row:has(input.prop-name[value={CssString(propName)}]) select.prop-type");

    // The cardinality <select> / key-type input of the `.prop-row` whose `.prop-name` holds `propName`,
    // scoped to that row (the key-type input only exists once the prop is a dictionary).
    private Microsoft.Playwright.ILocator PropCardinalitySelect(string propName) =>
        ctx.Page!.Locator($".design-editor .prop-row:has(input.prop-name[value={CssString(propName)}]) select.prop-cardinality");

    private Microsoft.Playwright.ILocator PropKeytypeInput(string propName) =>
        ctx.Page!.Locator($".design-editor .prop-row:has(input.prop-name[value={CssString(propName)}]) input.prop-keytype");

    // The multiline checkbox of the `.prop-row` whose `.prop-name` holds `propName`. Shown only on a
    // single text prop (progressive disclosure); always in the DOM, so "shows no toggle" means hidden.
    private Microsoft.Playwright.ILocator PropMultilineInput(string propName) =>
        ctx.Page!.Locator($".design-editor .prop-row:has(input.prop-name[value={CssString(propName)}]) input.prop-multiline");

    // Wait for a locator to become HIDDEN (display:none) — progressive-disclosure fields stay in the DOM
    // and only flip visibility, so "shows no X" means hidden, not detached.
    private static readonly Microsoft.Playwright.LocatorWaitForOptions Hidden =
        new() { State = Microsoft.Playwright.WaitForSelectorState.Hidden };

    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    // A double-quoted CSS/Playwright string argument with quotes/backslashes escaped.
    private static string CssString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // Polls a condition (a WS round-trip / file write is async). An IOException is the test thread
    // reading a store/app file mid-write — transient, retried. Mirrors TodoSteps.EventuallyAsync.
    private static async Task EventuallyAsync(Func<bool> condition, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return; }
            catch (IOException) { /* file mid-write — retry */ }
            await Task.Delay(50);
        }
        bool final;
        try { final = condition(); } catch (IOException) { final = false; }
        await Assert.That(final).IsTrue();
    }
}
