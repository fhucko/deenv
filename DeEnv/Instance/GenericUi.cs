using DeEnv.Code;

namespace DeEnv.Instance;

// The self-hosted generic UI (milestone 9), a reflective Code library over schema data,
// synthesized into the effective ui at render time. It is the DEFAULT UI: any app without a
// fully-custom `fn render()` (including an app with no `ui` section) renders through it. The
// library is also PUBLIC (milestone 11): its components are named in PascalCase and reachable
// from a custom `fn render()` — the renderer parents the app scope under the library scope, so
// `<ObjectForm …>` &c. resolve by name and a hand-written render can compose them.
//
//   • Input(obj, desc, variant) — the baseType-appropriate, two-way-bound control for one prop (the
//     desc): <input type={InputType(...)}> for a scalar, a checkbox for bool, a <select> of values
//     for an enum. The first composition PRIMITIVE — extracted from the three places that inlined the
//     same baseType branch (the object-form field, the reference create-new draft, the set add-form).
//     `variant` is an optional MUI-style presentation choice OWNED by the library (callers never
//     restyle via their own CSS): omitted → "outlined" (bordered, the form default); "standard" → a
//     borderless control that reads as plain text and reveals an underline on hover/focus (for inline
//     contexts like a checklist row). The only thing a caller passes for looks; styling is internal.
//   • Field(obj, desc) — a labeled field: a <div class="field"> wrapping the prop's humanized
//     <label> and its Input. The labeled-field composite ObjectForm and a custom render compose.
//   • ObjectForm(obj, meta, base, autosave) — an object page: a field per prop (a Field for a
//     scalar, a nested RefEditor for a single object reference, an inline SetTable for an object set,
//     or an inline DictTable for a dictionary). A collection's label is a navigable list-title link to
//     its own route. `base` is the page's URL path, so inline links nest. A COMPONENT: it holds a
//     local draft — a fresh `sys.new(meta)` object filled from the live object's scalars by
//     `sys.setFields(state.draft, obj)` — and returns a render fn. `autosave` (bool) controls when
//     scalar edits commit: omitted/false (the DEFAULT, what the synthesized object view passes)
//     STAGES scalar edits into the draft and commits them on a Save button (`sys.setFields(obj,
//     draft)`); Discard copies the object's scalars back ONTO the draft (`sys.setFields(draft, obj)`
//     — in place, so the draft keeps its identity and the slot-keyed Fields re-read it). true keeps
//     live per-keystroke autosave with no buttons. Either way COLLECTION props (reference/set/
//     dictionary) bind to the LIVE object — never staged (each manages its own members, which
//     persist on their own).
//   • RefEditor(parent, prop, target) — a reference editor: current label, a pick button
//     per extent() candidate, a clear button, and a create-new form. A COMPONENT: its body
//     runs once as init (a local `state` holding a draft), and it returns a render fn.
//   • SetTable(set, desc, setPath) — a set table: an aligned header + member rows + a `+ New`
//     button. A whole data row is navigable — its first cell wraps the member's identity (labelProp
//     value) in a stretched `<a class="row-link" href=nest(setPath, m)>` (CSS `::after { inset:0 }`
//     covers the row); a per-row Remove sits z-raised above the overlay. A bool column renders a
//     read-only ✓/✗ glyph, never "true"/"false". Shared structure with DictTable (row-link + Remove
//     + bool cell + the `+ New`/create-form swap), differing only in the identity source. A COMPONENT:
//     a local `state.creating` flag SWAPS the render between the table (default) and a labeled CREATE
//     FORM (the same `Field` label+Input the edit page uses, per scalar prop) with Save / Cancel — so
//     the create form is hidden until asked, not an always-visible inline add row. Save commits the
//     draft (`set.add`) and returns to the table; Cancel discards it; both reset `state.draft`.
//
// Builtins do the reflective work, all under the framework `sys` namespace: sys.field (dynamic
// access), sys.humanize (labels), sys.extent (a type's objects), sys.schema (a type's descriptor),
// sys.setRef (set/clear a reference), sys.nest (a URL path-join for nested member links), sys.new (a
// fresh default-valued object built from a descriptor — a create-new draft, or the seed of an edit
// draft), sys.setFields (bulk-copy one object's props onto another — fill or commit a draft).
// `obj.prop = x` resets a component's draft after Create.
//
// A type's descriptor — { name, labelProp, props } (plus a now-unused `blank`) — is fetched by
// `sys.schema(typeName)`,
// resolved server-side from the schema (the descriptor literal GenericUi threads into the executor)
// and shipped to the client like extent. Components are tag-invoked and slot-keyed, so a descriptor
// is now plain argument data (its identity carries no reactivity). Cross-type references are stored
// by type NAME (cycle-safe); a component resolves them with sys.schema(name).
//
// Synthesis is render-time only: the canonical InstanceDescription (what AppPrint emits)
// carries no UI at all for a plain app. A synthesized OBJECT view (Type=T, Prop=null) binds the
// routed object; a REFERENCE / SET view (Type=O, Prop=P) binds the parent object that owns
// the prop — both reuse the M8 type-view client binding (no new wire shape).
public static class GenericUi
{
    // The reserved view "type" for the shared scalar leaf editor (a scalar dictionary
    // entry page). SsrRenderer.ResolveView dispatches a scalar dict entry to it.
    public const string LeafViewType = "__leaf";

