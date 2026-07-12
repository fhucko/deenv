using System.Text;
using DeEnv.Code;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Designer;

// The bridge from the self-hosted designer to a runnable instance.
//
// The designer designs a whole app as ordinary data. The unit it projects is a
// `Design` node: a `types` set of MetaType (each holding a `props` set of MetaProp)
// — the STRUCTURED part — plus four `initialData`/`access`/`common`/`ui` TEXT fields
// that carry the other app-document sections verbatim. `Project` turns the structured
// `types` into TypeDefinitions; `ProjectDesignDb` assembles the whole app
// document (printed types + the verbatim sections), validates it with the normal
// loader, and returns it as text. A publish writes that text onto a target and
// resets the target's data; a create hands it to the kernel to spawn a new instance.
//
// The text fields hold the VERBATIM section source INCLUDING the section
// keyword and its indentation — e.g. the `ui` field is "ui\n    fn render()\n…",
// the empty string when there is no such section. That representation makes both
// directions trivial: assembly here is "print the types section, then concatenate
// the non-empty section texts", and the future committed-app → Design split is
// just slicing a document at its section boundaries. The one exception is `ui`, which
// assembly CANONICALIZES (parse∘print) rather than concatenating verbatim (M12 S0), so
// the projected artifact is stable however the render code was formatted. Validation (and an empty-section
// app — empty `ui` → generic UI, empty `initialData` → no seed) all fall out of the
// normal AppParse pipeline.
//
// This lives beside the instance runtime, not inside it — it never touches the
// renderer, the websocket handler, or the storage engine.

// The per-commit caches (M13 slice 2): the canonical printed app document + a name-path → intrinsic-id
// map over its types and props. Text alone is names-only (insufficient for rename-aware diff); the id
// map re-attaches the M5 identity a by-name projection otherwise drops. IdMap keys are "TypeName" (the
// type's MetaType row id) and "TypeName.propName" (the prop's MetaProp row id) — dotted name-paths,
// unambiguous because ProjectDesignDb's own validation already requires unique names. The map keys
// EXACTLY what the projected document shows, nothing more — so an enum type contributes only its own type
// entry and NO prop entries, even if leftover MetaProp members linger in its `props` set after an
// object→enum base-type flip (Project's enum branch hardcodes Props: null; its values live in a single
// text field with no per-value identity, so a value rename/reorder is a textual diff, not an identity
// one). Derived and rebuildable from the design at any time — never itself authoritative (slice 3
// persists it on the Commit row; nothing here writes storage).
public sealed record DesignSnapshot(string Text, IReadOnlyDictionary<string, int> IdMap);

public static class SchemaBridge
{
    // Build a design's per-commit snapshot: the canonical printed app document, then a name-path→id map
    // walked over the SAME structure ProjectDesignDb prints (types, each type's props), keeping the
    // member ids OrderedObjects/Project discard. Text is computed FIRST — ProjectDesignDb validates
    // (types, then the whole assembled document) and THROWS SchemaValidationException on an invalid
    // design — so an invalid design yields no snapshot at all, not a partial id map. (Snapshot inherits
    // that validate-or-throw behavior; how a future sys.commitDesign surfaces "can't commit an invalid
    // design" to the caller is that slice's decision, not this one's.) Same design state → byte-identical
    // Text on every call: the printer is canonical and the verbatim sections are passed through unchanged.
    public static DesignSnapshot Snapshot(NodeValue design)
    {
        var text = ProjectDesignDb(design); // throws on an invalid design — before the map is built
        var idMap = new Dictionary<string, int>();

        if (design is ObjectValue d && d.Fields.TryGetValue("types", out var typesNode))
            foreach (var (typeId, type) in OrderedMembers(typesNode))
            {
                var typeName = TextField(type, "name");
                idMap[typeName] = typeId;

                // Emit prop entries ONLY when the projection actually prints props for this type. Project's
                // ENUM branch hardcodes Props: null (an enum carries a value list, no props), so an enum's
                // props NEVER reach the document — even if leftover MetaProp members linger in its set after
                // a base-type flip (object → enum). Mirror that exclusion here so the map keys EXACTLY what
                // the printed doc shows: a phantom "EnumType.leftoverProp" entry would make a slice-4 diff
                // misclassify a prop the document has no trace of. Every other base (object, and the scalar
                // BaseTypes.IsName aliases) carries its props through the projection — keep walking those.
                if (TextField(type, "baseType") != "enum" && type.Fields.TryGetValue("props", out var propsNode))
                    foreach (var (propId, prop) in OrderedMembers(propsNode))
                        idMap[$"{typeName}.{TextField(prop, "name")}"] = propId;
            }

        return new DesignSnapshot(text, idMap);
    }

    // Project a Design node (structured types + the three verbatim section texts) into a
    // complete, validated app document (text) — the whole app, not just its types, so a
    // published/created instance keeps its custom UI (`fn render()`), seed data, and shared
    // functions. Throws SchemaValidationException on an invalid design (the same validation
    // pipeline as any hand-written document), so a bad design yields no document.
    public static string ProjectDesignDb(NodeValue design)
    {
        // M12 S1a — a structured render tree (Design.render, a `set of MetaNode` holding exactly one root)
        // projects to a canonical `ui` section, the same authority-inversion the `types` set already uses
        // (structure = truth, printed text = artifact). The root lives in a SET (not a single reference) so
        // ReadNode resolves it — and its nested `children`/`attrs` — recursively, exactly like `types`;
        // no store/resolver plumbing needed here. Empty set ⇒ no structured render (fall through to the
        // `ui` text field, unchanged). The gate: a structured render is valid ONLY when the `ui` text field
        // is empty; if BOTH are present, refuse (the user-decided precedence) rather than silently pick one.
        var renderRoot = design is ObjectValue dr && dr.Fields.TryGetValue("render", out var r)
            ? OrderedObjects(r).ToList() : [];
        if (renderRoot.Count > 1)
            throw new SchemaValidationException("A design's `render` tree may have only one root, but more than one was found.");
        if (renderRoot.Count == 1 && design is ObjectValue dt && TextField(dt, "ui") is { Length: > 0 })
            throw new SchemaValidationException(
                "A design cannot carry both a structured `render` tree and a non-empty `ui` text field — " +
                "the render tree projects the `ui` section, so the text field must be empty.");

        // M12 F1 — `fns` (structured components/helpers, Design.fns) requires a structured `render` root to
        // project INTO: ProjectRenderUi below assembles Functions alongside Render, so a design with fns but
        // no render has nowhere to put them. INTERIM, not law — InstanceUi legally allows Functions with
        // Render=null (a fn-only library), and a later palette (S5) may want exactly that — revisit then.
        // Today this state is reachable only by hand-editing storage (the "+ Component" button lives INSIDE
        // the render section, gated the same as the render tree itself), so refusing it here is enough.
        var fnsCount = design is ObjectValue df && df.Fields.TryGetValue("fns", out var fnsNode)
            ? OrderedObjects(fnsNode).Count() : 0;
        if (fnsCount > 0 && renderRoot.Count == 0)
            throw new SchemaValidationException(
                "A design's `fns` (structured components/helpers) require a structured `render` root to project " +
                "into; this design has functions but no render tree.");

        // Validate the projected TYPES first, on the typed description — so a structural type error
        // (e.g. an object Db with no props) surfaces as its precise semantic message ("…has baseType
        // 'object' but no props") rather than the parse error that printing-then-reparsing such an
        // invalid shape would raise (the printer can emit a propless object the parser won't accept).
        var typed = Project(design);
        InstanceDescriptionLoader.ValidateDescription(typed); // throws on invalid types

        // The `types` section, printed from the (now-validated) structured types via the canonical
        // printer. A types-only description prints exactly the `types` section (no other section
        // emitted), so this is just that section's text.
        var typesSection = AppPrint.Print(typed);

        // The other sections, each verbatim INCLUDING its keyword (empty → absent). Concatenated
        // after the types section with a blank line between (the section parsers skip blank lines
        // before their keyword, so the spacing is cosmetic / canonical). ORDER MATTERS — it must match
        // the document grammar (types, initialData, access, common, ui) so the reassembled text parses;
        // `access` (the M-auth ruleset, incl. the host-action `sys` subject) sits between initialData and
        // common, exactly as AppParse.Design expects.
        var sections = new List<string> { typesSection.TrimEnd('\n') };
        if (design is ObjectValue d)
            foreach (var name in new[] { "initialData", "access", "common", "ui" })
            {
                // The `ui` section, when a structured render root is present, is PROJECTED from the MetaNode
                // tree (S1a) rather than read from the `ui` text field (which the gate has already forced
                // empty). Projecting to AST-then-existing-printer inherits the canonical fixpoint exactly
                // as the canonicalize-on-project path below does — never hand-emitting text.
                if (name == "ui" && renderRoot.Count == 1)
                {
                    sections.Add(AppPrint.PrintUi(ProjectRenderUi((ObjectValue)design, renderRoot[0])).TrimEnd('\n'));
                    continue;
                }
                if (TextField(d, name) is { Length: > 0 } section)
                    // The `ui` section is CANONICALIZED (parse∘print) rather than passed through verbatim, so
                    // two designs differing only in render-code formatting project to byte-identical text —
                    // a stable commit/publish artifact for M13 diff (M12 S0). Only `ui` is re-printed: it has
                    // the canonical printer fixpoint, whereas re-printing `initialData` would reorder its dict
                    // entries, so those sections stay verbatim. An unparseable `ui` throws here — as it would
                    // at the Load below — so an invalid design still yields no document.
                    sections.Add((name == "ui"
                        ? AppPrint.PrintUi(CodeParse.ParseUiSection(section))
                        : section).TrimEnd('\n'));
            }

        var document = string.Join("\n\n", sections) + "\n";

        // Validate the WHOLE assembled document via the normal loader (parse + semantic validation):
        // this is what catches a malformed section text or a cross-section error (e.g. a Code/UI or
        // initialData problem). Throws on an invalid design, so nothing is published/spawned. Returning
        // the assembled text keeps the initialData/access/common sections as the user's exact source
        // (only `ui` was canonicalized above).
        InstanceDescriptionLoader.Load(document);
        return document;
    }

