using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Designer;

// The INVERSE of SchemaBridge's forward projection: build the operator IDE's `db.designs` seed from a
// set of committed app documents. Where SchemaBridge.ProjectDesignDb turns a `Design` node back
// INTO an app document (publish), this turns an app document INTO a `Design` — parsing its `types`
// section into the structured MetaType/MetaProp the type editor edits, and carrying its
// initialData/common/ui sections as VERBATIM source text (the exact representation SchemaBridge's three
// Design text fields expect, so the round-trip Design → app document → Design is faithful).
//
// It produces an InstanceInitialData (the friendly normalized extents the store seeds from on first
// run) so the kernel can seed the design-host's store through the normal seeding path
// (JsonFileInstanceStore's BuildSeededDb), with NO new storage write path — the seed is plain
// model-terms data, honoring the storage seam.
//
// CRITICAL: each Design's id is the caller-supplied designId (the instance's kernel.json `designId`),
// NOT a freshly-minted sequential id — so kernel.json's references (and future per-design version
// history) key off the SAME ids by construction. MetaType/MetaProp sub-ids are minted arbitrarily
// (not load-bearing) from a counter started ABOVE the highest designId, so they never collide with a
// Design id or each other.
//
// This is authoring-time-free: the duplicated escaped-string seed inside instances/1/app.app is gone —
// each app's own instances/<id>/app.app is the single source of truth, and the kernel reverse-projects
// it at first boot. A peer app whose initialData becomes a Design field is carried verbatim; the
// designer's OWN app document carries an empty initialData (it no longer embeds the design library), so
// reverse-projecting it yields a Design with empty initialData and there is no recursion.
//
// The kernel runs this on EVERY boot (KernelHost.SyncDesignHost): a FRESH store gets `Build` (every
// design seeded from its file, ONCE — this IS the adoption for a brand-new install). An EXISTING store
// never re-seeds via InitialData again (M13 slice 3 — the authority inversion: design-data + its commit
// history are the source of truth, an app file is a publish ARTIFACT, not authoritative input); instead
// each file-backed design NOT YET present is adopted individually via `AdoptInto`, through ordinary live
// store writes — never Reset, never truncating the log.
public static class DesignerSeed
{
    // Build the design-host's `db.designs` + `db.instances` seed from the given designs (each an
    // instance's display label, its kernel.json designId, and its committed app-document text) and the
    // given instance tuples (one per hosted instance: name, runtimeId, optional designId). Returns the
    // friendly InstanceInitialData the store seeds from: a single root Db holding both sets, plus the
    // Design / MetaType / MetaProp / Instance extents. The designs are emitted in the given order;
    // the root Db's `designs` set lists their ids in that order.
    public static InstanceInitialData Build(
        IReadOnlyList<(string Label, int DesignId, string AppText)> designs,
        IReadOnlyList<(string Name, int RuntimeId, int? DesignId)> instances)
    {
        var builder = new SeedBuilder(designs.Select(d => d.DesignId));
        var designIds = new List<int>();
        foreach (var (label, designId, appText) in designs)
            designIds.Add(builder.AddDesign(label, designId, appText));
        var instanceIds = new List<int>();
        foreach (var (name, runtimeId, designId) in instances)
            instanceIds.Add(builder.AddInstance(name, runtimeId, designId));
        builder.AddRootDb(designIds, instanceIds);
        return builder.Build();
    }