    // The reserved view "type" for the self-hosted NotFound page (an unrouted URL).
    public const string NotFoundViewType = "__notFound";

    private const string StdlibSource = """
        ui
            fn Input(obj, desc, variant)
                if desc.baseType == "bool"
                    return <input type="checkbox" class={desc.name} checked={sys.field(obj, desc.name)}>
                else if desc.baseType == "enum"
                    return <select class={desc.name} value={sys.field(obj, desc.name)}>
                        <option value="">
                            "(none)"
                        foreach v in desc.values
                            <option value={v}>
                                sys.humanize(v)
                else if variant == "standard"
                    return <input type={InputType(desc.baseType)} class={desc.name} value={sys.field(obj, desc.name)} variant="standard">
                else
                    return <input type={InputType(desc.baseType)} class={desc.name} value={sys.field(obj, desc.name)}>

            fn Field(obj, desc)
                return <div class="field">
                    <label class={desc.name}>
                        sys.humanize(desc.name)
                    <Input obj={obj} desc={desc}>

            fn ObjectForm(obj, meta, base, autosave)
                var state = { draft: sys.new(meta) }
                sys.setFields(state.draft, obj)
                fn save()
                    sys.setFields(obj, state.draft)
                fn discard()
                    sys.setFields(state.draft, obj)
                fn render()
                    return <div class="object-form">
                        <h2>
                            meta.name
                        foreach p in meta.props
                            if p.baseType == "object"
                                <div class="field">
                                    <label class={p.name}>
                                        sys.humanize(p.name)
                                    <RefEditor parent={obj} prop={p.name} target={sys.schema(p.target)}>
                            else if p.baseType == "set"
                                <div class="field">
                                    <a class="list-title" href={sys.nest(base, p.name)}>
                                        sys.humanize(p.name)
                                    <SetTable set={sys.field(obj, p.name)} desc={sys.schema(p.element)} setPath={sys.nest(base, p.name)}>
                            else if p.baseType == "dictionary"
                                <div class="field">
                                    <a class="list-title" href={sys.nest(base, p.name)}>
                                        sys.humanize(p.name)
                                    <DictTable dict={sys.field(obj, p.name)} desc={p} base={sys.nest(base, p.name)}>
                            else if autosave == true
                                <Field obj={obj} desc={p}>
                            else
                                <Field obj={state.draft} desc={p}>
                        if autosave != true
                            <div class="form-actions">
                                <button class="save" onClick={save}>
                                    "Save"
                                <button class="discard" onClick={discard}>
                                    "Discard"
                return render

            fn RefEditor(parent, prop, target)
                var state = { pick: 0, draft: sys.new(target) }
                fn createNew()
                    sys.setRef(parent, prop, state.draft)
                    state.draft = sys.new(target)
                fn render()
                    return <div class="ref-editor">
                        <h3 class="ref-type">
                            target.name
                        <div class="ref-current">
                            "Current: "
                            if sys.field(parent, prop) == null
                                "(none)"
                            else
                                sys.field(sys.field(parent, prop), target.labelProp)
                        <div class="ref-controls">
                            <select class="ref-pick" value={state.pick}>
                                <option value="0">
                                    "(choose…)"
                                foreach c in sys.extent(target.name)
                                    <option value={sys.id(c)}>
                                        sys.field(c, target.labelProp)
                            foreach c in sys.extent(target.name)
                                if sys.id(c) == state.pick
                                    <button class="ref-set" onClick={() => sys.setRef(parent, prop, c)}>
                                        "Set"
                            <button class="ref-clear" onClick={() => sys.setRef(parent, prop, null)}>
                                "Clear"
                        <div class="ref-new">
                            foreach p in target.props
                                if p.baseType != "object" && p.baseType != "set"
                                    <label class={p.name}>
                                        sys.humanize(p.name)
                                    <Input obj={state.draft} desc={p}>
                            <button class="ref-create" onClick={createNew}>
                                "Create new"
                return render

            fn SetTable(set, desc, setPath)
                var state = { draft: sys.new(desc), creating: false }
                fn save()
                    set.add(state.draft)
                    state.draft = sys.new(desc)
                    state.creating = false
                fn startCreate()
                    state.creating = true
                fn cancel()
                    state.draft = sys.new(desc)
                    state.creating = false
                fn render()
                    if state.creating
                        return <div class="create-form">
                            <h3>
                                "New "
                                sys.humanize(desc.name)
                            foreach p in desc.props
                                if p.baseType != "object" && p.baseType != "set" && p.baseType != "dictionary"
                                    <Field obj={state.draft} desc={p}>
                            <div class="create-actions">
                                <button class="set-add" onClick={save}>
                                    "Save"
                                <button class="cancel" onClick={cancel}>
                                    "Cancel"
                    return <div class="set-table">
                        <table>
                            <tr class="set-head">
                                <th>
                                    sys.humanize(desc.labelProp)
                                foreach p in desc.props
                                    if p.baseType != "object" && p.baseType != "set" && p.name != desc.labelProp
                                        <th>
                                            sys.humanize(p.name)
                                <th>
                            foreach m in set
                                <tr class="set-row">
                                    <td class="row-id">
                                        <a class="row-link" href={sys.nest(setPath, m)}>
                                            sys.field(m, desc.labelProp)
                                    foreach p in desc.props
                                        if p.baseType != "object" && p.baseType != "set" && p.name != desc.labelProp
                                            <td>
                                                if p.baseType == "bool"
                                                    <span class="bool-cell">
                                                        boolGlyph(sys.field(m, p.name))
                                                else
                                                    sys.field(m, p.name)
                                    <td class="row-action">
                                        <button class="set-remove" onClick={() => set.remove(m)}>
                                            "Remove"
                        <button class="new-btn" onClick={startCreate}>
                            "New "
                            sys.humanize(desc.name)
                return render

            fn DictTable(dict, desc, base)
                var state = { key: "", draft: sys.new(desc), error: "", creating: false }
                fn save()
                    if state.key == ""
                        state.error = "Key is required"
                    else if dict.any(m => sys.field(m, "__key") == state.key)
                        state.error = "Key already exists"
                    else
                        if desc.isScalar
                            dict.setEntry(state.key, sys.field(state.draft, "value"))
                        else
                            dict.setEntry(state.key, state.draft)
                        state.key = ""
                        state.draft = sys.new(desc)
                        state.error = ""
                        state.creating = false
                fn startCreate()
                    state.creating = true
                fn cancel()
                    state.key = ""
                    state.draft = sys.new(desc)
                    state.error = ""
                    state.creating = false
                fn render()
                    if state.creating
                        return <div class="create-form">
                            <h3>
                                "New "
                                sys.humanize(desc.name)
                            <div class="field">
                                <label class="dict-key">
                                    "Key"
                                <input class="dict-key" value={state.key}>
                            foreach p in desc.valueProps
                                <Field obj={state.draft} desc={p}>
                            if desc.isScalar
                                <div class="field">
                                    <label class="value">
                                        "Value"
                                    <input type={InputType(desc.element)} class="value" value={sys.field(state.draft, "value")}>
                            <div class="create-actions">
                                <button class="dict-add" onClick={save}>
                                    "Save"
                                <button class="cancel" onClick={cancel}>
                                    "Cancel"
                            <div class="dict-error">
                                state.error
                    return <div class="dict-table">
                        <table>
                            <tr class="dict-head">
                                <th>
                                    "Key"
                                foreach p in desc.valueProps
                                    <th>
                                        sys.humanize(p.name)
                                if desc.isScalar
                                    <th>
                                        "Value"
                                <th>
                            foreach m in dict
                                <tr class="dict-row">
                                    <td class="row-id">
                                        <a class="row-link" href={sys.nest(base, sys.field(m, "__key"))}>
                                            sys.field(m, "__key")
                                    foreach p in desc.valueProps
                                        <td>
                                            if p.baseType == "bool"
                                                <span class="bool-cell">
                                                    boolGlyph(sys.field(m, p.name))
                                            else
                                                sys.field(m, p.name)
                                    if desc.isScalar
                                        <td>
                                            sys.field(m, "value")
                                    <td class="row-action">
                                        <button class="dict-remove" onClick={() => dict.remove(m)}>
                                            "Remove"
                        <button class="new-btn" onClick={startCreate}>
                            "New "
                            sys.humanize(desc.name)
                return render

            fn LeafForm(entry, base)
                return <div class="leaf-form">
                    <h2>
                        sys.field(entry, "__key")
                    <input class="value" value={sys.field(entry, "value")}>

            fn NotFoundForm()
                status = 404
                return <main class="not-found">
                    <h1>
                        "Not found"
                    <p class="missing">
                        path
                    <a class="home" href="/">
                        "Home"

            fn InputType(baseType)
                if baseType == "int"
                    return "number"
                if baseType == "decimal"
                    return "number"
                if baseType == "date"
                    return "date"
                return "text"

            fn boolGlyph(v)
                if v
                    return "✓"
                return "✗"
        """;

