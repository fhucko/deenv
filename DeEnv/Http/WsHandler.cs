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
}

// Response records — one per op. Each property's camelCase (the shared `_jsonOpts`
// PropertyNamingPolicy) is the wire key, so these serialize to the exact bytes the old
// JsonObject literal produced; the correlation id is still appended last by WithId, so
// these never carry it. Field order matches the former literals; the get-only computed
// `Op`/`Ok` props serialize by default.

public sealed record WriteResponse
{
    public string Op => "write";
    public required string Path { get; init; }
    public bool Ok => true;
}

public sealed record AddEntryResponse
{
    public string Op => "addEntry";
    public required string Path { get; init; }
    public bool Ok => true;
    public required string Key { get; init; }
}

public sealed record RemoveEntryResponse
{
    public string Op => "removeEntry";
    public required string Path { get; init; }
    public bool Ok => true;
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
}

public sealed record SetReferenceFieldResponse
{
    public string Op => "setReferenceField";
    public bool Ok => true;
    // Present only when a new object was minted (a create-new pick), never for link/clear —
    // omitted when null by the options' DefaultIgnoreCondition (WhenWritingNull).
    public int? NewId { get; init; }
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
}

public sealed record ArrayRemoveResponse
{
    public string Op => "arrayRemove";
    public bool Ok => true;
}

public sealed record RefetchResponse
{
    public string Op => "refetch";
    // The raw client-state node from RenderState; serialized inline, not reshaped.
    public required JsonNode State { get; init; }
}

public sealed record HostActionResponse
{
    public string Op => "hostAction";
    public bool Ok => true;
}

public sealed record AckRemapResponse
{
    public string Op => "ackRemap";
    public bool Ok => true;
}

