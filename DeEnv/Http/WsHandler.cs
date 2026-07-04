using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Http;

// ── the wire model ─────────────────────────────────────────────────────────────
//
// The WS wire is the C#↔TS contract (see DeEnv/Instance/ws.ts). One incoming JSON
// message → one outgoing JSON response. Both sides are typed here so the field names
// are the contract; the shapes below reproduce the exact bytes the hand-built JSON
// used to. A value/vars/args body that needs the schema/runtime to interpret stays a
// raw JsonElement (DeserializeValue/ExecObjectValue/LeafForType read it schema-driven).

// One request record covering every op (nullable fields, deserialized once per message).
// A flat union over a tagged JSON object avoids polymorphic deserialization; each handler
// reads the fields its op carries and validates the ones it requires.
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
    // refetch: the client's live per-component view-state (client data layer, slice 1b), a map from a
    // component's render-slot key (`comp:<slotpath>`) to its setup-scope locals ({ varName: wireValue }).
    // HandleRefetch rebuilds it into the `seed` RenderState applies, so the server reproduces the client's
    // EXACT render (a toggled-open popup) and ships the data it demands. Same shape/handling as `Vars`.
    public JsonElement? SlotState { get; init; }
    // refetch: the ACTION-MISS intent (client data layer, slice 4) — present when this refetch is driven by a
    // CLICK HANDLER that read un-shipped data. `HandlerFn` is the clicked handler closure's twin-stable lambda
    // fn-id; `HandlerSlot` its render-slot path. HandleRefetch combines them (CodeExecutor.HandlerKey) into the
    // key the server uses to locate that handler in the reproduced render and invoke it read-only to harvest
    // the data it reads. Absent on an ordinary (render-miss) refetch — then no handler is invoked (today's path).
    public int? HandlerFn { get; init; }
    public string? HandlerSlot { get; init; }
    // login: the credentials a `login` op carries. `name` is the User's login identifier
    // (UserConvention.NameField), `password` the plaintext verified against the stored hash.
    public string? Name { get; init; }
    public string? Password { get; init; }
    // commit: the batch of field edits from a ctx.commit(). Shaped as a list so Step B can add
    // `creates`/`relations` without re-cutting the message format. Step A only sends/handles `edits`.
    public JsonElement? Edits { get; init; }
    // commit (atomic-commit Step B): the new objects staged in this ctx — each `{ tempId, props }` (the
    // draft's scalar fields; its TYPE is derived server-side from the relation that links it, never asserted
    // on the wire) — and the `relations` linking them in (`{ kind:"set", setId, childId }` /
    // `{ kind:"ref", parentId, prop, childId }`, where childId may be a create's negative tempId). Absent on
    // an edits-only commit.
    public JsonElement? Creates { get; init; }
    public JsonElement? Relations { get; init; }
    // commit: the optimistic-concurrency anti-clobber guard (DECISIONS.md "App versioning — the full
    // design (M13 clump)", §0's baseVersion bullet). The store version the committing ctx last knew
    // (ws.ts stamps it from the client's remembered HEAD, captured when the ctx first staged an edit).
    // Threaded to IInstanceStore.CommitBatch, which rejects (StaleBaseException) iff an object this
    // batch EDITS changed after this version. Null = no check (an older/lower-level caller with no
    // version concept) — kept optional for compatibility, not a permanent bypass; every real ws.ts
    // commit supplies it.
    public int? BaseVersion { get; init; }
}

// Response records — one per op. Each property's camelCase (the shared `_jsonOpts`
// PropertyNamingPolicy) is the wire key, so these serialize to the exact bytes the old
// JsonObject literal produced; the correlation id is still appended last by WithId, so
// these never carry it. Field order matches the former literals; the get-only computed
// `Op`/`Ok` props serialize by default.

// `newVersion` (optimistic-concurrency anti-clobber — see CommitResponse's doc): EVERY mutating op's
// reply carries the store's version AFTER this write, not just `commit`'s. A live write (autosave,
// arrayAdd, setRef, …) advances the store's HEAD exactly like a commit does (JsonFileInstanceStore
// bumps it uniformly); a client that only learned the version from `commit` acks would silently drift
// behind its OWN live writes (e.g. a create's arrayAdd, then a later ctx.commit() editing that SAME
// just-created object) and its next commit would be wrongly rejected as stale against its own history.
public sealed record WriteResponse
{
    public string Op => "write";
    public required string Path { get; init; }
    public bool Ok => true;
    public required int NewVersion { get; init; }
}

public sealed record AddEntryResponse
{
    public string Op => "addEntry";
    public required string Path { get; init; }
    public bool Ok => true;
    public required string Key { get; init; }
    public required int NewVersion { get; init; }
}

public sealed record RemoveEntryResponse
{
    public string Op => "removeEntry";
    public required string Path { get; init; }
    public bool Ok => true;
    public required int NewVersion { get; init; }
}

public sealed record HelloResponse
{
    public string Op => "hello";
    public required bool SessionAlive { get; init; }
}