    // The effective ui for rendering: the app's ui augmented with the generic library (ALWAYS) plus,
    // when the app has no custom `fn render()`, an OBJECT view per self-hostable type and a
    // REFERENCE / SET / DICT view per such prop. Functions are renumbered (CodeIds) over the whole
    // set so server and client key the memo cache alike.
    //
    // The library is now PUBLIC: a custom `fn render()` reaches it through the scope chain (the
    // renderer parents the app scope under the library scope), so it can compose <ObjectForm> &c.
    // — hence the library functions + descriptors are synthesized for EVERY app, custom or not. A
    // custom app gets NO per-type views (it owns its own routing); only a generic-UI (no custom
    // render) app does.
    //
    // SystemNames lists the synthesized framework members (the library functions) so the renderer
    // places them in the LIBRARY scope, between the system scope and the app scope. Descriptors maps
    // "TypeName" → a type's descriptor literal and "Owner/prop" → a dict prop's descriptor, threaded
    // into the executor so `sys.schema(...)` resolves a shape (the replacement for the old
    // `__descs`/`__dictDescs` globals).
    public static (InstanceUi? Ui, IReadOnlySet<string> SystemNames, IReadOnlyDictionary<string, CodeObject> Descriptors)
        Effective(InstanceDescription desc)
    {
        // A custom `fn render()` owns the whole URL space, so it gets the library but no per-type
        // views. A plain app — no `ui` section, or only common helpers — renders entirely through
        // the synthesized per-type views over the Code ObjectForm library (the DEFAULT UI).
        var ui = desc.Ui ?? new InstanceUi();
        var isCustom = ui.Render != null;

        // Parse the library FRESH (distinct CodeFunction instances each call, so concurrent
        // renderers never share mutable Ids).
        var (_, libUi) = CodeParse.ParseDocument(StdlibSource);
        var library = libUi.Functions ?? [];

        var objectTypes = (desc.Types ?? []).Where(t => t.BaseType == BaseType.Object).ToList();

        // Per-type views only for a generic-UI app; a custom render owns its own routing.
        var synthViews = new List<UiView>();
        if (!isCustom)
        {
            foreach (var type in objectTypes)
            {
                // An object page for every object type. (Dictionaries self-host now, so there is
                // no longer a per-type gate routing some types to the C# auto-form.)
                synthViews.Add(SynthObjectView(type.Name));

                // Reference-route editor per single object-reference prop (any owner — e.g. Db.lead).
                foreach (var prop in RefProps(type, desc))
                    synthViews.Add(SynthRefView(type.Name, prop.Name, prop.Type));

                // Set-table page per object set prop (e.g. Db.notes → /notes).
                foreach (var prop in SetProps(type, desc))
                    synthViews.Add(SynthSetView(type.Name, prop.Name, prop.Type));

                // Dict-table page per dictionary prop (e.g. Db.settings → /settings).
                foreach (var prop in DictProps(type))
                    synthViews.Add(SynthDictView(type.Name, prop.Name));
            }

            // One shared leaf editor for scalar dictionary entry pages (/settings/<key>), added
            // only when the schema has a scalar dictionary (an object dict entry uses its type view).
            if (objectTypes.Any(t => DictProps(t).Any(p => !desc.IsObjectType(p.Type))))
                synthViews.Add(SynthLeafView());

            // The self-hosted NotFound page for any unrouted URL (sets a 404 status).
            synthViews.Add(SynthNotFoundView());
        }

        var functions = new List<CodeFunction>();
        functions.AddRange(library);
        functions.AddRange(ui.Functions ?? []);
        var views = new List<UiView>();
        views.AddRange(ui.Views ?? []);
        views.AddRange(synthViews);

        var effective = ui with { Functions = functions, Views = views };
        // Number every function (library + app + synthesized) deterministically so the
        // server and the shipped client key the memo cache identically.
        CodeIds.Assign(new InstanceDescription(Types: desc.Types, Ui: effective, Common: desc.Common));

        // The framework-synthesized members (the library functions) — the renderer puts these in the
        // library scope, between the system scope and the app scope. Descriptors are resolved by the
        // `sys.schema` builtin from the threaded map (no descriptor var in scope anymore).
        var systemNames = new HashSet<string>(library.Where(f => f.Name != null).Select(f => f.Name!));
        return (effective, systemNames, Descriptors(objectTypes, desc));
    }