    // Pure projection: a Design (or legacy Db) node's `types` set → the typed description (types
    // only). Shared by ProjectDesignDb (which adds the other sections) and the M4 tests.
    public static InstanceDescription Project(NodeValue designerDb)
    {
        var types = new List<TypeDefinition>();

        if (designerDb is ObjectValue db && db.Fields.TryGetValue("types", out var typesNode))
        {
            foreach (var type in OrderedObjects(typesNode))
            {
                var name = TextField(type, "name");
                var baseName = TextField(type, "baseType");

                var props = new List<PropDefinition>();
                if (type.Fields.TryGetValue("props", out var propsNode))
                    foreach (var prop in OrderedObjects(propsNode))
                    {
                        var cardinality = TextField(prop, "cardinality") switch
                        {
                            "" or "single" => Cardinality.Single,
                            "set"          => Cardinality.Set,
                            "dictionary"   => Cardinality.Dictionary,
                            var other => throw new SchemaValidationException(
                                $"Prop on type '{name}' has unknown cardinality '{other}'."),
                        };
                        // keyType is meaningful ONLY for a dictionary. The designer now renders the key-type
                        // field only when the cardinality IS dictionary (progressive disclosure), but a
                        // single/set prop could still carry a leftover value from a hand-written document —
                        // ignore it here unless dictionary (a set that declared a keyType is rejected on load).
                        var keyType = cardinality == Cardinality.Dictionary
                            && TextField(prop, "keyType") is { Length: > 0 } key ? key : null;
                        // `multiline` is a presentation flag valid ONLY on a single text prop (the loader
                        // rejects it elsewhere). The designer's toggle is shown only for that shape, but a
                        // hand-written document could carry a stale flag on a retyped prop — so project it
                        // ONLY when the prop is still a single text prop, mirroring how keyType is ignored
                        // off a dictionary. A missing field defaults false (the same defensive read).
                        var propType = TextField(prop, "type");
                        var multiline = cardinality == Cardinality.Single
                            && propType == "text"
                            && BoolField(prop, "multiline");
                        props.Add(new PropDefinition(
                            TextField(prop, "name"),
                            propType,
                            cardinality,
                            keyType,
                            Multiline: multiline));
                    }

                if (baseName == "object")
                    // Emit props only when there are some, so a designed object type
                    // without props is rejected by the shared validation ("no props").
                    types.Add(new TypeDefinition(name, BaseType.Object, props.Count > 0 ? props : null));
                else if (baseName == "enum")
                {
                    // An enum carries no props — only a value list, authored in the designer as a single
                    // comma-separated field (the always-rendered `values` input). Split, trim, drop empties.
                    // An enum with zero values is rejected by the shared validation ("no values"), so an
                    // empty field yields no document — correct.
                    var values = TextField(type, "values")
                        .Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
                    types.Add(new TypeDefinition(name, BaseType.Enum, Props: null, Values: values));
                }
                else if (BaseTypes.IsName(baseName))
                    types.Add(new TypeDefinition(name, BaseTypes.Parse(baseName), props.Count > 0 ? props : null));
                else
                    throw new SchemaValidationException($"Type '{name}' has unknown baseType '{baseName}'.");
            }
        }

        return new InstanceDescription(types);
    }