public sealed record ObjectPropChangeResponse
{
    public string Op => "objectPropChange";
    public bool Ok => true;
    public required int NewVersion { get; init; }
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

public sealed record SetReferenceFieldResponse
{
    public string Op => "setReferenceField";
    public bool Ok => true;
    // Present only when a new object was minted (a create-new pick), never for link/clear —
    // omitted when null by the options' DefaultIgnoreCondition (WhenWritingNull).
    public int? NewId { get; init; }
    public required int NewVersion { get; init; }
}

// A collection prop the store minted on a new object: its intrinsic id + element type, so
// the client re-keys the transient array it created optimistically. Field order: id, then
// elementTypeName (matching the former literal).
public sealed record CollectionInfo
{
    public required int Id { get; init; }
    public required string ElementTypeName { get; init; }
}

public sealed record ArrayAddResponse
{
    public string Op => "arrayAdd";
    // `newId`, not `id` — the reply's `id` slot is the request correlation id (added by WithId).
    public required int NewId { get; init; }
    // Keyed by user prop name; the dictionary keys serialize verbatim (the naming policy
    // renames CLR PROPERTIES, not dictionary keys — DictionaryKeyPolicy is unset).
    public required Dictionary<string, CollectionInfo> Collections { get; init; }
    // Echoed back ONLY when the request carried one (a set add from the client) — omitted
    // when null by the options' DefaultIgnoreCondition (WhenWritingNull).
    public int? TempId { get; init; }
    public required int NewVersion { get; init; }
}

public sealed record ArrayRemoveResponse
{
    public string Op => "arrayRemove";
    public bool Ok => true;
    public required int NewVersion { get; init; }
}

public sealed record RefetchResponse
{
    public string Op => "refetch";
    // The raw client-state node from RenderState; serialized inline, not reshaped.
    public required JsonNode State { get; init; }
}

// `Report` (M13 slice 4, additive — the ONE approved wire widening this slice makes) carries a structured
// plan/outcome object for an action that produces one (today: `publish`'s identity-diff report); omitted
// (null) for every other action, so the reply is byte-identical to before for create/delete/clone/rename/
// setDesign/commitDesign. IHostActions.Run's return value flows straight through — see its own doc.
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

// login: the result of a credential check (M-auth). `ok` is the ONE bit the client acts on — a SUCCESS
// reply also carries the bound `userId`; a FAILURE (wrong password OR unknown user — the SAME reply, so
// the wire reveals no user-enumeration signal) carries no id (omitted when null). A failed login is a
// NORMAL negative result, not an error — it is NOT routed through the `{ error }` rollback path.
public sealed record LoginResponse
{
    public string Op => "login";
    public required bool Ok { get; init; }
    public int? UserId { get; init; }
}

// logout: always succeeds (clearing an already-anonymous session is idempotent).
public sealed record LogoutResponse
{
    public string Op => "logout";
    public bool Ok => true;
}

public sealed record ErrorResponse
{
    public required string Error { get; init; }
}

// A commit REJECTED by a same-field collision (M13 slice 6 — the ONE approved wire widening this slice
// makes). It carries BOTH the ordinary `error` string (so a custom `fn render()` that ignores conflicts
// still shows the global error banner — no app can silently clobber) AND the structured `conflicts`
// payload the generic form's coarse banner renders. `newVersion` is the store's CURRENT version — the
// client re-pins the committing ctx's base to it so a "Keep mine" re-commit forces at a now-fresh base.
// A commit rejected for any OTHER reason (a malformed batch, a floor denial) stays the plain `{ error }`
// reply — `conflicts` is present ONLY on a genuine same-field collision, so every non-conflict reply is
// byte-identical to before.
public sealed record ConflictResponse
{
    public required string Error { get; init; }
    public required IReadOnlyList<ConflictFieldWire> Conflicts { get; init; }
    public required int NewVersion { get; init; }
}

// One conflicted field on the wire (M13 slice 6): the object id + a cheap type label, the field name, and
// the per-field {base, mine, theirs} as bare wire scalars (scalarOf/refId shape). The coarse banner
// surfaces only `field`; base/mine/theirs ride along so the later fine per-field UI needs no wire change.
public sealed record ConflictFieldWire
{
    public required int Object { get; init; }
    public required string TypeName { get; init; }
    public required string Field { get; init; }
    public object? Base { get; init; }
    public object? Mine { get; init; }
    public object? Theirs { get; init; }
}

// ── parsed commit relations (atomic-commit Step B) ──────────────────────────────────────────────
// A relation linking a (possibly just-created) object into the graph. ChildRef is the linked object's id —
// a transient create's NEGATIVE tempId, or a resolved positive real id. ChildType is the link-derived
// declared TYPE of the child (a set's element type / a ref prop's target type) — server-resolved, never
// wire-asserted — used to type a transient create + the store link.
public abstract record ParsedRelation(int ChildRef, string ChildType);
public sealed record ParsedSetRelation(int SetId, int Child, string ElementType) : ParsedRelation(Child, ElementType);
public sealed record ParsedRefRelation(int ParentId, string Prop, int Child, string TargetType) : ParsedRelation(Child, TargetType);

// Transport-agnostic WebSocket message dispatcher.
// One incoming JSON message → one outgoing JSON response (request/response model).
// The transport (GenHTTP websocket) calls ProcessMessage and sends the result.
public sealed class WsHandler
{
    private readonly IInstanceStore _store;
    private readonly InstanceDescription _desc;
    private readonly TypeResolver _resolver;
    private readonly ClientSessionStore? _sessions;
    private readonly LiveRegistry _registry;
    private readonly IHostActions _hostActions;
    private readonly Func<string, string, bool> _verifyPassword;
    // The instance's mount prefix ("/" root-mounted, "/apps/<name>" path-mounted). The client sends
    // FULL paths (location.pathname, which carries the mount) for write/refetch ops; the handler
    // strips the mount before resolving against the schema, so the instance stays mount-unaware (its
    // node paths are root-relative). Identity-stripping when "/" (behavior-preserving).
    private readonly string _mountBase;
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public WsHandler(IInstanceStore store, InstanceDescription desc, ClientSessionStore? sessions = null,
        LiveRegistry? registry = null, IHostActions? hostActions = null, string mountBase = "/",
        Func<string, string, bool>? verifyPassword = null)
    {
        _store = store;
        _desc = desc;
        _resolver = new TypeResolver(desc);
        _sessions = sessions;
        _registry = registry ?? new LiveRegistry();
        _hostActions = hostActions ?? new NoHostActions();
        _mountBase = mountBase;
        _verifyPassword = verifyPassword ?? Code.AuthCrypto.Verify;
    }