public sealed record ErrorResponse
{
    public required string Error { get; init; }
}

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
        LiveRegistry? registry = null, IHostActions? hostActions = null, string mountBase = "/")
    {
        _store = store;
        _desc = desc;
        _resolver = new TypeResolver(desc);
        _sessions = sessions;
        _registry = registry ?? new LiveRegistry();
        _hostActions = hostActions ?? new NoHostActions();
        _mountBase = mountBase;
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
    // ponytail: this slice gates the OBJECT-graph write seams (set-member create/delete, object-field +
    // reference edit) — exactly the surface the read floor (DbBridge) gates. The DICTIONARY/leaf-path write
    // seams (addEntry/removeEntry on a dict, the path-addressed `write`) are NOT gated yet, matching the
    // read floor's own dict gap; they layer on additively when dict reads are gated. Per-field rules and
    // richer condition inputs (now/client/cross-row) are later slices too.
    private Code.AccessFloor Floor(WsRequest req) =>
        new(_desc.Rules ?? [], Code.AccessFloor.LoadPrincipal(_store, Session(req)?.PrincipalUserId));

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
        Code.AccessFloor.ScalarObject(type.Name, 0, (ObjectValue)DeserializeValue(value, type));

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

            var result = op switch
            {
                "write"             => HandleWrite(path, pathStr, req),
                "addEntry"          => HandleAddEntry(path, pathStr, req),
                "removeEntry"       => HandleRemoveEntry(path, pathStr, req),
                "hello"             => HandleHello(req),
                "objectPropChange"  => HandleObjectPropChange(req),
                "setReferenceField" => HandleSetReferenceField(req),
                "arrayAdd"          => HandleArrayAdd(req),
                "arrayRemove"       => HandleArrayRemove(req),
                "refetch"           => HandleRefetch(pathStr, req),
                "hostAction"        => HandleHostAction(req),
                "ackRemap"          => HandleAckRemap(req),
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

        // A SCALAR dictionary entry's value lives at its path but is addressed by (dict, key)
        // — WriteLeaf can't walk into a dict, so upsert the entry. (An OBJECT entry's field
        // path, e.g. /customers/42/name, has an object parent and writes through WriteLeaf.)
        if (!path.IsRoot
            && _resolver.ResolveType(NodePath.FromSegments(path.Segments.Take(path.Segments.Count - 1)))
               is { Cardinality: Cardinality.Dictionary } parentInfo)
        {
            var dictPath = NodePath.FromSegments(path.Segments.Take(path.Segments.Count - 1));
            _store.WriteDictionaryEntry(dictPath, ParseKey(path.Segments[^1], parentInfo.KeyTypeName ?? "text"), value);
        }
        else
        {
            _store.WriteLeaf(path, value);
        }

        return Serialize(new WriteResponse { Path = pathStr });
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

        var value = DeserializeValue(valueEl, typeInfo.Type);
        var key = ParseKey(keyStr, typeInfo.KeyTypeName ?? "text");
        _store.CreateEntry(path, key, value); // throws on duplicate → caught as { error }

        return Serialize(new AddEntryResponse { Path = pathStr, Key = KeyString(key) });
    }

    // ── removeEntry ────────────────────────────────────────────────────────────

    private string HandleRemoveEntry(NodePath path, string pathStr, WsRequest req)
    {
        var typeInfo = _resolver.ResolveType(path);
        if (typeInfo == null)
            return Error($"Path '{pathStr}' does not resolve.");
        if (req.Key is not { } keyStr)
            return Error("Missing 'key' in removeEntry message.");

        if (typeInfo.Cardinality == Cardinality.Set)
        {
            if (!int.TryParse(keyStr, out var memberId))
                return Error("Set member key must be an integer identity.");
            // The write floor: removing a set member is a `delete` of that member (same as arrayRemove).
            if (_store.ReadById(memberId) is { } member)
                RequireWrite(Floor(req), "delete", member.TypeName,
                    Code.AccessFloor.ScalarObject(member.TypeName, memberId, member.Fields));
            _store.RemoveFromSet(path, memberId);
        }
        else if (typeInfo.Cardinality == Cardinality.Dictionary)
        {
            // ponytail: dictionary-entry delete is NOT gated yet — the read floor doesn't gate dict reads
            // either, so the write floor matches that staging (gated when dict reads are).
            _store.RemoveDictionaryEntry(path, ParseKey(keyStr, typeInfo.KeyTypeName ?? "text"));
        }
        else
        {
            return Error($"Path '{pathStr}' is not a dictionary or set.");
        }

        return Serialize(new RemoveEntryResponse { Path = pathStr });
    }

    // ── set members + references (object model) ─────────────────────────────────

    private string HandleAddSetMember(NodePath path, string pathStr, ResolvedTypeInfo typeInfo, WsRequest req)
    {
        // The write floor: adding a set member is a `create` of the element type — whether minting a new
        // object (decided over the new value) or linking an existing one (decided over its current fields).
        var floor = Floor(req);
        var typeName = typeInfo.Type.Name;

        int id;
        if (req.RefId is { } refId)
        {
            if (_store.ReadById(refId) is { } linked)
                RequireWrite(floor, "create", typeName, Code.AccessFloor.ScalarObject(typeName, refId, linked.Fields));
            id = refId;
            _store.AddToSet(path, id); // link an existing object
        }
        else if (req.Value is { } valueEl)
        {
            var obj = (ObjectValue)DeserializeValue(valueEl, typeInfo.Type);
            RequireWrite(floor, "create", typeName, Code.AccessFloor.ScalarObject(typeName, 0, obj));
            id = _store.CreateObject(typeName, obj);            // mint a new object…
            _store.AddToSet(path, id);                          // …then link it
        }
        else
        {
            return Error("addEntry on a set requires 'refId' (existing) or 'value' (new).");
        }

        return Serialize(new AddEntryResponse { Path = pathStr, Key = id.ToString() });
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
        RequireWrite(Floor(req), "edit", hit.TypeName, Code.AccessFloor.ScalarObject(hit.TypeName, objectId, hit.Fields));
        _store.WriteField(objectId, prop, leaf);

        return Serialize(new ObjectPropChangeResponse());
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
        RequireWrite(floor, "edit", hit.TypeName, Code.AccessFloor.ScalarObject(hit.TypeName, objectId, hit.Fields));

        int? newId = null;
        if (req.RefId is { } refIdRaw)
        {
            _store.WriteReference(objectId, prop, Resolve(session, refIdRaw), targetType.Name);
        }
        else if (req.Value is { } valueEl)
        {
            RequireWrite(floor, "create", targetType.Name, CandidateFromValue(valueEl, targetType));
            newId = _store.CreateObject(targetType.Name, ExecObjectValue(valueEl, targetType));
            _store.WriteReference(objectId, prop, newId, targetType.Name);
        }
        else if (req.Clear is not null)
        {
            _store.WriteReference(objectId, prop, null, targetType.Name);
        }
        else
        {
            return Error("setReferenceField requires 'refId', 'value', or 'clear'.");
        }

        return Serialize(new SetReferenceFieldResponse { NewId = newId });
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

        Session(req); // slide liveness; the session holds no data

        // Load the graph once from the store; object-valued vars (the client's selection)
        // resolve to the same instances the render uses, so selection-dependent data the
        // first paint never shipped gets computed.
        var db = Code.DbBridge.LoadRoot(_store, _desc, new Code.ExecContext());
        var byId = IndexObjects(db);

        var sessionVars = new Dictionary<string, Code.IExecValue>();
        if (req.Vars is { ValueKind: JsonValueKind.Object } vars)
            foreach (var v in vars.EnumerateObject())
                if (SessionVarFromWire(v.Value, byId) is { } value)
                    sessionVars[v.Name] = value;

        // Transients mint below the client's id floor (no collisions with its local drafts).
        var lastId = req.LastId ?? 0;
        // The refetch renderer gets the SAME live registry provider as the SSR path, so a refetch
        // re-render reflects the kernel's current instances — no stale `instances` list.
        var state = new SsrRenderer(_store, _desc, registry: _registry).RenderState(pathStr, sessionVars, db, lastId);
        return Serialize(new RefetchResponse { State = state });
    }

    // A server-authoritative host action (the sys.publish channel): the server alone runs the
    // effect (the client staged nothing). Read the action name + raw evaluated args and run them
    // through the IHostActions seam; on success reply ok. NOT journaled and it does NOT touch the
    // optimistic IInstanceStore ops — a host action is a devops effect outside the data model. A
    // failure (unknown action, bad arg, invalid design, unknown target) throws and ProcessMessage's
    // catch returns it as `{ error }`, which the client surfaces as lastError (no journal replay).
    private string HandleHostAction(WsRequest req)
    {
        if (req.Action is not { } action)
            return Error("hostAction requires a string 'action'.");
        var args = req.Args ?? default;

        _hostActions.Run(action, args); // throws on failure → caught as { error }

        return Serialize(new HostActionResponse());
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
        RequireWrite(Floor(req), "create", typeName, Code.AccessFloor.ScalarObject(typeName, 0, value));

        var id = _store.CreateObject(typeName, value);
        _store.AddToSet(setId, id);

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
        return Serialize(new ArrayAddResponse { NewId = id, Collections = collections, TempId = req.TempId });
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
                Code.AccessFloor.ScalarObject(member.TypeName, objectId, member.Fields));
        _store.RemoveFromSet(setId, objectId);

        return Serialize(new ArrayRemoveResponse());
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
