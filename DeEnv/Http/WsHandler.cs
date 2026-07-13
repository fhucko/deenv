using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Http;

// ── the wire model ─────────────────────────────────────────────────────────────
//
// WS wire contract between C# server and TS client. Shapes use camelCase to match
// the original hand-built JSON. Complex bodies (values, vars, args) stay as raw
// JsonElement so they can be interpreted schema-driven later.
public sealed record WsRequest
{
    public string? Op { get; init; }
    public int? Id { get; init; }        // correlation id
    public string? ClientId { get; init; }
    public string? Path { get; init; }
    public string? Action { get; init; }
    public JsonElement? Args { get; init; }
    public int? SetId { get; init; }
    public int? ObjectId { get; init; }
    public string? TypeName { get; init; }
    public string? Prop { get; init; }
    public string? Key { get; init; }
    public int? RefId { get; init; }
    public int? TempId { get; init; }
    public int? LastId { get; init; }
    public bool? Clear { get; init; }
    public JsonElement? Value { get; init; }
    public JsonElement? Vars { get; init; }
    public JsonElement? SlotState { get; init; }  // client view-state for refetch (render-as-planner)
    public int? HandlerFn { get; init; }
    public string? HandlerSlot { get; init; }
    public string? Name { get; init; }
    public string? Password { get; init; }
    public JsonElement? Edits { get; init; }
    public JsonElement? Creates { get; init; }
    public JsonElement? Relations { get; init; }
    public int? BaseVersion { get; init; }  // optimistic concurrency guard
    public string[]? Texts { get; init; }
}

// Response records serialize using camelCase (via `_jsonOpts`) so they match the old wire bytes.
// `newVersion` is carried on every mutating reply so clients stay in sync with store version.

public sealed record HelloResponse
{
    public string Op => "hello";
    public required bool SessionAlive { get; init; }
}


// The reply for a `commit` op: all-or-nothing batch of field edits + creates + relations from a ctx.commit().
// `idMap` (atomic-commit Step B) carries the negative→real id remap for every created object plus its minted
// nested-collection ids, so the client re-keys its optimistic graph. Omitted (null) on an edits-only commit.
// `newVersion` (optimistic-concurrency anti-clobber) is the store's version AFTER this commit landed — the
// client re-pins the committing ctx's baseVersion to it, so a SECOND commit from the SAME (still-open) ctx
// is based on its own just-applied change, not the stale version it opened with (ws.ts's ctxBaseVersion).
public sealed record CommitResponse
{
    public string Op => "commit";
    public bool Ok => true;
    public IReadOnlyList<CommitIdMapEntry>? IdMap { get; init; }
    public required int NewVersion { get; init; }
}

// One created object's remap in a commit reply: its transient client id → the real extent id, plus the
// object's minted nested COLLECTION props (id + element type), so the client re-keys its transient arrays.
public sealed record CommitIdMapEntry
{
    public required int TempId { get; init; }
    public required int RealId { get; init; }
    public required Dictionary<string, CollectionInfo> Collections { get; init; }
}


// A collection prop the store minted on a new object: its intrinsic id + element type, so
// the client re-keys the transient array it created optimistically. Field order: id, then
// elementTypeName (matching the former literal).
public sealed record CollectionInfo
{
    public required int Id { get; init; }
    public required string ElementTypeName { get; init; }
}

public sealed record RefetchResponse
{
    public string Op => "refetch";
    public required JsonNode State { get; init; }
}

public sealed record HostActionResponse
{
    public string Op => "hostAction";
    public bool Ok => true;
    public object? Report { get; init; }
}

public sealed record AckRemapResponse
{
    public string Op => "ackRemap";
    public bool Ok => true;
}

public sealed record ParseExprsResponse
{
    public string Op => "parseExprsResult";
    public required Dictionary<string, string> Entries { get; init; }
}

public sealed record LoginResponse
{
    public string Op => "login";
    public required bool Ok { get; init; }
    public int? UserId { get; init; }
}

public sealed record LogoutResponse
{
    public string Op => "logout";
    public bool Ok => true;
}

public sealed record ErrorResponse
{
    public required string Error { get; init; }
}

public sealed record ConflictResponse
{
    public required string Error { get; init; }
    public required IReadOnlyList<ConflictFieldWire> Conflicts { get; init; }
    public required int NewVersion { get; init; }
}

public sealed record ConflictFieldWire
{
    public required int Object { get; init; }
    public required string TypeName { get; init; }
    public required string Field { get; init; }
    public object? Base { get; init; }
    public object? Mine { get; init; }
    public object? Theirs { get; init; }
}

public abstract record ParsedRelation(int ChildRef, string ChildType);
public sealed record ParsedSetRelation(int SetId, int Child, string ElementType) : ParsedRelation(Child, ElementType);
public sealed record ParsedRefRelation(int ParentId, string Prop, int Child, string TargetType) : ParsedRelation(Child, TargetType);

public sealed class WsHandler
{
    private readonly IInstanceStore _store;
    private readonly InstanceDescription _desc;
    private readonly TypeResolver _resolver;
    private readonly ClientSessionStore? _sessions;
    private readonly LiveRegistry _registry;
    private readonly IHostActions _hostActions;
    private readonly Func<string, string, bool> _verifyPassword;
    private readonly string _mountBase;
    private readonly Func<Code.ExecObject, int, Code.ExecContext, Code.IExecValue>? _publishPreview;
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public WsHandler(IInstanceStore store, InstanceDescription desc, ClientSessionStore? sessions = null,
        LiveRegistry? registry = null, IHostActions? hostActions = null, string mountBase = "/",
        Func<string, string, bool>? verifyPassword = null,
        Func<Code.ExecObject, int, Code.ExecContext, Code.IExecValue>? publishPreview = null)
    {
        _store = store;
        _desc = desc;
        _resolver = new TypeResolver(desc);
        _sessions = sessions;
        _registry = registry ?? new LiveRegistry();
        _hostActions = hostActions ?? new NoHostActions();
        _mountBase = mountBase;
        _verifyPassword = verifyPassword ?? Code.AuthCrypto.Verify;
        _publishPreview = publishPreview;
    }

    private ClientSession? Session(WsRequest req) =>
        _sessions != null && req.ClientId is { } id ? _sessions.Get(id) : null;

    private static int Resolve(ClientSession? session, int id) => session?.ResolveId(id) ?? id;

    private Code.AccessFloor Floor(WsRequest req) =>
        new(_desc.Rules ?? [], Code.AccessFloor.LoadPrincipal(_store, _desc, Session(req)?.PrincipalUserId));

    private static void RequireWrite(Code.AccessFloor floor, string verb, string typeName, Code.ExecObject target)
    {
        if (!floor.CanWrite(verb, typeName, target))
            throw new InvalidOperationException($"Access denied: not allowed to {verb} '{typeName}'.");
    }

    private Code.ExecObject CandidateFromValue(JsonElement value, TypeDefinition type) =>
        Code.AccessFloor.ScalarObject(type.Name, 0, (ObjectValue)DeserializeValue(value, type), _desc);

    private ObjectValue HashPasswordFields(string typeName, ObjectValue obj)
    {
        var fields = new Dictionary<string, NodeValue>();
        foreach (var (name, value) in obj.Fields)
            if (HashLeaf(typeName, name, value) is { } toWrite)
                fields[name] = toWrite;
        return new ObjectValue(fields);
    }

    // A single leaf, hashed when (typeName, prop) is a password field: the plaintext → its PBKDF2 hash, an
    // EMPTY plaintext → null (the caller writes nothing). A non-password field returns the leaf unchanged.
    private NodeValue? HashLeaf(string typeName, string prop, NodeValue leaf) =>
        _desc.IsPasswordProp(typeName, prop) ? HashPlaintext(leaf) : leaf;

    // A scalar leaf whose DECLARED type is itself `password` (a `dict of password` entry value, or a path
    // `write` onto a password leaf): same hash-or-skip as a password field. A non-password type is unchanged.
    private NodeValue? HashScalarLeaf(string declaredTypeName, NodeValue leaf) =>
        declaredTypeName == "password" ? HashPlaintext(leaf) : leaf;

    // Hash a plaintext leaf into its self-describing PBKDF2 string (AuthCrypto), or null when empty (= unset,
    // write nothing). The leaf is text-shaped (BaseType.Password maps to Text), so a non-text/empty leaf is
    // treated as "no value".
    private static NodeValue? HashPlaintext(NodeValue leaf) =>
        leaf is TextValue { Text: var t } && t.Length > 0 ? new TextValue(Code.AuthCrypto.Hash(t)) : null;

    // ── message dispatch ──────────────────────────────────────────────────────