    // The warm per-client session a code-UI message addresses (clientId minted at SSR).
    private ClientSession? Session(WsRequest req) =>
        _sessions != null && req.ClientId is { } id ? _sessions.Get(id) : null;

    // Resolve a wire id through the session's transient-id remap: a just-added object's negative id →
    // the real one the server minted for it. No session (or an id it never mapped) → unchanged. This is
    // what lets the client address a just-added object before its arrayAdd round-trip has returned.
    private static int Resolve(ClientSession? session, int id) => session?.ResolveId(id) ?? id;

    // ── the write floor (M-auth) ────────────────────────────────────────────────
    //
    // The non-bypassable WRITE check: a create/edit/delete is accepted only when a matching-verb rule's
    // condition holds for the bound principal + the target object — else it is REJECTED (the reply carries
    // an error, which the client surfaces and its existing rollback restores from; the store is never
    // touched). Deny-by-default among the ruled types; DORMANT (allow-all, today's behavior) when the app
    // declares no rules — so a solo app pays nothing. The principal is the session's bound `currentUser`
    // (ClientSession.PrincipalUserId — harness-set this slice; the password-login slice binds it on the WS).
    //
    // The OBJECT-graph write seams are ALL gated: set-member create/delete (arrayAdd/arrayRemove +
    // removeEntry on a set), object-field + reference edit (objectPropChange/setReferenceField), the
    // path-addressed `write` onto a set member's scalar field (HandleWrite — the SAME mutation
    // objectPropChange performs, so it is gated identically), AND (review fix 3) a client DICT write whose
    // owner is a set member — addEntry/removeEntry on a dict and a path-`write` onto a dict entry are gated
    // as an `edit` of the owning set member (RequireDictWrite), so an immutable Commit/Branch idMap cannot
    // be mutated from a client. That is the surface the read floor (DbBridge graph + sys.extent listing) gates.
    // ponytail: the dict READ floor is still deferred (DbBridge does not gate dict members), and a dict
    // whose owner is NOT a set member (a root-level dict like `/settings`) stays write-deferred too — only
    // the set-member dict WRITE is gated now, which is exactly what the Commit/Branch immutability needs.
    // Per-field rules and richer condition inputs (now/client/cross-row) are later slices too.
    private Code.AccessFloor Floor(WsRequest req) =>
        new(_desc.Rules ?? [], Code.AccessFloor.LoadPrincipal(_store, _desc, Session(req)?.PrincipalUserId));

    // Reject a denied write: throw whose message ProcessMessage's catch turns into the `{ error }` reply
    // (→ client rollback). A single chokepoint so every gated handler reads the same.
    private static void RequireWrite(Code.AccessFloor floor, string verb, string typeName, Code.ExecObject target)
    {
        if (!floor.CanWrite(verb, typeName, target))
            throw new InvalidOperationException(
                $"Access denied: not allowed to {verb} '{typeName}'.");
    }

    // The candidate object for a CREATE decision: the about-to-be-created value as a scalar-only ExecObject,
    // so a condition like `where object.status == "draft"` reads the NEW data. Built from the same parsed
    // ObjectValue the create would persist (id 0 — it has no identity until minted).
    private Code.ExecObject CandidateFromValue(JsonElement value, TypeDefinition type) =>
        Code.AccessFloor.ScalarObject(type.Name, 0, (ObjectValue)DeserializeValue(value, type), _desc);

