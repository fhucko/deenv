using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Kernel;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// TimeTravel.feature — M13 slice 7: cloneInstance(id, atSeq). Boots a REAL KernelHost over TWO real
// hosted instances — a lightweight design host (its schema alone gives it the IsDesignHost SHAPE, so
// KernelHost.StartAsync populates _designHostStore, the era-resolution lookup this slice reads) and a
// target app. Host actions are driven through a DIRECTLY-CONSTRUCTED KernelHostActions — the same
// convention Publish.feature/PublishSteps.cs uses — rather than a real WebSocket: `commitDesign` is not
// (yet) in HostActionScan.UsesHostActions' AST-wired builtin list (a named, ledgered scope line from M13
// slice 3 — wiring it is the future Commit-button slice's job), so a lightweight fixture with no
// host-action-calling Code never gets a REAL seam through KernelHost.HostActionsFor; constructing
// KernelHostActions directly is the established bypass. Its `cloneInstance` delegate calls the REAL
// `_kernel.CloneAsync` (not a recording stub), so the actual materializer + era-resolution logic under
// test runs for real, reading the REAL _designHostStore a genuine KernelHost.StartAsync populated.
[Binding]
public sealed class TimeTravelSteps
{
    // A minimal design-host meta-schema: Db.designs (the IsDesignHost shape) + Commit/Branch (M13
    // slice 3's immutable rows) + an unconditional `sys` grant (no login machinery needed here — this
    // feature is about the clone materializer, not auth). Mirrors PublishSteps.MetaSchema/
    // KernelSteps.DesignHostApp, kept local so this file's harness is self-contained.
    private const string DesignerApp = """
    types
        Db
            designs set of Design
            instances set of Instance
            commits set of Commit
            branches set of Branch
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
        Instance
            name text
            runtimeId int
            design Design
        Commit
            message text
            at datetime
            design Design
            parent Commit
            logSeq int
            text text
            idMap dict of int by text
        Branch
            name text
            head Commit
            workingCopy Design

    access
        sys
            *
        Commit
            create edit delete where false
        Branch
            create edit delete where false
    """;

    // The target app: one object type ("Item") with a "title" field — the smallest fixture the
    // scenarios' several-writes / rename / era-schema proofs need.
    private const string TargetApp = """
    types
        Db
            items set of Item
        Item
            title text
    """;

    private const int DesignerId = 1;
    private const int TargetId = 2;

    private string _kernelDir = "";
    private int _appPort;
    private int _assetPort;
    private KernelHost? _kernel;

    private int _designId;
    private int _dbTypeId;
    private int _itemTypeId;
    private readonly Dictionary<string, int> _propIds = new();

    // ── Background: a real kernel hosting a designer + a target ─────────────────────────────────────