    // ── M12 S1a/F1/V1: structured render tree (+ structured fns + vars) → `ui` section ─────
    //
    // Project the MetaNode tree rooted at `root` into an InstanceUi whose `render` is `fn render()`
    // returning the root element — the exact shape a hand-written custom-UI design carries, so it flows
    // unchanged through the existing print→parse→run pipeline (NO interpreter/grammar change). The root
    // MUST be an element (a non-empty tag) — a `render` returning a bare text/expression leaf is not a
    // page. `root`'s `children`/`attrs` sets (and every descendant's) are already resolved inline on the
    // ObjectValue — the store builds them recursively at read, exactly like `types` — so no resolver.
    //
    // F1 additionally projects `design.fns` (each a MetaFn: name, comma-separated params, a single-root
    // `body`) into InstanceUi.Functions, IN ORDER — the language's OWN InstanceUi shape distinguishes
    // Functions from Render (InstanceDescription.cs), so this mirrors that rather than special-casing.
    // Each MetaFn's body root projects through the SAME ProjectNode as the render tree; unlike render's
    // root, a fn's body root may be a LEAF (a helper returning a scalar expression) — only an ELEMENT is
    // required for render, since a page must return markup, but a helper legitimately returns a value.
    //
    // M12 V1 additionally projects two kinds of MetaVar rows:
    //   • `design.vars` (top-level `ui var`s) → InstanceUi.Vars, in `order` (the printer's canonical
    //     vars-then-fns-then-render order already holds — AppPrint.PrintUi).
    //   • a MetaFn's OWN `vars` (its component-local persistent state) → the STATEFUL canonical shape a
    //     hand-written setup/view component carries (confirmed against the designer's own `designEditor`
    //     and GenericUi's ConfirmButton/KebabMenu/… — ALL twelve stateful library components use this exact
    //     shape): `var v1 = …` … `fn render() return <view>` `return render`. A fn with an EMPTY `vars` set
    //     keeps projecting the plain single-`return` shape (byte-identical to before this slice).
    private static InstanceUi ProjectRenderUi(ObjectValue design, ObjectValue root)
    {
        if (TextField(root, "tag") is not { Length: > 0 })
            throw new SchemaValidationException(
                "A structured `render` root must be an element (a MetaNode with a non-empty `tag`), not a leaf expression.");

        var functions = new List<CodeFunction>();
        var seenNames = new HashSet<string>();
        foreach (var fn in OrderedObjects(design.Fields.GetValueOrDefault("fns")))
        {
            var name = TextField(fn, "name");
            if (name is not { Length: > 0 })
                throw new SchemaValidationException("A structured function has an empty name.");
            // Reserved: MapUi (CodeParse.cs) routes ANY fn named "render" into InstanceUi.Render, discarding
            // whatever was already there — a structured function named "render" would silently vanish from
            // the projected document (its slot is the page's own render fn, assembled separately below).
            if (name == "render")
                throw new SchemaValidationException(
                    "A structured function cannot be named \"render\" — that name is reserved for the page's " +
                    "render function; a function named \"render\" would be routed there and silently disappear " +
                    "from the projected document.");
            // Every consumer of a named-function list resolves duplicates by silent LAST-WINS (function
            // definition, the validator's scope, the generic-UI library merge) — and S1c's set-union merge
            // will produce duplicates routinely — so this refusal is load-bearing, exactly like type/prop
            // name uniqueness.
            if (!seenNames.Add(name))
                throw new SchemaValidationException(
                    $"Two structured functions are both named \"{name}\" — rename one; every resolution site " +
                    "silently keeps only the last-declared function with a given name.");

            var body = OrderedObjects(fn.Fields.GetValueOrDefault("body")).ToList();
            if (body.Count == 0)
                throw new SchemaValidationException($"Structured function \"{name}\" has no body.");
            if (body.Count > 1)
                throw new SchemaValidationException($"Structured function \"{name}\" has more than one body root.");
            // A `return` statement carries a VALUE (ICodeValue) — a for/if row is control flow, not a value,
            // so it cannot be a fn's body root (only render-tree CHILDREN may be for/if rows). Caught here
            // with a designer-facing message rather than an InvalidCastException from the ProjectNode cast below.
            if (TextField(body[0], "kind") is "for" or "if")
                throw new SchemaValidationException(
                    $"Structured function \"{name}\"'s body root cannot be a for/if row — a function body must " +
                    "return an element or an expression.");

            var parameters = TextField(fn, "params")
                .Split(',').Select(p => p.Trim()).Where(p => p.Length > 0)
                .Select(p => new CodeFunctionParam { Name = p }).ToArray();

            // M12 V1 — a fn's own `vars` (component-local state). Empty ⇒ the fn stays stateless (the
            // single-`return` shape, unchanged). Non-empty ⇒ the stateful canonical shape: each var
            // projects to a `CodeVarDec`, in `order`, followed by a nested `fn render()` wrapping the view
            // tree, followed by `return render` (a bare symbol reference — the setup/view split IS this
            // shape: the render closure captures the setup scope where the vars live, CodeExecutor.cs
            // ExecuteComponentValue). `render` is RESERVED inside a stateful fn's own scope for exactly the
            // reason it's reserved at the top level (above): ExecuteFunction unconditionally overwrites
            // scope.Items[Name], so a state var also named "render" would be silently clobbered when the
            // nested view fn is defined right after it.
            var varDecs = new List<CodeVarDec>();
            var seenVarNames = new HashSet<string>();
            foreach (var v in OrderedObjects(fn.Fields.GetValueOrDefault("vars")))
            {
                var varName = TextField(v, "name");
                if (varName is not { Length: > 0 })
                    throw new SchemaValidationException($"A state variable on structured function \"{name}\" has an empty name.");
                if (varName == "render")
                    throw new SchemaValidationException(
                        $"A state variable on structured function \"{name}\" cannot be named \"render\" — that name " +
                        "is reserved for the component's own projected view function; it would be silently " +
                        "overwritten when that function is defined.");
                if (!seenVarNames.Add(varName))
                    throw new SchemaValidationException(
                        $"Structured function \"{name}\" has two state variables both named \"{varName}\" — rename one.");
                var initSrc = TextField(v, "init");
                // An EMPTY init is legitimate — `var x` with no initializer is grammar-legal (CodeVarDec.Value
                // is nullable; CodeExecutor.ExecuteVarDec defaults it to ExecNull, a meaningful value, not an
                // error) — so it is NOT refused; it projects to a bare `var x` (AppPrint/CodePrint already
                // print a null Value with no `= …`).
                varDecs.Add(new CodeVarDec { Name = varName, Value = initSrc.Length > 0 ? CodeParse.ParseExpression(initSrc) : null });
            }

            // body[0]'s kind is "" (guarded above), so ProjectNode yields either a CodeTag or a parsed
            // expression — both ICodeValue, the value a `return` carries.
            var view = (ICodeValue)ProjectNode(body[0]);
            ICodeStatement[] bodyStatements;
            if (varDecs.Count > 0)
            {
                var viewFn = new CodeFunction
                {
                    Name = "render", Params = [],
                    Body = new CodeBlock { Statements = [new CodeReturn { Value = view }] },
                };
                bodyStatements = [.. varDecs, viewFn, new CodeReturn { Value = new CodeSymbol { Name = "render" } }];
            }
            else
                bodyStatements = [new CodeReturn { Value = view }];

            functions.Add(new CodeFunction { Name = name, Params = parameters, Body = new CodeBlock { Statements = bodyStatements } });
        }

        // M12 V1 — `design.vars` (top-level `ui var`s) → InstanceUi.Vars, in `order`. Checked AFTER the fns
        // loop above so `seenNames` already holds every design-level fn name: SsrRenderer defines fns into
        // the app/lib scope FIRST, then assigns vars into the SAME scope UNCONDITIONALLY (`target.Items
        // [v.Name] = …`, no existence check) — a top-level var sharing a fn's name would silently clobber
        // that fn's binding, the same silent-last-wins class the fn-name-collision refusals above guard.
        var vars = new List<UiVar>();
        var seenTopVarNames = new HashSet<string>();
        foreach (var v in OrderedObjects(design.Fields.GetValueOrDefault("vars")))
        {
            var varName = TextField(v, "name");
            if (varName is not { Length: > 0 })
                throw new SchemaValidationException("A design-level state variable has an empty name.");
            if (!seenTopVarNames.Add(varName))
                throw new SchemaValidationException(
                    $"Two design-level state variables are both named \"{varName}\" — rename one.");
            if (seenNames.Contains(varName))
                throw new SchemaValidationException(
                    $"A design-level state variable is named \"{varName}\", the same as a structured function — " +
                    "rename one; both would land in the same top-level scope and the function's binding would be " +
                    "silently overwritten.");
            var initSrc = TextField(v, "init");
            vars.Add(new UiVar(varName, initSrc.Length > 0 ? CodeParse.ParseExpression(initSrc) : null));
        }

        var render = new CodeFunction
        {
            Name = "render",
            Params = [],
            // The root is guaranteed an element (the tag guard above), so ProjectNode yields a CodeTag,
            // which is an ICodeValue — the value a `return` carries.
            Body = new CodeBlock { Statements = [new CodeReturn { Value = (ICodeValue)ProjectNode(root) }] },
        };
        return new InstanceUi(
            Vars: vars.Count > 0 ? vars : null,
            Functions: functions.Count > 0 ? functions : null,
            Render: render);
    }