    // ── the password write chokepoint (M-auth `password` type) ───────────────────
    //
    // A `password`-typed plaintext from ANY client write path is PBKDF2-hashed HERE, at the WS layer (above
    // the store), before it persists — so the IInstanceStore stays dumb (CLAUDE rule 6: a future pillar-5
    // storage engine inherits the hashing for free) and NO client write path ever stores plaintext. Every
    // WS write handler that materializes fields routes through these (objectPropChange/arrayAdd/addEntry/
    // path-write/setReferenceField create-new/addSetMember). The two NON-WS write paths bypass them on
    // purpose: AdminSeed writes an ALREADY-hashed value direct to the store (a store-level hash would
    // double-hash it), and a `password` in initialData is a load/validate ERROR (never persisted).
    //
    // EMPTY = NO CHANGE. An empty password value writes nothing — so a field that loads BLANK ("" from the
    // read chokepoint) and is re-submitted unchanged never clobbers the stored hash with "". A create that
    // omits the password yields a credential-less user (the documented "create-then-set" contract).

    // Every password-typed plaintext field in a create/edit ObjectValue, hashed (empty ones DROPPED so they
    // persist nothing). Non-password fields pass through untouched. Used by the object-create paths.
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
                "write"             => HandleWrite(path, pathStr, req),
                "addEntry"          => HandleAddEntry(path, pathStr, req),
                "removeEntry"       => HandleRemoveEntry(path, pathStr, req),
                "hello"             => HandleHello(req),
                "objectPropChange"  => HandleObjectPropChange(req),
                "commit"            => HandleCommit(req),
                "setReferenceField" => HandleSetReferenceField(req),
                "arrayAdd"          => HandleArrayAdd(req),
                "arrayRemove"       => HandleArrayRemove(req),
                "refetch"           => HandleRefetch(pathStr, req),
                "hostAction"        => HandleHostAction(req),
                "ackRemap"          => HandleAckRemap(req),
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

    // Echo the request's correlation id back so the client can match reply→request.
    private string WithId(string json, int? id)
    {
        if (id is null) return json;
        var node = JsonNode.Parse(json)!;
        node["id"] = id.Value;
        return node.ToJsonString(_jsonOpts);
    }

    // ── write ─────────────────────────────────────────────────────────────────

    private string HandleWrite(NodePath path, string pathStr, WsRequest req)
    {
        var typeInfo = _resolver.ResolveType(path);
        if (typeInfo == null)
            return Error($"Path '{pathStr}' does not resolve.");

        if (req.Value is not { } valEl)
            return Error("Missing 'value' in write message.");

        var value = DeserializeLeaf(valEl, typeInfo.Type);

        // The WRITE floor (M-auth): a path `write` onto a SET MEMBER's scalar field (`/<set>/<id>/<field>`)
        // is the SAME mutation HandleObjectPropChange performs — an `edit` of the owning extent object — so
        // it MUST be gated the same way, or it routes around the floor. The owner is the set member at the
        // parent path; its intrinsic id IS the parent's last segment (a set's members are keyed by id), so
        // the candidate is built by-id over its CURRENT scalar fields, exactly as the objectPropChange edit
        // floor builds it. Rejected → the `{ error }` reply (client rollback); the store is never touched.
        // A scalar dict-entry write on a set-member-owned dict IS gated below (review fix 3, RequireDictWrite);
        // an OBJECT-entry field path (`/customers/42/name`) has an object parent and writes through the
        // set-member gate right here; a root-level dict stays write-deferred with the still-deferred dict READ gap.
        var parentPath = path.IsRoot ? null : NodePath.FromSegments(path.Segments.Take(path.Segments.Count - 1));
        if (parentPath != null && SetMemberOwnerId(parentPath) is { } ownerId
            && _store.ReadById(ownerId) is { } owner)
            RequireWrite(Floor(req), "edit", owner.TypeName,
                Code.AccessFloor.ScalarObject(owner.TypeName, ownerId, owner.Fields, _desc));

        // The WRITE chokepoint (M-auth `password`): a path `write` onto a `password`-typed leaf (a set
        // member's password field, or a `dict of password` entry) hashes the plaintext before the store —
        // so this seam never routes plaintext around the WS hash. An EMPTY value writes NOTHING (= "no
        // change", the same blank-is-unchanged rule as objectPropChange). Decided AFTER the floor gate.
        if (typeInfo.Type.Name == "password")
        {
            if (HashScalarLeaf(typeInfo.Type.Name, value) is not { } hashed)
                // empty → NO write happened (store untouched): finding 3 (a WRITE's version over-counted by a
                // separate read) does not apply, there being no write. CurrentVersion is the accurate HEAD.
                return Serialize(new WriteResponse { Path = pathStr, NewVersion = _store.CurrentVersion });
            value = hashed;
        }

        // A SCALAR dictionary entry's value lives at its path but is addressed by (dict, key)
        // — WriteLeaf can't walk into a dict, so upsert the entry. (An OBJECT entry's field
        // path, e.g. /customers/42/name, has an object parent and writes through WriteLeaf.)
        // Report the version the store returns UNDER ITS LOCK (finding 3) — never a separate read.
        int newVersion;
        if (parentPath != null
            && _resolver.ResolveType(parentPath) is { Cardinality: Cardinality.Dictionary } parentInfo)
        {
            // The DICT write floor (review fix 3): a path-`write` onto a dict entry whose owning object is a
            // set member is an `edit` of that owner — so a client cannot overwrite a Commit's idMap lineage
            // id here any more than through addEntry. Rejected → the `{ error }` reply; store untouched.
            RequireDictWrite(req, parentPath);
            newVersion = _store.WriteDictionaryEntry(parentPath, ParseKey(path.Segments[^1], parentInfo.KeyTypeName ?? "text"), value);
        }
        else
        {
            newVersion = _store.WriteLeaf(path, value);
        }

        return Serialize(new WriteResponse { Path = pathStr, NewVersion = newVersion });
    }

