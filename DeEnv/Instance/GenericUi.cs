using DeEnv.Code;

namespace DeEnv.Instance;

// The self-hosted generic UI (milestone 9), a reflective Code library over schema data,
// synthesized into the effective ui at render time when an app opts in (InstanceUi.Generic).
//
//   • objectForm(obj, meta) — an object page: a field per prop (scalar input, or a nested
//     refEditor for a single object reference). Edits autosave via `field`.
//   • refEditor(parent, prop, target) — a reference editor: current label, a pick button
//     per extent() candidate, a clear button, and a create-new form. A COMPONENT: its body
//     runs once as init (a local `state` holding a draft), and it returns a render fn.
//   • setTable(set, desc) — a set table: header + member rows (+ an "open" id-route link +
//     Remove) + an add form. Also a COMPONENT (the add form holds a draft).
//
// Builtins do the reflective work: field (dynamic access), humanize (labels), extent (a
// type's objects), setRef (set/clear a reference), link (id-route URL), clone (a fresh
// draft from a blank template). `obj.prop = x` resets a component's draft after Create.
//
// Descriptors are a single stable top-scope registry var `__descs` (typeName → descriptor),
// synthesized once — so the component functions receive a stable descriptor argument every
// render, which is what lets the memo cache run their init exactly once (the same pattern
// hand-authored forms use; no separate synthesized draft vars). Cross-type references are
// stored by type NAME (cycle-safe); a component resolves them with field(__descs, name).
//
// Synthesis is render-time only: the canonical InstanceDescription (what AppPrint emits)
// keeps just the `generic` flag. A synthesized OBJECT view (Type=T, Prop=null) binds the
// routed object; a REFERENCE / SET view (Type=O, Prop=P) binds the parent object that owns
// the prop — both reuse the M8 type-view client binding (no new wire shape).
public static class GenericUi
{
    private const string DescsVar = "__descs";