    private static IEnumerable<PropDefinition> RefProps(TypeDefinition type, InstanceDescription desc) =>
        (type.Props ?? []).Where(p => p.Cardinality == Cardinality.Single && desc.IsObjectType(p.Type));

    private static IEnumerable<PropDefinition> SetProps(TypeDefinition type, InstanceDescription desc) =>
        (type.Props ?? []).Where(p => p.Cardinality == Cardinality.Set && desc.IsObjectType(p.Type));

    private static IEnumerable<PropDefinition> DictProps(TypeDefinition type) =>
        (type.Props ?? []).Where(p => p.Cardinality == Cardinality.Dictionary);

    // `view T(obj, base)` → `return <ObjectForm obj={obj} meta={sys.schema("T")} base={base}>`.
    // `base` is the page's URL path, threaded so inline sets build nested member links
    // (sys.nest(base, prop)). A TAG (not a call) because ObjectForm is now a COMPONENT — it holds a
    // staged-edit draft (local state) and returns its reactive render — so it must be slot-keyed (its
    // draft survives a re-render) and have its returned render auto-invoked. `autosave` is omitted →
    // it binds to null → falsy → the DEFAULT staged-edit + Save flow (true would be today's live
    // autosave). Keyed by its render-tree slot, bound to the routed object.
    private static UiView SynthObjectView(string typeName) =>
        new(typeName, Fn(["obj", "base"], Return(Tag("ObjectForm",
                ("obj", Sym("obj")), ("meta", Schema(typeName)), ("base", Sym("base"))))));