    // Project one MetaNode ObjectValue → an ICodeTagChild, dispatching on `kind` (S6a): "for" → a
    // CodeTagForEach (`item`/`collection` + the `children` body); "if" → a CodeTagIf (`condition` + the
    // `children` then-branch + the `elseChildren` else-branch); "" (legacy) → the tag/expr discrimination
    // an element (tag non-empty) → CodeTag with its attributes and children projected in `order`; a leaf
    // (tag empty) → its `expr` source parsed as an expression (a string-literal source like "\"Hi\"" parses
    // to CodeText, so a text child is just an expr). Every projected form flows through the UNCHANGED print
    // → parse pipeline (CodePrint emits the canonical `foreach`/`if` text the existing parser accepts — no
    // grammar/printer change). Recurses directly on child ObjectValues — already resolved inline by the store.
    private static ICodeTagChild ProjectNode(ObjectValue node)
    {
        switch (TextField(node, "kind"))
        {
            case "for":
            {
                // A loop needs both a variable name and a collection expression; empty either ⇒ refuse with
                // a designer-facing message (the S1a empty-guard precedent), not a raw parser error on "".
                var item = TextField(node, "item");
                if (item is not { Length: > 0 })
                    throw new SchemaValidationException(
                        "A structured `for` render row has an empty loop variable (`item`).");
                var collection = TextField(node, "collection");
                if (collection is not { Length: > 0 })
                    throw new SchemaValidationException(
                        "A structured `for` render row has an empty `collection` expression.");
                return new CodeTagForEach
                {
                    Item = new CodeSymbol { Name = item },
                    Collection = CodeParse.ParseExpression(collection),
                    Body = ProjectChildren(node, "children"),
                };
            }
            case "if":
            {
                var condition = TextField(node, "condition");
                if (condition is not { Length: > 0 })
                    throw new SchemaValidationException(
                        "A structured `if` render row has an empty `condition` expression.");
                return new CodeTagIf
                {
                    Condition = CodeParse.ParseExpression(condition),
                    Body = ProjectChildren(node, "children"),
                    // An empty `elseChildren` set projects to an empty ElseBody — the printer emits no `else`.
                    ElseBody = ProjectChildren(node, "elseChildren"),
                };
            }
        }

        var tag = TextField(node, "tag");
        if (tag is not { Length: > 0 })
        {
            // A leaf: `expr` is the child expression source. Empty ⇒ neither an element (no `tag`) nor an
            // expression — refuse with a designer-facing message rather than a raw parser error on "".
            var expr = TextField(node, "expr");
            if (expr is not { Length: > 0 })
                throw new SchemaValidationException(
                    "A structured render node has neither a `tag` (an element) nor an `expr` (a leaf expression).");
            return CodeParse.ParseExpression(expr);
        }

        var attrs = OrderedObjects(node.Fields.GetValueOrDefault("attrs")).Select(a =>
        {
            var attrName = TextField(a, "name");
            // An attribute value is an expression source; empty ⇒ refuse with a clear message, not a raw
            // parse error on "" (the malformed-but-non-empty case still surfaces as a CodeParseException —
            // ledgered for the authoring slice, which can point at the offending node).
            if (TextField(a, "value") is not { Length: > 0 } value)
                throw new SchemaValidationException(
                    $"A structured render attribute '{attrName}' on <{tag}> has an empty value expression.");
            return new CodeTagAttribute { Name = attrName, Value = CodeParse.ParseExpression(value) };
        }).ToArray();

        return new CodeTag { Name = tag, Attributes = attrs, Children = ProjectChildren(node, "children") };
    }

    // Project a MetaNode's child set (`children` or `elseChildren`) → an ordered ICodeTagChild array.
    private static ICodeTagChild[] ProjectChildren(ObjectValue node, string setProp) =>
        OrderedObjects(node.Fields.GetValueOrDefault(setProp)).Select(ProjectNode).ToArray();

    // ── M12 CANVAS-EVAL-1: collect the render tree's expression sources ───────────
    //
    // Walk a design's structured `render` tree (the MetaNode/MetaAttr rows) AND every `fns` row's body (M12
    // F2 — a component/helper's own leaf/attr/collection/condition sources need ASTs for expansion too, and
    // the invocation SITE's attrs — e.g. `<NoteCard note={n}/>` — are already collected as ordinary attrs of
    // an ordinary element row, wherever that row lives) and return every SOURCE TEXT, in walk order (deduping
    // is the caller's job — it content-addresses by text). The eval-context compute parses + serializes each
    // into the AST map the canvas walk consumes. This is the ProjectNode walk shape, but it COLLECTS source
    // text instead of projecting to an AST — so a source that won't parse is still returned here (the caller
    // drops it, and the canvas chips it) rather than throwing. Literal sources ("box"/2/true) are returned
    // too — harmless, since the canvas walk resolves a literal leaf/attr BEFORE consulting the map, so their
    // (unused) entries are never looked up. Empty ⇒ no structured render (a text-mode / generic-UI design) →
    // no sources.
    //
    // M12 V1 — also collects every MetaVar `init` source: `design.vars` (top-level `ui var`s) AND each
    // `fns` row's OWN `vars` (component-local state). A var's init is an ordinary expression source exactly
    // like a leaf/attr's — it needs an AST for the same reasons (F3 call-position evaluation of a stateful
    // fn's projected body). At V1 landing time the canvas walk itself never read `vars` at all (ExpandFn's
    // then-stated behavior), so collecting the source was "harmless either way" — the same "collect broadly,
    // the walk decides what to look up" stance the rest of this collector already takes for literal sources.
    // M12 V1b made this LOAD-BEARING: BindVars/bindVars now binds each var's init to its evaluated value at
    // the walk root and at ExpandFn/expandFn, so a real design's var inits (including plain literals like
    // `0` — BindVars has no literal shortcut, unlike a param's LiteralValue tier-0) depend on this collector
    // actually shipping their sources; a hand-built conformance fixture must add its own `ctx.exprs` entries.
    //
    // M12 U1 — also collects every MetaUse's `args` VALUE sources (`fn.uses`, each a stored sample
    // invocation of that component: name + args set of MetaAttr, exactly the shape an ordinary invocation
    // row's attrs already have). The workbench's static preview feeds `use.args` DIRECTLY as the synthesized
    // invocation node's `attrs` (SchemaBridge does not project a use anywhere — it never reaches the app
    // document; only the canvas walk, via `sys.renderTree`, ever reads it) — so a NON-literal arg value is
    // exactly like an ordinary attr's value: it needs an AST in `ctx.exprs` or it MISSES on first paint
    // (tier-3, chip) until an edit re-triggers the client-side auto-live parse-op. WITHOUT this the initial
    // ship would ALWAYS miss every non-literal use-arg — forever, until the operator edits it — so this is
    // load-bearing, not "harmless either way" like the literal-source over-collection elsewhere in this walk.
    public static List<string> RenderExprSources(NodeValue design)
    {
        var sources = new List<string>();
        if (design is not ObjectValue d) return sources;
        if (d.Fields.TryGetValue("render", out var r) && OrderedObjects(r).FirstOrDefault() is { } root)
            CollectExprSources(root, sources);
        if (d.Fields.TryGetValue("fns", out var fnsField))
            foreach (var fn in OrderedObjects(fnsField))
            {
                if (fn.Fields.TryGetValue("body", out var bodyField) && OrderedObjects(bodyField).FirstOrDefault() is { } bodyRoot)
                    CollectExprSources(bodyRoot, sources);
                CollectVarInitSources(fn, sources);
                CollectUseArgSources(fn, sources);
            }
        CollectVarInitSources(d, sources);
        return sources;
    }

    // The `init` source of every MetaVar in `owner.vars` (a Design or a MetaFn — both carry a `vars` set),
    // in walk order. Shared by RenderExprSources' two call sites (design-level + per-fn).
    private static void CollectVarInitSources(ObjectValue owner, List<string> into)
    {
        foreach (var v in OrderedObjects(owner.Fields.GetValueOrDefault("vars")))
            if (TextField(v, "init") is { Length: > 0 } init) into.Add(init);
    }

    // The `value` source of every MetaAttr in every MetaUse's `args` set (`fn.uses`), in walk order (M12
    // U1). Each MetaUse's args are ordinary MetaAttr rows — same shape, same collection rule as an
    // invocation row's `attrs` — so this is a flat one-level walk, no recursion needed (a use's args never
    // nest further).
    private static void CollectUseArgSources(ObjectValue fn, List<string> into)
    {
        foreach (var use in OrderedObjects(fn.Fields.GetValueOrDefault("uses")))
            foreach (var a in OrderedObjects(use.Fields.GetValueOrDefault("args")))
                if (TextField(a, "value") is { Length: > 0 } value) into.Add(value);
    }

