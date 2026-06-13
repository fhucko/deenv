using DeEnv.Code;

namespace DeEnv.Instance;

// The self-hosted generic UI (milestone 9, slice 1): the generic object page,
// re-expressed in Code. `objectForm(obj, meta)` renders a form from schema data
// (meta: { name, props: [{ name, baseType }] }) using the `field(obj, name)` builtin
// for dynamic, two-way-bound access. When an app opts in (InstanceUi.Generic), each
// all-scalar object type without an explicit view gets a synthesized `view T(obj)`
// that calls objectForm with that type's descriptor — so it plugs into the M8 type-view
// dispatch unchanged, and the JSON/wire stay as-is (the descriptor rides as a Code literal
// inside the shipped view AST, so there is no schema to ship separately).
//
// Synthesis is render-time only: the canonical InstanceDescription (what AppPrint emits)
// keeps just the `generic` flag — never the synthesized functions/views — so parse/print
// round-trips stay stable.
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
                            if p.baseType == "bool"
                                <input type="checkbox" class={p.name} checked={field(obj, p.name)}>
                            else
                                <input type={inputType(p.baseType)} class={p.name} value={field(obj, p.name)}>
                    <button class="save" onClick={() => save(obj)}>
                        "Save"

            fn inputType(baseType)
                if baseType == "int"
                    return "number"
                if baseType == "decimal"
                    return "number"
                if baseType == "date"
                    return "date"
                return "text"
        """;

    // The effective ui for rendering: the app's ui augmented with the generic library and
    // one synthesized view per all-scalar object type that has no explicit view. Returns
    // the app's ui unchanged when it does not opt in. Functions are renumbered (CodeIds)
    // over the whole effective set so the server render and the shipped client AST agree
    // on memo-cache keys.
    public static InstanceUi? Effective(InstanceDescription desc)
    {
        var ui = desc.Ui;
        if (ui is not { Generic: true }) return ui;

        // Parse the library FRESH (distinct CodeFunction instances each call, so concurrent
        // renderers never share mutable Ids).
        var (_, libUi) = CodeParse.ParseDocument(StdlibSource);
        var library = libUi.Functions ?? [];

        var explicitTypes = (ui.Views ?? [])
            .Where(v => v.Type != null).Select(v => v.Type!).ToHashSet();
        var synthViews = new List<UiView>();
        foreach (var type in desc.Types ?? [])
            if (type.BaseType == BaseType.Object && IsAllScalar(type) && !explicitTypes.Contains(type.Name))
                synthViews.Add(SynthView(type));

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

    // An all-scalar object type: every prop is a single scalar (no sets, dicts, or object
    // references). These are exactly the pages slice 1 self-hosts; richer shapes stay on
    // the C# auto-form until later slices.
    private static bool IsAllScalar(TypeDefinition type) =>
        type.Props is { Count: > 0 } props
        && props.All(p => p.Cardinality == Cardinality.Single && BaseTypes.IsName(p.Type));

    // `view T(obj)` whose body is `return objectForm(obj, { name: "T", props: [...] })`.
    private static UiView SynthView(TypeDefinition type)
    {
        var propDescriptors = (type.Props ?? [])
            .Select(p => (ICodeValue)new CodeObject
            {
                Props =
                [
                    new CodeObjectProp { Name = "name", Value = new CodeText { Value = p.Name } },
                    new CodeObjectProp { Name = "baseType", Value = new CodeText { Value = p.Type } },
                ],
            })
            .ToArray();

        var descriptor = new CodeObject
        {
            Props =
            [
                new CodeObjectProp { Name = "name", Value = new CodeText { Value = type.Name } },
                new CodeObjectProp { Name = "props", Value = new CodeArray { Items = propDescriptors } },
            ],
        };

        var body = new CodeBlock
        {
            Statements =
            [
                new CodeReturn
                {
                    Value = new CodeCall
                    {
                        Fn = new CodeSymbol { Name = "objectForm" },
                        Params = [new CodeSymbol { Name = "obj" }, descriptor],
                    },
                },
            ],
        };

        var fn = new CodeFunction
        {
            Name = null,
            Params = [new CodeFunctionParam { Name = "obj" }],
            Body = body,
        };
        return new UiView(type.Name, null, fn);
    }
}
