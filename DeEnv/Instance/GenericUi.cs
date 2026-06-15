using DeEnv.Code;

namespace DeEnv.Instance;

// The self-hosted generic UI (milestone 9), a reflective Code library over schema data,
// synthesized into the effective ui at render time. It is the DEFAULT UI: any app without a
// fully-custom `fn render()` (including an app with no `ui` section) renders through it.
//
//   • objectForm(obj, meta, base) — an object page: a field per prop (scalar input, a nested
//     refEditor for a single object reference, an inline setTable for an object set, or an
//     inline dictTable for a dictionary). A collection's label is a navigable list-title link
//     to its own route. `base` is the page's URL path, so inline links nest. Edits autosave.
//   • refEditor(parent, prop, target) — a reference editor: current label, a pick button
//     per extent() candidate, a clear button, and a create-new form. A COMPONENT: its body
//     runs once as init (a local `state` holding a draft), and it returns a render fn.
//   • setTable(set, desc, setPath) — a set table: header + member rows (+ an "open" link to
//     the nested member URL, nest(setPath, m) → /notes/3, + Remove) + an add form. Also a
//     COMPONENT (the add form holds a draft).
//
// Builtins do the reflective work, all under the framework `sys` namespace: sys.field (dynamic
// access), sys.humanize (labels), sys.extent (a type's objects), sys.setRef (set/clear a
// reference), sys.nest (a URL path-join for nested member links), sys.clone (a fresh draft from
// a blank template). `obj.prop = x` resets a component's draft after Create.
//
// Descriptors are a single stable top-scope registry var `__descs` (typeName → descriptor),
// synthesized once — so the component functions receive a stable descriptor argument every
// render, which is what lets the memo cache run their init exactly once (the same pattern
// hand-authored forms use; no separate synthesized draft vars). Cross-type references are
// stored by type NAME (cycle-safe); a component resolves them with sys.field(__descs, name).
//
// Synthesis is render-time only: the canonical InstanceDescription (what AppPrint emits)
// carries no UI at all for a plain app. A synthesized OBJECT view (Type=T, Prop=null) binds the
// routed object; a REFERENCE / SET view (Type=O, Prop=P) binds the parent object that owns
// the prop — both reuse the M8 type-view client binding (no new wire shape).
public static class GenericUi
{
    private const string DescsVar = "__descs";
    private const string DictDescsVar = "__dictDescs";

    // The reserved view "type" for the shared scalar leaf editor (a scalar dictionary
    // entry page). SsrRenderer.ResolveView dispatches a scalar dict entry to it.
    public const string LeafViewType = "__leaf";

    // The reserved view "type" for the self-hosted NotFound page (an unrouted URL).
    public const string NotFoundViewType = "__notFound";