    // Reference route `view(parent)` → `return <RefEditor parent={parent} prop="P" target={…}>` (a
    // root-position component tag, keyed by its render slot). Keyed by (owner, Prop), bound to the
    // parent. Takes only `parent`: a reference builds no nested links, so it ignores the `base` arg
    // ExecuteRender passes to every type view (both interpreters bind min(args, params), so the
    // extra arg is harmless — keeping the param out of the Code is more honest than declaring it unused).
    private static UiView SynthRefView(string ownerType, string prop, string targetType) =>
        new(ownerType, Fn(["parent"], Return(Tag("RefEditor",
                ("parent", Sym("parent")), ("prop", Text(prop)), ("target", Schema(targetType))))),
            Prop: prop);

    // Set route `view(parent, base)` → `return <SetTable set={parent.P} desc={…} setPath={base}>`.
    // `base` is the set's own URL path, used for nested member links.
    private static UiView SynthSetView(string ownerType, string prop, string elementType) =>
        new(ownerType, Fn(["parent", "base"], Return(Tag("SetTable",
                ("set", Field(Sym("parent"), Text(prop))), ("desc", Schema(elementType)), ("setPath", Sym("base"))))),
            Prop: prop);

    // The shared scalar-entry view: `view(entry, base)` → `return LeafForm(entry, base)`. Bound
    // to the entry object (FindTarget resolves it by key); its value persists path-addressed.
    // LeafForm is stateless (returns tags directly), so it stays a call — no slot identity needed.
    private static UiView SynthLeafView() =>
        new(LeafViewType, Fn(["entry", "base"], Return(Call("LeafForm", Sym("entry"), Sym("base")))));