    // NOTE: this is a hand-kept PARALLEL walk of one node tree (a render root OR a fn body root — the caller,
    // RenderExprSources, invokes it once per root) — its `kind` dispatch (for → collect `collection` +
    // recurse `children`; if → collect `condition` + recurse `children` AND `elseChildren`; "" → tag-non-empty
    // element with attrs+children, else expr leaf) MUST mirror the canvas walk (CodeExecutor.BuildRenderTree /
    // codeExec.ts renderTreeNode) so it can never UNDER-collect a source the walk will look up (over-collecting
    // dead entries is harmless — content-addressed + parse-or-skip). The easy-to-forget branch is
    // `elseChildren` — a collector-invariant test pins that every source the canvas evaluates (including one
    // inside an else branch) is collected. A component INVOCATION row (`<NoteCard note={n}/>`, M12 F2) needs
    // no special case here: it is an ordinary tag-non-empty element (attrs + the — always empty, per F2 —
    // children), so its attr sources are already collected by the SAME tag branch, wherever the invocation
    // row lives (a render tree or another fn's body) — a second collector-invariant test pins that a source
    // reachable ONLY via a fn body (never in `render`) is still collected. A MetaUse's `args` (M12 U1) is
    // NOT a node this walk ever visits — a use never appears IN a render/fn-body tree, it is a separate
    // sample-invocation row fed straight to `sys.renderTree` as a synthesized node's `attrs` — so its
    // sources are collected by the SEPARATE flat `CollectUseArgSources` above, called once per `fns` row
    // alongside this walk (RenderExprSources), not by recursing into this function. If the walk's shape
    // changes, change this in the SAME slice (S6a lifted the for/if rows here in lockstep with the canvas
    // walk; F2 pointed the caller at every `fns` body root too, in lockstep with the canvas's own expansion;
    // U1 added the sibling use-args walk for the same reason).
    private static void CollectExprSources(ObjectValue node, List<string> into)
    {
        switch (TextField(node, "kind"))
        {
            case "for":
                if (TextField(node, "collection") is { Length: > 0 } coll) into.Add(coll);
                foreach (var c in OrderedObjects(node.Fields.GetValueOrDefault("children")))
                    CollectExprSources(c, into);
                return;
            case "if":
                if (TextField(node, "condition") is { Length: > 0 } cond) into.Add(cond);
                foreach (var c in OrderedObjects(node.Fields.GetValueOrDefault("children")))
                    CollectExprSources(c, into);
                foreach (var c in OrderedObjects(node.Fields.GetValueOrDefault("elseChildren")))
                    CollectExprSources(c, into);
                return;
        }

        if (TextField(node, "tag") is { Length: > 0 })
        {
            foreach (var a in OrderedObjects(node.Fields.GetValueOrDefault("attrs")))
                if (TextField(a, "value") is { Length: > 0 } value) into.Add(value);
            foreach (var c in OrderedObjects(node.Fields.GetValueOrDefault("children")))
                CollectExprSources(c, into);
        }
        else if (TextField(node, "expr") is { Length: > 0 } expr)
            into.Add(expr);
    }

    // ── M12 F3b: per-fn content fingerprints (the staleness banner) ────────────────────
    //
    // A canonical STRING built from a MetaFn row's raw fields (name, params, and its body tree walked
    // in canonical order: kind/tag/expr/item/collection/condition/order per node, name/value/order per
    // attr) — NOT a hash: only ever compared for equality, so plain concatenation is sufficient and
    // avoids implementing a hash function identically on two languages. The eval context ships one of
    // these per fn (computed HERE, from the raw store rows, at ctx-build time — SsrRenderer.
    // BuildEvalContext); the canvas walk recomputes the SAME fingerprint from the LIVE fns rows
    // (CodeExecutor.FnFingerprint / codeExec.ts fnFingerprint — a dep-recorded PARALLEL walk of this
    // one, the SAME "must mirror" law as RenderExprSources/CollectExprSources above) and a mismatch
    // shows the "components changed" banner (M12 F3). Keyed by name (last-wins on a duplicate — every
    // other resolver in this file/the canvas walk already tie-breaks or refuses duplicates the same way).
    // REWORDED (M12 V1 — the original wording scoped this to "fields the render walk reads", which a
    // MetaFn's `vars` were NOT at the time: ExpandFn's canvas expansion never read `vars` at all, per its
    // then-stated behavior — V1b closed that gap (ExpandFn/BindVars now bind them), so this is no longer
    // even the narrower true fact, but the broader obligation below always covered `vars` regardless).
    // The correct, broader obligation: the fingerprint MUST cover every field that affects the
    // fn's PROJECTED/EVALUATED behavior — everything ProjectRenderUi folds into the fn's assembled
    // CodeFunction (which BuildEvalContext then serializes and ships as ctx.fns for F3 call-position
    // evaluation), not merely what the display-inert canvas walk happens to read. `vars` qualifies: a
    // stateful fn's vars are part of its projected body (the CodeVarDec statements), so an edited init
    // changes what a call-position evaluation of that fn would produce, even though no canvas EXPANSION
    // ever looks at them. A future MetaNode/MetaAttr/MetaVar field added to either the render walk OR
    // projection but not here would make the staleness comparison silently UNDER-detect (a real content
    // change nothing would flag). Keep the three walks (here, CodeExecutor.FnFingerprint, codeExec.ts
    // fnFingerprint) in the SAME slice as any such field addition, the collector-law pattern
    // RenderExprSources/CollectExprSources set.
    // Field/node separators for the fingerprint string — control characters that never appear in
    // authored text, so they can't be confused with real content. Twin-identical: CodeExecutor.cs /
    // codeExec.ts use the SAME two code points (1 and 2).
    private static readonly char FpFieldSep = (char)1;
    private static readonly char FpNodeSep = (char)2;

    // An UNNAMED fn (F1's "+ Component" mid-authoring mint — `name:""` — the NORMAL state before the
    // operator types a name, not an error) is SKIPPED here: it has no call sites anywhere (a call needs
    // a name to resolve against), so it cannot make any call result stale. This must stay symmetric with
    // the fact that ctx.fns (BuildEvalContext) can never ship an unnamed entry either — an unnamed fn
    // also blocks the WHOLE design's projection (ProjectRenderUi's own empty-name refusal), degrading
    // ctx to empty — and with FnsStale/fnsStale's OWN skip on the LIVE side (CodeExecutor.cs / codeExec.
    // ts): without it, the live set would carry an entry ctx.fns structurally never can, so the
    // comparison would mismatch on EVERY render (including right after Refresh, since a rebuilt ctx
    // still can't ship the unnamed row) — a staleness banner Refresh can never clear, contradicting the
    // affordance's own contract.
    public static Dictionary<string, string> FnFingerprints(NodeValue design)
    {
        var result = new Dictionary<string, string>();
        if (design is not ObjectValue d) return result;
        foreach (var fn in OrderedObjects(d.Fields.GetValueOrDefault("fns")))
        {
            var name = TextField(fn, "name");
            if (name.Length == 0) continue;
            var body = OrderedObjects(fn.Fields.GetValueOrDefault("body")).FirstOrDefault();
            // M12 V1 — fold `vars` (name/init/order) in right after `params`, mirroring an element node's own
            // attrs segment below. A fn with NO vars contributes NOTHING here, so this is byte-identical to
            // the pre-V1 fingerprint for every existing (vars-less) fn.
            var varsPart = new StringBuilder();
            foreach (var v in OrderedObjects(fn.Fields.GetValueOrDefault("vars")))
                varsPart.Append(FpNodeSep).Append("V:").Append(TextField(v, "name")).Append(FpFieldSep)
                    .Append(TextField(v, "init")).Append(FpFieldSep).Append(IntField(v, "order"));
            result[name] = name + FpFieldSep + TextField(fn, "params") + varsPart + FpFieldSep +
                (body != null ? FingerprintNode(body) : "");
        }
        return result;
    }

