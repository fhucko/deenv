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
// access), sys.humanize (labels), sys.extent (a type's objects), sys.schema (a type's descriptor),
// sys.setRef (set/clear a reference), sys.nest (a URL path-join for nested member links), sys.clone
// (a fresh draft from a blank template). `obj.prop = x` resets a component's draft after Create.
//
// A type's descriptor — { name, labelProp, props, blank } — is fetched by `sys.schema(typeName)`,
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
                                <refEditor parent={obj} prop={p.name} target={sys.schema(p.target)}>
                            else if p.baseType == "set"
                                <setTable set={sys.field(obj, p.name)} desc={sys.schema(p.element)} setPath={sys.nest(base, p.name)}>
                            else if p.baseType == "dictionary"
                                <dictTable dict={sys.field(obj, p.name)} desc={p} base={sys.nest(base, p.name)}>
                            else if p.baseType == "bool"
                                <input type="checkbox" class={p.name} checked={sys.field(obj, p.name)}>
                            else if p.baseType == "enum"
                                <select class={p.name} value={sys.field(obj, p.name)}>
                                    <option value="">
                                        "(none)"
                                    foreach v in p.values
                                        <option value={v}>
                                            sys.humanize(v)
                            else
                                <input type={inputType(p.baseType)} class={p.name} value={sys.field(obj, p.name)}>

            fn refEditor(parent, prop, target)
                var state = { pick: 0, draft: sys.clone(target.blank) }
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
                                    if p.baseType == "bool"
                                        <input type="checkbox" class={p.name} checked={sys.field(state.draft, p.name)}>
                                    else if p.baseType == "enum"
                                        <select class={p.name} value={sys.field(state.draft, p.name)}>
                                            <option value="">
                                                "(none)"
                                            foreach v in p.values
                                                <option value={v}>
                                                    sys.humanize(v)
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
                                    else if p.baseType == "enum"
                                        <select class={p.name} value={sys.field(state.draft, p.name)}>
                                            <option value="">
                                                "(none)"
                                            foreach v in p.values
                                                <option value={v}>
                                                    sys.humanize(v)
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
                                if p.baseType == "enum"
                                    <select class={p.name} value={sys.field(state.draft, p.name)}>
                                        <option value="">
                                            "(none)"
                                        foreach v in p.values
                                            <option value={v}>
                                                sys.humanize(v)
                                else
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

    // The effective ui for rendering: the app's ui augmented with the generic library, an OBJECT
    // view per self-hostable type, and a REFERENCE / SET view per object reference / set prop.
    // Returns the app's ui unchanged when it does not opt in. Functions are renumbered (CodeIds)
    // over the whole set so server and client key the memo cache alike.
    //
    // SystemNames lists the synthesized framework members (the library functions) so the renderer
    // places them in the SYSTEM scope, above the custom code — they never pollute the app scope.
    // Descriptors maps "TypeName" → a type's descriptor literal and "Owner/prop" → a dict prop's
    // descriptor, threaded into the executor so `sys.schema(...)` resolves a shape (the replacement
    // for the old `__descs`/`__dictDescs` globals); empty for a custom-render app.
    public static (InstanceUi? Ui, IReadOnlySet<string> SystemNames, IReadOnlyDictionary<string, CodeObject> Descriptors)
        Effective(InstanceDescription desc)
    {
        var ui = desc.Ui;
        // A fully-custom UI (`fn render()`) owns the whole URL space — no generic synthesis.
        if (ui?.Render != null) return (ui, EmptyNames, EmptyDescriptors);
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
        // system scope, leaving the app scope to the user's code. Descriptors are resolved by the
        // `sys.schema` builtin from the threaded map (no descriptor var in scope anymore).
        var systemNames = new HashSet<string>(library.Where(f => f.Name != null).Select(f => f.Name!));
        return (effective, systemNames, Descriptors(objectTypes, desc));
    }

    private static readonly IReadOnlySet<string> EmptyNames = new HashSet<string>();
    private static readonly IReadOnlyDictionary<string, CodeObject> EmptyDescriptors = new Dictionary<string, CodeObject>();

    private static IEnumerable<PropDefinition> RefProps(TypeDefinition type, InstanceDescription desc) =>
        (type.Props ?? []).Where(p => p.Cardinality == Cardinality.Single && desc.IsObjectType(p.Type));

    private static IEnumerable<PropDefinition> SetProps(TypeDefinition type, InstanceDescription desc) =>
        (type.Props ?? []).Where(p => p.Cardinality == Cardinality.Set && desc.IsObjectType(p.Type));

    private static IEnumerable<PropDefinition> DictProps(TypeDefinition type) =>
        (type.Props ?? []).Where(p => p.Cardinality == Cardinality.Dictionary);

    // `view T(obj, base)` → `return objectForm(obj, sys.schema("T"), base)`. `base` is the
    // page's URL path, threaded so inline sets build nested member links (sys.nest(base, prop)).
    private static UiView SynthObjectView(string typeName) =>
        new(typeName, Fn(["obj", "base"], Return(Call("objectForm", Sym("obj"), Schema(typeName), Sym("base")))));

    // Reference route `view(parent)` → `return <refEditor parent={parent} prop="P" target={…}>` (a
    // root-position component tag, keyed by its render slot). Keyed by (owner, Prop), bound to the
    // parent. Takes only `parent`: a reference builds no nested links, so it ignores the `base` arg
    // ExecuteRender passes to every type view (both interpreters bind min(args, params), so the
    // extra arg is harmless — keeping the param out of the Code is more honest than declaring it unused).
    private static UiView SynthRefView(string ownerType, string prop, string targetType) =>
        new(ownerType, Fn(["parent"], Return(Tag("refEditor",
                ("parent", Sym("parent")), ("prop", Text(prop)), ("target", Schema(targetType))))),
            Prop: prop);

    // Set route `view(parent, base)` → `return <setTable set={parent.P} desc={…} setPath={base}>`.
    // `base` is the set's own URL path, used for nested member links.
    private static UiView SynthSetView(string ownerType, string prop, string elementType) =>
        new(ownerType, Fn(["parent", "base"], Return(Tag("setTable",
                ("set", Field(Sym("parent"), Text(prop))), ("desc", Schema(elementType)), ("setPath", Sym("base"))))),
            Prop: prop);

    // The shared scalar-entry view: `view(entry, base)` → `return leafForm(entry, base)`. Bound
    // to the entry object (FindTarget resolves it by key); its value persists path-addressed.
    // leafForm is stateless (returns tags directly), so it stays a call — no slot identity needed.
    private static UiView SynthLeafView() =>
        new(LeafViewType, Fn(["entry", "base"], Return(Call("leafForm", Sym("entry"), Sym("base")))));

    // The shared NotFound view: `view() → return notFoundForm()`. Takes no target — it reads
    // the framework `path` var and sets a 404 status.
    private static UiView SynthNotFoundView() =>
        new(NotFoundViewType, Fn([], Return(Call("notFoundForm"))));

    // Dict route `view(parent, base)` → `return <dictTable dict={parent.P} desc={__dictDescs["O/P"]}
    // base={base}>` (a root-position component tag, slot-keyed so its draft state survives renders).
    private static UiView SynthDictView(string ownerType, string prop) =>
        new(ownerType, Fn(["parent", "base"], Return(Tag("dictTable",
                ("dict", Field(Sym("parent"), Text(prop))),
                ("desc", Schema(ownerType, prop)),
                ("base", Sym("base"))))),
            Prop: prop);

    // ── the descriptor registry ──────────────────────────────────────────────────────

    // The descriptor literals threaded into the executor for `sys.schema(...)` to evaluate (the
    // replacement for the old `__descs`/`__dictDescs` globals). Two key shapes, both pure data:
    //   "TypeName"      → { name, labelProp, props, blank } — a type's descriptor (sys.schema("T")).
    //   "Owner/prop"    → the dict prop's descriptor — for the root dict route (sys.schema("O","P")).
    // Built once per render from the schema; the executor evaluates + caches the one a call names and
    // ships it to the client like extent.
    private static Dictionary<string, CodeObject> Descriptors(List<TypeDefinition> objectTypes, InstanceDescription desc)
    {
        var map = objectTypes.ToDictionary(t => t.Name, t => TypeDescriptor(t, desc));
        foreach (var t in objectTypes)
            foreach (var p in (t.Props ?? []).Where(p => p.Cardinality == Cardinality.Dictionary))
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
            ("blank", Obj(scalars.Select(p => (p.Name, DefaultFor(p.Type))).ToArray())));
    }

    // A prop descriptor: scalar { name, baseType }; reference { name, baseType:"object",
    // target } and set { name, baseType:"set", element } carry the OTHER type's name (the
    // component resolves it via sys.schema(name) — cycle-safe). A dictionary
    // { name, baseType:"dictionary", keyType, isScalar, valueProps, blank } is self-contained
    // (dictTable reads it directly): valueProps are the element's scalar columns (empty for a
    // scalar dict, where isScalar=true and a single "Value" column is shown), blank seeds the
    // New-entry draft.
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
    // A childless tag `<name a={…} b={…}>` — used to invoke a synthesized component (refEditor /
    // setTable / dictTable) BY TAG, so it keys on its render-tree slot rather than its arguments.
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
