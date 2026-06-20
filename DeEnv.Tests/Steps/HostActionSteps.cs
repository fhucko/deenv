using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Kernel;
using DeEnv.Storage;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Drives the host-action primitives (`sys.publish` / `sys.create`) at the WS-HANDLER seam — the same
// level BridgeSteps drives SchemaBridge, no browser. The designer's data is an IDE: a `Db` holding a
// `db.designs` SET of Designs, and a `Design` is a WHOLE app (structured `types` + the other
// app-document sections as text). A scenario authors ONE design into the designer's store, then a
// `{ op:"hostAction", action:"publish"|"create", args:[designId, …] }` message is processed; the host
// action resolves that design's id → its subtree in the store and projects the WHOLE app. This
// exercises the full server path: WsHandler.HandleHostAction → IHostActions → SchemaBridge.
//
// The designer META-SCHEMA here is TEST-LOCAL (a minimal `Db { designs }` shape written to a temp
// .app), NOT the real instances/1/app.app (which the browser-driven Designer.feature exercises).
// Keeping the host-action test on its own controlled meta isolates the server-path assertions from
// the full seeded designer — a minimal design is enough to prove resolve → project → host action.
[Binding]
public sealed class HostActionSteps
{
    // Sentinel distinguishing "written by the publish" from "left untouched" (the app document).
    private const string TargetAppSentinel = "UNCHANGED-TARGET-APP-SENTINEL";

    // A test-local designer meta-schema: a Db holding a SET of Designs, where a Design is a whole app
    // (a structured `types` set + the other app-document sections — initialData/common/ui — as text).
    // This is the `Db { designs }` IDE shape the host action's design-resolution reads; the real
    // designer (Designer.feature) still uses `Db { types }`, untouched by this slice.
    private const string MetaSchema =
        """
        types
            Db
                designs set of Design
            Design
                label text
                initialData text
                common text
                ui text
                types set of MetaType
            MetaType
                name text
                baseType text
                order int
                props set of MetaProp
            MetaProp
                name text
                type text
                cardinality text
                keyType text
                order int
        """;

