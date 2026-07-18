using DeEnv.Kernel;
using DeEnv.Tests.TestSupport;
using DeEnv.Instance;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

public sealed partial class DesignerSteps
{

    [When("I open the designer designs route")]
    public async Task WhenOpenDesignerDesignsRoute() =>
        await ctx.Page!.GotoReadyAsync(ctx.DesignerUrl("/designs"));

    [When("I open the designs list")]
    public async Task WhenOpenDesignsList()
    {
        await ctx.Page!.GotoReadyAsync(ctx.DesignerUrl("/designs"));
        // The designs list now renders via the generic <SetTable> (a .set-row per design, the label in
        // a stretched a.row-link, with a per-row action cell carrying the Edit link + Delete button).
        // Use WaitForSelector (at-least-one) to tolerate the 3 rows (designer + hosted) without strict mode.
        await ctx.Page!.WaitForSelectorAsync("main.ide-designs .set-row", new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        // Wait for a post-init interactive element instead of the legacy initUi global.
        await ctx.Page.Locator("main.ide-designs .new-btn").First.WaitForAsync();
    }

    [When("I open the instances list")]
    public async Task WhenOpenList()
    {
        await ctx.Page!.GotoReadyAsync(ctx.DesignerUrl("/instances"));
        // Hydration checkpoint: the SSR instance rows are present AND the client bundle has bootstrapped
        // (window.initUi set), so the hand-rolled links/handlers are attached before we interact.
        await ctx.Page!.Locator("main.ide-list .set-row").First.WaitForAsync();
        await ctx.Page.Locator("main.ide-list .new-btn").First.WaitForAsync();
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
        await ctx.Page.Locator("main.ide-design-edit .design-editor .add-type").WaitForAsync();
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
        var openLink = RowFor(label).Locator(".kebab-menu.open a.open-instance");
        await openLink.WaitForAsync();
        await openLink.ClickAsync();
        await ctx.Page!.Locator("main.ide-instance select.design-pick").WaitForAsync();
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
        await ctx.Page!.Locator("main.ide-instance select.design-pick").WaitForAsync();
    }

    // ──── When: creating (the GENERIC SetTable create) ─────────────────────────────────────────────────────────

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
        var newDesignRow = ctx.Page.Locator("main.ide-designs .set-row", new() {
            Has = ctx.Page.Locator("a.row-link", new() { HasTextString = label })
        });
        await newDesignRow.WaitForAsync();
        // Wait for the remapped positive id on the Edit link (negative ids start with -).
        // Use contains (*) and tolerant of mount prefix (e.g. /apps/designer/designs/123).
        // The link is always present; we wait for the one without the transient negative id.
        await newDesignRow.Locator("a.edit-design:not([href*=\"/-\"])").WaitForAsync();
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
        await Assert.That(await form.Locator("button", new() { HasTextString = "Set" }).CountAsync()).IsEqualTo(0);
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

    // ──── When: editing a design (on /designs/<id>) ────────────────────────────────────────────────────────────────

    [When("I rename the type {string} to {string}")]
    public async Task WhenRename(string from, string to)
    {
        var input = TypeNameInput(from);
        await input.FillAsync(to);
        // The bound input re-renders the model name to the new value (the client edit landed)…
        await TypeNameInput(to).WaitForAsync();
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
        var select = PropTypeSelect(propName);
        await select.WaitForAsync();
        await Assert.That(await select.InputValueAsync()).IsEqualTo(newType);
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
        await PropKeytypeInput(propName).WaitForAsync();
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
        _justAddedTypeName = name;
        var lastCard = ctx.Page!.Locator("main.ide-design-edit .design-editor .type-card").Last;
        await lastCard.Locator("input.type-name").FillAsync(name);
        // The re-render after fill reflects the name into the input (value attr + prop).
        await TypeNameInput(name).WaitForAsync();
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
        var kindSelect = JustAddedTypeRow().Locator("select.type-kind");
        await kindSelect.SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Value = baseType });
        await kindSelect.WaitForAsync();
        await Assert.That(await kindSelect.InputValueAsync()).IsEqualTo(baseType);
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
        await JustAddedTypeRow().Locator("input.type-values").WaitForAsync();
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaType").Values
            .Any(o => o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == _justAddedTypeName
                && o.Fields.TryGetValue("values", out var v) && v is DeEnv.Storage.TextValue t && t.Text == values));
    }

    [When("I add a type to the design")]
    public async Task WhenAddType()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor button.add-type", new() { HasTextString = "+ Type" }).ClickAsync();
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .type-card").Last.WaitForAsync();
    }

    [When("I remove the just-added unnamed type")]
    public async Task WhenRemoveUnnamedType() =>
        // The just-added row is the last .type-card (adds append). Clicking its Remove button
        // drives arrayRemove on the nested types set.
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .type-card").Last
            .Locator("button.remove-type").ClickAsync();

    // ──── When: the instance selector (on /instances/<id>) ─────────────────────────────────────────────────

    [When("I pick the design {string} in the dropdown")]
    public async Task WhenPickDesign(string designLabel)
    {
        // Pick the option whose visible label is the design's name (its value is the design id). The
        // onchange binding writes the picked id back to the selector's state and re-renders, so the Apply
        // button below resolves to the newly-picked design.
        await ctx.Page!.Locator("select.design-pick").SelectOptionAsync(
            new Microsoft.Playwright.SelectOptionValue { Label = designLabel });
        // The selection lands (the bound state reflects the new pick) before we apply.
        // An <option> inside a closed <select> is never "visible", so wait for ATTACHED (it exists in the
        // DOM once the selection is reflected), not for visibility. Matches the pattern used for other
        // select option waits (e.g. ref-select in create form).
        var selected = ctx.Page!.Locator("select.design-pick option")
            .Filter(new() { HasTextString = designLabel });
        await selected.First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    [When("I apply the design")]
    public async Task WhenApply() =>
        await ctx.Page!.Locator("button.apply-design").ClickAsync();

    // ──── The per-row kebab (overflow) actions menu on the instances list ────────────────────

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

    // ──── The same kebab on the instance DETAIL page (/instances/<id>) — no Open item ────

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

    // ──── When: deleting a design (the two-step inline confirm) ────────────────────────────────────────

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

    // ──── When: a non-existent design id ─────────────────────────────────────────────────────────────────────────────────────

    [When("I open a non-existent design")]
    public async Task WhenOpenMissingDesign()
    {
        // Navigate straight to a design-editor URL whose id resolves to no design in db.designs (a high id
        // that the seeded library never reaches). The shell renders heading + Back; the body is the
        // not-found message — NOT `.design-editor` (that only mounts when a design id matches).
        await ctx.Page!.GotoReadyAsync(ctx.DesignerUrl("/designs/999999"));
        await ctx.Page!.WaitForSelectorAsync("main.ide-design-edit");
        await ctx.Page.Locator("main.ide-design-edit .not-found").WaitForAsync();
    }

    // ──── Then ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Then("the designs list shows a design {string}")]
    public async Task ThenDesignsListShows(string label) =>
        // The designs list renders via the generic <SetTable>: each design is a .set-row whose label
        // is the stretched a.row-link (label-only column), with an Edit link + Delete button per row.
        await Assert.That(await ctx.Page!.Locator(".set-row a.row-link", new() { HasTextString = label }).CountAsync())
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

    // ──── Then: the single create control is the generic New (the blocker fix) ─────────

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

    // ──── Then: Edit/Delete are clickable (no whole-row overlay) ─────────────────────────────────────

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

    // ──── Then: the two-step delete confirm ────────────────────────────────────────────────────────────────────────────────

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
        await Assert.That(await ctx.Page!.Locator(".set-row a.row-link", new() { HasTextString = label }).CountAsync())
            .IsGreaterThanOrEqualTo(1);

    [Then("the designs list eventually drops the design {string}")]
    public async Task ThenEventuallyDropped(string label)
    {
        // Yes runs db.designs.remove(d) — the row disappears client-side (the re-render), and the WS persist
        // commits it to the designer's sovereign store (GC included). Confirm both: the row leaves the DOM…
        await ctx.Page!.Locator(".set-row", new() {
            Has = ctx.Page.Locator("a.row-link", new() { HasTextString = label })
        }).WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        // …and the design is gone from the store (no Design object with that label survives).
        await EventuallyAsync(() => !_designer.Store.ReadExtent("Design").Values
            .Any(o => o.Fields.TryGetValue("label", out var v) && v is DeEnv.Storage.TextValue t && t.Text == label));
    }

    // ──── Then: nav active-state ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Then("the nav {string} link is active")]
    public async Task ThenNavActive(string label) =>
        await Assert.That(await ctx.Page!.Locator("nav.ide-nav a.is-active", new() { HasTextString = label }).CountAsync())
            .IsEqualTo(1);

    [Then("the nav {string} link is not active")]
    public async Task ThenNavNotActive(string label) =>
        await Assert.That(await ctx.Page!.Locator("nav.ide-nav a:not(.is-active)", new() { HasTextString = label }).CountAsync())
            .IsEqualTo(1);

    // ──── Then: a non-existent design id ─────────────────────────────────────────────────────────────────────────────────────

    [Then("the design editor shows a not-found message")]
    public async Task ThenEditorNotFound() =>
        await ctx.Page!.WaitForSelectorAsync("main.ide-design-edit .not-found");

    [Then("the design editor keeps its Back link")]
    public async Task ThenEditorKeepsBack() =>
        await Assert.That(await ctx.Page!.Locator("main.ide-design-edit a.back").CountAsync()).IsEqualTo(1);

    [Then("the design editor shows a type named {string}")]
    public async Task ThenEditorShowsType(string name)
    {
        var input = TypeNameInput(name);  // still uses [value] for identification in helper
        await input.WaitForAsync();
        await Assert.That(await input.InputValueAsync()).IsEqualTo(name);
    }

    [Then("the design editor shows the design's label {string}")]
    public async Task ThenEditorShowsLabel(string label)
    {
        // The editor's label is now an editable two-way-bound <input> (input.design-label = design.label);
        // a freshly-created design opens here with its label and otherwise-empty fields (an empty types
        // list, empty code areas) — a valid library entry, only invalid to DEPLOY until it gains types.
        var input = ctx.Page!.Locator("main.ide-design-edit .design-editor input.design-label");
        await input.WaitForAsync();
        await Assert.That(await input.InputValueAsync()).IsEqualTo(label);
    }

    // ──── Then/When: the editable design label (rename in the editor) ────────────────────────────

    [When("I rename the design's label to {string}")]
    public async Task WhenRenameDesignLabel(string newLabel)
    {
        // The editor's label input is two-way-bound to design.label; filling it edits the model and
        // autosaves a journaled scalar change (objectPropChange) to the designer's sovereign store.
        await ctx.Page!.Locator("main.ide-design-edit .design-editor input.design-label").FillAsync(newLabel);
        var input = ctx.Page.Locator("main.ide-design-edit .design-editor input.design-label");
        await input.WaitForAsync();
        await Assert.That(await input.InputValueAsync()).IsEqualTo(newLabel);
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
        await ctx.Page.Locator("main.ide-design-edit .design-editor .add-type").WaitForAsync();
    }

    [Then("the design editor's label input holds {string}")]
    public async Task ThenEditorLabelInputHolds(string label)
    {
        var input = ctx.Page!.Locator("main.ide-design-edit .design-editor input.design-label");
        await input.WaitForAsync();
        await Assert.That(await input.InputValueAsync()).IsEqualTo(label);
    }

    // ──── When/Then: the Commit-button UX slice (M13's last piece) ─────────────────────────────────

    [When("I type {string} into the commit message")]
    public async Task WhenTypeCommitMessage(string message)
        => await ctx.Page!.Locator("main.ide-design-edit .design-editor input.commit-message").FillAsync(message);

    // Just clicks — the commit message is NEVER cleared client-side (a UX review fix: a synchronous
    // clear both faked "done" before the server ack and destroyed the typed message on a rejected
    // commit), so this step makes no assumption about success. The positive confirmation is the
    // "Last commit:" line (updates on the success ack's refetch); a rejection surfaces as the global
    // error banner with the input untouched. Callers assert whichever leg they are testing.
    [When("I click Commit")]
    public async Task WhenClickCommit() =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor button.commit-design").ClickAsync();

    // The positive confirmation: the design editor's "Last commit:" line is pure Code reading the
    // design's main branch head, so it updates only once the success ack's refetch lands (ws.ts:947) —
    // poll, no fixed sleep.
    [Then("the last-commit line eventually shows message {string}")]
    public async Task ThenLastCommitLineShowsMessage(string message) =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor p.last-commit", new() { HasTextString = "\"" + message + "\"" }).WaitForAsync();

    // The bare-text variant (no quote-wrapping) — used for the "(no message)" placeholder, which the
    // Code renders WITHOUT the quote marks (only a real message gets wrapped in quotes).
    [Then("the last-commit line eventually shows {string}")]
    public async Task ThenLastCommitLineShowsText(string text) =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor p.last-commit", new() { HasTextString = text }).WaitForAsync();

    [Then("the global error banner is shown mentioning {string}")]
    public async Task ThenGlobalErrorBannerMentioning(string phrase)
    {
        var banner = ctx.Page!.Locator("#__error");
        await banner.WaitForAsync();
        await Assert.That(await banner.InnerTextAsync()).Contains(phrase);
    }

    [Then("the commit message input still holds {string}")]
    public async Task ThenCommitMessageInputStillHolds(string message) =>
        await Assert.That(await ctx.Page!.Locator("main.ide-design-edit .design-editor input.commit-message").InputValueAsync())
            .IsEqualTo(message);

    // Host-action success callback (docs/plans/host-action-success-signal.md) — the commit bar's
    // afterCommit clears commitMessage on the ok reply's refetch, which lands asynchronously (poll,
    // don't assert immediately after the click).
    [Then("the commit message input eventually holds {string}")]
    public async Task ThenCommitMessageInputEventuallyHolds(string message)
    {
        var input = ctx.Page!.Locator("main.ide-design-edit .design-editor input.commit-message");
        await input.WaitForAsync();
        await Assert.That(await input.InputValueAsync()).IsEqualTo(message);
    }

    [Then("the migration textarea eventually holds {string}")]
    public async Task ThenMigrationTextareaEventuallyHolds(string text)
    {
        var input = ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.commit-migration-input");
        await input.WaitForAsync();
        await Assert.That(await input.InputValueAsync()).IsEqualTo(text);
    }

    // The rejection leg's retained-migration proof: the callback never ran, so the textarea still
    // holds exactly what "I type a migration for ... into the migration textarea" typed.
    [Then("the migration textarea still holds the migration for {string}")]
    public async Task ThenMigrationTextareaStillHoldsMigrationFor(string typeName) =>
        await Assert.That(await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.commit-migration-input").InputValueAsync())
            .IsEqualTo($"fn {typeName}(old)\n    new.text = old.text");

    [When("I open the commit history")]
    public async Task WhenOpenCommitHistory()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor a.view-history").ClickAsync();
        await ctx.Page.WaitForSelectorAsync("main.ide-commits");
        await ctx.Page.Locator("main.ide-commits .set-row").First.WaitForAsync();
    }

    [Then("the commit history shows a commit with message {string}")]
    public async Task ThenCommitHistoryShowsMessage(string message) =>
        await ctx.Page!.Locator("main.ide-commits .set-row", new() { HasTextString = message }).WaitForAsync();

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
        await ctx.Page!.Locator("main.ide-commits .set-row", new() { HasTextString = message }).First.WaitForAsync();

    // B1: a history row is now a real link (linked restored) — clicking it navigates client-side to the
    // commit-detail page (/commits/<id>). Locate the row by its message and click its row-link.
    [When("I open the commit {string} from the history")]
    public async Task WhenOpenCommitFromHistory(string message)
    {
        await ctx.Page!.Locator("main.ide-commits .set-row a.row-link", new() { HasTextString = message }).ClickAsync();
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
        await ctx.Page!.Locator("main.ide-commit-detail .field-value", new() { HasTextString = message }).WaitForAsync();

    [Then("the commit detail page shows design {string}")]
    public async Task ThenCommitDetailShowsDesign(string design) =>
        await ctx.Page!.Locator("main.ide-commit-detail .field-value", new() { HasTextString = design }).WaitForAsync();

    [Then("the commit detail page shows author {string}")]
    public async Task ThenCommitDetailShowsAuthor(string author) =>
        await ctx.Page!.Locator("main.ide-commit-detail .commit-field", new() {
            Has = ctx.Page.Locator(".field-label", new() { HasTextString = "By" })
        }).Locator(".field-value", new() { HasTextString = author }).WaitForAsync();

    // Review fix 5 — the textarea→commitDesign→detail round-trip. The Migration input lives inside a
    // collapsed-by-default <details class="commit-migration">; click its <summary> to expand before
    // the textarea is fill-able (Playwright refuses to type into a hidden element).
    [When("I expand the Migration disclosure")]
    public async Task WhenExpandMigrationDisclosure() =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor details.commit-migration summary").ClickAsync();

    [When("I type a migration for {string} into the migration textarea")]
    public async Task WhenTypeMigrationTextarea(string typeName) =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.commit-migration-input").First
            .FillAsync($"fn {typeName}(old)\n    new.text = old.text");

    [Then("the commit detail page shows the migration source for {string}")]
    public async Task ThenCommitDetailShowsMigration(string typeName) =>
        await ctx.Page!.Locator(
            "main.ide-commit-detail .commit-migration-text", new() { HasTextString = $"fn {typeName}(old)" }).WaitForAsync();

    // B2 — the "Changes since parent" section. sys.diffCommits(parent, this) is a server-backed READ builtin
    // (computed server-side, shipped via the memo cache, reused by the client twin — like sys.schema). A
    // rename renders as ONE rename row ("From → To"), the identity-diff payoff — never a remove+add.
    [Then("the changes-since-parent shows a rename from {string} to {string}")]
    public async Task ThenChangesSinceParentRename(string from, string to) =>
        await ctx.Page!.Locator("main.ide-commit-detail .commit-diff .diff-rename", new() { HasTextString = from + " → " + to }).WaitForAsync();

    // The other half of the rename proof: a renamed type must NOT also surface as a removal — the diff joins
    // by intrinsic id, so the old name never appears in the "Removed" group.
    [Then("the changes-since-parent shows no removal of {string}")]
    public async Task ThenChangesSinceParentNoRemoval(string name)
    {
        // The rename row must be present first (proves the diff section rendered — otherwise "no removal"
        // could pass vacuously on a not-yet-hydrated page).
        await ctx.Page!.Locator("main.ide-commit-detail .commit-diff").WaitForAsync();
        await Assert.That(await ctx.Page!.Locator(".commit-diff .diff-remove", new() { HasTextString = name }).CountAsync())
            .IsEqualTo(0);
    }

    // ──── B3 — Publish + dry-run from the designer ────────────────────────────────────────────────────────────────────

    // Remove a leaf field from a type in the design editor (the prop-row's "×" remove button). Drives
    // arrayRemove on the type's nested props set; wait for the client edit AND the autosave to the designer's
    // store (a later commit snapshots the design, so the removal must have landed).
    [Then("the changes-since-parent shows an add of {string}")]
    public async Task ThenChangesSinceParentAdd(string path) =>
        await ctx.Page!.Locator("main.ide-commit-detail .commit-diff .diff-add", new() { HasTextString = path }).WaitForAsync();

    [When("I remove the field {string} from the type {string}")]
    public async Task WhenRemoveField(string propName, string typeName)
    {
        var row = ctx.Page!.Locator(
            "main.ide-design-edit .design-editor .type-card", new() {
                Has = ctx.Page.Locator($"input.type-name[value={CssString(typeName)}]")
            }).Locator(".prop-row", new() {
                Has = ctx.Page.Locator($"input.prop-name[value={CssString(propName)}]")
            });
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
        await PublishRowFor("todo").Locator(".publish-preview .publish-remove", new() { HasTextString = path }).WaitForAsync();

    [Then("the publish preview asks me to commit before publishing")]
    public async Task ThenPreviewAsksCommitFirst() =>
        await ctx.Page!.Locator(".publish-preview .publish-blocked", new() { HasTextString = "commit before publishing" }).WaitForAsync();

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
        await PublishRowFor("todo").Locator(".publish-preview .publish-rename", new() { HasTextString = from + " → " + to }).WaitForAsync();

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
        await PublishRowFor(label).Locator(".publish-preview .publish-uptodate").WaitForAsync();

    [Then("the publish row for instance {string} eventually shows {string}")]
    public async Task ThenPublishRowShows(string label, string text) =>
        await ctx.Page!.Locator(".publish-section .last-publish", new() { HasTextString = text }).WaitForAsync();

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
        });

    // ──── B4 — branches + merge from the design editor ─────────────────────────────────────────────────────────

    // Create a branch: type the name into the Branches-section input, click "+ Branch" (sys.createBranch —
    // a host action), then wait for the branch link to appear via the ack's refetch (the new Branch row is
    // GC-reachable via db.branches, and branchSection lists branches whose workingCopy shares the app's
    // lineage). Poll the DOM for the link — the refetch is async.
    [When("I create a branch named {string}")]
    public async Task WhenCreateBranch(string name)
    {
        await ctx.Page!.Locator(".branch-section input.branch-name").FillAsync(name);
        await ctx.Page.Locator(".branch-section button.create-branch").ClickAsync();
        await BranchLinkFor(name).WaitForAsync();
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
        await page.WaitForSelectorAsync("main.ide-design-edit .design-editor");
        await page.Locator("main.ide-design-edit .design-editor .add-type").WaitForAsync();
    }

    // Add a field to a named type: click that type-card's "+ Field", then name the just-added (empty-name)
    // prop. Waits for the client edit AND the autosave to the designer's store — a later reload / commit
    // re-reads the store (SSR prop rows draw value={prop.name} from persisted MetaProp rows).
    [When("I add a field {string} to the type {string}")]
    public async Task WhenAddField(string propName, string typeName)
    {
        var card = ctx.Page!.Locator("main.ide-design-edit .design-editor .type-card", new() {
            Has = ctx.Page.Locator($"input.type-name[value={CssString(typeName)}]")
        });
        // Always the LAST prop-row of THIS type card — the just-added field. Do not re-locate by
        // value={propName}: under load a remap/SSR paint can clear the name from the input, the
        // value-filter matches nothing, and Evaluate hung on a missing locator for the whole deadline.
        //
        // Under load, two failure modes show up as the same DesignerTestMs MetaProp timeout (incl. W1b
        // Counter scenarios that only need a Db.note field before authoring):
        //  1) arrayAdd never lands — propChange is dropped until the row is in pendingAdds (ws.ts).
        //  2) name write races remap — first write lost; re-fire after positive id is required.
        // Re-click + Field when the extent count never grows; re-fire name while count grew but name empty.
        var propsBefore = _designer.Store.ReadExtent("MetaProp").Values.Count();
        await card.Locator("button.add-prop").ClickAsync();
        var nameInput = card.Locator(".prop-row").Last.Locator("input.prop-name");
        await nameInput.WaitForAsync();
        await nameInput.FillAsync(propName);
        var deadline = DateTime.UtcNow.AddMilliseconds(TestTimeouts.DesignerTestMs);
        var lastRecreate = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            if (_designer.Store.ReadExtent("MetaProp").Values
                .Any(o => o.Fields.TryGetValue("name", out var v)
                    && v is DeEnv.Storage.TextValue t && t.Text == propName))
                return;

            var propsNow = _designer.Store.ReadExtent("MetaProp").Values.Count();
            // No create in the store for ~2s → the first "+ Field" / arrayAdd was lost under load; mint again.
            if (propsNow <= propsBefore && (DateTime.UtcNow - lastRecreate).TotalMilliseconds > 2000)
            {
                await card.Locator("button.add-prop").ClickAsync();
                lastRecreate = DateTime.UtcNow;
                nameInput = card.Locator(".prop-row").Last.Locator("input.prop-name");
                await nameInput.WaitForAsync();
                await nameInput.FillAsync(propName);
            }
            else
            {
                nameInput = card.Locator(".prop-row").Last.Locator("input.prop-name");
                await nameInput.EvaluateAsync(
                    "(el, v) => { el.value = v; el.dispatchEvent(new Event('input', { bubbles: true })); }",
                    propName);
            }
            await Task.Delay(50);
        }
        throw new TimeoutException(
            $"Timed out after {TestTimeouts.DesignerTestMs}ms waiting for MetaProp name '{propName}' in the designer store.");
    }

    // Rename a prop on a named type via its bound name input; wait for the client edit + the autosave, so a
    // later commit snapshots the renamed prop (the commit reads the store fresh).
    [When("I rename the prop {string} to {string} on the type {string}")]
    public async Task WhenRenameProp(string from, string to, string typeName)
    {
        var card = ctx.Page!.Locator("main.ide-design-edit .design-editor .type-card", new() {
            Has = ctx.Page.Locator($"input.type-name[value={CssString(typeName)}]")
        });
        var input = card.Locator(".prop-row", new() {
            Has = ctx.Page.Locator($"input.prop-name[value={CssString(from)}]")
        }).Locator("input.prop-name");
        await input.FillAsync(to);
        // Do not re-use the 'input' locator for post-fill waits/asserts: its definition includes
        // Has filter on the *original* value. Recreate using the new value (or rely on Fill + assert
        // DOM reflection; explicit WaitFor not needed per review). Matches pattern used for type renames.
        var renamedInput = card.Locator(".prop-row", new() {
            Has = ctx.Page.Locator($"input.prop-name[value={CssString(to)}]")
        }).Locator("input.prop-name");
        await Assert.That(await renamedInput.InputValueAsync()).IsEqualTo(to);
        // Same store wait as add-field: rename must land before a reload/commit re-reads MetaProp.
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values
            .Any(o => o.Fields.TryGetValue("name", out var v)
                && v is DeEnv.Storage.TextValue t && t.Text == to));
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
        await MergeRowFor(name).Locator(".merge-preview .merge-report").WaitForAsync();
    }

    [Then("the merge preview reports a clean merge")]
    public async Task ThenMergePreviewClean() =>
        await ctx.Page!.Locator(".merge-preview .merge-clean").WaitForAsync();

    [Then("the merge preview reports already up to date")]
    public async Task ThenMergePreviewUpToDate() =>
        await ctx.Page!.Locator(".merge-preview .merge-uptodate").WaitForAsync();

    [Then("the Branches section eventually shows {string}")]
    public async Task ThenBranchesShows(string text) =>
        await ctx.Page!.Locator(".branch-section", new() { HasTextString = text }).WaitForAsync();

    // The conflict's source row is labeled with the SOURCE BRANCH's real name (review fix — "source:"/
    // "target:" named the internal marker, not a branch, so the UI now reads "<branchName>: <value>" /
    // "this design: <value>"); the branch under test is always "feature" here.
    [Then("the merge preview shows a conflict with source {string} and target {string}")]
    public async Task ThenMergeConflictSourceTarget(string source, string target)
    {
        await ctx.Page!.Locator(".merge-conflict", new() {
            Has = ctx.Page.Locator(".merge-conflict-source", new() { HasTextString = "feature: " + source })
        }).Locator(".merge-conflict-target", new() { HasTextString = "this design: " + target }).WaitForAsync();
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
        await ctx.Page!.Locator(".merge-preview .merge-access .merge-access-row", new() { HasTextString = phrase }).WaitForAsync();

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
        });

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
        ctx.Page!.Locator(".branch-section .branch-list a.branch-link", new() { HasTextString = name });

    // The merge row for a branch in the Branches section (its head shows Merge "<name>").
    private Microsoft.Playwright.ILocator MergeRowFor(string name) =>
        ctx.Page!.Locator(".branch-section .branch-row", new() {
            Has = ctx.Page.Locator(".branch-link", new() { HasTextString = "Merge \"" + name + "\"" })
        });

    // The design editor's Publish-section row for an instance, located by its target name.
    private Microsoft.Playwright.ILocator PublishRowFor(string label) =>
        ctx.Page!.Locator(".publish-section .publish-row", new() {
            Has = ctx.Page.Locator(".publish-target", new() { HasTextString = label })
        });

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
    public async Task ThenEditorShowsUiText()
    {
        // The design's `ui` section text is bound into the code-area <textarea> (a real multi-line
        // editor); the seeded design's UI is a custom `fn render()`, so its text contains "fn render".
        // Wait for the element first (design "ui" is not shipped in list for privacy; editor does a
        // refetch). Then tolerate a short post-mount window for the bound value to appear under tight
        // per-action timeouts.
        var ta = ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui");
        // The ui textarea lives in the (initially closed) Advanced details disclosure. Wait for attached
        // (present in DOM) so we can read its value even while hidden; the scenario only asserts the
        // editor surface carries the design's ui source, not that the details is open.
        await ta.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        var deadline = DateTime.UtcNow.AddMilliseconds(TestTimeouts.DesignerActionMs);
        string val = "";
        while (DateTime.UtcNow < deadline)
        {
            val = await ta.InputValueAsync();
            if (val.Contains("fn render")) return;
            await Task.Delay(25);
        }
        await Assert.That(val).Contains("fn render");
    }


    [When("I expand the Advanced code disclosure")]
    public async Task WhenExpandAdvancedCode() =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor details.code-areas summary").ClickAsync();

    [When("I type this access section:")]
    public async Task WhenTypeAccessSection(string accessSection)
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-access").FillAsync(accessSection);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "todo" }
            && o.Fields.TryGetValue("access", out var av) && av is DeEnv.Storage.TextValue at && at.Text == accessSection));
    }

    // ──── M12 X2b — the Convert-to-structured button + the structured render view ────────────────────────────────────

    // A SIMPLE convertible render: a `fn render()` returning a `<main>` with an attribute and a nested
    // element whose child is a text literal — the exact shape S1b's ImportRender accepts (no foreach / if /
    // helper components, which it refuses). Filled into the editable `ui` textarea (bound to
    // sys.field(design,"ui"), a journaled scalar autosave like the access textarea); polled on the store so
    // the write has landed before we convert.
    // The stored `ui` field carries the `ui` SECTION (header + indented body) — the exact text
    // AppPrint.PrintUi emits and SchemaBridge.ImportRender re-parses via ParseUiSection (which expects the
    // `ui` header). So author the whole section, render body indented under `fn render()`.
    private const string SimpleConvertibleRender =
        """
        ui
            fn render()
                return <main class="greeting">
                    <h1>
                        "Hi"

        """;

    [When("I author a simple convertible render into the design's UI")]
    public async Task WhenAuthorSimpleRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(SimpleConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "convertme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == SimpleConvertibleRender));
    }

    // The convert button lives inside the Advanced (code) <details> disclosure. Its open/closed state is
    // uncontrolled DOM (not model-bound), so the autosave re-render after authoring the render rebuilds the
    // disclosure CLOSED — the button is present but hidden. Assert it is ATTACHED (the mode-conditional
    // rendered it), independent of the disclosure's transient open state.
    [Then("the design editor shows the Convert-to-structured button")]
    public async Task ThenShowsConvertButton()
    {
        var btn = ctx.Page!.Locator("main.ide-design-edit .design-editor button.convert-render").First;
        await btn.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    [When("I click Convert to structured")]
    public async Task WhenClickConvert()
    {
        // The convert button lives under the Advanced (code) disclosure (a text design's `ui` is "advanced
        // code"); its open state is UNCONTROLLED DOM that the authoring autosave re-render — possibly still
        // in flight from the previous step — rebuilds CLOSED, hiding the button. A normal (actionability-
        // gated) click then RACES that re-render: whenever a collapse lands in the gap the button is hidden
        // and the click's visibility wait runs out the clock (the 30s flake) — and a longer deadline can't
        // cure it, the button simply keeps getting re-hidden. The button is always in the DOM though
        // (ThenShowsConvertButton gated on it ATTACHED) and its click handler fires regardless of the
        // disclosure's visual open state, so dispatch the click directly on the element — deterministic, no
        // hit-testing race. This step tests the CONVERT behaviour (asserted next), not click mechanics; the
        // RESULT — the structured render section — is first-class, OUTSIDE this disclosure.
        var btn = ctx.Page!.Locator("main.ide-design-edit .design-editor button.convert-render").First;
        await btn.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        // Dispatch click without actionability/visibility gates — the disclosure can re-close mid-wait.
        await btn.EvaluateAsync("el => el.click()");
    }

    // ──── M12 E1 — the structured-render TREE EDITOR (recursive renderNodeEditor) ────────────────────────────────────

    // A NESTED convertible render: <main class="x"><h1>{leaf}</h1></main> — an element with an attribute,
    // a nested ELEMENT child (h1), whose own child is a text-EXPRESSION leaf ({leaf}). Its structure
    // forces the recursion to descend a level (main → h1) and to render both an element and a leaf node,
    // so the tree editor's nesting + leaf handling are both exercised. Same authoring plumbing as the
    // simple render: fill the `ui` textarea, poll the store for the write.
    private const string NestedConvertibleRender =
        """
        ui
            fn render()
                return <main class="x">
                    <h1>
                        leaf

        """;

    [When("I author a nested convertible render into the design's UI")]
    public async Task WhenAuthorNestedRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(NestedConvertibleRender);
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
        """
        ui
            fn render()
                return <main class="x">
                    <h1>
                        db.greeting

        """;

    [When("I author a projectable nested render into the design's UI")]
    public async Task WhenAuthorProjectableNestedRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(ProjectableNestedRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "treeme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == ProjectableNestedRender));
    }

    // A LITERAL render (no `db.` reference at all) — imports fine regardless of the type schema's
    // validity, so it isolates the eval-degrade-banner repro to the fieldless type alone (the render
    // itself is never the cause of the evalContext failure). Wraps the leaf in <h1> so the existing
    // ThenCanvasShowsEvaluatedText step (`.design-canvas h1`) can assert the post-fix evaluated text too.
    private const string LiteralConvertibleRender =
        """
        ui
            fn render()
                return <h1>
                    "Hello"

        """;

    [When("I author a literal convertible render into the design's UI")]
    public async Task WhenAuthorLiteralRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(LiteralConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "brokenme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == LiteralConvertibleRender));
    }

    // ──── M12 F1 — structured fns: the Components editor area ─────────────────────────────────────────────────────────────────────────────

    // A convertible render whose `ui` carries a COMPONENT function (`NoteCard(note)`, single-return
    // element) besides `fn render()` — the shape F1's import lifts the old refusal for. Same authoring
    // plumbing as the other convertible-render fixtures (fill the `ui` textarea, poll the store).
    private const string ComponentConvertibleRender =
        """
        ui
            fn NoteCard(note)
                return <li>
                    note.title
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with a component function into the design's UI")]
    public async Task WhenAuthorComponentRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(ComponentConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "compme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == ComponentConvertibleRender));
    }

    // The imported function shows as a `.fn-card` in the first-class "Components" area: a `name` input and
    // a comma-separated `params` input, both two-way-bound to the MetaFn row.
    [Then("the Components area shows a component named {string} with params {string}")]
    public async Task ThenComponentsAreaShowsFn(string name, string paramsText)
    {
        var card = ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-card")
            .Filter(new() { Has = ctx.Page.Locator($"input.fn-name[value={CssString(name)}]") });
        await card.WaitForAsync();
        var paramsInput = card.Locator("input.fn-params");
        await paramsInput.WaitForAsync();
        await Assert.That(await paramsInput.InputValueAsync()).IsEqualTo(paramsText);
    }

    // Locate the fn-card by its CURRENT name input value (mirrors JustAddedTypeRow's by-value lookup).
    [When("I edit the component {string}'s params to {string}")]
    public async Task WhenEditComponentParams(string name, string newParams) =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-card", new() {
            Has = ctx.Page.Locator($"input.fn-name[value=\"{name}\"]")
        }).Locator("input.fn-params").FillAsync(newParams);

    [Then("the stored component {string} has params {string}")]
    public async Task ThenStoredComponentParams(string name, string paramsText) =>
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaFn").Values.Any(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == name
            && o.Fields.TryGetValue("params", out var p) && p is DeEnv.Storage.TextValue pt && pt.Text == paramsText));

    // ──── M12 U1 — MetaUse rows: the Configurations editor + static per-configuration preview ─────────────
    //
    // A "configuration" (MetaUse) under a component card: a name input + its args (MetaAttr rows,
    // mirroring the render tree's own attr editing — `.node-attr`/`.node-attr-name`/`.node-attr-value`
    // reused verbatim, styling included), and a per-configuration STATIC preview panel — the component's
    // REAL rendered content with the configuration's args bound, via the same F2 expansion the main
    // canvas already uses (a synthesized transient invocation node fed to `sys.renderTree`). Every
    // scenario using these steps has exactly ONE component card, so queries are unscoped by fn name —
    // the same unscoped convention the design-level-state-var steps above use.

    [When("I click the add-configuration button")]
    public async Task WhenClickAddConfiguration()
    {
        // Prefer the first card that exposes Configurations (a named component under test), not
        // necessarily the last .fn-card in document order.
        var card = ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-card")
            .Filter(new() { Has = ctx.Page.Locator("button.add-use") }).First;
        await card.Locator(".add-use").First.ClickAsync();
    }

    private Microsoft.Playwright.ILocator ComponentCard() =>
        ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-card").Last;

    // Global document order of configuration rows (across every fn-card) — same index space as
    // ThenConfiguration* / LiveInstancePreview. Do not scope to ComponentCard().Last: after convert
    // the last card is not always the NoteCard under test.
    private Microsoft.Playwright.ILocator ConfigRow(int index) =>
        ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-uses .use-row").Nth(index);

    [Then("component configurations shows {int} row(s)")]
    public async Task ThenConfigurationsShowsCount(int count)
    {
        var locator = ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-uses .use-row");
        if (count == 0)
            await locator.First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        else
            await locator.Nth(count - 1).WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    [Then("configuration {int} shows the {string} hint")]
    public async Task ThenConfigurationShowsHint(int index, string hintText)
    {
        var hint = ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-uses .use-row")
            .Nth(index).Locator(".use-name-hint");
        await hint.WaitForAsync();
        await Assert.That(await hint.InnerTextAsync()).Contains(hintText);
    }

    [When("I set configuration {int}'s name to {string}")]
    public async Task WhenSetConfigurationName(int index, string name)
    {
        var row = ConfigRow(index);
        var input = row.Locator("input.use-name");
        await input.FillAsync(name);
        await input.WaitForAsync();
    }

    [When("I add an arg to configuration {int}")]
    public async Task WhenAddConfigurationArg(int index) =>
        await ConfigRow(index).Locator(".use-args button.add-attr").ClickAsync();

    [When("I set configuration {int}'s arg {int} name to {string}")]
    public async Task WhenSetConfigurationArgName(int useIndex, int argIndex, string name)
    {
        // Scope under .use-args so ambient MetaAttr rows (same attrRow classes) never cross-index.
        await ConfigRow(useIndex).Locator(".use-args input.node-attr-name").Nth(argIndex).FillAsync(name);
        // Journaled autosave: the workbench binds attrs by the *stored* name. Without this wait, a
        // rename ("nope" → "note") can still be "nope" on the server when the value step refreshes
        // evalContext — param `note` then binds null and the preview mounts an empty <li> forever.
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaAttr").Values
            .Any(o => o.Fields.TryGetValue("name", out var v)
                && v is DeEnv.Storage.TextValue t && t.Text == name));
    }

    [When("I set configuration {int}'s arg {int} value to {string}")]
    public async Task WhenSetConfigurationArgValue(int useIndex, int argIndex, string value)
    {
        var args = ConfigRow(useIndex).Locator(".use-args");
        // Pair name+value on the same MetaAttr before refresh: a value-only wait can pass while the
        // name is still a prior typo (the Configurations scenario renames nope→note right before this).
        var name = await args.Locator("input.node-attr-name").Nth(argIndex).InputValueAsync();
        var input = args.Locator("input.node-attr-value").Nth(argIndex);
        await input.FillAsync(value);
        // Binding must hit the designer store BEFORE refresh-eval: BuildEvalContext collects MetaUse arg
        // sources from the store; a premature refresh ships exprs without this value and the workbench
        // mounts with note unbound → empty <li> forever (no auto re-parse of use-args until another edit).
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaAttr").Values
            .Any(o => o.Fields.TryGetValue("value", out var vv)
                && vv is DeEnv.Storage.TextValue vt && vt.Text == value
                && o.Fields.TryGetValue("name", out var nv)
                && nv is DeEnv.Storage.TextValue nt && nt.Text == name));
        // Force a new evalContext so ctx.exprs includes the arg source, then Reset the live instance so
        // the workbench re-binds args against a fresh seed copy (config signature may already match if the
        // client had the value before the AST shipped, leaving an empty first mount stuck).
        await ctx.Page!.Locator("main.ide-design-edit .design-editor button.refresh-eval").First.ClickAsync();
        var reset = LiveInstancePreview(useIndex).Locator(".workbench-instance-reset");
        if (await reset.CountAsync() > 0)
            await reset.ClickAsync();
    }

    [When("I add an ambient to configuration {int}")]
    public async Task WhenAddConfigurationAmbient(int index) =>
        await ConfigRow(index).Locator(".use-ambients button.add-ambient").ClickAsync();

    [When("I set configuration {int}'s ambient {int} name to {string}")]
    public async Task WhenSetConfigurationAmbientName(int useIndex, int ambientIndex, string name)
    {
        await ConfigRow(useIndex).Locator(".use-ambients input.node-attr-name").Nth(ambientIndex).FillAsync(name);
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaAttr").Values
            .Any(o => o.Fields.TryGetValue("name", out var v)
                && v is DeEnv.Storage.TextValue t && t.Text == name));
    }

    // Quote-wrapping helper: Gherkin string escaping of "Admin" is fragile across parsers (often lands
    // as backslash-quotes, which are not Code string literals). Prefer this for simple string fakes.
    [When("I set configuration {int}'s ambient {int} value to a quoted string {word}")]
    public Task WhenSetConfigurationAmbientQuotedString(int useIndex, int ambientIndex, string raw) =>
        WhenSetConfigurationAmbientValue(useIndex, ambientIndex, "\"" + raw + "\"");

    [When("I set configuration {int}'s ambient {int} value to {string}")]
    public async Task WhenSetConfigurationAmbientValue(int useIndex, int ambientIndex, string value)
    {
        var ambients = ConfigRow(useIndex).Locator(".use-ambients");
        var name = await ambients.Locator("input.node-attr-name").Nth(ambientIndex).InputValueAsync();
        var input = ambients.Locator("input.node-attr-value").Nth(ambientIndex);
        await input.FillAsync(value);
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaAttr").Values
            .Any(o => o.Fields.TryGetValue("value", out var vv)
                && vv is DeEnv.Storage.TextValue vt && vt.Text == value
                && o.Fields.TryGetValue("name", out var nv)
                && nv is DeEnv.Storage.TextValue nt && nt.Text == name));
        // Pin that some MetaUse.ambients set actually contains a row with this name+value (not an orphan
        // MetaAttr). The extent-only wait above can pass while set membership is still catching up — without
        // this, workbench bindUseAmbients still sees an empty ambients array and the fake never lands.
        await EventuallyAsync(() =>
        {
            foreach (var use in _designer.Store.ReadExtent("MetaUse").Values)
            {
                if (use.Fields.GetValueOrDefault("ambients") is not DeEnv.Storage.SetValue set) continue;
                foreach (var id in set.Members.Keys)
                {
                    if (!_designer.Store.ReadExtent("MetaAttr").TryGetValue(id, out var attr)) continue;
                    if (attr.Fields.GetValueOrDefault("name") is DeEnv.Storage.TextValue nt && nt.Text == name
                        && attr.Fields.GetValueOrDefault("value") is DeEnv.Storage.TextValue vt && vt.Text == value)
                        return true;
                }
            }
            return false;
        });
        // Ship ambient source into ctx.exprs, then Reset so bindUseAmbients re-runs against a fresh mount.
        await ctx.Page!.Locator("main.ide-design-edit .design-editor button.refresh-eval").First.ClickAsync();
        var reset = LiveInstancePreview(useIndex).Locator(".workbench-instance-reset");
        if (await reset.CountAsync() > 0)
            await reset.ClickAsync();
    }

    // ux review — a typo'd arg name is currently byte-identical to no arg at all (both bind ExecNull);
    // the hint span (`.attr-name-hint`) is a SIBLING right after that specific arg's `.node-attr` row
    // (attrRow's own markup, shared with the tree editor, carries no such hint — it is layered on only
    // at THIS call site), so it is found via nextElementSibling off the Nth `.node-attr`, not nested
    // inside it. Scoped under .use-args so ambient rows never collide.
    [Then("configuration {int}'s arg {int} shows the {string} hint")]
    public async Task ThenConfigurationArgShowsHint(int useIndex, int argIndex, string hintText)
    {
        var row = ConfigRow(useIndex).Locator(".use-args");
        var attr = row.Locator(".node-attr").Nth(argIndex);
        // The hint is emitted as the next sibling after its attr div when present.
        var hint = attr.Locator("xpath=following-sibling::span[contains(@class, 'attr-name-hint')]");
        await hint.First.WaitForAsync();
        await Assert.That(await hint.First.InnerTextAsync()).Contains(hintText);
    }

    [Then("configuration {int}'s arg {int} shows no hint")]
    public async Task ThenConfigurationArgShowsNoHint(int useIndex, int argIndex)
    {
        var row = ConfigRow(useIndex).Locator(".use-args");
        var attr = row.Locator(".node-attr").Nth(argIndex);
        var hint = attr.Locator("xpath=following-sibling::span[contains(@class, 'attr-name-hint')]");
        // Either no following sibling hint, or it is detached/not matching.
        var count = await hint.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    // Scoped to THIS configuration's OWN `.use-preview` panel. The live mount puts content inside
    // .workbench-instance-content (or directly for static renderTree). Search broadly so we find the
    // rendered output from either path. Use includes for whitespace tolerance.
    [Then("configuration {int}'s preview shows a {string} element reading {string}")]
    public async Task ThenConfigurationPreviewShowsElement(int index, string tag, string text)
    {
        var row = ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-uses .use-row").Nth(index);
        var preview = row.Locator(".use-preview");
        try
        {
            // Wait for the element inside the preview (handles static renderTree or live mount).
            var container = preview.Locator(".workbench-instance-content").Or(preview);
            await container.Locator(tag, new() { HasTextString = text }).First.WaitForAsync();
        }
        catch (TimeoutException)
        {
            var previewHtml = await preview.InnerHTMLAsync();
            throw new TimeoutException($"Preview for config {index} did not show <{tag}> with '{text}'. Actual content at assert: {previewHtml}");
        }
    }

    [When("I remove configuration {int}")]
    public async Task WhenRemoveConfiguration(int index) =>
        await ConfigRow(index).Locator("button.remove-use").ClickAsync();

    // ──── M12 W1a — the live-instance driver (workbench.ts) ─────────────────────────────────────────────────────────────────────────────────
    //
    // Distinguishes a LIVE-mounted instance from U1's static row-walk preview by the marker the row-walk
    // (renderTreeNode) stamps on EVERY element it emits ("data-node", the canvas's own click-to-select
    // provenance attribute — codeExec.ts:1264) and the REAL runtime NEVER emits (that is canvas-only
    // instrumentation) — so an element with the expected text AND no data-node can only have come from the
    // real component invocation the workbench driver runs, not the static walk.
    [Then("configuration {int}'s live instance shows a {string} element reading {string}")]
    public async Task ThenConfigurationLiveInstanceShowsElement(int index, string tag, string text)
    {
        var row = ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-uses .use-row").Nth(index);
        var preview = row.Locator(".use-preview");
        // Live mount only: static U1 renderTree stamps data-node on every element; the real workbench
        // never does. Scope to .workbench-instance-content so toolbar chrome can't satisfy the assert.
        //
        // Empty text: Playwright HasTextString("") does NOT match empty elements (Field isolation
        // scenario — sibling echo <span class="echo"></span> is present but the locator never
        // resolves). Poll trimmed textContent instead. Non-empty: native HasTextString (TESTING.md).
        try
        {
            var content = preview.Locator(".workbench-instance-content");
            await content.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
            if (text.Length == 0)
            {
                // Prefer the fixture echo when present (schema Field scenarios); else any empty live tag.
                await ctx.Page!.WaitForFunctionAsync(
                    $"() => {{ const rows = document.querySelectorAll('main.ide-design-edit .design-editor .components-section .fn-uses .use-row'); " +
                    $"const r = rows[{index}]; if (r == null) return false; " +
                    $"const content = r.querySelector('.use-preview .workbench-instance-content'); if (content == null) return false; " +
                    $"const echo = content.querySelector('span.echo'); " +
                    $"if (echo != null && !echo.hasAttribute('data-node')) return (echo.textContent ?? '').trim() === ''; " +
                    $"return [...content.querySelectorAll({JsString(tag)})].some(e => !e.hasAttribute('data-node') && (e.textContent ?? '').trim() === ''); }}");
            }
            else
            {
                var live = content.Locator($"{tag}:not([data-node])", new() { HasTextString = text }).First;
                // <option> (and similar) are in the accessibility tree but Playwright does not treat them
                // as Visible unless the <select> is open — RefSelect seedlib asserts option text while the
                // closed select already lists Alpha/Beta in the DOM (failure dump had the option present).
                // Attached is the real contract for those tags; Visible for ordinary content (buttons, li).
                var state = tag.Equals("option", StringComparison.OrdinalIgnoreCase)
                    || tag.Equals("optgroup", StringComparison.OrdinalIgnoreCase)
                    ? Microsoft.Playwright.WaitForSelectorState.Attached
                    : Microsoft.Playwright.WaitForSelectorState.Visible;
                await live.WaitForAsync(new() { State = state });
            }
        }
        catch (TimeoutException ex)
        {
            var previewHtml = await preview.InnerHTMLAsync();
            throw new TimeoutException(
                $"Configuration {index}'s live instance did not show <{tag}> reading '{text}'. Actual preview: {previewHtml}", ex);
        }
    }

    // The v1 fidelity boundary made honest (design doc): a component whose render throws shows the REAL
    // error text in the card, as `.instance-error`.
    [Then("configuration {int}'s live instance shows the error {string}")]
    public async Task ThenConfigurationLiveInstanceShowsError(int index, string message)
    {
        var preview = ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-uses .use-row").Nth(index)
            .Locator(".use-preview");
        try
        {
            await preview.Locator(".instance-error", new() { HasTextString = message }).First.WaitForAsync();
        }
        catch (TimeoutException)
        {
            var previewHtml = await preview.InnerHTMLAsync();
            var anyErr = preview.Locator(".instance-error");
            var errText = await anyErr.CountAsync() > 0 ? await anyErr.First.InnerTextAsync() : "(no .instance-error)";
            throw new TimeoutException(
                $"Configuration {index}'s live instance did not show error '{message}'. " +
                $"Any error text: {errText}. Actual preview: {previewHtml}");
        }
    }

    // Stamps the mounted instance's first element with a test-only marker — the opaque-container pin: an
    // UNTOUCHED (idempotent) mount hook pass never rebuilds this element, so the marker surviving an
    // unrelated page re-render (below) proves the page never clobbered the driver's own live DOM.
    [When("I mark configuration {int}'s live instance node")]
    public async Task WhenMarkConfigurationLiveInstanceNode(int index) =>
        await ctx.Page!.EvaluateAsync(
            $"() => {{ const rows = document.querySelectorAll('main.ide-design-edit .design-editor .components-section .fn-uses .use-row'); " +
            $"const r = rows[{index}]; const preview = r.querySelector('.use-preview'); " +
            $"const el = preview.firstElementChild; el.setAttribute('data-test-marker', 'kept'); }}");

    [Then("configuration {int}'s live instance node is unchanged since marking")]
    public async Task ThenConfigurationLiveInstanceNodeUnchanged(int index) =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-uses .use-row").Nth(index)
            .Locator(".use-preview [data-test-marker='kept']").First.WaitForAsync();

    // A component that reads an AMBIENT (currentUser) with NO MetaUse.ambients fake — still a miss
    // against the workbench sandbox (canvas-never-lies). Isolation scenarios pin the error path;
    // happy-path uses GreeterAmbientReadingComponentConvertibleRender + an authored ambient row.
    private const string AmbientReadingComponentConvertibleRender =
        """
        ui
            fn Broken()
                return <div>
                    currentUser
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with an ambient-reading component into the design's UI")]
    public async Task WhenAuthorAmbientReadingComponentRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(AmbientReadingComponentConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "brokencomp" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == AmbientReadingComponentConvertibleRender));
    }

    // Happy-path ambient fixture: reads currentUser (string fake "Admin") — assertable text; object-shaped
    // fakes ({ role: "Admin" }) are also supported by the binder but string is the cheapest pin.
    private const string GreeterAmbientReadingComponentConvertibleRender =
        """
        ui
            fn Greeter()
                return <div class="who">
                    currentUser
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with a greeter ambient-reading component into the design's UI")]
    public async Task WhenAuthorGreeterAmbientReadingComponentRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(GreeterAmbientReadingComponentConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "ambientfake" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == GreeterAmbientReadingComponentConvertibleRender));
    }

    // ──── M12 W1b — the live-instance driver: events + Reset through the dispatch bracket ────────────────────────

    // A component whose handler actually CLICKS need REACTIVE local state — `var state = { count: 0 }` (an
    // OBJECT), not a bare scalar var. This is the framework's OWN established idiom for component-local
    // state that must re-render on change (GenericUi.cs's KebabMenu: `var state = { open: false }`,
    // `state.open = ...`): an object-prop write invalidates by (object id, prop) regardless of scope — a
    // PLAIN scalar var write only invalidates when the var lives in the page's TOP scope (codeExec.ts
    // executeAssignment's symbol branch — `if (itemScope.isTop) invalidateVar(...)`), which a component's
    // OWN local `var` never is. The EXISTING W1a fixture (StatefulComponentConvertibleRender, `var count =
    // 0`) only asserts its INITIAL render — never clicks it — so this gap stays latent there; W1b's own
    // scenarios click-and-observe, so they need the reactive shape. A SEPARATE fixture (not editing the
    // existing one) keeps the already-reviewed W1a scenario untouched.
    private const string ReactiveCounterConvertibleRender =
        """
        ui
            fn Counter()
                var state = { count: 0 }
                fn render()
                    return <button onClick={() => state.count = state.count + 1}>
                        state.count
                return render
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with a reactive Counter component into the design's UI")]
    public async Task WhenAuthorReactiveCounterRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(ReactiveCounterConvertibleRender);
        // Any design label — scenarios reuse this fixture under different names (wbcounterme, wbhistory, …).
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == ReactiveCounterConvertibleRender));
    }

    // A two-way-bound local var (`value={state.text}`) inside a stateful component — the shape wireEvents'
    // own input/textarea binding needs, mirrored by W1b's instanceWiring. `state.text` (not a bare scalar,
    // for the same reactivity reason as ReactiveCounterConvertibleRender above) — the echo <span> makes the
    // REPAINT (not just the underlying model write) directly observable. The bare `<a href>` (no onClick)
    // is the anchor-containment pin (arch review fold): it has NO wired handler at all, so only the
    // container-level click swallow (workbench.ts ensureInstanceContent) stops it reaching the page's
    // document-level interceptNavigation.
    private const string TwoWayComponentConvertibleRender =
        """
        ui
            fn TextBox()
                var state = { text: "" }
                fn render()
                    return <div>
                        <input class="tb-input" value={state.text}>
                        <span class="tb-echo">
                            state.text
                        <a class="tb-link" href="/designs">
                            "Go to designs"
                return render
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with a two-way-bound TextBox component into the design's UI")]
    public async Task WhenAuthorTwoWayComponentRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(TwoWayComponentConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "twowayme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == TwoWayComponentConvertibleRender));
    }

    // A component whose handler fires sys.logout() — the session-safety pin (component-workbench.md's
    // "grill's core fix"): sendLogout is NOT id-gated (codeExec.ts execLogout calls it unconditionally), so
    // ONLY the dispatch bracket's wsHooks-null is what stops a card's click from really logging the
    // operator's own page session out.
    private const string LogoutComponentConvertibleRender =
        """
        ui
            fn LogoutButton()
                return <button class="wb-logout" onClick={() => sys.logout()}>
                    "Log out (sandboxed)"
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with a sandboxed logout button component into the design's UI")]
    public async Task WhenAuthorLogoutComponentRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(LogoutComponentConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "logoutme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == LogoutComponentConvertibleRender));
    }

    // A Thrower (a handler that reads an unseeded AMBIENT — still a v1-fidelity-boundary miss even after
    // M12 W1c seeds schema:/extent:, same as AmbientReadingComponentConvertibleRender at render time)
    // alongside an ordinary REACTIVE Counter (see ReactiveCounterConvertibleRender's doc comment —
    // `var state = { count: 0 }`, not a bare scalar), in ONE design: proves a throwing instance's handler
    // error never touches a SIBLING instance's own liveness, nor the page's.
    // Code has no `throw` statement — the handler assigns from the unseeded ambient `currentUser` so the
    // workbench sandbox raises "Variable currentUser not found" (same message as ambient-at-render).
    // Stateful setup/view shape so structured import keeps a live onClick (same as Counter).
    private const string ThrowerAndCounterConvertibleRender =
        """
        ui
            fn Thrower()
                var state = { n: 0 }
                fn render()
                    return <button class="wb-throw" onClick={() => state.n = currentUser}>
                        "boom"
                return render
            fn Counter()
                var state = { count: 0 }
                fn render()
                    return <button onClick={() => state.count = state.count + 1}>
                        state.count
                return render
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with a throwing component and a Counter component into the design's UI")]
    public async Task WhenAuthorThrowerAndCounterRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(ThrowerAndCounterConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "throwme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == ThrowerAndCounterConvertibleRender));
    }

    // ──── M12 W1c — sandbox cache seeding: schema:/extent:/canWrite:/canRead: + library binding ────────────

    // A component composing the LIBRARY's own <Field> over sys.schema/sys.new — the ObjectForm-class
    // generic pattern the v1 boundary excluded until the private cache is seeded from the design's OWN
    // rows (BuildEvalContext's `types` payload). No var/setup split needed (single-return, stateless).
    private const string SchemaFieldComponentConvertibleRender =
        """
        ui
            fn Editor()
                return <Field obj={sys.new(sys.schema("Note"))} desc={sys.schema("Note", "title")}>
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with a schema-backed Field component into the design's UI")]
    public async Task WhenAuthorSchemaFieldComponentRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(SchemaFieldComponentConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "seedschema" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == SchemaFieldComponentConvertibleRender));
    }

    // sys.extent("Note") over the seed data — the instance's OWN deep-copied "notes" set IS the extent
    // (seedExtentCache's per-instance client-side derivation).
    private const string ExtentListingComponentConvertibleRender =
        """
        ui
            fn Lister()
                return <ul>
                    foreach n in sys.extent("Note")
                        <li>
                            n.title
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with an extent-listing component into the design's UI")]
    public async Task WhenAuthorExtentListingComponentRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(ExtentListingComponentConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "seedextent" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == ExtentListingComponentConvertibleRender));
    }

    // A STATEFUL component holding a sys.new-minted, sys.schema-backed draft in `var state` — a real
    // <Field> two-way-binds into it (sys.field's setValue, the SAME idiom RefEditor/ObjectForm use
    // throughout the library), with an echo <span> making the write directly observable per instance.
    private const string StatefulSchemaFieldComponentConvertibleRender =
        """
        ui
            fn Editor()
                var state = { draft: sys.new(sys.schema("Note")) }
                fn render()
                    return <div>
                        <Field obj={state.draft} desc={sys.schema("Note", "title")}>
                        <span class="echo">
                            sys.field(state.draft, "title")
                return render
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with a stateful schema-backed Editor component into the design's UI")]
    public async Task WhenAuthorStatefulSchemaFieldComponentRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(StatefulSchemaFieldComponentConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "seedfieldtype" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == StatefulSchemaFieldComponentConvertibleRender));
    }

    // A handler that ADDS a fresh sys.new-minted row to db's own set (db.notes.add) then re-lists
    // sys.extent("Note") — proves extent is re-derived every render pass (mutation-consistent) and Reset
    // discards the addition along with the rest of the sandbox (component-workbench.md's whole-sandbox
    // Reset semantics, now covering the seeded extent too). STATEFUL (var + nested render()): sys.schema
    // is read ONCE at SETUP time into `noteDesc` — a HANDLER runs under memoBypass (codeExec.ts memoize's
    // very first line skips the cache lookup entirely whenever memoBypass is set — a general interpreter
    // property, not workbench-specific), so calling sys.schema(...) FRESH *inside* the onClick handler
    // would always throw "Value not available" regardless of seeding; capturing the descriptor as a var
    // and reading it back (a plain symbol lookup, no memoize involved) is the same idiom GenericUi's own
    // RefEditor.closeCreate uses (`state.draft = sys.new(target)`, `target` a captured param, never a
    // fresh sys.schema call). No separately-named helper fn (TryMatchStatefulShape's stateful shape
    // refuses a component with an EXTRA fn alongside render() — component-workbench's own "GenericUi's
    // ConfirmButton/KebabMenu" import gap) — the handler is an inline lambda.
    private const string ExtentAddingComponentConvertibleRender =
        """
        ui
            fn AddNote()
                var noteDesc = sys.schema("Note")
                fn render()
                    return <div>
                        <button onClick={() => db.notes.add(sys.new(noteDesc))}>
                            "Add"
                        <ul>
                            foreach n in sys.extent("Note")
                                <li>
                                    n.title
                return render
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with an extent-adding component into the design's UI")]
    public async Task WhenAuthorExtentAddingComponentRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(ExtentAddingComponentConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "seedresetextent" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == ExtentAddingComponentConvertibleRender));
    }

    // A LIBRARY component (RefSelect) composing sys.extent for its OWN candidates — the "lib components
    // render as empty literal elements" v1 boundary this slice lifts (ctx.lib, bound into the sandbox
    // scope alongside the design's own ctx.fns). Stateless wrapper (no var needed — a fresh sys.new draft
    // per render is fine; this scenario never clicks the select).
    private const string RefSelectComponentConvertibleRender =
        """
        ui
            fn Picker()
                return <RefSelect parent={sys.new(sys.schema("Db"))} prop="pick" candidates={sys.extent("Note")} labelProp="title">
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with a RefSelect component into the design's UI")]
    public async Task WhenAuthorRefSelectComponentRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(RefSelectComponentConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "seedlib" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == RefSelectComponentConvertibleRender));
    }

    // The element-COUNT variant of "shows a {tag} element reading {text}" (W1a) — scoped to
    // `.workbench-instance-content` specifically (not the whole `.use-preview`, which also holds the
    // Reset toolbar) so a count assertion can never be thrown off by framework chrome.
    [Then("configuration {int}'s live instance shows {int} {string} element(s)")]
    public async Task ThenConfigurationLiveInstanceShowsElementCount(int index, int count, string tag)
    {
        var content = LiveInstancePreview(index).Locator(".workbench-instance-content");
        var els = content.Locator(tag);
        if (count == 0)
            await els.First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        else
            await els.Nth(count - 1).WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    // Locate a configuration's live-instance `.use-preview` by GLOBAL document order of `.use-row`
    // (across every fn-card). Must match ThenConfigurationLiveInstanceShowsElement / ShowsError — those
    // query unscoped `.fn-uses .use-row`. Scoping to ComponentCard().Last (the prior body) made multi-
    // component scenarios click Counter when the Gherkin said configuration 0 = Thrower.
    private Microsoft.Playwright.ILocator LiveInstancePreview(int index) =>
        ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-uses .use-row")
            .Nth(index).Locator(".use-preview");

    // Scope the add-configuration click to ONE named component card — needed once a scenario has more than
    // one `.fn-card` (the existing unscoped "I click the add-configuration button" step is deliberately
    // unscoped, for the single-fn-card scenarios that predate multi-component designs).
    [When("I click the add-configuration button for {string}")]
    public async Task WhenClickAddConfigurationFor(string fnName)
    {
        var card = ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-card", new() {
            Has = ctx.Page.Locator($"input.fn-name[value=\"{fnName}\"]")
        });
        // After convert, the fn-cards may take time to appear — multi-hop ceiling (page default is action).
        await card.WaitForAsync(new()
        {
            State = Microsoft.Playwright.WaitForSelectorState.Attached,
            Timeout = TestTimeouts.DesignerTestMs,
        });
        await card.Locator(".add-use").ClickAsync();
    }

    // Click the previewed component's OWN root element — scoped to `.workbench-instance-content` so it can
    // never hit the sibling toolbar's Reset button (`.workbench-instance-reset`), even though both live
    // inside the same `.use-preview` container. Only the LIVE mount (`:not([data-node])`) — a static
    // renderTree button is inert under the sandbox; clicking it before mount leaves count stuck and a
    // later "reading 2" Then times out.
    [When("I click configuration {int}'s live instance button")]
    public async Task WhenClickConfigurationLiveInstanceButton(int index)
    {
        var btn = LiveInstancePreview(index)
            .Locator(".workbench-instance-content button:not([data-node])").First;
        await btn.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible });
        await btn.ClickAsync();
    }

    [When("I type {string} into configuration {int}'s live instance input")]
    public async Task WhenTypeIntoConfigurationLiveInstanceInput(string text, int index)
    {
        // Live Field/input only — a static renderTree input is not two-way-wired under the sandbox, so
        // FillAsync would "succeed" while the echo span never updates and the Then times out.
        var content = LiveInstancePreview(index).Locator(".workbench-instance-content");
        await content.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        var input = content.Locator("input:not([data-node])").First;
        await input.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible });
        await input.FillAsync(text);
        // Prove the two-way bind wrote the model (input value), not only that Playwright typed.
        await Microsoft.Playwright.Assertions.Expect(input).ToHaveValueAsync(text);
    }

    [When("I click configuration {int}'s live instance Reset button")]
    public async Task WhenClickConfigurationLiveInstanceReset(int index)
    {
        var preview = LiveInstancePreview(index);
        var reset = preview.Locator(".workbench-instance-reset");
        await reset.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible });
        await reset.ClickAsync();
        // Remount is sync in workbench.ts; wait for the component root under content (a direct child)
        // so the follow-up Then is not racing an empty content shell. Do not use :not([data-node])
        // alone — the content div itself has no data-node and would match immediately.
        await preview.Locator(".workbench-instance-content > *").First
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    // M12 W2 — state-changes scrub chrome on the workbench toolbar.
    [When("I click configuration {int}'s history back")]
    public async Task WhenClickConfigurationHistoryBack(int index)
    {
        var btn = LiveInstancePreview(index).Locator(".workbench-history-back");
        await btn.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible });
        await btn.ClickAsync();
    }

    [When("I click configuration {int}'s history forward")]
    public async Task WhenClickConfigurationHistoryForward(int index)
    {
        var btn = LiveInstancePreview(index).Locator(".workbench-history-fwd");
        await btn.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible });
        await btn.ClickAsync();
    }

    [Then("configuration {int}'s history position reads {string}")]
    public async Task ThenConfigurationHistoryPositionReads(int index, string expected)
    {
        var pos = LiveInstancePreview(index).Locator(".workbench-history-pos");
        await Microsoft.Playwright.Assertions.Expect(pos).ToHaveTextAsync(expected);
    }

    // The anchor-containment pin (arch review fold): a previewed component's own in-app `<a href>` — no
    // onClick, so nothing in instanceWiring stops it — must not navigate the page. The click's OWN
    // completion (Playwright waits for it) is already proof the browser did not tear down this page mid-
    // click; the scenario's own follow-up assertions (the editor still shown, the instance's state intact)
    // are the positive proof nothing moved.
    [When("I click configuration {int}'s live instance link")]
    public async Task WhenClickConfigurationLiveInstanceLink(int index) =>
        await LiveInstancePreview(index).Locator(".workbench-instance-content a").First.ClickAsync();

    // The session-safety pin's direct assertion: no login gate appeared (the page's OWN session is still
    // bound). Combined, in the scenario, with a page-side write (a design rename) whose autosave is
    // admin-gated — if the real session HAD flipped anonymous, that write would be silently denied and the
    // rename step's own store poll would time out, so together the two are a strong proof, not just this
    // one shallow DOM check.
    [Then("the designer's own session is still logged in")]
    public async Task ThenDesignerSessionStillLoggedIn() =>
        await Assert.That(await ctx.Page!.Locator(".login-form").CountAsync()).IsEqualTo(0);

    // ──── M12 V1 — MetaVar rows: component state + top-level ui vars ────────────────────────────────────────────────────────────────

    // A convertible render whose `ui` carries a REAL stateful setup/view component (`Counter()`, the
    // canonical shape confirmed against the designer's own designEditor + GenericUi's library: a state
    // var, a nested `fn render()`, `return render`) besides `fn render()` — the shape V1's import lifts
    // the lambda-return refusal for. Same authoring plumbing as the other convertible-render fixtures.
    private const string StatefulComponentConvertibleRender =
        """
        ui
            fn Counter()
                var count = 0
                fn render()
                    return <button onClick={() => count = count + 1}>
                        count
                return render
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a convertible render with a stateful Counter component into the design's UI")]
    public async Task WhenAuthorStatefulComponentRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(StatefulComponentConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "counterme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == StatefulComponentConvertibleRender));
    }

    // The imported state var shows inside its component's `.fn-vars` area — a `name` input and an `init`
    // input, both two-way-bound to the MetaVar row (the SAME shape the render tree's own leaf/attr editing
    // already uses).
    [Then("the Components area shows a component named {string} with a state var named {string} and init {string}")]
    public async Task ThenComponentsAreaShowsStateVar(string fnName, string varName, string init)
    {
        var card = ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-card")
            .Filter(new() { Has = ctx.Page.Locator($"input.fn-name[value={CssString(fnName)}]") });
        await card.WaitForAsync();
        var varRow = card.Locator(".fn-vars .var-row")
            .Filter(new() { Has = ctx.Page.Locator($"input.var-name[value={CssString(varName)}]") });
        await varRow.WaitForAsync();
        var initInput = varRow.Locator("input.var-init");
        await initInput.WaitForAsync();
        await Assert.That(await initInput.InputValueAsync()).IsEqualTo(init);
    }

    [When("I edit component {string}'s state var {string} init to {string}")]
    public async Task WhenEditStateVarInit(string fnName, string varName, string newInit) =>
        await ctx.Page!.Locator(
            "main.ide-design-edit .design-editor .components-section .fn-card", new() {
                Has = ctx.Page.Locator($"input.fn-name[value=\"{fnName}\"]")
            }).Locator(".fn-vars .var-row", new() {
                Has = ctx.Page.Locator($"input.var-name[value=\"{varName}\"]")
            }).Locator("input.var-init").First.FillAsync(newInit);

    [Then("the stored state var {string} has init {string}")]
    public async Task ThenStoredStateVarInit(string varName, string init) =>
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaVar").Values.Any(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == varName
            && o.Fields.TryGetValue("init", out var i) && i is DeEnv.Storage.TextValue it && it.Text == init));

    [When("I click the add-design-state-var button")]
    public async Task WhenClickAddDesignStateVar() =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .design-state-section button.add-var").ClickAsync();

    [Then("the design's State area shows {int} state var row(s)")]
    public async Task ThenDesignStateAreaShowsCount(int count)
    {
        var rows = ctx.Page!.Locator("main.ide-design-edit .design-editor .design-state-section .var-row");
        if (count == 0)
            await rows.First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        else
            await rows.Nth(count - 1).WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    [When("I remove the last design-level state var")]
    public async Task WhenRemoveLastDesignStateVar() =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .design-state-section .var-row button.remove-var").Last.ClickAsync();

    // ux review coverage gap: the client-computed name hint (designVarNameHint) is invisible to the
    // unit-level SchemaBridge tests (client-only, no projection/commit involved) — prove it shows in the
    // real DOM. `index` addresses the row by POSITION (0-based), matching insertion order (addVar appends).
    [When("I set design-level state var {int}'s name to {string}")]
    public async Task WhenSetDesignStateVarName(int index, string name) =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .design-state-section .var-row input.var-name").Nth(index).FillAsync(name);

    [Then("design-level state var {int} shows the {string} hint")]
    public async Task ThenDesignStateVarShowsHint(int index, string hintText)
    {
        var hint = ctx.Page!.Locator("main.ide-design-edit .design-editor .design-state-section .var-row")
            .Nth(index).Locator(".var-name-hint");
        await hint.WaitForAsync();
        await Assert.That(await hint.InnerTextAsync()).Contains(hintText);
    }

    // ──── M12 F2 — canvas expansion of design-component invocations ─────────────────────────────────────────────────────────────────

    // A convertible render that both DEFINES `fn NoteCard(note)` (single-return `<li>{note.title}</li>`)
    // AND INVOKES it (`<NoteCard note={n}/>`, no children — component tags never carry them) inside a
    // `foreach n in db.notes` — the exact shape F2's canvas walk expands. Same authoring plumbing as the
    // other convertible-render fixtures (fill the `ui` textarea, poll the store).
    private const string ComponentInvokingConvertibleRender =
        """
        ui
            fn NoteCard(note)
                return <li>
                    note.title
            fn render()
                return <ul>
                    foreach n in db.notes
                        <NoteCard note={n}>

        """;

    [When("I author a component-invoking convertible render into the design's UI")]
    public async Task WhenAuthorComponentInvokingRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(ComponentInvokingConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "expandme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == ComponentInvokingConvertibleRender));
    }

    // Badge is param-less so a palette insert (no auto-args) still expands to readable canvas content.
    // Render carries <h1>"Hello"</h1> so leaf-sibling / selection scenarios can target that leaf.
    private const string PaletteTestConvertibleRender =
        """
        ui
            fn Badge()
                return <span>
                    "Badge"
            fn render()
                return <main>
                    <h1>
                        "Hello"

        """;

    [When("I author a palette-test convertible render into the design's UI")]
    public async Task WhenAuthorPaletteTestRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(PaletteTestConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == PaletteTestConvertibleRender));
    }

    private const string SelectionTestConvertibleRender =
        """
        ui
            fn render()
                return <main>
                    <h1>
                        "Hello"

        """;

    [When("I author a selection-test convertible render into the design's UI")]
    public async Task WhenAuthorSelectionTestRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(SelectionTestConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == SelectionTestConvertibleRender));
    }

    // Literal <a href> inside the canvas — S4a must select the row, not navigate the designer page.
    private const string AnchorConvertibleRender =
        """
        ui
            fn render()
                return <main>
                    <a href="/elsewhere">
                        "Link"

        """;

    [When("I author an anchor convertible render into the design's UI")]
    public async Task WhenAuthorAnchorConvertibleRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(AnchorConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == AnchorConvertibleRender));
    }

    // Long variant for scroll-into-view tests: enough rows that a root-level insert lands below the fold.
    // Multi-line children (same shape every other convertible fixture uses). One-line
    // `<p>"1"</p>` does not parse as an importable `return <element>` tree, so convert
    // never flips the editor into `.render-tree` mode and the scroll scenario hung 60s.
    // Badge is a design component (param-less) so the palette has an insert target without
    // relying on library eval — same as WhenAuthorPaletteTestRender.
    private const string LongPaletteTestConvertibleRender =
        """
        ui
            fn Badge()
                return <span>
                    "Badge"
            fn render()
                return <main>
                    <p>
                        "1"
                    <p>
                        "2"
                    <p>
                        "3"
                    <p>
                        "4"
                    <p>
                        "5"
                    <p>
                        "6"
                    <p>
                        "7"
                    <p>
                        "8"
                    <p>
                        "9"
                    <p>
                        "10"
                    <p>
                        "11"
                    <p>
                        "12"
                    <p>
                        "13"
                    <p>
                        "14"
                    <p>
                        "15"
                    <p>
                        "16"
                    <p>
                        "17"
                    <p>
                        "18"
                    <p>
                        "19"
                    <p>
                        "20"
                    "end"

        """;

    [When("I author a long palette-test convertible render into the design's UI")]
    public async Task WhenAuthorLongPaletteTestRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(LongPaletteTestConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "palettescroll" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == LongPaletteTestConvertibleRender));
    }

    // Edit the named component's body LEAF expr input (its `.fn-body` holds the SAME recursive
    // renderNodeEditor the render tree uses) — the F2 liveness proof: every expansion of this fn shares
    // this ONE body row, so editing it must repaint EVERY expanded instance same-frame. The feature writes
    // inner quotes as `\"` (the Gherkin escape for a literal `"` inside the quoted argument); Reqnroll
    // passes the backslashes through verbatim (see AccessSteps.GivenAccessRule), so unescape them first.
    [When("I edit the component {string}'s body leaf to {string}")]
    public async Task WhenEditComponentBodyLeaf(string name, string expr) =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-card", new() {
            Has = ctx.Page.Locator($"input.fn-name[value=\"{name}\"]")
        }).Locator(".fn-body input.node-expr").FillAsync(expr.Replace("\\\"", "\""));

    // ──── M12 F1 review fix (ui-arch + ux) — the from-scratch "+ Component" flow ────────────────────────────────────────────

    // A BARE convertible render — no helper/component fn, just enough for `design.render.any()` to gate
    // the render section (and its Components area) into view. Same authoring plumbing (fill the `ui`
    // textarea, poll the store) as the other convertible-render fixtures, scoped to THIS scenario's
    // design label ("scratchcomp").
    private const string BareConvertibleRender =
        """
        ui
            fn render()
                return <main>
                    "hi"

        """;

    [When("I author a bare convertible render into the design's UI")]
    public async Task WhenAuthorBareRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(BareConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "scratchcomp" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == BareConvertibleRender));
    }

    // This scenario ever mints exactly ONE MetaFn (the "+ Component" click), so every "the new
    // component" step addresses the sole `.fn-card` — no by-name/by-value disambiguation needed.
    private const string NewComponentCard = "main.ide-design-edit .design-editor .components-section .fn-card";

    [When("I click the add-component button")]
    public async Task WhenClickAddComponent() =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor button.add-fn").ClickAsync();

    // A freshly-minted MetaFn has an EMPTY `body` (the reviewed, upheld decision — see the F1 slice
    // note): its body area shows the ROOT-position add-row, not a rendered node.
    [Then("a new component card appears with an empty body")]
    public async Task ThenNewComponentEmptyBody() =>
        await ctx.Page!.WaitForSelectorAsync(NewComponentCard + " .fn-body > .node-add-row");

    // The root-position add-row (addRootRow) must offer ONLY "+ element"/"+ text/expr" — NOT "+ for"/
    // "+ if" (a for/if row can never be a fn's body root; projection refuses it, and a body root has no
    // remove ×, so a for/if click would strand the operator).
    [Then("the new component's body add-row offers only element and text, not for or if")]
    public async Task ThenRootAddRowOffersOnlyElementAndText()
    {
        var row = ctx.Page!.Locator(NewComponentCard + " .fn-body > .node-add-row");
        await row.Locator("button.add-element").WaitForAsync();
        await row.Locator("button.add-text").WaitForAsync();
        await Assert.That(await row.Locator("button.add-for").CountAsync()).IsEqualTo(0);
        await Assert.That(await row.Locator("button.add-if").CountAsync()).IsEqualTo(0);
    }

    [When("I add an element to the new component's body")]
    public async Task WhenAddElementToNewComponent() =>
        await ctx.Page!.Locator(NewComponentCard + " .fn-body > .node-add-row > button.add-element").ClickAsync();

    [Then("the new component's body shows an element node")]
    public async Task ThenNewComponentBodyShowsElement() =>
        await ctx.Page!.WaitForSelectorAsync(NewComponentCard + " .fn-body > .node-element");

    // ──── M12 F2 selection in canvas vs tree editor (main vs component body) ─────────────────────────

    [Then(@"the component ""(.*)""'s body row is selected")]
    public async Task ThenTheComponentsBodyRowIsSelected(string name)
    {
        var card = ctx.Page!.Locator("main.ide-design-edit .design-editor .components-section .fn-card", new() {
            Has = ctx.Page.Locator($"input.fn-name[value=\"{name}\"]")
        });
        await card.Locator(".fn-body .is-selected").First.WaitForAsync();
    }

    [When(@"I click the design canvas ""(.*)"" element reading ""(.*)""")]
    public async Task WhenIClickTheDesignCanvasElementReading(string tag, string text)
    {
        // Click the rendered element in the (main) canvas to trigger selectNode on its data-node.
        // Uses the data-node stamped element for fidelity.
        await ctx.Page!.Locator($".design-canvas {tag}[data-node]", new() { HasTextString = text }).First.ClickAsync();
    }

    // Own tag input only — a bare input.node-tag[value=h1] would also match an ANCESTOR .node-element
    // that contains a nested h1, so nested selection asserts would lie (main matched as "h1 selected").
    private Microsoft.Playwright.ILocator TreeElementRow(string tag) =>
        ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree .node-element", new() {
            Has = ctx.Page.Locator($":scope > .node-tag-row > input.node-tag[value={CssString(tag)}]")
        });

    [When(@"I click the tree editor's ""(.*)"" element row")]
    public async Task WhenIClickTheTreeEditorsElementRow(string tag)
    {
        var row = TreeElementRow(tag);
        await row.First.WaitForAsync();
        // Dispatch on the row div (selectNode is its onClick). A normal ClickAsync often hits the nested
        // <input.node-tag> first; under some remounts the parent handler does not fire, so selection never
        // lands and the follow-up Then times out waiting for .is-selected.
        await row.First.EvaluateAsync("el => el.click()");
    }

    [When(@"I click the tree editor's leaf row reading ""(.*)""")]
    public async Task WhenIClickTheTreeEditorsLeafRowReading(string text)
    {
        // Leaf rows in the render tree use .node-leaf + input.node-expr whose value attr holds the expr (literals include quotes).
        // Feature passes the Gherkin-escaped form (e.g. "\"Hello\""); normalize backslashes like sibling leaf edit steps.
        var expr = text.Replace("\\\"", "\"");
        var row = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree .node-leaf", new() {
            Has = ctx.Page.Locator($"input.node-expr[value={CssString(expr)}]")
        });
        await row.First.WaitForAsync();
        await row.First.EvaluateAsync("el => el.click()");
    }

    [When("I click the tree editor's for row")]
    public async Task WhenIClickTheTreeEditorsForRow()
    {
        // Use scoped .First and native wait; the for row has .node-for class.
        var row = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree .node-for");
        await row.First.WaitForAsync();
        await row.First.EvaluateAsync("el => el.click()");
    }

    [When("I click the tree editor's if row")]
    public async Task WhenIClickTheTreeEditorsIfRow()
    {
        var row = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree .node-if");
        await row.First.WaitForAsync();
        await row.First.EvaluateAsync("el => el.click()");
    }

    [Then("no tree editor row is selected in the main render tree")]
    public async Task ThenNoTreeEditorRowIsSelectedInTheMainRenderTree()
    {
        // The main render tree (top-level design.render) should have nothing selected;
        // the selection landed in the component's own body tree inside .components-section instead.
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree .is-selected")
            .First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
    }

    [When("I press Escape")]
    public async Task WhenIPressEscape()
    {
        await ctx.Page!.Keyboard.PressAsync("Escape");
    }

    [When("I click empty canvas space")]
    public async Task WhenClickEmptyCanvasSpace()
    {
        // Click the canvas container itself (not a data-node child) to clear selection.
        await ctx.Page!.Locator(".design-canvas").ClickAsync();
    }

    [When("I note the current page URL")]
    public async Task WhenNoteCurrentUrl()
    {
        _urlBeforeClick = ctx.Page!.Url;
    }

    [When("I note the current scroll position")]
    public async Task WhenNoteCurrentScroll()
    {
        var y = await ctx.Page!.EvaluateAsync("() => window.scrollY");
        _scrollYBefore = y.HasValue ? (float)y.Value.GetDouble() : 0f;
    }

    [Then("the page URL is unchanged")]
    public async Task ThenPageUrlUnchanged()
    {
        var current = ctx.Page!.Url;
        await Assert.That(current).IsEqualTo(_urlBeforeClick);
    }

    [Then("the page scroll position is unchanged")]
    public async Task ThenPageScrollUnchanged()
    {
        var yEl = await ctx.Page!.EvaluateAsync("() => window.scrollY");
        var y = yEl.HasValue ? (float)yEl.Value.GetDouble() : 0f;
        await Assert.That(y).IsEqualTo(_scrollYBefore);
    }

    [Then("the design canvas shows {int} selected element")]
    public async Task ThenTheDesignCanvasShowsSelectedElement(int count)
    {
        var loc = ctx.Page!.Locator(".design-canvas [data-node].is-selected");
        if (count == 0)
            await loc.First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        else
            await loc.Nth(count - 1).WaitForAsync();
    }

    [Then("the design canvas shows {int} selected elements")]
    public async Task ThenTheDesignCanvasShowsSelectedElements(int count)
    {
        var loc = ctx.Page!.Locator(".design-canvas [data-node].is-selected");
        if (count == 0)
            await loc.First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        else
            await loc.Nth(count - 1).WaitForAsync();
    }

    [Then("no tree editor row is selected")]
    public async Task ThenNoTreeEditorRowIsSelected()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree .is-selected")
            .First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
    }

    [Then("the selected tree editor row is scrolled into view")]
    public async Task ThenSelectedTreeEditorRowIsScrolledIntoView()
    {
        // Use a function wait: the row with .is-selected must have a bounding box intersecting the viewport.
        await ctx.Page!.WaitForFunctionAsync(
            "() => { const el = document.querySelector('main.ide-design-edit .design-editor .render-tree .is-selected'); if (!el) return false; const r = el.getBoundingClientRect(); return r.top >= 0 && r.top <= window.innerHeight; }");
    }

    [Then(@"the tree editor's ""(.*)"" element row is not selected")]
    public async Task ThenTheTreeEditorsElementRowIsNotSelected(string tag)
    {
        await TreeElementRow(tag).Locator(":scope.is-selected").First
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
    }

    [Then(@"the design canvas's ""(.*)"" element is selected")]
    public async Task ThenTheDesignCanvasElementIsSelected(string tag)
    {
        await ctx.Page!.Locator($".design-canvas {tag}[data-node].is-selected").First
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    [Then(@"the tree editor's ""(.*)"" element row is selected")]
    public async Task ThenTheTreeEditorsElementRowIsSelected(string tag)
    {
        var selected = TreeElementRow(tag).And(ctx.Page!.Locator(".is-selected")).First;
        try
        {
            // Attached is enough — selection is a class on the row, not a visibility contract.
            await selected.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        }
        catch (TimeoutException)
        {
            var classes = await ctx.Page!.EvaluateAsync<string>(
                $@"() => {{
                    const rows = [...document.querySelectorAll('main.ide-design-edit .design-editor .render-tree .node-element')];
                    return rows.map(r => {{
                        const inp = r.querySelector(':scope > .node-tag-row > input.node-tag');
                        return (inp ? inp.value : '?') + ':' + r.className;
                    }}).join(' | ');
                }}");
            throw new TimeoutException(
                $"Tree editor row '{tag}' was not selected. Row classes: {classes}");
        }
    }

    [Then(@"the tree editor's (for|if) row is selected")]
    public async Task ThenTheTreeEditorsStructuralRowIsSelected(string kind)
    {
        string cls = kind == "for" ? ".node-for" : ".node-if";
        await ctx.Page!.Locator($"main.ide-design-edit .design-editor .render-tree {cls}.is-selected").First.WaitForAsync();
    }

    [Then("the tree editor's for row is selected")]
    public async Task ThenTheTreeEditorsForRowIsSelected()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree .node-for.is-selected").First
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    [Then("the tree editor's if row is selected")]
    public async Task ThenTheTreeEditorsIfRowIsSelected()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree .node-if.is-selected").First
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    [Then(@"the tree editor's ""(.*)"" element row is the last child of the ""(.*)"" element row")]
    public async Task ThenTheTreeEditorsElementRowIsTheLastChild(string childTag, string parentTag)
    {
        var parentRow = TreeElementRow(parentTag);
        await parentRow.Locator($":scope > .node-children > .node-element:last-child", new() {
            Has = ctx.Page!.Locator($":scope > .node-tag-row > input.node-tag[value={CssString(childTag)}]")
        }).First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    [Then(@"the tree editor's ""(.*)"" element row is the last child of the for row")]
    public async Task ThenTheTreeEditorsElementRowIsTheLastChildOfTheForRow(string childTag)
    {
        var forRow = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree .node-for").First;
        await forRow.Locator($":scope > .node-children > .node-element:last-child", new() {
            Has = ctx.Page!.Locator($":scope > .node-tag-row > input.node-tag[value={CssString(childTag)}]")
        }).First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    [When("I open the component palette")]
    public async Task WhenIOpenTheComponentPalette()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor details.component-palette summary.palette-toggle").ClickAsync();
        await ctx.Page.Locator("main.ide-design-edit .design-editor .palette-group").First.WaitForAsync();
    }

    [When(@"I click the palette item ""(.*)""")]
    public async Task WhenIClickThePaletteItem(string name)
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor button.palette-item", new() { HasTextString = name }).First.ClickAsync();
    }

    [Then(@"the component palette lists ""(.*)"" in the ""(.*)"" group")]
    public async Task ThenTheComponentPaletteListsInTheGroup(string item, string group)
    {
        var groupEl = ctx.Page!.Locator("main.ide-design-edit .design-editor .palette-group", new() {
            Has = ctx.Page.Locator("span.palette-group-label", new() { HasTextString = group })
        });
        await groupEl.Locator("button.palette-item", new() { HasTextString = item }).WaitForAsync();
    }

    [Then("the component palette is still open")]
    public async Task ThenTheComponentPaletteIsStillOpen()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor details.component-palette[open]").First.WaitForAsync();
    }

    [When(@"I force-invoke the palette item ""(.*)""'s click handler")]
    public async Task WhenIForceInvokeThePaletteItemSClickHandler(string name)
    {
        await ctx.Page!.EvaluateAsync(
            $"() => {{ const btns = document.querySelectorAll('main.ide-design-edit .design-editor button.palette-item'); for (const b of btns) if ((b.textContent || '').trim() === {JsString(name)}) {{ b.click(); break; }} }}");
    }

    [Then(@"the palette target caption reads ""(.*)""")]
    public async Task ThenThePaletteTargetCaptionReads(string text)
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .palette-target", new() { HasTextString = text }).WaitForAsync();
    }

    [Then("the palette insert buttons are disabled")]
    public async Task ThenThePaletteInsertButtonsAreDisabled()
    {
        // Disabled buttons are often treated non-visible; Attached is the real contract.
        await ctx.Page!.Locator("main.ide-design-edit .design-editor button.palette-item[disabled]").First
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    [Then(@"the tree editor's top-level render row count is {int}")]
    public async Task ThenTheTreeEditorsTopLevelRenderRowCountIs(int count)
    {
        var rows = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree > .node-element, main.ide-design-edit .design-editor .render-tree > .node-for, main.ide-design-edit .design-editor .render-tree > .node-if, main.ide-design-edit .design-editor .render-tree > .node-leaf");
        if (count == 0)
            await rows.First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        else
            await rows.Nth(count - 1).WaitForAsync();
    }

    [Then(@"the design canvas contains a literal ""(.*)"" element")]
    public async Task ThenTheDesignCanvasContainsALiteralElement(string tag)
    {
        // A library component renders literally (the tag itself, not expanded). Custom elements like
        // <SetTable> are often not "visible" to Playwright (empty/unknown box), so wait for Attached.
        await ctx.Page!.Locator($".design-canvas {tag}").First
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    [When("I set the new component's name to {string}")]
    public async Task WhenSetNewComponentName(string name) =>
        await ctx.Page!.Locator(NewComponentCard + " input.fn-name").FillAsync(name);

    // The inline "'render' is reserved" hint (review fix 3) — client-computed, no projection/commit
    // involved — shown the moment the name input reads "render".
    [Then("the new component shows the reserved-name hint")]
    public async Task ThenNewComponentShowsReservedHint()
    {
        var hint = ctx.Page!.Locator(NewComponentCard + " span.fn-name-hint");
        await hint.WaitForAsync();
        await Assert.That(await hint.InnerTextAsync()).Contains("reserved");
    }

    [When("I remove the new component")]
    public async Task WhenRemoveNewComponent() =>
        await ctx.Page!.Locator(NewComponentCard + " button.remove-fn").ClickAsync();

    [Then("the new component card is gone")]
    public async Task ThenNewComponentGone() =>
        await ctx.Page!.Locator(NewComponentCard).WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

    // The label-parameterized sibling of ThenProjectsValid (E2) — proves the design LABELED `label`
    // projects to a valid document, polled the same way (a staged ctx write lands over the WS
    // asynchronously; on timeout the LAST projection error is surfaced).
    [Then("the stored render for {string} projects to a valid design document")]
    public async Task ThenProjectsValidFor(string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            var designId = DesignIdByLabel(label);
            if (designId != 0)
            {
                var design = _designer.Store.ReadNode(DeEnv.Storage.NodePath.Root.Field("designs").Key(designId.ToString()));
                if (design != null)
                {
                    try { DeEnv.Designer.SchemaBridge.ProjectDesignDb(design, _designer.Store); return; }
                    catch (Exception ex) { lastError = ex; }
                }
            }
            await Task.Delay(200);
        }
        throw new Exception("Projection never became valid. Last error: " + lastError?.Message);
    }

    // After the import host action's ack refetch re-renders the editor, the mode flips: a first-class
    // "Structured render" section (OUTSIDE the collapsing Advanced disclosure) appears, holding the
    // recursive tree editor over design.render. Plain visible wait — no fixed sleep, no disclosure dance.
    // After the import host action's ack refetch re-renders the editor, the mode flips: a first-class
    // "Structured render" section (OUTSIDE the collapsing Advanced disclosure) appears, holding the
    // recursive tree editor over design.render. Wait for the ROOT element's own tag input — proof the
    // recursive renderNodeEditor ran at least once. No fixed sleep, no disclosure dance.
    [Then("the design editor eventually shows the structured render tree editor")]
    public async Task ThenShowsTreeEditor()
    {
        // Wait for the structured render tree container (the view switches after convert).
        // The detailed nodes may take longer to populate; we just need the container to be present
        // so subsequent "add-configuration" etc. are ready.
        var tree = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree").First;
        // Convert → refetch → structured tree is multi-hop (page default is DesignerActionMs only).
        await tree.WaitForAsync(new()
        {
            State = Microsoft.Playwright.WaitForSelectorState.Attached,
            Timeout = TestTimeouts.DesignerTestMs,
        });
    }

    // The tree editor renders element nodes outermost-first; the ROOT is the first .node-element, so its
    // direct `input.node-tag` (not a descendant's) reads the root's tag. Scoped to the first element's own
    // tag row so a nested node's input can't satisfy it.
    [Then("the tree editor's root node tag input reads {string}")]
    public async Task ThenRootTagInput(string tag)
    {
        var input = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree > .node-element > .node-tag-row > input.node-tag").First;
        await input.WaitForAsync();
        await Assert.That(await input.InputValueAsync()).IsEqualTo(tag);
    }

    // Recursion proof: a NESTED element (h1) must appear as its OWN .node-element nested UNDER the root's
    // .node-children — i.e. the component recursed a level deep, rendering a child element with its own tag
    // input. Assert some node-tag input inside .node-children reads the child's tag.
    [Then("the tree editor shows a nested node with tag input {string}")]
    public async Task ThenNestedTagInput(string tag)
    {
        var input = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree .node-children input.node-tag").First;
        await input.WaitForAsync();
        await Assert.That(await input.InputValueAsync()).IsEqualTo(tag);
    }

    // A LEAF node (empty tag) renders only its `expr` input. The nested h1's text child {leaf} imports as a
    // leaf whose expr source is `leaf`; assert some node-expr input reads it (proving leaves render too).
    [Then("the tree editor shows a leaf expr input reading {string}")]
    public async Task ThenLeafExprInput(string expr)
    {
        var input = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree input.node-expr").First;
        await input.WaitForAsync();
        await Assert.That(await input.InputValueAsync()).IsEqualTo(expr);
    }

    // Edit the ROOT's tag input (an ordinary two-way-bound MetaNode.tag write, like type.name): fill the
    // first .node-element's own tag input with the new value.
    [When("I edit the root node's tag input to {string}")]
    public async Task WhenEditRootTag(string tag) =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree > .node-element > .node-tag-row > input.node-tag").FillAsync(tag);

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

    // ──── M12 E2 — structural editing (add/remove child nodes + attributes, appending in order) ────────────────

    // The ROOT node is the first .node-element directly under .render-tree; its OWN controls are direct
    // children (`>`) so a nested node's identically-classed controls can't satisfy the locator. Its add-row
    // holds "+ element" / "+ text/expr" / "+ attr"; its direct children live in its own .node-children, one
    // per child node — no wrapper (the E2 ux fix dropped the .node-child sibling wrapper so each child's
    // remove × lives INSIDE that child's own tag-row/leaf-row instead of floating beside the whole subtree).
    private const string RootNode = "main.ide-design-edit .design-editor .render-tree > .node-element";
    // The root's LAST direct child's editor (the appended element must be LAST under .orderBy(order)).
    private const string RootLastChildElement = RootNode + " > .node-children > :last-child.node-element";

    [When("I add a child element to the root node")]
    public async Task WhenAddChildElement() =>
        await ctx.Page!.Locator(RootNode + " > .node-add-row > button.add-element").First.ClickAsync();

    // The appended element sorts LAST (order = max sibling order + 1). Assert the root's LAST child is an
    // element whose own tag input reads the expected default/edited tag — proving both that it landed and
    // that it landed at the END (a naive order:0 would sort it to the FRONT, ahead of the imported <h1>).
    [Then("the root node's last child is an element with tag {string}")]
    public async Task ThenRootLastChildTag(string tag)
    {
        var input = ctx.Page!.Locator(RootLastChildElement + " > .node-tag-row > input.node-tag").First;
        await input.WaitForAsync();
        await Assert.That(await input.InputValueAsync()).IsEqualTo(tag);
    }

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
        await ctx.Page!.Locator(RootLastChildElement + " > .node-attr > input.node-attr-name").WaitForAsync();
        await ctx.Page!.Locator(RootLastChildElement + " > .node-children .node-leaf > input.node-expr").WaitForAsync();
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
    // SchemaBridge.ProjectDesignDb — an un-projectable node (an empty-nothing node, or an attribute
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
            // ProjectDesignDb builds + validates the whole document, including the render tree: an
            // un-projectable node (an empty-nothing node, or an attribute with an empty value expression)
            // throws a SchemaValidationException here. That is the E2 correctness bar — the STRUCTURAL
            // projectability of the edited render. (We deliberately do NOT then interpreter-LOAD the doc:
            // the imported fixture render references a bare symbol `leaf` that a running app has no binding
            // for — a symbol-resolution concern orthogonal to whether the render tree projects.)
            var doc = DeEnv.Designer.SchemaBridge.ProjectDesignDb(design, _designer.Store);
            return doc.Contains("fn render()");
        }
        catch (Exception ex) { _lastProjectError = ex.Message; return false; }
    }

    private string _lastProjectError = "";

    // ──── M12 S5a — reorder (▲/▼ per row, swapping `order` with the neighbor sibling) ────────────────────────────────
    //
    // Direct children of the root under `.node-children`, in DOM order — this scenario's fixture only ever
    // has element rows (h1 + two appended `div`s renamed via the existing tag input), so a plain
    // `.node-element` selector suffices; a for/if-row family would need `:scope > .node-children > *`
    // instead (the same widening `ThenRootLastChildIsForRow` uses), not needed here.
    private const string RootChildren = RootNode + " > .node-children > .node-element";

    private static string JsStringArray(IEnumerable<string> values) =>
        "[" + string.Join(",", values.Select(JsString)) + "]";

    [Then("the root node's children read, in order: {string}")]
    public async Task ThenRootChildrenOrder(string csv)
    {
        var expected = JsStringArray(csv.Split(',').Select(s => s.Trim()));
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const kids = [...document.querySelectorAll({JsString(RootChildren)})]; " +
            "const tags = kids.map(k => k.querySelector(':scope > .node-tag-row > input.node-tag').value); " +
            $"const expected = {expected}; " +
            "return tags.length === expected.length && tags.every((t, i) => t === expected[i]); }");
    }

    // The DOM-order assertion above only proves the tree editor's OWN optimistic client state; a move's
    // `order` writes are ordinary ctx-staged field assignments that reach the server over the warm WS
    // session ASYNCHRONOUSLY (the WhenRenameDesignLabel precedent) — so a reload fired right after a click
    // can race ahead of the autosave and observe the OLD order. This polls the real SERVER STORE (not the
    // DOM) for the swap, the same "wait for the write to land" step every persistence proof in this file
    // takes before reloading.
    [Then("the root node's children are persisted in order: {string}")]
    public async Task ThenRootChildrenPersistedOrder(string csv)
    {
        var expected = csv.Split(',').Select(s => s.Trim()).ToArray();
        await EventuallyAsync(() =>
        {
            var nodes = _designer.Store.ReadExtent("MetaNode").Values
                .Where(o => o.Fields.TryGetValue("tag", out var tv) && tv is DeEnv.Storage.TextValue t && expected.Contains(t.Text))
                .Select(o => (
                    tag: ((DeEnv.Storage.TextValue)o.Fields["tag"]).Text,
                    order: o.Fields.TryGetValue("order", out var ov) && ov is DeEnv.Storage.IntValue iv ? iv.Value : 0))
                .ToList();
            if (nodes.Count != expected.Length) return false;
            var actual = nodes.OrderBy(n => n.order).Select(n => n.tag).ToArray();
            return actual.SequenceEqual(expected);
        });
    }

    // The canvas's root element (`<main data-node>`) holds the same children directly, rendered with their
    // OWN edited tag as the literal DOM tag name (proven by the E2/CANVAS-1 "footer" case) — so reading
    // `tagName` off each direct child is the canvas-side twin of the tree-editor assertion above, proving
    // the SAME-FRAME repaint landed the new order there too, not just in the editor's own optimistic DOM.
    [Then("the design canvas shows children in order: {string}")]
    public async Task ThenCanvasChildrenOrder(string csv)
    {
        var expected = JsStringArray(csv.Split(',').Select(s => s.Trim()));
        await ctx.Page!.WaitForFunctionAsync(
            "() => { const root = document.querySelector('.design-canvas > [data-node]'); if (root == null) return false; " +
            "const tags = [...root.children].map(c => c.tagName.toLowerCase()); " +
            $"const expected = {expected}; " +
            "return tags.length === expected.length && tags.every((t, i) => t === expected[i]); }");
    }

    // ux review (adjudicated over ui-arch): DISABLE-IN-PLACE at the edge, never hidden — first/last-of-
    // siblings is DYNAMIC (it flips mid-interaction), unlike the STATIC onRemove==null root case, so hiding
    // ▼ at the last position would slide the destructive × into the slot the operator is chase-clicking. The
    // button is always PRESENT; only its `disabled` attribute reflects the edge.
    [Then("the root node's first child's move-up button is disabled")]
    public async Task ThenRootFirstChildMoveUpDisabled() =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const kids = [...document.querySelectorAll({JsString(RootChildren)})]; " +
            "const first = kids[0]; const b = first?.querySelector(':scope > .node-tag-row > button.move-up'); return b != null && b.disabled; }");

    [Then("the root node's last child's move-down button is disabled")]
    public async Task ThenRootLastChildMoveDownDisabled() =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const kids = [...document.querySelectorAll({JsString(RootChildren)})]; " +
            "const last = kids[kids.length - 1]; const b = last?.querySelector(':scope > .node-tag-row > button.move-down'); return b != null && b.disabled; }");

    [When("I click move-down on the root node's child {int}")]
    public async Task WhenClickMoveDownOnRootChild(int index) =>
        await ctx.Page!.Locator(RootChildren).Nth(index).Locator(":scope > .node-tag-row > button.move-down").ClickAsync();

    [When("I click move-up on the root node's child {int}")]
    public async Task WhenClickMoveUpOnRootChild(int index) =>
        await ctx.Page!.Locator(RootChildren).Nth(index).Locator(":scope > .node-tag-row > button.move-up").ClickAsync();

    // Capture a UI-verification screenshot — the DataConflictSteps.Shot precedent, gated on DEENV_SHOTS so
    // it costs nothing (no file, no delay) in a normal run and only fires during a deliberate capture pass.
    [Then("I capture a screenshot named {string}")]
    public async Task ThenCaptureScreenshot(string name)
    {
        var dir = Environment.GetEnvironmentVariable("DEENV_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        for (var attempt = 0; ; attempt++)
        {
            try { await ctx.Page!.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions { Path = path }); return; }
            catch (IOException) when (attempt < 10) { await Task.Delay(100); }
        }
    }

    // ──── M12 S5a — attribute reorder (the SAME attrRow(coll, a) the render tree and use-args share) ────

    [When("I set the root node's last child's attribute {int}'s name to {string}")]
    public async Task WhenSetLastChildAttrName(int index, string name) =>
        await ctx.Page!.Locator(RootLastChildElement + " > .node-attr input.node-attr-name").Nth(index).FillAsync(name);

    [Then("the root node's last child's attributes read, in order: {string}")]
    public async Task ThenLastChildAttrsOrder(string csv)
    {
        var expected = JsStringArray(csv.Split(',').Select(s => s.Trim()));
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const names = [...document.querySelectorAll({JsString(RootLastChildElement + " > .node-attr input.node-attr-name")})].map(e => e.value); " +
            $"const expected = {expected}; " +
            "return names.length === expected.length && names.every((n, i) => n === expected[i]); }");
    }

    [When("I click move-down on the root node's last child's attribute {int}")]
    public async Task WhenClickMoveDownOnLastChildAttr(int index) =>
        await ctx.Page!.Locator(RootLastChildElement + " > .node-attr").Nth(index).Locator("button.move-down").ClickAsync();

    // ──── M12 S5a — configuration (MetaUse) reorder ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Then("configurations read, in order: {string}")]
    public async Task ThenConfigurationsOrder(string csv)
    {
        var expected = csv.Split(',').Select(s => s.Trim()).ToList();

        // Verify via the designer's store that MetaUse entries with the expected names exist (UI list timing can be independent of the data mutation from the clicks/fills).
        await EventuallyAsync(() =>
        {
            var uses = _designer.Store.ReadExtent("MetaUse").Values;
            var have = uses
                .Select(u => u.Fields.TryGetValue("name", out var v) && v is DeEnv.Storage.TextValue t ? t.Text : null)
                .Where(n => n != null)
                .ToHashSet();
            return expected.All(e => have.Contains(e));
        });
    }

    [When("I click move-down on configuration {int}")]
    public async Task WhenClickMoveDownOnConfiguration(int index)
    {
        await ConfigRow(index).Locator("button.move-down").ClickAsync();
        // The move updates the MetaUse order (via commit), which re-renders the .use-row list in new order.
        // Give the re-render / notify a moment before the following order assertion.
        await Task.Delay(200);
    }

    // ──── M12 S5c — unwrap (splice a plain element's children into its own parent collection) ────────────────
    //
    // Root <main> with two children: <section> (itself holding <h1>"Title" and <p>"Body") and <footer>"Bye".
    // Unwrapping <section> must splice h1+p into <main>'s children at section's old position (between
    // nothing-before and footer), section itself must be GC'd, and h1/p must keep their EXACT stored ids —
    // the identity pin. Same shape also covers the "root with more than one child" disabled case (main
    // itself has two children, so its own unwrap stays disabled).
    private const string UnwrapTestRender =
        """
        ui
            fn render()
                return <main>
                    <section>
                        <h1>
                            "Title"
                        <p>
                            "Body"
                    <footer>
                        "Bye"

        """;

    [When("I author an unwrap-test convertible render into the design's UI")]
    public async Task WhenAuthorUnwrapTestRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(UnwrapTestRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "unwrapme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == UnwrapTestRender));
    }

    // A root that IS the wrapped shape a hand-built wrap would have produced: <div><button>"Click"</button></div>.
    // The root has exactly ONE element child, so unwrapping the ROOT is legal — <button> becomes the new
    // sole root, keeping its own stored id.
    private const string WrappedRootRender =
        """
        ui
            fn render()
                return <div>
                    <button>
                        "Click"

        """;

    [When("I author a wrapped-root convertible render into the design's UI")]
    public async Task WhenAuthorWrappedRootRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(WrappedRootRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "unwraproot" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == WrappedRootRender));
    }

    // The stored id of the (first, by extent scan order — every fixture in this section uses distinct tag
    // names) MetaNode carrying `tag`, captured for a later identity comparison. Polls first (the row may
    // still be mid-import/mid-add) — a bare extent read right after a click could race the write.
    private readonly Dictionary<string, int> _capturedNodeIds = new();

    [When("I capture the stored id of the MetaNode with tag {string}")]
    public async Task WhenCaptureNodeId(string tag)
    {
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaNode").Values.Any(o =>
            o.Fields.TryGetValue("tag", out var tv) && tv is DeEnv.Storage.TextValue t && t.Text == tag));
        _capturedNodeIds[tag] = _designer.Store.ReadExtent("MetaNode")
            .First(kv => kv.Value.Fields.TryGetValue("tag", out var tv) && tv is DeEnv.Storage.TextValue t && t.Text == tag).Key;
    }

    // The identity pin: the MetaNode now carrying `tag` is the SAME OBJECT (same intrinsic id) as the one
    // captured earlier — proving a move (link-then-unlink), not a mint-a-copy-and-abandon-the-original.
    [Then("the MetaNode with tag {string} still carries its captured id")]
    public async Task ThenNodeIdUnchanged(string tag)
    {
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaNode").Values.Any(o =>
            o.Fields.TryGetValue("tag", out var tv) && tv is DeEnv.Storage.TextValue t && t.Text == tag));
        var nowId = _designer.Store.ReadExtent("MetaNode")
            .First(kv => kv.Value.Fields.TryGetValue("tag", out var tv) && tv is DeEnv.Storage.TextValue t && t.Text == tag).Key;
        await Assert.That(nowId).IsEqualTo(_capturedNodeIds[tag]);
    }

    // No MetaNode anywhere carries this tag any more — the unwrapped-away wrapper's subtree was reclaimed
    // (GC), same check ThenNoChildWithTag already performs, reused under a name that also fits a root case.
    [Then("no MetaNode has tag {string}")]
    public async Task ThenNoMetaNodeWithTag(string tag) => await ThenNoChildWithTag(tag);

    // The design's sole render root now carries the id captured earlier under `tag` — proving the promoted
    // child became the SOLE root (Members.Count == 1) with its OWN identity intact, not a re-mint.
    [Then("the design {string}'s render root has the captured id of tag {string}")]
    public async Task ThenRenderRootHasCapturedId(string label, string tag) =>
        await EventuallyAsync(() =>
        {
            var designId = DesignIdByLabel(label);
            if (designId == 0) return false;
            var render = _designer.Store.ReadNode(DeEnv.Storage.NodePath.Root.Field("designs")
                .Key(designId.ToString()).Field("render")) as DeEnv.Storage.SetValue;
            return render != null && render.Members.Count == 1 && render.Members.ContainsKey(_capturedNodeIds[tag]);
        });

    [When("I click unwrap on the root node's child {int}")]
    public async Task WhenClickUnwrapOnRootChild(int index) =>
        await ctx.Page!.Locator(RootChildren).Nth(index).Locator(":scope > .node-tag-row > button.unwrap-node").ClickAsync();

    [When("I click wrap on the root node's child {int}")]
    public async Task WhenClickWrapOnRootChild(int index) =>
        await ctx.Page!.Locator(RootChildren).Nth(index).Locator(":scope > .node-tag-row > button.wrap-node").ClickAsync();

    [When("I click unwrap on the root node")]
    public async Task WhenClickUnwrapOnRoot() =>
        await ctx.Page!.Locator(RootNode + " > .node-tag-row > button.unwrap-node").ClickAsync();

    [Then("the root node's unwrap button is disabled")]
    public async Task ThenRootUnwrapDisabled() =>
        await ctx.Page!.Locator(RootNode + " > .node-tag-row > button.unwrap-node[disabled]").First.WaitForAsync();

    [Then("the root node's unwrap button's title reads {string}")]
    public async Task ThenRootUnwrapTitle(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const b = document.querySelector({JsString(RootNode + " > .node-tag-row > button.unwrap-node")}); return b != null && b.title === {JsString(text)}; }}");

    [Then("the root node's last child's unwrap button is disabled")]
    public async Task ThenRootLastChildUnwrapDisabled() =>
        await ctx.Page!.Locator(RootLastChildElement + " > .node-tag-row > button.unwrap-node[disabled]").First.WaitForAsync();

    [Then("the root node's last child's unwrap button's title reads {string}")]
    public async Task ThenRootLastChildUnwrapTitle(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const b = document.querySelector({JsString(RootLastChildElement + " > .node-tag-row > button.unwrap-node")}); return b != null && b.title === {JsString(text)}; }}");

    // ──── M12 S5c review fold — the tie-scramble regression ────────────────────────────────────────────────────────────────────────────────
    //
    // A reorder (moveRow swaps `order` values, not ids) done BEFORE an unwrap must survive splicing. The
    // live client's stable sort tie-breaks by array-insertion order (masking a shared-order tie), but the
    // DURABLE paths (SchemaBridge.OrderedMembers, the store reload) tie-break by intrinsic id — so without
    // renumbering after the splice, the published/projected document silently reverts the reorder even
    // though the live tree editor and canvas still show it correctly. These steps reach one level deeper
    // than RootChildren (a grandchild of the root — the wrapped element's OWN children) and read the
    // DURABLE projected document text directly (SchemaBridge.ProjectDesignDb), not just the DOM.

    [When("I click move-down on the root node's child {int}'s child {int}")]
    public async Task WhenClickMoveDownOnGrandchild(int parentIndex, int childIndex) =>
        await ctx.Page!.Locator(RootChildren).Nth(parentIndex)
            .Locator(":scope > .node-children > .node-element").Nth(childIndex)
            .Locator(":scope > .node-tag-row > button.move-down").ClickAsync();

    [Then("the root node's child {int}'s children read, in order: {string}")]
    public async Task ThenGrandchildrenOrder(int parentIndex, string csv)
    {
        var expected = JsStringArray(csv.Split(',').Select(s => s.Trim()));
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const parents = [...document.querySelectorAll({JsString(RootChildren)})]; " +
            $"const parent = parents[{parentIndex}]; if (parent == null) return false; " +
            "const kids = [...parent.querySelectorAll(':scope > .node-children > .node-element')]; " +
            "const tags = kids.map(k => k.querySelector(':scope > .node-tag-row > input.node-tag').value); " +
            $"const expected = {expected}; " +
            "return tags.length === expected.length && tags.every((t, i) => t === expected[i]); }");
    }

    // The DURABLE-projection assertion (the one that catches the tie-scramble without the renumber fix):
    // reads the design fresh from the store and runs the REAL SchemaBridge.ProjectDesignDb — the same
    // walk `sys.publish`/Commit use — then checks the printed source has `first`'s opening tag textually
    // BEFORE `second`'s. A shared-order tie that SchemaBridge tie-breaks by id (not the operator's intended
    // visual order) would print them in the WRONG sequence even though the live tree editor/canvas agree
    // with each other (both client-side, both order-tie-tolerant the same way) — only this text-level check
    // on the SERVER-SIDE canonical projection sees the divergence.
    [Then("the projected document shows {string} before {string} in the render")]
    public async Task ThenProjectedOrder(string first, string second) =>
        await EventuallyAsync(() =>
        {
            var designId = DesignIdByLabel("unwrapme");
            if (designId == 0) return false;
            var design = _designer.Store.ReadNode(DeEnv.Storage.NodePath.Root.Field("designs").Key(designId.ToString()));
            if (design == null) return false;
            try
            {
                var projected = DeEnv.Designer.SchemaBridge.ProjectDesignDb(design, _designer.Store);
                var i1 = projected.IndexOf("<" + first, StringComparison.Ordinal);
                var i2 = projected.IndexOf("<" + second, StringComparison.Ordinal);
                return i1 >= 0 && i2 >= 0 && i1 < i2;
            }
            catch { return false; }
        });

    // ──── M12 CANVAS-1 — the client-computable canvas (sys.renderTree) ────────────────────────────────────────────────────────
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
        await ctx.Page!.Locator(".design-canvas span.expr-chip[data-node]", new() { HasTextString = source }).First.WaitForAsync();

    // ──── M12 CANVAS-EVAL-1 — the canvas EVALUATES expressions (sys.evalContext) ─────────────────────────────────────────
    //
    // These steps drive the eval-context wiring: an idempotent Advanced-disclosure opener (Convert-to-
    // structured collapses the disclosure — see WhenClickConvert — so a later textarea edit needs it
    // reopened, but blindly clicking the summary again would TOGGLE it shut), authoring `initialData` through
    // the same journaled-textarea idiom the access/common sections use, editing a LEAF's expr input (the
    // render's one leaf under the imported <h1>), clicking the Refresh-values control, and asserting the
    // canvas's EVALUATED text (as opposed to a chip) — the twin of ThenCanvasShowsChip.

    [When("I ensure the Advanced code disclosure is open")]
    public async Task WhenEnsureAdvancedOpen()
    {
        if (await ctx.Page!.Locator("main.ide-design-edit .design-editor details.code-areas[open]").CountAsync() == 0)
            await ctx.Page!.Locator("main.ide-design-edit .design-editor details.code-areas summary").ClickAsync();
    }

    // The `.design-initial` textarea is a journaled scalar autosave over sys.field(design, "initialData"),
    // exactly like `.design-access`/`.design-common` (see WhenTypeAccessSection). Poll the store so the seed
    // has landed before evaluating against it.
    [When("I set the design's initial data to:")]
    public async Task WhenSetInitialData(string initialData)
    {
        // Gherkin on Windows may pass CRLF; the app-document / seed parser expects LF section text.
        var normalized = initialData.Replace("\r\n", "\n").Replace("\r", "\n");
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-initial").FillAsync(normalized);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("initialData", out var iv) && iv is DeEnv.Storage.TextValue it && it.Text == normalized));
    }

    // The render's one leaf (imported from `{db.greeting}` under <h1>) — a plain journaled edit, the same
    // two-way-bound MetaNode.expr write the leaf input already exercises (ThenLeafExprInput reads it back).
    [When("I edit the leaf expr input to {string}")]
    public async Task WhenEditLeafExpr(string expr) =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree input.node-expr").FillAsync(expr);

    [When("I click Refresh values")]
    public async Task WhenClickRefreshValues() =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor button.refresh-eval").ClickAsync();

    // The canvas's <h1> (the render's one element wrapping the evaluated leaf) shows its EVALUATED text —
    // never a chip's raw source — proving the eval-context pivot actually ran the real interpreter over the
    // seed graph. Scoped to <h1> (the render's only element besides the root <main>/<section>), so this
    // cannot accidentally match a chip's OWN text (a chip is a <span>, never an <h1>).
    [Then("the design canvas shows the evaluated leaf text {string}")]
    public async Task ThenCanvasShowsEvaluatedText(string text) =>
        await ctx.Page!.Locator(".design-canvas h1", new() { HasTextString = text }).First.WaitForAsync();

    // ──── M12 S6a — `foreach`/`if` as structured rows (for-row tree editor + canvas template mode) ─────

    // A convertible render whose root has ONE `foreach` child — the S6a import lift's target shape
    // (S1b previously REFUSED this; it now mints a `kind="for"` row). Same authoring plumbing as the other
    // convertible-render fixtures: fill the `ui` textarea, poll the store for the write.
    private const string ForLoopConvertibleRender =
        """
        ui
            fn render()
                return <main class="x">
                    foreach note in db.notes
                        <li>
                            note.title

        """;

    [When("I author a for-loop convertible render into the design's UI")]
    public async Task WhenAuthorForLoopRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(ForLoopConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "treeme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == ForLoopConvertibleRender));
    }

    // The tree editor's for-row: item/collection inputs (`.node-for-item`/`.node-for-collection`) hold the
    // imported values. Scoped loosely (some input reads X) — there is only one for-row in these scenarios.
    [Then("the tree editor shows a for row with item {string} and collection {string}")]
    public async Task ThenForRowInputs(string item, string collection)
    {
        var itemInput = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree input.node-for-item").First;
        await itemInput.WaitForAsync();
        await Assert.That(await itemInput.InputValueAsync()).IsEqualTo(item);
        var collInput = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree input.node-for-collection").First;
        await collInput.WaitForAsync();
        await Assert.That(await collInput.InputValueAsync()).IsEqualTo(collection);
    }

    [When("I edit the for row's item input to {string}")]
    public async Task WhenEditForItem(string item) =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree input.node-for-item").FillAsync(item);

    [When("I edit the for row's collection input to {string}")]
    public async Task WhenEditForCollection(string collection)
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree input.node-for-collection").FillAsync(collection);
        // Poll for the journaled autosave to reach the designer's store (no timer): the collection field's
        // new value must be persisted before a later "Refresh values" recomputes sys.evalContext server-side
        // (it reads the design fresh) — otherwise the refresh would re-ship the OLD source's AST and the
        // canvas would still miss. The optimistic client edit re-renders the canvas immediately regardless.
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaNode").Values.Any(o =>
            o.Fields.TryGetValue("collection", out var v) && v is DeEnv.Storage.TextValue t && t.Text == collection));
    }

    // The canvas's for-template badge shows the loop var name in `.for-item` — the NO-CTX marker (S6a; the
    // loop is not evaluated). Auto-waits, so an item-input edit's live repaint (no reload) is observed here.
    [Then("the design canvas shows a for-template with item {string}")]
    public async Task ThenCanvasForTemplateItem(string item) =>
        await ctx.Page!.Locator(".design-canvas .for-template .for-item", new() { HasTextString = item }).First.WaitForAsync();

    // The root's "+ for" control (same add-row as "+ element"/"+ text"/"+ attr") appends a for-row LAST,
    // exactly like E2's element append (order = max sibling order + 1).
    [When("I add a for loop to the root node")]
    public async Task WhenAddForLoop() =>
        await ctx.Page!.Locator(RootNode + " > .node-add-row > button.add-for").ClickAsync();

    // The root's last child (by DOM order under `.node-children`) carries the for-row's `.node-for` class —
    // NOT `.node-element` (a for-row is a distinct kind), so this can't reuse RootLastChildElement.
    [Then("the root node's last child is a for row")]
    public async Task ThenRootLastChildIsForRow() =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => {{ const kids = document.querySelectorAll({JsString(RootNode + " > .node-children > *")}); " +
            "const last = kids[kids.length - 1]; return last != null && last.classList.contains('node-for'); }");

    // The added for-row's own remove control lives in its `.node-for-head` (the S6a mirror of E2's
    // tag-row-anchored ×). Scoped to the LAST for-row under the root's children.
    [When("I remove the root node's last child for row")]
    public async Task WhenRemoveLastForRow() =>
        await ctx.Page!.Locator(RootNode + " > .node-children > .node-for:last-child > .node-for-head > button.remove-node").ClickAsync();

    // The GC-reclaim proof, by COUNT rather than presence: the scenario's imported for-row is edited (still
    // kind="for") BEFORE a second one is added and then removed again, so "no for row remains" would be
    // false even after a correct removal. Counting is unambiguous: 1 (imported) -> 2 (after add) -> 1 (after
    // remove) proves the ADDED subtree specifically was reclaimed, not a false "zero" read.
    [Then("the render tree has {int} for row(s)")]
    public async Task ThenForRowCount(int count) =>
        await EventuallyAsync(() =>
            _designer.Store.ReadExtent("MetaNode").Values.Count(o =>
                o.Fields.TryGetValue("kind", out var kv) && kv is DeEnv.Storage.TextValue { Text: "for" }) == count);

    // ──── M12 S6b — the canvas EVALUATES for/if rows (row-scope evaluation) ─────────────────────────────────────────────────
    //
    // A convertible render whose <main> holds a `foreach note in db.notes → <li>{note.title}` AND an
    // `if db.flag → <p>"ON" else <p>"OFF"`. Paired with a Db{notes: set of Note{title}, flag: bool} schema
    // and a two-note seed, so after Convert the WITH-CTX canvas evaluates the loop (two <li>s with real
    // titles) and the if (the taken branch). Same authoring plumbing as the other fixtures.
    private const string ForAndIfConvertibleRender =
        """
        ui
            fn render()
                return <main class="x">
                    foreach note in db.notes
                        <li>
                            note.title
                    if db.flag
                        <p class="on">
                            "ON"
                    else
                        <p class="off">
                            "OFF"

        """;

    [When("I author a for-and-if convertible render into the design's UI")]
    public async Task WhenAuthorForAndIfRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(ForAndIfConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "loopme" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == ForAndIfConvertibleRender));
    }

    // A REAL evaluated element in the canvas whose textContent is the given text — the S6b proof that a
    // for-body instance (`<li>{note.title}` -> "Alpha"/"Beta") or an if taken-branch (`<p>"ON"`) rendered as
    // actual content, NOT a chip and NOT a for-template badge. Auto-waits so a live repaint (edit/refresh)
    // is observed with no reload. Matches ANY element of that tag in the canvas whose text equals `text`.
    [Then("the design canvas shows a {string} element reading {string}")]
    public async Task ThenCanvasElementReading(string tag, string text) =>
        await ctx.Page!.Locator($".design-canvas {tag}", new() { HasTextString = text }).First.WaitForAsync();

    // The falsy/omitted if-branch is NEVER rendered: the canvas must not contain the given text anywhere.
    // Guarded by a WaitForFunction so it settles rather than reading a mid-render frame (the preceding
    // positive assertions already prove the canvas is populated, so a persistent presence would fail here).
    [Then("the design canvas does not show the text {string}")]
    public async Task ThenCanvasNoText(string text) =>
        // When the text is gone the "canvas that has this text" locator no longer matches.
        await ctx.Page!.Locator(".design-canvas", new() { HasTextString = text }).First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

    // The tree editor's for-row collection input still reads the edited source (the race-guard proof: the
    // canvas falls to the template, but the operator's own input is UNDISTURBED — not reverted).
    [Then("the tree editor shows a for-collection input reading {string}")]
    public async Task ThenForCollectionInputReads(string collection)
    {
        var collInput = ctx.Page!.Locator("main.ide-design-edit .design-editor .render-tree input.node-for-collection").First;
        await collInput.WaitForAsync();
        await Assert.That(await collInput.InputValueAsync()).IsEqualTo(collection);
    }

    // ──── M12 F3 — call-position evaluation of design fns ─────────────────────────────────────────────────────────────────────────────────
    //
    // A convertible render that DEFINES a HELPER (`fmtGreeting(name)`, a scalar-returning fn — no
    // element root) AND a COMPONENT (`NoteCard(note)`, F2's own shape) and INVOKES both: the helper by
    // plain call syntax inside a leaf (`fmtGreeting(db.greeting)`, wrapped in its own <span> for a clean
    // assertion target), the component via a `foreach`-driven tag invocation (the F2 fixture, proving F3
    // and F2 coexist on one canvas). Same authoring plumbing as the other convertible-render fixtures.
    private const string CallEvalConvertibleRender =
        """
        ui
            fn fmtGreeting(name)
                return "Hi " + name
            fn NoteCard(note)
                return <li>
                    note.title
            fn render()
                return <main>
                    <span class="greeting">
                        fmtGreeting(db.greeting)
                    foreach n in db.notes
                        <NoteCard note={n}>

        """;

    [When("I author a call-eval convertible render into the design's UI")]
    public async Task WhenAuthorCallEvalRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(CallEvalConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "calleval" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == CallEvalConvertibleRender));
    }

    // The F3b staleness affordance: ONE banner (div.stale-fns-banner) at the canvas root when the
    // shipped ctx.fns fingerprints no longer match the LIVE fns rows (an fn body edit since evalContext
    // was last computed/refreshed).
    [Then("the design canvas shows the stale-fns banner")]
    public async Task ThenCanvasShowsStaleBanner() =>
        await ctx.Page!.Locator(".design-canvas .stale-fns-banner").First
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });

    [Then("the design canvas does not show the stale-fns banner")]
    public async Task ThenCanvasNoStaleBanner() =>
        await ctx.Page!.Locator(".design-canvas .stale-fns-banner").First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

    // ──── M12 eval-degrade-banner — an honest notice when evalContext itself fails to build ────────────────────
    //
    // BuildEvalContext's catch arm ships a non-empty `error` (the REAL exception message, never a
    // paraphrase) alongside the empty db/exprs/fns/ambients/params payload; the walk splices ONE
    // div.eval-degrade-banner ahead of the tree, mirroring the stale-fns-banner idiom above.

    [Then("the design canvas shows the eval-degrade notice mentioning {string}")]
    public async Task ThenCanvasShowsEvalDegradeNotice(string substring) =>
        await ctx.Page!.Locator(".design-canvas .eval-degrade-banner", new() { HasTextString = substring }).First.WaitForAsync();

    [Then("the design canvas does not show the eval-degrade notice")]
    public async Task ThenCanvasNoEvalDegradeNotice() =>
        await ctx.Page!.Locator(".design-canvas .eval-degrade-banner").First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

    // ──── M12 V1b — init-evaluated state in the static canvas ────────────────────────────────────────────────────────────────────────────────
    //
    // A top-level `ui var greeting` (design-level state, V1's import shape) referenced in its own <span>,
    // AND a real stateful `Counter()` (V1's canonical setup/view shape) INVOKED as a tag inside the
    // render (F2's expansion) — the ONE fixture that proves BOTH V1b binding sites at once: the walk
    // ROOT (design.vars) and ExpandFn's own bodyBindings (a MetaFn's vars). Same authoring plumbing as
    // the other convertible-render fixtures.
    private const string InitStateConvertibleRender =
        """
        ui
            var greeting = "hi"
            fn Counter()
                var count = 0
                fn render()
                    return <button onClick={() => count = count + 1}>
                        count
                return render
            fn render()
                return <main>
                    <Counter>
                    <span>
                        greeting

        """;

    [When("I author a convertible render with a design var and an invoked Counter component into the design's UI")]
    public async Task WhenAuthorInitStateRender()
    {
        await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").FillAsync(InitStateConvertibleRender);
        await EventuallyAsync(() => _designer.Store.ReadExtent("Design").Values.Any(o =>
            o.Fields.TryGetValue("label", out var lv) && lv is DeEnv.Storage.TextValue { Text: "initstate" }
            && o.Fields.TryGetValue("ui", out var uv) && uv is DeEnv.Storage.TextValue ut && ut.Text == InitStateConvertibleRender));
    }

    // Edit the design-level var's INIT input (`.design-state-section .var-row input.var-init`, by row
    // index — this scenario ever imports exactly one design-level var, so index 0 is unambiguous).
    [When("I edit design-level state var {int}'s init to {string}")]
    // The feature writes inner quotes as `\"` (the Gherkin escape for a literal `"` inside the quoted
    // argument); Reqnroll passes the backslashes through verbatim (see WhenEditComponentBodyLeaf /
    // AccessSteps.GivenAccessRule), so unescape them first.
    public async Task WhenEditDesignStateVarInit(int index, string newInit) =>
        await ctx.Page!.Locator("main.ide-design-edit .design-editor .design-state-section .var-row input.var-init").Nth(index)
            .FillAsync(newInit.Replace("\\\"", "\""));

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
        await Assert.That(await ctx.Page!.Locator("main.ide-design-edit .design-editor textarea.design-ui").CountAsync()).IsEqualTo(0);

    [Then("the design editor no longer shows the Convert-to-structured button")]
    public async Task ThenNoConvertButton() =>
        await Assert.That(await ctx.Page!.Locator("main.ide-design-edit .design-editor button.convert-render").CountAsync()).IsEqualTo(0);

    [Then("the instances list shows the instance {string} running design {string}")]
    public async Task ThenListShows(string label, string designLabel)
    {
        var row = RowFor(label);
        await Assert.That(await row.CountAsync()).IsEqualTo(1);
        // The list is the generic <SetTable> with columns ["name", "design"]: the `design` column is an
        // object-ref cell that SetTable renders as the referenced Design's label text (a plain <td>, not
        // the row-id identity cell). Assert that cell holds the expected design label.
        await Assert.That(await row.Locator("td:not(.row-id)", new() { HasTextString = designLabel }).CountAsync())
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
        await ctx.Page!.Locator("main.ide-list .set-row", new() {
            Has = ctx.Page.Locator("a.row-link", new() { HasTextString = name })
        }).WaitForAsync();
        // The DESIGN CELL must populate IN PLACE too — no reload. This is the load-bearing assertion:
        // the kernel mirror writes the new Instance's `design` reference AFTER adding it to the set (a GC
        // ordering constraint), so the row could momentarily render with an empty design cell; the
        // in-place refetch must show the design label. WaitForSelector (auto-waiting) proves it appears
        // without racing the row's first paint — if it never populates in place, this fails (a real
        // refetch-timing bug), rather than a count that might pass on a stale/empty cell.
        await ctx.Page.Locator("main.ide-list .set-row", new() {
            Has = ctx.Page.Locator("a.row-link", new() { HasTextString = name })
        }).Locator("td:not(.row-id)", new() { HasTextString = designLabel }).WaitForAsync();
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
        // a saturated full suite — a wide window keeps it deterministic under peak load.
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await EventuallyAsync(() => File.Exists(target.Spec.SchemaPath)
            && File.ReadAllText(target.Spec.SchemaPath).Contains(typeName));
    }

    [Then("the {string} instance's app document declares {string}")]
    public async Task ThenTargetDeclares(string label, string declaration)
    {
        // Apply deployed the projected app document; assert it contains the given prop declaration
        // (e.g. "checked set of TodoList" / "text dict of text by text") -- the canonical AppPrint
        // form of a collection-shaped prop, proving cardinality + key type flowed through projection.
        // DesignerTestMs: the deploy projects the WHOLE app + resets data under peak full-suite load.
        var target = ctx.Kernel!.Instances.Single(i => i.Spec.App == label);
        await EventuallyAsync(() => File.Exists(target.Spec.SchemaPath)
            && File.ReadAllText(target.Spec.SchemaPath).Contains(declaration));
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
        });
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

    // LIGHT authoring seam (cardinality / key type): the designer store has the shape; no apply/file poll.
    // Match by prop name only (same as the When steps that wrote these fields) — unique in the todo design.
    [Then("the design's prop {string} is a set of {string}")]
    public async Task ThenDesignerPropIsSetOf(string propName, string elementType) =>
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values.Any(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == propName
            && o.Fields.TryGetValue("type", out var ty) && ty is DeEnv.Storage.TextValue tt && tt.Text == elementType
            && o.Fields.TryGetValue("cardinality", out var c) && c is DeEnv.Storage.TextValue ct && ct.Text == "set"));

    [Then("the design's prop {string} is a dict of {string} by {string}")]
    public async Task ThenDesignerPropIsDictOf(string propName, string valueType, string keyType) =>
        await EventuallyAsync(() => _designer.Store.ReadExtent("MetaProp").Values.Any(o =>
            o.Fields.TryGetValue("name", out var n) && n is DeEnv.Storage.TextValue nt && nt.Text == propName
            && o.Fields.TryGetValue("type", out var ty) && ty is DeEnv.Storage.TextValue tt && tt.Text == valueType
            && o.Fields.TryGetValue("cardinality", out var c) && c is DeEnv.Storage.TextValue ct && ct.Text == "dictionary"
            && o.Fields.TryGetValue("keyType", out var k) && k is DeEnv.Storage.TextValue kt && kt.Text == keyType));

    // DURABLE projection (no open-instance / apply / disk poll): ProjectDesignDb on the live Design node —
    // the same walk publish/apply uses. Proves collection-shaped authoring survives the projector.
    [Then("projecting design {string} declares {string}")]
    public async Task ThenProjectedDesignDeclares(string label, string declaration) =>
        await EventuallyAsync(() =>
        {
            var designId = DesignIdByLabel(label);
            if (designId == 0) return false;
            var design = _designer.Store.ReadNode(DeEnv.Storage.NodePath.Root.Field("designs").Key(designId.ToString()));
            if (design == null) return false;
            try
            {
                var projected = DeEnv.Designer.SchemaBridge.ProjectDesignDb(design, _designer.Store);
                return projected.Contains(declaration, StringComparison.Ordinal);
            }
            catch { return false; }
        });

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
        await ctx.Page.Locator("main.ide-design-edit .design-editor .add-type").WaitForAsync();
        var emptyNameInputs = ctx.Page.Locator("main.ide-design-edit .design-editor input.type-name[value=\"\"]");
        await Assert.That(await emptyNameInputs.CountAsync()).IsEqualTo(0);
    }

    // ──── Then: progressive disclosure (fields hidden until their shape is chosen) ────

    [Then("the prop {string} shows no key-type field")]
    public async Task ThenPropNoKeyType(string propName) =>
        // A single/set prop's key-type field is hidden (it is meaningful only for a dictionary). The field
        // stays in the DOM — progressive disclosure flips visibility via the row's class — so assert it is
        // HIDDEN, not absent.
        await PropKeytypeInput(propName).WaitForAsync(Hidden);

    [Then("the prop {string} shows a key-type field")]
    public async Task ThenPropKeyType(string propName) =>
        // Set to dictionary, the key-type field becomes visible via the row's class change — wait for it
        // (proving the disclosure reconciles when cardinality changes).
        await PropKeytypeInput(propName).WaitForAsync();

    [Then("the prop {string} shows a multiline toggle")]
    public async Task ThenPropMultilineToggle(string propName) =>
        // A single text prop's row shows the multiline checkbox (visible via the row's is-text-single
        // class). Wait for it visible (the field is always in the DOM; disclosure flips visibility).
        await PropMultilineInput(propName).WaitForAsync();

    [Then("the prop {string} shows no multiline toggle")]
    public async Task ThenPropNoMultilineToggle(string propName) =>
        // A non-text (or non-single) prop's multiline checkbox is hidden — multiline is valid only on a
        // single text prop. The field stays in the DOM; assert it is HIDDEN, not absent.
        await PropMultilineInput(propName).WaitForAsync(Hidden);

    [Then("the just-added type shows a props editor")]
    public async Task ThenJustAddedPropsEditor() =>
        await JustAddedTypeRow().Locator(".props-editor").WaitForAsync();

    [Then("the just-added type shows no props editor")]
    public async Task ThenJustAddedNoPropsEditor() =>
        await JustAddedTypeRow().Locator(".props-editor").WaitForAsync(Hidden);

    [Then("the just-added type shows a values field")]
    public async Task ThenJustAddedValuesField() =>
        await JustAddedTypeRow().Locator("input.type-values").WaitForAsync();

    [Then("the just-added type shows no values field")]
    public async Task ThenJustAddedNoValuesField() =>
        await JustAddedTypeRow().Locator("input.type-values").WaitForAsync(Hidden);

    // M12 eval-degrade-banner — the type-card hint (typeHint idiom, mirrors fnNameHint) for a baseType
    // "object" type with zero props (the same condition that degrades evalContext).
    [Then("the just-added type shows the hint {string}")]
    public async Task ThenJustAddedTypeHint(string hintText) =>
        await JustAddedTypeRow().Locator(".type-hint", new() { HasTextString = hintText }).WaitForAsync();

    // The hint span is a structural `if` (mirroring fn-name-hint/var-name-hint) — ABSENT from the DOM
    // when there is no hint, not merely CSS-hidden, so this waits for absence (Detached) rather than Hidden.
    [Then("the just-added type shows no hint")]
    public async Task ThenJustAddedNoTypeHint() =>
        await JustAddedTypeRow().Locator(".type-hint")
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });

    // ──── Then: the grouped prop-type picker ─────────────────────────────────────────────────────────────────────────────

    [Then("the prop {string} type picker offers the built-in type {string}")]
    public async Task ThenPickerOffersBuiltin(string propName, string typeName) =>
        await PropTypeSelect(propName)
            .Locator("optgroup[label=\"Built-in\"] option", new() { HasTextString = typeName })
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });

    [Then("the prop {string} type picker offers the design type {string}")]
    public async Task ThenPickerOffersDesignType(string propName, string typeName) =>
        await PropTypeSelect(propName)
            .Locator("optgroup[label=\"This design\"] option", new() { HasTextString = typeName })
            .WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });

    [Then("the prop {string} type picker keeps built-in and design types in separate groups")]
    public async Task ThenPickerGrouped(string propName)
    {
        // The system scalars and the user's own types live in SEPARATE <optgroup>s — not flatly intermixed.
        var select = PropTypeSelect(propName);
        await select.Locator("optgroup[label=\"Built-in\"]").WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        await select.Locator("optgroup[label=\"This design\"]").WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    // ──── Then: client-side (SPA) navigation in the custom designer ────────────────────────────────

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

    // ──── Then: no partial-content FLASH on the deep editor (round-2) ────────────────────────────

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

    // ──── Then: db.instances seeded from registry (slice 1) ────────────────────────────────────────────────

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

    // ──── When/Then: db.instances mirror (Slice 2 — direct host-action calls, no browser) ────────────────────────

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

    // Seed directly into the designer's store (bypassing all UI tree editor / convert paths) so the render
    // root is a bare leaf MetaNode (tag="", no children). Used to prove the "can't hold children" guard + disabled
    // palette for the second-root case. After this the test does "reload the design editor" to pick up the change.
    [When(@"the design ""(.*)""'s render root is seeded as a bare leaf, bypassing the UI")]
    public async Task WhenTheDesignsRenderRootIsSeededAsBareLeaf(string label)
    {
        var designId = DesignIdByLabel(label);
        if (designId == 0)
            throw new Exception($"Design not found for bare-leaf seed: {label}");
        var renderPath = DeEnv.Storage.NodePath.Root.Field("designs").Key(designId.ToString()).Field("render");
        var render = _designer.Store.ReadNode(renderPath) as DeEnv.Storage.SetValue;
        if (render != null)
        {
            foreach (var mid in render.Members.Keys.ToList())
                _designer.Store.RemoveFromSet(renderPath, mid);
        }
        var leafId = _designer.Store.CreateObject("MetaNode", new DeEnv.Storage.ObjectValue(new Dictionary<string, DeEnv.Storage.NodeValue>
        {
            ["tag"] = new DeEnv.Storage.TextValue(""),
            ["expr"] = new DeEnv.Storage.TextValue(""),
            ["order"] = new DeEnv.Storage.IntValue(0),
        }));
        _designer.Store.AddToSet(renderPath, leafId);
        await Task.CompletedTask;
    }

    [Then("the design editor's sections are ordered types, render, publish, branches")]
    public async Task ThenDesignEditorSectionsOrdered()
    {
        // Markers must match designEditor in instances/1/app.deenv: types (.add-type), structured
        // render (.render-section / .render-tree), publishSection (.publish-section), branchSection
        // (.branch-section). NOT instance-page apply-design, and NOT .branch (that matches if/else
        // .node-branch rows inside the render tree).
        var editor = ctx.Page!.Locator("main.ide-design-edit .design-editor");
        await editor.Locator(".add-type, .type-card").First.WaitForAsync();
        await editor.Locator(".render-section, .render-tree").First.WaitForAsync();
        await editor.Locator(".publish-section").First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        await editor.Locator(".branch-section").First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        // Order proof: types before render before publish before branches (commit-bar may sit above types).
        var inOrder = await ctx.Page!.EvaluateAsync<bool>(@"() => {
            const ed = document.querySelector('main.ide-design-edit .design-editor');
            if (!ed) return false;
            const types = ed.querySelector('.add-type, .type-card');
            const render = ed.querySelector('.render-section, .render-tree');
            const pub = ed.querySelector('.publish-section');
            const br = ed.querySelector('.branch-section');
            if (!types || !render || !pub || !br) return false;
            const follows = (a, b) => (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
            return follows(types, render) && follows(render, pub) && follows(pub, br);
        }");
        await Assert.That(inOrder).IsTrue();
    }

}