    // The per-node half of FnFingerprints — twin of CodeExecutor.FingerprintNode / codeExec.ts
    // fingerprintNode. Dispatches on `kind` FIRST (mirroring the canvas walk's own dispatch) so it never
    // reads a field a for/if/element/leaf row doesn't carry.
    private static string FingerprintNode(ObjectValue node)
    {
        var kind = TextField(node, "kind");
        var sb = new StringBuilder();
        if (kind == "for")
        {
            sb.Append("for").Append(FpFieldSep).Append(TextField(node, "item")).Append(FpFieldSep)
              .Append(TextField(node, "collection"));
            foreach (var c in OrderedObjects(node.Fields.GetValueOrDefault("children")))
                sb.Append(FpNodeSep).Append("C:").Append(FingerprintNode(c));
            return sb.ToString();
        }
        if (kind == "if")
        {
            sb.Append("if").Append(FpFieldSep).Append(TextField(node, "condition"));
            foreach (var c in OrderedObjects(node.Fields.GetValueOrDefault("children")))
                sb.Append(FpNodeSep).Append("C:").Append(FingerprintNode(c));
            foreach (var c in OrderedObjects(node.Fields.GetValueOrDefault("elseChildren")))
                sb.Append(FpNodeSep).Append("E:").Append(FingerprintNode(c));
            return sb.ToString();
        }
        var tag = TextField(node, "tag");
        if (tag.Length > 0)
        {
            sb.Append("E:").Append(tag);
            foreach (var a in OrderedObjects(node.Fields.GetValueOrDefault("attrs")))
                sb.Append(FpNodeSep).Append("A:").Append(TextField(a, "name")).Append(FpFieldSep)
                  .Append(TextField(a, "value")).Append(FpFieldSep).Append(IntField(a, "order"));
            foreach (var c in OrderedObjects(node.Fields.GetValueOrDefault("children")))
                sb.Append(FpNodeSep).Append("C:").Append(FingerprintNode(c));
            return sb.ToString();
        }
        return "L:" + TextField(node, "expr");
    }

    // ── M12 V1: detect the stateful setup/view canonical statement shape ──────────────
    //
    // Confirmed against reality at build time (per the slice's mandate): parsed every stateful component in
    // the designer's OWN `designEditor` (instances/1/app.deenv) and every stateful component in GenericUi's
    // library (ConfirmButton, KebabMenu, ConflictBar, LoginForm, … — TWELVE `return render`/`return view`
    // sites, ALL of them) through ParseUiSection. Every one uses the IDENTICAL shape:
    //     var v1 = …            (zero or more state vars — CodeVarDec)
    //     fn render()           (a nested, UNPARAMETERIZED named function — CodeFunction { Name: "render" })
    //         return <view>     (its own body is a single `return`, exactly the stateless-fn shape)
    //     return render         (a bare symbol reference to the just-declared "render" — CodeReturn { Value:
    //                              CodeSymbol { Name: "render" } })
    // This is NOT an artifact of the design-doc's guess (`return c => <view>`, a lambda returned directly) —
    // that shape parses (MultilineLambda) but is used NOWHERE in the real codebase; every stateful component
    // instead declares a NAMED nested `render` and returns the symbol. Both are runtime-equivalent (a named
    // function still evaluates to a closure value, CodeExecutor.ExecuteFunction binds it before the return
    // reads it), so only the shape actually written is imported this slice — the direct-lambda-return form
    // stays refused (as it already was pre-V1), reported rather than silently guessed at.
    //
    // A component with EXTRA statements beyond vars/render/return — most commonly an additional named HELPER
    // function used as an event handler (GenericUi's ConfirmButton declares `doConfirm`, KebabMenu declares
    // `close`) — does NOT match: MetaVar has a row for a state VAR, not a nested helper FUNCTION, so there is
    // nowhere to carry it. Those specific components (a real, non-hypothetical fraction of the library) stay
    // `ui` text this slice — reported in the slice's own findings, not silently smoothed over.
    //
    // Review note (arch): the ZERO-var degenerate case (`fn render() return <view>` / `return render`, no
    // `var` at all) also matches — it imports as MetaFn.vars=[] and therefore PROJECTS BACK as the plain
    // STATELESS single-`return` shape (ProjectRenderUi's `varDecs.Count > 0` branch), not the nested-render
    // form it was written in. So project∘import is NOT byte-identity for that one input — a behavior-
    // preserving simplification (the nested render()/return-render indirection was inert with no vars to
    // close over), never a data-loss one. import∘project (the law this slice's tests actually pin) still
    // holds: re-importing the simplified stateless shape is the identity from there on.
    private static bool TryMatchStatefulShape(ICodeStatement[] statements, out List<CodeVarDec> vars, out CodeReturn viewReturn)
    {
        vars = [];
        viewReturn = null!;
        var i = 0;
        while (i < statements.Length && statements[i] is CodeVarDec v) { vars.Add(v); i++; }
        if (statements.Length - i != 2) return false;
        if (statements[i] is not CodeFunction { Name: "render", Params.Length: 0 } viewFn) return false;
        if (viewFn.Body.Statements is not [CodeReturn vr]) return false;
        if (statements[i + 1] is not CodeReturn { Value: CodeSymbol { Name: "render" } }) return false;
        viewReturn = vr;
        return true;
    }