    // The shared NotFound view: `view() → return NotFoundForm()`. Takes no target — it reads
    // the framework `path` var and sets a 404 status.
    private static UiView SynthNotFoundView() =>
        new(NotFoundViewType, Fn([], Return(Call("NotFoundForm"))));

    // Dict route `view(parent, base)` → `return <DictTable dict={parent.P} desc={__dictDescs["O/P"]}
    // base={base}>` (a root-position component tag, slot-keyed so its draft state survives renders).
    private static UiView SynthDictView(string ownerType, string prop) =>
        new(ownerType, Fn(["parent", "base"], Return(Tag("DictTable",
                ("dict", Field(Sym("parent"), Text(prop))),
                ("desc", Schema(ownerType, prop)),
                ("base", Sym("base"))))),
            Prop: prop);

    // ── the descriptor registry ──────────────────────────────────────────────────────

    // The descriptor literals threaded into the executor for `sys.schema(...)` to evaluate (the
    // replacement for the old `__descs`/`__dictDescs` globals). Two key shapes, both pure data:
    //   "TypeName"      → { name, labelProp, props } — a type's descriptor (sys.schema("T")). (Also
    //                     carries a `blank` template, now UNUSED — sys.new(desc) mints drafts
    //                     reflectively from `props` — but kept so the public descriptor shape is
    //                     unchanged; harmless pure data.)
    //   "Owner/prop"    → that prop's descriptor — sys.schema("O","P"). One entry per prop of every
    //                     object type, so a caller can fetch a single prop's shape by name. The
    //                     generic UI uses this for a dict route; a hand-written `fn render()` uses it
    //                     to compose <Input>/<Field> over a chosen prop (e.g. sys.schema("TodoItem","text")).
    // Built once per render from the schema; the executor evaluates + caches the one a call names and
    // ships it to the client like extent.
    private static Dictionary<string, CodeObject> Descriptors(List<TypeDefinition> objectTypes, InstanceDescription desc)
    {
        var map = objectTypes.ToDictionary(t => t.Name, t => TypeDescriptor(t, desc));
        foreach (var t in objectTypes)
            foreach (var p in t.Props ?? [])
                map[t.Name + "/" + p.Name] = PropDesc(p, desc);
        return map;
    }

    private static CodeObject TypeDescriptor(TypeDefinition t, InstanceDescription desc)
    {
        var scalars = Scalars(t, desc);
        var labelProp = scalars.FirstOrDefault(p => p.Type == "text")?.Name
            ?? scalars.FirstOrDefault()?.Name ?? "";
        return Obj(
            ("name", Text(t.Name)),
            ("labelProp", Text(labelProp)),
            ("props", Arr((t.Props ?? []).Select(p => (ICodeValue)PropDesc(p, desc)))),
            // `blank`: a default-valued draft template. UNUSED since sys.new(desc) builds drafts
            // reflectively from `props`; kept so the public sys.schema descriptor shape is unchanged.
            ("blank", Obj(scalars.Select(p => (p.Name, DefaultFor(p.Type))).ToArray())));
    }

    // A prop descriptor: scalar { name, baseType }; reference { name, baseType:"object",
    // target } and set { name, baseType:"set", element } carry the OTHER type's name (the
    // component resolves it via sys.schema(name) — cycle-safe). A dictionary
    // { name, baseType:"dictionary", keyType, element, isScalar, valueProps } is self-contained
    // (dictTable reads it directly): valueProps are the element's scalar columns (empty for a
    // scalar dict, where isScalar=true and a single "Value" column is shown). sys.new(desc) reads
    // these to mint the New-entry draft — a `value` for a scalar dict (defaulted by `element`), else
    // one field per valueProp. (A `blank` template is also emitted but UNUSED, kept for shape-stability.)
    private static CodeObject PropDesc(PropDefinition p, InstanceDescription desc)
    {
        if (p.Cardinality == Cardinality.Dictionary)
        {
            var isScalar = !desc.IsObjectType(p.Type);
            var valueProps = isScalar ? [] : Scalars(desc.FindType(p.Type)!, desc);
            return Obj(
                ("name", Text(p.Name)),
                ("baseType", Text("dictionary")),
                ("keyType", Text(p.KeyType ?? "text")),
                ("element", Text(p.Type)),
                ("isScalar", new CodeBool { Value = isScalar }),
                ("valueProps", Arr(valueProps.Select(vp => (ICodeValue)PropDesc(vp, desc)))),
                ("blank", isScalar
                    ? Obj(("value", DefaultFor(p.Type)))
                    : Obj(valueProps.Select(vp => (vp.Name, DefaultFor(vp.Type))).ToArray())));
        }
        if (p.Cardinality == Cardinality.Set)
            return Obj(("name", Text(p.Name)), ("baseType", Text("set")), ("element", Text(p.Type)));
        if (desc.IsObjectType(p.Type))
            return Obj(("name", Text(p.Name)), ("baseType", Text("object")), ("target", Text(p.Type)));
        // An enum scalar prop: { name, baseType: "enum", values: [...] } so objectForm renders a
        // <select> of its values (a bare `baseType: <typeName>` would fall through to a text input).
        if (desc.FindType(p.Type) is { BaseType: BaseType.Enum, Values: { } values })
            return Obj(("name", Text(p.Name)), ("baseType", Text("enum")),
                ("values", Arr(values.Select(v => (ICodeValue)Text(v)))));
        return Obj(("name", Text(p.Name)), ("baseType", Text(p.Type)));
    }

