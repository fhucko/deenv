using DeEnv.Code;

namespace DeEnv.Instance;

// The self-hosted generic UI (milestone 9), re-expressed in Code and plugged into M8's
// view dispatch when an app opts in (InstanceUi.Generic). Two libraries:
//   • objectForm(obj, meta) — an object page from schema data, slice 1 (scalar fields)
//     extended in slice 2 to render a reference prop with a nested refEditor.
//   • refEditor(parent, prop, target) — the reference pick-or-create editor (slice 2):
//     "current" label, a pick button per extent() candidate, and a clear button.
// Builtins do the reflective work: field (dynamic access), humanize (labels), save
// (flush scalars), extent (a type's objects), setRef (set/clear a reference).
//
// Synthesis is render-time only: the canonical InstanceDescription (what AppPrint emits)
// keeps just the `generic` flag — never the synthesized functions/views — so parse/print
// round-trips stay stable. A synthesized OBJECT view (Type=T, Prop=null) is bound to the
// routed object; a synthesized REFERENCE view (Type=O, Prop=P) owns the route of the
// reference prop O.P and is bound to its PARENT object (the owner) — both reuse the M8
// type-view client binding, so no new wire shape is needed (the descriptor rides as a
// Code literal in the view body).
//
// Slice 2 scope: pick-existing + clear. Create-new (mint a draft through the reference)
// needs persistent per-field draft state and is a focused follow-up.
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

            fn inputType(baseType)
                if baseType == "int"
                    return "number"
                if baseType == "decimal"
                    return "number"
                if baseType == "date"
                    return "date"
                return "text"
        """;

    // The effective ui for rendering: the app's ui augmented with the generic library, a
    // synthesized OBJECT view per self-hostable object type with no explicit view, and a
    // synthesized REFERENCE view per object reference prop. Returns the app's ui unchanged
    // when it does not opt in. Functions are renumbered (CodeIds) over the whole effective
    // set so the server render and the shipped client AST agree on memo-cache keys.
    public static InstanceUi? Effective(InstanceDescription desc)
    {
        var ui = desc.Ui;
        if (ui is not { Generic: true }) return ui;

        // Parse the library FRESH (distinct CodeFunction instances each call, so concurrent
        // renderers never share mutable Ids).
        var (_, libUi) = CodeParse.ParseDocument(StdlibSource);
        var library = libUi.Functions ?? [];

        var explicitObjectTypes = (ui.Views ?? [])
            .Where(v => v is { Type: not null, Path: null, Prop: null }).Select(v => v.Type!).ToHashSet();

        var synthViews = new List<UiView>();
        foreach (var type in (desc.Types ?? []).Where(t => t.BaseType == BaseType.Object))
        {
            // Object page for a self-hostable type without an explicit view.
            if (IsSelfHostable(type) && !explicitObjectTypes.Contains(type.Name))
                synthViews.Add(SynthObjectView(type, desc));

            // Reference-route editor for each single object-reference prop (any owner —
            // e.g. Db.lead, even though Db itself isn't self-hostable).
            foreach (var prop in (type.Props ?? [])
                .Where(p => p.Cardinality == Cardinality.Single && desc.IsObjectType(p.Type)))
                synthViews.Add(SynthRefView(type.Name, prop, desc.FindType(prop.Type)!, desc));
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
        return effective;
    }

    // A self-hostable object type: every prop is single (a scalar or a single object
    // reference) — sets and dictionaries still fall to the C# auto-form (later slices).
    private static bool IsSelfHostable(TypeDefinition type) =>
        type.Props is { Count: > 0 } props && props.All(p => p.Cardinality == Cardinality.Single);

    // `view T(obj)` → `return objectForm(obj, { name, props })`.
    private static UiView SynthObjectView(TypeDefinition type, InstanceDescription desc)
    {
        var descriptor = ObjectDescriptor(type, desc);
        var body = Return(Call("objectForm", Sym("obj"), descriptor));
        return new UiView(type.Name, null, Fn("obj", body));
    }

    // Reference route: `view(parent)` → `return refEditor(parent, "P", <target descriptor>)`,
    // keyed by (owner Type, Prop) and bound to the parent object.
    private static UiView SynthRefView(string ownerType, PropDefinition prop, TypeDefinition target, InstanceDescription desc)
    {
        var body = Return(Call("refEditor", Sym("parent"), Text(prop.Name), TargetDescriptor(target, desc)));
        return new UiView(ownerType, null, Fn("parent", body), Prop: prop.Name);
    }

    // The object descriptor objectForm iterates: { name, props: [propDescriptor...] }.
    private static CodeObject ObjectDescriptor(TypeDefinition type, InstanceDescription desc) => Obj(
        ("name", Text(type.Name)),
        ("props", Arr((type.Props ?? []).Select(p => (ICodeValue)PropDescriptor(p, desc)))));

    // A prop descriptor: { name, baseType } for a scalar; { name, baseType: "object",
    // target } for a single object reference (target = the editor's type descriptor).
    private static CodeObject PropDescriptor(PropDefinition p, InstanceDescription desc) =>
        desc.IsObjectType(p.Type)
            ? Obj(("name", Text(p.Name)), ("baseType", Text("object")),
                  ("target", TargetDescriptor(desc.FindType(p.Type)!, desc)))
            : Obj(("name", Text(p.Name)), ("baseType", Text(p.Type)));

    // The reference target's descriptor: { name, labelProp, props } — labelProp is the
    // first text prop (the candidate option's label), props its scalar fields.
    private static CodeObject TargetDescriptor(TypeDefinition t, InstanceDescription desc)
    {
        var scalars = (t.Props ?? [])
            .Where(p => p.Cardinality == Cardinality.Single && BaseTypes.IsName(p.Type)).ToList();
        var labelProp = scalars.FirstOrDefault(p => p.Type == "text")?.Name
            ?? scalars.FirstOrDefault()?.Name ?? "";
        return Obj(
            ("name", Text(t.Name)),
            ("labelProp", Text(labelProp)),
            ("props", Arr(scalars.Select(p => (ICodeValue)Obj(("name", Text(p.Name)), ("baseType", Text(p.Type)))))));
    }

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
}
