using DeEnv.Kernel;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for Designer.feature — the operator IDE (the REAL DeEnv/instances/1/app.app), a URL-routed
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
// Cross-route navigation is a fresh-SSR <a href> per route (the no-load model — each route's page needs
// different store data, so a server render from the store is exactly right), not a client-side path
// reassignment; the steps click the links and let the browser navigate.
[Binding]
public sealed class DesignerSteps(InstanceContext ctx)
{
    // The kernel-hosted designer instance (id 1); its targets are reached via ctx.Kernel.Instances by
    // their registry label (Spec.App).
    private HostedInstance _designer = null!;

    // The name + free port pair the create-instance form was filled with — used to locate the spawned
    // instance (the name is its display label in the list; the ports pin exactly this one in the kernel).
    private string _newInstanceName = "";
    private int _newInstanceAppPort;
    private int _newInstanceInfraPort;

    // The name given to a just-added type (so the base-type / values steps relocate that row once it is
    // no longer the empty-name one).
    private string _justAddedTypeName = "";

    // ── Given ───────────────────────────────────────────────────────────────────

    [Given("the operator IDE is running on a kernel hosting instances {string} and {string}")]
    public async Task GivenIdeRunning(string firstLabel, string secondLabel)
    {
        // Boot a kernel hosting the real designer (id 1) + two target instances labelled to match the
        // designer's two seeded designs (the fixture seeds each target's designId to the matching
        // design), then point the browser at the designer's app port.
        _designer = await ctx.StartKernelDesignerBrowserAsync((5, firstLabel), (6, secondLabel));
    }

    // ── When: navigation ─────────────────────────────────────────────────────────