    private const string StdlibSource = """
        ui
            fn objectForm(obj, meta, base)
                return <div class="object-form">
                    <h2>
                        meta.name
                    foreach p in meta.props
                        <div class="field">
                            if p.baseType == "set" || p.baseType == "dictionary"
                                <a class="list-title" href={sys.nest(base, p.name)}>
                                    sys.humanize(p.name)
                            else
                                <label class={p.name}>
                                    sys.humanize(p.name)
                            if p.baseType == "object"
                                refEditor(obj, p.name, sys.field(__descs, p.target))()
                            else if p.baseType == "set"
                                setTable(sys.field(obj, p.name), sys.field(__descs, p.element), sys.nest(base, p.name))()
                            else if p.baseType == "dictionary"
                                dictTable(sys.field(obj, p.name), p, sys.nest(base, p.name))()
                            else if p.baseType == "bool"
                                <input type="checkbox" class={p.name} checked={sys.field(obj, p.name)}>
                            else
                                <input type={inputType(p.baseType)} class={p.name} value={sys.field(obj, p.name)}>

            fn refEditor(parent, prop, target)
                var state = { draft: sys.clone(target.blank) }
                fn createNew()
                    sys.setRef(parent, prop, state.draft)
                    state.draft = sys.clone(target.blank)
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
                        foreach c in sys.extent(target.name)
                            <button class="ref-pick" onClick={() => sys.setRef(parent, prop, c)}>
                                sys.field(c, target.labelProp)
                        <button class="ref-clear" onClick={() => sys.setRef(parent, prop, null)}>
                            "Clear"
                        <div class="ref-new">
                            foreach p in target.props
                                if p.baseType != "object" && p.baseType != "set"
                                    <label class={p.name}>
                                        sys.humanize(p.name)
                                    if p.baseType == "bool"
                                        <input type="checkbox" class={p.name} checked={sys.field(state.draft, p.name)}>
                                    else
                                        <input type={inputType(p.baseType)} class={p.name} value={sys.field(state.draft, p.name)}>
                            <button class="ref-create" onClick={createNew}>
                                "Create new"
                return render

            fn setTable(set, desc, setPath)
                var state = { draft: sys.clone(desc.blank) }
                fn addNew()
                    set.add(state.draft)
                    state.draft = sys.clone(desc.blank)
                fn render()
                    return <div class="set-table">
                        <table>
                            <tr class="set-head">
                                foreach p in desc.props
                                    if p.baseType != "object" && p.baseType != "set"
                                        <th>
                                            sys.humanize(p.name)
                            foreach m in set
                                <tr class="set-row">
                                    foreach p in desc.props
                                        if p.baseType != "object" && p.baseType != "set"
                                            <td>
                                                sys.field(m, p.name)
                                    <td>
                                        <a class="set-open" href={sys.nest(setPath, m)}>
                                            "open"
                                    <td>
                                        <button class="set-remove" onClick={() => set.remove(m)}>
                                            "Remove"
                        <div class="set-new">
                            foreach p in desc.props
                                if p.baseType != "object" && p.baseType != "set"
                                    if p.baseType == "bool"
                                        <input type="checkbox" class={p.name} checked={sys.field(state.draft, p.name)}>
                                    else
                                        <input type={inputType(p.baseType)} class={p.name} value={sys.field(state.draft, p.name)}>
                            <button class="set-add" onClick={addNew}>
                                "Add"
                return render

            fn dictTable(dict, desc, base)
                var state = { key: "", draft: sys.clone(desc.blank), error: "" }
                fn addNew()
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
                        state.draft = sys.clone(desc.blank)
                        state.error = ""
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
                            foreach m in dict
                                <tr class="dict-row">
                                    <td>
                                        <a class="dict-open" href={sys.nest(base, sys.field(m, "__key"))}>
                                            sys.field(m, "__key")
                                    foreach p in desc.valueProps
                                        <td>
                                            sys.field(m, p.name)
                                    if desc.isScalar
                                        <td>
                                            sys.field(m, "value")
                                    <td>
                                        <button class="dict-remove" onClick={() => dict.remove(m)}>
                                            "Remove"
                        <div class="dict-new">
                            <input class="dict-key" value={state.key}>
                            foreach p in desc.valueProps
                                <input type={inputType(p.baseType)} class={p.name} value={sys.field(state.draft, p.name)}>
                            if desc.isScalar
                                <input class="value" value={sys.field(state.draft, "value")}>
                            <button class="dict-add" onClick={addNew}>
                                "Add"
                            <div class="dict-error">
                                state.error
                return render

            fn leafForm(entry, base)
                return <div class="leaf-form">
                    <h2>
                        sys.field(entry, "__key")
                    <input class="value" value={sys.field(entry, "value")}>

            fn notFoundForm()
                status = 404
                return <main class="not-found">
                    <h1>
                        "Not found"
                    <p class="missing">
                        path
                    <a class="home" href="/">
                        "Home"

            fn inputType(baseType)
                if baseType == "int"
                    return "number"
                if baseType == "decimal"
                    return "number"
                if baseType == "date"
                    return "date"
                return "text"
        """;

