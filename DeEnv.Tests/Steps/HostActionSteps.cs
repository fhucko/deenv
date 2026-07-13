using DeEnv.Designer;
using System.Text.Json;
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
// .app), NOT the real instances/1/app.deenv (which the browser-driven Designer.feature exercises).
// Keeping the host-action test on its own controlled meta isolates the server-path assertions from
// the full seeded designer — a minimal design is enough to prove resolve → project → host action.
[Binding]
public sealed class HostActionSteps
{
    // Sentinel distinguishing "written by the publish" from "left untouched" (the app document).
    private const string TargetAppSentinel = "UNCHANGED-TARGET-APP-SENTINEL";

    // A test-local designer meta-schema: a Db holding a SET of Designs (+ a `users` set of User and a
    // Role enum for the host-action authorization scenarios), where a Design is a whole app (a structured
    // `types` set + the other app-document sections — initialData/common/ui — as text). This is the
    // `Db { designs }` IDE shape the host action's design-resolution reads.
    //
    // The `access` section carries the host-action `sys` subject gated to the Admin role — the mechanism
    // under test: a host action is accepted only when the session's principal satisfies this rule. The
    // "no sys rule" + "ordinary app" scenarios swap in the variants below; the rest run under this one
    // (bound to a seeded admin), so the ADMIN path exercises the full success flow and the others prove
    // the gate. (The real designer, instances/1, uses an UNCONDITIONAL `sys` rule for now — its
    // per-page-load session cannot persist an admin login, so tightening it awaits login persistence;
    // this test meta is where the admin-gated `sys` rule is proven end-to-end at the WS seam.)
    private const string MetaSchema = MetaTypes + SysAdminAccess;

    // The types half, shared by every meta variant (with / without an access section). Includes the M13
    // slice-3 Commit/Branch types (the real instances/1/app.deenv shape) so a commitDesign scenario runs
    // against the SAME meta every other host-action scenario does — no separate designer fixture needed.
    private const string MetaTypes =
        """
        types
            Db
                designs set of Design
                users set of User
                commits set of Commit
                branches set of Branch
            Design
                label text
                initialData text
                common text
                ui text
                render set of MetaNode
                types set of MetaType
            MetaNode
                tag text
                expr text
                attrs set of MetaAttr
                children set of MetaNode
                order int
            MetaAttr
                name text
                value text
                order int
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
            Role enum
                Admin
                Member
            User
                name text
                role Role
                password password
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
        """;

    // The host-action authorization rule: `sys` gated to the Admin role — deny-by-default for everyone
    // else. Plus the M13 slice-3 write-floor immutability guard on Commit/Branch: no rule grants create/
    // edit/delete on either (an always-false condition), so a client write is denied floor-side while
    // sys.commitDesign — writing through the store seam directly — is the only path that can ever create
    // or move one. A leading newline so it concatenates after MetaTypes as its own section.
    private const string SysAdminAccess =
        "\n\naccess\n    sys\n        * where currentUser.role == \"Admin\"\n" +
        "    Commit\n        create edit delete where false\n" +
        "    Branch\n        create edit delete where false\n";

    // A designer-shaped meta with NO access section — the shape-authority-hole scenario: an instance that
    // HAS the designer shape and calls host actions but declares no `sys` rule must reject for everyone.
    private const string MetaSchemaNoAccess = MetaTypes;

