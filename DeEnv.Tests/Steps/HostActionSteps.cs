using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Kernel;
using DeEnv.Storage;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Drives the host-action primitive (`sys.publish`) at the WS-HANDLER seam — the same level
// BridgeSteps drives SchemaBridge, no browser. A "designer" instance's store is authored as a
// design; a "target" (created-style) instance is addressed by an id; the designer's WsHandler is
// hand-constructed with a real KernelHostActions whose resolver maps the target id → the target's
// app+data paths; then a `{ op:"hostAction", action:"publish", args:[id] }` message is processed.
// This exercises the full server path: WsHandler.HandleHostAction → IHostActions → SchemaBridge.
[Binding]
public sealed class HostActionSteps
{
    // Sentinels distinguishing "written by the export" from "left untouched".
    private const string TargetAppSentinel = "UNCHANGED-TARGET-APP-SENTINEL";
    private const string TargetDataSentinel = "{ \"unchanged\": \"target-data-sentinel\" }";

    // The designer is the meta-schema; it lives in its id-dir (instances/4/app.app) now that storage
    // is id-based (the file name no longer carries the app's identity).
    private readonly string _metaAppPath = Path.Combine(AppContext.BaseDirectory, "instances", "4", "app.app");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deenv-hostaction-" + Guid.NewGuid().ToString("N"));

    private InstanceDescription _meta = null!;
    private IInstanceStore _designer = null!;
    private string _designerDataPath = "";
    private readonly Dictionary<string, int> _typeKeys = new();

    private string _targetAppPath = "";
    private string _targetDataPath = "";
    private const int TargetId = 7;

    // The designer passes `db`, the root schema object — DbBridge.RootId. The host action reads
    // the caller's root and projects it; a non-root schema id is rejected (a future extension).
    private const int RootSchemaId = 1;

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

    // ── Given: a designer instance + a designed schema ──────────────────────────

    // A valid design: a Db object holding a set of the named element type, and that element type
    // carrying one scalar prop — enough to export and load. The element type name is what the
    // published target document must describe.
    [Given("a designer instance with a designed type {string} with a {string} prop")]
    public void GivenDesignerWithType(string typeName, string propName)
    {
        OpenDesigner();
        DesignType("Db", "object");
        DesignType(typeName, "object");
        DesignProp(typeName, propName, "text");
        DesignSetProp("Db", typeName.ToLowerInvariant() + "s", typeName);
    }

    // An INVALID design: the root Db is an object type with no props (SchemaBridge rejects it with a
    // "props" error). The same shape Bridge.feature's invalid case uses — the export validates the
    // design and writes nothing, so the host action surfaces the rejection.
    [Given("a designer instance whose design is an object type with no props")]
    public void GivenDesignerWithEmptyObjectType()
    {
        OpenDesigner();
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

    [When("the designer publishes the schema to the target's id over the WS")]
    public void WhenPublishToTarget() => Publish(TargetId);

    [When("the designer publishes the schema to an unknown id over the WS")]
    public void WhenPublishToUnknown() => Publish(TargetId + 999);

    [When("the designer creates an instance from the schema on ports {int} and {int} over the WS")]
    public void WhenCreate(int appPort, int infraPort) =>
        _reply = Ws().ProcessMessage(
            $$"""{ "op": "hostAction", "action": "create", "args": [ { "type": "int", "value": {{RootSchemaId}} }, { "type": "int", "value": {{appPort}} }, { "type": "int", "value": {{infraPort}} } ] }""");

    // A schema id that is NOT the root object — the guard must reject it (only `db` is projectable
    // today) before any projection or spawn, so the create delegate is never reached.
    [When("the designer creates an instance from a non-root schema object over the WS")]
    public void WhenCreateNonRoot() =>
        _reply = Ws().ProcessMessage(
            $$"""{ "op": "hostAction", "action": "create", "args": [ { "type": "int", "value": {{RootSchemaId + 99}} }, { "type": "int", "value": 9100 }, { "type": "int", "value": 9101 } ] }""");

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

    // publish(schema, targetId): the schema is the root object (RootSchemaId); the target id resolves
    // to a spec (only TargetId resolves — any other id → null → a reject, never a write).
    private void Publish(int targetId) =>
        _reply = Ws().ProcessMessage(
            $$"""{ "op": "hostAction", "action": "publish", "args": [ { "type": "int", "value": {{RootSchemaId}} }, { "type": "int", "value": {{targetId}} } ] }""");

    // The designer's WsHandler with a real KernelHostActions: it acts as the designer (its own
    // meta+data are the meta-schema it projects), resolves ONLY TargetId → the target spec, and its
    // create/delete/clone delegates RECORD what they were asked to do instead of driving a real kernel.
    private WsHandler Ws()
    {
        // The delete/clone seam scenarios carry no designed schema (they never touch the store — the
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
        // The export wrote a real app document over the sentinel; it loads and declares the type.
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        await Assert.That(published.FindType(typeName)).IsNotNull();
    }

    [Then("the target instance's data is reset")]
    public async Task ThenTargetDataReset()
    {
        // The export deletes the old data file and reseeds the NEW schema's initial document, so the
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
        // create projected the designer's design to an app document (text) and handed it to the
        // kernel create; it parses and declares the designed type.
        await Assert.That(InstanceDescriptionLoader.Load(_createdAppDoc).FindType(typeName)).IsNotNull();
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

    // ── helpers (designer-data authoring, mirroring BridgeSteps) ─────────────────

    private void OpenDesigner()
    {
        _meta = InstanceDescriptionLoader.LoadFile(_metaAppPath);
        _designerDataPath = Path.GetTempFileName();
        _designer = new JsonFileInstanceStore(_designerDataPath, _meta);
    }

    private void DesignType(string name, string baseType)
    {
        var id = _designer.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]     = new TextValue(name),
            ["baseType"] = new TextValue(baseType),
            ["order"]    = new IntValue(0)
        }));
        _designer.AddToSet(NodePath.Root.Field("types"), id);
        _typeKeys[name] = id;
    }

    private void DesignProp(string typeName, string propName, string propType) =>
        AddProp(typeName, propName, propType, cardinality: "");

    private void DesignSetProp(string typeName, string propName, string propType) =>
        AddProp(typeName, propName, propType, cardinality: "set");

    private void AddProp(string typeName, string propName, string propType, string cardinality)
    {
        var propsPath = NodePath.Root.Field("types").Key(_typeKeys[typeName].ToString()).Field("props");
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