    // The intrinsic id of the EXTENT OBJECT a path addresses, but ONLY when the path is a SET MEMBER
    // (`/<set>/<id>`) — a member's own segment IS its id (a set keys members by id), and the grandparent
    // resolves to a Set. Used by the write floor to gate a path `write` onto a set member's scalar field as
    // an `edit` of that member, matching HandleObjectPropChange. Returns null for anything else (the root, a
    // top-level field, a single reference, or a DICTIONARY entry — the deferred dict gap), so only the
    // set-member case is gated. The grandparent check is what distinguishes a set member (gate) from a dict
    // entry (defer): both have a Single-object parent, but only a set member's parent sits under a Set.
    private int? SetMemberOwnerId(NodePath memberPath)
    {
        if (memberPath.Segments.Count < 2) return null; // need a /<set>/<id> shape at least
        if (_resolver.ResolveType(memberPath) is not { Cardinality: Cardinality.Single } info
            || info.Type.BaseType != BaseType.Object)
            return null; // not an object (so not a member with scalar fields)
        var grandparent = NodePath.FromSegments(memberPath.Segments.Take(memberPath.Segments.Count - 1));
        if (_resolver.ResolveType(grandparent) is not { Cardinality: Cardinality.Set }) return null;
        return int.TryParse(memberPath.Segments[^1], out var id) ? id : null;
    }

    // The DICTIONARY write floor (M-auth, review fix 3): gate a client write to a dict ENTRY as an `edit`
    // of the object that OWNS the dict, when that owner is a set member (`/<set>/<id>/<dict>`) — the
    // security-critical case (a `Commit`/`Branch` idMap lives on a `db.commits`/`db.branches` set member,
    // so `create edit delete where false` on those types now blocks a client addEntry/removeEntry/path-
    // write, not only objectPropChange). `dictPath` is the DICT node's own path; its owner is the object
    // one segment up, whose id IS that segment (a set keys members by id). Throws (the `{ error }` reply)
    // when the owner's `edit` is denied; a no-op when the dict is NOT on a set member (a root-level dict
    // like `/settings` keeps the deferred behavior — its owner is the un-ruled Db root, and the dict READ
    // floor is still deferred there too). Mirrors the set-member gate, not the read floor: write-side only.
    private void RequireDictWrite(WsRequest req, NodePath dictPath)
    {
        if (dictPath.IsRoot) return;
        var ownerPath = NodePath.FromSegments(dictPath.Segments.Take(dictPath.Segments.Count - 1));
        if (SetMemberOwnerId(ownerPath) is not { } ownerId || _store.ReadById(ownerId) is not { } owner)
            return; // owner is not a set member (root/nested-object dict) — deferred, as before
        RequireWrite(Floor(req), "edit", owner.TypeName,
            Code.AccessFloor.ScalarObject(owner.TypeName, ownerId, owner.Fields, _desc));
    }

    // ── addEntry (create on the create-form Save) ──────────────────────────────

    private string HandleAddEntry(NodePath path, string pathStr, WsRequest req)
    {
        var typeInfo = _resolver.ResolveType(path);
        if (typeInfo == null)
            return Error($"Path '{pathStr}' does not resolve.");

        if (typeInfo.Cardinality == Cardinality.Set)
            return HandleAddSetMember(path, pathStr, typeInfo, req);

        if (typeInfo.Cardinality != Cardinality.Dictionary)
            return Error($"Path '{pathStr}' is not a dictionary.");

        if (req.Value is not { } valueEl)
            return Error("Missing 'value' in addEntry message.");
        if (req.Key is not { } keyStr || keyStr.Length == 0)
            return Error("A dictionary entry requires a non-empty 'key'.");

        // The DICT write floor (review fix 3): adding a dict entry whose owner is a set member is an `edit`
        // of that owner — the fix's core case (a client cannot inject an idMap entry into an immutable
        // Commit). Rejected → the `{ error }` reply; the store is never touched (the checks below are pure).
        RequireDictWrite(req, path);

        var value = DeserializeValue(valueEl, typeInfo.Type);
        // The WRITE chokepoint (M-auth `password`): hash any password-typed plaintext before the store. An
        // object-entry dict's value is an ObjectValue (hash its password fields); a scalar-entry dict whose
        // element type is itself `password` hashes the leaf. An empty value persists as-is (an empty entry).
        value = value is ObjectValue ov
            ? HashPasswordFields(typeInfo.Type.Name, ov)
            : HashScalarLeaf(typeInfo.Type.Name, value) ?? value;
        var key = ParseKey(keyStr, typeInfo.KeyTypeName ?? "text");
        // throws on duplicate → caught as { error }. Report the store's under-lock post-write version (finding 3).
        var newVersion = _store.CreateEntry(path, key, value);

        return Serialize(new AddEntryResponse { Path = pathStr, Key = KeyString(key), NewVersion = newVersion });
    }