    // An ORDINARY app (devlog-shaped: a Milestone set + a User/Role) whose access rules gate its DATA but
    // declare NO `sys` subject — kernel authority is not granted by data rules, so host actions reject
    // even for its own admin. Used to prove the `sys` gate is independent of the data ruleset.
    private const string OrdinaryAppSchema =
        """
        types
            Db
                milestones set of Milestone
                users set of User
            Milestone
                name text
            Role enum
                Admin
                Member
            User
                name text
                role Role
                password password

        access
            Milestone
                read
                * where currentUser.role == "Admin"
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

    // ── host-action authorization (M-auth `sys` subject) ─────────────────────────
    // The meta-schema TEXT the designer store + WsHandler use — the default admin-gated `sys` meta, or a
    // variant a scenario swaps in (no access section / an ordinary data-ruled app). Set BEFORE OpenDesigner.
    private string _metaSchema = MetaSchema;
    // The WS session store + the principal bound on it. Host-action authorization reads the session's
    // PrincipalUserId (exactly like the write floor), so the WsHandler is built with a real session store
    // and the chosen principal — an admin by default (so the existing publish/create/delete/rename/clone
    // scenarios, which now pass through the `sys` gate, are authorized), overridden by the auth scenarios.
    private ClientSessionStore _sessions = null!;
    private int? _principalUserId;
    private bool _principalChosen; // whether a scenario explicitly set the principal (else default admin)
    private int _adminUserId;
    private int _memberUserId;

    // The id of the authored design (the schema object the designer passes — one member of
    // db.designs), and a non-design object's id (a MetaType) for the "not a design" reject.
    private int _designId;
    private int _nonDesignId;

    private string _targetAppPath = "";
    private string _targetDataPath = "";
    private const int TargetId = 7;
    private const int UnknownTargetId = TargetId + 999;

    // What the fake create delegate recorded — the projected app document + the requested NAME + the
    // design's id — so a create scenario can assert the kernel was asked to spawn the right thing (no
    // real host). Addressing is by PATH now (the mount derives from the name), so there are no ports.
    // The design id is threaded so the new instance's registry entry pre-selects its design.
    private string _createdAppDoc = "";
    private string _createdName = "";
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

    // What the fake delete/clone delegates recorded — the ids the kernel was asked to act on — so a
    // delete/clone scenario can assert the channel carried the right arguments (no real host). Clone
    // takes only a source id now (no ports — the clone gets a mount name derived from the source); its
    // OPTIONAL atSeq (M13 slice 7) is accepted here for signature parity but this file's existing
    // scenarios never pass one, so it is discarded, not recorded (nothing to assert yet).
    private int _deletedId;
    private bool _deleteInvoked;
    private int _clonedSourceId;
    private bool _cloneInvoked;

    // What the fake recordDesign delegate recorded — the (targetId, designId) the kernel was asked to
    // record on the registry — so a setDesign scenario can assert the registry-write half ran with the
    // right reference (the deploy half is a real file write, asserted on the target document).
    private int _recordedTargetId;
    private int _recordedDesignId;
    private bool _recordInvoked;

    // M13 slice 4 — a bare in-memory versioning stamp (targetId → the publish's stamped commitId). Every
    // scenario in THIS file targets an unstamped instance (none commits before publishing), so this stays
    // empty for them; Publish.feature exercises the real registry-persisted stamp.
    private readonly Dictionary<int, int> _publishedCommitIds = new();

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

    [Given("a designer instance holding a runtime-created design with a type {string} and no branch")]
    public void GivenDesignerHoldingRuntimeCreatedDesign(string typeName)
    {
        OpenDesigner();
        AddDesign(CustomUiSection, ensureMainBranch: false);
        DesignType("Db", "object");
        DesignType(typeName, "object");
        DesignProp(typeName, "label", "text");
        DesignSetProp("Db", typeName.ToLowerInvariant() + "s", typeName);
    }

    // An INVALID design: its root Db is an object type with no fields (InstanceDescriptionLoader rejects
    // it with a "fields" error). The projection validates the WHOLE app and writes nothing, so the host
    // action surfaces the rejection. No custom UI needed — the types are already invalid.
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

    // Same additive-schema-change shape as GivenDesignerHoldingAdditiveDesign, but for a NON-text field
    // type — proves a newly added decimal/date/datetime field defaults to UNSET (the empty-text leaf),
    // not a fabricated 0/today/now (DefaultBase's absent-field read path).
    [Given("a designer instance holding a design that adds a {string} field of type {string} to {string}")]
    public void GivenDesignerHoldingAdditiveDesignTyped(string newField, string fieldType, string typeName)
    {
        OpenDesigner();
        AddDesign(CustomUiSection);
        DesignType("Db", "object");
        DesignType(typeName, "object");
        DesignProp(typeName, "label", "text");
        DesignProp(typeName, newField, fieldType);
        DesignSetProp("Db", typeName.ToLowerInvariant() + "s", typeName);
    }

    [Given("the design adds type {string} with field {string}")]
    public void GivenDesignAddsTypeWithField(string typeName, string field)
    {
        DesignType(typeName, "object");
        DesignProp(typeName, field, "text");
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

    // A target seeded under a WIDER prior schema — <Type> { label, note } — holding one object with
    // BOTH fields. Publishing a design that DROPS `note` must keep the object and prune the orphaned
    // `note` value (proven by the survival check, which re-opens under the narrower schema's strict guard).
    [Given("a target instance holding an {string} with label {string} and note {string}")]
    public void GivenTargetHoldingItemWithNote(string typeName, string label, string note)
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
                    note text
            """;
        var prior = InstanceDescriptionLoader.Load(priorApp);
        var store = new JsonFileInstanceStore(_targetDataPath, prior);
        var id = store.CreateObject(typeName, new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["label"] = new TextValue(label),
            ["note"]  = new TextValue(note),
        }));
        store.AddToSet(NodePath.Root.Field(set), id);
    }

    // A target seeded under a prior schema where <Type> has ONE scalar field of the given base type +
    // value. Publishing a design that RETYPES that field exercises the value conversion on a type change.
    [Given("a target instance whose {string} has {string} of type {string} set to {string}")]
    public void GivenTargetTypedField(string typeName, string field, string baseType, string value)
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
                    {field} {baseType}
            """;
        var prior = InstanceDescriptionLoader.Load(priorApp);
        var store = new JsonFileInstanceStore(_targetDataPath, prior);
        var id = store.CreateObject(typeName, new ObjectValue(new Dictionary<string, NodeValue>
        {
            [field] = ParseScalar(baseType, value),
        }));
        store.AddToSet(NodePath.Root.Field(set), id);
    }

    // A target whose <Type> has an UNSET optional decimal — stored as the canonical empty-text leaf
    // (the form the UI/WS write produces for a blank optional number), NOT DecimalValue(0). Seeded
    // explicitly so a republish/additive apply must leave it empty rather than convert-and-clobber it.
    [Given("a target instance whose {string} has an unset optional decimal {string}")]
    public void GivenTargetUnsetOptionalDecimal(string typeName, string field)
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
                    {field} decimal
            """;
        var prior = InstanceDescriptionLoader.Load(priorApp);
        var store = new JsonFileInstanceStore(_targetDataPath, prior);
        var id = store.CreateObject(typeName, new ObjectValue(new Dictionary<string, NodeValue>
        {
            [field] = new TextValue(""), // unset optional decimal — the empty-text leaf
        }));
        store.AddToSet(NodePath.Root.Field(set), id);
    }

    // A target whose Db holds a SINGLE object reference (Db has one object-typed prop pointing at a
    // referenced object). Publishing a design that makes that prop a SET exercises the cardinality
    // reshape (single object ref -> one-member set).
    [Given("a target instance whose Db has a single {string} referencing a {string} named {string}")]
    public void GivenTargetSingleRef(string field, string refType, string name)
    {
        Directory.CreateDirectory(_dir);
        _targetAppPath = Path.Combine(_dir, "target.app");
        _targetDataPath = Path.Combine(_dir, "target-data.json");
        File.WriteAllText(_targetAppPath, TargetAppSentinel);

        var priorApp =
            $"""
            types
                Db
                    {field} {refType}
                {refType}
                    name text
            """;
        var prior = InstanceDescriptionLoader.Load(priorApp);
        var store = new JsonFileInstanceStore(_targetDataPath, prior);
        var pid = store.CreateObject(refType, new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name),
        }));
        store.SetReference(NodePath.Root.Field(field), pid); // Db.<field> -> the referenced object
    }

    // A design whose Db holds the named prop as a SET of the element type (its element type carries `name`).
    [Given("a designer instance holding a design with Db's {string} as a set of {string}")]
    public void GivenDesignDbSetOf(string field, string elemType)
    {
        OpenDesigner();
        AddDesign(CustomUiSection);
        DesignType("Db", "object");
        DesignType(elemType, "object");
        DesignProp(elemType, "name", "text");
        DesignSetProp("Db", field, elemType);
    }

    // A design whose element type carries ONE scalar field of the given (possibly RE-typed) base type.
    [Given("a designer instance holding a design with {string} field {string} typed {string}")]
    public void GivenDesignTypedField(string typeName, string field, string fieldType)
    {
        OpenDesigner();
        AddDesign(CustomUiSection);
        DesignType("Db", "object");
        DesignType(typeName, "object");
        DesignProp(typeName, field, fieldType);
        DesignSetProp("Db", typeName.ToLowerInvariant() + "s", typeName);
    }

    // ── Given: host-action authorization (the `sys` subject) ────────────────────

    // The default (admin-gated `sys`) meta: open the designer store over it. The DELETE the auth
    // scenarios fire targets a bare instance id (TargetId), so no design authoring is needed.
    [Given("the designer's access grants sys to the admin role")]
    public void GivenSysGrantsAdmin() => OpenDesigner();

    // A designer-shaped meta with NO access section — the shape-authority-hole case.
    [Given("the designer meta-schema declares no access section")]
    public void GivenNoAccessSection()
    {
        _metaSchema = MetaSchemaNoAccess;
        OpenDesigner();
    }

    // An ordinary (devlog-shaped) app whose access rules gate its DATA but declare no `sys` subject.
    [Given("an ordinary app whose access rules gate its data but declare no sys subject")]
    public void GivenOrdinaryDataRuledApp()
    {
        _metaSchema = OrdinaryAppSchema;
        OpenDesigner();
    }

    // The seeded operators (OpenDesigner mints them from the User type). These assert the fixture, so the
    // scenarios read naturally; the principal is chosen by the "current operator is …" steps below.
    [Given("a seeded admin operator")]
    public async Task GivenSeededAdmin() =>
        await Assert.That(_designer.ReadById(_adminUserId)).IsNotNull();

    [Given("a seeded admin operator and a seeded member operator")]
    public async Task GivenSeededAdminAndMember()
    {
        await Assert.That(_designer.ReadById(_adminUserId)).IsNotNull();
        await Assert.That(_designer.ReadById(_memberUserId)).IsNotNull();
    }

    [Given("the current operator is the admin")]
    public void GivenOperatorAdmin()
    {
        _principalUserId = _adminUserId;
        _principalChosen = true;
    }

    [Given("the current operator is the member")]
    public void GivenOperatorMember()
    {
        _principalUserId = _memberUserId;
        _principalChosen = true;
    }

    [Given("the operator session is anonymous")]
    public void GivenOperatorAnonymous()
    {
        _principalUserId = null;
        _principalChosen = true;
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

    [When("the designer creates an instance named {string} from that design over the WS")]
    public void WhenCreateFromDesign(string name) => Create(_designId, name);

    // A schema id that is NOT a design (an existing MetaType object) — the resolver must reject it
    // before any projection or spawn, so the create delegate is never reached.
    [When("the designer creates an instance from a non-design id over the WS")]
    public void WhenCreateNonDesign() => Create(_nonDesignId, "app");

    [When("the operator renames instance id {int} to {string} over the WS")]
    public void WhenRename(int id, string name) =>
        Send("rename", $$"""{ "type": "int", "value": {{id}} }, { "type": "text", "value": "{{name}}" }""");

    // delete(targetId): a bare instance id (NOT a schema object). The recording delete delegate
    // captures the id; the seam carries it through unchanged and replies ok.
    [When("the operator deletes instance id {int} over the WS")]
    public void WhenDelete(int id) =>
        Send("delete", $$"""{ "type": "int", "value": {{id}} }""");

    // cloneInstance(sourceId): one bare int (the source id). Addressing is by PATH, so there are no
    // port args — the clone gets a mount name derived from the source. The recording clone delegate
    // captures the id; the seam carries it through unchanged and replies ok.
    [When("the operator clones instance id {int} over the WS")]
    public void WhenClone(int sourceId) =>
        Send("cloneInstance", $$"""{ "type": "int", "value": {{sourceId}} }""");

    // publish(design, targetId): arg 0 is the design object's id (resolved against the designer's
    // store), arg 1 the target id (only TargetId resolves to a spec → any other id is rejected).
    private void Publish(int designId, int targetId) =>
        Send("publish", $$"""{ "type": "int", "value": {{designId}} }, { "type": "int", "value": {{targetId}} }""");

    private void Create(int designId, string name) =>
        Send("create", $$"""{ "type": "int", "value": {{designId}} }, { "type": "text", "value": "{{name}}" }""");

    // setDesign(design, targetId): the IDE's Apply — arg 0 the design object's id, arg 1 the target id.
    // The real KernelHostActions both records the reference (the fake recordDesign captures it) AND
    // writes the projected document onto the target (a real file write, like publish).
    [When("the operator applies that design to the target's id over the WS")]
    public void WhenApplyDesignToTarget() =>
        Send("setDesign", $$"""{ "type": "int", "value": {{_designId}} }, { "type": "int", "value": {{TargetId}} }""");

    // ── When: importRender over the WS (M12 X2a) ────────────────────────────────

    // importRender(design): arg 0 the design's id. The real KernelHostActions resolves it against the
    // caller's own store and runs SchemaBridge.ImportRender (convert the text `ui` render to structured
    // MetaNode rows + clear `ui`), atomically. Drives the full server path: WsHandler.HandleHostAction →
    // KernelHostActions.Run → SchemaBridge.ImportRender, gated by the `sys` access rule.
    [When("the designer imports that design's render over the WS")]
    public void WhenImportRender() =>
        Send("importRender", $$"""{ "type": "int", "value": {{_designId}} }""");

    [When("the operator imports that design's render over the WS")]
    public void WhenOperatorImportRender() => WhenImportRender();

    // The design's own render/ui state, read fresh from disk (KernelHostActions wrote through the store).
    private ObjectValue FreshDesign() =>
        (ObjectValue)FreshDesigner().ReadNode(NodePath.Root.Field("designs").Key(_designId.ToString()))!;

    [Then("the design's `ui` text is cleared")]
    public async Task ThenDesignUiCleared() =>
        await Assert.That(((TextValue)FreshDesign().Fields["ui"]).Text).IsEqualTo("");

    [Then("the design's `render` set now holds the imported tree")]
    public async Task ThenDesignRenderPopulated()
    {
        var render = FreshDesign().Fields.GetValueOrDefault("render") as SetValue;
        await Assert.That(render).IsNotNull();
        await Assert.That(render!.Members.Count).IsGreaterThan(0);
    }

    // The authorization reject teeth for importRender: the design's `ui` text is UNCHANGED (still holds
    // the original render) and `render` stays empty — the `sys` gate blocked the action BEFORE it reached
    // KernelHostActions.Run, so nothing was converted (mirrors ThenNotAskedToDelete).
    [Then("the design's render was not imported")]
    public async Task ThenRenderNotImported()
    {
        var design = FreshDesign();
        await Assert.That(((TextValue)design.Fields["ui"]).Text.Length).IsGreaterThan(0);
        var render = design.Fields.GetValueOrDefault("render") as SetValue;
        await Assert.That(render is null || render.Members.Count == 0).IsTrue();
    }

    // ── When: commitDesign over the WS (M13 slice 3) ────────────────────────────

    // commitDesign(design, message, migration): arg 0 the design's id, arg 1 the commit message text,
    // arg 2 the migration source. Every commit
    // tracks the message it just committed — the "that commit" Then-steps below always read the MOST
    // RECENT one, so a two-commit scenario's later assertions automatically read the second commit.
    private string _lastCommitMessage = "";
    private int _versionBeforeLastCommit;

    [When("the designer commits that design with message {string} over the WS")]
    public void WhenCommitDesign(string message) => Commit(_designId, message);

    [When("the designer commits that design with message {string} and migration")]
    public void WhenCommitDesignWithMigration(string message, string migration) => Commit(_designId, message, migration);

    [When("the designer commits that design with message {string} and revert migration")]
    public void WhenCommitDesignWithRevertMigration(string message, string revertMigration) =>
        Commit(_designId, message, migration: "", revertMigration);

    [When("the operator commits design id {int} with message {string} over the WS")]
    public void WhenOperatorCommitsDesignId(int designId, string message) => Commit(designId, message);

    [Given("the designer already committed that design with message {string}")]
    public void GivenAlreadyCommitted(string message) => Commit(_designId, message);

    private void Commit(int designId, string message, string migration = "", string revertMigration = "")
    {
        _lastCommitMessage = message;
        // _designer is the SAME store instance Ws() builds the WsHandler over — CurrentVersion here IS
        // the "before the commit's own writes" baseline the new commit's logSeq must equal.
        _versionBeforeLastCommit = _designer.CurrentVersion;
        Send("commitDesign",
            $$"""{ "type": "int", "value": {{designId}} }, { "type": "text", "value": "{{message}}" }, { "type": "text", "value": {{JsonSerializer.Serialize(migration)}} }, { "type": "text", "value": {{JsonSerializer.Serialize(revertMigration)}} }""");
    }

    // Build the WsHandler (which mints + binds the principal's session), then send ONE hostAction frame
    // carrying that session's clientId + the given (already-serialized) args — so authorization decides
    // over the bound principal. `Ws()` sets `_clientId`; it runs before the frame is built (the receiver
    // is evaluated before the argument), so the id is current.
    private void Send(string action, string argsJson)
    {
        var ws = Ws();
        _reply = ws.ProcessMessage(
            $$"""{ "op": "hostAction", "clientId": "{{_clientId}}", "action": "{{action}}", "args": [ {{argsJson}} ] }""");
    }

    // The designer's WsHandler with a real KernelHostActions: it acts as the designer (its own
    // meta+data are the IDE it projects from), resolves ONLY TargetId → the target spec, and its
    // create/delete/clone delegates RECORD what they were asked to do instead of driving a real kernel.
    private WsHandler Ws()
    {
        // The delete/clone seam scenarios carry no authored design (they never touch the store — the
        // action just routes ids to the kernel), so open a bare designer instance lazily to give the
        // WsHandler a valid store + description. The publish/create scenarios already opened one.
        if (_designer == null) OpenDesigner();

        // Re-open fresh over the SAME data file at the START of each Ws() build so a NEW scenario step's
        // handler reads ground truth after any prior on-disk writes. Post the mirror-clobber fix, KernelHostActions
        // shares THIS store (resolveStore below) rather than opening its own second instance, so within one
        // Ws() lifetime the host actions and the WsHandler are the SAME single store over the file — the
        // production shape (one store instance per hosted instance), no longer two independent copies.
        _designer = new JsonFileInstanceStore(_designerDataPath, _meta);

        var hostActions = new KernelHostActions(
            () => _designer,
            callerId: 1, // the designer (instances/1 by convention); never equals TargetId
            id => id == TargetId ? new InstanceSpec(TargetId, "target", _targetAppPath, _targetDataPath) : null,
            createInstance: (appDoc, name, designId) =>
            {
                _createdAppDoc = appDoc;
                _createdName = name;
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
            cloneInstance: (sourceId, _) =>
            {
                _clonedSourceId = sourceId;
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
            },
            // M13 slice 4: every scenario here targets an UNSTAMPED instance (none of these scenarios
            // author a commit before publishing), so publish always takes the fallback path — a bare
            // in-memory stamp that starts empty is sufficient (Publish.feature drives the real
            // read/write-through-the-registry path where stamping persistence actually matters).
            readPublishedCommitId: id => _publishedCommitIds.GetValueOrDefault(id),
            stampPublishedCommit: (id, commitId) =>
            {
                _publishedCommitIds[id] = commitId;
                return Task.CompletedTask;
            });
        // A WS session carrying the chosen principal — host-action authorization decides over it (the
        // `sys` access rule, evaluated with the session's PrincipalUserId, exactly like the write floor).
        // Default to the seeded admin when a scenario did not choose one, so the non-auth scenarios
        // (publish/create/delete/rename/clone) run authorized through the `sys` gate. The clientId threads
        // into each hostAction frame below so the handler resolves the principal.
        var session = _sessions.Create();
        session.PrincipalUserId = _principalChosen ? _principalUserId : _adminUserId;
        _clientId = session.Id;
        // _designer/_meta are guaranteed set above (a Given, or the lazy OpenDesigner here).
        return new WsHandler(_designer!, _meta, sessions: _sessions, registry: null, hostActions: hostActions);
    }

    // The clientId of the session bound in Ws() — sent on each hostAction frame so the handler resolves
    // the principal. Set by Ws(); read by the frame builders.
    private string _clientId = "";

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

    [Given("the target has log and genesis files")]
    public async Task GivenTargetHasHistoryFiles()
    {
        await Assert.That(File.Exists(AppPaths.LogPathForDataPath(_targetDataPath))).IsTrue();
        await Assert.That(File.Exists(AppPaths.GenesisPathForDataPath(_targetDataPath))).IsTrue();
    }

    [Then("the target has no log or genesis files")]
    public async Task ThenTargetHasNoHistoryFiles()
    {
        await Assert.That(File.Exists(AppPaths.LogPathForDataPath(_targetDataPath))).IsFalse();
        await Assert.That(File.Exists(AppPaths.GenesisPathForDataPath(_targetDataPath))).IsFalse();
    }

    [Then("the target still holds an {string} labelled {string}")]
    public async Task ThenTargetStillHolds(string typeName, string label)
    {
        // Open a store over the preserved data with the now-published schema. The construction runs the
        // STRICT startup guard, so a lingering undeclared field (a removed field NOT pruned) would throw
        // here — a clean open that finds the row proves both survival and the prune.
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var item = store.ReadExtent(typeName).Values
            .FirstOrDefault(o => o.Fields.GetValueOrDefault("label") is TextValue t && t.Text == label);
        await Assert.That(item).IsNotNull();
    }

    [Then("the target's {string} reads {string} as {string} {string}")]
    public async Task ThenTargetFieldReads(string typeName, string field, string baseType, string value)
    {
        // Open under the now-published schema (strict guard) and read the field back. The row survived
        // the apply, and the field holds the CONVERTED value (or its default when unconvertible) — read
        // as the new declared type. Compared via canonical text (a surviving row under the new schema
        // guarantees the stored value already matches the new declared type, else apply would reseed).
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var item = store.ReadExtent(typeName).Values.FirstOrDefault();
        await Assert.That(item).IsNotNull();
        var actual = item!.Fields.GetValueOrDefault(field) switch
        {
            TextValue t    => t.Text,
            IntValue i     => i.Value.ToString(),
            DecimalValue d => d.Value.ToString(),
            BoolValue b    => b.Value ? "true" : "false",
            var other      => other?.ToString(),
        };
        await Assert.That(actual).IsEqualTo(value);
    }

    [Then("the target's Db {string} set holds the {string} named {string}")]
    public async Task ThenDbSetHolds(string field, string elemType, string name)
    {
        // The single reference was reshaped into a one-member set, preserved across the apply. Open
        // under the new (set) schema, read the set, and confirm its one member is the referenced object.
        var published = InstanceDescriptionLoader.LoadFile(_targetAppPath);
        var store = new JsonFileInstanceStore(_targetDataPath, published);
        var set = store.ReadNode(NodePath.Root.Field(field)) as SetValue;
        await Assert.That(set).IsNotNull();
        await Assert.That(set!.Members.Count).IsEqualTo(1);
        var member = set.Members.Values.First() as ObjectValue;
        await Assert.That((member?.Fields.GetValueOrDefault("name") as TextValue)?.Text).IsEqualTo(name);
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

    [Then("a new instance was created named {string}")]
    public async Task ThenCreatedNamed(string name)
    {
        await Assert.That(_createInvoked).IsTrue();
        await Assert.That(_createdName).IsEqualTo(name);
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

    // The authorization reject teeth: the delete delegate was NEVER invoked — the `sys` gate blocked the
    // action BEFORE it reached the seam, so nothing was deleted (not merely that the reply carried an error).
    [Then("the kernel was not asked to delete anything")]
    public async Task ThenNotAskedToDelete() =>
        await Assert.That(_deleteInvoked).IsFalse();

    [Then("the kernel was asked to clone source id {int}")]
    public async Task ThenAskedToClone(int sourceId)
    {
        await Assert.That(_cloneInvoked).IsTrue();
        await Assert.That(_clonedSourceId).IsEqualTo(sourceId);
    }

    [Then("the kernel was asked to record that design on the target's id")]
    public async Task ThenAskedToRecordDesign()
    {
        await Assert.That(_recordInvoked).IsTrue();
        await Assert.That(_recordedTargetId).IsEqualTo(TargetId);
        await Assert.That(_recordedDesignId).IsEqualTo(_designId);
    }

    // ── Then: db.commits / db.branches assertions (M13 slice 3) ─────────────────
    //
    // Every lookup below returns its INTRINSIC ID alongside its fields (never bare ObjectValue ==
    // comparisons — ObjectValue is a record whose auto-generated equality falls back to the backing
    // Dictionary's REFERENCE equality, so two structurally-identical-but-freshly-read ObjectValues from
    // separate ReadNode calls are never equal). The id is what a caller needing to WRITE (objectPropChange/
    // setReferenceField, both id-addressed) or cross-reference (a ReferenceValue's TargetId) actually needs.
    //
    // Read via a FRESH store over the SAME data file (mirroring ThenTargetStillHolds's pattern), not the
    // `_designer` field's cached in-memory copy: KernelHostActions.CommitDesign opens its OWN store
    // instance internally (same file, real KernelHostActions convention — see ResolveDesign's doc), so its
    // writes land on disk but never update `_designer`'s already-loaded copy. A fresh re-open always
    // observes ground truth, matching how a real second process/session would see it too.
    private IInstanceStore FreshDesigner() => new JsonFileInstanceStore(_designerDataPath, _meta);

    private SetValue CommitsSet() => (SetValue)FreshDesigner().ReadNode(NodePath.Root.Field("commits"))!;
    private SetValue BranchesSet() => (SetValue)FreshDesigner().ReadNode(NodePath.Root.Field("branches"))!;

    private (int Id, ObjectValue Fields) CommitByMessage(string message) =>
        CommitsSet().Members
            .Where(m => m.Value is ObjectValue)
            .Select(m => (m.Key, Fields: (ObjectValue)m.Value))
            .First(c => c.Fields.Fields.GetValueOrDefault("message") is TextValue t && t.Text == message);

    private (int Id, ObjectValue Fields) MainBranch() =>
        BranchesSet().Members
            .Where(m => m.Value is ObjectValue)
            .Select(m => (m.Key, Fields: (ObjectValue)m.Value))
            .First(b => b.Fields.Fields.GetValueOrDefault("name") is TextValue { Text: "main" }
                && b.Fields.Fields.GetValueOrDefault("workingCopy") is ReferenceValue { TargetId: var t } && t == _designId);

    // The authorization reject teeth for commitDesign: the store's db.commits stayed empty — the `sys`
    // gate blocked the action BEFORE it ever reached KernelHostActions.Run (mirrors ThenNotAskedToDelete).
    [Then("the kernel was not asked to commit anything")]
    public async Task ThenNotAskedToCommit() =>
        await Assert.That(CommitsSet().Members.Count).IsEqualTo(0);

    [Then("db.commits holds a commit with message {string}")]
    public async Task ThenCommitsHoldsMessage(string message) =>
        await Assert.That(CommitsSet().Members.Values.OfType<ObjectValue>()
            .Any(c => c.Fields.GetValueOrDefault("message") is TextValue t && t.Text == message)).IsTrue();

    [Then("that commit has a timestamp")]
    public async Task ThenCommitHasTimestamp()
    {
        var commit = CommitByMessage(_lastCommitMessage);
        await Assert.That(commit.Fields.Fields.GetValueOrDefault("at")).IsTypeOf<DateTimeValue>();
    }

    [Then("that commit's design reference is the committed design")]
    public async Task ThenCommitDesignRefMatches()
    {
        var commit = CommitByMessage(_lastCommitMessage);
        var refVal = commit.Fields.Fields.GetValueOrDefault("design") as ReferenceValue;
        await Assert.That(refVal?.TargetId).IsEqualTo(_designId);
    }

    [Then("that commit's parent is empty")]
    public async Task ThenCommitParentEmpty()
    {
        var commit = CommitByMessage(_lastCommitMessage);
        var parent = commit.Fields.Fields.GetValueOrDefault("parent");
        var targetId = parent is ReferenceValue r ? r.TargetId : null;
        await Assert.That(targetId).IsNull();
    }

    [Then("that commit's logSeq equals the head version before the commit's own writes")]
    public async Task ThenLogSeqMatchesPreCommitVersion()
    {
        var commit = CommitByMessage(_lastCommitMessage);
        var logSeq = ((IntValue)commit.Fields.Fields["logSeq"]).Value;
        await Assert.That(logSeq).IsEqualTo(_versionBeforeLastCommit);
    }

    [Then("that commit's text is the design's canonical printed document")]
    public async Task ThenCommitTextIsCanonical()
    {
        var commit = CommitByMessage(_lastCommitMessage);
        var text = ((TextValue)commit.Fields.Fields["text"]).Text;
        var design = _designer.ReadNode(NodePath.Root.Field("designs").Key(_designId.ToString()))!;
        var expected = SchemaBridge.ProjectDesignDb(design);
        await Assert.That(text).IsEqualTo(expected);
    }

    [Then("that commit's migration is")]
    public async Task ThenCommitMigrationIs(string migration)
    {
        var commit = CommitByMessage(_lastCommitMessage);
        var text = ((TextValue)commit.Fields.Fields["migration"]).Text;
        await Assert.That(text).IsEqualTo(migration);
    }

    [Then("that commit's idMap covers every type and prop in the design")]
    public async Task ThenIdMapCoversTypesAndProps()
    {
        var commit = CommitByMessage(_lastCommitMessage);
        var idMap = (DictionaryValue)commit.Fields.Fields["idMap"];
        var design = _designer.ReadNode(NodePath.Root.Field("designs").Key(_designId.ToString()))!;
        var expected = SchemaBridge.Snapshot(design).IdMap;
        await Assert.That(idMap.Entries.Count).IsEqualTo(expected.Count);
        foreach (var (path, id) in expected)
        {
            var key = new TextValue(path);
            await Assert.That(idMap.Entries.ContainsKey(key)).IsTrue();
            await Assert.That(((IntValue)idMap.Entries[key]).Value).IsEqualTo(id);
        }
    }

    [Then("the design's main branch head points at that commit")]
    public async Task ThenMainBranchHeadPointsAtCommit()
    {
        var (expectedId, _) = CommitByMessage(_lastCommitMessage);
        var (_, branchFields) = MainBranch();
        var head = branchFields.Fields.GetValueOrDefault("head") as ReferenceValue;
        await Assert.That(head?.TargetId).IsEqualTo(expectedId);
    }

    [Then("that commit's parent is the {string} commit")]
    public async Task ThenCommitParentIs(string parentMessage)
    {
        var (parentId, _) = CommitByMessage(parentMessage);
        var (_, secondFields) = CommitByMessage(_lastCommitMessage);
        var parentRef = secondFields.Fields.GetValueOrDefault("parent") as ReferenceValue;
        await Assert.That(parentRef?.TargetId).IsEqualTo(parentId);
    }

    [Then("that commit's logSeq is strictly greater than the {string} commit's logSeq")]
    public async Task ThenLogSeqStrictlyGreater(string earlierMessage)
    {
        var earlier = CommitByMessage(earlierMessage);
        var earlierSeq = ((IntValue)earlier.Fields.Fields["logSeq"]).Value;
        var later = CommitByMessage(_lastCommitMessage);
        var laterSeq = ((IntValue)later.Fields.Fields["logSeq"]).Value;
        await Assert.That(laterSeq).IsGreaterThan(earlierSeq);
    }

    [Then("db.commits is empty")]
    public async Task ThenCommitsEmpty() =>
        await Assert.That(CommitsSet().Members.Count).IsEqualTo(0);

    [Then("the design's main branch head is unset")]
    public async Task ThenMainBranchHeadUnset()
    {
        // No Branch exists yet either (nothing has been adopted/ensured over this bare test store), or a
        // branch exists with no head — either way there is no committed head to observe.
        var branches = BranchesSet().Members.Values.OfType<ObjectValue>().ToList();
        if (branches.Count == 0) return;
        var main = branches.FirstOrDefault(b => b.Fields.GetValueOrDefault("name") is TextValue { Text: "main" });
        var head = main?.Fields.GetValueOrDefault("head") as ReferenceValue;
        await Assert.That(head?.TargetId).IsNull();
    }

    // ── the write-floor immutability scenario ────────────────────────────────────

    private string _messageBeforeWrite = "";
    private string _headBeforeWrite = "";

    [When("a client-path write to the commit's message field is attempted")]
    public void WhenClientWritesCommitMessage()
    {
        var (commitId, fields) = CommitByMessage(_lastCommitMessage);
        _messageBeforeWrite = ((TextValue)fields.Fields["message"]).Text;
        var ws = Ws();
        _reply = ws.ProcessMessage(
            $$"""{ "op": "commit", "clientId": "{{_clientId}}", "edits": [ { "objectId": {{commitId}}, "prop": "message", "value": { "type": "text", "value": "HACKED" } } ], "creates": [], "relations": [] }""");
    }

    [Then("the commit's message is unchanged")]
    public async Task ThenCommitMessageUnchanged()
    {
        var commit = CommitByMessage(_lastCommitMessage);
        await Assert.That(((TextValue)commit.Fields.Fields["message"]).Text).IsEqualTo(_messageBeforeWrite);
    }

    [When("a client-path write to the branch's head field is attempted")]
    public void WhenClientWritesBranchHead()
    {
        var (branchId, fields) = MainBranch();
        var head = fields.Fields.GetValueOrDefault("head") as ReferenceValue;
        _headBeforeWrite = head?.TargetId?.ToString() ?? "";
        var ws = Ws();
        _reply = ws.ProcessMessage(
            $$"""{ "op": "commit", "clientId": "{{_clientId}}", "edits": [], "creates": [], "relations": [ { "kind": "setUnlinkByProp", "parentId": {{branchId}}, "prop": "head" } ] }""");
    }

    [Then("the branch's head is unchanged")]
    public async Task ThenBranchHeadUnchanged()
    {
        var (_, fields) = MainBranch();
        var head = fields.Fields.GetValueOrDefault("head") as ReferenceValue;
        await Assert.That(head?.TargetId?.ToString() ?? "").IsEqualTo(_headBeforeWrite);
    }

    // ── the dict-write floor immutability legs (review fix 3) ────────────────────

    // The idMap contents of the last-committed Commit, read fresh (key → int), for the before/after
    // comparison. A commit's idMap is never empty (a design always has ≥1 type), so it has a real key.
    private Dictionary<string, int> CommitIdMap()
    {
        var (_, fields) = CommitByMessage(_lastCommitMessage);
        var dict = (DictionaryValue)fields.Fields["idMap"];
        return dict.Entries.ToDictionary(
            e => ((TextValue)e.Key).Text, e => ((IntValue)e.Value).Value);
    }

    private Dictionary<string, int> _idMapBeforeWrite = new();

    [When("a client addEntry into the commit's idMap is attempted")]
    public void WhenClientAddEntryIntoIdMap()
    {
        var (commitId, _) = CommitByMessage(_lastCommitMessage);
        _idMapBeforeWrite = CommitIdMap();
        // addEntry's value is a BARE scalar at the top level (a `dict of int` entry is a raw int — see
        // ws.ts entryAdd), NOT the tagged { type, value } form the id-addressed ops use.
        var ws = Ws();
        _reply = ws.ProcessMessage(
            $$"""{ "op": "addEntry", "clientId": "{{_clientId}}", "path": "/commits/{{commitId}}/idMap", "key": "Injected", "value": 777 }""");
    }

    [When("a client-path write into the commit's idMap is attempted")]
    public void WhenClientWriteIntoIdMap()
    {
        var (commitId, _) = CommitByMessage(_lastCommitMessage);
        _idMapBeforeWrite = CommitIdMap();
        // Overwrite an EXISTING lineage id (the first key in the map) with a bogus value — the review's
        // exact probe (a path-write clobbering a seeded lineage id must be denied). `write`'s value is a
        // BARE scalar too (see ws.ts).
        var existingKey = _idMapBeforeWrite.Keys.First();
        var ws = Ws();
        _reply = ws.ProcessMessage(
            $$"""{ "op": "write", "clientId": "{{_clientId}}", "path": "/commits/{{commitId}}/idMap/{{existingKey}}", "value": 999 }""");
    }

    [Then("the commit's idMap is unchanged")]
    public async Task ThenCommitIdMapUnchanged()
    {
        var after = CommitIdMap();
        await Assert.That(after.Count).IsEqualTo(_idMapBeforeWrite.Count);
        foreach (var (key, value) in _idMapBeforeWrite)
        {
            await Assert.That(after.ContainsKey(key)).IsTrue();
            await Assert.That(after[key]).IsEqualTo(value);
        }
    }

    // ── the single-atomic-changeset log proof (review fix 3) ─────────────────────

    private int _logLinesBeforeCommit;

    [Given("the designer's own log line count is remembered before committing over the WS")]
    public void GivenLogLinesRememberedWs()
    {
        // The designer store's data file exists after OpenDesigner (a construction Save + AddDesign
        // writes), so its append-only log sibling exists too. Count its lines before the commit.
        var logPath = AppPaths.LogPathForDataPath(_designerDataPath);
        _logLinesBeforeCommit = File.Exists(logPath) ? File.ReadAllLines(logPath).Length : 0;
    }

    [Then("the designer's log grew by exactly one entry")]
    public async Task ThenLogGrewByOne()
    {
        var logPath = AppPaths.LogPathForDataPath(_designerDataPath);
        var after = File.ReadAllLines(logPath).Length;
        await Assert.That(after).IsEqualTo(_logLinesBeforeCommit + 1);
    }

    [Then("the designer's log replays from genesis to the live snapshot")]
    public async Task ThenLogReplaysToSnapshot()
    {
        // The store's OWN fsck over the SAME live files: replay(genesis→head) must reproduce the snapshot
        // (the slice-1 invariant), proving the whole commit — creates + refs + set link + dict writes +
        // head advance — landed as one internally-consistent changeset.
        var store = new JsonFileInstanceStore(_designerDataPath, _meta);
        await Assert.That(store.Fsck()).IsTrue();
    }

    // The write-floor's `{ error }` reject — same wire shape as a host-action reject (ThenReplyError), but
    // this phrasing covers a DIFFERENT op (objectPropChange/setReferenceField, not hostAction), so it is
    // its own step rather than overloading ThenReplyError's Gherkin text.
    [Then("the write is denied")]
    public async Task ThenWriteDenied()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(_reply);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsTrue();
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
        File.WriteAllText(_metaAppPath, _metaSchema);
        _meta = InstanceDescriptionLoader.LoadFile(_metaAppPath);
        _designerDataPath = Path.Combine(_dir, "designer-data.json");
        _designer = new JsonFileInstanceStore(_designerDataPath, _meta);

        // Seed an admin + a member operator when the meta declares a User type (the `sys`-gated + ordinary
        // metas do; a design-resolution-only scenario using a User-less meta would skip this). The session
        // store binds the chosen principal; host-action authorization then decides over it.
        _sessions = new ClientSessionStore();
        if (_meta.FindType("User") is not null)
        {
            _adminUserId = SeedUser("admin", "Admin");
            _memberUserId = SeedUser("bob", "Member");
        }
    }

    // Seed one User (name + role) into the designer store, linked into the root `users` set so it is a
    // real graph member (survives GC). Returns its id — the principal a session binds to.
    private int SeedUser(string name, string role)
    {
        var id = _designer.CreateObject("User", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue(name),
            ["role"] = new TextValue(role),
        }));
        _designer.AddToSet(NodePath.Root.Field("users"), id);
        return id;
    }

    // Mint one Design into db.designs (its `types` set starts empty; DesignType/DesignProp fill it).
    // The three section texts are authored verbatim; an empty ui section means the generic UI. Also
    // ensures the design's `main` Branch (the same idempotent shape KernelHost.EnsureMainBranches creates
    // at boot — M13 slice 3) so a commitDesign scenario has something to chain onto; head starts UNSET.
    private void AddDesign(string uiSection, bool ensureMainBranch = true)
    {
        _designId = _designer.CreateObject("Design", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["label"]       = new TextValue("app"),
            ["initialData"] = new TextValue(""),
            ["common"]      = new TextValue(""),
            ["ui"]          = new TextValue(uiSection),
        }));
        _designer.AddToSet(NodePath.Root.Field("designs"), _designId);

        if (!ensureMainBranch) return;

        var branchId = _designer.CreateObject("Branch", new ObjectValue(new Dictionary<string, NodeValue>
        {
            ["name"] = new TextValue("main"),
        }));
        _designer.AddToSet(NodePath.Root.Field("branches"), branchId);
        _designer.WriteReference(branchId, "workingCopy", _designId, "Design");
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

    // A base-typed scalar NodeValue from its base-type name + text value (for seeding typed fields).
    private static NodeValue ParseScalar(string baseType, string value) => baseType switch
    {
        "int"     => new IntValue(int.Parse(value)),
        "decimal" => new DecimalValue(decimal.Parse(value)),
        "bool"    => new BoolValue(bool.Parse(value)),
        _         => new TextValue(value),
    };
}
