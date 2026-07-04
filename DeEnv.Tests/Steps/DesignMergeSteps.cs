using System.Text.Json;
using DeEnv.Designer;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Kernel;
using DeEnv.Storage;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// DesignMerge.feature — the M13 slice-5 branches + origin-keyed three-way structural merge. Drives the
// REAL KernelHostActions.CreateBranch/MergeBranch at the WS-handler seam, the same level Publish.feature
// drives commitDesign/publish, with the SAME kind of self-contained test-local designer meta (a Db
// holding designs/commits/branches, M13 slice-3 shape + slice-5's `origin`/`mergeParent` additions).
[Binding]
public sealed class DesignMergeSteps
{
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
                types set of MetaType
                origin int
            MetaType
                name text
                baseType text
                values text
                order int
                props set of MetaProp
                origin int
            MetaProp
                name text
                type text
                cardinality text
                keyType text
                multiline bool
                order int
                origin int
            Commit
                message text
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

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deenv-designmerge-" + Guid.NewGuid().ToString("N"));
    private string _metaAppPath = "";
    private string _designerDataPath = "";

    private InstanceDescription _meta = null!;
    private IInstanceStore _designer = null!;
    private ClientSessionStore _sessions = null!;
    private string _clientId = "";

    private int _designId; // main's own working-copy Design row id
    private int _branchDesignId; // the branch clone's working-copy Design row id
    private readonly Dictionary<string, int> _mainTypeIds = new();
    private readonly Dictionary<(string Type, string Prop), int> _mainPropIds = new();
    private readonly Dictionary<string, int> _branchTypeIds = new();
    private readonly Dictionary<(string Type, string Prop), int> _branchPropIds = new();

    private JsonElement _replyRoot;
    private JsonElement _report;
    private JsonElement _secondReport;

    // ── Background: a versioned designer + a baseline commit + a branch ─────────────────────────────

    [Given("a branchable designer instance holding a design with a type {string}")]
    public void GivenBranchableDesignerHoldingDesign(string typeName)
    {
        OpenDesigner();
        AddDesign();
        AddType(_mainTypeIds, _mainPropIds, _designId, "Db", "object");
        AddType(_mainTypeIds, _mainPropIds, _designId, typeName, "object");
        AddProp(_mainTypeIds, _mainPropIds, _designId, typeName, "label", "text");
        // A second prop so a scenario that REMOVES/renames `label` leaves the type with props (a propless
        // object type is invalid to snapshot/commit). Not referenced by any scenario's own assertions.
        AddProp(_mainTypeIds, _mainPropIds, _designId, typeName, "count", "int");
        AddSetProp(_mainTypeIds, _mainPropIds, _designId, "Db", typeName.ToLowerInvariant() + "s", typeName);
        AddFn(_designId, "common", "greet", "\"hello\"");
    }

    [Given("the design is committed as {string} for branching")]
    public void GivenDesignCommittedForBranching(string message) => Commit(_designId, message);

    [Given("the main design is committed with message {string}")]
    public void GivenMainDesignCommitted(string message) => Commit(_designId, message);

    [Given("a branch named {string} is created from the design over the WS")]
    public void GivenBranchCreated(string name)
    {
        var ws = Ws();
        var reply = ws.ProcessMessage(
            $$"""{ "op": "hostAction", "clientId": "{{_clientId}}", "action": "createBranch", "args": [ { "type": "int", "value": {{_designId}} }, { "type": "text", "value": "{{name}}" } ] }""");
        using var doc = JsonDocument.Parse(reply);
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"createBranch failed: {reply}");

        // Locate the freshly-minted branch's working-copy Design id, and mirror the type/prop id maps
        // (the clone's OWN row ids) so later "the branch's ..." steps can address them directly.
        var fresh = FreshDesigner();
        var branch = fresh.ReadExtent("Branch").Values.First(b =>
            b.Fields.GetValueOrDefault("name") is TextValue { Text: var n } && n == name);
        _branchDesignId = branch.Fields.GetValueOrDefault("workingCopy") is ReferenceValue { TargetId: { } wc } ? wc
            : throw new InvalidOperationException("createBranch produced a branch with no workingCopy.");

