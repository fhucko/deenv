using DeEnv.Designer;
using DeEnv.Instance;
using DeEnv.Storage;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// DesignSnapshot.feature — the M13 slice-2 per-commit caches (SchemaBridge.Snapshot): the canonical
// printed app document + a name-path → intrinsic-id map. Drives SchemaBridge.Snapshot directly (no
// storage, no wire, no WS) over a REAL designer store — the same test-local `Db { designs set of Design }`
// meta-schema shape HostActionSteps/BridgeSteps use, so every id in the id map is a genuine store-minted
// id, not hand-built. Mutations (rename/remove/add) go through the store's write methods
// (WriteField/RemoveFromSet/AddToSet/CreateObject) — the same seam a real designer session uses.
[Binding]
public sealed class DesignSnapshotSteps
{
    // The designer meta-schema: a Db holding a set of whole-app Designs, each a structured `types` set
    // (of MetaType, each holding a `props` set of MetaProp) + the other app-document sections as text —
    // exactly the shape SchemaBridge.ProjectDesignDb/Snapshot project. Test-local, isolated from the
    // live designer instance (the same isolation HostActionSteps/BridgeSteps use).
    private const string MetaSchema =
        """
        types
            Db
                designs set of Design
            Design
                label text
                initialData text
                access text
                common text
                ui text
                types set of MetaType
            MetaType
                name text
                baseType text
                values text
                order int
                props set of MetaProp
            MetaProp
                name text
                type text
                cardinality text
                keyType text
                multiline bool
                order int
        """;

    private readonly IInstanceStore _designer =
        new JsonFileInstanceStore(Path.GetTempFileName(), InstanceDescriptionLoader.Load(MetaSchema));

    private int _designId;
    private int _dbTypeId;
    private int _noteTypeId;
    private int _notesPropId;
    private int _titlePropId;
    private int _countPropId;
    private int _standaloneTypeId; // a type not referenced by any set (so retyping it to enum stays valid)

    private DesignSnapshot? _snapshot;
    private DesignSnapshot? _oldSnapshot;
    private Exception? _error;

    // ── Background: a design with a Db{notes set} + Note{title, count} ─────────────────────────────