    public string ProcessMessage(string json)
    {
        int? id = null;
        var op = "?";
        try
        {
            var req = JsonSerializer.Deserialize<WsRequest>(json, _jsonOpts)
                ?? throw new InvalidOperationException("Empty message.");

            id = req.Id;
            op = req.Op ?? "";
            var pathStr = req.Path ?? "/";
            var path = ParsePath(pathStr);

            // Ambient "who is writing" for the append-only changeset log (M13 slice 1 — StoreWriteContext):
            // the bound principal (null for an unauthenticated/DORMANT session) + this request's own
            // correlation id, so any store write this dispatch causes is attributed without threading a
            // who/msgId parameter through every IInstanceStore method. Scoped to exactly this dispatch
            // (disposed in the `finally` below) so it can never leak into an unrelated request reusing the
            // same thread-pool thread.
            using var _ = StoreWriteContext.Scope(Session(req)?.PrincipalUserId, req.Id);

            var result = op switch
            {
                "hello"             => HandleHello(req),
                "commit"            => HandleCommit(req),
                "refetch"           => HandleRefetch(pathStr, req),
                "hostAction"        => HandleHostAction(req),
                "ackRemap"          => HandleAckRemap(req),
                "parseExprs"        => HandleParseExprs(req),
                "login"             => HandleLogin(req),
                "logout"            => HandleLogout(req),
                _                   => Error($"Unknown op '{op}'")
            };
            return WithId(result, id);
        }
        catch (Exception ex)
        {
            // The reply carries the reason to the client (reject → rollback); the log
            // is the server-side record of it.
            Console.Error.WriteLine($"WS '{op}' failed: {ex.Message}");
            return WithId(Error(ex.Message), id);
        }
    }

    private string WithId(string json, int? id)
    {
        if (id is null) return json;
        var node = JsonNode.Parse(json)!;
        node["id"] = id.Value;
        return node.ToJsonString(_jsonOpts);
    }

    // ── write ─────────────────────────────────────────────────────────────────



    // ── set members + references (object model) ─────────────────────────────────

    // The intrinsic id of the object AT `objPath` — resolved purely from the schema/store surface the
    // handler already has, with NO store-interface change (the addEntry mint+link batching fix, below):
    // the Db root is always extent id 1 (DbBridge.RootId, M5's settled convention — every Db is minted at
    // that id, seeded or not), a SET MEMBER's own path segment IS its extent id (a set keys members by id
    // — mirrors SetMemberOwnerId, above), and anything else is a plain single-reference field, resolved by
    // reading the (recursively resolved) parent's fields for a ReferenceValue — the exact shape ReadById /
    // BuildObject already expose for a reference prop. Covers every shape a routable page's NodePath can
    // take (root-owned, set-member-owned, or reached through a chain of single-ref pages), so a "set of
    // set" nested arbitrarily deep still resolves. Null only for a shape none of these cover (e.g. a set
    // nested on an object-valued DICTIONARY entry, whose owner id isn't recoverable from a dict KEY) — no
    // addEntry call site produces that path today; the caller below falls back to the old two-call path
    // rather than mis-link.
    private int? OwnerIdAt(NodePath objPath)
    {
        if (objPath.IsRoot) return Code.DbBridge.RootId;
        var parent = NodePath.FromSegments(objPath.Segments.Take(objPath.Segments.Count - 1));
        var lastSeg = objPath.Segments[^1];
        if (_resolver.ResolveType(parent) is { Cardinality: Cardinality.Set })
        {
            // A set member's own path segment IS its extent id — but ONLY when it genuinely names a member
            // of THIS set. The store walk this decomposition replaces (WalkToObject, reached via AddToSet's
            // EnsureSet) reads StoredSet.Members and REJECTS an id that is shaped like a member but isn't
            // actually linked — the decomposition must never be MORE PERMISSIVE than the resolution it
            // replaces, or a crafted `/items/<real-id-not-in-items>/…` path would succeed where the old
            // two-call path threw. Read the set through the existing interface (ReadNode, same as every
            // other read here — no store-interface change) and check membership before trusting the segment.
            // A mismatch resolves to null: the caller's fallback then runs the OLD two-call path, which
            // throws exactly as before (nothing linked) — this branch commits to the set-member reading
            // once the parent is known to be a Set, rather than falling through to the reference-chain
            // branch below (which cannot possibly match a Set parent's shape anyway).
            return int.TryParse(lastSeg, out var memberId)
                && _store.ReadNode(parent) is SetValue setVal && setVal.Members.ContainsKey(memberId)
                ? memberId
                : null;
        }
        if (OwnerIdAt(parent) is not { } parentId || _store.ReadById(parentId) is not { } parentHit) return null;
        return parentHit.Fields.Fields.GetValueOrDefault(lastSeg) is ReferenceValue { TargetId: { } tid } ? tid : null;
    }


    // ── code-owned UI mutations (the Code runtime, identity-addressed) ──────────