    // ── M13 slice 3: adopt ONE file-backed design directly into an ALREADY-OPEN, non-fresh store ──────
    //
    // The one-time-adoption half of the authority inversion: writes the design LIVE, through ordinary
    // IInstanceStore calls (CreateObject/AddToSet — the same primitives KernelHost's db.instances mirror
    // already uses), never through InitialData/Reset (which would re-freeze genesis and TRUNCATE the
    // designer's own log — exactly the boot-wipe this inversion ends). Unlike Build/Merge (which mint via
    // a JSON-pool SeedBuilder that can PIN an arbitrary caller-supplied id), a live store's CreateObject
    // always auto-mints through its own counter — there is no id-pinning write path, and adding one would
    // be an IInstanceStore shape change (out of scope for this slice; ask-before-structural-changes).
    // So an adopted design's Design-row id is WHATEVER the store mints, not the kernel.json designId that
    // named it — the caller (KernelHost.SyncDesignHost) must rewrite that registry entry's designId to
    // match afterward, so future resolution (KernelHostActions.ResolveDesign) still finds it by that id.
    // Returns the newly-minted Design id.
    public static int AdoptInto(IInstanceStore store, string appText)
    {
        var desc = AppParse.Parse(appText);
        var sections = SplitSections(appText);

        var designId = store.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["label"]       = new TextValue(""), // the label is a UI-editable field, not carried by the app doc
            ["initialData"] = new TextValue(sections.GetValueOrDefault("initialData", "")),
            ["access"]      = new TextValue(sections.GetValueOrDefault("access", "")),
            ["common"]      = new TextValue(sections.GetValueOrDefault("common", "")),
            ["ui"]          = new TextValue(sections.GetValueOrDefault("ui", "")),
        }));
        var typesSetId = SetIdOf(store, designId, "types");

        var typeOrder = 1;
        foreach (var type in desc.AllTypes())
            store.AddToSet(typesSetId, AdoptType(store, type, typeOrder++ * 10));

        store.AddToSet(NodePath.Root.Field("designs"), designId);
        return designId;
    }

    // Adopt one MetaType (+ its MetaProps) live, mirroring SeedBuilder.AddType's field shape. Returns the
    // newly-minted MetaType id (not yet linked into any design's `types` set — the caller links it).
    private static int AdoptType(IInstanceStore store, TypeDefinition type, int order)
    {
        var typeId = store.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]     = new TextValue(type.Name),
            ["baseType"] = new TextValue(type.BaseType == BaseType.Object ? "object"
                : type.BaseType == BaseType.Enum ? "enum"
                : BaseTypes.NameOf(type.BaseType)),
            ["values"]   = new TextValue(string.Join(", ", type.Values ?? [])),
            ["order"]    = new IntValue(order),
        }));
        var propsSetId = SetIdOf(store, typeId, "props");

        var propOrder = 1;
        foreach (var prop in type.Props ?? [])
            store.AddToSet(propsSetId, AdoptProp(store, prop, propOrder++ * 10));

        return typeId;
    }

    // Adopt one MetaProp live, mirroring SeedBuilder.AddProp's field shape (cardinality always explicit;
    // keyType only for a dictionary — see SeedBuilder.AddProp's own doc for why).
    private static int AdoptProp(IInstanceStore store, PropDefinition prop, int order)
    {
        var fields = new Dictionary<string, NodeValue>
        {
            ["name"]        = new TextValue(prop.Name),
            ["type"]        = new TextValue(prop.Type),
            ["order"]       = new IntValue(order),
            ["cardinality"] = new TextValue(prop.Cardinality switch
            {
                Cardinality.Set => "set",
                Cardinality.Dictionary => "dictionary",
                Cardinality.List => "list",
                _ => "single",
            }),
            ["multiline"] = new BoolValue(prop.Multiline),
        };
        if (prop.Cardinality == Cardinality.Dictionary)
            fields["keyType"] = new TextValue(prop.KeyType ?? "text");
        return store.CreateObject("MetaProp", new ObjectValue(fields));
    }

    // The intrinsic id of a just-created object's named SET prop (every declared set prop starts as an
    // EMPTY StoredSet with its own minted id — see JsonFileInstanceStore.BuildFields), read back through
    // ReadById since the object is not yet linked anywhere a NodePath could reach it.
    private static int SetIdOf(IInstanceStore store, int objectId, string setProp)
    {
        var hit = store.ReadById(objectId)
            ?? throw new InvalidOperationException($"Object {objectId} vanished immediately after creation.");
        return hit.Fields.Fields.GetValueOrDefault(setProp) is SetValue sv ? sv.Id
            : throw new InvalidOperationException($"'{hit.TypeName}' has no set prop '{setProp}'.");
    }

    // Split an app document into its top-level sections, keyed by section keyword (types / initialData /
    // access / common / ui), each value the VERBATIM section text INCLUDING its keyword line and trailing
    // newline (e.g. "ui\n    fn render()\n        return <main>\n"). A section runs from its column-0
    // keyword line through the line before the next column-0 keyword (or EOF), with trailing blank lines
    // trimmed and a single closing newline — the exact form SchemaBridge's Design text fields carry, so
    // the reverse → forward round-trip is faithful. Only the five known keywords start a section (an
    // unindented blank line never does); every app document begins with `types`, so there is no leading
    // non-section content. Public so the kernel-seed tests can assert section boundaries directly.
    public static IReadOnlyDictionary<string, string> SplitSections(string appText)
    {
        var keywords = new HashSet<string> { "types", "initialData", "access", "common", "ui" };
        var lines = appText.Replace("\r\n", "\n").Split('\n');
        var sections = new Dictionary<string, string>();

        string? current = null;
        var body = new List<string>();
        void Flush()
        {
            if (current == null) return;
            while (body.Count > 0 && body[^1].Length == 0) body.RemoveAt(body.Count - 1);
            sections[current] = string.Join("\n", body) + "\n";
        }

        foreach (var line in lines)
        {
            if (keywords.Contains(line))
            {
                Flush();
                current = line;
                body = [line];
            }
            else if (current != null)
            {
                body.Add(line);
            }
        }
        Flush();
        return sections;
    }

    // Accumulates the flat, id-keyed initialData pools (Db / Design / MetaType / MetaProp) the design
    // seed expresses: every object is a top-level entry with a unique id, sets are arrays of member ids.
    // Design ids are caller-supplied (the load-bearing kernel.json designIds); every OTHER minted id (the
    // root Db, MetaTypes/MetaProps) comes from a counter started ABOVE every FIXED (caller-supplied) id,
    // so a minted id never collides with a Design id. Used only by `Build` — the FRESH-store, first-boot
    // seed, where InitialData/Reset is still the right mechanism (there is no prior log to preserve).
    private sealed class SeedBuilder
    {
        private readonly Dictionary<string, Dictionary<string, JsonElement>> _pools = new();
        private int _nextId;

        // `fixedIds` = every id that must NOT be re-minted: the file-backed designIds. The counter
        // starts above them all.
        public SeedBuilder(IEnumerable<int> fixedIds) =>
            _nextId = fixedIds.DefaultIfEmpty(0).Max() + 1;

        // Add one committed app as a Design AT its caller-supplied id (the kernel.json designId): the
        // structured types (reverse-projected from the parsed `types` section) + the other three sections
        // as verbatim text. Returns the Design's id (== designId). The designer's own app document carries
        // an empty initialData, so this is uniform — no self-snapshot special case is needed.
        public int AddDesign(string label, int designId, string appText)
        {
            var desc = AppParse.Parse(appText);
            var sections = SplitSections(appText);

            var typeIds = new List<int>();
            var typeOrder = 1;
            foreach (var type in desc.AllTypes())
                typeIds.Add(AddType(type, typeOrder++ * 10));

            var fields = new JsonObject
            {
                ["label"] = label,
                // The other sections VERBATIM (keyword + body, "" when absent) — exactly what
                // SchemaBridge's Design text fields expect and ProjectDesignDb reassembles. `access`
                // (the M-auth ruleset, incl. the host-action `sys` subject) is carried like the other text
                // sections so a design round-trips its access rules (else a published design silently
                // dropped them — the designer's own `sys` rule included).
                ["initialData"] = sections.GetValueOrDefault("initialData", ""),
                ["access"] = sections.GetValueOrDefault("access", ""),
                ["common"] = sections.GetValueOrDefault("common", ""),
                ["ui"] = sections.GetValueOrDefault("ui", ""),
                ["types"] = IdArray(typeIds),
            };
            Pool("Design")[designId.ToString()] = ToElement(fields);
            return designId;
        }

        // Mint an Instance seed entry: { name, runtimeId, design: <bare designId or absent> }. The
        // `design` field is a BARE id (a single reference in seed form — INSTANCE_DESCRIPTION_FORMAT.md
        // "a single reference is a bare id"). When designId is 0 (not-yet-hosted), the field is omitted
        // so the reference stays unset (same as an omitted single-ref in initialData).
        // runtimeId comment: this field is the link to the kernel runtime row (0 = not-yet-hosted).
        // Storage-collapse (plan slice 2) would dissolve it — the store id would BE the runtime id.
        public int AddInstance(string name, int runtimeId, int? designId)
        {
            var fields = new JsonObject
            {
                ["name"] = name,
                ["runtimeId"] = runtimeId,
            };
            if (designId is { } did && did != 0)
                fields["design"] = did;
            return Add("Instance", fields);
        }

        public void AddRootDb(IReadOnlyList<int> designIds, IReadOnlyList<int> instanceIds) =>
            Pool("Db")[MintId().ToString()] = ToElement(new JsonObject
            {
                ["designs"] = IdArray(designIds),
                ["instances"] = IdArray(instanceIds),
            });

        public InstanceInitialData Build()
        {
            var extents = _pools.ToDictionary(
                e => e.Key,
                e => (IReadOnlyDictionary<string, JsonElement>)e.Value);
            return new InstanceInitialData(extents);
        }

        // A MetaType seed (name + baseType + values + order + its props), reverse-projecting a
        // TypeDefinition the same way the type editor / SchemaBridge.Project round-trip it. An enum's
        // value list is seeded into the comma-separated `values` field (the always-rendered enum-values
        // input the type editor edits + SchemaBridge.Project reads back); object/leaf types carry "".
        private int AddType(TypeDefinition type, int order)
        {
            var propIds = new List<int>();
            var propOrder = 1;
            foreach (var prop in type.Props ?? [])
                propIds.Add(AddProp(prop, propOrder++ * 10));

            var fields = new JsonObject
            {
                ["name"] = type.Name,
                // `object` for object types; the lowercase leaf/enum name for a leaf alias or enum.
                // Mirrors how SchemaBridge.Project reads baseType back (== "object", "enum", or a base).
                ["baseType"] = type.BaseType == BaseType.Object ? "object"
                    : type.BaseType == BaseType.Enum ? "enum"
                    : BaseTypes.NameOf(type.BaseType),
                ["values"] = string.Join(", ", type.Values ?? []),
                ["order"] = order,
                ["props"] = IdArray(propIds),
            };
            return Add("MetaType", fields);
        }

        // A MetaProp seed. cardinality is written EXPLICITLY for every prop — "single" included — so the
        // value matches an option in the designer's cardinality <select> (a blank cardinality would
        // leave that bound <select> with no selected option after hydration). SchemaBridge.Project reads
        // "single" and "" alike as Single, so this changes nothing about what projects back. keyType is
        // emitted only for a dictionary (the one cardinality it is meaningful for).
        private int AddProp(PropDefinition prop, int order)
        {
            var fields = new JsonObject
            {
                ["name"] = prop.Name,
                ["type"] = prop.Type,
                ["order"] = order,
                ["cardinality"] = prop.Cardinality switch
                {
                    Cardinality.Set => "set",
                    Cardinality.Dictionary => "dictionary",
                    Cardinality.List => "list",
                    _ => "single",
                },
                // The `multiline` presentation flag (a single text prop's textarea toggle) round-trips
                // into the designer's data, so a committed app's `notes text multiline` shows toggled-on
                // in the editor and SchemaBridge.Project reads it back. Always emitted (false for every
                // non-multiline prop) so the field is present on every seeded MetaProp — the bool the
                // designer's checkbox binds and the type editor reads without a missing-field error.
                ["multiline"] = prop.Multiline,
            };
            if (prop.Cardinality == Cardinality.Dictionary)
                fields["keyType"] = prop.KeyType ?? "text";
            return Add("MetaProp", fields);
        }

        private int Add(string type, JsonObject fields)
        {
            var id = MintId();
            Pool(type)[id.ToString()] = ToElement(fields);
            return id;
        }

        private int MintId() => _nextId++;

        private Dictionary<string, JsonElement> Pool(string type)
        {
            if (!_pools.TryGetValue(type, out var pool))
                _pools[type] = pool = new Dictionary<string, JsonElement>();
            return pool;
        }

        private static JsonArray IdArray(IReadOnlyList<int> ids) =>
            new(ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

        private static JsonElement ToElement(JsonObject obj) =>
            JsonSerializer.SerializeToElement(obj);
    }
}
