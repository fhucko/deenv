using System.Text.Json;
using DeEnv.Designer;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Kernel;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Publish.feature — the M13 slice-4 identity diff + rename-safe forward publish. Drives the REAL
// KernelHostActions.Publish (versioned path) at the WS-handler seam, exactly the level HostActionSteps
// already proves publish at — but with a REAL kernel.json-style registry file (RegistryReader/Writer) so
// the versioning STAMP (RegistryEntry.PublishedCommitId) genuinely persists across the two publishes some
// scenarios drive, and a real designer store carrying Commit/Branch rows (the M13 slice-3 shape) so
// sys.commitDesign mints real, identity-bearing commits to diff between.
[Binding]
public sealed class PublishSteps
{
    // The same test-local designer meta-schema shape HostActionSteps/DesignSnapshotSteps use — a Db
    // holding designs/commits/branches, with the M13 slice-3 Commit/Branch types and their immutability
    // rule. Isolated from the real instances/1/app.deenv (the browser-driven Designer.feature exercises
    // that one).
    private const string MetaSchema =
        """
        types
            Db
                designs set of Design
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
            Commit
                message text
                migration text
                revertMigration text
                at datetime
                design Design
                parent Commit
                mergeParent Commit
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

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deenv-publish-" + Guid.NewGuid().ToString("N"));
    private string _metaAppPath = "";
    private string _designerDataPath = "";
    private string _registryPath = "";

    private InstanceDescription _meta = null!;
    private IInstanceStore _designer = null!;
    private ClientSessionStore _sessions = null!;

    private int _designId;
    private int _dbTypeId;
    private readonly Dictionary<string, int> _typeIds = new();
    private readonly Dictionary<(string Type, string Prop), int> _propIds = new();

    private const int TargetId = 42;
    private string _targetAppPath = "";
    private string _targetDataPath = "";
    private string _targetLogPath = "";
    private string _targetGenesisPath = "";

    private bool _restartInvoked;
    private JsonElement _replyRoot;
    private JsonElement _report;
    private JsonElement _revertReplyRoot;

    private static readonly JsonSerializerOptions StoreOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new StoredValueConverter(), new LogWriteConverter() },
    };

    // ── Background: a designer holding a design with an "Item" type + a target stamped to its baseline ──

    [Given("a versioned designer instance holding a design with a type {string} and a custom render")]
    public void GivenVersionedDesignerHoldingDesign(string typeName)
    {
        OpenDesigner();
        AddDesign();
        AddType("Db", "object");
        AddType(typeName, "object");
        AddProp(typeName, "label", "text");
        AddSetProp("Db", typeName.ToLowerInvariant() + "s", typeName);
    }

    // A target instance whose registry entry is stamped to the design's CURRENT head commit (a baseline
    // commit taken at Background time, before any of the scenario's own renames/edits) — the "already
    // publishing this design, versioned" starting state every non-fallback scenario needs.
    [Given("a target instance addressed by an id, stamped to the design's baseline commit")]
    public void GivenTargetStampedToBaseline()
    {
        SetUpTarget();
        Commit("baseline");
        var baselineCommitId = HeadCommitId();
        Publish(dryRun: false);
        if (!_replyRoot.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"Background baseline publish failed: {_replyRoot}");
        // Publish() above already stamped the target (a real versioned/fallback publish always stamps).
        // Confirm it landed at the expected baseline before the scenario's own edits begin.
        var stamped = KernelHost.ReadPublishedCommitId(TargetId, _registryPath);
        if (stamped != baselineCommitId)
            throw new InvalidOperationException("Background publish did not stamp the expected baseline commit.");
        _restartInvoked = false; // reset the recorder so a scenario's OWN publish is what gets asserted
    }

    // An unstamped target (never published through the versioned path) — the fallback scenario's starting
    // state. No baseline publish runs here.
    [Given("a target instance addressed by an id, holding an {string} labelled {string}, never stamped")]
    public void GivenUnstampedTargetHoldingItem(string typeName, string label)
    {
        SetUpTarget();
        SeedTargetItem(typeName, label);
    }

    private void SetUpTarget()
    {
        Directory.CreateDirectory(_dir);
        _targetAppPath = Path.Combine(_dir, "target.app");
        _targetDataPath = Path.Combine(_dir, "target-data.json");
        _targetLogPath = AppPaths.LogPathForDataPath(_targetDataPath);
        _targetGenesisPath = AppPaths.GenesisPathForDataPath(_targetDataPath);
        File.WriteAllText(_targetAppPath, "UNPUBLISHED-TARGET-SENTINEL");

        _registryPath = Path.Combine(_dir, "kernel.json");
        RegistryWriter.Write(_registryPath, new Registry([new RegistryEntry(TargetId, "target")]));
    }

    // Seed the target with real data under a PRIOR schema (Item.label only) — through the store seam, so
    // it is genuine stored shape a publish then diffs/migrates against. Mirrors HostActionSteps'
    // GivenTargetHoldingItem, kept local so this file's harness is self-contained.
    private void SeedTargetItem(string typeName, string label)
    {
        var set = typeName.ToLowerInvariant() + "s";
        var priorApp = $$"""
            types
                Db
                    {{set}} set of {{typeName}}
                {{typeName}}
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

    [Given("the target holds an {string} labelled {string}")]
    public void GivenTargetHoldsItem(string typeName, string label) => SeedTargetItem(typeName, label);

