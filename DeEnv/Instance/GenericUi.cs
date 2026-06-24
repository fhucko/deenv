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
//     its own route. `base` is the page's URL path, so inline links nest. A COMPONENT: it opens a
//     staging data-context — `ambient ctx = ctx.new(autosave)` — and binds Fields to the LIVE `obj`
//     directly; the ctx decides whether a write stages or persists. `autosave` (bool) controls that:
//     omitted/false (the DEFAULT, what the synthesized object view passes) makes `ctx.new` a STAGING
//     child, so scalar edits stage in the overlay (the stored object is untouched) and commit on a
//     Save button (`ctx.commit()`); Discard drops the overlay (`ctx.discard()`) and the inputs re-read
//     the stored value. true makes `ctx.new(true)` the live parent → per-keystroke autosave, no
//     buttons. Either way COLLECTION props (reference/set/dictionary) bind to the LIVE object, and a
//     nested create-form's draft (transient, negative id) writes live — never staged in this form's
//     ctx (each manages its own members, which persist on their own).
//   • RefEditor(parent, prop, target) — a reference editor: current label, a pick button
//     per extent() candidate, a clear button, and a create-new form. A COMPONENT: its body
//     runs once as init (a local `state` holding a draft), and it returns a render fn.
//   • SetTable(set, desc, setPath, columns, rowActions, createForm) — a set table: an aligned header +
//     member rows + a `+ New` button. A whole data row is navigable — its first cell wraps the member's
//     identity (labelProp value) in a stretched `<a class="row-link" href=nest(setPath, m)>` (CSS
//     `::after { inset:0 }` covers the row); a per-row Remove sits z-raised above the overlay. A bool
//     column renders a read-only ✓/✗ glyph, never "true"/"false". Shared structure with DictTable
//     (row-link + Remove + bool cell + the `+ New`/create-form pattern), differing only in the identity
//     source. A COMPONENT: the read-only table ALWAYS renders; a local `state.creating` flag toggles what
//     sits BELOW it — the `+ New` button (default) or a labeled CREATE FORM with Save / Cancel. So the
//     existing list stays visible while you add a member (a tracker keeps the list in view while
//     appending); the create form is a separate card under the table, hidden until asked — NOT an inline
//     add row (tables stay read-only; edits happen on the member page). Save commits the draft (`set.add`)
//     and closes the form; Cancel discards it; both reset `state.draft`. The last three params shape the table (all optional, default
//     to the plain data-table behavior):
//       · columns — the column names to show (omitted/null → labelProp + every scalar prop).
//       · rowActions(m) — a per-row action cell; when given, it REPLACES the default Remove cell AND
//         marks the table `managed`, which suppresses the whole-row click overlay (the stretched
//         a.row-link::after) so the consumer's action buttons are not under a click-stealing layer — the
//         label stays an in-cell nav link. Omitted/null → the default Remove cell + whole-row overlay
//         (the data-table path, byte-for-byte unchanged).
//       · createForm(draft) — the body of the create form: a function returning the fields to edit on the
//         create draft, rendered INSIDE the create-form card in place of the default per-scalar `Field`
//         form (the Save/Cancel + set.add flow stays around it). Omitted/null → the default all-scalars
//         form (one `Field` per scalar prop), byte-for-byte unchanged. A consumer passes it to show a
//         FOCUSED create (the designer's designs list shows just a label field, not Design's raw
//         ui/common/initialData scalars) while still using the generic New + set.add.
//
// Builtins do the reflective work, all under the framework `sys` namespace: sys.field (dynamic
// access), sys.humanize (labels), sys.extent (a type's objects), sys.schema (a type's descriptor),
// sys.setRef (set/clear a reference), sys.nest (a URL path-join for nested member links), sys.new (a
// fresh default-valued object built from a descriptor — a create-new draft). `obj.prop = x` resets a component's draft after Create.
//
// A type's descriptor — { name, labelProp, props } — is fetched by
// `sys.schema(typeName)`,
// resolved server-side from the schema (the descriptor literal GenericUi threads into the executor)
// and shipped to the client like extent. Components are tag-invoked and slot-keyed, so a descriptor
// is now plain argument data (its identity carries no reactivity). Cross-type references are stored
// by type NAME (cycle-safe); a component resolves them with sys.schema(name).
//
// Synthesis is render-time only: the canonical InstanceDescription (what AppPrint emits) carries no
// UI at all for a plain app. For a generic-UI app the framework synthesizes a single `fn render()`
// (below) that routes every URL by calling `sys.resolve(path)` — the Code twin of the old C#
// per-URL dispatch — and composing the library component for the resolved kind. There are no
// per-type views anymore: every page runs through this one render, exactly as a custom render does.
public static class GenericUi
{
    private const string StdlibSource = """
        ui
            fn render()
                var r = sys.resolve(path)
                if r.kind == "object" && r.target != null
                    return <ObjectForm obj={r.target} meta={sys.schema(r.typeName)} base={path}>
                else if r.kind == "set"
                    return <SetTable set={sys.field(r.parent, r.prop)} desc={sys.schema(r.typeName)} setPath={path}>
                else if r.kind == "ref"
                    return <RefEditor parent={r.parent} prop={r.prop} target={sys.schema(r.typeName)}>
                else if r.kind == "dict"
                    return <DictTable dict={sys.field(r.parent, r.prop)} desc={sys.schema(r.parentType, r.prop)} base={path}>
                else if r.kind == "leaf" && r.target != null
                    return <LeafForm entry={r.target} base={path}>
                else
                    status = 404
                    return NotFoundForm()

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
                ambient ctx = ctx.new(autosave)
                fn save()
                    ctx.commit()
                fn discard()
                    ctx.discard()
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
                            else
                                <Field obj={obj} desc={p}>
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

            fn SetTable(set, desc, setPath, columns, rowActions, createForm)
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
                    var tableClass = "set-table"
                    if rowActions != null
                        tableClass = "set-table managed"
                    return <div class={tableClass}>
                        <table>
                            <tr class="set-head">
                                if columns != null
                                    foreach name in columns
                                        foreach p in desc.props.where(c => c.name == name)
                                            <th>
                                                sys.humanize(p.name)
                                else
                                    <th>
                                        sys.humanize(desc.labelProp)
                                    foreach p in desc.props
                                        if p.baseType != "set" && p.baseType != "dictionary" && p.name != desc.labelProp
                                            <th>
                                                sys.humanize(p.name)
                                <th>
                            foreach m in set
                                <tr class="set-row">
                                    if columns != null
                                        foreach name in columns
                                            foreach p in desc.props.where(c => c.name == name)
                                                if p.name == desc.labelProp
                                                    <td class="row-id">
                                                        <a class="row-link" href={sys.nest(setPath, m)}>
                                                            sys.field(m, p.name)
                                                else
                                                    <td>
                                                        if p.baseType == "bool"
                                                            <span class="bool-cell">
                                                                boolGlyph(sys.field(m, p.name))
                                                        else if p.baseType == "object"
                                                            if sys.field(m, p.name) != null
                                                                sys.field(sys.field(m, p.name), sys.schema(p.target).labelProp)
                                                        else if p.baseType == "enum"
                                                            sys.humanize(sys.field(m, p.name))
                                                        else
                                                            sys.field(m, p.name)
                                    else
                                        <td class="row-id">
                                            <a class="row-link" href={sys.nest(setPath, m)}>
                                                sys.field(m, desc.labelProp)
                                        foreach p in desc.props
                                            if p.baseType != "set" && p.baseType != "dictionary" && p.name != desc.labelProp
                                                <td>
                                                    if p.baseType == "bool"
                                                        <span class="bool-cell">
                                                            boolGlyph(sys.field(m, p.name))
                                                    else if p.baseType == "object"
                                                        if sys.field(m, p.name) != null
                                                            sys.field(sys.field(m, p.name), sys.schema(p.target).labelProp)
                                                    else if p.baseType == "enum"
                                                        sys.humanize(sys.field(m, p.name))
                                                    else
                                                        sys.field(m, p.name)
                                    if rowActions != null
                                        rowActions(m)
                                    else
                                        <td class="row-action">
                                            <button class="set-remove" onClick={() => set.remove(m)}>
                                                "Remove"
                        if state.creating
                            <div class="create-form">
                                <h3>
                                    "New "
                                    sys.humanize(desc.name)
                                if createForm != null
                                    createForm(state.draft)
                                else
                                    foreach p in desc.props
                                        if p.baseType != "object" && p.baseType != "set" && p.baseType != "dictionary"
                                            <Field obj={state.draft} desc={p}>
                                <div class="create-actions">
                                    <button class="set-add" onClick={save}>
                                        "Save"
                                    <button class="cancel" onClick={cancel}>
                                        "Cancel"
                        else
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
                                            else if p.baseType == "enum"
                                                sys.humanize(sys.field(m, p.name))
                                            else
                                                sys.field(m, p.name)
                                    if desc.isScalar
                                        <td>
                                            sys.field(m, "value")
                                    <td class="row-action">
                                        <button class="dict-remove" onClick={() => dict.remove(m)}>
                                            "Remove"
                        if state.creating
                            <div class="create-form">
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
                        else
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

    // The effective ui for rendering: the app's ui augmented with the generic library (ALWAYS), plus —
    // when the app has no custom `fn render()` — a single synthesized generic `fn render()` that routes
    // every URL itself by composing the library (the DEFAULT UI). Functions are renumbered (CodeIds)
    // over the whole set so server and client key the memo cache alike.
    //
    // The library is PUBLIC: a custom `fn render()` reaches it through the scope chain (the renderer
    // parents the app scope under the library scope), so it can compose <ObjectForm> &c. — hence the
    // library functions + descriptors are synthesized for EVERY app, custom or not. The synthesized
    // generic render is added ONLY for a generic-UI app (no custom render); a custom app owns its own
    // routing.
    //
    // The generic render IS the collapse of the old per-URL C# dispatch (SsrRenderer.ResolveView +
    // the Synth*View builders): instead of synthesizing one anonymous view per type/prop and a C#
    // cardinality-walk to pick one, a single render calls `sys.resolve(path)` (the Code twin of that
    // walk) and switches on the resolved kind, binding the SAME library components the views used to.
    // So every page is now the custom-render path — the client always runs `render` with no ViewInfo.
    //
    // SystemNames lists the synthesized framework members (the library functions + the generic render)
    // so the renderer places them in the LIBRARY scope, between the system scope and the app scope.
    // IsGeneric is true when the synthesized generic render is in play (no custom render): the renderer
    // runs the render in the lib scope and keeps the generic breadcrumb/title chrome. Descriptors maps
    // "TypeName" → a type's descriptor literal and "Owner/prop" → a dict prop's descriptor, threaded
    // into the executor so `sys.schema(...)` resolves a shape.
    public static (InstanceUi? Ui, IReadOnlySet<string> SystemNames, bool IsGeneric,
        IReadOnlyDictionary<string, CodeObject> Descriptors) Effective(InstanceDescription desc)
    {
        // A custom `fn render()` owns the whole URL space, so it gets the library but no generic
        // render. A plain app — no `ui` section, or only common helpers — renders entirely through
        // the synthesized generic render over the Code ObjectForm library (the DEFAULT UI).
        var ui = desc.Ui ?? new InstanceUi();
        var isCustom = ui.Render != null;

        // Parse the library FRESH (distinct CodeFunction instances each call, so concurrent
        // renderers never share mutable Ids). StdlibSource defines the library functions plus a
        // `fn render()` (the generic router) — MapUi pulls the latter into libUi.Render.
        var (_, libUi) = CodeParse.ParseDocument(StdlibSource);
        var library = libUi.Functions ?? [];
        var genericRender = libUi.Render
            ?? throw new InvalidOperationException("The generic library must define `fn render()`.");

        var objectTypes = (desc.Types ?? []).Where(t => t.BaseType == BaseType.Object).ToList();

        // The render: the app's own when custom; otherwise the synthesized generic router (which
        // composes the library to route every URL via sys.resolve).
        var render = isCustom ? ui.Render : genericRender;

        var functions = new List<CodeFunction>();
        functions.AddRange(library);
        functions.AddRange(ui.Functions ?? []);

        var effective = ui with { Functions = functions, Render = render };
        // Number every function (library + app + the render) deterministically so the
        // server and the shipped client key the memo cache identically.
        CodeIds.Assign(new InstanceDescription(Types: desc.Types, Ui: effective, Common: desc.Common));

        // The framework-synthesized members — the renderer puts these in the library scope, between
        // the system scope and the app scope. The library functions are always synthesized; the
        // generic render joins them only when it is in play (a generic app), so it runs in the lib
        // scope and resolves the library components by name. Descriptors are resolved by `sys.schema`.
        var systemNames = new HashSet<string>(library.Where(f => f.Name != null).Select(f => f.Name!));
        return (effective, systemNames, IsGeneric: !isCustom, Descriptors(objectTypes, desc));
    }

    // ── the descriptor registry ──────────────────────────────────────────────────────

    // The descriptor literals threaded into the executor for `sys.schema(...)` to evaluate (the
    // replacement for the old `__descs`/`__dictDescs` globals). Two key shapes, both pure data:
    //   "TypeName"      → { name, labelProp, props } — a type's descriptor (sys.schema("T")).
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
            ("props", Arr((t.Props ?? []).Select(p => (ICodeValue)PropDesc(p, desc)))));
    }

    // A prop descriptor: scalar { name, baseType }; reference { name, baseType:"object",
    // target } and set { name, baseType:"set", element } carry the OTHER type's name (the
    // component resolves it via sys.schema(name) — cycle-safe). A dictionary
    // { name, baseType:"dictionary", keyType, element, isScalar, valueProps } is self-contained
    // (dictTable reads it directly): valueProps are the element's scalar columns (empty for a
    // scalar dict, where isScalar=true and a single "Value" column is shown). sys.new(desc) reads
    // these to mint the New-entry draft — a `value` for a scalar dict (defaulted by `element`), else
    // one field per valueProp.
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
                ("valueProps", Arr(valueProps.Select(vp => (ICodeValue)PropDesc(vp, desc)))));
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

    // Scalar (leaf-valued) props for the label prop and the table columns: base
    // leaves and enums (an enum value is text-shaped). References/sets/dicts are excluded.
    private static List<PropDefinition> Scalars(TypeDefinition t, InstanceDescription desc) => (t.Props ?? [])
        .Where(p => p.Cardinality == Cardinality.Single && (BaseTypes.IsName(p.Type) || desc.IsEnumType(p.Type)))
        .ToList();

    // ── tiny AST builders (the descriptor literals only) ──────────────────────────────
    // The generic UI's per-URL views are now a single synthesized `fn render()` written in
    // StdlibSource, so the only ASTs built in C# are the pure-data type/prop descriptors below.

    private static CodeText Text(string v) => new() { Value = v };
    private static CodeObject Obj(params (string Name, ICodeValue Value)[] props) =>
        new() { Props = props.Select(p => new CodeObjectProp { Name = p.Name, Value = p.Value }).ToArray() };
    private static CodeArray Arr(IEnumerable<ICodeValue> items) => new() { Items = items.ToArray() };
}