    [When("I open the designs list")]
    public async Task WhenOpenDesignsList()
    {
        await ctx.Page!.GotoReadyAsync("/designs");
        // The designs list now renders via the generic <SetTable> (a .set-row per design, the label in
        // a stretched a.row-link, with a per-row action cell carrying the Edit link + Delete button).
        await ctx.Page!.WaitForSelectorAsync("main.ide-designs .set-row");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I open the instances list")]
    public async Task WhenOpenList()
    {
        await ctx.Page!.GotoReadyAsync("/instances");
        // Hydration checkpoint: the SSR instance rows are present AND the client bundle has bootstrapped
        // (window.initUi set), so the hand-rolled links/handlers are attached before we interact.
        await ctx.Page!.WaitForSelectorAsync("main.ide-list .instance-row");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I edit the design {string}")]
    public async Task WhenEditDesign(string label)
    {
        // The Edit link is a fresh-SSR <a href="/designs/<designId>"> on the matching design row; clicking
        // it navigates the browser, so the editor page is a full server render with the design's data.
        // Wait for the editor SECTION (always present once the design resolves) rather than a type row —
        // a freshly-added design has no types yet, so .type-name would never appear for it.
        await DesignRowFor(label).Locator("a.edit-design").ClickAsync();
        await ctx.Page!.WaitForSelectorAsync("main.ide-design-edit .design-editor");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I open the instance {string}")]
    public async Task WhenOpenInstance(string label)
    {
        // The Open link is a fresh-SSR <a href="/instances/<id>"> on the matching instance row. Reaching
        // the instance page can start from the instances list OR directly (after editing a design) — go
        // to the list first so the row's Open link is present, then click it.
        if (await ctx.Page!.Locator($".instance-row").CountAsync() == 0)
            await WhenOpenList();
        await RowFor(label).Locator("a.open-instance").ClickAsync();
        await ctx.Page!.WaitForSelectorAsync("main.ide-instance select.design-pick");
        await ctx.Page.WaitForFunctionAsync("() => typeof window.initUi !== 'undefined'");
    }

    [When("I open that new instance")]
    public async Task WhenOpenNewInstance()
    {
        // The just-created instance is the one bound to the free ports we filled. Navigate straight to its
        // selector page (a fresh SSR over the kernel's now-refreshed live set), exactly as the Open link
        // on the list would.
        var created = ctx.Kernel!.Instances.Single(i => i.Spec.AppPort == _newInstanceAppPort);
        await ctx.Page!.GotoReadyAsync($"/instances/{created.Spec.Id}");
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
        await ctx.Page.Locator("main.ide-designs .create-form button.set-add").ClickAsync();
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

    [When("I create an instance named {string} from the design {string} on a free port pair")]
    public async Task WhenCreateInstance(string name, string designLabel)
    {
        // The inline "New instance" form on /instances: pick the design (its option value is the design
        // id), give it a display name, fill a genuinely free app/infra port pair (a hard-coded pair would
        // collide with the other in-process hosts → a kernel reject), then click Create. Create runs
        // sys.create(d, name, appPort, infraPort) — a host action that spawns a new instance running that
        // design under that name.
        await ctx.Page!.Locator("select.new-instance-design").SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Label = designLabel });
        _newInstanceName = name;
        await ctx.Page.Locator("input.new-instance-name").FillAsync(name);
        _newInstanceAppPort = InstanceContext.FreePort();
        _newInstanceInfraPort = InstanceContext.FreePort();
        await ctx.Page.Locator("input.new-instance-app-port").FillAsync(_newInstanceAppPort.ToString());
        await ctx.Page.Locator("input.new-instance-infra-port").FillAsync(_newInstanceInfraPort.ToString());
        // The Create button is gated on a picked design (it renders inside `if sys.id(d) == newDesignId`),
        // so it only appears once the <select> onchange has set the picked id — wait for it, then click.
        await ctx.Page.Locator("button.create-instance").ClickAsync();
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
        await ctx.Page!.GotoReadyAsync("/designs/999999");
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

    [Then("the instances list shows the instance {string} running design {string}")]
    public async Task ThenListShows(string label, string designLabel)
    {
        var row = RowFor(label);
        await Assert.That(await row.CountAsync()).IsEqualTo(1);
        // The current design's label is rendered inside the same row, resolved by the explicit designId
        // reference (a row whose designId matches no design shows no .design-label).
        await Assert.That(await row.Locator($".design-label:text-is({CssString(designLabel)})").CountAsync())
            .IsGreaterThanOrEqualTo(1);
    }

    [Then("a new instance {string} running design {string} appears in the instances list")]
    public async Task ThenNewInstanceAppears(string name, string designLabel)
    {
        // The host action (sys.create) is async; first wait until the kernel has spawned the instance on
        // the ports we picked, with the chosen design recorded on its new registry entry (its designId is
        // the picked design's id — threaded through CreateAsync). This proves the create landed. Create
        // binds two ports + starts two GenHTTP hosts, so it can run long at the tail of a saturated full
        // suite — a wide window keeps it deterministic (same reasoning as ThenTargetDescribesType's deploy).
        var designId = ctx.DesignIdForLabel(designLabel);
        await EventuallyAsync(() => ctx.Kernel!.Instances
            .Any(i => i.Spec.AppPort == _newInstanceAppPort && i.Spec.DesignId == designId), timeoutMs: 45000);

        // The instances list is a live VIEW, not a live PUSH (a host-action ok does not re-render the open
        // page), so reload /instances — a fresh SSR over the kernel's refreshed live set now shows the new
        // row. The created instance carries the name we typed; assert a row for it shows the picked design
        // (its design-label resolves through the new designId reference) — proving name + design both flowed
        // through create → registry → list.
        await ctx.Page!.GotoReadyAsync("/instances");
        await ctx.Page!.WaitForSelectorAsync("main.ide-list .instance-row");
        var newRow = ctx.Page.Locator($".instance-row:has(.instance-app:text-is({CssString(name)}))");
        await Assert.That(await newRow.CountAsync()).IsGreaterThanOrEqualTo(1);
        await Assert.That(
            await newRow.Locator($".design-label:text-is({CssString(designLabel)})").CountAsync())
            .IsGreaterThanOrEqualTo(1);
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

    [Then("the {string} instance's app document declares the enum {string} with values {string}")]
    public async Task ThenTargetDeclaresEnum(string label, string typeName, string values)
    {
        // Apply deployed the projected app document; assert it declares the enum in the canonical AppPrint
        // form -- `    Name enum\n` then each value indented 8 spaces -- proving the type name, base type
        // "enum", and the comma-separated value list all flowed through projection. The whole block is
        // matched (not a bare value substring that could collide elsewhere). Wide window: the deploy
        // projects the WHOLE app + resets data, run under peak full-suite load.
        var expected = "    " + typeName + " enum\n"
            + string.Concat(values.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0)
                .Select(v => "        " + v + "\n"));
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await EventuallyAsync(() => File.Exists(target.Spec.SchemaPath)
            && File.ReadAllText(target.Spec.SchemaPath).Replace("\r\n", "\n").Contains(expected), timeoutMs: 45000);
    }

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

    // ── helpers ─────────────────────────────────────────────────────────────────

    // The instances-list row for an instance, located by its app-name cell (exact match, so "todo"
    // never matches "designer"/"crm").
    private Microsoft.Playwright.ILocator RowFor(string label) =>
        ctx.Page!.Locator($".instance-row:has(.instance-app:text-is({CssString(label)}))");

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