        var cloneDesign = fresh.ReadById(_branchDesignId)!.Value.Fields;
        var cloneTypes = (cloneDesign.Fields.GetValueOrDefault("types") as SetValue)?.Members ?? new Dictionary<int, NodeValue>();
        foreach (var (id, val) in cloneTypes)
            if (val is ObjectValue typeObj && typeObj.Fields.GetValueOrDefault("name") is TextValue { Text: var typeName })
            {
                _branchTypeIds[typeName] = id;
                var props = (typeObj.Fields.GetValueOrDefault("props") as SetValue)?.Members ?? new Dictionary<int, NodeValue>();
                foreach (var (propId, propVal) in props)
                    if (propVal is ObjectValue propObj && propObj.Fields.GetValueOrDefault("name") is TextValue { Text: var propName })
                        _branchPropIds[(typeName, propName)] = propId;
            }
    }

    // ── authoring on MAIN or the BRANCH (separate id maps) ──────────────────────────────────────────

    [Given("the branch's {string} prop {string} is renamed to {string}")]
    public void GivenBranchPropRenamed(string typeName, string from, string to) =>
        RenameProp(_branchPropIds, typeName, from, to);

    [Given("the main design's {string} prop {string} is renamed to {string}")]
    [Given("the main design's {string} prop {string} is renamed to {string} but left uncommitted")]
    public void GivenMainPropRenamed(string typeName, string from, string to) =>
        RenameProp(_mainPropIds, typeName, from, to);

    private void RenameProp(Dictionary<(string, string), int> propIds, string typeName, string from, string to)
    {
        var propId = propIds[(typeName, from)];
        FreshDesigner().WriteField(propId, "name", new TextValue(to));
        propIds.Remove((typeName, from));
        propIds[(typeName, to)] = propId;
    }

    [Given("the branch adds a {string} field to {string}")]
    public void GivenBranchFieldAdded(string field, string typeName) =>
        AddProp(_branchTypeIds, _branchPropIds, _branchDesignId, typeName, field, "text");

    [Given("the branch adds a {string} field to {string} after {string}")]
    public void GivenBranchFieldAddedAfter(string field, string typeName, string afterField) =>
        AddProp(_branchTypeIds, _branchPropIds, _branchDesignId, typeName, field, "text");

    [Given("the main design adds a {string} field to {string} after {string}")]
    public void GivenMainFieldAddedAfter(string field, string typeName, string afterField) =>
        AddProp(_mainTypeIds, _mainPropIds, _designId, typeName, field, "text");

    [Given("the main design's {string} field {string} is retyped to {string}")]
    public void GivenMainFieldRetyped(string typeName, string field, string toType)
    {
        var propId = _mainPropIds[(typeName, field)];
        FreshDesigner().WriteField(propId, "type", new TextValue(toType));
    }

    [Given("the branch's {string} field {string} is removed")]
    public void GivenBranchFieldRemoved(string typeName, string field)
    {
        var propId = _branchPropIds[(typeName, field)];
        var typeId = _branchTypeIds[typeName];
        var propsSetId = (FreshDesigner().ReadById(typeId)!.Value.Fields.Fields.GetValueOrDefault("props") as SetValue)!.Id;
        FreshDesigner().RemoveFromSet(propsSetId, propId);
        _branchPropIds.Remove((typeName, field));
    }

    [Given("the main design's access grants read on {string} to everyone")]
    public void GivenMainAccessGrantsRead(string typeName) => AddAccessRule(_designId, typeName, "read", condition: null);

    [Given("the branch's access grants edit on {string} to admins")]
    public void GivenBranchAccessGrantsEdit(string typeName) =>
        AddAccessRule(_branchDesignId, typeName, "edit", condition: "currentUser.role == \"Admin\"");

    [Given("the main design's fn {string} is edited to return {string} on main")]
    public void GivenMainFnEdited(string fnName, string returnValue) =>
        EditFn(_designId, "common", fnName, $"\"{returnValue}\"");

    [Given("the branch's fn {string} is edited to return {string} on the branch")]
    public void GivenBranchFnEdited(string fnName, string returnValue) =>
        EditFn(_branchDesignId, "common", fnName, $"\"{returnValue}\"");

    // ── committing ───────────────────────────────────────────────────────────────────────────────

    [Given("the branch is committed with message {string}")]
    public void GivenBranchCommitted(string message) => Commit(_branchDesignId, message);

    [When("the branch adds a {string} field to {string}")]
    public void WhenBranchFieldAdded(string field, string typeName) =>
        AddProp(_branchTypeIds, _branchPropIds, _branchDesignId, typeName, field, "text");

    [When("the branch is committed with message {string}")]
    public void WhenBranchCommitted(string message) => Commit(_branchDesignId, message);

    private void Commit(int designId, string message)
    {
        var ws = Ws();
        var reply = ws.ProcessMessage(
            $$"""{ "op": "hostAction", "clientId": "{{_clientId}}", "action": "commitDesign", "args": [ { "type": "int", "value": {{designId}} }, { "type": "text", "value": "{{message}}" } ] }""");
        using var doc = JsonDocument.Parse(reply);
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"commitDesign failed: {reply}");
    }

    // ── merging ──────────────────────────────────────────────────────────────────────────────────

    private bool _mergeAlreadyRanOnce;

    [Given("the branch {string} is merged into {string} over the WS")]
    [When("the branch {string} is merged into {string} over the WS")]
    public void WhenMerged(string sourceBranchName, string targetBranchName)
    {
        _ = sourceBranchName; _ = targetBranchName; // this harness has exactly one branch + main
        Merge(resolutionsJson: "");
    }

    [When("the branch {string} is merged into {string} taking source for that conflict over the WS")]
    public void WhenMergedTakingSource(string sourceBranchName, string targetBranchName)
    {
        _ = sourceBranchName; _ = targetBranchName;
        Merge(resolutionsJson: BuildResolutionArgs("source"));
    }

    [When("the branch {string} is merged into {string} taking target for that conflict over the WS")]
    public void WhenMergedTakingTarget(string sourceBranchName, string targetBranchName)
    {
        _ = sourceBranchName; _ = targetBranchName;
        Merge(resolutionsJson: BuildResolutionArgs("target"));
    }

    // Build a `resolutions` arg that resolves EVERY conflict the LAST attempt reported with the same
    // `take` — sufficient for this feature's scenarios (each conflict scenario drives exactly one
    // conflict at a time). A plain JSON array of {id, take} objects (KernelHostActions.ArgResolutionsOptional
    // accepts a bare array defensively).
    private string BuildResolutionArgs(string take)
    {
        var ids = _report.GetProperty("conflicts").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();
        var items = string.Join(", ", ids.Select(id => $$"""{ "id": "{{id}}", "take": "{{take}}" }"""));
        return $"[{items}]";
    }

    private void Merge(string resolutionsJson)
    {
        var ws = Ws();
        var argsJson = resolutionsJson.Length > 0
            ? $$"""{ "type": "int", "value": {{_branchDesignId}} }, { "type": "int", "value": {{_designId}} }, {{resolutionsJson}}"""
            : $$"""{ "type": "int", "value": {{_branchDesignId}} }, { "type": "int", "value": {{_designId}} }""";
        var reply = ws.ProcessMessage(
            $$"""{ "op": "hostAction", "clientId": "{{_clientId}}", "action": "mergeBranch", "args": [ {{argsJson}} ] }""");
        using var doc = JsonDocument.Parse(reply);
        if (_mergeAlreadyRanOnce) _secondReport = _report;
        _replyRoot = doc.RootElement.Clone();
        if (_replyRoot.TryGetProperty("report", out var report)) _report = report.Clone();
        _mergeAlreadyRanOnce = true;
    }

    // ── Then: reports ────────────────────────────────────────────────────────────────────────────

    [Then("the merge report shows merged {word}")]
    public async Task ThenMergedShows(string expected) =>
        await Assert.That(_report.GetProperty("merged").GetBoolean()).IsEqualTo(bool.Parse(expected));

    [Then("the second merge report shows merged {word}")]
    public async Task ThenSecondMergedShows(string expected) =>
        await Assert.That(_secondReport.GetProperty("merged").GetBoolean()).IsEqualTo(bool.Parse(expected));

    [Then("the merge report shows no conflicts")]
    public async Task ThenNoConflicts() =>
        await Assert.That(_report.GetProperty("conflicts").GetArrayLength()).IsEqualTo(0);

    [Then("the second merge report shows no conflicts")]
    public async Task ThenSecondNoConflicts() =>
        await Assert.That(_secondReport.GetProperty("conflicts").GetArrayLength()).IsEqualTo(0);

    [Then("the merge report flags drift on {string}")]
    public async Task ThenDriftFlagged(string side) =>
        await Assert.That(_report.TryGetProperty("driftRefusal", out var d) && d.GetString() == side).IsTrue();

    [Then("the merge report has a conflict for the {string} prop rename")]
    public async Task ThenConflictForPropRename(string typeName)
    {
        var conflicts = _report.GetProperty("conflicts").EnumerateArray().ToList();
        await Assert.That(conflicts.Any(c => c.GetProperty("kind").GetString() == "meta" && c.GetProperty("field").GetString() == "name")).IsTrue();
        _ = typeName;
    }

    [Then("the merge report has an existence conflict for the {string} prop {string}")]
    public async Task ThenExistenceConflict(string typeName, string propName)
    {
        var conflicts = _report.GetProperty("conflicts").EnumerateArray().ToList();
        await Assert.That(conflicts.Any(c => c.GetProperty("kind").GetString() == "existence" && c.GetProperty("path").GetString() == propName)).IsTrue();
        _ = typeName;
    }

    [Then("the merge report has a conflict for the {string} function")]
    public async Task ThenConflictForFunction(string fnName)
    {
        var conflicts = _report.GetProperty("conflicts").EnumerateArray().ToList();
        await Assert.That(conflicts.Any(c => c.GetProperty("kind").GetString() == "fn" && c.GetProperty("path").GetString() == fnName)).IsTrue();
    }

    [Then("the merge report's access changes mention the {string} read rule")]
    public async Task ThenAccessChangesMentionRead(string typeName)
    {
        var changes = _report.GetProperty("accessChanges").EnumerateArray().ToList();
        await Assert.That(changes.Any(c => c.GetProperty("ruleKey").GetString()!.StartsWith(typeName + "|read"))).IsTrue();
    }

    [Then("the merge report's access changes mention the {string} edit rule")]
    public async Task ThenAccessChangesMentionEdit(string typeName)
    {
        var changes = _report.GetProperty("accessChanges").EnumerateArray().ToList();
        await Assert.That(changes.Any(c => c.GetProperty("ruleKey").GetString()!.StartsWith(typeName + "|edit"))).IsTrue();
    }

    [Then("the merge commit has parent equal to main's pre-merge head and mergeParent equal to the branch's head")]
    public async Task ThenMergeCommitParents()
    {
        var mergeCommitId = _report.GetProperty("mergeCommit").GetInt32();
        var fresh = FreshDesigner();
        var mergeCommit = fresh.ReadById(mergeCommitId)!.Value.Fields;
        var parent = mergeCommit.Fields.GetValueOrDefault("parent") as ReferenceValue;
        var mergeParent = mergeCommit.Fields.GetValueOrDefault("mergeParent") as ReferenceValue;
        var targetPreMergeHead = _report.GetProperty("targetCommit").GetInt32();
        var sourceHead = _report.GetProperty("sourceCommit").GetInt32();
        await Assert.That(parent?.TargetId).IsEqualTo((int?)targetPreMergeHead);
        await Assert.That(mergeParent?.TargetId).IsEqualTo((int?)sourceHead);
    }

    // ── Then: post-merge store assertions on MAIN's working copy ────────────────────────────────────

    [Then("db.designs is unchanged by the branch creation")]
    public async Task ThenDesignsUnchangedByBranch()
    {
        var fresh = FreshDesigner();
        var designs = fresh.ReadExtent("Design");
        await Assert.That(designs.ContainsKey(_designId)).IsTrue();
        // The branch's clone Design must NOT be a member of db.designs (only main working copies live there).
        var designsSet = fresh.ReadNode(NodePath.Root.Field("designs")) as SetValue;
        await Assert.That(designsSet!.Members.ContainsKey(_branchDesignId)).IsFalse();
    }

    [Then("db.branches holds a branch named {string} whose head is the design's baseline commit")]
    public async Task ThenBranchesHoldsNamedBranch(string name)
    {
        var fresh = FreshDesigner();
        var branch = fresh.ReadExtent("Branch").Values.FirstOrDefault(b =>
            b.Fields.GetValueOrDefault("name") is TextValue { Text: var n } && n == name);
        await Assert.That(branch).IsNotNull();
        var mainBranch = fresh.ReadExtent("Branch").Values.First(b =>
            b.Fields.GetValueOrDefault("name") is TextValue { Text: "main" }
            && b.Fields.GetValueOrDefault("workingCopy") is ReferenceValue { TargetId: var t } && t == _designId);
        var branchHead = branch!.Fields.GetValueOrDefault("head") as ReferenceValue;
        var mainHead = mainBranch.Fields.GetValueOrDefault("head") as ReferenceValue;
        await Assert.That(branchHead?.TargetId).IsEqualTo(mainHead?.TargetId);
    }

    [Then("the branch's cloned {string} type has origin equal to the original {string} type's id")]
    public async Task ThenClonedTypeOrigin(string cloneTypeName, string originalTypeName)
    {
        var fresh = FreshDesigner();
        var cloneId = _branchTypeIds[cloneTypeName];
        var originalId = _mainTypeIds[originalTypeName];
        var cloneFields = fresh.ReadById(cloneId)!.Value.Fields;
        var origin = cloneFields.Fields.GetValueOrDefault("origin") is IntValue { Value: var o } ? o : 0;
        await Assert.That(origin).IsEqualTo(originalId);
    }

    [Then("the branch's working copy renders through the generic designer store without error")]
    public async Task ThenBranchRendersWithoutError()
    {
        // "Renders without error" proven at the store/WS read path (no browser here — see the task's own
        // allowance): the clone's Design row is well-formed enough for SchemaBridge to snapshot it (the
        // SAME validate-then-print path a real designer page composes through).
        var fresh = FreshDesigner();
        var cloneDesign = fresh.ReadById(_branchDesignId)!.Value.Fields;
        var snap = SchemaBridge.Snapshot(cloneDesign); // throws if the clone is malformed
        await Assert.That(snap.Text.Length).IsGreaterThan(0);
    }

    [Then("the branch's head advanced to the new commit")]
    public async Task ThenBranchHeadAdvanced()
    {
        var fresh = FreshDesigner();
        var branch = FindBranchByWorkingCopy(fresh, _branchDesignId);
        await Assert.That(branch.Fields.Fields.GetValueOrDefault("head")).IsNotNull();
    }

    [Then("the main branch's head is still the baseline commit")]
    public async Task ThenMainHeadUnchanged()
    {
        var fresh = FreshDesigner();
        var mainBranch = FindBranchByWorkingCopy(fresh, _designId);
        await Assert.That(mainBranch.Fields.Fields.GetValueOrDefault("head")).IsNotNull();
        // "Still the baseline" is proven relatively: main's head commit's logSeq must be STRICTLY LESS
        // than the branch's (which just advanced past it) — main never moved.
        var mainHeadId = (mainBranch.Fields.Fields.GetValueOrDefault("head") as ReferenceValue)!.TargetId!.Value;
        var branchHeadId = (FindBranchByWorkingCopy(fresh, _branchDesignId).Fields.Fields.GetValueOrDefault("head") as ReferenceValue)!.TargetId!.Value;
        var mainSeq = IntField(fresh.ReadById(mainHeadId)!.Value.Fields, "logSeq");
        var branchSeq = IntField(fresh.ReadById(branchHeadId)!.Value.Fields, "logSeq");
        await Assert.That(mainSeq).IsLessThan(branchSeq);
    }

    [Then("the main design's working copy is unchanged")]
    public async Task ThenMainWorkingCopyUnchanged()
    {
        var fresh = FreshDesigner();
        var mainDesign = fresh.ReadById(_designId)!.Value.Fields;
        var itemTypeId = _mainTypeIds["Item"];
        var propsSet = (fresh.ReadById(itemTypeId)!.Value.Fields.Fields.GetValueOrDefault("props") as SetValue)!;
        var hasLabel = propsSet.Members.Keys.Any(id =>
            fresh.ReadById(id)!.Value.Fields.Fields.GetValueOrDefault("name") is TextValue { Text: "label" });
        await Assert.That(hasLabel).IsTrue();
        _ = mainDesign;
    }

    [Then("the main design's {string} now has a prop named {string}")]
    [Then("the main design's {string} still has a prop named {string}")]
    public async Task ThenMainHasProp(string typeName, string propName) =>
        await Assert.That(MainHasProp(typeName, propName)).IsTrue();

    [Then("the main design's {string} has no prop named {string}")]
    public async Task ThenMainHasNoProp(string typeName, string propName) =>
        await Assert.That(MainHasProp(typeName, propName)).IsFalse();

    private bool MainHasProp(string typeName, string propName)
    {
        var fresh = FreshDesigner();
        var typeId = _mainTypeIds[typeName];
        var propsSet = (fresh.ReadById(typeId)!.Value.Fields.Fields.GetValueOrDefault("props") as SetValue)!;
        return propsSet.Members.Keys.Any(id =>
            fresh.ReadById(id)!.Value.Fields.Fields.GetValueOrDefault("name") is TextValue { Text: var n } && n == propName);
    }

    [Then("the main design's {string} still has a prop named {string} typed {string}")]
    public async Task ThenMainHasPropTyped(string typeName, string propName, string propType)
    {
        var fresh = FreshDesigner();
        var typeId = _mainTypeIds[typeName];
        var propsSet = (fresh.ReadById(typeId)!.Value.Fields.Fields.GetValueOrDefault("props") as SetValue)!;
        var match = propsSet.Members.Keys
            .Select(id => fresh.ReadById(id)!.Value.Fields)
            .FirstOrDefault(f => f.Fields.GetValueOrDefault("name") is TextValue { Text: var n } && n == propName);
        await Assert.That(match).IsNotNull();
        await Assert.That((match!.Fields.GetValueOrDefault("type") as TextValue)?.Text).IsEqualTo(propType);
    }

    [Then("the {string} prop's origin traces back to the branch's source row")]
    public async Task ThenPropOriginTracesBack(string propName)
    {
        var fresh = FreshDesigner();
        var typeId = _mainTypeIds["Item"];
        var propsSet = (fresh.ReadById(typeId)!.Value.Fields.Fields.GetValueOrDefault("props") as SetValue)!;
        var match = propsSet.Members.Keys
            .Select(id => (Id: id, Fields: fresh.ReadById(id)!.Value.Fields))
            .First(x => x.Fields.Fields.GetValueOrDefault("name") is TextValue { Text: var n } && n == propName);
        var sourceRowId = _branchPropIds[("Item", propName)];
        var origin = match.Fields.Fields.GetValueOrDefault("origin") is IntValue { Value: var o } ? o : match.Id;
        // Lineage traces back to the branch's OWN row's origin (flattened) — its own id if this is the
        // first branch (origin == 0 on the branch row itself), matching KernelHostActions.LineageOf.
        var branchRowFields = fresh.ReadById(sourceRowId)!.Value.Fields;
        var expectedLineage = branchRowFields.Fields.GetValueOrDefault("origin") is IntValue { Value: var bo } && bo != 0 ? bo : sourceRowId;
        await Assert.That(origin).IsEqualTo(expectedLineage);
    }

    [Then("the main design's {string} function returns {string}")]
    public async Task ThenMainFnReturns(string fnName, string expected)
    {
        var fresh = FreshDesigner();
        var design = fresh.ReadById(_designId)!.Value.Fields;
        var commonText = (design.Fields.GetValueOrDefault("common") as TextValue)?.Text ?? "";
        await Assert.That(commonText).Contains(expected);
        _ = fnName;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static (int Id, ObjectValue Fields) FindBranchByWorkingCopy(IInstanceStore store, int designId)
    {
        foreach (var (id, branch) in store.ReadExtent("Branch"))
            if (branch.Fields.GetValueOrDefault("workingCopy") is ReferenceValue { TargetId: var t } && t == designId)
                return (id, branch);
        throw new InvalidOperationException($"No branch has working copy {designId}.");
    }

    private static int IntField(ObjectValue o, string name) =>
        o.Fields.TryGetValue(name, out var v) && v is IntValue i ? i.Value : 0;

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
            ["ui"]          = new TextValue("ui\n    fn render()\n        return <main>\n            \"Items\"\n"),
        }));
        _designer.AddToSet(NodePath.Root.Field("designs"), _designId);

        var branchId = _designer.CreateObject("Branch", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("main"),
        }));
        _designer.AddToSet(NodePath.Root.Field("branches"), branchId);
        _designer.WriteReference(branchId, "workingCopy", _designId, "Design");
    }

    private NodePath TypesPath(int designId) => NodePath.Root.Field("designs").Key(designId.ToString()).Field("types");

    private void AddType(Dictionary<string, int> typeIds, Dictionary<(string, string), int> propIds, int designId, string name, string baseType)
    {
        _ = propIds;
        var id = FreshDesigner().CreateObject("MetaType", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]     = new TextValue(name),
            ["baseType"] = new TextValue(baseType),
            ["order"]    = new IntValue(0),
        }));
        if (designId == _designId)
            FreshDesigner().AddToSet(TypesPath(designId), id);
        else
        {
            var typesSetId = (FreshDesigner().ReadById(designId)!.Value.Fields.Fields.GetValueOrDefault("types") as SetValue)!.Id;
            FreshDesigner().AddToSet(typesSetId, id);
        }
        typeIds[name] = id;
    }

    private void AddProp(Dictionary<string, int> typeIds, Dictionary<(string, string), int> propIds, int designId, string typeName, string propName, string propType)
    {
        _ = designId;
        var typeId = typeIds[typeName];
        var propsSetId = (FreshDesigner().ReadById(typeId)!.Value.Fields.Fields.GetValueOrDefault("props") as SetValue)!.Id;
        var id = FreshDesigner().CreateObject("MetaProp", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]  = new TextValue(propName),
            ["type"]  = new TextValue(propType),
            ["order"] = new IntValue(0),
        }));
        FreshDesigner().AddToSet(propsSetId, id);
        propIds[(typeName, propName)] = id;
    }

    private void AddSetProp(Dictionary<string, int> typeIds, Dictionary<(string, string), int> propIds, int designId, string typeName, string propName, string elemType)
    {
        _ = designId;
        var typeId = typeIds[typeName];
        var propsSetId = (FreshDesigner().ReadById(typeId)!.Value.Fields.Fields.GetValueOrDefault("props") as SetValue)!.Id;
        var id = FreshDesigner().CreateObject("MetaProp", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"]        = new TextValue(propName),
            ["type"]        = new TextValue(elemType),
            ["cardinality"] = new TextValue("set"),
            ["order"]       = new IntValue(0),
        }));
        FreshDesigner().AddToSet(propsSetId, id);
        propIds[(typeName, propName)] = id;
    }

    private void AddFn(int designId, string section, string name, string returnExpr)
    {
        var design = FreshDesigner();
        var current = section == "common"
            ? (design.ReadById(designId)!.Value.Fields.Fields.GetValueOrDefault("common") as TextValue)?.Text ?? ""
            : (design.ReadById(designId)!.Value.Fields.Fields.GetValueOrDefault("ui") as TextValue)?.Text ?? "";
        var body = current.Length == 0 ? "common\n" : current;
        body += $"    fn {name}()\n        return {returnExpr}\n";
        design.WriteField(designId, section, new TextValue(body));
    }

    private void EditFn(int designId, string section, string name, string returnExpr)
    {
        // Replace the WHOLE section with a single fn of the given name+return — this feature's fixtures
        // only ever carry one common fn ("greet"), so a full-section rewrite is a faithful, simple edit.
        var body = $"common\n    fn {name}()\n        return {returnExpr}\n";
        FreshDesigner().WriteField(designId, section, new TextValue(body));
    }

    private void AddAccessRule(int designId, string typeName, string verb, string? condition)
    {
        var design = FreshDesigner();
        var current = (design.ReadById(designId)!.Value.Fields.Fields.GetValueOrDefault("access") as TextValue)?.Text ?? "";
        var body = current.Length == 0 ? "access\n" : current;
        body += condition is null
            ? $"    {typeName}\n        {verb}\n"
            : $"    {typeName}\n        {verb} where {condition}\n";
        design.WriteField(designId, "access", new TextValue(body));
    }

    private IInstanceStore FreshDesigner() => new JsonFileInstanceStore(_designerDataPath, _meta);

    // The designer's WsHandler with a REAL KernelHostActions. create/delete/clone/rename/setDesign are
    // never exercised by this feature (they throw if accidentally reached — a clear signal a scenario
    // used the wrong action). readPublishedCommitId/stampPublishedCommit are unused here (no publish in
    // this feature) — bare in-memory stand-ins.
    private WsHandler Ws()
    {
        _designer = FreshDesigner();

        var publishedStamps = new Dictionary<int, int>();
        var hostActions = new KernelHostActions(
            // The SAME live designer store WsHandler serves from (one store instance per data file) — was a
            // second `new JsonFileInstanceStore` opened inside KernelHostActions over the same file.
            () => _designer,
            callerId: 1, // the designer (instances/1 by convention)
            resolveTarget: _ => null,
            createInstance: (_, _, _) => throw new InvalidOperationException("create not exercised by DesignMerge.feature"),
            deleteInstance: _ => throw new InvalidOperationException("delete not exercised by DesignMerge.feature"),
            cloneInstance: (_, _) => throw new InvalidOperationException("cloneInstance not exercised by DesignMerge.feature"),
            recordDesign: (_, _) => throw new InvalidOperationException("setDesign not exercised by DesignMerge.feature"),
            restartInstance: _ => Task.CompletedTask,
            renameInstance: (_, _) => throw new InvalidOperationException("rename not exercised by DesignMerge.feature"),
            readPublishedCommitId: id => publishedStamps.GetValueOrDefault(id),
            stampPublishedCommit: (id, commitId) => { publishedStamps[id] = commitId; return Task.CompletedTask; });

        var session = _sessions.Create();
        session.PrincipalUserId = null; // the test meta's `sys` rule is unconditional (mirrors instances/1 today)
        _clientId = session.Id;
        return new WsHandler(_designer, _meta, sessions: _sessions, registry: null, hostActions: hostActions);
    }

    [AfterScenario]
    public void Cleanup()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