    // A custom render section authored into a design's `ui` text field — verbatim section source
    // INCLUDING the `ui` keyword + indentation (the pinned text-field representation). The published
    // app must keep this `fn render()` (the WHOLE app is projected, not just its types), so the
    // generic UI is NOT substituted.
    private const string CustomUiSection =
        "ui\n" +
        "    fn render()\n" +
        "        return <main class=\"item-app\">\n" +
        "            \"Items\"\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deenv-hostaction-" + Guid.NewGuid().ToString("N"));
    private string _metaAppPath = "";

    private InstanceDescription _meta = null!;
    private IInstanceStore _designer = null!;
    private string _designerDataPath = "";
    private readonly Dictionary<string, int> _typeKeys = new();

    // The id of the authored design (the schema object the designer passes — one member of
    // db.designs), and a non-design object's id (a MetaType) for the "not a design" reject.
    private int _designId;
    private int _nonDesignId;

    private string _targetAppPath = "";
    private string _targetDataPath = "";
    private const int TargetId = 7;
    private const int UnknownTargetId = TargetId + 999;

    // What the fake create delegate recorded — the projected app document + the requested ports + the
    // design's id — so a create scenario can assert the kernel was asked to spawn the right thing (no
    // real host). The design id is threaded so the new instance's registry entry pre-selects its design.
    private string _createdAppDoc = "";
    private string _createdName = "";
    private int _createdAppPort;
    private int _createdInfraPort;
    private int? _createdDesignId;
    private bool _createInvoked;

    // What the fake restart delegate recorded — the id the kernel was asked to restart — so a
    // publish/setDesign scenario can assert the post-write restart was triggered (no real host).
    private int _restartedId;
    private bool _restartInvoked;

    // What the fake rename delegate recorded — the (id, name) the kernel was asked to set.
    private int _renamedId;
    private string _renamedName = "";
    private bool _renameInvoked;

    // What the fake delete/clone delegates recorded — the ids/ports the kernel was asked to act on —
    // so a delete/clone scenario can assert the channel carried the right arguments (no real host).
    private int _deletedId;
    private bool _deleteInvoked;
    private int _clonedSourceId;
    private int _clonedAppPort;
    private int _clonedInfraPort;
    private bool _cloneInvoked;

    // What the fake recordDesign delegate recorded — the (targetId, designId) the kernel was asked to
    // record on the registry — so a setDesign scenario can assert the registry-write half ran with the
    // right reference (the deploy half is a real file write, asserted on the target document).
    private int _recordedTargetId;
    private int _recordedDesignId;
    private bool _recordInvoked;

    private string _reply = "";

    // ── Given: a designer instance holding a design ─────────────────────────────

    // A valid WHOLE-APP design: a Design whose `types` set holds a Db object (with a set of the named
    // element type) and that element type carrying one scalar prop, AND a custom `ui` render section.
    // Enough to project, load, and prove the custom UI round-trips (not dropped to the generic UI).
    [Given("a designer instance holding a design with a type {string} and a custom render")]
    public void GivenDesignerHoldingDesign(string typeName)
    {
        OpenDesigner();
        AddDesign(CustomUiSection);
        DesignType("Db", "object");
        DesignType(typeName, "object");
        DesignProp(typeName, "label", "text");
        DesignSetProp("Db", typeName.ToLowerInvariant() + "s", typeName);
    }

    // An INVALID design: its root Db is an object type with no props (SchemaBridge rejects it with a
    // "props" error). The projection validates the WHOLE app and writes nothing, so the host action
    // surfaces the rejection. No custom UI needed — the types are already invalid.
    [Given("a designer instance holding a design whose root is an object type with no props")]
    public void GivenDesignerHoldingInvalidDesign()
    {
        OpenDesigner();
        AddDesign(uiSection: "");
        DesignType("Db", "object");
    }

    // A design that EVOLVES the target's schema by ADDING a field to the element type:
    // Db { items set of Item }, Item { label, <newField> } + the custom UI. The target (above) is
    // seeded under the SAME app WITHOUT <newField>, so publishing this design is a purely additive
    // schema change — the proof that apply preserves the row and defaults the new field.
    [Given("a designer instance holding a design that adds a {string} field to {string}")]
    public void GivenDesignerHoldingAdditiveDesign(string newField, string typeName)
    {
        OpenDesigner();
        AddDesign(CustomUiSection);
        DesignType("Db", "object");
        DesignType(typeName, "object");
        DesignProp(typeName, "label", "text");
        DesignProp(typeName, newField, "text");
        DesignSetProp("Db", typeName.ToLowerInvariant() + "s", typeName);
    }

    // ── Given: a target instance addressed by an id ─────────────────────────────

    [Given("a target instance addressed by an id")]
    public void GivenTargetInstance()
    {
        Directory.CreateDirectory(_dir);
        _targetAppPath = Path.Combine(_dir, "target.app");
        _targetDataPath = Path.Combine(_dir, "target-data.json");
        File.WriteAllText(_targetAppPath, TargetAppSentinel);
        // No data file: this is a FRESH-publish target (nothing to preserve), so a publish/apply seeds
        // the new schema's initial document. A target with prior data uses GivenTargetHoldingItem.
        // (Apply now PRESERVES existing data; a garbage sentinel here would be refused, not reset.)
    }

    // A target instance seeded with REAL data under the element type's PREVIOUS schema —
    // Db { items set of <Type> }, <Type> { label } — holding one object. Publishing an ADDITIVE design
    // (the type gains a field) over this must preserve the object and default the new field. Seeded
    // THROUGH the store seam (CreateObject/AddToSet) so the data is genuine stored shape, valid for the
    // prior schema; the apply reconciles it against the new schema without wiping it.
    [Given("a target instance holding an {string} labelled {string}")]
    public void GivenTargetHoldingItem(string typeName, string label)
    {
        Directory.CreateDirectory(_dir);
        _targetAppPath = Path.Combine(_dir, "target.app");
        _targetDataPath = Path.Combine(_dir, "target-data.json");
        File.WriteAllText(_targetAppPath, TargetAppSentinel);

        var set = typeName.ToLowerInvariant() + "s";
        var priorApp =
            $"""
            types
                Db
                    {set} set of {typeName}
                {typeName}
                    label text
            """;
        var prior = InstanceDescriptionLoader.Load(priorApp);
        var store = new JsonFileInstanceStore(_targetDataPath, prior);
        var id = store.CreateObject(typeName, new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["label"] = new TextValue(label),
        }));
        store.AddToSet(NodePath.Root.Field(set), id);
    }

    // ── When: publish over the WS ───────────────────────────────────────────────

    [When("the designer publishes that design to the target's id over the WS")]
    public void WhenPublishDesignToTarget() => Publish(_designId, TargetId);

    // A schema id that is NOT a member of db.designs (an existing MetaType object): the resolver must
    // reject it — only a design is projectable — before any write to the target.
    [When("the designer publishes a non-design id to the target's id over the WS")]
    public void WhenPublishNonDesign() => Publish(_nonDesignId, TargetId);

    [When("the designer publishes that design to an unknown target id over the WS")]
    public void WhenPublishToUnknownTarget() => Publish(_designId, UnknownTargetId);

    [When("the designer creates an instance named {string} from that design on ports {int} and {int} over the WS")]
    public void WhenCreateFromDesign(string name, int appPort, int infraPort) => Create(_designId, name, appPort, infraPort);

    // A schema id that is NOT a design (an existing MetaType object) — the resolver must reject it
    // before any projection or spawn, so the create delegate is never reached.
    [When("the designer creates an instance from a non-design id over the WS")]
    public void WhenCreateNonDesign() => Create(_nonDesignId, "app", 9100, 9101);

    [When("the operator renames instance id {int} to {string} over the WS")]
    public void WhenRename(int id, string name) =>
        _reply = Ws().ProcessMessage(
            $$"""{ "op": "hostAction", "action": "rename", "args": [ { "type": "int", "value": {{id}} }, { "type": "text", "value": "{{name}}" } ] }""");

    // delete(targetId): a bare instance id (NOT a schema object). The recording delete delegate
    // captures the id; the seam carries it through unchanged and replies ok.
    [When("the operator deletes instance id {int} over the WS")]
    public void WhenDelete(int id) =>
        _reply = Ws().ProcessMessage(
            $$"""{ "op": "hostAction", "action": "delete", "args": [ { "type": "int", "value": {{id}} } ] }""");

    // cloneInstance(sourceId, appPort, infraPort): three bare ints (a source id + the new ports). The
    // recording clone delegate captures the triple; the seam carries it through unchanged and replies ok.
    [When("the operator clones instance id {int} onto ports {int} and {int} over the WS")]
    public void WhenClone(int sourceId, int appPort, int infraPort) =>
        _reply = Ws().ProcessMessage(
            $$"""{ "op": "hostAction", "action": "cloneInstance", "args": [ { "type": "int", "value": {{sourceId}} }, { "type": "int", "value": {{appPort}} }, { "type": "int", "value": {{infraPort}} } ] }""");

    // publish(design, targetId): arg 0 is the design object's id (resolved against the designer's
    // store), arg 1 the target id (only TargetId resolves to a spec → any other id is rejected).
    private void Publish(int designId, int targetId) =>
        _reply = Ws().ProcessMessage(
            $$"""{ "op": "hostAction", "action": "publish", "args": [ { "type": "int", "value": {{designId}} }, { "type": "int", "value": {{targetId}} } ] }""");

    private void Create(int designId, string name, int appPort, int infraPort) =>
        _reply = Ws().ProcessMessage(
            $$"""{ "op": "hostAction", "action": "create", "args": [ { "type": "int", "value": {{designId}} }, { "type": "text", "value": "{{name}}" }, { "type": "int", "value": {{appPort}} }, { "type": "int", "value": {{infraPort}} } ] }""");

    // setDesign(design, targetId): the IDE's Apply — arg 0 the design object's id, arg 1 the target id.
    // The real KernelHostActions both records the reference (the fake recordDesign captures it) AND
    // writes the projected document onto the target (a real file write, like publish).
    [When("the operator applies that design to the target's id over the WS")]
    public void WhenApplyDesignToTarget() =>
        _reply = Ws().ProcessMessage(
            $$"""{ "op": "hostAction", "action": "setDesign", "args": [ { "type": "int", "value": {{_designId}} }, { "type": "int", "value": {{TargetId}} } ] }""");

    // The designer's WsHandler with a real KernelHostActions: it acts as the designer (its own
    // meta+data are the IDE it projects from), resolves ONLY TargetId → the target spec, and its
    // create/delete/clone delegates RECORD what they were asked to do instead of driving a real kernel.
    private WsHandler Ws()
    {
        // The delete/clone seam scenarios carry no authored design (they never touch the store — the
        // action just routes ids to the kernel), so open a bare designer instance lazily to give the
        // WsHandler a valid store + description. The publish/create scenarios already opened one.
        if (_designer == null) OpenDesigner();

        var hostActions = new KernelHostActions(
            _metaAppPath, _designerDataPath,
            id => id == TargetId ? new InstanceSpec(TargetId, "target", _targetAppPath, _targetDataPath, 0, 0) : null,
            createInstance: (appDoc, name, appPort, infraPort, designId) =>
            {
                _createdAppDoc = appDoc;
                _createdName = name;
                _createdAppPort = appPort;
                _createdInfraPort = infraPort;
                _createdDesignId = designId;
                _createInvoked = true;
                return Task.CompletedTask;
            },
            deleteInstance: id =>
            {
                _deletedId = id;
                _deleteInvoked = true;
                return Task.CompletedTask;
            },
            cloneInstance: (sourceId, appPort, infraPort) =>
            {
                _clonedSourceId = sourceId;
                _clonedAppPort = appPort;
                _clonedInfraPort = infraPort;
                _cloneInvoked = true;
                return Task.CompletedTask;
            },
            recordDesign: (targetId, designId) =>
            {
                _recordedTargetId = targetId;
                _recordedDesignId = designId;
                _recordInvoked = true;
                return Task.CompletedTask;
            },
            restartInstance: id =>
            {
                _restartedId = id;
                _restartInvoked = true;
                return Task.CompletedTask;
            },
            renameInstance: (id, name) =>
            {
                _renamedId = id;
                _renamedName = name;
                _renameInvoked = true;
                return Task.CompletedTask;
            });
        // _designer/_meta are guaranteed set above (a Given, or the lazy OpenDesigner here).
        return new WsHandler(_designer!, _meta, sessions: null, registry: null, hostActions: hostActions);
    }

    // ── Then ────────────────────────────────────────────────────────────────────

    [Then("the host action reply is ok")]
    public async Task ThenReplyOk()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(_reply);
        await Assert.That(doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()).IsTrue();
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsFalse();
    }

    [Then("the host action reply is an error mentioning {string}")]
    public async Task ThenReplyErrorMentioning(string phrase)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(_reply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out var err)).IsTrue();
        await Assert.That(err.GetString()).Contains(phrase);
    }

    [Then("the host action reply is an error")]
    public async Task ThenReplyError()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(_reply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsTrue();
    }

    [Then("the target app document describes the designed type {string}")]
    public async Task ThenTargetDescribesType(string typeName)
    {
        // The publish wrote a real app document over the sentinel; it loads and declares the type.
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        await Assert.That(published.FindType(typeName)).IsNotNull();
    }

    [Then("the target app document contains the custom render")]
    public async Task ThenTargetHasCustomRender()
    {
        // The WHOLE app was projected: the published document loads with a custom `fn render()` (so
        // the generic UI is NOT used) carrying the authored marker — the `ui` section round-tripped.
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        await Assert.That(published.Ui?.Render).IsNotNull();
        await Assert.That(File.ReadAllText(_targetAppPath)).Contains("item-app");
    }

    [Then("the target instance's data is reset")]
    public async Task ThenTargetDataReset()
    {
        // A FRESH-publish target (no prior data) is seeded with the NEW schema's initial document on
        // publish/apply, so the data file now exists and loads cleanly against the published schema.
        // (Apply PRESERVES prior data — proven separately; a fresh target has none, so it seeds.)
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        _ = new JsonFileInstanceStore(_targetDataPath, published); // seeded data is present + valid
    }

    [Then("the target still holds an {string} labelled {string}, with {string} defaulted to {string}")]
    public async Task ThenTargetPreservedWithDefault(string typeName, string label, string newField, string expected)
    {
        // Open a store over the PRESERVED data with the now-published (wider) schema. The row survived
        // the apply (found by its label), and the newly added field — absent from the older stored
        // data — reads its default: additive evolution shown end-to-end through apply, not a wipe.
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var item = store.ReadExtent(typeName).Values
            .FirstOrDefault(o => o.Fields.GetValueOrDefault("label") is TextValue t && t.Text == label);
        await Assert.That(item).IsNotNull();
        var field = item!.Fields.GetValueOrDefault(newField);
        await Assert.That(field is TextValue ft ? ft.Text : null).IsEqualTo(expected);
    }

    [Then("the target instance was restarted")]
    public async Task ThenTargetRestarted()
    {
        await Assert.That(_restartInvoked).IsTrue();
        await Assert.That(_restartedId).IsEqualTo(TargetId);
    }

    [Then("the target app document is unchanged")]
    public async Task ThenTargetAppUnchanged()
    {
        await Assert.That(File.ReadAllText(_targetAppPath)).IsEqualTo(TargetAppSentinel);
    }

    [Then("a new instance was created on ports {int} and {int}")]
    public async Task ThenCreatedOnPorts(int appPort, int infraPort)
    {
        await Assert.That(_createInvoked).IsTrue();
        await Assert.That(_createdAppPort).IsEqualTo(appPort);
        await Assert.That(_createdInfraPort).IsEqualTo(infraPort);
        // The seam threads the design's id through to the new entry (so its dropdown pre-selects it);
        // the create-from-a-design scenario passes that design's id, so the delegate must receive it.
        await Assert.That(_createdDesignId).IsEqualTo((int?)_designId);
    }

    [Then("the created instance has the name {string}")]
    public async Task ThenCreatedHasName(string name) =>
        await Assert.That(_createdName).IsEqualTo(name);

    [Then("the kernel was asked to rename instance id {int} to {string}")]
    public async Task ThenRenameInvoked(int id, string name)
    {
        await Assert.That(_renameInvoked).IsTrue();
        await Assert.That(_renamedId).IsEqualTo(id);
        await Assert.That(_renamedName).IsEqualTo(name);
    }

    [Then("the created app document describes the designed type {string}")]
    public async Task ThenCreatedDescribes(string typeName)
    {
        // create projected the design to an app document (text) and handed it to the kernel create;
        // it parses and declares the designed type.
        await Assert.That(InstanceDescriptionLoader.Load(_createdAppDoc).FindType(typeName)).IsNotNull();
    }

    [Then("the created app document contains the custom render")]
    public async Task ThenCreatedHasCustomRender()
    {
        // The WHOLE app was projected into the new instance's document — its custom `fn render()` is
        // present (not the generic UI).
        await Assert.That(InstanceDescriptionLoader.Load(_createdAppDoc).Ui?.Render).IsNotNull();
        await Assert.That(_createdAppDoc).Contains("item-app");
    }

    [Then("no instance was created")]
    public async Task ThenNoneCreated() =>
        await Assert.That(_createInvoked).IsFalse();

    [Then("the kernel was asked to delete instance id {int}")]
    public async Task ThenAskedToDelete(int id)
    {
        await Assert.That(_deleteInvoked).IsTrue();
        await Assert.That(_deletedId).IsEqualTo(id);
    }

    [Then("the kernel was asked to clone source id {int} onto ports {int} and {int}")]
    public async Task ThenAskedToClone(int sourceId, int appPort, int infraPort)
    {
        await Assert.That(_cloneInvoked).IsTrue();
        await Assert.That(_clonedSourceId).IsEqualTo(sourceId);
        await Assert.That(_clonedAppPort).IsEqualTo(appPort);
        await Assert.That(_clonedInfraPort).IsEqualTo(infraPort);
    }

    [Then("the kernel was asked to record that design on the target's id")]
    public async Task ThenAskedToRecordDesign()
    {
        await Assert.That(_recordInvoked).IsTrue();
        await Assert.That(_recordedTargetId).IsEqualTo(TargetId);
        await Assert.That(_recordedDesignId).IsEqualTo(_designId);
    }

    // ── teardown ────────────────────────────────────────────────────────────────

    [AfterScenario]
    public void Cleanup()
    {
        try { if (File.Exists(_designerDataPath)) File.Delete(_designerDataPath); } catch { /* best-effort */ }
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── helpers (designer-data authoring over the `Db { designs }` IDE shape) ────

    private void OpenDesigner()
    {
        Directory.CreateDirectory(_dir);
        // Write the test-local meta-schema into the temp dir and load it as the designer's description,
        // then open the designer's data store over it. ResolveDesign re-loads this same meta path.
        _metaAppPath = Path.Combine(_dir, "designer.app");
        File.WriteAllText(_metaAppPath, MetaSchema);
        _meta = InstanceDescriptionLoader.LoadFile(_metaAppPath);
        _designerDataPath = Path.Combine(_dir, "designer-data.json");
        _designer = new JsonFileInstanceStore(_designerDataPath, _meta);
    }

    // Mint one Design into db.designs (its `types` set starts empty; DesignType/DesignProp fill it).
    // The three section texts are authored verbatim; an empty ui section means the generic UI.
    private void AddDesign(string uiSection)
    {
        _designId = _designer.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["label"]       = new TextValue("app"),
            ["initialData"] = new TextValue(""),
            ["common"]      = new TextValue(""),
            ["ui"]          = new TextValue(uiSection),
        }));
        _designer.AddToSet(NodePath.Root.Field("designs"), _designId);
    }

    private NodePath DesignTypesPath => NodePath.Root.Field("designs").Key(_designId.ToString()).Field("types");

    private void DesignType(string name, string baseType)
    {
        var id = _designer.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]     = new TextValue(name),
            ["baseType"] = new TextValue(baseType),
            ["order"]    = new IntValue(0)
        }));
        _designer.AddToSet(DesignTypesPath, id);
        _typeKeys[name] = id;
        // The first MetaType minted is a convenient non-design object id (a real object that is NOT a
        // member of db.designs) for the "publish/create a non-design id" reject scenarios.
        if (_nonDesignId == 0) _nonDesignId = id;
    }

    private void DesignProp(string typeName, string propName, string propType) =>
        AddProp(typeName, propName, propType, cardinality: "");

    private void DesignSetProp(string typeName, string propName, string propType) =>
        AddProp(typeName, propName, propType, cardinality: "set");

    private void AddProp(string typeName, string propName, string propType, string cardinality)
    {
        var propsPath = DesignTypesPath.Key(_typeKeys[typeName].ToString()).Field("props");
        var fields = new Dictionary<string, NodeValue>
        {
            ["name"]  = new TextValue(propName),
            ["type"]  = new TextValue(propType),
            ["order"] = new IntValue(0)
        };
        if (cardinality.Length > 0)
            fields["cardinality"] = new TextValue(cardinality);
        var id = _designer.CreateObject("MetaProp", new ObjectValue(fields));
        _designer.AddToSet(propsPath, id);
    }
}