    [Given("a designer store seeded with a design that has a Db with a notes set and a Note with title and count")]
    public void GivenSeededDesign()
    {
        _designId = _designer.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["label"]       = new TextValue("app"),
            ["initialData"] = new TextValue(""),
            ["access"]      = new TextValue(""),
            ["common"]      = new TextValue(""),
            ["ui"]          = new TextValue(""),
        }));
        _designer.AddToSet(NodePath.Root.Field("designs"), _designId);

        _dbTypeId = AddType("Db", "object");
        _noteTypeId = AddType("Note", "object");
        _notesPropId = AddSetProp(_dbTypeId, "notes", "Note");
        _titlePropId = AddProp(_noteTypeId, "title", "text");
        _countPropId = AddProp(_noteTypeId, "count", "int");
    }

    // ── When/Given: build a snapshot ─────────────────────────────────────────────────────────────────

    [When("a snapshot of the design is built")]
    public void WhenSnapshotBuilt() => _snapshot = BuildSnapshot();

    [Given("a snapshot of the design is built and set aside as the old snapshot")]
    public void GivenOldSnapshot() => _oldSnapshot = BuildSnapshot();

    private DesignSnapshot BuildSnapshot()
    {
        var design = _designer.ReadNode(NodePath.Root.Field("designs").Key(_designId.ToString()))!;
        return SchemaBridge.Snapshot(design);
    }

    [Then("building a snapshot fails with a schema validation error")]
    public async Task ThenBuildFails()
    {
        try
        {
            var design = _designer.ReadNode(NodePath.Root.Field("designs").Key(_designId.ToString()))!;
            SchemaBridge.Snapshot(design);
        }
        catch (Exception ex) { _error = ex; }

        await Assert.That(_error).IsNotNull();
        await Assert.That(_error).IsTypeOf<SchemaValidationException>();
    }

    // ── Then: the canonical fixpoint ─────────────────────────────────────────────────────────────────

    // The snapshot text parses, and re-printing the parsed description reproduces the SAME text — the
    // canonical-printer fixpoint (parse(print(d)) round-trips and printing again is idempotent), the same
    // proof SchemaSteps.ThenRoundTrips asserts elsewhere. This design's other sections (initialData/
    // access/common/ui) are all empty, so Text is exactly its printed `types` section.
    [Then("the snapshot text round-trips through the printer")]
    public async Task ThenTextRoundTrips()
    {
        var desc = InstanceDescriptionLoader.Load(_snapshot!.Text);
        await Assert.That(AppPrint.Print(desc)).IsEqualTo(_snapshot.Text);
    }

    [Then("building the snapshot again yields byte-identical text")]
    public async Task ThenBuildAgainIdentical()
    {
        var again = BuildSnapshot();
        await Assert.That(again.Text).IsEqualTo(_snapshot!.Text);
    }

    // ── Then: the id map ──────────────────────────────────────────────────────────────────────────────

    // Db, Db.notes, Note, Note.title, Note.count — two types + their three props (Db's set prop counts too).
    [Then("the id map has exactly one entry per type and per prop")]
    public async Task ThenIdMapCount() =>
        await Assert.That(_snapshot!.IdMap.Count).IsEqualTo(5);

    [Then("the entry for each type and prop matches its row id in the designer store")]
    public async Task ThenIdMapMatchesRowIds()
    {
        await Assert.That(_snapshot!.IdMap["Db"]).IsEqualTo(_dbTypeId);
        await Assert.That(_snapshot.IdMap["Db.notes"]).IsEqualTo(_notesPropId);
        await Assert.That(_snapshot.IdMap["Note"]).IsEqualTo(_noteTypeId);
        await Assert.That(_snapshot.IdMap["Note.title"]).IsEqualTo(_titlePropId);
        await Assert.That(_snapshot.IdMap["Note.count"]).IsEqualTo(_countPropId);
    }

    [Then("the id map has an entry for {string}")]
    public async Task ThenIdMapHasEntry(string key) =>
        await Assert.That(_snapshot!.IdMap.ContainsKey(key)).IsTrue();

    [Then("the id map has no entry for {string}")]
    public async Task ThenIdMapHasNoEntry(string key) =>
        await Assert.That(_snapshot!.IdMap.ContainsKey(key)).IsFalse();

    // ── When: mutate the design through the store seam ───────────────────────────────────────────────

    [When("the Note type's title prop is renamed to {string} in the designer store")]
    public void WhenPropRenamed(string newName) => _designer.WriteField(_titlePropId, "name", new TextValue(newName));

    [When("the Note type's title prop is removed and a new prop named {string} is added in the designer store")]
    public void WhenPropReplaced(string sameName)
    {
        var propsPath = NodePath.Root.Field("designs").Key(_designId.ToString())
            .Field("types").Key(_noteTypeId.ToString()).Field("props");
        _designer.RemoveFromSet(propsPath, _titlePropId);
        _titlePropId = AddProp(_noteTypeId, sameName, "text"); // a FRESH mint — a different id, same name
    }

    // A standalone object type (NOT referenced by any set, so retyping it to enum leaves the whole
    // document valid — unlike Note, which Db.notes points at) carrying one prop. This is the type the
    // enum-leftover-props probe flips: after the flip its prop lingers in the store but vanishes from the
    // projected doc.
    [Given("a standalone object type {string} with a prop {string} in the design")]
    public void GivenStandaloneType(string typeName, string propName)
    {
        _standaloneTypeId = AddType(typeName, "object");
        AddProp(_standaloneTypeId, propName, "text");
    }

    // Flip the standalone type's base type to enum WITHOUT touching its `props` set — the reachable designer
    // state (the base-type <select> is unguarded) where leftover MetaProp members linger on a now-enum type.
    // The enum needs a non-empty `values` field or the projection rejects it ("no values"); Project's enum
    // branch then hardcodes Props: null, so the leftover props vanish from the printed doc — the exact
    // condition that would leave a phantom "Type.<prop>" in an unguarded map walk.
    [When("the {word} type is retyped to an enum with values {string} leaving its props in the store")]
    public void WhenRetypedToEnum(string typeName, string values)
    {
        _designer.WriteField(_standaloneTypeId, "baseType", new TextValue("enum"));
        _designer.WriteField(_standaloneTypeId, "values", new TextValue(values));
    }

    [Given("the Note type's name is blanked in the designer store")]
    public void GivenTypeNameBlanked() => _designer.WriteField(_noteTypeId, "name", new TextValue(""));

    // ── Then: comparisons between old and new snapshots ──────────────────────────────────────────────

    [Then("the new snapshot text differs from the old snapshot text")]
    public async Task ThenTextDiffers() =>
        await Assert.That(_snapshot!.Text).IsNotEqualTo(_oldSnapshot!.Text);

    [Then("the id under {string} in the new snapshot's id map equals the id under {string} in the old snapshot's id map")]
    public async Task ThenIdUnchanged(string newPath, string oldPath) =>
        await Assert.That(_snapshot!.IdMap[newPath]).IsEqualTo(_oldSnapshot!.IdMap[oldPath]);

    [Then("the id under {string} in the new snapshot's id map differs from the id under {string} in the old snapshot's id map")]
    public async Task ThenIdChanged(string newPath, string oldPath) =>
        await Assert.That(_snapshot!.IdMap[newPath]).IsNotEqualTo(_oldSnapshot!.IdMap[oldPath]);

    // ── helpers (designer-data authoring, mirroring HostActionSteps/BridgeSteps) ─────────────────────

    private int AddType(string name, string baseType)
    {
        var id = _designer.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]     = new TextValue(name),
            ["baseType"] = new TextValue(baseType),
            ["order"]    = new IntValue(0),
        }));
        _designer.AddToSet(NodePath.Root.Field("designs").Key(_designId.ToString()).Field("types"), id);
        return id;
    }

    private int AddProp(int typeId, string name, string type)
    {
        var propsPath = NodePath.Root.Field("designs").Key(_designId.ToString())
            .Field("types").Key(typeId.ToString()).Field("props");
        var id = _designer.CreateObject("MetaProp", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]  = new TextValue(name),
            ["type"]  = new TextValue(type),
            ["order"] = new IntValue(0),
        }));
        _designer.AddToSet(propsPath, id);
        return id;
    }

    private int AddSetProp(int typeId, string name, string elementType)
    {
        var propsPath = NodePath.Root.Field("designs").Key(_designId.ToString())
            .Field("types").Key(typeId.ToString()).Field("props");
        var id = _designer.CreateObject("MetaProp", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]        = new TextValue(name),
            ["type"]        = new TextValue(elementType),
            ["cardinality"] = new TextValue("set"),
            ["order"]       = new IntValue(0),
        }));
        _designer.AddToSet(propsPath, id);
        return id;
    }
}
