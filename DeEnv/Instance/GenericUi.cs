using DeEnv.Code;

namespace DeEnv.Instance;

// The self-hosted generic UI (milestone 9), re-expressed in Code and plugged into M8's
// view dispatch when an app opts in (InstanceUi.Generic). Two libraries:
//   • objectForm(obj, meta) — an object page from schema data (scalar fields + a nested
//     refEditor for each reference prop).
//   • refEditor(parent, prop, target) — the reference pick-or-create editor: a "current"
//     label, a pick button per extent() candidate, a clear button, and a create-new form
//     (inputs bound to a per-prop draft → a Create button that mints + points).
// Builtins do the reflective work: field (dynamic access), humanize (labels), extent (a
// type's objects), setRef (set/clear a reference).
//
// Synthesis is render-time only: the canonical InstanceDescription (what AppPrint emits)
// keeps just the `generic` flag — never the synthesized vars/functions/views — so
// parse/print round-trips stay stable. A synthesized OBJECT view (Type=T, Prop=null) is
// bound to the routed object; a synthesized REFERENCE view (Type=O, Prop=P) owns the route
// of the reference prop O.P and is bound to its PARENT object — both reuse the M8 type-view
// client binding, so no new wire shape is needed (descriptors ride as Code literals).
//
// Create-new state: each reference prop gets a synthesized top-scope draft var
// (`__draft_<Owner>_<Prop>`, a pre-shaped default object) + a reset closure, bundled into
// the reference descriptor. This reuses the proven top-scope-var reactivity (reassign →
// invalidateVar → re-render) — the same pattern hand-written apps use for new-item forms.
public static class GenericUi
{
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
                                refEditor(obj, p.name, p.target)
                            else if p.baseType == "bool"
                                <input type="checkbox" class={p.name} checked={field(obj, p.name)}>
                            else
                                <input type={inputType(p.baseType)} class={p.name} value={field(obj, p.name)}>

            fn refEditor(parent, prop, target)
                fn createRef()
                    setRef(parent, prop, target.draft)
                    target.resetDraft()
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
                            <label class={p.name}>
                                humanize(p.name)
                            if p.baseType == "bool"
                                <input type="checkbox" class={p.name} checked={field(target.draft, p.name)}>
                            else
                                <input type={inputType(p.baseType)} class={p.name} value={field(target.draft, p.name)}>
                        <button class="ref-create" onClick={createRef}>
                            "Create new"

            fn setTable(set, desc)
                fn addNew()
                    set.add(desc.draft)
                    desc.resetDraft()
                return <div class="set-table">
                    <table>
                        <tr class="set-head">
                            foreach p in desc.props
                                <th>
                                    humanize(p.name)
                        foreach m in set
                            <tr class="set-row">
                                foreach p in desc.props
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
                            if p.baseType == "bool"
                                <input type="checkbox" class={p.name} checked={field(desc.draft, p.name)}>
                            else
                                <input type={inputType(p.baseType)} class={p.name} value={field(desc.draft, p.name)}>
                        <button class="set-add" onClick={addNew}>
                            "Add"

