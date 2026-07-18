using System.Text.Json;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Kernel;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// SingleStorePerFile.feature — the data-loss class where several JsonFileInstanceStore instances write
// one file (the design host's) in one kernel process. Boots a REAL KernelHost over a design host + a
// target (the same fixture shape TimeTravel.feature uses), drives commit/clone through a
// directly-constructed KernelHostActions, and checks the design's commit history + WAL after mirror
// writes / live-session edits — the exact interleavings that used to clobber the snapshot and collide
// log seqs. This harness deliberately reads the DESIGN HOST through its LIVE hosted store (not a fresh
// re-open) so the fix's "one store per file" is what makes these pass.
[Binding]
public sealed class SingleStorePerFileSteps
{
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
            types list of MetaType
        MetaType
            name text
            baseType text
            values text
            order int
            props list of MetaProp
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
            migration text
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

    [Given("a versioned designer instance and a target instance, both hosted by a single-store kernel")]
    public async Task GivenKernel()
    {
        _kernelDir = Path.Combine(Path.GetTempPath(), "deenv-singlestore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_kernelDir);
        WriteIdApp(DesignerId, DesignerApp);
        WriteIdApp(TargetId, TargetApp);

        _appPort = PortAllocator.Next();
        _assetPort = PortAllocator.Next();
        RegistryWriter.Write(RegistryPath, new Registry(
            [new RegistryEntry(DesignerId, "designer"), new RegistryEntry(TargetId, "target")],
            _appPort, _assetPort));

        _kernel = new KernelHost(_kernelDir, RegistryPath, _appPort, _assetPort, bindLoopback: true);
        await _kernel.StartAsync(KernelHost.SpecsFor(RegistryReader.Read(RegistryPath), _kernelDir));

        // Author a design (Db.items set of Item; Item.title text) on the LIVE design host store — the
        // same store the WS session serves from. With one-store-per-file this is the ONLY store over the
        // file, so authoring here is seen by every later read.
        var designer = LiveDesignerStore();
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
            ["name"] = new TextValue("Db"), ["baseType"] = new TextValue("object"),
        }));
        DesignerListHelpers.AppendToList(designer, typesPath, _dbTypeId, "MetaType");
        _itemTypeId = designer.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("Item"), ["baseType"] = new TextValue("object"),
        }));
        DesignerListHelpers.AppendToList(designer, typesPath, _itemTypeId, "MetaType");

        AddProp(designer, _itemTypeId, "title", "text", cardinality: "");
        AddProp(designer, _dbTypeId, "items", "Item", cardinality: "set");
    }

    private void AddProp(IInstanceStore designer, int ownerTypeId, string name, string type, string cardinality)
    {
        var propsPath = NodePath.Root.Field("designs").Key(_designId.ToString())
            .Field("types").Key(ownerTypeId.ToString()).Field("props");
        var fields = new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name), ["type"] = new TextValue(type),
        };
        if (cardinality.Length > 0) fields["cardinality"] = new TextValue(cardinality);
        var id = designer.CreateObject("MetaProp", new ObjectValue(fields));
        DesignerListHelpers.AppendToList(designer, propsPath, id, "MetaProp");
        _propIds[name] = id;
    }

    private void WriteIdApp(int id, string appDoc)
    {
        var idDir = AppPaths.IdDirFor(_kernelDir, id);
        Directory.CreateDirectory(idDir);
        File.WriteAllText(Path.Combine(idDir, "app.deenv"), appDoc);
    }

    private string RegistryPath => Path.Combine(_kernelDir, "kernel.json");

    // The LIVE hosted design host's OWN store — the one the WS session serves renders from. This is the
    // whole point of the fix: after "one store per file", reads through this store see the mirror/commit
    // writes immediately, and there is only ONE writer to the file.
    private IInstanceStore LiveDesignerStore() =>
        _kernel!.Instances.Single(i => i.Spec.Id == DesignerId).Store;

    private IInstanceStore TargetStore() => _kernel!.Instances.Single(i => i.Spec.Id == TargetId).Store;

    // ── commit + clone via a directly-constructed KernelHostActions (the TimeTravel/Publish convention) ──

    [Given("the designer commits the design with message {string}")]
    public void GivenDesignerCommits(string message) =>
        HostActions().Run("commitDesign", ArgsOf(
            $$"""{ "type": "int", "value": {{_designId}} }""", $$"""{ "type": "text", "value": "{{message}}" }""",
            """{ "type": "text", "value": "" }"""));

    private int _committedCount;

    [Given("the design's committed count is remembered")]
    public void GivenCommittedCountRemembered() => _committedCount = CommitCount();

    [When("the operator clones the target")]
    [When("the operator clones the target again")]
    public void WhenOperatorClones() =>
        HostActions().Run("cloneInstance", ArgsOf($$"""{ "type": "int", "value": {{TargetId}} }"""));

    [When("a label is edited through the live designer session's own store")]
    public void WhenLiveEdit() =>
        LiveDesignerStore().WriteField(_designId, "label", new TextValue("edited-live"));

    private KernelHostActions HostActions()
    {
        return new KernelHostActions(
            // The design host's ONE LIVE store — the same instance the mirror writes + live WS session use.
            () => _kernel!.Instances.Single(i => i.Spec.Id == DesignerId).Store,
            callerId: DesignerId,
            id => id == TargetId ? _kernel!.Instances.Single(i => i.Spec.Id == TargetId).Spec : null,
            createInstance: (_, _, _) => throw new InvalidOperationException("create not exercised here"),
            deleteInstance: _ => throw new InvalidOperationException("delete not exercised here"),
            cloneInstance: (sourceId, atSeq) =>
            {
                var source = _kernel!.Instances.Single(i => i.Spec.Id == sourceId);
                return _kernel!.CloneAsync(source, _kernelDir, RegistryPath, atSeq);
            },
            recordDesign: (_, _) => throw new InvalidOperationException("setDesign not exercised here"),
            restartInstance: id => _kernel!.RestartAsync(id),
            renameInstance: (_, _) => throw new InvalidOperationException("rename not exercised here"),
            readPublishedCommitId: id => KernelHost.ReadPublishedCommitId(id, RegistryPath),
            stampPublishedCommit: (id, commitId) => KernelHost.StampPublishedCommitAsync(id, commitId, RegistryPath));
    }

    private static JsonElement ArgsOf(params string[] argJsonFragments) =>
        JsonDocument.Parse("[" + string.Join(",", argJsonFragments) + "]").RootElement;

    // ── assertions: the design's history survives ON DISK (ground truth) ────────────────────────────
    // These read a FRESH store re-opened over the design host's files — i.e. what a reboot, or any next
    // fresh KernelHostActions store, would see. That is exactly the surface the mirror-clobber destroys:
    // a fresh-store commit lands on disk, then a stale-`_doc` mirror/live write rewrites the snapshot
    // WITHOUT it. Reading the live in-memory `_doc` would hide the clobber (it never saw the fresh-store
    // commit either), so ground-truth-on-disk is the honest check. `_committedCount` is captured the same
    // way (a fresh disk read immediately after the commit), so the assertion is "the count on disk did not
    // regress," not a trivially-equal pair.

    private IInstanceStore FreshDesignerStore()
    {
        var spec = _kernel!.Instances.Single(i => i.Spec.Id == DesignerId).Spec;
        return new JsonFileInstanceStore(spec.DataPath, InstanceDescriptionLoader.LoadFile(spec.SchemaPath));
    }

    private int CommitCount() =>
        FreshDesignerStore().ReadExtent("Commit").Values
            .Count(c => c.Fields.GetValueOrDefault("design") is ReferenceValue { TargetId: { } t } && t == _designId);

    [Then("the design still has its committed count of commits")]
    public async Task ThenCommitCountSurvives()
    {
        // A live sanity precondition: the count we remembered was actually non-zero (the commit really
        // landed on disk) — otherwise "survives" would be vacuous.
        await Assert.That(_committedCount).IsGreaterThan(0);
        await Assert.That(CommitCount()).IsEqualTo(_committedCount);
    }

    [Then("the design's main branch still has a head commit")]
    public async Task ThenMainBranchHasHead()
    {
        var branch = FreshDesignerStore().ReadExtent("Branch").Values.FirstOrDefault(b =>
            b.Fields.GetValueOrDefault("workingCopy") is ReferenceValue { TargetId: { } t } && t == _designId);
        var head = branch?.Fields.GetValueOrDefault("head") as ReferenceValue;
        await Assert.That(head?.TargetId).IsNotNull();
    }

    [Then("the designer's log fsck holds for single store")]
    public async Task ThenDesignerFsckHolds() =>
        await Assert.That(((JsonFileInstanceStore)FreshDesignerStore()).Fsck()).IsTrue();

    // No two log entries share a Seq — the WAL-collision half of the sibling leg (a stale _doc.Version
    // minting a seq that duplicates a fresh store's already-appended entry).
    [Then("the designer's log has no duplicate seqs")]
    public async Task ThenNoDuplicateSeqs()
    {
        var seqs = DesignerLogSeqs();
        await Assert.That(seqs.Count).IsEqualTo(seqs.Distinct().Count());
    }

    private IReadOnlyList<long> DesignerLogSeqs()
    {
        var path = AppPaths.LogPathForId(_kernelDir, DesignerId);
        if (!File.Exists(path)) return [];
        var seqs = new List<long>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("seq", out var s)) seqs.Add(s.GetInt64());
        }
        return seqs;
    }

    // ── mirror-visible-live leg ─────────────────────────────────────────────────────────────────────

    private int _instanceRowsBefore;

    [Given("the live designer store's Instance-row count is remembered")]
    public void GivenInstanceRowsRemembered() => _instanceRowsBefore = InstanceRowCount();

    private int InstanceRowCount() => LiveDesignerStore().ReadExtent("Instance").Count;

    [Then("the live designer store holds one more Instance row than before")]
    public async Task ThenOneMoreInstanceRow() =>
        await Assert.That(InstanceRowCount()).IsEqualTo(_instanceRowsBefore + 1);

    [AfterScenario]
    public async Task Cleanup()
    {
        try { if (_kernel is not null) await _kernel.DisposeAsync(); } catch { /* best-effort */ }
        try { if (Directory.Exists(_kernelDir)) Directory.Delete(_kernelDir, recursive: true); } catch { /* best-effort */ }
    }
}