    // ── M12 S1b: `ui` render text → structured MetaNode rows (the inverse of ProjectRenderUi) ─────
    //
    // Import a design authored as `ui` TEXT (a custom `fn render()`) INTO the structured MetaNode tree
    // (Design.render), then CLEAR the `ui` text field so the S1a precedence gate passes and the design
    // now projects its `ui` section FROM `render`. Import then project is the IDENTITY on the render
    // (modulo canonical formatting): ProjectDesignDb(after import) ≡ canonicalize(original `ui`).
    //
    // This is a ONE-TIME FRESH MINT (AdoptInto-style — new ids, no re-import identity matching): the
    // design must currently carry a `ui` render fn and an EMPTY `render` set. `foreach`/`if` render forms
    // import to structured `kind="for"`/`kind="if"` rows (S6a); top-level `var`s and a fn's stateful
    // var+nested-render shape both import too (M12 V1, above) — anything else (a fn with helper statements
    // beyond that shape) still refuses the WHOLE import. Component tags (PascalCase) and html tags are BOTH
    // just MetaNode {tag=Name}; neither is special-cased.
    //
    // ATOMIC: the whole import is ONE store.CommitBatch — all creates + links + the `ui` clear persist
    // all-or-none (the store mints, links, and Saves ONCE). A mid-import crash can therefore never leave a
    // design with partial `render` rows AND a non-empty `ui` (the bricked state ProjectDesignDb's S1a
    // precedence gate refuses). Every refusal below is checked BEFORE the batch is built, so a refusal
    // builds and commits NOTHING. Behind IInstanceStore in the model's terms — never a flat kv or file write.
    public static void ImportRender(IInstanceStore store, int designId)
    {
        var designPath = NodePath.Root.Field("designs").Key(designId.ToString());
        if (store.ReadNode(designPath) is not ObjectValue design)
            throw new SchemaValidationException($"No design with id {designId} to import.");

        // The render tree must be empty (fresh mint only) and the `ui` text must carry a render fn.
        if (design.Fields.GetValueOrDefault("render") is SetValue existing && existing.Members.Count > 0)
            throw new SchemaValidationException(
                "This design already has a structured `render` tree; re-import (identity matching) is not supported yet.");

        var uiText = TextField(design, "ui");
        if (uiText.Length == 0)
            throw new SchemaValidationException("This design has no `ui` render text to import.");

        var ui = CodeParse.ParseUiSection(uiText);

        var render = ui.Render
            ?? throw new SchemaValidationException(
                "This design's `ui` section has no `fn render()` to import (a generic-UI design has no render tree).");

        // The render body must be a single `return <element>` — the exact shape ProjectRenderUi mints and
        // a canonical custom render carries. Anything else (helper statements, a non-element return) is
        // outside the plain-tag subset this slice imports; it stays as `ui` text.
        if (render.Body.Statements is not [CodeReturn { Value: CodeTag root }])
            throw new SchemaValidationException(
                "This design's `fn render()` is not a single `return <element>` — only a plain tag tree can be imported.");

        // M12 F1/V1 — every top-level named function (besides `render`) imports to a MetaFn row, but ONLY
        // when it is a "structured-safe" shape: either the plain single-`return` (a helper or stateless
        // component) OR the STATEFUL setup/view shape (M12 V1 — see TryMatchStatefulShape). Checked for
        // EVERY fn BEFORE any create is built (the whole-import all-or-nothing law) — a design with even
        // one unsupported function stays entirely as `ui` text, never a partial import.
        var fns = ui.Functions ?? [];
        var statefulShapes = new (List<CodeVarDec> Vars, CodeReturn ViewReturn)?[fns.Count];
        var seenImportNames = new HashSet<string>();
        for (var fi = 0; fi < fns.Count; fi++)
        {
            var fn = fns[fi];
            // MetaFn carries no server-only flag — projecting it back would silently SHIP a server-only
            // function to the client, exactly the downgrade ServerOnly exists to prevent. It stays as `ui`
            // text until MetaFn can carry the flag.
            if (fn.ServerOnly)
                throw new SchemaValidationException(
                    $"This design's `ui` section has a server-only function \"{fn.Name}\", which import cannot " +
                    "carry to structured form yet — it would ship to the client. It stays as `ui` text.");

            if (TryMatchStatefulShape(fn.Body.Statements, out var stateVars, out var viewReturn))
            {
                // M12 V1 — a state var needs a unique name (within this fn) and must not be named "render":
                // that name is reserved for the nested view fn this shape always carries (ExecuteFunction
                // unconditionally overwrites scope.Items[Name], so a same-named var would be silently
                // clobbered) — the same "a shape that imports must project" symmetry the projection-side
                // refusals (ProjectRenderUi) enforce in the other direction.
                var seenVarNames = new HashSet<string>();
                foreach (var v in stateVars)
                {
                    if (v.Name == "render")
                        throw new SchemaValidationException(
                            $"This design's `ui` section has a function \"{fn.Name}\" with a state variable " +
                            "named \"render\", which import cannot carry to structured form (that name is " +
                            "reserved for the component's own nested view function) — it stays as `ui` text.");
                    if (!seenVarNames.Add(v.Name))
                        throw new SchemaValidationException(
                            $"This design's `ui` section has a function \"{fn.Name}\" with two state variables " +
                            $"both named \"{v.Name}\", which import cannot carry to structured form — it stays " +
                            "as `ui` text.");
                }
                statefulShapes[fi] = (stateVars, viewReturn);
            }
            else
            {
                if (fn.Body.Statements is not [CodeReturn ret])
                    throw new SchemaValidationException(
                        $"This design's `ui` section has a function \"{fn.Name}\" whose body is not a single " +
                        "`return` statement (and not the supported var-state/nested-render() component shape), " +
                        "which import cannot carry to structured form yet — it stays as `ui` text.");
                // A lambda-returning fn (`return c => …`, no named nested `render()`) is a stateful setup/view
                // component in a shape this slice does not import (only the nested-`fn render()` shape is
                // supported — see TryMatchStatefulShape's comment for why). It stays as `ui` text.
                if (ret.Value is CodeFunction)
                    throw new SchemaValidationException(
                        $"This design's `ui` section has a function \"{fn.Name}\" that returns a lambda directly " +
                        "(a stateful setup/view component in a shape import does not support — only the " +
                        "`var …` / `fn render()` / `return render` shape is), which import cannot carry to " +
                        "structured form yet — it stays as `ui` text.");
                statefulShapes[fi] = null;
            }

            // Review fix (arch, should-fix): MapUi (CodeParse.cs) APPENDS every non-render function without
            // deduping, so two `fn foo()` in `ui` text import fine today — minting two MetaFn rows, clearing
            // the SOURCE text — and only THEN does projection's own seenNames check refuse them, leaving a
            // design that imported successfully but can never project back, with the original text already
            // gone. Refuse the WHOLE import instead (same seenNames/HashSet idiom ProjectRenderUi uses).
            if (fn.Name != null && !seenImportNames.Add(fn.Name))
                throw new SchemaValidationException(
                    $"This design's `ui` section has two functions both named \"{fn.Name}\", which import " +
                    "cannot carry to structured form (every structured function needs a unique name) — it " +
                    "stays as `ui` text.");
        }

        // M12 V1 — top-level `ui var`s import to Design.vars rows. Each needs a unique name; a name
        // colliding with a top-level fn name would collide in the SAME scope both bind into (SsrRenderer
        // defines fns FIRST, then assigns vars UNCONDITIONALLY into the same scope — the symmetric
        // projection-side refusal, ProjectRenderUi, catches this in the other direction).
        var seenImportVarNames = new HashSet<string>();
        foreach (var v in ui.Vars ?? [])
        {
            if (!seenImportVarNames.Add(v.Name))
                throw new SchemaValidationException(
                    $"This design's `ui` section has two top-level `var`s both named \"{v.Name}\", which " +
                    "import cannot carry to structured form — it stays as `ui` text.");
            if (seenImportNames.Contains(v.Name))
                throw new SchemaValidationException(
                    $"This design's `ui` section has a top-level `var` named \"{v.Name}\", the same as a " +
                    "function — both would land in the same top-level scope and the function's binding would " +
                    "be silently overwritten, which import cannot carry to structured form — it stays as `ui` text.");
        }

        // Build the whole changeset: a CommitCreate per MetaNode/MetaAttr keyed by a distinct NEGATIVE
        // tempId, and mutations that link each child into its parent's `children`/`elseChildren` set, each
        // attr into its node's `attrs` set (all addressed by (owner-tempId, prop) so a child can link into
        // its just-minted parent within the ONE batch), the root into the EXISTING Design's `render` set,
        // and a field-write clearing the design's `ui` text. store.CommitBatch mints + links + Saves ONCE.
        var creates = new List<CommitCreate>();
        var mutations = new List<CommitMutation>();
        var nextTempId = -1;
        // Link a body of tag children into `owner.setProp` in `order`, minting each child first (top-down:
        // the parent is already minted, so the store's GC never sweeps a transiently-unlinked child).
        void LinkBody(int owner, string setProp, IEnumerable<ICodeTagChild> body)
        {
            var order = 0;
            foreach (var c in body)
                mutations.Add(new SetLinkByPropMutation(owner, setProp, ImportNode(c, order++)));
        }
        int ImportNode(ICodeTagChild child, int order)
        {
            var tempId = nextTempId--;
            switch (child)
            {
                case CodeTagForEach forEach:
                    // A `for` row: kind="for" + the loop var + the collection source (CodePrint.Value is the
                    // printer's canonical fixpoint, so it round-trips through ParseExpression on projection).
                    // Its body is the `children` set. Unset tag/expr default to "" — a for row is neither an
                    // element nor a leaf.
                    creates.Add(new CommitCreate(tempId, "MetaNode", new ObjectValue(new Dictionary<string, NodeValue>
                    {
                        ["kind"] = new TextValue("for"),
                        ["item"] = new TextValue(forEach.Item.Name),
                        ["collection"] = new TextValue(CodePrint.Value(forEach.Collection)),
                        ["order"] = new IntValue(order),
                    })));
                    LinkBody(tempId, "children", forEach.Body);
                    return tempId;
                case CodeTagIf tagIf:
                    // An `if` row: kind="if" + the condition source; the then-branch is `children`, the
                    // else-branch is `elseChildren` (a SECOND semantic child-order set — empty ElseBody links
                    // nothing, so the row projects back with no `else`).
                    creates.Add(new CommitCreate(tempId, "MetaNode", new ObjectValue(new Dictionary<string, NodeValue>
                    {
                        ["kind"] = new TextValue("if"),
                        ["condition"] = new TextValue(CodePrint.Value(tagIf.Condition)),
                        ["order"] = new IntValue(order),
                    })));
                    LinkBody(tempId, "children", tagIf.Body);
                    LinkBody(tempId, "elseChildren", tagIf.ElseBody);
                    return tempId;
                case not CodeTag:
                    // A leaf: print its source back (the inverse of ParseExpression on import) — CodePrint.Value
                    // is the printer's canonical fixpoint, so a CodeText "Hi" round-trips to the source `"Hi"`.
                    var leaf = (ICodeValue)child;
                    creates.Add(new CommitCreate(tempId, "MetaNode", new ObjectValue(new Dictionary<string, NodeValue>
                    {
                        ["tag"] = new TextValue(""), ["expr"] = new TextValue(CodePrint.Value(leaf)), ["order"] = new IntValue(order),
                    })));
                    return tempId;
            }

            var tag = (CodeTag)child;
            creates.Add(new CommitCreate(tempId, "MetaNode", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["tag"] = new TextValue(tag.Name), ["expr"] = new TextValue(""), ["order"] = new IntValue(order),
            })));