    // ── removeEntry ────────────────────────────────────────────────────────────

    private string HandleRemoveEntry(NodePath path, string pathStr, WsRequest req)
    {
        var typeInfo = _resolver.ResolveType(path);
        if (typeInfo == null)
            return Error($"Path '{pathStr}' does not resolve.");
        if (req.Key is not { } keyStr)
            return Error("Missing 'key' in removeEntry message.");

        int newVersion; // the store's under-lock post-write version (finding 3), from whichever branch ran
        if (typeInfo.Cardinality == Cardinality.Set)
        {
            if (!int.TryParse(keyStr, out var memberId))
                return Error("Set member key must be an integer identity.");
            // The write floor: removing a set member is a `delete` of that member (same as arrayRemove).
            if (_store.ReadById(memberId) is { } member)
                RequireWrite(Floor(req), "delete", member.TypeName,
                    Code.AccessFloor.ScalarObject(member.TypeName, memberId, member.Fields, _desc));
            newVersion = _store.RemoveFromSet(path, memberId);
        }
        else if (typeInfo.Cardinality == Cardinality.Dictionary)
        {
            // The DICT write floor (review fix 3): removing a dict entry whose owner is a set member is an
            // `edit` of that owner — so a client cannot delete a Commit's idMap entry either. Rejected →
            // the `{ error }` reply; store untouched. (A dict on a non-set-member owner stays deferred, as
            // before, in lockstep with the still-deferred dict READ floor there.)
            RequireDictWrite(req, path);
            newVersion = _store.RemoveDictionaryEntry(path, ParseKey(keyStr, typeInfo.KeyTypeName ?? "text"));
        }
        else
        {
            return Error($"Path '{pathStr}' is not a dictionary or set.");
        }

        return Serialize(new RemoveEntryResponse { Path = pathStr, NewVersion = newVersion });
    }

    // ── set members + references (object model) ─────────────────────────────────

    private string HandleAddSetMember(NodePath path, string pathStr, ResolvedTypeInfo typeInfo, WsRequest req)
    {
        // The write floor: adding a set member is a `create` of the element type — whether minting a new
        // object (decided over the new value) or linking an existing one (decided over its current fields).
        var floor = Floor(req);
        var typeName = typeInfo.Type.Name;

        int id;
        int newVersion; // the LAST store op's under-lock version — the AddToSet link, in both branches (finding 3)
        if (req.RefId is { } refId)
        {
            if (_store.ReadById(refId) is { } linked)
                RequireWrite(floor, "create", typeName, Code.AccessFloor.ScalarObject(typeName, refId, linked.Fields, _desc));
            id = refId;
            newVersion = _store.AddToSet(path, id); // link an existing object
        }
        else if (req.Value is { } valueEl)
        {
            var obj = (ObjectValue)DeserializeValue(valueEl, typeInfo.Type);
            RequireWrite(floor, "create", typeName, Code.AccessFloor.ScalarObject(typeName, 0, obj, _desc));
            // The WRITE chokepoint (M-auth `password`): hash any password-typed plaintext before the store.
            id = _store.CreateObject(typeName, HashPasswordFields(typeName, obj)); // mint a new object…
            newVersion = _store.AddToSet(path, id);             // …then link it (its version is the reported one)
        }
        else
        {
            return Error("addEntry on a set requires 'refId' (existing) or 'value' (new).");
        }

        return Serialize(new AddEntryResponse { Path = pathStr, Key = id.ToString(), NewVersion = newVersion });
    }

    // ── code-owned UI mutations (the Code runtime, identity-addressed) ──────────

    // A two-way-bound prop write from the client: persist a single leaf field on the
    // object with this intrinsic id, after validating it against the schema — the
    // object's type must declare the prop as a single scalar field, and the value
    // must fit its declared base type. (The client already applied the change
    // optimistically; a reject rolls it back.)
    private string HandleObjectPropChange(WsRequest req)
    {
        if (req.ObjectId is not { } objectIdRaw)
            return Error("objectPropChange requires a numeric 'objectId'.");
        var objectId = Resolve(Session(req), objectIdRaw);
        if (req.Prop is not { } prop)
            return Error("objectPropChange requires 'prop'.");
        if (req.Value is not { } valEl)
            return Error("objectPropChange requires 'value'.");

        if (_store.ReadById(objectId) is not { } hit)
            return Error($"No object with id {objectId}.");
        var propDef = _desc.FindType(hit.TypeName)?.Props?.FirstOrDefault(p => p.Name == prop);
        if (propDef is null)
            return Error($"Type '{hit.TypeName}' has no field '{prop}'.");
        if (propDef.Cardinality != Cardinality.Single || _desc.ScalarBaseOf(propDef.Type) is not { } baseType)
            return Error($"Field '{prop}' on '{hit.TypeName}' is not a scalar field.");

        var leaf = LeafForType(valEl, baseType);
        // An enum field's value must be a declared member of its enum (or empty). The leaf is
        // text-shaped; off-list is rejected so a bad value never persists (mirrored in the
        // startup guard, StoredDataValidator).
        if (leaf is TextValue tv && !_desc.EnumAccepts(propDef.Type, tv.Text))
            return Error($"'{tv.Text}' is not a value of enum '{propDef.Type}'.");

        // The write floor: editing a field is an `edit` of the (existing) object, decided over its
        // CURRENT scalar fields. A denied edit throws → the `{ error }` reply rolls the client back.
        RequireWrite(Floor(req), "edit", hit.TypeName, Code.AccessFloor.ScalarObject(hit.TypeName, objectId, hit.Fields, _desc));
        // The WRITE chokepoint of the `password` type (M-auth): a `password`-typed plaintext is PBKDF2-hashed
        // here, at the WS layer (above the dumb store), before it persists — so no client write path ever
        // stores plaintext. An empty value writes NOTHING (= "no change", so a blank-on-load password field
        // re-submitted unchanged never clobbers the stored hash); HashLeaf returns null for that case.
        // A real write reports the store's UNDER-LOCK post-write version (finding 3); the empty no-op writes
        // nothing (no write-then-read split), so CurrentVersion is the accurate current HEAD.
        var newVersion = HashLeaf(hit.TypeName, prop, leaf) is { } toWrite
            ? _store.WriteField(objectId, prop, toWrite)
            : _store.CurrentVersion;

        return Serialize(new ObjectPropChangeResponse { NewVersion = newVersion });
    }

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
                var fields = ExecObjectValue(valueEl, typeDef);

