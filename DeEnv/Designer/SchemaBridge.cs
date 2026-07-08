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
// `types` into TypeDefinitions; `ProjectDesignDocument` assembles the whole app
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
// unambiguous because ProjectDesignDocument's own validation already requires unique names. The map keys
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
    // walked over the SAME structure ProjectDesignDocument prints (types, each type's props), keeping the
    // member ids OrderedObjects/Project discard. Text is computed FIRST — ProjectDesignDocument validates
    // (types, then the whole assembled document) and THROWS SchemaValidationException on an invalid
    // design — so an invalid design yields no snapshot at all, not a partial id map. (Snapshot inherits
    // that validate-or-throw behavior; how a future sys.commitDesign surfaces "can't commit an invalid
    // design" to the caller is that slice's decision, not this one's.) Same design state → byte-identical
    // Text on every call: the printer is canonical and the verbatim sections are passed through unchanged.
    public static DesignSnapshot Snapshot(NodeValue design)
    {
        var text = ProjectDesignDocument(design); // throws on an invalid design — before the map is built
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
    public static string ProjectDesignDocument(NodeValue design)
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
        // common, exactly as AppParse.Document expects.
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
                    sections.Add(AppPrint.PrintUi(ProjectRenderUi(renderRoot[0])).TrimEnd('\n'));
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
    // only). Shared by ProjectDesignDocument (which adds the other sections) and the M4 tests.
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

    // ── M12 S1a: structured render tree → `ui` section ────────────────────────────
    //
    // Project the MetaNode tree rooted at `root` into an InstanceUi whose only content is `fn render()`
    // returning the root element — the exact shape a hand-written custom-UI design carries, so it flows
    // unchanged through the existing print→parse→run pipeline (NO interpreter/grammar change). The root
    // MUST be an element (a non-empty tag) — a `render` returning a bare text/expression leaf is not a
    // page. `root`'s `children`/`attrs` sets (and every descendant's) are already resolved inline on the
    // ObjectValue — the store builds them recursively at read, exactly like `types` — so no resolver.
    private static InstanceUi ProjectRenderUi(ObjectValue root)
    {
        if (TextField(root, "tag") is not { Length: > 0 })
            throw new SchemaValidationException(
                "A structured `render` root must be an element (a MetaNode with a non-empty `tag`), not a leaf expression.");

        var render = new CodeFunction
        {
            Name = "render",
            Params = [],
            Body = new CodeBlock { Statements = [new CodeReturn { Value = ProjectNode(root) }] },
        };
        return new InstanceUi(Render: render);
    }

    // Project one MetaNode ObjectValue → an ICodeValue (a tag child): an element (tag non-empty) → CodeTag
    // with its attributes and children projected in `order`; a leaf (tag empty) → its `expr` source parsed
    // as an expression (a string-literal source like "\"Hi\"" parses to CodeText, so a text child is just
    // an expr). Recurses directly on child ObjectValues — already resolved inline by the store.
    private static ICodeValue ProjectNode(ObjectValue node)
    {
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

        var children = OrderedObjects(node.Fields.GetValueOrDefault("children"))
            .Select(c => (ICodeTagChild)ProjectNode(c)).ToArray();

        return new CodeTag { Name = tag, Attributes = attrs, Children = children };
    }

    // ── M12 CANVAS-EVAL-1: collect the render tree's expression sources ───────────
    //
    // Walk a design's structured `render` tree (the MetaNode/MetaAttr rows) and return every leaf `expr` and
    // attr `value` SOURCE TEXT, in walk order (deduping is the caller's job — it content-addresses by text).
    // The eval-context compute parses + serializes each into the AST map the canvas walk consumes. This is
    // the ProjectNode walk shape, but it COLLECTS source text instead of projecting to an AST — so a source
    // that won't parse is still returned here (the caller drops it, and the canvas chips it) rather than
    // throwing. Literal sources ("box"/2/true) are returned too — harmless, since the canvas walk resolves a
    // literal leaf/attr BEFORE consulting the map, so their (unused) entries are never looked up. Empty ⇒ no
    // structured render (a text-mode / generic-UI design) → no sources.
    public static List<string> RenderExprSources(NodeValue design)
    {
        var sources = new List<string>();
        if (design is ObjectValue d && d.Fields.TryGetValue("render", out var r)
            && OrderedObjects(r).FirstOrDefault() is { } root)
            CollectExprSources(root, sources);
        return sources;
    }

    // NOTE: this is a hand-kept PARALLEL walk of the render tree — its branch condition (tag-non-empty =
    // element with attrs+children; else expr leaf) MUST mirror the canvas walk (CodeExecutor.BuildRenderTree /
    // codeExec.ts renderTreeNode) so it can never UNDER-collect a source the walk will look up (over-collecting
    // dead entries is harmless — content-addressed + parse-or-skip). If the walk's shape changes (S6 for…in/if
    // rows), change this in the same slice.
    private static void CollectExprSources(ObjectValue node, List<string> into)
    {
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

    // ── M12 S1b: `ui` render text → structured MetaNode rows (the inverse of ProjectRenderUi) ─────
    //
    // Import a design authored as `ui` TEXT (a custom `fn render()`) INTO the structured MetaNode tree
    // (Design.render), then CLEAR the `ui` text field so the S1a precedence gate passes and the design
    // now projects its `ui` section FROM `render`. Import then project is the IDENTITY on the render
    // (modulo canonical formatting): ProjectDesignDocument(after import) ≡ canonicalize(original `ui`).
    //
    // This is a ONE-TIME FRESH MINT (AdoptInto-style — new ids, no re-import identity matching): the
    // design must currently carry a `ui` render fn and an EMPTY `render` set. It refuses (throws, imports
    // nothing) a render whose tree contains a `for`/`if` tag form (CodeTagForEach/CodeTagIf) — those have
    // no structured shape yet (S6). Such a render stays as `ui` text. Component tags (PascalCase) and html
    // tags are BOTH just MetaNode {tag=Name}; neither is special-cased.
    //
    // ATOMIC: the whole import is ONE store.CommitBatch — all creates + links + the `ui` clear persist
    // all-or-none (the store mints, links, and Saves ONCE). A mid-import crash can therefore never leave a
    // design with partial `render` rows AND a non-empty `ui` (the bricked state ProjectDesignDocument's S1a
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

        // Import clears the WHOLE `ui` text but only carries the render TREE to structured form — so a `ui`
        // section that ALSO has `var`s or helper functions besides `fn render()` cannot be imported without
        // silently dropping them. Refuse it (import nothing); such a design stays as `ui` text until the
        // structured tree can carry vars/helpers (a later slice). This guards real designs (e.g. todo/crm,
        // whose `ui` carries helper fns alongside render) from losing code on import.
        if (ui.Vars is { Count: > 0 } || ui.Functions is { Count: > 0 })
            throw new SchemaValidationException(
                "This design's `ui` section has `var`s or helper functions besides `fn render()`, which import " +
                "cannot carry to structured form yet — it stays as `ui` text.");

        var render = ui.Render
            ?? throw new SchemaValidationException(
                "This design's `ui` section has no `fn render()` to import (a generic-UI design has no render tree).");

        // The render body must be a single `return <element>` — the exact shape ProjectRenderUi mints and
        // a canonical custom render carries. Anything else (helper statements, a non-element return) is
        // outside the plain-tag subset this slice imports; it stays as `ui` text.
        if (render.Body.Statements is not [CodeReturn { Value: CodeTag root }])
            throw new SchemaValidationException(
                "This design's `fn render()` is not a single `return <element>` — only a plain tag tree can be imported.");

        // Refuse a tree containing any `for`/`if` tag form ANYWHERE (no structured form yet — S6). Checked
        // BEFORE the batch is built so a refusal imports nothing.
        RefuseUnstructurableChildren(root);

        // Build the whole changeset: a CommitCreate per MetaNode/MetaAttr keyed by a distinct NEGATIVE
        // tempId, and mutations that link each child into its parent's `children` set, each attr into its
        // node's `attrs` set (both addressed by (owner-tempId, prop) so a child can link into its
        // just-minted parent within the ONE batch), the root into the EXISTING Design's `render` set, and
        // a field-write clearing the design's `ui` text. store.CommitBatch mints + links + Saves ONCE.
        var creates = new List<CommitCreate>();
        var mutations = new List<CommitMutation>();
        var nextTempId = -1;
        int ImportNode(ICodeTagChild child, int order)
        {
            var tempId = nextTempId--;
            if (child is not CodeTag tag)
            {
                // A leaf: print its source back (the inverse of ParseExpression on import) — CodePrint.Value
                // is the printer's canonical fixpoint, so a CodeText "Hi" round-trips to the source `"Hi"`.
                var leaf = (ICodeValue)child;
                creates.Add(new CommitCreate(tempId, "MetaNode", new ObjectValue(new Dictionary<string, NodeValue>
                {
                    ["tag"] = new TextValue(""), ["expr"] = new TextValue(CodePrint.Value(leaf)), ["order"] = new IntValue(order),
                })));
                return tempId;
            }

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

            var childOrder = 0;
            foreach (var c in tag.Children)
                mutations.Add(new SetLinkByPropMutation(tempId, "children", ImportNode(c, childOrder++)));

            return tempId;
        }

        var rootTempId = ImportNode(root, order: 0);
        mutations.Add(new SetLinkByPropMutation(designId, "render", rootTempId));
        // Clear the `ui` text field so the S1a gate accepts the structured render as the authority — in the
        // SAME batch, so the rows and the cleared text land together (the atomicity that unbricks a crash).
        mutations.Add(new FieldWriteMutation(designId, "ui", new TextValue("")));

        store.CommitBatch(creates, mutations);
    }

    // Throw (importing nothing) if the tree rooted at `tag` contains any `for`/`if` tag form — they have no
    // structured MetaNode shape yet (deferred to S6). A leaf child cannot contain tag children, so only
    // CodeTag children recurse.
    private static void RefuseUnstructurableChildren(CodeTag tag)
    {
        foreach (var child in tag.Children)
            switch (child)
            {
                case CodeTagForEach:
                case CodeTagIf:
                    throw new SchemaValidationException(
                        "This design's render uses a `for`/`if` render form, which has no structured shape yet — " +
                        "it stays as `ui` text (import supports a plain tag tree only).");
                case CodeTag childTag:
                    RefuseUnstructurableChildren(childTag);
                    break;
            }
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
    public static void WriteDocument(string documentText, string targetAppPath, string targetDataPath)
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