            var attrOrder = 0;
            foreach (var attr in tag.Attributes)
            {
                var attrTempId = nextTempId--;
                creates.Add(new CommitCreate(attrTempId, "MetaAttr", new ObjectValue(new Dictionary<string, NodeValue>
                {
                    ["name"] = new TextValue(attr.Name),
                    ["value"] = new TextValue(CodePrint.Value(attr.Value)),
                    ["order"] = new IntValue(attrOrder++),
                })));
                mutations.Add(new SetLinkByPropMutation(tempId, "attrs", attrTempId));
            }

            LinkBody(tempId, "children", tag.Children);
            return tempId;
        }

        var rootTempId = ImportNode(root, order: 0);
        mutations.Add(new SetLinkByPropMutation(designId, "render", rootTempId));

        // M12 V1 — mint a MetaVar per state var, linked into `owner.vars` in order. Shared by both the
        // top-level `ui var`s (owner = the design) and a stateful fn's own vars (owner = its MetaFn) below.
        void LinkVars(int owner, IEnumerable<CodeVarDec> varDecs)
        {
            var order = 0;
            foreach (var v in varDecs)
            {
                var varTempId = nextTempId--;
                creates.Add(new CommitCreate(varTempId, "MetaVar", new ObjectValue(new Dictionary<string, NodeValue>
                {
                    ["name"] = new TextValue(v.Name),
                    // An uninitialized `var x` (Value == null) is grammar-legal and meaningful (ExecuteVarDec
                    // defaults it to ExecNull) — its init prints as "" (ProjectRenderUi's mirror: an empty
                    // init projects back to a bare `var x`, no `= …`).
                    ["init"] = new TextValue(v.Value != null ? CodePrint.Value(v.Value) : ""),
                    ["order"] = new IntValue(order++),
                })));
                mutations.Add(new SetLinkByPropMutation(owner, "vars", varTempId));
            }
        }

        // M12 V1 — top-level `ui var`s → Design.vars, in order (validated above).
        LinkVars(designId, ui.Vars?.Select(v => new CodeVarDec { Name = v.Name, Value = v.Value }) ?? []);

        // M12 F1/V1 — each validated top-level fn mints a MetaFn (name, params joined ", ", order = list
        // index) linked into the design's `fns`. A STATELESS fn's RETURN VALUE imports as its `body` root
        // via the SAME ImportNode used for the render tree (a fn's body root may be a leaf expression —
        // ImportNode already handles a non-CodeTag child as a leaf); a STATEFUL fn (M12 V1, TryMatchStateful
        // Shape matched) additionally mints its state vars into `vars`, and its `body` root is the NESTED
        // render fn's view value (not the outer `return render` symbol — that symbol is a projection
        // artifact ProjectRenderUi re-synthesizes, never stored). Top-down: the MetaFn is created before its
        // body/vars are linked in, same GC law as the render tree.
        var fnOrder = 0;
        for (var fi = 0; fi < fns.Count; fi++)
        {
            var fn = fns[fi];
            var fnTempId = nextTempId--;
            creates.Add(new CommitCreate(fnTempId, "MetaFn", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["name"] = new TextValue(fn.Name ?? ""),
                ["params"] = new TextValue(string.Join(", ", fn.Params.Select(p => p.Name))),
                ["order"] = new IntValue(fnOrder++),
            })));

            ICodeValue view;
            if (statefulShapes[fi] is { } shape)
            {
                view = shape.ViewReturn.Value;
                LinkVars(fnTempId, shape.Vars);
            }
            else
                view = ((CodeReturn)fn.Body.Statements[0]).Value; // shape validated above: [CodeReturn]

            mutations.Add(new SetLinkByPropMutation(fnTempId, "body", ImportNode(view, order: 0)));
            mutations.Add(new SetLinkByPropMutation(designId, "fns", fnTempId));
        }

        // Clear the `ui` text field so the S1a gate accepts the structured render as the authority — in the
        // SAME batch, so the rows and the cleared text land together (the atomicity that unbricks a crash).
        mutations.Add(new FieldWriteMutation(designId, "ui", new TextValue("")));

        store.CommitBatch(creates, mutations);
    }

    // Write an already-projected, already-validated app document onto a target instance, PRESERVING
    // its existing data across the schema change when the data still fits (non-destructive apply — the
    // migration substrate under M13 versioning; see DECISIONS "Data must survive schema changes").
    // Shared by publish/apply/create paths that already projected a whole Design row.
    //
    // Non-destructive apply — migrate-toward-then-preserve-or-reseed:
    //   • Migrate the existing data TOWARD the new schema (drop removed fields, …), then KEEP it when it
    //     fits — additive (a new prop reads its default) AND subtractive (a removed field is pruned)
    //     changes survive. This is the win: data survives a schema change.
    //   • No data yet (a fresh target), OR a change a slice cannot yet carry forward (a rename, a
    //     type/cardinality change, a wholesale different app) → reseed the new schema's initial
    //     document. Carrying those forward (value conversion, rename remap) is the follow-up slices that
    //     progressively replace this reseed; until then such an apply still resets, as it always has.
    public static void WriteDesign(string documentText, string targetAppPath, string targetDataPath)
    {
        var newDesc = InstanceDescriptionLoader.Load(documentText);
        File.WriteAllText(targetAppPath, documentText);

        var hasData = File.Exists(targetDataPath) && new FileInfo(targetDataPath).Length > 0;
        if (hasData)
        {
            // Carry the data forward (drop removed fields, convert type-changed scalars). Values that
            // could not be converted are reset to default and REPORTED here — non-silent, not corruption.
            var reset = JsonFileInstanceStore.MigrateTowardSchema(targetDataPath, newDesc);
            if (reset.Count > 0)
                Console.Error.WriteLine(
                    $"[non-destructive apply] {reset.Count} value(s) could not be converted to the new " +
                    $"type and were reset to default: {string.Join(", ", reset)}");
        }

        if (!(hasData && DataFits(targetDataPath, newDesc)))
        {
            // No prior data, or a change this slice cannot carry — drop any prior data and reseed.
            // Delete first because opening a store over incompatible data would trip the startup guard.
            File.Delete(targetDataPath);
            new JsonFileInstanceStore(targetDataPath, newDesc).Reset();
        }
    }

    // Whether the data file still satisfies the schema — i.e. opening a store over it passes the
    // startup guard (StoredDataValidator), which tolerates additive evolution (a newly declared prop
    // absent from stored data reads its default) and rejects removed/changed fields. A clean open means
    // the data carries forward unchanged; a StoredDataException (incompatible, or unreadable) means it
    // does not. The opened store is discarded — a successful open leaves a compatible file untouched.
    private static bool DataFits(string dataPath, InstanceDescription desc)
    {
        try
        {
            _ = new JsonFileInstanceStore(dataPath, desc);
            return true;
        }
        catch (StoredDataException)
        {
            return false;
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    // Member objects of a set node, sorted by the `order` field then by identity
    // (identity as a stable tiebreak / fallback when order is absent or equal).
    private static IEnumerable<ObjectValue> OrderedObjects(NodeValue? set) =>
        OrderedMembers(set).Select(m => m.Obj);

    // Same ordering as OrderedObjects (by `order` then by identity), but keeping each member's intrinsic
    // set-key id alongside its object — what Snapshot's id-map walk needs and OrderedObjects discards.
    private static IEnumerable<(int Id, ObjectValue Obj)> OrderedMembers(NodeValue? set)
    {
        if (set is not SetValue sv)
            return [];

        return sv.Members
            .Where(e => e.Value is ObjectValue)
            .Select(e => (Id: e.Key, Obj: (ObjectValue)e.Value, order: IntField((ObjectValue)e.Value, "order")))
            .OrderBy(x => x.order).ThenBy(x => x.Id)
            .Select(x => (x.Id, x.Obj));
    }

    private static string TextField(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is TextValue t ? t.Text : "";

    private static int IntField(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is IntValue i ? i.Value : 0;

    // A bool meta-field, defaulting false when absent — the same defensive read as TextField/IntField,
    // so a MetaProp that predates the `multiline` field (or any node missing it) reads false, not error.
    private static bool BoolField(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is BoolValue b && b.Value;
}