    private const string StdlibSource = """
        ui
            fn objectForm(obj, meta)
                return <div class="object-form">
                    <h2>
                        meta.name
                    foreach p in meta.props
                        <div class="field">
                            <label class={p.name}>
                                humanize(p.name)
                            if p.baseType == "object"
                                refEditor(obj, p.name, field(__descs, p.target))()
                            else if p.baseType == "bool"
                                <input type="checkbox" class={p.name} checked={field(obj, p.name)}>
                            else
                                <input type={inputType(p.baseType)} class={p.name} value={field(obj, p.name)}>

            fn refEditor(parent, prop, target)
                var state = { draft: clone(target.blank) }
                fn createNew()
                    setRef(parent, prop, state.draft)
                    state.draft = clone(target.blank)
                fn render()
                    return <div class="ref-editor">
                        <h3 class="ref-type">
                            target.name
                        <div class="ref-current">
                            "Current: "
                            if field(parent, prop) == null
                                "(none)"
                            else
                                field(field(parent, prop), target.labelProp)
                        foreach c in extent(target.name)
                            <button class="ref-pick" onClick={() => setRef(parent, prop, c)}>
                                field(c, target.labelProp)
                        <button class="ref-clear" onClick={() => setRef(parent, prop, null)}>
                            "Clear"
                        <div class="ref-new">
                            foreach p in target.props
                                if p.baseType != "object" && p.baseType != "set"
                                    <label class={p.name}>
                                        humanize(p.name)
                                    if p.baseType == "bool"
                                        <input type="checkbox" class={p.name} checked={field(state.draft, p.name)}>
                                    else
                                        <input type={inputType(p.baseType)} class={p.name} value={field(state.draft, p.name)}>
                            <button class="ref-create" onClick={createNew}>
                                "Create new"
                return render

            fn setTable(set, desc)
                var state = { draft: clone(desc.blank) }
                fn addNew()
                    set.add(state.draft)
                    state.draft = clone(desc.blank)
                fn render()
                    return <div class="set-table">
                        <table>
                            <tr class="set-head">
                                foreach p in desc.props
                                    if p.baseType != "object" && p.baseType != "set"
                                        <th>
                                            humanize(p.name)
                            foreach m in set
                                <tr class="set-row">
                                    foreach p in desc.props
                                        if p.baseType != "object" && p.baseType != "set"
                                            <td>
                                                field(m, p.name)
                                    <td>
                                        <a class="set-open" href={link(m)}>
                                            "open"
                                    <td>
                                        <button class="set-remove" onClick={() => set.remove(m)}>
                                            "Remove"
                        <div class="set-new">
                            foreach p in desc.props
                                if p.baseType != "object" && p.baseType != "set"
                                    if p.baseType == "bool"
                                        <input type="checkbox" class={p.name} checked={field(state.draft, p.name)}>
                                    else
                                        <input type={inputType(p.baseType)} class={p.name} value={field(state.draft, p.name)}>
                            <button class="set-add" onClick={addNew}>
                                "Add"
                return render

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
    public static InstanceUi? Effective(InstanceDescription desc)
    {
        var ui = desc.Ui;
        if (ui is not { Generic: true }) return ui;

        // Parse the library FRESH (distinct CodeFunction instances each call, so concurrent
        // renderers never share mutable Ids).
        var (_, libUi) = CodeParse.ParseDocument(StdlibSource);
        var library = libUi.Functions ?? [];

        var objectTypes = (desc.Types ?? []).Where(t => t.BaseType == BaseType.Object).ToList();

        var synthViews = new List<UiView>();
        foreach (var type in objectTypes)
        {
            // Object page for a self-hostable type (scalars + single references).
            if (IsSelfHostable(type))
                synthViews.Add(SynthObjectView(type.Name));

            // Reference-route editor per single object-reference prop (any owner — e.g. Db.lead).
            foreach (var prop in RefProps(type, desc))
                synthViews.Add(SynthRefView(type.Name, prop.Name, prop.Type));

            // Set-table page per object set prop (e.g. Db.notes → /notes).
            foreach (var prop in SetProps(type, desc))
                synthViews.Add(SynthSetView(type.Name, prop.Name, prop.Type));
        }

        var vars = new List<UiVar>();
        vars.AddRange(ui.Vars ?? []);
        vars.Add(new UiVar(DescsVar, Registry(objectTypes, desc)));   // the stable descriptor registry
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
        return effective;
    }

    private static IEnumerable<PropDefinition> RefProps(TypeDefinition type, InstanceDescription desc) =>
        (type.Props ?? []).Where(p => p.Cardinality == Cardinality.Single && desc.IsObjectType(p.Type));

    private static IEnumerable<PropDefinition> SetProps(TypeDefinition type, InstanceDescription desc) =>
        (type.Props ?? []).Where(p => p.Cardinality == Cardinality.Set && desc.IsObjectType(p.Type));

    // A self-hostable object type: every prop is single (a scalar or a single object
    // reference) — sets and dictionaries still fall to the C# auto-form (later slices).
    private static bool IsSelfHostable(TypeDefinition type) =>
        type.Props is { Count: > 0 } props && props.All(p => p.Cardinality == Cardinality.Single);

    // `view T(obj)` → `return objectForm(obj, field(__descs, "T"))`.
    private static UiView SynthObjectView(string typeName) =>
        new(typeName, Fn("obj", Return(Call("objectForm", Sym("obj"), Desc(typeName)))));

    // Reference route `view(parent)` → `return refEditor(parent, "P", field(__descs, "T"))()`
    // (the component is invoked). Keyed by (owner, Prop), bound to the parent.
    private static UiView SynthRefView(string ownerType, string prop, string targetType) =>
        new(ownerType, Fn("parent", Return(Invoke(Call("refEditor", Sym("parent"), Text(prop), Desc(targetType))))),
            Prop: prop);

    // Set route `view(parent)` → `return setTable(field(parent, "P"), field(__descs, "T"))()`.
    private static UiView SynthSetView(string ownerType, string prop, string elementType) =>
        new(ownerType, Fn("parent", Return(Invoke(Call("setTable", Field(Sym("parent"), Text(prop)), Desc(elementType))))),
            Prop: prop);

    // ── the descriptor registry ──────────────────────────────────────────────────────

    // { TypeName: { name, labelProp, props, blank }, … } — one stable top-scope object.
    private static CodeObject Registry(List<TypeDefinition> objectTypes, InstanceDescription desc) =>
        new() { Props = objectTypes
            .Select(t => new CodeObjectProp { Name = t.Name, Value = TypeDescriptor(t, desc) })
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
    // component resolves it via field(__descs, name) — cycle-safe).
    private static CodeObject PropDesc(PropDefinition p, InstanceDescription desc) =>
        p.Cardinality == Cardinality.Set
            ? Obj(("name", Text(p.Name)), ("baseType", Text("set")), ("element", Text(p.Type)))
            : desc.IsObjectType(p.Type)
                ? Obj(("name", Text(p.Name)), ("baseType", Text("object")), ("target", Text(p.Type)))
                : Obj(("name", Text(p.Name)), ("baseType", Text(p.Type)));

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
    private static CodeCall Field(ICodeValue obj, ICodeValue name) => new() { Fn = Sym("field"), Params = [obj, name] };
    private static CodeCall Desc(string typeName) => Field(Sym(DescsVar), Text(typeName));
    private static CodeCall Invoke(ICodeValue fn) => new() { Fn = fn, Params = [] };
    private static CodeBlock Return(ICodeValue value) => new() { Statements = [new CodeReturn { Value = value }] };
    private static CodeFunction Fn(string param, CodeBlock body) =>
        new() { Name = null, Params = [new CodeFunctionParam { Name = param }], Body = body };
}