                // The write floor: a create of the element/target type, decided over the NEW object's scalar
                // fields (id 0 — no identity yet), exactly as HandleAddSetMember / HandleArrayAdd.
                RequireWrite(floor, "create", typeName, Code.AccessFloor.ScalarObject(typeName, 0, fields, _desc));
                // The WRITE chokepoint (M-auth `password`): hash any password-typed plaintext BEFORE the store —
                // a staged `User` create can never skip hashing (a SECURITY must), same as every other create path.
                createBatch.Add(new CommitCreate(tempId, typeName, HashPasswordFields(typeName, fields)));
            }

        // ── edits: validate each exactly as Step A / HandleObjectPropChange (an edit of an EXISTING object).
        var mutations = new List<CommitMutation>();
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
        var childRaw = childEl.GetInt32();
        var childRef = childRaw < 0 ? childRaw : Resolve(session, childRaw); // a tempId stays; a real id remaps

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
    // the self-hosted reference editor's persist path (setRef). `refId` points at an
    // existing extent object; `value` ({ props }) mints a new object and points at it
    // (reply carries its real id); `clear` unsets. GC runs after (an orphaned target
    // is collected). Identity-addressed so it serves both a reference route and an
    // embedded reference field uniformly.
    private string HandleSetReferenceField(WsRequest req)
    {
        var session = Session(req);
        if (req.ObjectId is not { } objectIdRaw)
            return Error("setReferenceField requires a numeric 'objectId'.");
        var objectId = Resolve(session, objectIdRaw);
        if (req.Prop is not { } prop)
            return Error("setReferenceField requires 'prop'.");

        if (_store.ReadById(objectId) is not { } hit)
            return Error($"No object with id {objectId}.");
        var propDef = _desc.FindType(hit.TypeName)?.Props?.FirstOrDefault(p => p.Name == prop);
        if (propDef is null)
            return Error($"Type '{hit.TypeName}' has no field '{prop}'.");
        if (propDef.Cardinality != Cardinality.Single || !_desc.IsObjectType(propDef.Type))
            return Error($"Field '{prop}' on '{hit.TypeName}' is not a single reference.");
        var targetType = _desc.FindType(propDef.Type)!;

        // The write floor: setting/clearing a reference is an `edit` of the (existing) OWNER object, so the
        // edit verb is non-bypassable via this seam too (not just objectPropChange). A create-new pick ALSO
        // mints a target object, gated as `create` on the target type below.
        var floor = Floor(req);
        RequireWrite(floor, "edit", hit.TypeName, Code.AccessFloor.ScalarObject(hit.TypeName, objectId, hit.Fields, _desc));

        int? newId = null;
        int newVersion; // the WriteReference under-lock version — the LAST store op in every branch (finding 3)
        if (req.RefId is { } refIdRaw)
        {
            newVersion = _store.WriteReference(objectId, prop, Resolve(session, refIdRaw), targetType.Name);
        }
        else if (req.Value is { } valueEl)
        {
            RequireWrite(floor, "create", targetType.Name, CandidateFromValue(valueEl, targetType));
            // The WRITE chokepoint (M-auth `password`): hash any password-typed plaintext before the store.
            newId = _store.CreateObject(targetType.Name, HashPasswordFields(targetType.Name, ExecObjectValue(valueEl, targetType)));
            newVersion = _store.WriteReference(objectId, prop, newId, targetType.Name);
        }
        else if (req.Clear is not null)
        {
            newVersion = _store.WriteReference(objectId, prop, null, targetType.Name);
        }
        else
        {
            return Error("setReferenceField requires 'refId', 'value', or 'clear'.");
        }

        return Serialize(new SetReferenceFieldResponse { NewId = newId, NewVersion = newVersion });
    }