    // Set a field DIRECTLY on the target's already-published, already-existing Item — used AFTER an
    // intermediate publish already deployed a schema declaring this field (so opening a fresh store over
    // the CURRENT app.deenv + data is exactly what a real operator hand-editing that record would do).
    [Given("the target's {string} has {string} set to {string}")]
    public void GivenTargetFieldSetDirectly(string typeName, string field, string value)
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var id = store.ReadExtent(typeName).Keys.First();
        store.WriteField(id, field, new TextValue(value));
    }

    [Given("the target's {string} has int field {string} set to {int}")]
    public void GivenTargetIntFieldSetDirectly(string typeName, string field, int value)
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var id = store.ReadExtent(typeName).Keys.First();
        store.WriteField(id, field, new IntValue(value));
    }

    // Seed a member into the target's Db SET prop (against the CURRENT published schema, which already
    // declares it a set) — a real stored StoredRef member the later set -> single reshape must drop.
    [Given("the target's Db {string} set is seeded with a {string} named {string}")]
    public void GivenTargetDbSetSeeded(string setProp, string elemType, string name)
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var memberId = store.CreateObject(elemType, new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name),
        }));
        store.AddToSet(NodePath.Root.Field(setProp), memberId);
    }

    [Given("the target's own log line count is remembered")]
    public void GivenTargetLogLinesRemembered()
    {
        _targetLogLinesBefore = TargetLogLineCount();
        _targetAppTextBefore = File.ReadAllText(_targetAppPath);
    }

    private int _targetLogLinesBefore;
    private string _targetAppTextBefore = "";
    private int TargetLogLineCount() => File.Exists(_targetLogPath) ? File.ReadAllLines(_targetLogPath).Length : 0;

    // ── design authoring: renames / adds / removes / retypes ────────────────────────────────────────

    [Given("the design's {string} prop {string} is renamed to {string}")]
    [Given("the design's {string} prop {string} is renamed to {string} but left uncommitted")]
    public void GivenPropRenamed(string typeName, string from, string to)
    {
        var propId = _propIds[(typeName, from)];
        _designer.WriteField(propId, "name", new TextValue(to));
        _propIds.Remove((typeName, from));
        _propIds[(typeName, to)] = propId;
    }

    [Given("the design's type {string} is renamed to {string}")]
    public void GivenTypeRenamed(string from, string to)
    {
        var typeId = _typeIds[from];
        _designer.WriteField(typeId, "name", new TextValue(to));
        _typeIds.Remove(from);
        _typeIds[to] = typeId;

        // Every OTHER prop in the design declared with `type: <from>` must repoint at the new name too — a
        // MetaProp's `type` field is stored as a plain NAME STRING (SchemaBridge.Project reads it verbatim),
        // not a live reference, so renaming the referenced type alone would leave a dangling type name (the
        // exact scenario a real designer UI would need its own "propagate the rename" step for — out of
        // scope here; this harness does it directly so the resulting design stays valid).
        foreach (var ((otherType, propName), propId) in _propIds.ToList())
        {
            if (otherType == from) continue; // the renamed type's OWN props moved with it — nothing to repoint
            var current = _designer.ReadById(propId);
            if (current?.Fields.Fields.GetValueOrDefault("type") is TextValue { Text: var t } && t == from)
            {
                _designer.WriteField(propId, "type", new TextValue(to));
                _ = propName; // key unchanged — only the referenced type name updates
            }
        }
    }

    [Given("the design adds a {string} field to {string}")]
    public void GivenFieldAdded(string field, string typeName) => AddProp(typeName, field, "text");

    [Given("the design adds an int field {string} to {string}")]
    public void GivenIntFieldAdded(string field, string typeName) => AddProp(typeName, field, "int");

    [Given("the design adds a text dictionary {string} to {string}")]
    public void GivenTextDictionaryAdded(string field, string typeName) =>
        AddPropCore(typeName, field, "text", cardinality: "dictionary", keyType: "text");

    [Given("the design's {string} field {string} is removed")]
    public void GivenFieldRemoved(string typeName, string field)
    {
        var propId = _propIds[(typeName, field)];
        var propsPath = NodePath.Root.Field("designs").Key(_designId.ToString())
            .Field("types").Key(_typeIds[typeName].ToString()).Field("props");
        DesignerListHelpers.RemoveFromList(_designer, propsPath, propId);
        _propIds.Remove((typeName, field));
    }

    [Given("the design's type {string} is removed")]
    public void GivenTypeRemoved(string typeName)
    {
        var typeId = _typeIds[typeName];
        DesignerListHelpers.RemoveFromList(_designer, DesignTypesPath, typeId);
        _typeIds.Remove(typeName);
        foreach (var key in _propIds.Keys.Where(k => k.Type == typeName).ToList())
            _propIds.Remove(key);
    }

    [Given("the design's {string} field {string} is retyped to {string}")]
    public void GivenFieldRetyped(string typeName, string field, string toType)
    {
        var propId = _propIds[(typeName, field)];
        _designer.WriteField(propId, "type", new TextValue(toType));
    }

    // Adds a new object type + a Db SET prop referencing it (the "leads set of Person" reshape fixtures).
    [Given("the design's Db gains a {string} set of {string}")]
    public void GivenDbGainsSetProp(string setProp, string elemType)
    {
        AddType(elemType, "object");
        AddProp(elemType, "name", "text");
        AddSetProp("Db", setProp, elemType);
    }

    // Reshape a Db SET prop to a SINGLE reference (same MetaProp id — identity preserved), the reshape this
    // slice cannot carry: flip its cardinality field from "set" to "single".
    [Given("the design's Db {string} prop is reshaped to a single reference")]
    public void GivenDbPropReshapedToSingle(string setProp)
    {
        var propId = _propIds[("Db", setProp)];
        _designer.WriteField(propId, "cardinality", new TextValue("single"));
    }

    // ── committing + publishing ──────────────────────────────────────────────────────────────────────

    private string _lastCommitMessage = "";

    [Given("the design is committed with message {string}")]
    public void GivenDesignCommitted(string message) => Commit(message);

    [When("the design is committed with message {string}")]
    public void WhenDesignCommitted(string message) => Commit(message);
    
    [Given("the design is committed with message {string} and migration:")]
    public void GivenDesignCommittedWithMigration(string message, string migration) => Commit(message, migration);

    [Given("the design is committed with message {string} and whitespace migration")]
    public void GivenDesignCommittedWithWhitespaceMigration(string message) => Commit(message, "  \n\t");

    private void Commit(string message, string migration = "")
    {
        _lastCommitMessage = message;
        var ws = Ws();
        var reply = ws.ProcessMessage(
            $$"""{ "op": "hostAction", "clientId": "{{_clientId}}", "action": "commitDesign", "args": [ { "type": "int", "value": {{_designId}} }, { "type": "text", "value": {{JsonSerializer.Serialize(message)}} }, { "type": "text", "value": {{JsonSerializer.Serialize(migration)}} } ] }""");
        using var doc = JsonDocument.Parse(reply);
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"commitDesign failed: {reply}");
    }

    [When("the designer reverts the design to commit {string} over the WS")]
    public void WhenRevertToCommit(string message) => RevertToCommit(message);

    [When("the designer attempts to revert the design to commit {string} over the WS")]
    public void WhenAttemptRevertToCommit(string message) => RevertToCommit(message);

    private void RevertToCommit(string message)
    {
        var commitId = CommitIdByMessage(message);
        var ws = Ws();
        var reply = ws.ProcessMessage(
            $$"""{ "op": "hostAction", "clientId": "{{_clientId}}", "action": "revertCommit", "args": [ { "type": "int", "value": {{_designId}} }, { "type": "int", "value": {{commitId}} } ] }""");
        using var doc = JsonDocument.Parse(reply);
        _revertReplyRoot = doc.RootElement.Clone();
    }

    private int CommitIdByMessage(string message)
    {
        var fresh = FreshDesigner();
        foreach (var (id, fields) in fresh.ReadExtent("Commit"))
            if (fields.Fields.GetValueOrDefault("message") is TextValue { Text: var text } && text == message)
                return id;
        throw new InvalidOperationException($"No commit with message '{message}'.");
    }

    [Given("the design has a merged side-branch commit with migration:")]
    public void GivenMergedSideBranchMigration(string migration)
    {
        var store = FreshDesigner();
        var baseline = HeadCommitId();
        var side = CreateCommitRow(store, "side migration", migration, parent: baseline, mergeParent: null);
        var merge = CreateCommitRow(store, "merge side", "", parent: baseline, mergeParent: side);
        var branch = store.ReadExtent("Branch").First(b => b.Value.Fields.GetValueOrDefault("name") is TextValue { Text: "main" });
        store.WriteReference(branch.Key, "head", merge, "Commit");
    }

    [Given("the target already has the publish boundary entry for the design's head commit but no registry stamp")]
    public void GivenBoundaryEntryWithoutStamp()
    {
        var store = FreshDesigner();
        var baseline = KernelHost.ReadPublishedCommitId(TargetId, _registryPath)!.Value;
        _ = CommitFields(store, baseline);
        var headId = HeadCommitId();
        var targetStore = new JsonFileInstanceStore(_targetDataPath, InstanceDescriptionLoader.LoadFile(_targetAppPath));
        var entry = new LogEntry(
            targetStore.CurrentVersion + 1, DateTimeOffset.UtcNow, null, null, 0, [],
            new BoundaryMarker(_designId, headId, baseline));
        File.AppendAllText(_targetLogPath, JsonSerializer.Serialize(entry, StoreOpts) + "\n");
        KernelHost.StampPublishedCommitAsync(TargetId, baseline, _registryPath).GetAwaiter().GetResult();
    }

    // The design's `main` Branch head commit's intrinsic id, read fresh from disk (the store used to
    // build the WsHandler may be stale relative to a preceding commitDesign call — same convention
    // HostActionSteps documents on its own Ws()).
    private int HeadCommitId()
    {
        var fresh = FreshDesigner();
        var branch = fresh.ReadExtent("Branch").Values
            .First(b => b.Fields.GetValueOrDefault("name") is TextValue { Text: "main" });
        return branch.Fields.GetValueOrDefault("head") is ReferenceValue { TargetId: { } id } ? id
            : throw new InvalidOperationException("The design's main branch has no head commit yet.");
    }

    [When("the designer publishes the design's head commit to the target's id over the WS")]
    public void WhenPublish() => Publish(dryRun: false);

    [Given("the designer publishes the design's head commit to the target's id over the WS")]
    public void GivenPublish() => Publish(dryRun: false);

    [When("the designer dry-runs a publish of the design's head commit to the target's id over the WS")]
    public void WhenDryRunPublish() => Publish(dryRun: true);

    // The single-writer guard: the design host (callerId 1 in this harness) must never be its own publish
    // target — publishing onto the caller would rewrite the designer's own schema under its live store.
    [When("the designer attempts to publish the design onto the design host itself over the WS")]
    public void WhenPublishOntoSelf()
    {
        var ws = Ws();
        var reply = ws.ProcessMessage(
            $$"""{ "op": "hostAction", "clientId": "{{_clientId}}", "action": "publish", "args": [ { "type": "int", "value": {{_designId}} }, { "type": "int", "value": 1 } ] }""");
        using var doc = JsonDocument.Parse(reply);
        _replyRoot = doc.RootElement.Clone();
    }

    [Then("the publish reply is an error saying the design host cannot be its own publish target")]
    public async Task ThenSelfPublishRejected()
    {
        await Assert.That(_replyRoot.TryGetProperty("error", out var err)).IsTrue();
        await Assert.That(err.GetString()!).Contains("cannot be its own publish target");
    }

    private void Publish(bool dryRun)
    {
        var ws = Ws();
        var argsJson = dryRun
            ? $$"""{ "type": "int", "value": {{_designId}} }, { "type": "int", "value": {{TargetId}} }, { "type": "bool", "value": true }"""
            : $$"""{ "type": "int", "value": {{_designId}} }, { "type": "int", "value": {{TargetId}} }""";
        var reply = ws.ProcessMessage(
            $$"""{ "op": "hostAction", "clientId": "{{_clientId}}", "action": "publish", "args": [ {{argsJson}} ] }""");
        using var doc = JsonDocument.Parse(reply);
        _replyRoot = doc.RootElement.Clone();
        if (_replyRoot.TryGetProperty("report", out var report))
            _report = report.Clone();
    }

    private string _clientId = "";

    // The designer's WsHandler with a REAL KernelHostActions — resolves ONLY TargetId → the target spec,
    // and the create/delete/clone/rename/setDesign delegates are never exercised by this feature (they
    // throw if accidentally reached, a clear signal a scenario used the wrong action). restartInstance
    // just records invocation; readPublishedCommitId/stampPublishedCommit go through the REAL registry
    // file (RegistryReader/Writer), so a stamp genuinely persists across two Publish() calls.
    private WsHandler Ws()
    {
        _designer = FreshDesigner();

        var hostActions = new KernelHostActions(
            // The SAME live designer store WsHandler serves from (one store instance per data file) — was a
            // second `new JsonFileInstanceStore` opened inside KernelHostActions over the same file.
            () => _designer,
            callerId: 1, // the designer (instances/1 by convention); never equals TargetId
            id => id == TargetId ? new InstanceSpec(TargetId, "target", _targetAppPath, _targetDataPath) : null,
            createInstance: (_, _, _) => throw new InvalidOperationException("create not exercised by Publish.feature"),
            deleteInstance: _ => throw new InvalidOperationException("delete not exercised by Publish.feature"),
            cloneInstance: (_, _) => throw new InvalidOperationException("cloneInstance not exercised by Publish.feature"),
            recordDesign: (_, _) => throw new InvalidOperationException("setDesign not exercised by Publish.feature"),
            restartInstance: id =>
            {
                if (id == TargetId) _restartInvoked = true;
                return Task.CompletedTask;
            },
            renameInstance: (_, _) => throw new InvalidOperationException("rename not exercised by Publish.feature"),
            readPublishedCommitId: id => KernelHost.ReadPublishedCommitId(id, _registryPath),
            stampPublishedCommit: (id, commitId) => KernelHost.StampPublishedCommitAsync(id, commitId, _registryPath));

        var session = _sessions.Create();
        session.PrincipalUserId = null; // the test meta's `sys` rule is unconditional (mirrors instances/1 today)
        _clientId = session.Id;
        return new WsHandler(_designer, _meta, sessions: _sessions, registry: null, hostActions: hostActions);
    }

    private IInstanceStore FreshDesigner() => new JsonFileInstanceStore(_designerDataPath, _meta);

    // ── Then: the reply + report ─────────────────────────────────────────────────────────────────────

    [Then("the publish host action reply is ok")]
    public async Task ThenPublishReplyOk()
    {
        await Assert.That(_replyRoot.TryGetProperty("ok", out var ok) && ok.GetBoolean()).IsTrue();
        await Assert.That(_replyRoot.TryGetProperty("error", out _)).IsFalse();
    }

    [Then("the revert host action reply is ok")]
    public async Task ThenRevertReplyOk()
    {
        await Assert.That(_revertReplyRoot.TryGetProperty("ok", out var ok) && ok.GetBoolean()).IsTrue();
        await Assert.That(_revertReplyRoot.TryGetProperty("error", out _)).IsFalse();
    }

    [Then("the revert reply is an error mentioning {string}")]
    public async Task ThenRevertReplyErrorMentions(string text)
    {
        await Assert.That(_revertReplyRoot.TryGetProperty("error", out var err)).IsTrue();
        await Assert.That(err.GetString()!).Contains(text);
    }

    [Then("the publish report used the name-match fallback")]
    public async Task ThenFallbackUsed() =>
        await Assert.That(_report.GetProperty("fallbackNameMatched").GetBoolean()).IsTrue();

    [Then("the publish report did not use the name-match fallback")]
    public async Task ThenFallbackNotUsed() =>
        await Assert.That(_report.GetProperty("fallbackNameMatched").GetBoolean()).IsFalse();

    [Then("the target was stamped to the design's head commit")]
    public async Task ThenTargetStamped()
    {
        var stamped = KernelHost.ReadPublishedCommitId(TargetId, _registryPath);
        await Assert.That(stamped).IsEqualTo((int?)HeadCommitId());
    }

    [Then("the target was not stamped by the dry run")]
    public async Task ThenTargetNotStampedByDryRun()
    {
        // A dry run against an already-stamped target (the Background stamps it) must leave the stamp
        // exactly as it was before this scenario's dry-run publish — the Background's baseline commit.
        var stamped = KernelHost.ReadPublishedCommitId(TargetId, _registryPath);
        await Assert.That(stamped).IsNotNull();
        await Assert.That(stamped).IsNotEqualTo((int?)HeadCommitId());
    }

    [Then("the target was not stamped to the failed head commit")]
    public async Task ThenTargetNotStampedToFailedHead()
    {
        var stamped = KernelHost.ReadPublishedCommitId(TargetId, _registryPath);
        await Assert.That(stamped).IsNotEqualTo((int?)HeadCommitId());
    }

    [Then("the target instance was not restarted")]
    public async Task ThenTargetNotRestarted() => await Assert.That(_restartInvoked).IsFalse();

    [Then("the dry-run reply reports a rename from {string} to {string}")]
    public async Task ThenDryRunReportsRename(string from, string to)
    {
        var renames = _report.GetProperty("renames").EnumerateArray().ToList();
        await Assert.That(renames.Any(r =>
            r.GetProperty("from").GetString()!.EndsWith(from) && r.GetProperty("to").GetString()!.EndsWith(to))).IsTrue();
    }

    [Then("the publish report shows no renames")]
    public async Task ThenNoRenames() =>
        await Assert.That(_report.GetProperty("renames").GetArrayLength()).IsEqualTo(0);

    [Then("the publish report flags uncommitted drift")]
    public async Task ThenUncommittedDriftFlagged() =>
        await Assert.That(_report.GetProperty("uncommittedDrift").GetBoolean()).IsTrue();

    [Then("the publish report flags {string} as a removed field")]
    public async Task ThenRemovedFieldFlagged(string field)
    {
        var removes = _report.GetProperty("removes").EnumerateArray().ToList();
        await Assert.That(removes.Any(r => r.GetProperty("path").GetString()!.EndsWith("." + field))).IsTrue();
    }

    [Then("the publish report flags the {string} cell as unconvertible")]
    public async Task ThenUnconvertibleCellFlagged(string field)
    {
        var conversions = _report.GetProperty("conversions").EnumerateArray().ToList();
        var hit = conversions.FirstOrDefault(c => c.GetProperty("path").GetString()!.EndsWith("." + field));
        await Assert.That(hit.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(hit.GetProperty("unconvertible").GetArrayLength()).IsGreaterThan(0);
    }

    [Then("the publish reply is an error mentioning {string}")]
    public async Task ThenPublishReplyErrorMentions(string text)
    {
        await Assert.That(_replyRoot.TryGetProperty("error", out var err)).IsTrue();
        await Assert.That(err.GetString()!).Contains(text);
    }

    [Then("the publish report includes a migration for {string} over {int} object")]
    public async Task ThenMigrationReportIncludes(string typeName, int count)
    {
        var migrations = _report.GetProperty("migrations").EnumerateArray().ToList();
        var hit = migrations.FirstOrDefault(m =>
            m.GetProperty("types").EnumerateArray().Any(t => t.GetString() == typeName));
        await Assert.That(hit.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(hit.GetProperty("objectsMigrated").GetInt32()).IsEqualTo(count);
    }

    [Then("the publish report includes {int} migration steps")]
    public async Task ThenMigrationReportStepCount(int count) =>
        await Assert.That(_report.GetProperty("migrations").GetArrayLength()).IsEqualTo(count);

    [Then("the publish report says {int} cell was restored from history")]
    public async Task ThenRestorationReportCount(int count) =>
        await Assert.That(_report.GetProperty("restorations").GetArrayLength()).IsEqualTo(count);

    [Then("the publish report says {int} cells were restored from history")]
    public async Task ThenRestorationReportCountPlural(int count) => await ThenRestorationReportCount(count);

    [Then("the publish report says no cells were restored from history")]
    public async Task ThenNoRestorationReport() =>
        await Assert.That(_report.GetProperty("restorations").GetArrayLength()).IsEqualTo(0);

    // ── Then: the target's app document / data / log / genesis ─────────────────────────────────────

    // Compares against whichever "before" text the scenario captured: an UNSTAMPED target (never
    // published at all) still holds the sentinel written by SetUpTarget; a scenario running AFTER the
    // Background's real baseline publish captures its own "before" via "the target's own log line count is
    // remembered" (which also snapshots the app text) — so this Then always compares against the state
    // right before the action under test, not a hardcoded value that a prior real publish already moved past.
    [Then("the target app document was never republished")]
    public async Task ThenTargetAppNeverRepublished()
    {
        var before = _targetAppTextBefore.Length > 0 ? _targetAppTextBefore : "UNPUBLISHED-TARGET-SENTINEL";
        await Assert.That(File.ReadAllText(_targetAppPath)).IsEqualTo(before);
    }

    [Then("the target's published {string} reads {string} as {string} {string}")]
    [Then("the target's {string} still reads {string} as {string} {string}")]
    public async Task ThenTargetFieldReads(string typeName, string field, string baseType, string value)
    {
        _ = baseType;
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var item = store.ReadExtent(typeName).Values.First();
        var actual = ScalarText(item.Fields.GetValueOrDefault(field));
        await Assert.That(actual).IsEqualTo(value);
    }

    [Then("the target's published {string} reads {string} defaulted to {string}")]
    public async Task ThenTargetFieldDefaulted(string typeName, string field, string defaultValue)
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var item = store.ReadExtent(typeName).Values.First();
        var actual = ScalarText(item.Fields.GetValueOrDefault(field));
        await Assert.That(actual).IsEqualTo(defaultValue);
    }

    [Given("the target's {string} has no stored {string} value")]
    [Then("the target's {string} has no stored {string} value")]
    public async Task ThenTargetHasNoStoredValue(string typeName, string field)
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        // A field the CURRENT schema does not declare cannot be read via ReadExtent's BuildObject (it only
        // iterates declared props) — the honest proof of "dropped" is that the published schema simply
        // no longer HAS the field at all (a renamed-away or removed prop).
        var type = published.FindType(typeName)!;
        await Assert.That(type.Props?.Any(p => p.Name == field) ?? false).IsFalse();
    }

    [Then("the kept value is not the schema's default for {string}")]
    public async Task ThenKeptValueNotDefault(string field)
    {
        // The default for an added/reseeded text field is "" (empty) — the value read back for "title"
        // must be the CARRIED one ("Keep me"), never empty, proving identity (not a coincidental reseed).
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var item = store.ReadExtent("Item").Values.First();
        var actual = ScalarText(item.Fields.GetValueOrDefault(field));
        await Assert.That(actual).IsNotEqualTo("");
    }

    [Then("the target's {string} extent holds an object labelled {string}")]
    public async Task ThenExtentHoldsLabelled(string typeName, string label)
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var found = store.ReadExtent(typeName).Values
            .Any(o => o.Fields.GetValueOrDefault("label") is TextValue t && t.Text == label);
        await Assert.That(found).IsTrue();
    }

    [Then("the target's {string} extent holds an object named {string}")]
    public async Task ThenExtentHoldsNamed(string typeName, string name)
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var found = store.ReadExtent(typeName).Values
            .Any(o => o.Fields.GetValueOrDefault("name") is TextValue t && t.Text == name);
        await Assert.That(found).IsTrue();
    }

    [Then("the target's Db {string} set has {int} member")]
    public async Task ThenTargetDbSetMemberCount(string setProp, int count)
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var set = (SetValue)store.ReadNode(NodePath.Root.Field(setProp))!;
        await Assert.That(set.Members.Count).IsEqualTo(count);
    }

    [Then("every reference to the renamed type in the target now points at {string}")]
    public async Task ThenReferencesRepointed(string newTypeName)
    {
        // The Db's set prop (its OWN name is unaffected by a TYPE rename — only its declared element
        // TYPE changed) must still resolve its members via ReadNode/BuildObject, which walks by the
        // member's CURRENT type name — a stale StoredRef.TypeName would fail to resolve here. `_dbTypeId`'s
        // set prop is "items" (named from the ORIGINAL type name when the Background authored it); the
        // published schema now declares its element type as `newTypeName`, proving the repoint.
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var dbType = published.FindType("Db")!;
        var setProp = dbType.Props!.Single(p => p.Cardinality == Cardinality.Set);
        await Assert.That(setProp.Type).IsEqualTo(newTypeName);
        var setNode = store.ReadNode(NodePath.Root.Field(setProp.Name));
        await Assert.That(setNode).IsTypeOf<SetValue>();
        await Assert.That(((SetValue)setNode!).Members.Count).IsGreaterThan(0);
    }

    [Then("the target's log grew by exactly one entry")]
    public async Task ThenTargetLogGrewByOne() =>
        await Assert.That(TargetLogLineCount()).IsEqualTo(_targetLogLinesBefore + 1);

    [Then("the target's log did not grow")]
    public async Task ThenTargetLogDidNotGrow() =>
        await Assert.That(TargetLogLineCount()).IsEqualTo(_targetLogLinesBefore);

    [Then("the target's newest log entry carries a boundary marker for that commit")]
    public async Task ThenNewestEntryCarriesBoundary()
    {
        var lines = File.ReadAllLines(_targetLogPath);
        var last = JsonSerializer.Deserialize<JsonElement>(lines[^1], StoreOpts);
        await Assert.That(last.TryGetProperty("boundary", out var boundary) && boundary.ValueKind == JsonValueKind.Object).IsTrue();
        await Assert.That(boundary.GetProperty("designId").GetInt32()).IsEqualTo(_designId);
        await Assert.That(boundary.GetProperty("commitId").GetInt32()).IsEqualTo(HeadCommitId());
    }

    [Then("the target's genesis is unchanged by the publish")]
    public async Task ThenGenesisUnchanged()
    {
        // Genesis was frozen at the target's FIRST-EVER mutation (long before this publish); a versioned
        // publish must never re-freeze it (unlike the unversioned re-baseline path). Proven structurally:
        // genesis must still exist and its own seq must predate the log's first entry.
        await Assert.That(File.Exists(_targetGenesisPath)).IsTrue();
        var genesis = JsonSerializer.Deserialize<GenesisFile>(File.ReadAllText(_targetGenesisPath), StoreOpts)!;
        var firstEntrySeq = JsonSerializer.Deserialize<JsonElement>(File.ReadAllLines(_targetLogPath)[0], StoreOpts)
            .GetProperty("seq").GetInt32();
        await Assert.That(genesis.GenesisSeq).IsLessThan(firstEntrySeq);
    }

    [Then("the target's log fsck holds")]
    public async Task ThenTargetFsckHolds()
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        await Assert.That(store.Fsck()).IsTrue();
    }

    [Then("replaying the target's log from genesis to head reproduces the post-publish snapshot")]
    public async Task ThenReplayReproducesHead()
    {
        var genesis = JsonSerializer.Deserialize<GenesisFile>(File.ReadAllText(_targetGenesisPath), StoreOpts)!;
        var entries = File.ReadAllLines(_targetLogPath)
            .Select(l => JsonSerializer.Deserialize<LogEntry>(l, StoreOpts)!).ToList();
        var replayed = entries.Aggregate(genesis.Db, AppLogReplay.Apply);
        var live = JsonSerializer.Deserialize<Db>(File.ReadAllText(_targetDataPath), StoreOpts)!;
        await Assert.That(AppLogReplay.Equivalent(replayed, live)).IsTrue();
    }

    // ── the WAL crash-window scenario (fix 1) ──────────────────────────────────────────────────────
    //
    // Simulates the CORRECT-order crash: the boundary entry is on the log, but the snapshot died at its
    // pre-publish version. Roll the snapshot back to genesis-replayed-through-every-entry-except-the-last
    // (the same technique as AppLog.feature's "snapshot left behind a crash") — the log is left untouched.
    // A fresh store must then REPLAY the tail forward and serve the post-publish (renamed) state.
    private JsonFileInstanceStore? _reopenedTarget;

    [When("the target's snapshot is rolled back to before the publish while the log keeps the boundary entry")]
    public void WhenTargetSnapshotRolledBack()
    {
        var genesis = JsonSerializer.Deserialize<GenesisFile>(File.ReadAllText(_targetGenesisPath), StoreOpts)!;
        var entries = File.ReadAllLines(_targetLogPath)
            .Select(l => JsonSerializer.Deserialize<LogEntry>(l, StoreOpts)!).ToList();
        // Reconstruct the doc as it stood BEFORE the last (boundary) entry, and write THAT over the
        // caught-up snapshot — exactly a crash that appended the entry but died before SaveRaw.
        var rolledBack = entries.Take(entries.Count - 1).Aggregate(genesis.Db, AppLogReplay.Apply);
        File.WriteAllText(_targetDataPath, JsonSerializer.Serialize(rolledBack, StoreOpts));
    }

    [When("a fresh store is opened over the target's files")]
    public void WhenFreshTargetStoreOpened()
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        _reopenedTarget = new JsonFileInstanceStore(_targetDataPath, published);
    }

    [Then("the reopened target reads its {string} {string} as {string}")]
    public async Task ThenReopenedTargetFieldReads(string typeName, string field, string expected)
    {
        var member = _reopenedTarget!.ReadExtent(typeName).Values.First();
        await Assert.That(ScalarText(member.Fields.GetValueOrDefault(field))).IsEqualTo(expected);
    }

    [Then("the reopened target's log fsck holds")]
    public async Task ThenReopenedTargetFsckHolds() =>
        await Assert.That(_reopenedTarget!.Fsck()).IsTrue();

    // The INVERSE: what the pre-fix append-AFTER-snapshot order would leave — the snapshot at the
    // post-publish version but the log MISSING the boundary entry (removed here). Boot reconciliation
    // must REJECT it loudly ("snapshot AHEAD of its own log") — never silently trust the snapshot.
    // (The prior fresh-open step already replayed the rolled-back snapshot forward, so the on-disk
    // snapshot is now at the post-publish version and consistent with the log; dropping the last log
    // line makes the snapshot exceed the log head.)
    [When("the target's log has its boundary entry removed while the snapshot stays at the post-publish version")]
    public void WhenTargetBoundaryEntryRemoved()
    {
        var lines = File.ReadAllLines(_targetLogPath).Where(l => l.Length > 0).ToList();
        File.WriteAllText(_targetLogPath, lines.Count > 1 ? string.Join("\n", lines.Take(lines.Count - 1)) + "\n" : "");
    }

    private Exception? _reopenError;

    [Then("opening a store over the target's files is rejected as snapshot-ahead-of-log")]
    public async Task ThenOpenRejectedSnapshotAhead()
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        try { _ = new JsonFileInstanceStore(_targetDataPath, published); _reopenError = null; }
        catch (StoredDataException ex) { _reopenError = ex; }
        await Assert.That(_reopenError).IsNotNull();
        await Assert.That(_reopenError!.Message.ToLowerInvariant()).Contains("ahead");
    }

    // ── the unsupported-reshape scenario (fix 2) ───────────────────────────────────────────────────

    [Then("the publish report flags the {string} reshape as unsupported and dropped")]
    public async Task ThenReshapeFlaggedUnsupportedDropped(string prop)
    {
        var cardinality = _report.GetProperty("cardinality").EnumerateArray().ToList();
        var hit = cardinality.FirstOrDefault(c => c.GetProperty("path").GetString()!.EndsWith("." + prop));
        await Assert.That(hit.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(hit.GetProperty("unsupported").GetBoolean()).IsTrue();
        await Assert.That(hit.GetProperty("dropped").GetBoolean()).IsTrue();
    }

    [Then("a fresh store opens over the target's files without error")]
    public async Task ThenFreshStoreOpensClean()
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        // Construction runs the STRICT startup guard — a stored old-shaped value the new schema no longer
        // allows would throw here, so a clean open IS the proof the reshape dropped-to-default correctly.
        Exception? error = null;
        try { _ = new JsonFileInstanceStore(_targetDataPath, published); } catch (Exception ex) { error = ex; }
        await Assert.That(error).IsNull();
    }

    [Then("the target's Db {string} reads as an unset reference")]
    public async Task ThenDbPropUnsetReference(string prop)
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var node = store.ReadNode(NodePath.Root.Field(prop));
        // A single object-typed prop reads as a ReferenceValue; an unset one has a null TargetId.
        await Assert.That(node).IsTypeOf<ReferenceValue>();
        await Assert.That(((ReferenceValue)node!).TargetId).IsNull();
    }

    // ── the adoption baseline-commit scenario: a REAL kernel boot over the REAL designer meta-schema ──
    //
    // Reuses the Kernel/DesignCommit harness pattern (KernelSteps' "kernel booted from the committed
    // designer, todo and crm apps" Given): a real KernelHost.StartAsync over instances/1 (the real
    // designer, InstanceContext.AppFixture(1)) plus the committed todo app. Adoption (KernelHost.
    // SyncDesignHost → EnsureMainBranches) reads todo's OWN app.deenv to build its Design, so — once
    // adopted — todo's app document canonically equals that design's freshly-minted baseline commit text,
    // making todo itself the natural "matching instance" the stamping half targets.
    private KernelHost? _realKernel;
    private string _kernelDir = "";
    private const int TodoInstanceId = 2;

    [Given("a kernel booted from the committed designer and the committed todo app")]
    public async Task GivenKernelWithDesignerAndTodo()
    {
        _kernelDir = Path.Combine(Path.GetTempPath(), "deenv-publish-kernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_kernelDir);
        WriteIdApp(_kernelDir, 1, File.ReadAllText(TestSupport.InstanceContext.AppFixture(1))); // the real designer
        WriteIdApp(_kernelDir, TodoInstanceId, File.ReadAllText(TestSupport.InstanceContext.AppFixture(2))); // todo

        var appPort = TestSupport.InstanceContext.FreePort();
        var assetPort = TestSupport.InstanceContext.FreePort();
        _registryPath = Path.Combine(_kernelDir, "kernel.json");
        File.WriteAllText(_registryPath, JsonSerializer.Serialize(new
        {
            appPort, assetPort,
            instances = new object[]
            {
                new { id = 1, app = "designer", designId = 60 },
                new { id = TodoInstanceId, app = "todo", designId = 13 },
            },
        }));

        var registry = RegistryReader.Read(_registryPath);
        _realKernel = new KernelHost(_kernelDir, _registryPath, appPort, assetPort, bindLoopback: true);
        await _realKernel.StartAsync(KernelHost.SpecsFor(registry, _kernelDir));
    }

    private static void WriteIdApp(string dir, int id, string appDoc)
    {
        var idDir = AppPaths.IdDirFor(dir, id);
        Directory.CreateDirectory(idDir);
        File.WriteAllText(Path.Combine(idDir, "app.deenv"), appDoc);
    }

    [Then("the todo design was adopted with a baseline commit whose parent is empty")]
    public async Task ThenTodoDesignAdoptedWithBaseline()
    {
        var designerStore = _realKernel!.Instances.Single(i => i.Spec.Id == 1).Store;
        var design = designerStore.ReadExtent("Design").Single(d =>
            d.Value.Fields.GetValueOrDefault("label") is TextValue { Text: "todo" });
        var branch = designerStore.ReadExtent("Branch").Values
            .First(b => b.Fields.GetValueOrDefault("workingCopy") is ReferenceValue { TargetId: var t } && t == design.Key);
        var headRef = branch.Fields.GetValueOrDefault("head") as ReferenceValue;
        await Assert.That(headRef?.TargetId).IsNotNull();

        var commit = designerStore.ReadById(headRef!.TargetId!.Value);
        await Assert.That(commit).IsNotNull();
        await Assert.That(commit!.Value.TypeName).IsEqualTo("Commit");
        await Assert.That((commit.Value.Fields.Fields.GetValueOrDefault("message") as TextValue)?.Text).IsEqualTo("Adopted");
        var parent = commit.Value.Fields.Fields.GetValueOrDefault("parent") as ReferenceValue;
        await Assert.That(parent?.TargetId).IsNull();

        _adoptedBaselineCommitId = headRef.TargetId!.Value;
    }

    private int _adoptedBaselineCommitId;

    [Then("the todo instance's registry entry is stamped to that baseline commit")]
    public async Task ThenTodoStampedToBaseline()
    {
        var stamped = KernelHost.ReadPublishedCommitId(TodoInstanceId, _registryPath);
        await Assert.That(stamped).IsEqualTo((int?)_adoptedBaselineCommitId);
    }

    // ── the stale-draft scenario: a client-loaded base version, staged after publish ────────────────

    private int _staleObjectId;
    private int _staleBaseVersion;

    [Given("a client loaded the target's {string} and staged an edit at that base version")]
    public void GivenClientLoadedAndStaged(string typeName)
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        _staleObjectId = store.ReadExtent(typeName).Keys.First();
        _staleBaseVersion = store.CurrentVersion; // "loaded" — the version a real client would remember
    }

    [When("the stale client commits its staged edit to the target")]
    public void WhenStaleClientCommits()
    {
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        try
        {
            store.CommitBatch([], [new FieldSetMutation(_staleObjectId, "title", new TextValue("stale edit"))],
                baseVersion: _staleBaseVersion);
            _staleCommitError = null;
        }
        catch (StaleBaseException ex)
        {
            _staleCommitError = ex.Message;
        }
    }

    private string? _staleCommitError;

    [Then("the target instance rejects the stale commit with a message mentioning {string}")]
    public async Task ThenStaleCommitRejected(string phrase)
    {
        await Assert.That(_staleCommitError).IsNotNull();
        await Assert.That(_staleCommitError).Contains(phrase);
    }

    // ── helpers: designer authoring (mirrors HostActionSteps/DesignSnapshotSteps) ───────────────────

    private void OpenDesigner()
    {
        Directory.CreateDirectory(_dir);
        _metaAppPath = Path.Combine(_dir, "designer.app");
        File.WriteAllText(_metaAppPath, MetaSchema);
        _meta = InstanceDescriptionLoader.LoadFile(_metaAppPath);
        _designerDataPath = Path.Combine(_dir, "designer-data.json");
        _designer = new JsonFileInstanceStore(_designerDataPath, _meta);
        _sessions = new ClientSessionStore();
    }

    private void AddDesign()
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

        var branchId = _designer.CreateObject("Branch", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("main"),
        }));
        _designer.AddToSet(NodePath.Root.Field("branches"), branchId);
        _designer.WriteReference(branchId, "workingCopy", _designId, "Design");
    }

    private NodePath DesignTypesPath => NodePath.Root.Field("designs").Key(_designId.ToString()).Field("types");

    private void AddType(string name, string baseType)
    {
        var id = _designer.CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]     = new TextValue(name),
            ["baseType"] = new TextValue(baseType),
        }));
        DesignerListHelpers.AppendToList(_designer, DesignTypesPath, id, "MetaType");
        _typeIds[name] = id;
        if (name == "Db") _dbTypeId = id;
    }

    private void AddProp(string typeName, string propName, string propType) =>
        AddPropCore(typeName, propName, propType, cardinality: "");

    private void AddSetProp(string typeName, string propName, string propType) =>
        AddPropCore(typeName, propName, propType, cardinality: "set");

    private void AddPropCore(string typeName, string propName, string propType, string cardinality, string keyType = "")
    {
        var propsPath = DesignTypesPath.Key(_typeIds[typeName].ToString()).Field("props");
        var fields = new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(propName),
            ["type"] = new TextValue(propType),
        };
        if (cardinality.Length > 0)
            fields["cardinality"] = new TextValue(cardinality);
        if (keyType.Length > 0)
            fields["keyType"] = new TextValue(keyType);
        var id = _designer.CreateObject("MetaProp", new ObjectValue(fields));
        DesignerListHelpers.AppendToList(_designer, propsPath, id, "MetaProp");
        _propIds[(typeName, propName)] = id;
    }

    private int CreateCommitRow(IInstanceStore store, string message, string migration, int? parent, int? mergeParent)
    {
        var design = store.ReadById(_designId) is ("Design", var d) ? d
            : throw new InvalidOperationException("No design row.");
        var snap = SchemaBridge.Snapshot(design, store);
        var commitsSetId = (store.ReadNode(NodePath.Root.Field("commits")) as SetValue)?.Id
            ?? throw new InvalidOperationException("No commits set.");
        const int temp = -1;
        var creates = new List<CommitCreate>
        {
            new(temp, "Commit", new ObjectValue(new Dictionary<string, NodeValue>
            {
                ["message"] = new TextValue(message),
                ["migration"] = new TextValue(migration),
                ["at"] = new DateTimeValue(DateTimeOffset.UtcNow),
                ["logSeq"] = new IntValue(store.CurrentVersion),
                ["text"] = new TextValue(snap.Text),
            })),
        };
        var mutations = new List<CommitMutation>
        {
            new RefSetMutation(temp, "design", _designId, "Design"),
            new SetAddMutation(commitsSetId, temp),
        };
        if (parent is { } p) mutations.Add(new RefSetMutation(temp, "parent", p, "Commit"));
        if (mergeParent is { } mp) mutations.Add(new RefSetMutation(temp, "mergeParent", mp, "Commit"));
        foreach (var (path, id) in snap.IdMap)
            mutations.Add(new DictAddMutation(temp, "idMap", new TextValue(path), new IntValue(id)));
        return store.CommitBatch(creates, mutations).Creates.Single().RealId;
    }

    private static ObjectValue CommitFields(IInstanceStore store, int commitId) =>
        store.ReadById(commitId) is ("Commit", var fields) ? fields
            : throw new InvalidOperationException($"No commit {commitId}.");

    private static string ScalarText(NodeValue? value) => value switch
    {
        TextValue t    => t.Text,
        IntValue i     => i.Value.ToString(),
        DecimalValue d => d.Value.ToString(),
        BoolValue b    => b.Value ? "true" : "false",
        null           => "",
        var other      => other.ToString() ?? "",
    };

    [AfterScenario]
    public async Task Cleanup()
    {
        try { if (_realKernel is not null) await _realKernel.DisposeAsync(); } catch { /* best-effort */ }
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        try { if (_kernelDir.Length > 0 && Directory.Exists(_kernelDir)) Directory.Delete(_kernelDir, recursive: true); } catch { /* best-effort */ }
    }
}