    // The effective ui for rendering: the app's ui augmented with the generic library, the
    // stable `__descs` descriptor registry, an OBJECT view per self-hostable type, and a
    // REFERENCE / SET view per object reference / set prop. Returns the app's ui unchanged
    // when it does not opt in. Functions are renumbered (CodeIds) over the whole set so
    // server and client key the memo cache alike.
    //
    // SystemNames lists the synthesized framework members (the library functions + the
    // descriptor registries) so the renderer places them in the SYSTEM scope, above the
    // custom code — they never pollute the app scope.
    public static (InstanceUi? Ui, IReadOnlySet<string> SystemNames) Effective(InstanceDescription desc)
    {
        var ui = desc.Ui;
        // A fully-custom UI (`fn render()`) owns the whole URL space — no generic synthesis.
        if (ui?.Render != null) return (ui, EmptyNames);
        // Otherwise the self-hosted generic UI is the DEFAULT: synthesize per-type views over
        // the (possibly absent) ui section. A plain app — no `ui` section, or only common
        // helpers — renders entirely through the Code objectForm library.
        ui ??= new InstanceUi();

        // Parse the library FRESH (distinct CodeFunction instances each call, so concurrent
        // renderers never share mutable Ids).
        var (_, libUi) = CodeParse.ParseDocument(StdlibSource);
        var library = libUi.Functions ?? [];

        var objectTypes = (desc.Types ?? []).Where(t => t.BaseType == BaseType.Object).ToList();

        var synthViews = new List<UiView>();
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

        var vars = new List<UiVar>();
        vars.AddRange(ui.Vars ?? []);
        vars.Add(new UiVar(DescsVar, Registry(objectTypes, desc)));        // stable type-descriptor registry
        vars.Add(new UiVar(DictDescsVar, DictRegistry(objectTypes, desc))); // stable dict prop-descriptor registry
        var functions = new List<CodeFunction>();
        functions.AddRange(library);
        functions.AddRange(ui.Functions ?? []);
        var views = new List<UiView>();
        views.AddRange(ui.Views ?? []);
        views.AddRange(synthViews);

        var effective = ui with { Vars = vars, Functions = functions, Views = views };
        // Number every function (library + app + synthesized) deterministically so the
        // server and the shipped client key the memo cache identically.
        CodeIds.Assign(new InstanceDescription(Types: desc.Types, Ui: effective, Common: desc.Common));

        // The framework-synthesized members (library functions + descriptor registries) — the
        // renderer puts these in the system scope, leaving the app scope to the user's code.
        var systemNames = new HashSet<string>(library.Where(f => f.Name != null).Select(f => f.Name!))
            { DescsVar, DictDescsVar };
        return (effective, systemNames);
    }

    private static readonly IReadOnlySet<string> EmptyNames = new HashSet<string>();

    private static IEnumerable<PropDefinition> RefProps(TypeDefinition type, InstanceDescription desc) =>
        (type.Props ?? []).Where(p => p.Cardinality == Cardinality.Single && desc.IsObjectType(p.Type));

    private static IEnumerable<PropDefinition> SetProps(TypeDefinition type, InstanceDescription desc) =>
        (type.Props ?? []).Where(p => p.Cardinality == Cardinality.Set && desc.IsObjectType(p.Type));

    private static IEnumerable<PropDefinition> DictProps(TypeDefinition type) =>
        (type.Props ?? []).Where(p => p.Cardinality == Cardinality.Dictionary);

    // `view T(obj, base)` → `return objectForm(obj, sys.field(__descs, "T"), base)`. `base` is the
    // page's URL path, threaded so inline sets build nested member links (sys.nest(base, prop)).
    private static UiView SynthObjectView(string typeName) =>
        new(typeName, Fn(["obj", "base"], Return(Call("objectForm", Sym("obj"), Desc(typeName), Sym("base")))));

    // Reference route `view(parent)` → `return refEditor(parent, "P", sys.field(__descs, "T"))()`
    // (the component is invoked). Keyed by (owner, Prop), bound to the parent. Takes only
    // `parent`: a reference builds no nested links, so it ignores the `base` arg ExecuteRender
    // passes to every type view (both interpreters bind min(args, params), so the extra arg is
    // harmless — keeping the param out of the Code is more honest than declaring it unused).
    private static UiView SynthRefView(string ownerType, string prop, string targetType) =>
        new(ownerType, Fn(["parent"], Return(Invoke(Call("refEditor", Sym("parent"), Text(prop), Desc(targetType))))),
            Prop: prop);

    // Set route `view(parent, base)` → `return setTable(sys.field(parent, "P"), sys.field(__descs,
    // "T"), base)()`. `base` is the set's own URL path, used for nested member links.
    private static UiView SynthSetView(string ownerType, string prop, string elementType) =>
        new(ownerType, Fn(["parent", "base"], Return(Invoke(Call("setTable", Field(Sym("parent"), Text(prop)), Desc(elementType), Sym("base"))))),
            Prop: prop);

    // Dict route `view(parent, base)` → `return dictTable(sys.field(parent, "P"), __dictDescs["O/P"],
    // base)()`. The prop descriptor comes from the STABLE __dictDescs registry (not a literal),
    // so the dictTable component's memoized init runs once and its draft state survives renders.
    // The shared scalar-entry view: `view(entry, base)` → `return leafForm(entry, base)`. Bound
    // to the entry object (FindTarget resolves it by key); its value persists path-addressed.
    private static UiView SynthLeafView() =>
        new(LeafViewType, Fn(["entry", "base"], Return(Call("leafForm", Sym("entry"), Sym("base")))));