    [Given("a versioned designer instance and a target instance, both hosted by a real kernel")]
    public async Task GivenKernelWithDesignerAndTarget()
    {
        _kernelDir = Path.Combine(Path.GetTempPath(), "deenv-timetravel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_kernelDir);
        WriteIdApp(DesignerId, DesignerApp);
        WriteIdApp(TargetId, TargetApp);

        _appPort = PortAllocator.Next();
        _assetPort = PortAllocator.Next();
        var registryPath = RegistryPath;
        RegistryWriter.Write(registryPath, new Registry(
            [new RegistryEntry(DesignerId, "designer"), new RegistryEntry(TargetId, "target")],
            _appPort, _assetPort));

        _kernel = new KernelHost(_kernelDir, registryPath, _appPort, _assetPort, bindLoopback: true);
        await _kernel.StartAsync(KernelHost.SpecsFor(RegistryReader.Read(registryPath), _kernelDir));

        // Author a design mirroring the target's shape (Db.items set of Item; Item.title text) directly
        // on the LIVE design host store (the same pattern PublishSteps.AddDesign/AddType/AddProp use,
        // now against a REAL kernel-hosted instance rather than a bare store).
        var designer = DesignerStore();
        _designId = designer.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["label"] = new TextValue("app"), ["initialData"] = new TextValue(""),
            ["access"] = new TextValue(""), ["common"] = new TextValue(""), ["ui"] = new TextValue(""),
        }));
        designer.AddToSet(NodePath.Root.Field("designs"), _designId);
        var branchId = designer.CreateObject("Branch", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("main"),
        }));
        designer.AddToSet(NodePath.Root.Field("branches"), branchId);
        designer.WriteReference(branchId, "workingCopy", _designId, "Design");

        var typesPath = NodePath.Root.Field("designs").Key(_designId.ToString()).Field("types");
        _dbTypeId = designer.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("Db"), ["baseType"] = new TextValue("object"), ["order"] = new IntValue(0),
        }));
        designer.AddToSet(typesPath, _dbTypeId);
        _itemTypeId = designer.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("Item"), ["baseType"] = new TextValue("object"), ["order"] = new IntValue(0),
        }));
        designer.AddToSet(typesPath, _itemTypeId);

        AddProp(designer, _itemTypeId, "title", "text", cardinality: "");
        AddProp(designer, _dbTypeId, "items", "Item", cardinality: "set");
    }

    private void AddProp(IInstanceStore designer, int ownerTypeId, string name, string type, string cardinality)
    {
        var propsPath = NodePath.Root.Field("designs").Key(_designId.ToString())
            .Field("types").Key(ownerTypeId.ToString()).Field("props");
        var fields = new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name), ["type"] = new TextValue(type), ["order"] = new IntValue(0),
        };
        if (cardinality.Length > 0) fields["cardinality"] = new TextValue(cardinality);
        var id = designer.CreateObject("MetaProp", new ObjectValue(fields));
        designer.AddToSet(propsPath, id);
        _propIds[name] = id;
    }

    private void WriteIdApp(int id, string appDoc)
    {
        var idDir = AppPaths.IdDirFor(_kernelDir, id);
        Directory.CreateDirectory(idDir);
        File.WriteAllText(Path.Combine(idDir, "app.deenv"), appDoc);
    }

    // ── Given/When/Then ──────────────────────────────────────────────────────────────────────────────

    private string RegistryPath => Path.Combine(_kernelDir, "kernel.json");

    // A FRESH store over the design host's OWN files, re-read from disk every call — never the live
    // kernel-hosted instance's cached in-memory Store object. KernelHostActions' own internals (CommitDesign/
    // Publish/ResolveDesign) ALSO always open a fresh `new JsonFileInstanceStore(metaAppPath, dataPath)`
    // per call (see KernelHostActions.cs) rather than reusing one long-lived instance — nothing here ever
    // restarts the DESIGN HOST itself (only `publish`'s restartInstance touches the TARGET), so its
    // HostedInstance.Store reference would otherwise go stale the moment any KernelHostActions write lands
    // on the same file through a DIFFERENT in-memory JsonFileInstanceStore object. Re-opening fresh here
    // keeps every read looking at ground truth, exactly the convention PublishSteps.FreshDesigner() uses.
    private IInstanceStore DesignerStore()
    {
        var spec = _kernel!.Instances.Single(i => i.Spec.Id == DesignerId).Spec;
        return new JsonFileInstanceStore(spec.DataPath, InstanceDescriptionLoader.LoadFile(spec.SchemaPath));
    }

    private IInstanceStore TargetStore() => _kernel!.Instances.Single(i => i.Spec.Id == TargetId).Store;

    [Given("the target holds an {string} titled {string}")]
    public void GivenTargetHoldsItemTitled(string typeName, string title)
    {
        _ = typeName;
        var store = TargetStore();
        var id = store.CreateObject("Item", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["title"] = new TextValue(title),
        }));
        store.AddToSet(NodePath.Root.Field("items"), id);
    }

    // One write to the target's Item title (seeding the Item first if none exists yet) — each call is
    // its own log entry, so a scenario chains several of these to build the mid-history the
    // seq-remembering step anchors into.
    [Given("the target's {string} title is written to {string}")]
    public void GivenTargetTitleWritten(string typeName, string value)
    {
        _ = typeName;
        var store = TargetStore();
        var existing = store.ReadExtent("Item").Keys.FirstOrDefault();
        var id = existing != 0 ? existing : store.CreateObject("Item", new ObjectValue(new Dictionary<string, NodeValue>()));
        if (existing == 0) store.AddToSet(NodePath.Root.Field("items"), id);
        store.WriteField(id, "title", new TextValue(value));
    }

    private readonly Dictionary<string, int> _rememberedSeqs = new();

    // Remembers the TARGET's current log seq under an alias — placed inline between the Gherkin steps
    // whose moment it needs to capture (Given steps run in the written order, so this reads the seq
    // exactly as of the point it appears in the scenario, not after some later step has already moved on).
    [Given("the current seq is remembered as {string}")]
    public void GivenCurrentSeqRemembered(string alias) => _rememberedSeqs[alias] = TargetStore().CurrentVersion;

    // ── design authoring + commit + publish (a directly-constructed KernelHostActions — see HostActions()) ──

    [Given("the design's {string} prop {string} is renamed to {string} for time travel")]
    public void GivenDesignPropRenamed(string typeName, string from, string to)
    {
        _ = typeName;
        var propId = _propIds[from];
        DesignerStore().WriteField(propId, "name", new TextValue(to));
        _propIds.Remove(from);
        _propIds[to] = propId;
    }

    [Given("the time-travel design is committed with message {string}")]
    public void GivenDesignCommitted(string message) =>
        HostActions().Run("commitDesign", ArgsOf(
            $$"""{ "type": "int", "value": {{_designId}} }""", $$"""{ "type": "text", "value": "{{message}}" }"""));

    [Given("the time-travel designer publishes the design's head commit to the target over the WS")]
    public void GivenDesignerPublishes() =>
        HostActions().Run("publish", ArgsOf(
            $$"""{ "type": "int", "value": {{_designId}} }""", $$"""{ "type": "int", "value": {{TargetId}} }"""));

    // ── the clone action itself (a directly-constructed KernelHostActions whose cloneInstance delegate
    //    calls the REAL KernelHost.CloneAsync — see the file header) ─────────────────────────────────

    private Exception? _cloneError;
    private HostedInstance? _clonedInstance;

    [When("the operator clones the target at the remembered {string} seq")]
    public void WhenOperatorClonesAtRememberedSeq(string alias) => RunClone(_rememberedSeqs[alias]);

    [When("the operator clones the target with no seq given")]
    public void WhenOperatorClonesWithNoSeq() => RunClone(atSeq: null);

    [When("the operator clones the target at a seq far past the head")]
    public void WhenOperatorClonesFarPastHead() => RunClone(TargetStore().CurrentVersion + 1000);

    [When("the operator clones the target at seq -1")]
    public void WhenOperatorClonesNegative() => RunClone(-1);

    [When("the operator clones the target at its current head seq")]
    public void WhenOperatorClonesAtHead() => RunClone(TargetStore().CurrentVersion);

    private void RunClone(int? atSeq)
    {
        var before = _kernel!.Instances.Select(i => i.Spec.Id).ToHashSet();
        var args = atSeq.HasValue
            ? ArgsOf($$"""{ "type": "int", "value": {{TargetId}} }""", $$"""{ "type": "int", "value": {{atSeq.Value}} }""")
            : ArgsOf($$"""{ "type": "int", "value": {{TargetId}} }""");
        try
        {
            HostActions().Run("cloneInstance", args);
            _cloneError = null;
            var newId = _kernel!.Instances.Select(i => i.Spec.Id).Except(before).Single();
            _clonedInstance = _kernel!.Instances.Single(i => i.Spec.Id == newId);
        }
        catch (Exception ex)
        {
            _cloneError = ex;
            _clonedInstance = null;
        }
    }

    private HostedInstance Clone => _clonedInstance
        ?? throw new InvalidOperationException(
            $"No clone was created (the last clone attempt failed): {_cloneError}");

    // A directly-constructed KernelHostActions over the design host's OWN files (mirrors
    // PublishSteps.Ws()'s hostActions ctor) — its cloneInstance delegate calls the REAL
    // _kernel.CloneAsync (not a recording stub) and its restart/stamp/publishedCommitId delegates go
    // through the REAL kernel + registry, so era resolution reads the REAL _designHostStore.
    private KernelHostActions HostActions()
    {
        var designerSpec = _kernel!.Instances.Single(i => i.Spec.Id == DesignerId).Spec;
        return new KernelHostActions(
            designerSpec.SchemaPath, designerSpec.DataPath,
            id => id == TargetId ? _kernel!.Instances.Single(i => i.Spec.Id == TargetId).Spec : null,
            createInstance: (_, _, _) => throw new InvalidOperationException("create not exercised by TimeTravel.feature"),
            deleteInstance: _ => throw new InvalidOperationException("delete not exercised by TimeTravel.feature"),
            cloneInstance: (sourceId, atSeq) =>
            {
                var source = _kernel!.Instances.Single(i => i.Spec.Id == sourceId);
                return _kernel!.CloneAsync(source, _kernelDir, RegistryPath, atSeq);
            },
            recordDesign: (_, _) => throw new InvalidOperationException("setDesign not exercised by TimeTravel.feature"),
            restartInstance: id => _kernel!.RestartAsync(id),
            renameInstance: (_, _) => throw new InvalidOperationException("rename not exercised by TimeTravel.feature"),
            readPublishedCommitId: id => KernelHost.ReadPublishedCommitId(id, RegistryPath),
            stampPublishedCommit: (id, commitId) => KernelHost.StampPublishedCommitAsync(id, commitId, RegistryPath));
    }

    // A bare Code-args JSON array from pre-built scalar-literal fragments (the same `{ "type", "value" }`
    // shape KernelHostActions.ArgInt/ArgText/ArgIntOptional parse), matching PublishSteps'/HostActionSteps'
    // convention of hand-building the wire args a real Code call would evaluate to.
    private static JsonElement ArgsOf(params string[] argJsonFragments) =>
        JsonDocument.Parse("[" + string.Join(",", argJsonFragments) + "]").RootElement;

    // ── Then: the clone's / source's data and files ─────────────────────────────────────────────────

    [Then("the clone's {string} title reads {string}")]
    public async Task ThenCloneTitleReads(string typeName, string value)
    {
        _ = typeName;
        var item = Clone.Store.ReadExtent("Item").Values.First();
        await Assert.That((item.Fields.GetValueOrDefault("title") as TextValue)?.Text ?? "").IsEqualTo(value);
    }

    [Then("that clone's {string} reads {string} as {string}")]
    public async Task ThenCloneFieldReadsAs(string typeName, string field, string value)
    {
        _ = typeName;
        var item = Clone.Store.ReadExtent("Item").Values.First();
        await Assert.That((item.Fields.GetValueOrDefault(field) as TextValue)?.Text ?? "").IsEqualTo(value);
    }

    [Then("the source still reads {string} title as {string}")]
    public async Task ThenSourceStillReads(string typeName, string value)
    {
        _ = typeName;
        var item = TargetStore().ReadExtent("Item").Values.First();
        await Assert.That((item.Fields.GetValueOrDefault("title") as TextValue)?.Text).IsEqualTo(value);
    }

    [Then("that clone's app document declares {string} prop {string}")]
    public async Task ThenCloneDeclaresProp(string typeName, string propName)
    {
        var desc = InstanceDescriptionLoader.LoadFile(Clone.Spec.SchemaPath);
        var type = desc.FindType(typeName);
        await Assert.That(type?.Props?.Any(p => p.Name == propName) ?? false).IsTrue();
    }

    [Then("that clone's app document does not declare {string} prop {string}")]
    public async Task ThenCloneDoesNotDeclareProp(string typeName, string propName)
    {
        var desc = InstanceDescriptionLoader.LoadFile(Clone.Spec.SchemaPath);
        var type = desc.FindType(typeName);
        await Assert.That(type?.Props?.Any(p => p.Name == propName) ?? false).IsFalse();
    }

    [Then("the clone has no log or genesis files yet")]
    public async Task ThenCloneHasNoLogFilesYet()
    {
        var logPath = AppPaths.LogPathForId(_kernelDir, Clone.Spec.Id);
        var genesisPath = AppPaths.GenesisPathForId(_kernelDir, Clone.Spec.Id);
        await Assert.That(File.Exists(logPath)).IsFalse();
        await Assert.That(File.Exists(genesisPath)).IsFalse();
    }

    private int _targetLogLinesBefore;

    [Given("the target's own log line count is remembered for time travel")]
    public void GivenTargetLogLinesRemembered() => _targetLogLinesBefore = TargetLogLineCount();

    private int TargetLogLineCount()
    {
        var path = AppPaths.LogPathForId(_kernelDir, TargetId);
        return File.Exists(path) ? File.ReadAllLines(path).Length : 0;
    }

    [Then("the source's log did not grow")]
    public async Task ThenSourceLogDidNotGrow() =>
        await Assert.That(TargetLogLineCount()).IsEqualTo(_targetLogLinesBefore);

    // The Item's own extent id — a tiny local helper since ReadExtent returns fields keyed by id.
    private static int ExtentIdOf(IInstanceStore store, string typeName) => store.ReadExtent(typeName).Keys.First();

    [When("the clone's {string} title is written to {string}")]
    public void WhenCloneTitleWritten(string typeName, string value)
    {
        _ = typeName;
        var id = ExtentIdOf(Clone.Store, "Item");
        Clone.Store.WriteField(id, "title", new TextValue(value));
    }

    [Then("the clone's log holds exactly one entry")]
    public async Task ThenCloneLogHoldsOneEntry()
    {
        var path = AppPaths.LogPathForId(_kernelDir, Clone.Spec.Id);
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(File.ReadAllLines(path).Length).IsEqualTo(1);
    }

    // The clone's OWN genesis freezes at whatever version the MATERIALIZED doc carries (AppLogReplay.Apply
    // stamps doc.Version = entry.Seq per entry folded) — i.e. the remembered atSeq itself, NOT zero. This
    // mirrors the ORDINARY (atSeq-less) clone too: a plain file copy carries the source's CURRENT version
    // verbatim, so ITS genesis also freezes at whatever that was, never a hard reset to 0 — a time-travel
    // clone's own version numbering simply continues from the snapshot point, only its LOG/history is fresh.
    [Then("the clone's genesis is frozen at the remembered {string} seq")]
    public async Task ThenCloneGenesisFrozenAtRememberedSeq(string alias)
    {
        var path = AppPaths.GenesisPathForId(_kernelDir, Clone.Spec.Id);
        await Assert.That(File.Exists(path)).IsTrue();
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new StoredValueConverter(), new LogWriteConverter() },
        };
        var genesis = JsonSerializer.Deserialize<GenesisFile>(File.ReadAllText(path), opts)!;
        await Assert.That(genesis.GenesisSeq).IsEqualTo(_rememberedSeqs[alias]);
    }

    [Then("the clone's {string} id equals the source's {string} id")]
    public async Task ThenCloneItemIdEqualsSourceItemId(string cloneType, string sourceType)
    {
        _ = (cloneType, sourceType);
        var cloneId = ExtentIdOf(Clone.Store, "Item");
        var sourceId = ExtentIdOf(TargetStore(), "Item");
        await Assert.That(cloneId).IsEqualTo(sourceId);
    }

    [Then("the clone's app document is byte-identical to the target's")]
    public async Task ThenCloneAppByteIdentical() =>
        await Assert.That(File.ReadAllText(Clone.Spec.SchemaPath)).IsEqualTo(File.ReadAllText(TargetSchemaPath));

    private string TargetSchemaPath => AppPaths.SchemaPathForId(_kernelDir, TargetId);

    private int _hostedCountBefore;

    [Given("the kernel's hosted instance count is remembered")]
    public void GivenHostedCountRemembered() => _hostedCountBefore = _kernel!.Instances.Count;

    [Then("the kernel's hosted instance count is unchanged")]
    public async Task ThenHostedCountUnchanged() =>
        await Assert.That(_kernel!.Instances.Count).IsEqualTo(_hostedCountBefore);

    [Then("the clone attempt fails")]
    public async Task ThenCloneAttemptFails() => await Assert.That(_cloneError).IsNotNull();

    [Then("the target's own log fsck holds for time travel")]
    public async Task ThenTargetLogFsckHolds() =>
        await Assert.That(((JsonFileInstanceStore)TargetStore()).Fsck()).IsTrue();

    [AfterScenario]
    public async Task Cleanup()
    {
        try { if (_kernel is not null) await _kernel.DisposeAsync(); } catch { /* best-effort */ }
        try { if (Directory.Exists(_kernelDir)) Directory.Delete(_kernelDir, recursive: true); } catch { /* best-effort */ }
    }
}
