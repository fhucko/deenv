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
// The designer META-SCHEMA here is TEST-LOCAL (a `Db { designs }` shape written to a temp .app),
// NOT the real instances/4/app.app — that real designer still edits today's `Db { types }` shape and
// is exercised by Designer.feature; the `Db { designs }` IDE schema + its visible UI are the NEXT
// slice. Keeping the host-action test on its own meta isolates it from that pending change.
[Binding]
public sealed class HostActionSteps
{
    // Sentinels distinguishing "written by the publish" from "left untouched".
    private const string TargetAppSentinel = "UNCHANGED-TARGET-APP-SENTINEL";
    private const string TargetDataSentinel = "{ \"unchanged\": \"target-data-sentinel\" }";

    // A test-local designer meta-schema: a Db holding a SET of Designs, where a Design is a whole app
    // (a structured `types` set + the other app-document sections — initialData/common/ui — as text).
    // This is the `Db { designs }` IDE shape the host action's design-resolution reads; the real
    // designer (Designer.feature) still uses `Db { types }`, untouched by this slice.
    private const string MetaSchema =
        """
        types
            Db
                designs: set of Design
            Design
                label: text
                initialData: text
                common: text
                ui: text
                types: set of MetaType
            MetaType
                name: text
                baseType: text
                order: int
                props: set of MetaProp
            MetaProp
                name: text
                type: text
                cardinality: text
                keyType: text
                order: int
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

    // What the fake create delegate recorded — the projected app document + the requested ports —
    // so a create scenario can assert the kernel was asked to spawn the right thing (no real host).
    private string _createdAppDoc = "";
    private int _createdAppPort;
    private int _createdInfraPort;
    private bool _createInvoked;

    // What the fake delete/clone delegates recorded — the ids/ports the kernel was asked to act on —
    // so a delete/clone scenario can assert the channel carried the right arguments (no real host).
    private int _deletedId;
    private bool _deleteInvoked;
    private int _clonedSourceId;
    private int _clonedAppPort;
    private int _clonedInfraPort;
    private bool _cloneInvoked;

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

    // ── Given: a target instance addressed by an id ─────────────────────────────

    [Given("a target instance addressed by an id")]
    public void GivenTargetInstance()
    {
        Directory.CreateDirectory(_dir);
        _targetAppPath = Path.Combine(_dir, "target.app");
        _targetDataPath = Path.Combine(_dir, "target-data.json");
        File.WriteAllText(_targetAppPath, TargetAppSentinel);
        File.WriteAllText(_targetDataPath, TargetDataSentinel);
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

    [When("the designer creates an instance from that design on ports {int} and {int} over the WS")]
    public void WhenCreateFromDesign(int appPort, int infraPort) => Create(_designId, appPort, infraPort);

    // A schema id that is NOT a design (an existing MetaType object) — the resolver must reject it
    // before any projection or spawn, so the create delegate is never reached.
    [When("the designer creates an instance from a non-design id over the WS")]
    public void WhenCreateNonDesign() => Create(_nonDesignId, 9100, 9101);

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

    private void Create(int designId, int appPort, int infraPort) =>
        _reply = Ws().ProcessMessage(
            $$"""{ "op": "hostAction", "action": "create", "args": [ { "type": "int", "value": {{designId}} }, { "type": "int", "value": {{appPort}} }, { "type": "int", "value": {{infraPort}} } ] }""");

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
            createInstance: (appDoc, appPort, infraPort) =>
            {
                _createdAppDoc = appDoc;
                _createdAppPort = appPort;
                _createdInfraPort = infraPort;
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
        // The publish deletes the old data file and reseeds the NEW schema's initial document, so the
        // sentinel is gone and what's there now loads cleanly against the published schema.
        await Assert.That(File.ReadAllText(_targetDataPath)).IsNotEqualTo(TargetDataSentinel);
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        _ = new JsonFileInstanceStore(_targetDataPath, published); // throws if the reset data is invalid
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