    private static ICodeValue DefaultFor(string baseType) => baseType switch
    {
        "bool" => new CodeBool { Value = false },
        "int" => new CodeInt { Value = 0 },
        _ => Text(""),
    };

    // Scalar (leaf-valued) props for the blank-draft template and the table columns: base
    // leaves and enums (an enum value is text-shaped). References/sets/dicts are excluded.
    private static List<PropDefinition> Scalars(TypeDefinition t, InstanceDescription desc) => (t.Props ?? [])
        .Where(p => p.Cardinality == Cardinality.Single && (BaseTypes.IsName(p.Type) || desc.IsEnumType(p.Type)))
        .ToList();

    // ── tiny AST builders ───────────────────────────────────────────────────────────

    private static CodeText Text(string v) => new() { Value = v };
    private static CodeSymbol Sym(string n) => new() { Name = n };
    private static CodeObject Obj(params (string Name, ICodeValue Value)[] props) =>
        new() { Props = props.Select(p => new CodeObjectProp { Name = p.Name, Value = p.Value }).ToArray() };
    private static CodeArray Arr(IEnumerable<ICodeValue> items) => new() { Items = items.ToArray() };
    private static CodeCall Call(string fn, params ICodeValue[] args) => new() { Fn = Sym(fn), Params = args };
    // The builtin `field` is namespaced under `sys` — its callee is `sys.field` (a member access
    // on the `sys` symbol), the shape both interpreters dispatch on.
    private static CodeCall Field(ICodeValue obj, ICodeValue name) => new() { Fn = SysMember("field"), Params = [obj, name] };
    private static CodeInfixOp SysMember(string name) =>
        new() { Op = CodeInfixOpType.ObjectProp, Left = Sym("sys"), Right = Sym(name) };
    // `sys.schema("T")` — the descriptor for type T, resolved server-side from the schema (the
    // replacement for the old `sys.field(__descs, "T")` registry read). Mirrors the Field/Call helpers.
    private static CodeCall Schema(string typeName) => new() { Fn = SysMember("schema"), Params = [Text(typeName)] };
    // `sys.schema("Owner", "prop")` — the descriptor of a specific PROP of a type (e.g. a dictionary
    // prop at its root route), the replacement for the old `__dictDescs["Owner/prop"]` registry read.
    private static CodeCall Schema(string typeName, string prop) =>
        new() { Fn = SysMember("schema"), Params = [Text(typeName), Text(prop)] };
    // A childless tag `<Name a={…} b={…}>` — used to invoke a synthesized component (RefEditor /
    // SetTable / DictTable) BY TAG, so it keys on its render-tree slot rather than its arguments.
    private static CodeTag Tag(string name, params (string Name, ICodeValue Value)[] attrs) => new()
    {
        Name = name,
        Attributes = attrs.Select(a => new CodeTagAttribute { Name = a.Name, Value = a.Value }).ToArray(),
        Children = [],
    };
    private static CodeBlock Return(ICodeValue value) => new() { Statements = [new CodeReturn { Value = value }] };
    private static CodeFunction Fn(string[] @params, CodeBlock body) =>
        new() { Name = null, Params = @params.Select(p => new CodeFunctionParam { Name = p }).ToArray(), Body = body };
}