    // The WS's first message on open: claims the session minted at SSR (keeping it past
    // the claim window). The session carries no data — a refetch re-renders from a fresh
    // store load — so the report is informational; `sessionAlive: false` just means the
    // hello arrived past the window.
    private string HandleHello(WsRequest req)
    {
        var alive = Session(req) != null;
        return Serialize(new HelloResponse { SessionAlive = alive });
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
        var state = new SsrRenderer(_store, _desc, registry: _registry)
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

    // A new set member built on the client (its negative id is transient): mint a real
    // object into the extent, link it into the set, and echo the negative→real id mapping
    // so the client can re-key its optimistic copy.
    private string HandleArrayAdd(WsRequest req)
    {
        var session = Session(req);
        if (req.SetId is not { } setIdRaw)
            return Error("arrayAdd requires a numeric 'setId'.");
        var setId = Resolve(session, setIdRaw);
        if (req.TypeName is not { } typeName)
            return Error("arrayAdd requires 'typeName'.");
        if (_desc.FindType(typeName) is not { } typeDef)
            return Error($"Unknown type '{typeName}'.");

        // The target set must exist and must declare exactly this element type.
        var elementType = _store.SetElementType(setId);
        if (elementType is null)
            return Error($"No set with id {setId}.");
        if (elementType != typeName)
            return Error($"Set {setId} holds '{elementType}' members, not '{typeName}'.");

        var value = req.Value is { } valEl
            ? ExecObjectValue(valEl, typeDef)
            : new ObjectValue(new Dictionary<string, NodeValue>());

        // The write floor: adding a set member is a `create` of the element type, decided over the NEW
        // object's scalar fields (so `where object.status == "draft"` reads the new data). Rejected BEFORE
        // minting, so a denied create leaves no orphan object in the extent. id 0 = no identity yet.
        RequireWrite(Floor(req), "create", typeName, Code.AccessFloor.ScalarObject(typeName, 0, value, _desc));

        // The WRITE chokepoint (M-auth `password`): hash any password-typed plaintext before the store.
        var id = _store.CreateObject(typeName, HashPasswordFields(typeName, value));
        // AddToSet is the LAST mutation; its under-lock version is the reply's newVersion (finding 3). The
        // ReadById below is a pure read (no bump), so this stays the post-write version at reply time.
        var newVersion = _store.AddToSet(setId, id);

        // Record the negative→real mapping so the client's follow-up ops (a field edit, a remove) that
        // still address this object by its transient id resolve to the real one — even if they arrive
        // before the client has applied this reply's remap. (No tempId → an add that needs no remap.)
        if (req.TempId is { } tempId)
            session?.MapTransientId(tempId, id);

        // The store minted the new object's collection props with their own intrinsic
        // ids; echo them so the client re-keys its transient arrays (else later adds
        // into them would silently not persist).
        var minted = _store.ReadById(id);
        var collections = new Dictionary<string, CollectionInfo>();
        foreach (var prop in typeDef.Props ?? [])
            if (prop.Cardinality == Cardinality.Set
                && minted?.Fields.Fields.GetValueOrDefault(prop.Name) is SetValue sv)
                collections[prop.Name] = new CollectionInfo { Id = sv.Id, ElementTypeName = prop.Type };

        // `newId`, not `id` — the reply's `id` slot is the request correlation id.
        // tempId is echoed only when the request carried one (omitted otherwise).
        return Serialize(new ArrayAddResponse
            { NewId = id, Collections = collections, TempId = req.TempId, NewVersion = newVersion });
    }

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

    private string HandleArrayRemove(WsRequest req)
    {
        var session = Session(req);
        if (req.SetId is not { } setIdRaw)
            return Error("arrayRemove requires a numeric 'setId'.");
        if (req.ObjectId is not { } objectIdRaw)
            return Error("arrayRemove requires a numeric 'objectId'.");
        var setId = Resolve(session, setIdRaw);
        var objectId = Resolve(session, objectIdRaw);

        if (_store.SetElementType(setId) is null)
            return Error($"No set with id {setId}.");

        // The write floor: removing a set member is a `delete` of that member, decided over its current
        // scalar fields. Rejected BEFORE removal (and its GC), so a denied delete leaves the object intact.
        if (_store.ReadById(objectId) is { } member)
            RequireWrite(Floor(req), "delete", member.TypeName,
                Code.AccessFloor.ScalarObject(member.TypeName, objectId, member.Fields, _desc));
        // Report the store's under-lock post-write version (finding 3), never a separate CurrentVersion read.
        var newVersion = _store.RemoveFromSet(setId, objectId);

        return Serialize(new ArrayRemoveResponse { NewVersion = newVersion });
    }

    // A new object's scalar props as the client ships them ({ "props": { name: leaf } }),
    // validated against the declared type: unknown or non-scalar fields are rejected,
    // and each value must fit its prop's declared base type.
    private ObjectValue ExecObjectValue(JsonElement el, TypeDefinition type)
    {
        var fields = new Dictionary<string, NodeValue>();
        if (el.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject())
            {
                var propDef = type.Props?.FirstOrDefault(d => d.Name == p.Name)
                    ?? throw new InvalidOperationException($"Type '{type.Name}' has no field '{p.Name}'.");
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