            fn inputType(baseType)
                if baseType == "int"
                    return "number"
                if baseType == "decimal"
                    return "number"
                if baseType == "date"
                    return "date"
                return "text"
        """;

    // The effective ui for rendering: the app's ui augmented with the generic library; a
    // synthesized OBJECT view per self-hostable object type without an explicit view; a
    // synthesized REFERENCE view per object reference prop; and a per-reference-prop draft
    // var for create-new. Returns the app's ui unchanged when it does not opt in. Functions
    // are renumbered (CodeIds) over the whole set so server and client key the cache alike.
    public static InstanceUi? Effective(InstanceDescription desc)
    {
        var ui = desc.Ui;
        if (ui is not { Generic: true }) return ui;

        // Parse the library FRESH (distinct CodeFunction instances each call, so concurrent
        // renderers never share mutable Ids).
        var (_, libUi) = CodeParse.ParseDocument(StdlibSource);
        var library = libUi.Functions ?? [];

        var draftVars = new List<UiVar>();
        var synthViews = new List<UiView>();
        foreach (var type in (desc.Types ?? []).Where(t => t.BaseType == BaseType.Object))
        {
            // A pre-shaped draft var per reference / set prop (create-new state; the set's
            // draft is an element-type object).
            foreach (var prop in RefProps(type, desc))
                draftVars.Add(new UiVar(DraftVarName(type.Name, prop.Name), DraftLiteral(desc.FindType(prop.Type)!)));
            foreach (var prop in SetProps(type, desc))
                draftVars.Add(new UiVar(DraftVarName(type.Name, prop.Name), DraftLiteral(desc.FindType(prop.Type)!)));

            // Object page for a self-hostable type.
            if (IsSelfHostable(type))
                synthViews.Add(SynthObjectView(type, desc));

            // Reference-route editor for each reference prop (any owner — e.g. Db.lead).
            foreach (var prop in RefProps(type, desc))
                synthViews.Add(SynthRefView(type.Name, prop, desc.FindType(prop.Type)!, desc));

            // Set-table page for each object set prop (e.g. Db.notes → /notes).
            foreach (var prop in SetProps(type, desc))
                synthViews.Add(SynthSetView(type.Name, prop, desc.FindType(prop.Type)!, desc));
        }

        var vars = new List<UiVar>();
        vars.AddRange(ui.Vars ?? []);
        vars.AddRange(draftVars);
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

    private static string DraftVarName(string ownerType, string prop) => $"__draft_{ownerType}_{prop}";

    // A self-hostable object type: every prop is single (a scalar or a single object
    // reference) — sets and dictionaries still fall to the C# auto-form (later slices).
    private static bool IsSelfHostable(TypeDefinition type) =>
        type.Props is { Count: > 0 } props && props.All(p => p.Cardinality == Cardinality.Single);

    // `view T(obj)` → `return objectForm(obj, { name, props })`.
    private static UiView SynthObjectView(TypeDefinition type, InstanceDescription desc)
    {
        var body = Return(Call("objectForm", Sym("obj"), ObjectDescriptor(type, desc)));
        return new UiView(type.Name, Fn("obj", body));
    }

    // Reference route: `view(parent)` → `return refEditor(parent, "P", <ref descriptor>)`,
    // keyed by (owner Type, Prop) and bound to the parent object.
    private static UiView SynthRefView(string ownerType, PropDefinition prop, TypeDefinition target, InstanceDescription desc)
    {
        var body = Return(Call("refEditor", Sym("parent"), Text(prop.Name), RefDescriptor(ownerType, prop.Name, target, desc)));
        return new UiView(ownerType, Fn("parent", body), Prop: prop.Name);
    }

    // Set route: `view(parent)` → `return setTable(field(parent, "P"), <set descriptor>)`,
    // keyed by (owner Type, set Prop) and bound to the parent object.
    private static UiView SynthSetView(string ownerType, PropDefinition prop, TypeDefinition element, InstanceDescription desc)
    {
        var set = new CodeCall { Fn = Sym("field"), Params = [Sym("parent"), Text(prop.Name)] };
        var body = Return(Call("setTable", set, SetDescriptor(ownerType, prop.Name, element)));
        return new UiView(ownerType, Fn("parent", body), Prop: prop.Name);
    }

    // The set descriptor setTable uses: { props, draft, resetDraft } — props are the
    // element type's scalar fields (columns + the add form); draft/resetDraft are the
    // synthesized add-form state (an element-type draft + its reset).
    private static CodeObject SetDescriptor(string ownerType, string prop, TypeDefinition element)
    {
        var draftVar = DraftVarName(ownerType, prop);
        return Obj(
            ("props", Arr(Scalars(element).Select(p => (ICodeValue)Obj(("name", Text(p.Name)), ("baseType", Text(p.Type)))))),
            ("draft", Sym(draftVar)),
            ("resetDraft", Fn0(Return(new CodeAssignment { Target = Sym(draftVar), Value = DraftLiteral(element) }))));
    }

    // The object descriptor objectForm iterates: { name, props: [propDescriptor...] }.
    private static CodeObject ObjectDescriptor(TypeDefinition type, InstanceDescription desc) => Obj(
        ("name", Text(type.Name)),
        ("props", Arr((type.Props ?? []).Select(p => (ICodeValue)PropDescriptor(type.Name, p, desc)))));

    // A prop descriptor: { name, baseType } for a scalar; { name, baseType: "object",
    // target } for a single object reference (target = the reference editor's descriptor).
    private static CodeObject PropDescriptor(string ownerType, PropDefinition p, InstanceDescription desc) =>
        desc.IsObjectType(p.Type)
            ? Obj(("name", Text(p.Name)), ("baseType", Text("object")),
                  ("target", RefDescriptor(ownerType, p.Name, desc.FindType(p.Type)!, desc)))
            : Obj(("name", Text(p.Name)), ("baseType", Text(p.Type)));

    // The reference descriptor refEditor uses: { name, labelProp, props, draft, resetDraft }.
    // labelProp = the first text prop (option label); props = the target's scalar fields;
    // draft = the synthesized top-scope draft var (create-new state); resetDraft = a closure
    // that reassigns the draft var to a fresh default object.
    private static CodeObject RefDescriptor(string ownerType, string prop, TypeDefinition target, InstanceDescription desc)
    {
        var scalars = Scalars(target);
        var labelProp = scalars.FirstOrDefault(p => p.Type == "text")?.Name
            ?? scalars.FirstOrDefault()?.Name ?? "";
        var draftVar = DraftVarName(ownerType, prop);
        return Obj(
            ("name", Text(target.Name)),
            ("labelProp", Text(labelProp)),
            ("props", Arr(scalars.Select(p => (ICodeValue)Obj(("name", Text(p.Name)), ("baseType", Text(p.Type)))))),
            ("draft", Sym(draftVar)),
            ("resetDraft", Fn0(Return(new CodeAssignment { Target = Sym(draftVar), Value = DraftLiteral(target) }))));
    }

    // A blank draft object for a type: its scalar props, each at its base type's default.
    private static CodeObject DraftLiteral(TypeDefinition target) =>
        Obj(Scalars(target).Select(p => (p.Name, DefaultFor(p.Type))).ToArray());

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
    private static CodeCall Call(string fn, params ICodeValue[] args) =>
        new() { Fn = Sym(fn), Params = args };
    private static CodeBlock Return(ICodeValue value) =>
        new() { Statements = [new CodeReturn { Value = value }] };
    private static CodeFunction Fn(string param, CodeBlock body) =>
        new() { Name = null, Params = [new CodeFunctionParam { Name = param }], Body = body };
    private static CodeFunction Fn0(CodeBlock body) =>
        new() { Name = null, Params = [], Body = body };
}