    // Atomic batch commit of a whole changeset staged in a ctx.commit() — field EDITS (Step A) + new objects
    // (CREATES) and the RELATIONS linking them (Step B). Payload: { edits: [ { objectId, prop, value } ],
    // creates: [ { tempId, props } ], relations: [ { kind, … } ] }. VALIDATES EVERYTHING FIRST (schema +
    // access floor + password hash) without touching the store; if ALL pass, applies the whole changeset in
    // ONE store batch (mint + link + write, ONE Save()) and replies { ok, idMap }; if ANY fails, applies
    // NOTHING and replies { error } — so a partial graph is impossible (no orphan object, no half-linked set).
    //
    // FLAT remap invariant: mint-all-then-link is correct because a create's draft fields are SCALARS only
    // (object links are the separate `relations`), so creates have no inter-create field deps — every create
    // gets a real id, then every relation/edit referencing a tempId is remapped. A reference to a tempId NOT
    // in the batch fails the whole commit (the store's ResolveRefId throws); a negId never leaks back as a real id.
    private string HandleCommit(WsRequest req)
    {
        if (req.Edits is not { ValueKind: JsonValueKind.Array } editsEl)
            return Error("commit requires an 'edits' array.");

        var session = Session(req);
        var floor = Floor(req);

        // ── relations: index each by the create tempId it introduces, so a create can be TYPED from its
        //    link (a set's element type / a ref prop's declared target type) — the wire asserts no type. A
        //    relation also drives a write-floor decision (link a member = create; set a ref = edit the owner).
        var relations = new List<ParsedRelation>();
        var createTypeByTempId = new Dictionary<int, string>();
        if (req.Relations is { ValueKind: JsonValueKind.Array } relsEl)
            foreach (var relEl in relsEl.EnumerateArray())
            {
                // T2.2: the (owner,prop) set-link + set-unlink kinds are parsed in the SECOND pass below
                // (kept out of the ParsedRelation union on purpose — unlinks have no typed child). Skip them
                // here so ParseRelation (which only knows "set"/"ref") does not reject them as malformed.
                if (relEl.TryGetProperty("kind", out var skipKindEl) && skipKindEl.GetString() is
                    "setByProp" or "setUnlink" or "setUnlinkByProp" or "dictRemove" or "dictAdd")
                    continue;
                if (ParseRelation(relEl, session) is not { } rel)
                    return Error("commit relation is malformed.");
                relations.Add(rel);
                // A relation whose child is a transient create supplies that create's declared TYPE. A create
                // has EXACTLY ONE join (the interpreter emits one relation per draft), so a tempId appearing
                // as the child of MORE THAN ONE relation is a forged/malformed message — REJECT the whole
                // commit. Without this, two relations for one tempId would make this map last-write-wins: the
                // create would be minted + floor-checked as the LAST relation's type but linked by the FIRST
                // (e.g. minted as type X yet linked into a set declared for type Y), so the create-floor
                // decision would be made against the WRONG type — a floor-widening hole. One-join-per-create
                // closes it (the link then always matches the create-floor's type).
                if (rel.ChildRef < 0 && rel.ChildType is { } ct)
                {
                    if (!createTypeByTempId.TryAdd(rel.ChildRef, ct))
                        return Error($"Commit create {rel.ChildRef} has more than one relation.");
                }
            }

        // ── second pass: the T2.2 (owner,prop) set-link + set-unlink relations. Kept OUT of the
        //    ParsedRelation union on purpose (D2): unlinks have no typed child, and the (owner,prop) link is
        //    a fresh shape. We parse them here, register a transient child's TYPE so a just-created member is
        //    typed from its link (mirroring the set/ref child-type wiring above, before the creates loop below
        //    consumes createTypeByTempId), and emit the matching CommitMutation — every id resolved through the
        //    session like the set/ref ops, so the store can remap temp ids. Built into `extraMutations` and
        //    merged into `mutations` once that list is declared.
        var extraMutations = new List<CommitMutation>();
        if (req.Relations is { ValueKind: JsonValueKind.Array } relsEl2)
            foreach (var relEl in relsEl2.EnumerateArray())
            {
                var tpKind = relEl.TryGetProperty("kind", out var tpKindEl) ? tpKindEl.GetString() : null;

                // T3: `dictRemove` (drop ONE dict entry) carries NO `childId` — parse + emit it here and
                // continue. It is a commit-internal mutation routed through CommitBatch, never a live op,
                // and mirrors `dict.Remove(k)` (a targeted unlink, not a bulk detach).
                if (tpKind == "dictRemove")
                {
                    if ((relEl.TryGetProperty("prop", out var drPropEl) ? drPropEl.GetString() : null) is not { } drProp)
                        return Error("commit relation is malformed.");
                    if (!relEl.TryGetProperty("owner", out var drOwnerEl) || drOwnerEl.ValueKind != JsonValueKind.Number)
                        return Error("commit relation is malformed.");
                    if ((relEl.TryGetProperty("key", out var drKeyEl) ? drKeyEl.GetString() : null) is not { } drKeyStr
                        || drKeyStr.Length == 0)
                        return Error("commit relation is malformed.");
                    var drOwnerRaw = drOwnerEl.GetInt32();
                    var drOwnerRef = Resolve(session, drOwnerRaw);
                    // The dict key is a NodeValue, parsed against the owner prop's declared keyType — mirrors
                    // HandleAddEntry's `ParseKey(keyStr, KeyTypeName ?? "text")`. Owner's type is unknown for a
                    // fresh (negative) owner; default to text (the store's apply arm re-keys under its schema).
                    var drKeyType = drOwnerRef >= 0 && _store.ReadById(drOwnerRef) is { } drOwner
                        ? _desc.FindType(drOwner.TypeName)?.Props?.FirstOrDefault(p => p.Name == drProp)?.KeyType ?? "text"
                        : "text";
                    // Access floor: a dict edit is an `edit` of the owner (mirrors RequireDictWrite's edit floor).
                    if (drOwnerRef >= 0 && _store.ReadById(drOwnerRef) is { } drOwnerObj)
                        RequireWrite(floor, "edit", drOwnerObj.TypeName,
                            Code.AccessFloor.ScalarObject(drOwnerObj.TypeName, drOwnerRef, drOwnerObj.Fields, _desc));
                    extraMutations.Add(new DictRemoveMutation(drOwnerRef, drProp, ParseKey(drKeyStr, drKeyType)));
                    continue;
                }

                if (tpKind == "dictAdd")
                {
                    // T6a.1: `dictAdd` (write ONE dict entry) — the wire-accepted counterpart of DictWriteMutation
                    // (formerly server-only). Scalar value only (object entries keep the standalone
                    // WriteDictionaryEntry path). Mirrors dictRemove's parse + the edit floor of HandleWrite's
                    // dict-entry gate. owner/prop/key address the entry; value is a scalar leaf { type, value }.
                    if ((relEl.TryGetProperty("prop", out var dPropEl) ? dPropEl.GetString() : null) is not { } dProp)
                        return Error("commit relation is malformed.");
                    if (!relEl.TryGetProperty("owner", out var dOwnerEl) || dOwnerEl.ValueKind != JsonValueKind.Number)
                        return Error("commit relation is malformed.");
                    if ((relEl.TryGetProperty("key", out var dKeyEl) ? dKeyEl.GetString() : null) is not { } dKeyStr
                        || dKeyStr.Length == 0)
                        return Error("commit relation is malformed.");
                    if (!relEl.TryGetProperty("value", out var dValEl))
                        return Error("commit relation is malformed.");
                    var dOwnerRaw = dOwnerEl.GetInt32();
                    var dOwnerRef = Resolve(session, dOwnerRaw);
                    // The dict key is a NodeValue, parsed against the owner prop's declared keyType — mirrors
                    // HandleAddEntry's `ParseKey(keyStr, KeyTypeName ?? "text")`. Owner's type is unknown for a
                    // fresh (negative) owner; default to text (the store's apply arm re-keys under its schema).
                    var dKeyType = dOwnerRef >= 0 && _store.ReadById(dOwnerRef) is { } dOwner
                        ? _desc.FindType(dOwner.TypeName)?.Props?.FirstOrDefault(p => p.Name == dProp)?.KeyType ?? "text"
                        : "text";
                    // The entry's VALUE must fit the dict prop's element type. Scalar entries use the scalar
                    // base type; object entries (dict of Config) are now a commit case too — T6b-4a extends
                    // DictWriteMutation's apply arm to mint a StoredObject (mirrors WriteDictionaryEntryInto).
                    var dPropDef = dOwnerRef >= 0 && _store.ReadById(dOwnerRef) is { } dOwnerVal
                        ? _desc.FindType(dOwnerVal.TypeName)?.Props?.FirstOrDefault(p => p.Name == dProp)
                        : null;
                    if (dPropDef is null || dPropDef.Cardinality != Cardinality.Dictionary)
                        return Error($"Field '{dProp}' on the owner is not a dictionary field.");
                    NodeValue dValue;
                    if (_desc.ScalarBaseOf(dPropDef.Type) is { } dBaseType)
                    {
                        dValue = LeafForType(dValEl, dBaseType);
                        if (dValue is TextValue tv && !_desc.EnumAccepts(dPropDef.Type, tv.Text))
                            return Error($"'{tv.Text}' is not a value of enum '{dPropDef.Type}'.");
                        // The WRITE chokepoint (M-auth `password`): a `dict of password` entry hashes the
                        // plaintext before the store — never routes plaintext around the WS hash (mirrors HandleWrite).
                        if (dPropDef.Type == "password" && HashScalarLeaf(dPropDef.Type, dValue) is { } hashed)
                            dValue = hashed;
                    }
                    else if (_desc.FindType(dPropDef.Type) is { BaseType: BaseType.Object } dElemType)
                    {
                        // Object dictionary entry (dict of Config). Consume the value in the SAME { props: {...} }
                        // shape a commit create ships (ExecObjectValue, allowSets:true — nested sets skipped, linked
                        // by a separate set relation), mirroring HandleAddSetMember / HandleArrayAdd.
                        dValue = ExecObjectValue(dValEl, dElemType, allowSets: true);
                    }
                    else
                    {
                        return Error($"Field '{dProp}' on the owner is not a supported dictionary field.");
                    }
                    // Access floor: a dict write is an `edit` of the owner (mirrors RequireDictWrite's edit floor
                    // and the dictRemove branch above).
                    if (dOwnerRef >= 0 && _store.ReadById(dOwnerRef) is { } dOwnerObj)
                        RequireWrite(floor, "edit", dOwnerObj.TypeName,
                            Code.AccessFloor.ScalarObject(dOwnerObj.TypeName, dOwnerRef, dOwnerObj.Fields, _desc));
                    extraMutations.Add(new DictWriteMutation(dOwnerRef, dProp, ParseKey(dKeyStr, dKeyType), dValue));
                    continue;
                }

                if (tpKind is not ("setByProp" or "setUnlink" or "setUnlinkByProp")) continue;

                if (!relEl.TryGetProperty("childId", out var childEl) || childEl.ValueKind != JsonValueKind.Number)
                    return Error("commit relation is malformed.");
                var childRef = Resolve(session, childEl.GetInt32());

                switch (relEl.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null)
                {
                    case "setByProp":
                    {
                        if ((relEl.TryGetProperty("prop", out var propEl) ? propEl.GetString() : null) is not { } prop)
                            return Error("commit relation is malformed.");
                        if (!relEl.TryGetProperty("parentId", out var pEl) || pEl.ValueKind != JsonValueKind.Number)
                            return Error("commit relation is malformed.");
                        var ownerRaw = pEl.GetInt32();
                        var ownerRef = Resolve(session, ownerRaw);

                        // A transient (negative) child is typed from the owner's SET prop — the wire asserts no
                        // type a client could forge. An owner whose type isn't yet known (a tempId not yet seen
                        // by the first pass) is left untyped here; SetLinkByPropMutation's own pre-validation in
                        // the store covers that case (errors before applying). One-join-per-create still holds:
                        // two relations naming the same transient child fail the whole commit.
                        if (childRef < 0)
                        {
                            var ownerType = ownerRef < 0
                                ? (createTypeByTempId.TryGetValue(ownerRef, out var t) ? t : null)
                                : _store.ReadById(ownerRef)?.TypeName;
                            if (ownerType is { } ot
                                && _desc.FindType(ot)?.Props?.FirstOrDefault(p => p.Name == prop)
                                    is { Cardinality: Cardinality.Set } setProp)
                            {
                                if (!createTypeByTempId.TryAdd(childRef, setProp.Type))
                                    return Error($"Commit create {childRef} has more than one relation.");
                            }
                        }
                        // Access floor (mirrors the first-pass set/ref link): the OWNER is edited (its set
                        // prop changes) and a real (positive-id) MEMBER being linked is a `create` (forgery +
                        // denial — you must be able to read the object you attach). A transient (<0) member
                        // already passed the create gate in the createBatch path above.
                        if (_store.ReadById(ownerRef) is { } setByPropOwner)
                            RequireWrite(floor, "edit", setByPropOwner.TypeName,
                                Code.AccessFloor.ScalarObject(setByPropOwner.TypeName, ownerRef, setByPropOwner.Fields, _desc));
                        if (childRef >= 0 && _store.ReadById(childRef) is { } setByPropLinked)
                            RequireWrite(floor, "create", setByPropLinked.TypeName,
                                Code.AccessFloor.ScalarObject(setByPropLinked.TypeName, childRef, setByPropLinked.Fields, _desc));
                        extraMutations.Add(new SetLinkByPropMutation(ownerRef, prop, childRef));
                        break;
                    }
                    case "setUnlink":
                    {
                        if (!relEl.TryGetProperty("setId", out var setEl) || setEl.ValueKind != JsonValueKind.Number)
                            return Error("commit relation is malformed.");
                        var setId = Resolve(session, setEl.GetInt32());
                        // Access floor: removing a member from a set is a `delete` of THAT member, exactly as the
                        // former `arrayRemove` live op — you must be able to delete the object you detach (forgery
                        // + denial). No owner handle here — the member's delete floor alone is the guard (mirrors
                        // HandleArrayRemove's RequireWrite "delete" on the member, not an owner edit gate).
                        if (childRef >= 0 && _store.ReadById(childRef) is { } setUnlinked)
                            RequireWrite(floor, "delete", setUnlinked.TypeName,
                                Code.AccessFloor.ScalarObject(setUnlinked.TypeName, childRef, setUnlinked.Fields, _desc));
                        extraMutations.Add(new SetUnlinkMutation(setId, childRef));
                        break;
                    }
                    case "setUnlinkByProp":
                    {
                        if ((relEl.TryGetProperty("prop", out var propEl) ? propEl.GetString() : null) is not { } prop)
                            return Error("commit relation is malformed.");
                        if (!relEl.TryGetProperty("parentId", out var pEl) || pEl.ValueKind != JsonValueKind.Number)
                            return Error("commit relation is malformed.");
                        var ownerRaw = pEl.GetInt32();
                        var ownerRef = Resolve(session, ownerRaw);
                        // Access floor: you must be able to read the object you detach (forgery + denial), and
                        // the OWNER is edited (its set prop changes) — mirrors the setByProp link side.
                        if (_store.ReadById(ownerRef) is { } setUnlinkByPropOwner)
                            RequireWrite(floor, "edit", setUnlinkByPropOwner.TypeName,
                                Code.AccessFloor.ScalarObject(setUnlinkByPropOwner.TypeName, ownerRef, setUnlinkByPropOwner.Fields, _desc));
                        if (childRef >= 0 && _store.ReadById(childRef) is { } setUnlinked)
                            RequireWrite(floor, "create", setUnlinked.TypeName,
                                Code.AccessFloor.ScalarObject(setUnlinked.TypeName, childRef, setUnlinked.Fields, _desc));
                        extraMutations.Add(new SetUnlinkByPropMutation(ownerRef, prop, childRef));
                        break;
                    }
                }
            }

        // ── creates: validate each against its relation-derived type — built EXACTLY as HandleAddSetMember
        //    (ScalarObject(type, 0, fields) candidate + the password-hash chokepoint + RequireWrite "create").
        var createBatch = new List<CommitCreate>();
        if (req.Creates is { ValueKind: JsonValueKind.Array } createsEl)
            foreach (var createEl in createsEl.EnumerateArray())
            {
                if (!createEl.TryGetProperty("tempId", out var tEl) || tEl.ValueKind != JsonValueKind.Number)
                    return Error("commit create missing numeric 'tempId'.");
                var tempId = tEl.GetInt32();
                if (!createTypeByTempId.TryGetValue(tempId, out var typeName))
                    return Error($"Commit create {tempId} has no relation to type it."); // an orphan create
                if (_desc.FindType(typeName) is not { } typeDef)
                    return Error($"Unknown type '{typeName}'.");

                if (!createEl.TryGetProperty("value", out var valueEl))
                    return Error("commit create missing 'value'.");
                // The draft's scalar fields in the SAME tagged { props: { name: leaf } } shape an arrayAdd
                // ships, validated against the declared type exactly like HandleAddSetMember (ExecObjectValue).
                // allowSets:true skips nested collection fields (e.g. an Order's `lines` set) — those are linked
                // by the create's `set`/`setByProp` relation, never shipped inline (mirrors HandleAddSetMember).
                var fields = ExecObjectValue(valueEl, typeDef, allowSets: true);

                // The write floor: a create of the element/target type, decided over the NEW object's scalar
                // fields (id 0 — no identity yet), exactly as HandleAddSetMember / HandleArrayAdd.
                RequireWrite(floor, "create", typeName, Code.AccessFloor.ScalarObject(typeName, 0, fields, _desc));
                // The WRITE chokepoint (M-auth `password`): hash any password-typed plaintext BEFORE the store —
                // a staged `User` create can never skip hashing (a SECURITY must), same as every other create path.
                createBatch.Add(new CommitCreate(tempId, typeName, HashPasswordFields(typeName, fields)));
            }

        // ── edits: validate each exactly as Step A / HandleObjectPropChange (an edit of an EXISTING object).
        var mutations = new List<CommitMutation>();
        mutations.AddRange(extraMutations); // the T2.2 (owner,prop) set-link / set-unlink ops
        foreach (var editEl in editsEl.EnumerateArray())
        {
            if (!editEl.TryGetProperty("objectId", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                return Error("commit edit missing numeric 'objectId'.");
            var objectId = Resolve(session, idEl.GetInt32());

            if (!editEl.TryGetProperty("prop", out var propEl) || propEl.ValueKind != JsonValueKind.String)
                return Error("commit edit missing 'prop'.");
            var prop = propEl.GetString()!;

            if (!editEl.TryGetProperty("value", out var valEl))
                return Error("commit edit missing 'value'.");

            if (_store.ReadById(objectId) is not { } hit)
                return Error($"No object with id {objectId}.");
            var propDef = _desc.FindType(hit.TypeName)?.Props?.FirstOrDefault(p => p.Name == prop);
            if (propDef is null)
                return Error($"Type '{hit.TypeName}' has no field '{prop}'.");
            if (propDef.Cardinality != Cardinality.Single || _desc.ScalarBaseOf(propDef.Type) is not { } baseType)
                return Error($"Field '{prop}' on '{hit.TypeName}' is not a scalar field.");

            var leaf = LeafForType(valEl, baseType);
            if (leaf is TextValue tv && !_desc.EnumAccepts(propDef.Type, tv.Text))
                return Error($"'{tv.Text}' is not a value of enum '{propDef.Type}'.");

            // Write floor: same decision as HandleObjectPropChange — an edit of the existing object.
            RequireWrite(floor, "edit", hit.TypeName, Code.AccessFloor.ScalarObject(hit.TypeName, objectId, hit.Fields, _desc));

            // Password hash chokepoint: same as HandleObjectPropChange — a blank password is no change.
            if (HashLeaf(hit.TypeName, prop, leaf) is { } hashed)
                mutations.Add(new FieldWriteMutation(objectId, prop, hashed));
        }

        // ── relation write-floor + the link mutations. A set link = a `create` of the member type (whether
        //    minting it here, or linking an existing object); a ref link = an `edit` of the OWNER (and a clear
        //    is just an owner edit). A relation to an EXISTING (positive-id) child is decided over its current
        //    fields, exactly as HandleAddSetMember's link-existing path.
        foreach (var rel in relations)
            switch (rel)
            {
                case ParsedSetRelation(var setId, var childRef, var childType):
                {
                    if (childRef >= 0 && _store.ReadById(childRef) is { } linked)
                        RequireWrite(floor, "create", linked.TypeName,
                            Code.AccessFloor.ScalarObject(linked.TypeName, childRef, linked.Fields, _desc));
                    // a transient (childRef<0) member was already create-gated above (createBatch); childType
                    // is its element type, used only to type the link in the store.
                    _ = childType;
                    mutations.Add(new SetLinkMutation(setId, childRef));
                    break;
                }
                case ParsedRefRelation(var parentId, var prop, var childRef, var childType):
                {
                    if (_store.ReadById(parentId) is not { } owner)
                        return Error($"No object with id {parentId}.");
                    RequireWrite(floor, "edit", owner.TypeName,
                        Code.AccessFloor.ScalarObject(owner.TypeName, parentId, owner.Fields, _desc));
                    if (childRef >= 0 && _store.ReadById(childRef) is { } linkedRef)
                        RequireWrite(floor, "create", linkedRef.TypeName,
                            Code.AccessFloor.ScalarObject(linkedRef.TypeName, childRef, linkedRef.Fields, _desc));
                    mutations.Add(new RefLinkMutation(parentId, prop, childRef, childType));
                    break;
                }
            }

        // ── apply: everything validated — mint + link + write the whole changeset in one lock + one Save().
        // baseVersion rides through to the store's ONE critical section (check + apply, never two calls —
        // see CommitBatch's doc). A same-field COLLISION throws ConflictException (M13 slice 6): caught HERE
        // (not by ProcessMessage's generic catch) so the reply carries the structured `conflicts` payload
        // ALONGSIDE the plain `error` — the generic form renders the coarse banner, a custom `fn render()`
        // that ignores conflicts still shows the global error banner (no silent clobber). A DISJOINT stale
        // base does NOT throw — it auto-merges (applies). Any OTHER reject (malformed batch, floor denial)
        // propagates to ProcessMessage's `{ error }` catch, unchanged. CommitBatch is called even for an
        // EMPTY batch, so `result.Version` (captured under the store's lock) is ALWAYS the reply's newVersion
        // (finding 3); the client re-pins its ctx base to a version its own commit actually produced.
        CommitResult result;
        try
        {
            result = _store.CommitBatch(createBatch, mutations, req.BaseVersion);
        }
        catch (ConflictException ex)
        {
            // newVersion = the store's CURRENT version (the conflict left it untouched, so this is the head an
            // interleaved commit reached) — the client re-pins the committing ctx's base to it, so a "Keep
            // mine" re-commit forces at a now-fresh base and the guard then passes it.
            var wire = ex.Conflicts.Select(c => new ConflictFieldWire
            {
                Object = c.Object, TypeName = c.TypeName, Field = c.Field,
                Base = WireScalar(c.Base), Mine = WireScalar(c.Mine), Theirs = WireScalar(c.Theirs),
            }).ToList();
            return Serialize(new ConflictResponse
            {
                Error = ex.Message, Conflicts = wire, NewVersion = _store.CurrentVersion,
            });
        }

        var idMap = result.Creates.Select(r => new CommitIdMapEntry
        {
            TempId = r.TempId,
            RealId = r.RealId,
            Collections = r.Collections.ToDictionary(
                kv => kv.Key, kv => new CollectionInfo { Id = kv.Value.Id, ElementTypeName = kv.Value.ElementTypeName }),
        }).ToList();

        // Record every minted object's transient (negative) id → real id in the session so the client's
        // follow-up ops (a field edit, a remove) that still address a just-created object by its temp id
        // resolve to the real one — even if they arrive before the client has applied this reply's remap.
        // Mirrors HandleArrayAdd (L1475). Without it a post-commit added object addressed by temp id would
        // fail to resolve (the applyCommitRemap path on the client reconciles, but server-side resolution
        // for the next inbound op would not).
        foreach (var r in result.Creates)
            session?.MapTransientId(r.TempId, r.RealId);

        return Serialize(new CommitResponse { IdMap = idMap.Count > 0 ? idMap : null, NewVersion = result.Version });
    }

    // A scalar NodeValue as a BARE wire value for the conflict payload (M13 slice 6) — the JSON-native form
    // the client displays (a text as a string, an int/decimal as a number, a bool as a bool, a date/datetime
    // as its ISO string, a ref as its target id). Null (an absent field) stays null. Mirrors the bare shape
    // DeserializeLeaf reads on the way IN; only scalars reach here (a wire commit conflicts on scalar fields
    // and single refs, never a set/dict value).
    private static object? WireScalar(NodeValue? v) => v switch
    {
        BoolValue b => b.Value,
        IntValue i => i.Value,
        DecimalValue d => d.Value,
        TextValue t => t.Text,
        DateValue d => d.Value.ToString("yyyy-MM-dd"),
        DateTimeValue dt => dt.Value.ToString("O"),
        _ => null,
    };

    // Parse a commit relation (atomic-commit Step B). A set relation `{ kind:"set", setId, childId }` links a
    // member into a set; a ref relation `{ kind:"ref", parentId, prop, childId }` points a single reference at
    // a target. childId may be a transient create's NEGATIVE id (resolved to its real id in the store batch) or
    // an existing positive id (resolved through the session's transient-id remap, like every other addressed
    // id). ChildType — the create's declared TYPE — is derived from the link itself (a set's element type, a
    // ref prop's declared target type), so the WIRE never asserts a type a client could forge. Null if malformed.
    private ParsedRelation? ParseRelation(JsonElement el, ClientSession? session)
    {
        if ((el.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null) is not { } kind) return null;
        if (!el.TryGetProperty("childId", out var childEl) || childEl.ValueKind != JsonValueKind.Number) return null;
        var childRef = Resolve(session, childEl.GetInt32()); // a tempId stays (unknown → unchanged); a real id remaps

        if (kind == "set")
        {
            if (!el.TryGetProperty("setId", out var setEl) || setEl.ValueKind != JsonValueKind.Number) return null;
            var setId = Resolve(session, setEl.GetInt32());
            var elementType = _store.SetElementType(setId);
            if (elementType is null) return null; // no such set
            return new ParsedSetRelation(setId, childRef, elementType);
        }
        if (kind == "ref")
        {
            if (!el.TryGetProperty("parentId", out var pEl) || pEl.ValueKind != JsonValueKind.Number) return null;
            if ((el.TryGetProperty("prop", out var propEl) ? propEl.GetString() : null) is not { } prop) return null;
            var parentId = Resolve(session, pEl.GetInt32());
            if (_store.ReadById(parentId) is not { } owner) return null;
            var propDef = _desc.FindType(owner.TypeName)?.Props?.FirstOrDefault(p => p.Name == prop);
            if (propDef is null || propDef.Cardinality != Cardinality.Single || !_desc.IsObjectType(propDef.Type))
                return null; // not a single reference
            return new ParsedRefRelation(parentId, prop, childRef, propDef.Type);
        }
        return null;
    }

    // Set/clear a single object REFERENCE prop on the object with this intrinsic id —

    // The WS's first message on open: claims the session minted at SSR (keeping it past
    // the claim window). The session carries no data — a refetch re-renders from a fresh
    // store load — so the report is informational; `sessionAlive: false` just means the
    // hello arrived past the window.
    private string HandleHello(WsRequest req)
    {
        var alive = Session(req) != null;
        return Serialize(new HelloResponse { SessionAlive = alive });
    }

    // ── parseExprs (M12 auto-live parse-op) ──────────────────────────────────────
    //
    // On-demand parse round-trip that involves NO refetch and NO store access, so it can never race the
    // canvas tree editor's optimistic mutations by construction (unlike S3a's removed auto-live attempt).
    // The canvas walk (codeExec.ts) collects expression texts it can't find in its shipped `sys.evalContext`
    // exprs map (a NEWLY EDITED, not-yet-refreshed leaf/attr/for-collection/if-condition/var-init source) and
    // batches them here; a valid text's AST rides back so the client can evaluate it live, merged locally
    // into the SAME evalContext object the walk already holds (never re-keying its memo, never touching
    // needsServerData). Pure + store-free (CodeParse.ParseExpression takes no store/floor/session), so this
    // handler needs none of them either — every session (even anonymous, even one with no bound principal)
    // may parse expression text; nothing here reads or writes app data.
    //
    // Caps are DEFENSIVE BOUNDS against a pathological single request (bulk-paste, a scripted/hostile
    // client) — a real single-operator edit never approaches 200 texts or 10k chars in one ~300ms debounce
    // batch (one edited leaf at a time). CORRECTED (a prior version of this comment claimed a truncated text
    // "simply re-requests on its next debounced pass" — FALSE): the client (ws.ts applyParseExprsResult)
    // treats any text OMITTED from the reply — truncated exactly like genuinely unparseable — identically:
    // it joins the per-ctx `failed` set and is NOT re-asked again this generation. The only recovery is the
    // SAME one an unparseable text already has: an explicit "Refresh values" mints a brand-new evalContext
    // object (a fresh memo key), which is a fresh WeakMap entry with an empty `failed` set. This is honest,
    // not a gap: hitting the cap at all is already the pathological case the bound exists for. Truncation is
    // logged so an operator can see it happened; never surfaced as a client-visible error (a partial batch is
    // a normal, useful reply, not a failure).
    private const int ParseExprsMaxTexts = 200;
    private const int ParseExprsMaxChars = 10_000;
    // A per-TEXT length cap (security posture, not a UX bound): this op is reachable ANONYMOUSLY on ANY
    // instance's WS, including a PUBLIC one (e.g. devlog.deenv.org) — no session/floor gate (see the doc
    // above, "pure + store-free"). CodeParse.ParseExpression is a recursive-descent parser with no depth
    // limit of its own; a single pathological deeply-nested expression (thousands of chars of nested
    // parens/ternaries/calls) drives that recursion toward an UNCATCHABLE StackOverflowException — the exact
    // process-death class FG (the interpreter call-depth guard) already closed for EVALUATION. No real
    // designer expression comes remotely close to this; an oversize text is skipped exactly like an
    // unparseable one (omitted from the reply, warn-logged) — never attempted.
    private const int ParseExprsMaxTextChars = 1_000;

    private string HandleParseExprs(WsRequest req)
    {
        if (req.Texts is not { Length: > 0 } texts)
            return Error("parseExprs requires a non-empty 'texts' array.");

        var entries = new Dictionary<string, string>();
        var totalChars = 0;
        var seen = new HashSet<string>();
        foreach (var text in texts)
        {
            if (text is null || !seen.Add(text)) continue; // dedup within one request
            if (seen.Count > ParseExprsMaxTexts)
            {
                Console.Error.WriteLine($"parseExprs: request truncated at {ParseExprsMaxTexts} texts.");
                break;
            }
            totalChars += text.Length;
            if (totalChars > ParseExprsMaxChars)
            {
                Console.Error.WriteLine($"parseExprs: request truncated at {ParseExprsMaxChars} total chars.");
                break;
            }
            // The security-posture cap: never hand a pathologically long text to ParseExpression's recursive
            // descent (see ParseExprsMaxTextChars's doc — an uncatchable StackOverflowException, not just a
            // slow parse). Skipped like an unparseable text (omitted, warn-logged) — the REST of this batch
            // still gets a fair try (continue, not break: one oversize text must not starve its siblings).
            if (text.Length > ParseExprsMaxTextChars)
            {
                Console.Error.WriteLine(
                    $"parseExprs: text of length {text.Length} exceeds the per-text cap ({ParseExprsMaxTextChars} chars) — skipped, never parsed.");
                continue;
            }
            try
            {
                Code.ICodeValue ast = Code.CodeParse.ParseExpression(text);
                entries[text] = JsonSerializer.Serialize(ast, SchemaJson.Options);
            }
            catch { /* unparseable → omitted (the client keeps its chip, honest) */ }
        }

        return Serialize(new ParseExprsResponse { Entries = entries });
    }

    // Re-render the code UI and return authoritative client state. Called when a mutation
    // leaves a cache entry the client cannot recompute locally (a hidden dependency). The
    // render runs over a FRESH load from the store — the single source of truth — so it
    // reflects every committed change, not a per-client mirror that could have diverged.
    private string HandleRefetch(string pathStr, WsRequest req)
    {
        // The client sends location.pathname (the FULL URL, carrying the mount prefix); strip the
        // mount so the re-render's `path` var is root-relative, exactly like the SSR first paint. A
        // path that does not carry the mount (the X-Forwarded-Prefix="" domain-root case) is left
        // unchanged. Identity when root-mounted.
        pathStr = SsrRenderer.StripBase(_mountBase, pathStr);

        var session = Session(req); // slide liveness + recover the bound principal (M-auth)

        // The refetch re-renders AS the logged-in principal: the same access read floor gates the graph
        // load so a denied object stays out of the refetched state, and the principal threads into
        // RenderState so `currentUser`-dependent UI re-renders bound (without this, a post-login refetch
        // would render anonymous). Null (anonymous) ⇒ a dormant/no-rules app is unaffected.
        var principalUserId = session?.PrincipalUserId;
        var floor = Floor(req);

        // Load the graph once from the store; object-valued vars (the client's selection)
        // resolve to the same instances the render uses, so selection-dependent data the
        // first paint never shipped gets computed.
        var db = Code.DbBridge.LoadRoot(_store, _desc, new Code.ExecContext(), floor);
        var byId = IndexObjects(db);

        var sessionVars = new Dictionary<string, Code.IExecValue>();
        if (req.Vars is { ValueKind: JsonValueKind.Object } vars)
            foreach (var v in vars.EnumerateObject())
                if (SessionVarFromWire(v.Value, byId) is { } value)
                    sessionVars[v.Name] = value;

        // Transients mint below the client's id floor (no collisions with its local drafts).
        var lastId = req.LastId ?? 0;

        // The client's live per-component view-state (client data layer, slice 1b): rebuild { slotKey →
        // (varName → ExecValue) } from the wire — scalar by value; in-store (positive-id) object ref against
        // the loaded `byId` graph; a TRANSIENT object (the common `var state = { … }` shape) by value, rebuilt
        // as a throwaway transient minted below the client's id floor. Threaded as the `seed` RenderState
        // applies, so the server reproduces the client's EXACT render (the toggled-open popup) and structural
        // privacy harvests + ships the data that state demands.
        var seed = SlotStateFromWire(req.SlotState, byId, ref lastId);

        // The action-miss intent (client data layer, slice 4): when this refetch is driven by a click handler
        // that read un-shipped data, the client sends the handler's (slot, fn-id). Combine them into the key
        // RenderState uses to locate that handler in the reproduced render and invoke it READ-ONLY, harvesting
        // the data it reads. Absent → a normal render-miss refetch (no handler invoke). Built via the SAME
        // HandlerKey both twins derive, so the server's reproduced index matches the client's reported handler.
        var harvestAction = req.HandlerFn is { } fnId && req.HandlerSlot is { } slot
            ? Code.CodeExecutor.HandlerKey([.. slot.Split('/', StringSplitOptions.RemoveEmptyEntries)], fnId)
            : null;

        // The refetch renderer gets the SAME live registry provider as the SSR path, so a refetch
        // re-render reflects the kernel's current instances — no stale `instances` list.
        var state = new SsrRenderer(_store, _desc, registry: _registry, publishPreview: _publishPreview)
            .RenderState(pathStr, sessionVars, db, lastId, principalUserId, seed, harvestAction);
        return Serialize(new RefetchResponse { State = state });
    }

    // A server-authoritative host action (the sys.publish channel): the server alone runs the
    // effect (the client staged nothing). Read the action name + raw evaluated args and run them
    // through the IHostActions seam; on success reply ok. NOT journaled and it does NOT touch the
    // optimistic IInstanceStore ops — a host action is a devops effect outside the data model. A
    // failure (unknown action, bad arg, invalid design, unknown target) throws and ProcessMessage's
    // catch returns it as `{ error }`, which the client surfaces as lastError (no journal replay).
    //
    // AUTHORITY IS THE APP'S `sys` ACCESS RULE, CHECKED HERE. Host actions run with KERNEL authority
    // (outside the per-type floor), so the gate is the access section's `sys` subject: the action is
    // dispatched ONLY when the session's principal satisfies the `sys` rule (AccessFloor.CanHostAction —
    // deny-by-default, evaluated with the SAME kernel-floor condition the type rules use). This holds
    // EVEN IF the app's data rules default open — kernel authority is never open by default, so no
    // access section / no `sys` rule / a false condition / an anonymous session all REJECT. It is
    // BELTS-AND-BRACES with the wiring gate (KernelHost.HostActionsFor hands NoHostActions to an
    // instance whose Code calls no host action): a wired instance still denies unless its `sys` rule
    // grants the caller, and an unwired one denies via the seam. Do NOT remove this check on the
    // assumption the seam is safe — the seam is the MECHANISM, this rule is the AUTHORITY.
    private string HandleHostAction(WsRequest req)
    {
        if (req.Action is not { } action)
            return Error("hostAction requires a string 'action'.");
        var args = req.Args ?? default;

        // The authority gate (M-auth `sys` subject): deny unless the app's `sys` access rule grants this
        // principal. Rejected → the `{ error }` reply (same path as the NoHostActions rejection).
        if (!Floor(req).CanHostAction())
            throw new InvalidOperationException(
                $"Access denied: host action '{action}' requires an authorized operator (a `sys` access rule).");

        var report = _hostActions.Run(action, args); // throws on failure → caught as { error }

        return Serialize(new HostActionResponse { Report = report });
    }

    // Index every persisted object in a loaded graph by its intrinsic id (for resolving
    // an object-valued session var to the instance the render will use).
    private static Dictionary<int, Code.ExecObject> IndexObjects(Code.ExecObject root)
    {
        var byId = new Dictionary<int, Code.ExecObject>();
        void Walk(Code.IExecValue value)
        {
            switch (value)
            {
                case Code.ExecObject o:
                    if (o.Id > 0 && byId.TryAdd(o.Id, o))
                        foreach (var p in o.Props.Values) Walk(p);
                    break;
                case Code.ExecArray a:
                    foreach (var item in a.Items) Walk(item.Value);
                    break;
            }
        }
        Walk(root);
        return byId;
    }

    // Rebuild the client's per-component view-state seed (client data layer, slice 1b): a map from each
    // component's render-slot key to its setup-scope locals as ExecValues. A null / empty payload (the common
    // case — no mounted stateful component shipped state) yields null, so the render is byte-identical to
    // today. The result feeds SsrRenderer.RenderState's `seed`, which ApplySeed overwrites the matching
    // component's setup locals with (whole-object, v1).
    // `transientFloor` is the client's id floor (req.LastId): a reconstructed transient mints BELOW it (a
    // descending local counter), so it never collides with an in-store (positive) id NOR with a draft the
    // client already holds. Updated by ref to the lowest id minted here, so the caller's subsequent render
    // counter (SsrRenderer.RenderState's context.LastId) starts BELOW it instead of from the same starting
    // number — two counters independently seeded from the same floor mint the identical sequence, so a
    // render-minted object (a schema descriptor) can land on the exact id a transient here already used,
    // and ClientState's id-keyed dedup then silently drops one of the two colliding objects.
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Code.IExecValue>>? SlotStateFromWire(
        JsonElement? slotState, Dictionary<int, Code.ExecObject> byId, ref int transientFloor)
    {
        if (slotState is not { ValueKind: JsonValueKind.Object } slots) return null;
        var nextTransient = Math.Min(0, transientFloor);
        var seed = new Dictionary<string, IReadOnlyDictionary<string, Code.IExecValue>>();
        foreach (var slot in slots.EnumerateObject())
        {
            if (slot.Value.ValueKind != JsonValueKind.Object) continue;
            var locals = new Dictionary<string, Code.IExecValue>();
            foreach (var local in slot.Value.EnumerateObject())
                if (SlotLocalFromWire(local.Value, byId, ref nextTransient) is { } value)
                    locals[local.Name] = value;
            if (locals.Count > 0) seed[slot.Name] = locals;
        }
        transientFloor = nextTransient;
        return seed.Count > 0 ? seed : null;
    }

    // A single setup-scope local from the slotState wire (client data layer, slice 1b). Scalars and an
    // in-store object REF resolve exactly like a session var (SessionVarFromWire). A TRANSIENT object ships
    // BY VALUE ({ type:"object", props:{…} }, no id) — the common component-state shape (`var state = { … }`):
    // reconstruct it as a throwaway ExecObject (a fresh negative id below the client floor) carrying its
    // props, RECURSIVELY — a nested transient prop ({ type:"object", props:{…} }) rebuilds as its own
    // throwaway transient (threading the SAME nextTransient counter), an in-store prop ({ type:"object", id })
    // resolves via byId (unresolved → null, fail-soft), a scalar/null prop via ExecValueFromWire. This carries
    // a nested draft (SetTable's `state.draft = sys.new(desc)`) so the reproduced open create-form renders
    // RefSelect/Field over a REAL draft (not null → no `sys.field(null,…)` throw), reads `db.designs`, and
    // harvests it. The whole graph is never persisted and never held (I2/I3): it is pure view-state the
    // server reproduces to plan the fetch. A by-id ref that no longer resolves is dropped (fail-soft).
    private static Code.IExecValue? SlotLocalFromWire(JsonElement el, Dictionary<int, Code.ExecObject> byId, ref int nextTransient)
    {
        if ((el.TryGetProperty("type", out var t) ? t.GetString() : null) == "object")
        {
            if (el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                return byId.TryGetValue(idEl.GetInt32(), out var obj) ? obj : null; // an in-store ref
            // A transient object by value: rebuild its props (no identity in the store), each prop routed
            // back through this method so a nested transient/ref/scalar/null reconstructs uniformly.
            var props = new Dictionary<string, Code.IExecValue>();
            if (el.TryGetProperty("props", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var f in p.EnumerateObject())
                    props[f.Name] = SlotLocalFromWire(f.Value, byId, ref nextTransient) ?? new Code.ExecNull();
            return new Code.ExecObject { Id = --nextTransient, Props = props };
        }
        return ExecValueFromWire(el);
    }

    private static Code.IExecValue? SessionVarFromWire(JsonElement el, Dictionary<int, Code.ExecObject> byId)
    {
        if ((el.TryGetProperty("type", out var t) ? t.GetString() : null) == "object")
            return el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                   && byId.TryGetValue(idEl.GetInt32(), out var obj)
                ? obj : null;
        return ExecValueFromWire(el);
    }

    // A scalar session var as the client interpreter holds it: { "type", "value" }.
    private static Code.IExecValue ExecValueFromWire(JsonElement el) =>
        (el.TryGetProperty("type", out var t) ? t.GetString() : null) switch
        {
            "int"  => new Code.ExecInt { Value = el.GetProperty("value").GetInt32() },
            "bool" => new Code.ExecBool { Value = el.GetProperty("value").GetBoolean() },
            "text" => new Code.ExecText { Value = el.GetProperty("value").GetString() ?? "" },
            _ => new Code.ExecNull(),
        };

    // The client's ack that it has applied an arrayAdd's remap (re-keyed its copy from the transient
    // negative id to the real one): drop that mapping. The client now addresses the object by its real id
    // and will never send the transient one again, so the entry is dead — this keeps the per-session table
    // to just the in-flight adds. A lost ack only means the entry lingers until the session expires (the
    // backstop), never a wrong resolution.
    private string HandleAckRemap(WsRequest req)
    {
        if (req.TempId is { } tempId)
            Session(req)?.DropTransientId(tempId);
        return Serialize(new AckRemapResponse());
    }

    // ── login / logout (the session→principal bind, M-auth) ─────────────────────
    //
    // Authentication over the EXISTING WS connection — no reserved route (login is a STATE, not a URL;
    // the app keeps a clean URL space). `login` resolves the `User` by `name` through the store seam,
    // verifies the plaintext `password` against the stored hash in the User's `password`-typed field
    // (PBKDF2, server-side; the store keeps the real hash, only the SHIPPED value is blanked), and on
    // success SETS this session's PrincipalUserId — the durable home the renderer/floors read. On failure
    // it leaves the principal unchanged. The principal binds THIS session only (no cross-session/real-time
    // propagation this slice).

    // login: verify credentials and bind the principal. Wrong password AND unknown user produce the SAME
    // negative reply ({ ok:false }, no userId), so the wire reveals no user-enumeration signal. A failed
    // login is a NORMAL result (not an exception), so it does NOT go through the `{ error }` rollback path.
    //
    private string HandleLogin(WsRequest req)
    {
        if (Session(req) is not { } session)
            return Error("login requires a known 'clientId'.");
        if (req.Name is not { } name || req.Password is not { } password)
            return Error("login requires 'name' and 'password'.");

        // The User's credential field — the `password`-typed prop. No password field declared ⇒ login is
        // impossible (the negative reply), exactly like a missing user.
        if (Code.UserConvention.PasswordFieldName(_desc) is not { } passwordField)
        {
            _verifyPassword(password, Code.AuthCrypto.DummyHash);
            return Serialize(new LoginResponse { Ok = false });
        }

        // Resolve the principal by name through the store seam (the model's terms — an extent read), then
        // verify. A missing user, a user with no/blank hash, or a wrong password ALL fall to the same
        // negative reply. (FindUserByName reads the raw stored fields — the password value is blanked only
        // in the SHIPPED graph, never in this kernel-side lookup, so the real hash is here to verify.)
        (int? UserId, string Hash) realHash = FindUserByName(name) is (int userId, ObjectValue userFields)
            && userFields.Fields.GetValueOrDefault(passwordField) is TextValue { Text.Length: > 0 } h
            ? (UserId: userId, Hash: h.Text)
            : (UserId: null, Hash: Code.AuthCrypto.DummyHash);
        if (_verifyPassword(password, realHash.Hash) && realHash.UserId is { } verifiedUserId)
        {
            session.PrincipalUserId = verifiedUserId;
            return Serialize(new LoginResponse { Ok = true, UserId = verifiedUserId });
        }

        return Serialize(new LoginResponse { Ok = false });
    }

    // logout: clear the bound principal back to anonymous (idempotent — a no-session/already-anonymous
    // logout still replies ok).
    private string HandleLogout(WsRequest req)
    {
        if (Session(req) is { } session)
            session.PrincipalUserId = null;
        return Serialize(new LogoutResponse());
    }

    // Resolve a User by its `name` field through the store seam (an extent scan). Returns the matching
    // object's intrinsic id + its RAW stored fields (so the caller reads the real hash in the User's
    // `password`-typed field — the load boundary blanks only the SHIPPED value, never this kernel lookup),
    // or null when no User carries that name. Kernel-side only (never app Code).
    private (int Id, ObjectValue Fields)? FindUserByName(string name)
    {
        foreach (var (id, fields) in _store.ReadExtent(Code.UserConvention.TypeName))
            if (fields.Fields.GetValueOrDefault(Code.UserConvention.NameField) is TextValue { Text: var n } && n == name)
                return (id, fields);
        return null;
    }


    // Deserialize a wire object value ({ props: { name: leaf } }) into an ObjectValue, schema-driven:
    // every prop must be a declared single scalar field, and an enum value must be a declared member. Used by
    // the live object/collection handlers (HandleWrite / HandleAddEntry / HandleAddSetMember / HandleArrayAdd)
    // to turn a request's value element into store fields. (Restored when HandleArrayRemove — which formerly
    // hosted it — was deleted in the unified-commit slice; it is a shared helper, not array-remove-specific.)
    private ObjectValue ExecObjectValue(JsonElement el, TypeDefinition type, bool allowSets = false)
    {
        var fields = new Dictionary<string, NodeValue>();
        if (el.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject())
            {
                var propDef = type.Props?.FirstOrDefault(d => d.Name == p.Name)
                    ?? throw new InvalidOperationException($"Type '{type.Name}' has no field '{p.Name}'.");
                if (allowSets && propDef.Cardinality == Cardinality.Set && p.Value.ValueKind == JsonValueKind.Object
                    && p.Value.TryGetProperty("type", out var setType) && setType.GetString() == "array")
                    continue;
                if (propDef.Cardinality != Cardinality.Single || _desc.ScalarBaseOf(propDef.Type) is not { } baseType)
                    throw new InvalidOperationException($"Field '{p.Name}' on '{type.Name}' is not a scalar field.");
                var leaf = LeafForType(p.Value, baseType);
                if (leaf is TextValue tv && !_desc.EnumAccepts(propDef.Type, tv.Text))
                    throw new InvalidOperationException($"'{tv.Text}' is not a value of enum '{propDef.Type}'.");
                fields[p.Name] = leaf;
            }
        return new ObjectValue(fields);
    }

    // Convert a wire scalar ({ type, value }) to the prop's DECLARED base type. The
    // wire's claimed type must agree: int/bool/text exactly; decimal/date/datetime
    // arrive as the Code runtime's text projection (see DbBridge.ScalarToExec) and
    // are parsed. An EMPTY decimal/date/datetime means UNSET — it round-trips as the
    // empty leaf (TextValue ""), never force-parsed (DateOnly.Parse("") threw). Anything
    // else is a type mismatch → reject.
    private static NodeValue LeafForType(JsonElement el, BaseType declared)
    {
        var wireType = el.TryGetProperty("type", out var t) ? t.GetString() : null;
        var v = el.TryGetProperty("value", out var vv) ? vv : default;
        return (declared, wireType) switch
        {
            (BaseType.Int, "int")       => new IntValue(v.GetInt32()),
            (BaseType.Bool, "bool")     => new BoolValue(v.GetBoolean()),
            (BaseType.Text, "text")     => new TextValue(v.GetString() ?? ""),
            (BaseType.Decimal, "text")  => OptionalLeaf(v.GetString() ?? "", s => new DecimalValue(decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture))),
            (BaseType.Date, "text")     => OptionalLeaf(v.GetString() ?? "", s => new DateValue(DateOnly.Parse(s))),
            (BaseType.DateTime, "text") => OptionalLeaf(v.GetString() ?? "", s => new DateTimeValue(DateTimeOffset.Parse(s))),
            _ => throw new InvalidOperationException($"A '{wireType}' value does not fit the declared '{declared}' field."),
        };
    }

    // A decimal/date/datetime leaf, where the empty string is UNSET. An optional leaf has no typed
    // "empty" (DateOnly/decimal/DateTimeOffset are non-nullable), so an unset one is the empty leaf
    // (TextValue "") — exactly how an enum's unset value is stored. A non-empty value parses as before.
    // The store, validator (the empty-text-for-an-optional carve-out), and DbBridge.ScalarToExec all
    // round-trip the empty leaf back to the blank field.
    private static NodeValue OptionalLeaf(string s, Func<string, NodeValue> parse) =>
        s.Length == 0 ? new TextValue("") : parse(s);

    // ── NodeValue deserialization ─────────────────────────────────────────────

    private static NodeValue DeserializeLeaf(JsonElement el, TypeDefinition type) =>
        type.BaseType switch
        {
            BaseType.Bool     => new BoolValue(el.GetBoolean()),
            BaseType.Int      => new IntValue(el.ValueKind == JsonValueKind.String ? int.Parse(el.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : el.GetInt32()),
            BaseType.Decimal  => el.ValueKind == JsonValueKind.String ? OptionalLeaf(el.GetString() ?? "", s => new DecimalValue(decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture))) : new DecimalValue(el.GetDecimal()),
            BaseType.Text     => new TextValue(el.GetString() ?? ""),
            // An enum value is its value name — text-shaped, no new value-kind.
            BaseType.Enum     => new TextValue(el.GetString() ?? ""),
            // A password's plaintext is text-shaped here (the WS write LAYER hashes it before the
            // store — see HashPasswordFields — so this only materializes the leaf; it never stores
            // plaintext). The two chokepoints key on the declared `password` type, not this base.
            BaseType.Password => new TextValue(el.GetString() ?? ""),
            // An image's wire value is its pool blob NAME (the hash string the upload edge returned),
            // or "" (cleared) — text-shaped like the rest; no bytes ever cross this wire
            // (docs/plans/assets-design.md — uploads go through the pool's own HTTP edges, never here).
            BaseType.Image    => new TextValue(el.GetString() ?? ""),
            // An empty date/datetime means UNSET — the empty leaf, not a force-parse of "" (which threw).
            BaseType.Date     => OptionalLeaf(el.GetString() ?? "", s => new DateValue(DateOnly.Parse(s))),
            BaseType.DateTime => OptionalLeaf(el.GetString() ?? "", s => new DateTimeValue(DateTimeOffset.Parse(s))),
            _ => throw new InvalidOperationException($"Cannot deserialize leaf of type {type.BaseType}")
        };

    // Deserialize a node value (object body or base leaf) from a raw JSON value.
    // Object: reads each non-dictionary leaf field from the element (dictionary
    // fields are navigation boundaries and are not part of a create/edit form).
    private NodeValue DeserializeValue(JsonElement el, TypeDefinition type)
    {
        if (type.BaseType != BaseType.Object)
            return DeserializeLeaf(el, type);

        var fields = new Dictionary<string, NodeValue>();
        foreach (var prop in type.Props ?? [])
        {
            if (prop.Cardinality == Cardinality.Dictionary) continue;
            if (el.TryGetProperty(prop.Name, out var fe))
                fields[prop.Name] = DeserializeValue(fe, ResolveTypeDef(prop.Type));
        }
        return new ObjectValue(fields);
    }

    private TypeDefinition ResolveTypeDef(string name) =>
        BaseTypes.IsName(name)
            ? BaseTypes.Leaf(name)
            : _desc.FindType(name) ?? throw new InvalidOperationException($"Unknown type '{name}'.");

    private static NodeValue ParseKey(string key, string keyType) => keyType switch
    {
        "int"      => new IntValue(int.Parse(key)),
        "decimal"  => new DecimalValue(decimal.Parse(key)),
        "bool"     => new BoolValue(bool.Parse(key)),
        "date"     => new DateValue(DateOnly.Parse(key)),
        "datetime" => new DateTimeValue(DateTimeOffset.Parse(key)),
        _          => new TextValue(key)
    };

    private static string KeyString(NodeValue key) => key switch
    {
        IntValue i       => i.Value.ToString(),
        TextValue t      => t.Text,
        DecimalValue d   => d.Value.ToString(),
        BoolValue b      => b.Value.ToString().ToLowerInvariant(),
        DateValue d      => d.Value.ToString("yyyy-MM-dd"),
        DateTimeValue dt => dt.Value.ToString("O"),
        _ => throw new InvalidOperationException($"Cannot use {key.GetType().Name} as a key.")
    };

    // ── helpers ───────────────────────────────────────────────────────────────

    private static NodePath ParsePath(string urlPath)
    {
        var segs = urlPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return NodePath.FromSegments(segs);
    }

    // Serialize a typed response with the handler's options (compact, camelCase naming
    // policy) — the bytes the former hand-built JsonObject produced. The correlation id,
    // when present, is still appended last by WithId.
    private string Serialize<T>(T response) => JsonSerializer.Serialize(response, _jsonOpts);

    // Routes through the same options (so `Error` → the `error` wire key); instance, not
    // static, because the naming policy lives on the instance's _jsonOpts.
    private string Error(string message) =>
        Serialize(new ErrorResponse { Error = message });
}