    // The shared NotFound view: `view() → return notFoundForm()`. Takes no target — it reads
    // the framework `path` var and sets a 404 status.
    private static UiView SynthNotFoundView() =>
        new(NotFoundViewType, Fn([], Return(Call("notFoundForm"))));

    private static UiView SynthDictView(string ownerType, string prop) =>
        new(ownerType, Fn(["parent", "base"], Return(Invoke(Call("dictTable",
                Field(Sym("parent"), Text(prop)),
                Field(Sym(DictDescsVar), Text(ownerType + "/" + prop)),
                Sym("base"))))),
            Prop: prop);

    // ── the descriptor registry ──────────────────────────────────────────────────────

    // { TypeName: { name, labelProp, props, blank }, … } — one stable top-scope object.
    private static CodeObject Registry(List<TypeDefinition> objectTypes, InstanceDescription desc) =>
        new() { Props = objectTypes
            .Select(t => new CodeObjectProp { Name = t.Name, Value = TypeDescriptor(t, desc) })
            .ToArray() };

    // { "Owner/prop": <dict prop descriptor>, … } — a stable top-scope object so a dict-route
    // view hands dictTable the SAME descriptor every render (memo init-once for its add form).
    private static CodeObject DictRegistry(List<TypeDefinition> objectTypes, InstanceDescription desc) =>
        new() { Props = objectTypes
            .SelectMany(t => (t.Props ?? [])
                .Where(p => p.Cardinality == Cardinality.Dictionary)
                .Select(p => new CodeObjectProp { Name = t.Name + "/" + p.Name, Value = PropDesc(p, desc) }))
            .ToArray() };

    private static CodeObject TypeDescriptor(TypeDefinition t, InstanceDescription desc)
    {
        var scalars = Scalars(t);
        var labelProp = scalars.FirstOrDefault(p => p.Type == "text")?.Name
            ?? scalars.FirstOrDefault()?.Name ?? "";
        return Obj(
            ("name", Text(t.Name)),
            ("labelProp", Text(labelProp)),
            ("props", Arr((t.Props ?? []).Select(p => (ICodeValue)PropDesc(p, desc)))),
            ("blank", Obj(scalars.Select(p => (p.Name, DefaultFor(p.Type))).ToArray())));
    }

    // A prop descriptor: scalar { name, baseType }; reference { name, baseType:"object",
    // target } and set { name, baseType:"set", element } carry the OTHER type's name (the
    // component resolves it via field(__descs, name) — cycle-safe). A dictionary
    // { name, baseType:"dictionary", keyType, isScalar, valueProps, blank } is self-contained
    // (dictTable reads it directly): valueProps are the element's scalar columns (empty for a
    // scalar dict, where isScalar=true and a single "Value" column is shown), blank seeds the
    // New-entry draft.
    private static CodeObject PropDesc(PropDefinition p, InstanceDescription desc)
    {
        if (p.Cardinality == Cardinality.Dictionary)
        {
            var isScalar = !desc.IsObjectType(p.Type);
            var valueProps = isScalar ? [] : Scalars(desc.FindType(p.Type)!);
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
        return p.Cardinality == Cardinality.Set
            ? Obj(("name", Text(p.Name)), ("baseType", Text("set")), ("element", Text(p.Type)))
            : desc.IsObjectType(p.Type)
                ? Obj(("name", Text(p.Name)), ("baseType", Text("object")), ("target", Text(p.Type)))
                : Obj(("name", Text(p.Name)), ("baseType", Text(p.Type)));
    }

    private static ICodeValue DefaultFor(string baseType) => baseType switch
    {
        "bool" => new CodeBool { Value = false },
        "int" => new CodeInt { Value = 0 },
        _ => Text(""),
    };

    private static List<PropDefinition> Scalars(TypeDefinition t) => (t.Props ?? [])
        .Where(p => p.Cardinality == Cardinality.Single && BaseTypes.IsName(p.Type)).ToList();

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
    private static CodeCall Desc(string typeName) => Field(Sym(DescsVar), Text(typeName));
    private static CodeCall Invoke(ICodeValue fn) => new() { Fn = fn, Params = [] };
    private static CodeBlock Return(ICodeValue value) => new() { Statements = [new CodeReturn { Value = value }] };
    private static CodeFunction Fn(string[] @params, CodeBlock body) =>
        new() { Name = null, Params = @params.Select(p => new CodeFunctionParam { Name = p }).ToArray(), Body = body };
}
