using System.Globalization;
using System.Text.Json;
using DeEnv.Designer;
using DeEnv.Instance;

namespace DeEnv.Storage;

// JSON-file store for the object-graph model, manipulated through a TYPED model
// (StoreModel.cs). One uniform on-disk format: every value is a tagged object.
//
//   { "extents": { "<Type>": { "<id>": { "type":"object","typeName":T,"id":N,"fields":{…} } } },
//     "root":    { "type":"object","typeName":"Db","id":1 }, "nextId": N }   // or a scalar root
//
// Value forms (the `type` discriminator is a fixed structural word):
//   scalar            { "type":"text", "value":"Ada" }            // no identity
//   object reference  { "type":"object", "typeName":T, "id":N }   // points into an extent
//   set               { "type":"set", "id":N, "members": { "<id>": <object-ref> } }
//   dictionary        { "type":"dictionary", "id":N, "entries": { "<key>": <value> } }
//
// An object's fields exist ONLY in its extent entry (the single source of truth); every
// object value held in a field/member/entry is the id-only reference form. Internally the
// document is the closed StoredValue union, so a generic walk (notably the GC) pattern-
// matches on node kind and never reads a user field key named "type" as a tag.
public sealed class JsonFileInstanceStore : IInstanceStore
{
    private readonly string _filePath;
    private readonly InstanceDescription _desc;
    private readonly TypeResolver _resolver;
    // Shared, read-only after first use (the recommended JsonSerializerOptions pattern): both the
    // instance load/save and the static migrate/load/save helpers serialize through it.
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        // CLR PascalCase props → camelCase JSON (the on-disk keys: extents/root/nextId,
        // typeName/id/fields). Dictionary keys (Extents = type names, Fields = user field
        // names) are NOT renamed — DictionaryKeyPolicy is deliberately unset.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new StoredValueConverter(), new LogWriteConverter() },
    };

    // A ONE-LINE-per-entry variant of Opts (same naming policy + converters, WriteIndented off) for the
    // JSONL log: an indented (multi-line) LogEntry would break the one-object-per-line format every other
    // part of this file assumes (LoadLog reads File.ReadAllLines; the torn-tail repair truncates by line).
    private static readonly JsonSerializerOptions LogLineOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new StoredValueConverter(), new LogWriteConverter() },
    };

    // The document is loaded into memory ONCE at construction and kept as the
    // authoritative copy: reads serve from it, and a mutation edits it then rewrites the
    // file (write-temp-then-move, atomic for any reader) for durability. This is safe
    // because an instance is single-process — nothing else writes the file behind our
    // back (the cross-process / real-time story is a later milestone). The lock
    // serializes operations against concurrent connections in this one process.
    private readonly object _sync = new();
    private StoreDoc _doc;

    // Per-object last-modified version (optimistic-concurrency guard — DECISIONS.md "App versioning —
    // the full design (M13 clump)"). IN-MEMORY only, deliberately not persisted directly — but the
    // documented cross-restart residual this comment used to name ("a truly-stale pre-restart base can
    // pass, because a fresh boot's map starts empty") is CLOSED as of M13 slice 1: ReconcileLogOnBoot
    // rebuilds this map from the WHOLE durable log on every boot (entry-final seq per touched object). The
    // "touched" set must match EVERY object the live BumpVersion(objectId) path stamps: field-write and
    // create targets AND set-link/unlink MEMBERS (a set op stamps its member's version and the baseVersion
    // guard checks it) — see TouchedObjectIds, which restores all three. The entry-final seq is coarser
    // than the exact live per-write bump, but safe in the direction that matters: at worst a spurious
    // stale-base rejection right after restart, never a missed clobber. Every mutating write records here
    // under the SAME _sync lock the mutation itself holds — see BumpVersion.
    private readonly Dictionary<int, int> _objectVersions = new();

    // ── the append-only changeset log (M13 slice 1) ─────────────────────────────────────────────────
    //
    // Sibling paths derived by suffix from _filePath (AppPaths.LogPathForDataPath/GenesisPathForDataPath —
    // one rule, works for a production id-dir path and a bare test temp file alike). _pending accumulates
    // this operation's LogWrites; every public mutating method calls BeginMutation() as its first
    // statement (freezes genesis, clears _pending, and — the correctness-critical part — captures the
    // version this operation STARTED at), each write site appends to _pending BEFORE overwriting the value
    // it captures as Old, and Save() (the store's one commit chokepoint — see its doc) turns it into one
    // appended LogEntry before the snapshot rewrite (WAL order: append THEN snapshot). _genesisWritten
    // caches the "genesis file already exists" check so a hot mutation path doesn't File.Exists every call.
    private readonly string _logPath;
    private readonly string _genesisPath;
    private readonly List<LogWrite> _pending = new();
    private bool _genesisWritten;

    // The store's version when the CURRENT operation began (set by BeginMutation, read by Save). Whether
    // to log an entry is decided by comparing Version to THIS, not by "is _pending non-empty": a
    // no-op removal (RemoveDictionaryEntry on an absent key; RemoveFromSet when the id was never a member)
    // still calls BumpVersion() unconditionally today — genuinely advancing the store's HEAD — while
    // recording ZERO LogWrites (there is nothing to describe; nothing was removed). Gating on _pending
    // alone would silently skip logging that version bump, breaking the very invariant this whole log
    // exists to hold ("the log's seq and the store's version are the same monotonic number" — every
    // version this store ever reaches has exactly one log entry whose seq is that version, possibly
    // carrying an EMPTY Writes list for a bump with nothing to describe).
    private int _versionAtOpStart;

    // Every public mutating method's first statement: freeze genesis, clear any stale writes from a
    // previous call (belt-and-braces — Save() already clears _pending, but a method that returns early
    // without reaching Save() must never leave one for the NEXT call to append), and record the version
    // this operation is starting from.
    private void BeginMutation()
    {
        EnsureGenesis();
        _pending.Clear();
        _versionAtOpStart = _doc.Version;
    }

    public int CurrentVersion { get { lock (_sync) return _doc.Version; } }

    // Bump the store's HEAD version and (for a real object write) record it as that object's
    // last-modified version; RETURN the post-write version. Called at the END of every mutating
    // operation's already-held _sync lock — never on its own lock, so the bump is never observably
    // separate from the write it accompanies. Its RETURN is what every mutating public method hands back
    // (and each WS handler reports as the reply's `newVersion`): reading the version this way — captured
    // INSIDE the same lock as the write — instead of a SECOND `CurrentVersion` acquisition closes the
    // missed-clobber window a concurrent commit could otherwise slip into that gap, over-counting the
    // reported version and letting the client re-pin its ctx base too high (review finding 3).
    // objectId is null for a mutation with no single "the object this changed" (Reset, a set/dict
    // structural op reached only by path — those stay ungated by baseVersion, matching today's
    // unconditional-accept live-edit behavior for everything CommitBatch does not itself route through).
    private int BumpVersion(int? objectId = null)
    {
        _doc.Version++;
        if (objectId is { } id) _objectVersions[id] = _doc.Version;
        return _doc.Version;
    }

    public JsonFileInstanceStore(string filePath, InstanceDescription desc)
    {
        _filePath = filePath;
        _desc = desc;
        _resolver = new TypeResolver(desc);
        _logPath = AppPaths.LogPathForDataPath(filePath);
        _genesisPath = AppPaths.GenesisPathForDataPath(filePath);

        if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
        {
            // Seed from the app's initialData (or an empty root) and persist it. No genesis exists yet
            // for a brand-new store (freezes on the FIRST real mutation afterward — see EnsureGenesis).
            // _versionAtOpStart pinned to match the fresh doc's OWN version (not left at the field's
            // default) so this seed Save() reads as a no-op on its own terms, not by coincidence.
            _doc = BuildInitialDoc();
            _versionAtOpStart = _doc.Version;
            Save();
        }
        else
        {
            _doc = Normalize(LoadDocFromFile());
            _genesisWritten = File.Exists(_genesisPath);
            // Pin BEFORE reconciliation may replay+bump _doc.Version — ReconcileLogOnBoot's own catch-up
            // checkpoint re-pins it again right before its Save() (see that method), so this assignment
            // only matters for the (overwhelmingly common) case where nothing needs replaying at all.
            _versionAtOpStart = _doc.Version;

            // Reconcile (replay a lagging snapshot forward) BEFORE the strict startup guard, so the guard
            // checks the CAUGHT-UP document, not a snapshot that a crash left BEHIND a schema boundary. A
            // versioned publish's boundary entry (M13 slice 4) carries the new schema's shape (renames et
            // al.); if a crash landed the entry on the log but not yet in the snapshot, the on-disk snapshot
            // is still the OLD shape — validating THAT against the now-new app document would falsely reject
            // a perfectly recoverable store (e.g. "field 'label' the app does not declare" when the boundary
            // renamed label→title). Replaying first brings the doc to the new-schema head; then the guard
            // validates that. A within-schema crash (slice 1's own scenario) is unaffected — its rolled-back
            // snapshot already matched the unchanged schema, and the caught-up doc still does.
            ReconcileLogOnBoot();

            // The startup guard: the (now caught-up) data must match the running app's types — fail loudly
            // here rather than half-work over stale data.
            StoredDataValidator.Validate(_doc, desc, filePath);
        }
    }

    // ── boot reconciliation: repair a torn tail, replay a lagging snapshot, rebuild _objectVersions ──
    //
    // Runs once, after load+Normalize and BEFORE the strict startup guard (so the guard validates the
    // CAUGHT-UP doc, not a snapshot a crash left behind a schema boundary — see the ctor's own note). A
    // crash can only ever leave the snapshot BEHIND the log (WAL order is append-then-snapshot — see
    // Save()), never ahead of it; a torn FINAL line (the process died mid-append) is tolerated and repaired
    // by truncating the file to its last complete line, since the append that produced it never got far
    // enough to be the entry whose snapshot Save() would have written next. Any OTHER unparseable line is a
    // corrupted log, not a crash artifact — loud failure, same remedy style as StoredDataValidator.
    private void ReconcileLogOnBoot()
    {
        var entries = LoadLog();
        if (entries.Count == 0) return;

        var last = entries[^1];
        if (last.Seq > _doc.Version)
        {
            // The snapshot lagged the log — replay every entry the snapshot hasn't absorbed yet, then
            // checkpoint: pin _versionAtOpStart to the CAUGHT-UP version BEFORE calling Save(), so its
            // "did Version change during this operation" gate reads "no" and appends nothing — this Save()
            // must only rewrite the snapshot to match the log it is already fully described by, never
            // create a SECOND entry duplicating a seq the log already has.
            foreach (var entry in entries.Where(e => e.Seq > _doc.Version))
                _doc = AppLogReplay.Apply(_doc, entry);
            _versionAtOpStart = _doc.Version;
            Save();
        }
        else if (last.Seq < _doc.Version)
        {
            // Impossible under the WAL order (the log always leads or matches) unless the files were
            // hand-edited or the snapshot was replaced independently of the log — loud failure rather than
            // silently trusting either side.
            throw new StoredDataException(
                $"Data file '{_filePath}' (version {_doc.Version}) is AHEAD of its own log " +
                $"'{_logPath}' (last seq {last.Seq}). The log and snapshot have gone out of sync — " +
                "restore a consistent pair from backup, or delete both to reseed.");
        }

        // Rebuild the in-memory per-object last-modified map from the WHOLE log (closes the documented
        // cross-restart residual on _objectVersions — see that field's doc). Entry-final seq for every
        // object a write in that entry touched: a batch entry stamps all its objects at the entry's ONE
        // seq rather than each write's own intra-batch bump, which is coarser than live BumpVersion but
        // SAFE in the direction that matters — at worst a spurious stale-base rejection right after
        // restart (never a missed clobber), because the recorded version can only be >= the true one.
        foreach (var entry in entries)
            foreach (var id in TouchedObjectIds(entry))
                _objectVersions[id] = entry.Seq;
    }

    // Every extent-object id a log entry's writes touched — the attribution set _objectVersions needs.
    // SetLink/SetUnlink map to their MEMBER id: live code stamps the linked member's version on every set
    // op (BumpVersion(memberId) in AddToSet/RemoveFromSet + CommitBatch's SetLinkMutation) and the
    // baseVersion guard checks it (CommitBatch's RequireFresh(memberRef)), so a boot rebuild that dropped
    // the member here would let a stale commit editing that member — rejected before a restart — be
    // ACCEPTED after one (a missed clobber across restart, the exact failure residual #2's rebuild exists
    // to prevent). DictSet/DictRemove/RootWrite stay null: those live-bump with NO objectId (a store-wide
    // HEAD bump only, never a per-object attribution — see BumpVersion's objectId==null callers), so there
    // is no member to restore for them.
    private static IEnumerable<int> TouchedObjectIds(LogEntry entry) => entry.Writes
        .Select(w => w switch
        {
            FieldWrite(var id, _, _, _) => id,
            Create(var id, _, _) => id,
            Remove(var id, _) => id,
            SetLink(_, var memberId) => memberId,
            SetUnlink(_, var memberId) => memberId,
            DictSet or DictRemove or RootWrite => (int?)null,
            _ => null,
        })
        .Where(id => id is not null)
        .Select(id => id!.Value)
        .Distinct();

    // Deserialize the file to the typed model. A malformed / garbage file (not a readable
    // document) becomes a StoredDataException — same remedy message as before.
    private StoreDoc LoadDocFromFile() => LoadRaw(_filePath);

    // Deserialize a data file to the typed model WITHOUT the startup guard (the instance ctor validates
    // separately; the static migrate pass re-validates after reconciling). A garbage / unreadable file
    // becomes a StoredDataException with the same remedy message.
    private static StoreDoc LoadRaw(string path)
    {
        try
        {
            if (JsonSerializer.Deserialize<StoreDoc>(File.ReadAllText(path), Opts) is { } doc)
                return doc;
        }
        catch (JsonException)
        {
        }
        throw new StoredDataException(
            $"Data file '{path}' is not a readable data document. " +
            "Delete or move the file to reseed it from the app's initialData.");
    }

    // ── read ────────────────────────────────────────────────────────────────────

    public NodeValue? ReadNode(NodePath path)
    {
        lock (_sync) return ReadNodeCore(path);
    }

    private NodeValue? ReadNodeCore(NodePath path)
    {
        if (path.Segments.Count > 0 && path.Segments[0] == "~") return null; // id-route → renderer

        var db = _desc.Db();
        if (db == null) return null;
        if (_doc.Root is not { } rootVal) return null;

        // Scalar Db root: the root is the value itself.
        if (rootVal is not StoredRef)
            return path.IsRoot && rootVal is StoredLeaf leaf ? leaf.Scalar : null;

        var curObj = ResolveRef((StoredRef)rootVal);
        if (curObj == null) return null;
        var curType = db;

        var segs = path.Segments;
        for (var i = 0; i < segs.Count; i++)
        {
            var prop = curType.Props?.FirstOrDefault(p => p.Name == segs[i]);
            if (prop == null) return null;
            var elemType = ResolveTypeDef(prop.Type);
            var last = i == segs.Count - 1;

            switch (prop.Cardinality)
            {
                case Cardinality.Set:
                {
                    var set = curObj.Fields.GetValueOrDefault(prop.Name) as StoredSet;
                    if (last) return BuildSetValue(set, elemType);

                    var member = set?.Members.GetValueOrDefault(ParseSeg(segs[i + 1])) as StoredRef;
                    var mo = member == null ? null : ResolveRef(member);
                    if (mo == null) return null;
                    if (i + 1 == segs.Count - 1) return BuildObject(mo, elemType);
                    curObj = mo; curType = elemType; i++; continue;
                }
                case Cardinality.Dictionary:
                {
                    var dict = curObj.Fields.GetValueOrDefault(prop.Name) as StoredDict;
                    if (last) return BuildDictionary(dict, elemType, prop.KeyType ?? "text");

                    var entry = dict?.Entries.GetValueOrDefault(segs[i + 1]);
                    if (entry == null) return null;
                    if (i + 1 == segs.Count - 1)
                        return elemType.BaseType == BaseType.Object
                            ? (entry is StoredRef er ? BuildObjectFromRef(er, elemType) : null)
                            : (entry is StoredLeaf el ? el.Scalar : null);
                    if (elemType.BaseType != BaseType.Object) return null;
                    var eo = entry is StoredRef er2 ? ResolveRef(er2) : null;
                    if (eo == null) return null;
                    curObj = eo; curType = elemType; i++; continue;
                }
                default: // Single
                {
                    if (_desc.IsObjectType(prop.Type))
                    {
                        // A single object-typed prop is a reference.
                        if (curObj.Fields.GetValueOrDefault(prop.Name) is not StoredRef refVal)
                            return last ? new ReferenceValue(null, prop.Type) : null;
                        var ro = ResolveRef(refVal);
                        if (ro == null) return last ? new ReferenceValue(null, prop.Type) : null;
                        if (last) return BuildObject(ro, elemType);
                        curObj = ro; curType = elemType; continue;
                    }

                    if (!last) return null;
                    return curObj.Fields.GetValueOrDefault(prop.Name) is StoredLeaf sl
                        ? sl.Scalar
                        : DefaultBase(elemType.BaseType);
                }
            }
        }

        return BuildObject(curObj, curType);
    }

    private ObjectValue BuildObject(StoredObject obj, TypeDefinition type)
    {
        var map = new Dictionary<string, NodeValue>();
        foreach (var prop in type.Props ?? [])
        {
            var elemType = ResolveTypeDef(prop.Type);
            switch (prop.Cardinality)
            {
                case Cardinality.Set:
                    map[prop.Name] = BuildSetValue(obj.Fields.GetValueOrDefault(prop.Name) as StoredSet, elemType);
                    break;
                case Cardinality.Dictionary:
                    map[prop.Name] = BuildDictionary(obj.Fields.GetValueOrDefault(prop.Name) as StoredDict, elemType, prop.KeyType ?? "text");
                    break;
                default:
                    if (_desc.IsObjectType(prop.Type))
                        map[prop.Name] = new ReferenceValue((obj.Fields.GetValueOrDefault(prop.Name) as StoredRef)?.Id, prop.Type);
                    else
                        map[prop.Name] = obj.Fields.GetValueOrDefault(prop.Name) is StoredLeaf sl
                            ? sl.Scalar
                            : DefaultBase(elemType.BaseType);
                    break;
            }
        }
        return new ObjectValue(map);
    }

    private ObjectValue? BuildObjectFromRef(StoredRef objRef, TypeDefinition type)
    {
        var o = ResolveRef(objRef);
        return o == null ? null : BuildObject(o, type);
    }

    private SetValue BuildSetValue(StoredSet? set, TypeDefinition elemType)
    {
        var members = new Dictionary<int, NodeValue>();
        if (set != null)
            foreach (var (k, v) in set.Members)
                if (v is StoredRef objRef && BuildObjectFromRef(objRef, elemType) is { } obj)
                    members[k] = obj;
        return new SetValue(set?.Id ?? 0, members);
    }

    private DictionaryValue BuildDictionary(StoredDict? dict, TypeDefinition elemType, string keyType)
    {
        var entries = new Dictionary<NodeValue, NodeValue>();
        if (dict != null)
            foreach (var (k, v) in dict.Entries)
            {
                NodeValue? val = elemType.BaseType == BaseType.Object
                    ? (v is StoredRef objRef ? BuildObjectFromRef(objRef, elemType) : null)
                    : (v is StoredLeaf leaf ? leaf.Scalar : null);
                if (val != null) entries[ParseKey(k, keyType)] = val;
            }
        return new DictionaryValue(dict?.Id ?? 0, entries);
    }

    // ── write ─────────────────────────────────────────────────────────────────
    //
    // Every write site below follows the same pattern: BeginMutation() first (freeze genesis the very
    // first time, clear _pending, capture the starting version — see its own doc), then
    // RecordFieldWrite/RecordCreate/etc. capture Old BEFORE the mutation overwrites it.

    // Capture a field write into _pending: reads the CURRENT value as Old before the caller overwrites it.
    // The single chokepoint every scalar-field write (leaf write, object-form save, ref set/clear) funnels
    // through, so the log's FieldWrite shape is defined in exactly one place.
    private void RecordFieldWrite(StoredObject obj, string prop, StoredValue? @new) =>
        _pending.Add(new FieldWrite(obj.Id, prop, obj.Fields.GetValueOrDefault(prop), @new));

    public int WriteLeaf(NodePath path, NodeValue value)
    {
        lock (_sync) return WriteLeafCore(path, value);
    }

    private int WriteLeafCore(NodePath path, NodeValue value)
    {
        BeginMutation();
        if (path.IsRoot)
        {
            _pending.Add(new RootWrite(_doc.Root, new StoredLeaf(value)));
            _doc.Root = new StoredLeaf(value); // scalar Db root
            var v = BumpVersion();
            Save();
            return v;
        }
        var parent = WalkToObject(ParentPath(path))
            ?? throw new InvalidOperationException($"Parent of {path} is not an object.");
        RecordFieldWrite(parent.Object, path.Segments[^1], new StoredLeaf(value));
        parent.Object.Fields[path.Segments[^1]] = new StoredLeaf(value);
        var ver = BumpVersion(parent.Object.Id);
        Save();
        return ver;
    }

    public int WriteObject(NodePath path, ObjectValue value)
    {
        lock (_sync) return WriteObjectCore(path, value);
    }

    private int WriteObjectCore(NodePath path, ObjectValue value)
    {
        BeginMutation();
        var target = WalkToObject(path)
            ?? throw new InvalidOperationException($"Path {path} is not a writable object.");

        foreach (var prop in target.Type.Props ?? [])
            if (prop.Cardinality == Cardinality.Single && !_desc.IsObjectType(prop.Type)
                && value.Fields.TryGetValue(prop.Name, out var v))
            {
                RecordFieldWrite(target.Object, prop.Name, new StoredLeaf(v));
                target.Object.Fields[prop.Name] = new StoredLeaf(v);
            }

        var ver = BumpVersion(target.Object.Id);
        Save();
        return ver;
    }

    public int WriteField(int objectId, string prop, NodeValue value)
    {
        lock (_sync)
        {
            BeginMutation();
            var entry = ExtentEntryById(objectId)
                ?? throw new InvalidOperationException($"No object with id {objectId}.");
            RecordFieldWrite(entry, prop, new StoredLeaf(value));
            entry.Fields[prop] = new StoredLeaf(value);
            var ver = BumpVersion(objectId);
            Save();
            return ver;
        }
    }

    public void WriteFieldBatch(IReadOnlyList<(int ObjectId, string Prop, NodeValue Value)> edits)
    {
        lock (_sync)
        {
            BeginMutation();
            foreach (var (objectId, prop, value) in edits)
            {
                var entry = ExtentEntryById(objectId)
                    ?? throw new InvalidOperationException($"No object with id {objectId}.");
                RecordFieldWrite(entry, prop, new StoredLeaf(value));
                entry.Fields[prop] = new StoredLeaf(value);
                BumpVersion(objectId);
            }
            Save(); // one atomic file write for the whole batch
        }
    }

    // Apply a whole changeset (atomic-commit Step B) — mint creates, then link + write — under ONE held lock
    // + ONE Save(), so the batch persists all-or-none. Mints first (so the tempId→realId map exists), then
    // applies each mutation with its object refs remapped (a negative tempId → its just-minted real id; a
    // positive id passes through). No GC (a commit only ADDS — it can orphan nothing). The mechanical apply;
    // the caller (WsHandler.HandleCommit) has already validated every create/edit against the schema + floor
    // + password hash. A reference to a tempId not in this batch's creates throws — a caller bug, never
    // silent. Returns each create's real id + its minted nested-collection ids (for the client re-key).
    //
    // baseVersion (optimistic-concurrency anti-clobber — see the interface doc): checked + applied in this
    // SAME held _sync lock, so the check can never race a concurrent commit's apply. Runs BEFORE any
    // mutation (alongside the existing reference pre-validation), so a rejected commit leaves the store
    // fully untouched — matching the all-or-none guarantee this method already gives a malformed batch.
    public CommitResult CommitBatch(
        IReadOnlyList<CommitCreate> creates, IReadOnlyList<CommitMutation> mutations, int? baseVersion = null)
    {
        lock (_sync)
        {
            BeginMutation();

            // PRE-VALIDATE every object reference resolves BEFORE mutating anything — a negative tempId must
            // name a create in THIS batch, a positive id must exist — so a malformed batch throws with the
            // store UNTOUCHED (no half-minted in-memory state served to the next read). This is the store's
            // own all-or-none guard, independent of the caller's validation. (The minted-id pass below cannot
            // dangle thereafter: every ref is known-good here.)
            var tempIds = creates.Select(c => c.TempId).ToHashSet();
            void RequireResolvable(int idRef)
            {
                if (idRef < 0)
                {
                    if (!tempIds.Contains(idRef))
                        throw new InvalidOperationException($"Commit references unknown temp id {idRef}.");
                }
                else if (ExtentEntryById(idRef) is null)
                    throw new InvalidOperationException($"No object with id {idRef}.");
            }
            foreach (var mutation in mutations)
                switch (mutation)
                {
                    case SetLinkMutation(var setId, var memberRef):
                        if (FindSetNode(setId) is null) throw new InvalidOperationException($"No set with id {setId}.");
                        RequireResolvable(memberRef);
                        break;
                    case RefLinkMutation(var ownerRef, _, var targetRef, _):
                        RequireResolvable(ownerRef);
                        if (targetRef is { } t) RequireResolvable(t);
                        break;
                    case FieldWriteMutation(var objectRef, _, _):
                        RequireResolvable(objectRef);
                        break;
                    case DictWriteMutation(var ownerRef, _, _, _):
                        RequireResolvable(ownerRef);
                        break;
                }

            // The FIELD-LEVEL overlap check (M13 slice 6 — DECISIONS.md / app-versioning-design.md §2 + §0's
            // baseVersion bullet). Iff baseVersion is supplied, a mutation targeting an EXISTING object that
            // has advanced past baseVersion is NOT rejected outright: we look at WHICH FIELDS this batch
            // writes vs which fields the interleaved commits (log entries with seq > baseVersion) wrote to
            // the SAME object. No field overlap → AUTO-MERGE (the disjoint edits survive on both sides — the
            // design's "disjoint interleaved commits auto-merge, no OCC retry storms"). A field overlap →
            // REJECT the WHOLE batch (all-or-none) with a per-field {base, mine, theirs} payload. Set
            // add/remove COMMUTE (a set link writes no field — never a conflict; different members never
            // collide, same-member add+remove interleave, all apply). Creates never conflict (fresh id).
            // baseVersion == null (the commitDesign/merge/clone server paths, a version-less test harness)
            // → unchanged, no check.
            if (baseVersion is { } bv)
            {
                // My field writes per existing object (field → the value I'm writing, for the `mine` cell). A
                // FieldWriteMutation and a RefLinkMutation each write ONE named field; a SetLinkMutation writes
                // no field (commutes — excluded); a DictWriteMutation can't reach here from a wire commit
                // (HandleCommit builds none) and dict conflicts would be per-(prop,key), so it is left
                // unanalyzed — commitDesign, its only source, passes no baseVersion anyway.
                var myFields = new Dictionary<int, Dictionary<string, StoredValue?>>();
                void MyWrite(int idRef, string prop, StoredValue? value)
                {
                    if (idRef < 0) return; // a fresh create has no prior version to be stale against
                    (myFields.TryGetValue(idRef, out var fs) ? fs : myFields[idRef] = new())[prop] = value;
                }
                foreach (var mutation in mutations)
                    switch (mutation)
                    {
                        case RefLinkMutation(var ownerRef, var prop, var targetRef, var targetType):
                            MyWrite(ownerRef, prop, targetRef is { } t ? new StoredRef(targetType, t) : null);
                            break;
                        case FieldWriteMutation(var objectRef, var prop, var value):
                            MyWrite(objectRef, prop, new StoredLeaf(value));
                            break;
                    }

                // Only objects that ACTUALLY advanced past the base need the (log-reading) overlap analysis —
                // a fresh-based object cannot collide. A missing _objectVersions entry = unchanged since boot
                // (see the field's doc) → not stale. This keeps the log read on the RARE stale path only.
                var staleObjects = myFields.Keys
                    .Where(id => _objectVersions.TryGetValue(id, out var v) && v > bv)
                    .ToHashSet();

                if (staleObjects.Count > 0)
                {
                    // The interleaved field writes per stale object, read from the durable LOG (base values
                    // live there — §7's old+new logging exists for exactly this). Cost: reading the whole log
                    // file once, filtered to seq > bv — O(log size), acceptable because this runs ONLY when an
                    // edited object is genuinely stale (rare); an in-memory tail is a future optimization if a
                    // stale commit under a huge log ever becomes hot. For each interleaved field we keep the
                    // FIRST write's Old (= what the draft saw at its base) and the object's TypeName.
                    var interleaved = InterleavedFieldWrites(bv, staleObjects);
                    var conflicts = new List<ConflictField>();
                    foreach (var (objectId, fields) in myFields)
                    {
                        if (!interleaved.TryGetValue(objectId, out var changed)) continue; // not stale, or no logged field write
                        var typeName = ExtentEntryById(objectId)?.TypeName ?? "";
                        foreach (var (prop, mine) in fields)
                            if (changed.TryGetValue(prop, out var baseVal)) // SAME field written on both sides = collision
                                conflicts.Add(new ConflictField(objectId, typeName, prop,
                                    LeafOrRef(baseVal),
                                    LeafOrRef(mine),
                                    LeafOrRef(ExtentEntryById(objectId)?.Fields.GetValueOrDefault(prop))));
                    }
                    if (conflicts.Count > 0)
                        // The Message reaches the client verbatim as the `{ error }` (WsHandler surfaces it for
                        // the global banner) AND is the server log line — kept ONE branch-free sentence, honest
                        // for BOTH surfaces (three-lens review, fix 3): the generic form's coarse bar carries the
                        // resolution affordances (Keep mine / Take theirs), but a CUSTOM render that never reads
                        // ctx.conflicts has no such door — telling it to "resolve the conflict" would point at
                        // nothing. Action-first instead: state what happened, that nothing was lost (the draft is
                        // still in the form — true on both surfaces), and the one action every surface supports
                        // (reload). "Someone else changed this" + lowercase "reload" are exact substrings the
                        // existing Concurrency.feature / Publish.feature assertions pin — preserved verbatim.
                        // Store UNTOUCHED — this throws BEFORE any mutation, all-or-none like a malformed batch.
                        throw new ConflictException(
                            "Someone else changed this — your edits were NOT saved (they're still in the form); " +
                            "reload to see the latest.",
                            conflicts);
                    // else: every stale object's edits are DISJOINT from what changed → fall through and apply
                    // (auto-merge). No retry, no reject.
                }
            }

            var idMap = new Dictionary<int, int>();
            var results = new List<CommitCreateResult>();

            // Mint every create first — no per-create Save; the whole batch Saves once at the end.
            foreach (var c in creates)
            {
                var realId = MintObject(c.TypeName, c.Fields);
                idMap[c.TempId] = realId;
                var minted = ExtentEntryById(realId)!;
                var type = _desc.FindType(c.TypeName)!;
                var collections = new Dictionary<string, CommitCollection>();
                foreach (var prop in type.Props ?? [])
                    if (prop.Cardinality == Cardinality.Set
                        && minted.Fields.GetValueOrDefault(prop.Name) is StoredSet sv)
                        collections[prop.Name] = new CommitCollection(sv.Id, prop.Type);
                results.Add(new CommitCreateResult(c.TempId, realId, collections));
                BumpVersion(realId); // a create's own first version (so a later stale-base check sees it if edited)
            }

            // A reference id: a positive real id passes through; a negative tempId resolves to the create it
            // introduced (a ref to an un-minted tempId is a caller bug — fail loudly, never persist a dangle).
            int ResolveRefId(int idRef) => idRef >= 0 ? idRef
                : idMap.TryGetValue(idRef, out var real) ? real
                : throw new InvalidOperationException($"Commit references unknown temp id {idRef}.");

            foreach (var mutation in mutations)
                switch (mutation)
                {
                    case SetLinkMutation(var setId, var memberRef):
                    {
                        var memberId = ResolveRefId(memberRef);
                        var set = FindSetNode(setId)
                            ?? throw new InvalidOperationException($"No set with id {setId}.");
                        var typeName = ExtentEntryById(memberId)?.TypeName
                            ?? throw new InvalidOperationException($"No object with id {memberId}.");
                        set.Members[memberId] = new StoredRef(typeName, memberId);
                        _pending.Add(new SetLink(setId, memberId));
                        BumpVersion(memberId);
                        break;
                    }
                    case RefLinkMutation(var ownerRef, var prop, var targetRef, var targetType):
                    {
                        var entry = ExtentEntryById(ResolveRefId(ownerRef))
                            ?? throw new InvalidOperationException($"No object with id {ownerRef}.");
                        if (targetRef is { } t)
                        {
                            RecordFieldWrite(entry, prop, new StoredRef(targetType, ResolveRefId(t)));
                            entry.Fields[prop] = new StoredRef(targetType, ResolveRefId(t));
                        }
                        else
                        {
                            RecordFieldWrite(entry, prop, null);
                            entry.Fields.Remove(prop);
                        }
                        BumpVersion(ResolveRefId(ownerRef));
                        break;
                    }
                    case FieldWriteMutation(var objectRef, var prop, var value):
                    {
                        var entry = ExtentEntryById(ResolveRefId(objectRef))
                            ?? throw new InvalidOperationException($"No object with id {objectRef}.");
                        RecordFieldWrite(entry, prop, new StoredLeaf(value));
                        entry.Fields[prop] = new StoredLeaf(value);
                        BumpVersion(ResolveRefId(objectRef));
                        break;
                    }
                    case DictWriteMutation(var ownerRef, var prop, var key, var value):
                    {
                        // Upsert a SCALAR dict entry on the owner's `prop` dictionary field, by id (the owner
                        // may be a fresh create just minted above, unreachable by any NodePath yet). Same
                        // DictSet log-write + owner-version bump the standalone WriteDictionaryEntry emits, so
                        // fsck/replay stay total; the value is a scalar leaf (idMap holds ints).
                        var ownerId = ResolveRefId(ownerRef);
                        var dict = EnsureDictOnEntry(ownerId, prop);
                        var keyStr = KeyToString(key);
                        var newLeaf = new StoredLeaf(value);
                        _pending.Add(new DictSet(dict.Id, keyStr, dict.Entries.GetValueOrDefault(keyStr), newLeaf));
                        dict.Entries[keyStr] = newLeaf;
                        BumpVersion(ownerId);
                        break;
                    }
                }

            Save(); // one atomic file write for the whole changeset
            // _doc.Version, read under this same lock, is the exact version this batch produced (or the
            // unchanged HEAD for an empty batch) — the reply's newVersion, never a separate CurrentVersion
            // read that a concurrent commit could over-count between the write and the read (finding 3).
            return new CommitResult(results, _doc.Version);
        }
    }

    // ── field-level conflict analysis (M13 slice 6) ──────────────────────────────────
    //
    // The interleaved field writes to `objectIds` from every log entry with seq > baseVersion (the commits
    // that landed AFTER the committing draft loaded). Result: object id → (field → the value the draft SAW at
    // its base). The base is the FIRST interleaved write's Old for that field — the value in the store at the
    // moment the draft's base was captured (the second+ interleaved write's Old is an intermediate the draft
    // never saw). Only FieldWrites count (a set link/unlink writes no field — set ops COMMUTE, never a
    // field collision; a DictSet targets a dict id, and dict conflicts don't arise from a wire commit).
    // Reads the whole log once (LoadLog) — called ONLY on the rare stale path (CommitBatch pre-filters to
    // objects that actually advanced), so the O(log size) cost is paid only when a real collision is possible.
    private Dictionary<int, Dictionary<string, StoredValue?>> InterleavedFieldWrites(int baseVersion, HashSet<int> objectIds)
    {
        var result = new Dictionary<int, Dictionary<string, StoredValue?>>();
        foreach (var entry in LoadLog())
        {
            if (entry.Seq <= baseVersion) continue; // at or before the draft's base — the draft saw it
            foreach (var write in entry.Writes)
                if (write is FieldWrite(var id, var prop, var old, _) && objectIds.Contains(id))
                {
                    var fields = result.TryGetValue(id, out var fs) ? fs : result[id] = new();
                    // FIRST interleaved write for this field wins as the base (what the draft saw); a later
                    // one's Old is an intermediate value the draft never observed.
                    if (!fields.ContainsKey(prop)) fields[prop] = old;
                }
        }
        return result;
    }

    // A stored value as the scalar NodeValue the conflict payload carries: a leaf → its scalar; a ref → the
    // target id as an int (the client displays/keeps it as an id, the shape the store already holds a single
    // reference in); null (absent field) → null. A set/dict value never reaches here (a wire commit writes
    // neither as a conflictable field).
    private static NodeValue? LeafOrRef(StoredValue? v) => v switch
    {
        StoredLeaf leaf => leaf.Scalar,
        StoredRef r => new IntValue(r.Id),
        _ => null,
    };

    // Walk to the object a path lands on (following set/dict/refs).
    private (StoredObject Object, TypeDefinition Type)? WalkToObject(NodePath path)
    {
        var db = _desc.Db();
        if (db == null || _doc.Root is not StoredRef rootRef) return null;

        var curObj = ResolveRef(rootRef);
        if (curObj == null) return null;
        var curType = db;

        var segs = path.Segments;
        for (var i = 0; i < segs.Count; i++)
        {
            var prop = curType.Props?.FirstOrDefault(p => p.Name == segs[i]);
            if (prop == null) return null;
            var elemType = ResolveTypeDef(prop.Type);

            if (prop.Cardinality == Cardinality.Set)
            {
                if (i + 1 >= segs.Count) return null;
                var member = (curObj.Fields.GetValueOrDefault(prop.Name) as StoredSet)
                    ?.Members.GetValueOrDefault(ParseSeg(segs[i + 1])) as StoredRef;
                var mo = member == null ? null : ResolveRef(member);
                if (mo == null) return null;
                curObj = mo; curType = elemType; i++; continue;
            }
            if (prop.Cardinality == Cardinality.Dictionary)
            {
                if (i + 1 >= segs.Count || elemType.BaseType != BaseType.Object) return null;
                var entry = (curObj.Fields.GetValueOrDefault(prop.Name) as StoredDict)
                    ?.Entries.GetValueOrDefault(segs[i + 1]) as StoredRef;
                var eo = entry == null ? null : ResolveRef(entry);
                if (eo == null) return null;
                curObj = eo; curType = elemType; i++; continue;
            }
            if (_desc.IsObjectType(prop.Type))
            {
                if (curObj.Fields.GetValueOrDefault(prop.Name) is not StoredRef refVal) return null;
                var ro = ResolveRef(refVal);
                if (ro == null) return null;
                curObj = ro; curType = elemType; continue;
            }
            return null; // scalar is not an object
        }
        return (curObj, curType);
    }

    // ── object-graph mutations ──────────────────────────────────────────────────

    // Returns the minted ID (not the version — see the interface doc): every WS create path links the new
    // object in with a following AddToSet/WriteReference whose returned version is the one reported.
    public int CreateObject(string typeName, ObjectValue fields)
    {
        lock (_sync)
        {
            BeginMutation();
            var id = MintObject(typeName, fields);
            BumpVersion(id);
            Save();
            return id;
        }
    }

    public int AddToSet(NodePath setPath, int id)
    {
        lock (_sync)
        {
            BeginMutation();
            var typeName = _resolver.ResolveType(setPath)?.Type.Name
                ?? throw new InvalidOperationException($"{setPath} does not resolve.");
            var set = EnsureSet(setPath);
            set.Members[id] = new StoredRef(typeName, id);
            _pending.Add(new SetLink(set.Id, id));
            var ver = BumpVersion(id);
            Save();
            return ver;
        }
    }

    public int RemoveFromSet(NodePath setPath, int id)
    {
        lock (_sync)
        {
            BeginMutation();
            if (SetNodeAt(setPath) is { } set)
            {
                _pending.Add(new SetUnlink(set.Id, id));
                set.Members.Remove(id);
            }
            CollectGarbage();
            var ver = BumpVersion(id);
            Save();
            return ver;
        }
    }

    // ── set ops by intrinsic id (a set is found by its own id, not a path) ──────────

    public int AddToSet(int setId, int objectId)
    {
        lock (_sync)
        {
            BeginMutation();
            var set = FindSetNode(setId)
                ?? throw new InvalidOperationException($"No set with id {setId}.");
            var typeName = ExtentEntryById(objectId)?.TypeName
                ?? throw new InvalidOperationException($"No object with id {objectId}.");
            set.Members[objectId] = new StoredRef(typeName, objectId);
            _pending.Add(new SetLink(setId, objectId));
            var ver = BumpVersion(objectId);
            Save();
            return ver;
        }
    }

    public int RemoveFromSet(int setId, int objectId)
    {
        lock (_sync)
        {
            BeginMutation();
            if (FindSetNode(setId) is { } set)
            {
                _pending.Add(new SetUnlink(setId, objectId));
                set.Members.Remove(objectId);
            }
            CollectGarbage();
            var ver = BumpVersion(objectId);
            Save();
            return ver;
        }
    }

    // The declared element type of the set carrying this intrinsic id, or null when
    // no set does. Lets a mutation be validated against the schema before it lands.
    public string? SetElementType(int setId)
    {
        lock (_sync)
        {
            foreach (var (typeName, pool) in _doc.Extents)
                if (_desc.FindType(typeName) is { } type)
                    foreach (var entry in pool.Values)
                        foreach (var prop in type.Props ?? [])
                            if (prop.Cardinality == Cardinality.Set
                                && entry.Fields.GetValueOrDefault(prop.Name) is StoredSet set
                                && set.Id == setId)
                                return prop.Type;
            return null;
        }
    }

    // Locate a set node by its intrinsic id (sets live in object fields, in extents).
    private StoredSet? FindSetNode(int setId)
    {
        foreach (var (_, pool) in _doc.Extents)
            foreach (var entry in pool.Values)
                foreach (var fv in entry.Fields.Values)
                    if (fv is StoredSet set && set.Id == setId)
                        return set;
        return null;
    }

    public int WriteReference(int objectId, string prop, int? targetId, string targetTypeName)
    {
        lock (_sync)
        {
            BeginMutation();
            var entry = ExtentEntryById(objectId)
                ?? throw new InvalidOperationException($"No object with id {objectId}.");
            if (targetId is null)
            {
                RecordFieldWrite(entry, prop, null);
                entry.Fields.Remove(prop);
            }
            else
            {
                RecordFieldWrite(entry, prop, new StoredRef(targetTypeName, targetId.Value));
                entry.Fields[prop] = new StoredRef(targetTypeName, targetId.Value);
            }
            CollectGarbage();
            var ver = BumpVersion(objectId);
            Save();
            return ver;
        }
    }

    public int SetReference(NodePath fieldPath, int? id)
    {
        lock (_sync)
        {
            BeginMutation();
            var parent = WalkToObject(ParentPath(fieldPath))
                ?? throw new InvalidOperationException($"Parent of {fieldPath} is not an object.");
            var field = fieldPath.Segments[^1];
            if (id is null)
            {
                RecordFieldWrite(parent.Object, field, null);
                parent.Object.Fields.Remove(field);
            }
            else
            {
                var typeName = _resolver.ResolveType(fieldPath)?.Type.Name
                    ?? throw new InvalidOperationException($"{fieldPath} does not resolve.");
                RecordFieldWrite(parent.Object, field, new StoredRef(typeName, id.Value));
                parent.Object.Fields[field] = new StoredRef(typeName, id.Value);
            }
            CollectGarbage();
            var ver = BumpVersion(parent.Object.Id);
            Save();
            return ver;
        }
    }

    public IReadOnlyDictionary<int, ObjectValue> ReadExtent(string typeName)
    {
        lock (_sync)
        {
            var map = new Dictionary<int, ObjectValue>();
            if (_doc.Extents.GetValueOrDefault(typeName) is { } pool && _desc.FindType(typeName) is { } type)
                foreach (var (id, entry) in pool)
                    map[id] = BuildObject(entry, type);
            return map;
        }
    }

    public (string TypeName, ObjectValue Fields)? ReadById(int id)
    {
        lock (_sync)
        {
            foreach (var (typeName, pool) in _doc.Extents)
                if (pool.GetValueOrDefault(id) is { } entry && _desc.FindType(typeName) is { } type)
                    return (typeName, BuildObject(entry, type));
            return null;
        }
    }

    // ── non-destructive apply: migrate a data file toward a new schema ──────────────

    // Best-effort, in-place reconciliation of an existing data file TOWARD a new schema — the apply's
    // data-carry step (non-destructive apply). It runs BEFORE the startup guard (which stays STRICT),
    // so the migrated file then passes that guard; a change a slice cannot yet carry is left as-is for
    // the apply's fit check to fall back to a reseed, and a garbage/unreadable file is left untouched.
    //
    //   • Slice 2 — removed field → drop the value: on each extent of a STILL-DECLARED type, remove
    //     stored fields the type no longer declares (the object survives; the orphaned value is pruned).
    //   • Slice 3 — scalar TYPE change → convert the value: a single leaf prop whose stored value is a
    //     leaf of a different base tag is converted to the new type (int→text "3", text "3"→int 3, …).
    //     An UNCONVERTIBLE value (text "abc"→int) is reset to the new type's default and RETURNED in the
    //     report — never silent corruption. Structural changes (leaf↔object/set/dict, a removed type's
    //     extent, a rename) are left for the apply to reseed until later slices carry them.
    // Returns the cells whose value could not be converted and were defaulted (the caller surfaces them).
    // OFFLINE pass — assumes no live writer on the file (the same single-process assumption as the rest
    // of the store); call it during apply, not against a running store.
    internal static IReadOnlyList<string> MigrateTowardSchema(string dataPath, InstanceDescription desc)
    {
        var unconvertible = new List<string>();
        StoreDoc doc;
        try { doc = LoadRaw(dataPath); }
        catch (StoredDataException) { return unconvertible; } // unreadable → leave for the caller to reseed

        var changed = false;
        foreach (var (typeName, pool) in doc.Extents)
        {
            if (desc.FindType(typeName) is not { } type) continue; // removed type → leave (→ reseed)
            var props = (type.Props ?? []).ToDictionary(p => p.Name);

            foreach (var (id, obj) in pool)
                foreach (var name in obj.Fields.Keys.ToList())
                {
                    // Removed field → drop the value (slice 2).
                    if (!props.TryGetValue(name, out var prop))
                    {
                        obj.Fields.Remove(name);
                        changed = true;
                        continue;
                    }

                    // Scalar type change → convert (slice 3). Only a single leaf prop whose stored value
                    // is a leaf of a different base tag; a structural change is left for the apply to reseed.
                    if (prop.Cardinality == Cardinality.Single
                        && !desc.IsObjectType(prop.Type)
                        && obj.Fields[name] is StoredLeaf leaf
                        && ScalarTag(leaf.Scalar) != LeafTag(prop.Type, desc)
                        && !IsUnsetOptionalLeaf(leaf.Scalar, prop.Type, desc))
                    {
                        var converted = ConvertScalar(leaf.Scalar, prop.Type, desc);
                        obj.Fields[name] = new StoredLeaf(converted ?? DefaultBase(LeafBase(prop.Type, desc)));
                        if (converted is null) unconvertible.Add($"{typeName}/{id}.{name}");
                        changed = true;
                    }
                    // Cardinality change → reshape the stored value (same-name). single object ref -> set:
                    // wrap the one reference into a fresh one-member set (lossless one -> many). The reverse
                    // (set -> single) and dictionary combos are left for the apply to reseed.
                    else if (prop.Cardinality == Cardinality.Set
                             && desc.IsObjectType(prop.Type)
                             && obj.Fields[name] is StoredRef objRef)
                    {
                        obj.Fields[name] = new StoredSet(MintCollectionId(doc),
                            new Dictionary<int, StoredValue> { [objRef.Id] = objRef });
                        changed = true;
                    }
                }
        }

        // ponytail: a rewritten data file re-baselines its history — history resets at schema apply until
        // slice 4's publish boundary entry (a proper migration marker in the log, replacing this delete).
        // A migrated file's PAST writes described objects under the OLD schema; nothing here can express
        // that transition as replayable LogWrites, so the safe move is to drop the log+genesis and let the
        // next real mutation re-freeze genesis from the just-migrated doc — never leave history that
        // claims to replay to a document under a schema this store no longer has. `changed == false`
        // leaves an untouched data file's siblings untouched too (nothing to re-baseline).
        if (changed)
        {
            SaveRaw(dataPath, doc);
            var logPath = AppPaths.LogPathForDataPath(dataPath);
            var genesisPath = AppPaths.GenesisPathForDataPath(dataPath);
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(genesisPath)) File.Delete(genesisPath);
        }
        return unconvertible;
    }

    // Mint a fresh intrinsic id on a doc being migrated (a reshaped collection needs one). Mirrors the
    // instance MintId: bump NextId, falling back to the max extent id for a counter-less legacy doc.
    private static int MintCollectionId(StoreDoc doc)
    {
        var basis = doc.NextId != 0 ? doc.NextId : MaxExtentId(doc);
        doc.NextId = basis + 1;
        return doc.NextId;
    }

    // ── the versioned publish boundary entry (M13 slice 4) ───────────────────────────
    //
    // Apply a Designer.DesignDiff's identity-diff ops DIRECTLY to the target's CURRENT (pre-publish) data
    // file, then append ONE new LogEntry (boundary-marked) to the target's EXISTING log — genesis
    // untouched, prior history preserved (replacing slice-1's delete-log-on-migrate re-baseline for this
    // VERSIONED path only; the unversioned hand-edit boot path — an unstamped instance's one-time
    // name-match fallback, or the M4 designer/export bridge — keeps its current WriteDocument/
    // MigrateTowardSchema re-baseline behavior). This is the ONLY apply the versioned publish path runs —
    // deliberately NOT layered on top of a separate by-name WriteDocument pass: the diff is computed BY
    // IDENTITY between the two commits, so it already covers every add/remove/convert/reshape whose id was
    // present in both commits, renamed or not (a same-id, same-name prop with a changed type/cardinality is
    // reported in Conversions/CardinalityChanges exactly like a renamed one is in Renames) — running a
    // by-name pass FIRST would be actively WRONG here: MigrateTowardSchema matches by the NEW schema's
    // names, so it would see a renamed prop's OLD name as "removed" and DROP its value before this method
    // ever got a chance to carry it across to the new name.
    //
    // OFFLINE — the instance is unmounted for the duration (its store is not live), so this loads/rewrites
    // the target's data file directly, exactly like MigrateTowardSchema. Every write is recorded as a
    // LogWrite in the SAME closed union the live store uses, so replay needs no new semantics: a prop
    // rename is a per-object drop-old-key (FieldWrite …→null) + set-new-key (FieldWrite null→…) pair; a
    // type rename is a per-object Remove+Create AT THE SAME ID into the re-keyed extent (replay's Create
    // inserts literally, at the id it names) followed by refreshing every StoredRef that pointed at the old
    // type name — a field/root ref directly (FieldWrite/RootWrite to the same id under the new type name)
    // and a SET MEMBER indirectly (SetUnlink+SetLink — replay's SetLink recomputes a member's StoredRef
    // TypeName off the CURRENT extent at apply time, so relinking after the rename's Remove+Create already
    // yields the new name with no new replay case). Order inside the entry matters and is fixed here: type
    // renames' Remove+Create pairs come FIRST, so every ref-refresh op that follows sees the re-keyed extent.
    //
    // A draft staged against an object this changeset touches is naturally caught by the EXISTING
    // baseVersion guard on its NEXT commit (CommitBatch.RequireFresh — one store-wide version bump covers
    // every object this boundary entry wrote), so no new staleness mechanism is needed for the "reload"
    // behavior — the existing "Someone else changed this — reload…" rejection already fires.
    //
    // `targetDesc` is the TARGET commit's parsed description — the schema a conversion's `ToType` resolves
    // against (an enum/password/leaf type name), via the SAME ConvertScalar/LeafTag/LeafBase helpers
    // MigrateTowardSchema already uses. The base commit's description is not needed here: every op in
    // `diff` already carries its OWN old/new declared names as plain strings.
    //
    // `dryRun` (M13 slice 4 publish preview): computes the EXACT same plan (every conversion attempted,
    // every unconvertible/unsupported cell identified) but skips the two disk-touching side effects
    // (SaveRaw + AppendLogEntry) — so a dry run changes NOTHING while still returning the real report,
    // one code path for both (no second, drifting "preview" implementation of the same conversion rules).
    internal static BoundaryApplyResult ApplyPublishBoundary(
        string dataPath, DesignDiff diff, InstanceDescription targetDesc, BoundaryMarker boundary, bool dryRun = false)
    {
        var doc = LoadRaw(dataPath);
        var writes = new List<LogWrite>();
        var startVersion = doc.Version;

        // ── type renames first (so every ref-refresh below sees the re-keyed extent) ──
        foreach (var rename in diff.TypeRenames)
        {
            if (!doc.Extents.TryGetValue(rename.FromName, out var pool)) continue; // no data of this type — nothing to carry
            var newPool = doc.Extents.TryGetValue(rename.ToName, out var existing) ? existing : new Dictionary<int, StoredObject>();
            foreach (var (id, obj) in pool.ToList())
            {
                writes.Add(new Remove(id, obj));
                var renamed = obj with { TypeName = rename.ToName };
                writes.Add(new Create(id, rename.ToName, new Dictionary<string, StoredValue>(renamed.Fields)));
                newPool[id] = renamed;
            }
            doc.Extents.Remove(rename.FromName);
            doc.Extents[rename.ToName] = newPool;
        }

        // ── prop renames: per object of the (possibly just-renamed) owning type, drop the old key + set
        //    the new one, carrying the SAME stored value across (identity — the whole point of this slice).
        foreach (var rename in diff.PropRenames)
        {
            if (!doc.Extents.TryGetValue(rename.TypeName, out var pool)) continue;
            foreach (var obj in pool.Values)
            {
                if (!obj.Fields.TryGetValue(rename.FromProp, out var value)) continue; // nothing stored under the old name
                writes.Add(new FieldWrite(obj.Id, rename.FromProp, value, null));
                writes.Add(new FieldWrite(obj.Id, rename.ToProp, null, value));
                obj.Fields.Remove(rename.FromProp);
                obj.Fields[rename.ToProp] = value;
            }
        }

        // ── scalar conversions (identity-matched — a renamed-and-retyped prop lands here under its NEW
        //    name, already relocated by the rename pass above) — same widening/narrowing rules
        //    MigrateTowardSchema uses (ConvertScalar/LeafTag/LeafBase), resolved against the TARGET's
        //    description (conv.ToType is one of ITS declared type names — enum/password/leaf alike) ──
        var unconvertibleCells = new List<string>();
        foreach (var conv in diff.Conversions)
        {
            if (!doc.Extents.TryGetValue(conv.TypeName, out var pool)) continue;
            foreach (var obj in pool.Values)
            {
                if (obj.Fields.GetValueOrDefault(conv.PropName) is not StoredLeaf leaf) continue;
                if (ScalarTag(leaf.Scalar) == LeafTag(conv.ToType, targetDesc)) continue; // already the new tag
                if (IsUnsetOptionalLeaf(leaf.Scalar, conv.ToType, targetDesc)) continue; // unset stays unset
                var converted = ConvertScalar(leaf.Scalar, conv.ToType, targetDesc);
                var newLeaf = new StoredLeaf(converted ?? DefaultBase(LeafBase(conv.ToType, targetDesc)));
                if (converted is null) unconvertibleCells.Add($"{conv.TypeName}/{obj.Id}.{conv.PropName}");
                writes.Add(new FieldWrite(obj.Id, conv.PropName, leaf, newLeaf));
                obj.Fields[conv.PropName] = newLeaf;
            }
        }

        // ── cardinality reshapes (identity-matched, same-carried semantics as MigrateTowardSchema's) ──
        // single object ref -> set is carried losslessly (wrap the one ref into a one-member set); ANY
        // OTHER reshape (set -> single, a dictionary combo) has no data-carry this slice — but it must NOT
        // leave the OLD-shaped value in place, because the new schema now declares the new shape and
        // StoredDataValidator would then throw on remount (a stored StoredRef where the schema says `set`,
        // etc.) — a 503, reported to the operator as `applied: true`. DECIDED contract (design doc's own
        // remove semantics): DROP the old value to the NEW shape's default so the store always LOADS, and
        // report it LOUDLY as a destructive drop (unsupportedReshapes → the report's cardinality item keeps
        // `unsupported: true`; the old value stays recoverable from THIS boundary entry's own old-value log
        // write — that is what history is for). Refusing would strand the operator (no relink tooling yet).
        var unsupportedReshapes = new List<string>();
        foreach (var card in diff.CardinalityChanges)
        {
            if (!doc.Extents.TryGetValue(card.TypeName, out var pool)) continue;
            foreach (var obj in pool.Values)
            {
                if (card.FromCardinality == Cardinality.Single && card.ToCardinality == Cardinality.Set
                    && obj.Fields.GetValueOrDefault(card.PropName) is StoredRef objRef)
                {
                    var setId = MintCollectionId(doc);
                    var newSet = new StoredSet(setId, new Dictionary<int, StoredValue> { [objRef.Id] = objRef });
                    writes.Add(new FieldWrite(obj.Id, card.PropName, objRef, newSet));
                    obj.Fields[card.PropName] = newSet;
                }
                else if (obj.Fields.TryGetValue(card.PropName, out var oldValue))
                {
                    // Drop the old-shaped value to the NEW shape's default (matching BuildFields: an empty
                    // Set/Dict with a fresh id, an unset single object ref = absent, a scalar single's
                    // default leaf), so the remount's startup guard passes. Logged as a FieldWrite carrying
                    // the OLD value (recoverable), and flagged as a destructive drop.
                    var newDefault = NewShapeDefault(card.PropName, card.ToCardinality, card.TypeName, targetDesc, doc);
                    writes.Add(new FieldWrite(obj.Id, card.PropName, oldValue, newDefault));
                    if (newDefault is null) obj.Fields.Remove(card.PropName);
                    else obj.Fields[card.PropName] = newDefault;
                    unsupportedReshapes.Add($"{card.TypeName}/{obj.Id}.{card.PropName}");
                }
            }
        }

        // ── removed props / types: drop the stored value (destructive — reported, still applied loudly) ──
        foreach (var rem in diff.Removes)
        {
            if (!doc.Extents.TryGetValue(rem.TypeName, out var pool)) continue;
            foreach (var obj in pool.Values)
            {
                if (!obj.Fields.TryGetValue(rem.PropName, out var old)) continue;
                writes.Add(new FieldWrite(obj.Id, rem.PropName, old, null));
                obj.Fields.Remove(rem.PropName);
            }
        }
        foreach (var typeRem in diff.TypeRemoves)
        {
            if (!doc.Extents.TryGetValue(typeRem.TypeName, out var pool)) continue;
            foreach (var (id, obj) in pool.ToList())
                writes.Add(new Remove(id, obj));
            doc.Extents.Remove(typeRem.TypeName);
        }

        // ── refresh every StoredRef.TypeName that pointed at a renamed type — root/field refs directly,
        //    set members via SetUnlink+SetLink (replay recomputes TypeName off the NOW-re-keyed extent) ──
        var renameMap = diff.TypeRenames.ToDictionary(r => r.FromName, r => r.ToName);
        if (renameMap.Count > 0)
        {
            if (doc.Root is StoredRef rootRef && renameMap.TryGetValue(rootRef.TypeName, out var newRootType))
            {
                var newRoot = rootRef with { TypeName = newRootType };
                writes.Add(new RootWrite(rootRef, newRoot));
                doc.Root = newRoot;
            }

            foreach (var pool in doc.Extents.Values)
                foreach (var obj in pool.Values)
                    foreach (var name in obj.Fields.Keys.ToList())
                        switch (obj.Fields[name])
                        {
                            case StoredRef fieldRef when renameMap.TryGetValue(fieldRef.TypeName, out var newType):
                            {
                                var newRef = fieldRef with { TypeName = newType };
                                writes.Add(new FieldWrite(obj.Id, name, fieldRef, newRef));
                                obj.Fields[name] = newRef;
                                break;
                            }
                            case StoredSet set:
                                foreach (var memberId in set.Members.Keys.ToList())
                                    if (set.Members[memberId] is StoredRef memberRef
                                        && renameMap.ContainsKey(memberRef.TypeName))
                                    {
                                        writes.Add(new SetUnlink(set.Id, memberId));
                                        writes.Add(new SetLink(set.Id, memberId));
                                        // The live in-memory value is refreshed directly (replay derives it
                                        // from the extent — this keeps the in-memory doc consistent with
                                        // what replay would independently reconstruct).
                                        set.Members[memberId] = memberRef with { TypeName = renameMap[memberRef.TypeName] };
                                    }
                                break;
                            case StoredDict dict:
                                foreach (var key in dict.Entries.Keys.ToList())
                                    if (dict.Entries[key] is StoredRef entryRef
                                        && renameMap.TryGetValue(entryRef.TypeName, out var newEntryType))
                                    {
                                        var newEntryRef = entryRef with { TypeName = newEntryType };
                                        writes.Add(new DictSet(dict.Id, key, entryRef, newEntryRef));
                                        dict.Entries[key] = newEntryRef;
                                    }
                                break;
                        }
        }

        // Nothing to carry (an empty diff, or every op found no data to touch) — leave the target's files
        // and history completely untouched. A caller with a genuinely empty diff should not call this at
        // all, but staying a no-op here keeps the method honest either way.
        if (writes.Count == 0 || dryRun) return new BoundaryApplyResult(writes.Count > 0, unconvertibleCells, unsupportedReshapes);

        // WAL ORDER (the slice-1 law): append the log entry FIRST, THEN rewrite the snapshot — the SAME
        // fixed order the live Save() uses (append THEN SaveRaw), never the inverse. A crash BETWEEN the two
        // must leave the snapshot BEHIND the log (so ReconcileLogOnBoot replays the tail forward and serves
        // the post-publish state), never AHEAD of it (which ReconcileLogOnBoot rejects with a loud
        // StoredDataException — "snapshot is AHEAD of its own log" — bricking the published instance). So
        // the entry has to be on disk before the snapshot that describes the same version is.
        doc.Version = startVersion + 1;
        var (who, msgId) = StoreWriteContext.Get();
        var entry = new LogEntry(doc.Version, DateTimeOffset.UtcNow, who, msgId, doc.NextId, writes, boundary);
        AppendLogEntry(AppPaths.LogPathForDataPath(dataPath), entry);
        SaveRaw(dataPath, doc);

        return new BoundaryApplyResult(true, unconvertibleCells, unsupportedReshapes);
    }

    // The value a freshly-created object would carry for `propName` under its NEW cardinality (mirrors
    // BuildFields exactly): an empty StoredSet/StoredDict (each with a fresh minted id), null for an unset
    // single OBJECT reference (the caller REMOVES the key — an unset single ref is absent), or the scalar
    // default leaf for a single scalar. Used when a boundary apply must drop an un-carriable reshape's
    // old value to the new shape so the remount's startup guard passes (fix 2).
    private static StoredValue? NewShapeDefault(
        string propName, Cardinality toCardinality, string typeName, InstanceDescription targetDesc, StoreDoc doc)
    {
        if (toCardinality == Cardinality.Set) return new StoredSet(MintCollectionId(doc), new());
        if (toCardinality == Cardinality.Dictionary) return new StoredDict(MintCollectionId(doc), new());
        // Single: an object ref is absent (null → the caller removes the key); a scalar gets its default.
        var propType = targetDesc.FindType(typeName)?.Props?.FirstOrDefault(p => p.Name == propName)?.Type;
        if (propType is null || targetDesc.IsObjectType(propType)) return null;
        return new StoredLeaf(DefaultBase(LeafBase(propType, targetDesc)));
    }

    private static int MaxExtentId(StoreDoc doc)
    {
        var max = 0;
        foreach (var pool in doc.Extents.Values)
            foreach (var id in pool.Keys)
                if (id > max) max = id;
        return max;
    }

    // ── scalar conversion (type-change migration) ───────────────────────────────

    private static string ScalarTag(NodeValue scalar) => scalar switch
    {
        BoolValue => "bool", IntValue => "int", DecimalValue => "decimal",
        TextValue => "text", DateValue => "date", DateTimeValue => "datetime",
        _ => scalar.GetType().Name,
    };

    // The BaseType a leaf prop's stored value carries (an enum stores as text → BaseType.Enum, whose
    // DefaultBase is empty text), and the on-disk tag that base uses (enum → "text").
    private static BaseType LeafBase(string typeName, InstanceDescription desc) =>
        BaseTypes.IsName(typeName) ? BaseTypes.Parse(typeName)
        : desc.FindType(typeName)?.BaseType ?? BaseType.Text;

    private static string LeafTag(string typeName, InstanceDescription desc)
    {
        var b = LeafBase(typeName, desc);
        // Enum and Password both store on disk as text (an enum value name; a password's plaintext
        // hash — see BaseType.Password), so their tag is "text".
        return b is BaseType.Enum or BaseType.Password ? "text" : b.ToString().ToLowerInvariant();
    }

    // An unset optional decimal/date/datetime is stored as an empty-text leaf (the validator's canonical
    // "unset" form — those typed values have no "empty"). It is NOT a value to convert: an empty text
    // already IS the unset form for the new type, so leave it untouched (mirrors StoredDataValidator's
    // optional-empty rule), rather than default-and-report it as unconvertible.
    private static bool IsUnsetOptionalLeaf(NodeValue scalar, string typeName, InstanceDescription desc) =>
        scalar is TextValue { Text: "" }
        && LeafBase(typeName, desc) is BaseType.Decimal or BaseType.Date or BaseType.DateTime;

    // Convert a scalar to a leaf type, or null when the value cannot be represented (the caller defaults
    // and reports it — never silent corruption). Widening (int→decimal, anything→text) is lossless;
    // narrowing parses (text→int, decimal→int when whole/in range) and yields null when it cannot.
    private static NodeValue? ConvertScalar(NodeValue from, string toTypeName, InstanceDescription desc)
    {
        if (desc.IsEnumType(toTypeName))
        {
            var s = ScalarToText(from);
            return s != null && desc.EnumAccepts(toTypeName, s) ? new TextValue(s) : null;
        }
        return BaseTypes.Parse(toTypeName) switch
        {
            BaseType.Text     => new TextValue(ScalarToText(from) ?? ""),
            // A password stores/migrates as text (its plaintext hash — see BaseType.Password), so
            // converting toward it is the same lossless text widening as Text.
            BaseType.Password => new TextValue(ScalarToText(from) ?? ""),
            BaseType.Int      => ToInt(from),
            BaseType.Decimal  => ToDecimal(from),
            BaseType.Bool     => ToBool(from),
            BaseType.Date     => ToDate(from),
            BaseType.DateTime => ToDateTime(from),
            _ => null,
        };
    }

    // Stored numbers are wire/JSON-invariant (the converter reads/writes them as numbers), and stored
    // dates are ISO — so the round-trip through text here is pinned to InvariantCulture, not the server
    // locale (else a value converts on one machine and defaults on another).
    private static string? ScalarToText(NodeValue v) => v switch
    {
        TextValue t      => t.Text,
        IntValue i       => i.Value.ToString(CultureInfo.InvariantCulture),
        DecimalValue d   => d.Value.ToString(CultureInfo.InvariantCulture),
        BoolValue b      => b.Value ? "true" : "false",
        DateValue d      => d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeValue dt => dt.Value.ToString("O", CultureInfo.InvariantCulture),
        _ => null,
    };

    private static NodeValue? ToInt(NodeValue v) => v switch
    {
        IntValue i => i,
        DecimalValue d when decimal.Truncate(d.Value) == d.Value
                            && d.Value >= int.MinValue && d.Value <= int.MaxValue => new IntValue((int)d.Value),
        TextValue t when int.TryParse(t.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => new IntValue(n),
        _ => null,
    };

    private static NodeValue? ToDecimal(NodeValue v) => v switch
    {
        DecimalValue d => d,
        IntValue i     => new DecimalValue(i.Value),
        TextValue t when decimal.TryParse(t.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => new DecimalValue(d),
        _ => null,
    };

    private static NodeValue? ToBool(NodeValue v) => v switch
    {
        BoolValue b => b,
        TextValue t when bool.TryParse(t.Text, out var b) => new BoolValue(b),
        _ => null,
    };

    private static NodeValue? ToDate(NodeValue v) => v switch
    {
        DateValue d      => d,
        DateTimeValue dt => new DateValue(DateOnly.FromDateTime(dt.Value.DateTime)),
        TextValue t when DateOnly.TryParse(t.Text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => new DateValue(d),
        _ => null,
    };

    private static NodeValue? ToDateTime(NodeValue v) => v switch
    {
        DateTimeValue dt => dt,
        DateValue d      => new DateTimeValue(new DateTimeOffset(d.Value.ToDateTime(TimeOnly.MinValue))),
        TextValue t when DateTimeOffset.TryParse(t.Text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) => new DateTimeValue(dt),
        _ => null,
    };

    // Reinitialize the data to the schema's initial document (the initialData seed when
    // the schema carries one, else the default empty root) — in memory and on disk.
    // Used for a FRESH publish (a target with no prior data — apply otherwise PRESERVES
    // existing data) and by tests.
    //
    // A brand-new document, not a continuation: the version resets to 0 (BuildInitialDoc's fresh
    // StoreDoc), and the per-object version map is CLEARED — every old object id is gone/reseeded, so
    // a stale entry would be meaningless (and could wrongly flag a freshly-reseeded id that happens to
    // reuse an old integer as "changed since" a version from a document this one has replaced).
    public void Reset()
    {
        lock (_sync)
        {
            // "A brand-new document, not a continuation" (the field's own words, above) extends to its
            // history: delete the log + genesis BEFORE this reset's Save() (nothing to log for the reset
            // itself — see the _versionAtOpStart = 0 line below), so the NEXT real mutation re-freezes
            // genesis from the reset doc rather than leaving history that describes a document this one
            // has replaced.
            if (File.Exists(_logPath)) File.Delete(_logPath);
            if (File.Exists(_genesisPath)) File.Delete(_genesisPath);
            _genesisWritten = false;
            _pending.Clear();
            _doc = BuildInitialDoc();
            _objectVersions.Clear();
            // BuildInitialDoc's fresh StoreDoc.Version is 0 — pin _versionAtOpStart to match it (NOT the
            // stale value a PRIOR mutation on the old doc left behind), so Save()'s "did Version change
            // during this operation" check correctly reads "no" for a reset (0 == 0) and appends nothing.
            _versionAtOpStart = _doc.Version;
            Save();
        }
    }

    // ── dictionary entries (manual keys; values are scalars or object references) ──
    //
    // Dictionary mutation is not yet reachable through ctx.commit()/CommitBatch (the Code runtime has no
    // dict-entry CommitMutation — dicts stay a later slice, same ponytail as their unmated access floor),
    // so no baseVersion check can ever target one — only the store-wide HEAD bump matters here (parallel
    // to WriteLeafCore's root-write branch), not per-object attribution.

    public int CreateEntry(NodePath dictPath, NodeValue key, NodeValue value)
    {
        lock (_sync)
        {
            BeginMutation();
            if (DictNodeAt(dictPath) is { } existing && existing.Entries.ContainsKey(KeyToString(key)))
                throw new InvalidOperationException(
                    $"An entry with key '{KeyToString(key)}' already exists at {dictPath}.");
            WriteDictionaryEntryInto(dictPath, key, value);
            var ver = BumpVersion();
            Save();
            return ver;
        }
    }

    public int WriteDictionaryEntry(NodePath path, NodeValue key, NodeValue value)
    {
        lock (_sync)
        {
            BeginMutation();
            WriteDictionaryEntryInto(path, key, value);
            var ver = BumpVersion();
            Save();
            return ver;
        }
    }

    private void WriteDictionaryEntryInto(NodePath path, NodeValue key, NodeValue value)
    {
        var typeInfo = _resolver.ResolveType(path)
            ?? throw new InvalidOperationException($"{path} does not resolve.");
        var dict = EnsureDict(path);
        var keyStr = KeyToString(key);
        var old = dict.Entries.GetValueOrDefault(keyStr);

        if (value is ObjectValue obj && typeInfo.Type.BaseType == BaseType.Object)
        {
            // MintObject records its OWN Create write (the choke point) — appended to _pending BEFORE the
            // DictSet below, so replay applies the Create first and the DictSet's New ref then resolves.
            var id = MintObject(typeInfo.Type.Name, obj);
            var newRef = new StoredRef(typeInfo.Type.Name, id);
            _pending.Add(new DictSet(dict.Id, keyStr, old, newRef));
            dict.Entries[keyStr] = newRef;
        }
        else
        {
            var newLeaf = new StoredLeaf(value);
            _pending.Add(new DictSet(dict.Id, keyStr, old, newLeaf));
            dict.Entries[keyStr] = newLeaf;
        }
    }

    public int RemoveDictionaryEntry(NodePath path, NodeValue key)
    {
        lock (_sync)
        {
            BeginMutation();
            if (DictNodeAt(path) is { } dict)
            {
                var keyStr = KeyToString(key);
                if (dict.Entries.TryGetValue(keyStr, out var old))
                    _pending.Add(new DictRemove(dict.Id, keyStr, old));
                dict.Entries.Remove(keyStr);
            }
            CollectGarbage();
            var ver = BumpVersion();
            Save();
            return ver;
        }
    }

    // ── helpers: doc + extents ──────────────────────────────────────────────────

    // Patch in the structural slots a hand-seeded or legacy document may omit.
    private StoreDoc Normalize(StoreDoc doc)
    {
        doc.Extents ??= new();
        doc.Root ??= InitialRootValue();
        return doc;
    }

    // The store's ONE commit chokepoint — called exactly where it always was, by every mutating method,
    // caller already holding _sync. Now also the WAL commit step (M13 slice 1): if the store's Version
    // ADVANCED during this operation (compared to _versionAtOpStart, captured by BeginMutation), append
    // ONE LogEntry to the log BEFORE the snapshot rewrite (the fixed crash order — a crash between the two
    // leaves the snapshot behind, repaired by ReconcileLogOnBoot; a crash before the append leaves NEITHER
    // updated, which is correct — the operation never committed).
    //
    // Gated on the VERSION delta, not on "_pending is non-empty": a no-op removal (RemoveDictionaryEntry
    // on an absent key; RemoveFromSet when the id was never a member) still calls BumpVersion()
    // unconditionally — genuinely advancing HEAD — while recording ZERO LogWrites (nothing was removed,
    // so there is nothing to describe). Gating on _pending alone would silently DROP that version's log
    // entry, breaking "every version this store reaches has exactly one log entry whose seq is that
    // version" — so such a bump still gets its own entry, just with an EMPTY Writes list (replay applies
    // no field mutation for it, only advances doc.Version/NextId to match — exactly what happened live).
    // An unchanged Version (an empty CommitBatch; the ctor seed; Reset — see its own _versionAtOpStart
    // pin; a boot catch-up checkpoint) appends nothing. _pending is always cleared here, whether or not it
    // was appended, so a later no-op Save() (there is always at least one more before the next real
    // mutation) never re-appends stale writes.
    private void Save()
    {
        if (_doc.Version != _versionAtOpStart)
        {
            var (who, msgId) = StoreWriteContext.Get();
            var entry = new LogEntry(_doc.Version, DateTimeOffset.UtcNow, who, msgId, _doc.NextId, [.._pending]);
            AppendLogEntry(_logPath, entry);
        }
        _pending.Clear();
        SaveRaw(_filePath, _doc);
    }

    // Freeze genesis from the CURRENT (pre-mutation) doc, once, the first time any public mutating method
    // runs — called by BeginMutation() (every mutating method's first statement), under _sync, BEFORE the
    // method mutates _doc. Cheap after the first call (the cached bool skips the File.Exists this method
    // would otherwise need).
    private void EnsureGenesis()
    {
        if (_genesisWritten) return;
        var genesis = new GenesisFile(_doc.Version, CloneDoc(_doc));
        Directory.CreateDirectory(Path.GetDirectoryName(_genesisPath)!);
        var tmp = _genesisPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(genesis, Opts));
        File.Move(tmp, _genesisPath, overwrite: true);
        _genesisWritten = true;
    }

    // A snapshot of doc, independent of the live _doc reference (genesis must not silently track later
    // mutations — it round-trips through the same Opts the rest of the store already uses, so this is
    // exactly what a fresh LoadRaw of the just-serialized bytes would produce).
    private static StoreDoc CloneDoc(StoreDoc doc) =>
        JsonSerializer.Deserialize<StoreDoc>(JsonSerializer.Serialize(doc, Opts), Opts)!;

    // Append one JSONL line (UTF-8, no BOM, trailing '\n') to the log file — the durable half of the WAL
    // commit. Creates the directory/file on first use (mirrors SaveRaw's Directory.CreateDirectory — a
    // freshly-created instance may not have its data dir yet).
    private static void AppendLogEntry(string logPath, LogEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var line = JsonSerializer.Serialize(entry, LogLineOpts) + "\n";
        File.AppendAllText(logPath, line, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // Load every entry from the log file, repairing a torn FINAL line (the process crashed mid-append —
    // the ONLY way a line can be malformed under the WAL discipline, since every append is a single
    // File.AppendAllText call and every entry before the last was itself completed by a PRIOR successful
    // boot). Any OTHER unparseable line — one that isn't the last, or a last line that parses as valid
    // JSON but not a LogEntry — is a corrupted log, not a crash artifact: loud StoredDataException.
    private List<LogEntry> LoadLog()
    {
        if (!File.Exists(_logPath)) return [];
        var lines = File.ReadAllLines(_logPath).Where(l => l.Length > 0).ToList();
        var entries = new List<LogEntry>();
        for (var i = 0; i < lines.Count; i++)
        {
            var isLast = i == lines.Count - 1;
            try
            {
                entries.Add(JsonSerializer.Deserialize<LogEntry>(lines[i], Opts)
                    ?? throw new JsonException("null entry"));
            }
            catch (JsonException) when (isLast)
            {
                // Torn tail: truncate the file to the last COMPLETE line and stop — the incomplete append
                // never committed (its snapshot write, if any, never ran either — WAL order), so dropping
                // it is exactly undoing the interrupted operation, not losing committed data.
                RewriteLogWithoutLastLine(lines.Take(i));
                break;
            }
            catch (JsonException ex)
            {
                throw new StoredDataException(
                    $"Log file '{_logPath}' line {i + 1} is not a readable changeset entry: {ex.Message} " +
                    "The log is corrupted (not a crash artifact — a torn line is only tolerated as the " +
                    "LAST line). Restore it from backup, or delete the log + its genesis file " +
                    $"('{_genesisPath}') to reseed history from the current snapshot.");
            }
        }
        return entries;
    }

    private void RewriteLogWithoutLastLine(IEnumerable<string> keepLines)
    {
        var kept = keepLines.ToList();
        var tmp = _logPath + ".tmp";
        File.WriteAllText(tmp, kept.Count == 0 ? "" : string.Join("\n", kept) + "\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, _logPath, overwrite: true);
    }

    // Replay genesis→head and compare against the live snapshot — the fsck invariant this whole clump sits
    // on. True iff every appended entry, applied in order to the frozen genesis doc, reproduces EXACTLY
    // the current in-memory document — a genuine structural compare (AppLogReplay.Equivalent), not a
    // serialized-text one: two docs holding identical data can enumerate their Dictionary<,> fields in
    // different orders, which would make a naive text-compare spuriously FAIL a correct replay. A store
    // with no genesis yet (nothing has ever mutated) trivially passes: genesis-less means log-less, so
    // "replay" is a no-op and the live doc — never touched — must already equal itself.
    public bool Fsck()
    {
        lock (_sync)
        {
            if (!File.Exists(_genesisPath)) return true;
            var genesis = JsonSerializer.Deserialize<GenesisFile>(File.ReadAllText(_genesisPath), Opts)
                ?? throw new StoredDataException($"Genesis file '{_genesisPath}' is not readable.");
            var replayed = LoadLog().Aggregate(genesis.Doc, AppLogReplay.Apply);
            return AppLogReplay.Equivalent(replayed, _doc);
        }
    }

    // Serialize a doc to a file atomically (temp-then-move). Shared by the instance save and the
    // static migrate pass.
    private static void SaveRaw(string path, StoreDoc doc)
    {
        var tmp = path + ".tmp";
        // Ensure the target directory exists before the temp write. A freshly-created instance
        // (sys.create) may not have its data dir yet, and File.WriteAllText would otherwise throw
        // "Could not find a part of the path …app-data.json.tmp" — the host-action deploy race.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Opts));
        // Atomically replace the data file, retrying through a transient sharing violation. On Windows the
        // overwriting move must replace `path`, which fails ("Access to the path is denied" /
        // "used by another process") whenever ANOTHER handle is briefly open on it — a virus scanner,
        // search indexer or backup in production, or a concurrent reader polling the file in the tests. That
        // conflicting handle is held for microseconds, so a brief retry rides it out; WITHOUT it the OS-level
        // conflict surfaces as a spurious write failure that gets reported back over the wire and rolls the
        // user's edit back (the "persist/action" test flake under parallel load — and a real, if rare,
        // production data-loss path). The whole point of write-temp-then-move is a durable, reader-safe
        // commit; a transient replace conflict must not defeat that.
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 50)
            {
                Thread.Sleep(10); // ~microsecond conflict window; up to ~500ms of retries dwarfs it
            }
        }
    }

    // The object a reference (or the root) points at, via its extent. Null if dangling.
    private StoredObject? ResolveRef(StoredRef objRef) =>
        _doc.Extents.GetValueOrDefault(objRef.TypeName)?.GetValueOrDefault(objRef.Id);

    private StoredObject? ExtentEntryById(int id)
    {
        foreach (var pool in _doc.Extents.Values)
            if (pool.GetValueOrDefault(id) is { } entry)
                return entry;
        return null;
    }

    // The ONE choke point every extent insert mints through (CommitBatch creates, a dict-value mint inside
    // WriteDictionaryEntryInto, CreateObject) — so a Create LogWrite is recorded here, once, rather than at
    // each of its callers separately.
    private int MintObject(string typeName, ObjectValue fields)
    {
        if (!_doc.Extents.TryGetValue(typeName, out var pool))
            _doc.Extents[typeName] = pool = new();
        var id = MintId();
        var type = _desc.FindType(typeName)
            ?? throw new InvalidOperationException($"Unknown type '{typeName}'.");
        var built = BuildFields(type, fields);
        pool[id] = new StoredObject(typeName, id, built);
        // A DEEP copy for the logged Create, not a shallow Dictionary(built) — built's own collection-
        // typed values (StoredSet/StoredDict) are MUTABLE reference types; a later write in the SAME
        // batch/entry that happens to touch one of THIS object's own freshly-minted collections would
        // otherwise mutate the very instance the log's Create snapshot also points at, corrupting the
        // logged mint-time state into whatever it looks like by the time Save() serializes it.
        _pending.Add(new Create(id, typeName, DeepCloneFields(built)));
        return id;
    }

    // A field map with every StoredSet/StoredDict value cloned (fresh Members/Entries dictionaries) —
    // StoredLeaf/StoredRef are immutable records, safe to share. Used ONLY for the log's Create snapshot
    // (see MintObject) so it can never alias a value the live extent entry goes on to mutate.
    private static Dictionary<string, StoredValue> DeepCloneFields(Dictionary<string, StoredValue> fields)
    {
        var clone = new Dictionary<string, StoredValue>(fields.Count);
        foreach (var (k, v) in fields)
            clone[k] = v switch
            {
                StoredSet s => new StoredSet(s.Id, new Dictionary<int, StoredValue>(s.Members)),
                StoredDict d => new StoredDict(d.Id, new Dictionary<string, StoredValue>(d.Entries)),
                _ => v, // StoredLeaf / StoredRef: immutable, safe to share
            };
        return clone;
    }

    // New object's stored fields: provided scalars, empty collections (each with its
    // own intrinsic id), unset refs.
    private Dictionary<string, StoredValue> BuildFields(TypeDefinition type, ObjectValue provided)
    {
        var fields = new Dictionary<string, StoredValue>();
        foreach (var prop in type.Props ?? [])
        {
            if (prop.Cardinality == Cardinality.Set)
                fields[prop.Name] = new StoredSet(MintId(), new());
            else if (prop.Cardinality == Cardinality.Dictionary)
                fields[prop.Name] = new StoredDict(MintId(), new());
            else if (!_desc.IsObjectType(prop.Type))
                fields[prop.Name] = provided.Fields.TryGetValue(prop.Name, out var v)
                    ? new StoredLeaf(v)
                    : new StoredLeaf(DefaultBase(ResolveTypeDef(prop.Type).BaseType));
            // single object props start unset (absent)
        }
        return fields;
    }

    // The set node at path, creating it (with a fresh intrinsic id) if absent. Every object minted through
    // MintObject/SeededFields already carries an empty StoredSet for each declared set prop (BuildFields),
    // so this "create if missing" branch is reachable only for a LEGACY field a migration introduced onto
    // an object that predates it. When it DOES mint, that structural change must be logged (a FieldWrite
    // introducing the field) — genesis freezes the doc BEFORE this call runs, so without recording it
    // replay would apply a later SetLink against a set id genesis never established, and throw.
    private StoredSet EnsureSet(NodePath path)
    {
        var parent = WalkToObject(ParentPath(path))
            ?? throw new InvalidOperationException($"Parent of {path} is not an object.");
        var field = path.Segments[^1];
        if (parent.Object.Fields.GetValueOrDefault(field) is not StoredSet set)
        {
            set = new StoredSet(MintId(), new());
            RecordFieldWrite(parent.Object, field, set);
            parent.Object.Fields[field] = set;
        }
        return set;
    }

    // The dictionary node at path, creating it (with a fresh intrinsic id) if absent. Same legacy-field
    // rationale and logging need as EnsureSet, above.
    private StoredDict EnsureDict(NodePath path)
    {
        var parent = WalkToObject(ParentPath(path))
            ?? throw new InvalidOperationException($"Parent of {path} is not an object.");
        var field = path.Segments[^1];
        if (parent.Object.Fields.GetValueOrDefault(field) is not StoredDict dict)
        {
            dict = new StoredDict(MintId(), new());
            RecordFieldWrite(parent.Object, field, dict);
            parent.Object.Fields[field] = dict;
        }
        return dict;
    }

    // The dictionary node on an extent entry addressed BY ID + prop name (the id-addressed sibling of
    // EnsureDict, for CommitBatch's DictWriteMutation — the owner may be a fresh create no NodePath can
    // reach yet). Every object minted through MintObject already carries an empty StoredDict for each
    // declared dict prop (BuildFields), so the create-if-missing branch only fires for a legacy field a
    // migration introduced; when it DOES mint, that structural change is logged (a FieldWrite introducing
    // the field) — same rationale as EnsureDict/EnsureSet.
    private StoredDict EnsureDictOnEntry(int objectId, string prop)
    {
        var entry = ExtentEntryById(objectId)
            ?? throw new InvalidOperationException($"No object with id {objectId}.");
        if (entry.Fields.GetValueOrDefault(prop) is not StoredDict dict)
        {
            dict = new StoredDict(MintId(), new());
            RecordFieldWrite(entry, prop, dict);
            entry.Fields[prop] = dict;
        }
        return dict;
    }

    private StoredSet? SetNodeAt(NodePath path)
    {
        var parent = WalkToObject(ParentPath(path));
        return parent?.Object.Fields.GetValueOrDefault(path.Segments[^1]) as StoredSet;
    }

    private StoredDict? DictNodeAt(NodePath path)
    {
        var parent = WalkToObject(ParentPath(path));
        return parent?.Object.Fields.GetValueOrDefault(path.Segments[^1]) as StoredDict;
    }

    // Intrinsic ids come from one global counter shared by objects, sets and dicts, so
    // every mutable thing has a unique stable id. Falls back to the max extent id for a
    // legacy doc with no counter yet.
    private int MintId()
    {
        var next = (_doc.NextId != 0 ? _doc.NextId : ExtentMaxId()) + 1;
        _doc.NextId = next;
        return next;
    }

    private int ExtentMaxId()
    {
        var max = 0;
        foreach (var pool in _doc.Extents.Values)
            foreach (var id in pool.Keys)
                if (id > max) max = id;
        return max;
    }

    // Mark-sweep from the root: any object value reachable is kept; the rest swept. The
    // walk is a typed, exhaustive switch on the value union — never a string-key probe —
    // so a user field / dict key named "type" or "id" can never be mistaken for a tag.
    private void CollectGarbage()
    {
        var visited = new HashSet<int>();

        void Mark(StoredValue? value)
        {
            switch (value)
            {
                case StoredRef r: // a reference (or the root) → resolve its extent entry, mark its fields' values
                    if (visited.Add(r.Id) && ResolveRef(r) is { } entry)
                        foreach (var v in entry.Fields.Values) Mark(v);
                    break;
                case StoredSet s:
                    foreach (var v in s.Members.Values) Mark(v);
                    break;
                case StoredDict d:
                    foreach (var v in d.Entries.Values) Mark(v);
                    break;
                case StoredLeaf:
                    break; // scalar leaves reference nothing
            }
        }

        Mark(_doc.Root);

        // A swept object is recorded as a Remove LogWrite BEFORE it is dropped (materialized with its
        // FULL prior fields — feeds future history-resurrection, and lets replay drop it without itself
        // running GC: a durable log must not depend on future GC code behaving identically to today's).
        foreach (var pool in _doc.Extents.Values)
            foreach (var id in pool.Keys.ToList())
                if (!visited.Contains(id))
                {
                    var entry = pool[id];
                    _pending.Add(new Remove(id, entry with { Fields = new Dictionary<string, StoredValue>(entry.Fields) }));
                    pool.Remove(id);
                }
    }

    // ── helpers: values ─────────────────────────────────────────────────────────

    private static NodeValue DefaultBase(BaseType bt) => bt switch
    {
        BaseType.Bool     => new BoolValue(false),
        BaseType.Int      => new IntValue(0),
        BaseType.Decimal  => new DecimalValue(0m),
        BaseType.Text     => new TextValue(""),
        // An unset enum field defaults to empty (the decided default — NOT the first value);
        // it stores as text, so the <select> shows its empty option until a value is chosen.
        BaseType.Enum     => new TextValue(""),
        // A password defaults to empty text (= "no password set"); stored as text (the hash).
        // Reachable for an absent password field on a freshly-created User (BuildFields).
        BaseType.Password => new TextValue(""),
        BaseType.Date     => new DateValue(DateOnly.FromDateTime(DateTime.Today)),
        BaseType.DateTime => new DateTimeValue(DateTimeOffset.Now),
        _ => throw new InvalidOperationException($"No base default for {bt}")
    };

    private TypeDefinition ResolveTypeDef(string name) =>
        BaseTypes.IsName(name)
            ? BaseTypes.Leaf(name)
            : _desc.FindType(name) ?? throw new InvalidOperationException($"Unknown type '{name}'.");

    // A set member / set path segment is the member object's intrinsic id.
    private static int ParseSeg(string seg) => int.TryParse(seg, out var n) ? n : -1;

    private static NodeValue ParseKey(string key, string keyType) => keyType switch
    {
        "int"      => new IntValue(int.Parse(key)),
        "decimal"  => new DecimalValue(decimal.Parse(key)),
        "bool"     => new BoolValue(bool.Parse(key)),
        "date"     => new DateValue(DateOnly.Parse(key)),
        "datetime" => new DateTimeValue(DateTimeOffset.Parse(key)),
        _          => new TextValue(key)
    };

    private static string KeyToString(NodeValue key) => key switch
    {
        IntValue i       => i.Value.ToString(),
        TextValue t      => t.Text,
        DecimalValue d   => d.Value.ToString(),
        BoolValue b      => b.Value.ToString().ToLowerInvariant(),
        DateValue d      => d.Value.ToString("yyyy-MM-dd"),
        DateTimeValue dt => dt.Value.ToString("O"),
        _ => throw new InvalidOperationException($"Cannot use {key.GetType().Name} as a key.")
    };

    private static NodePath ParentPath(NodePath path) =>
        NodePath.FromSegments(path.Segments.Take(path.Segments.Count - 1));

    // ── initial document ────────────────────────────────────────────────────────

    private StoreDoc BuildInitialDoc()
    {
        if (_desc.InitialData?.Extents is { } seed)
            return BuildSeededDoc(seed);

        var doc = new StoreDoc { NextId = 1, Root = InitialRootValue() };
        var db = _desc.Db();
        if (db is { BaseType: BaseType.Object })
        {
            _doc = doc; // MintId / BuildFields read the counter off the live doc
            // The root is id 1; its collection props get ids from the counter (which
            // starts at 1, so they mint 2, 3, …) so every set/dict has a stable id.
            doc.Extents["Db"] = new()
            {
                [1] = new StoredObject("Db", 1, BuildFields(db, new ObjectValue(new Dictionary<string, NodeValue>()))),
            };
        }
        return doc;
    }

    // First-run document from the schema's hand-authored initialData (normalized extents:
    // plain scalars, sets as arrays of member ids, refs as bare ids — already validated by
    // the loader). nextId starts above every authored id, so the set/dict ids minted here,
    // and everything created later, never collide.
    private StoreDoc BuildSeededDoc(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> seed)
    {
        var doc = new StoreDoc();
        _doc = doc; // MintId reads/bumps the live counter while seeding collection ids

        var maxId = 0;
        foreach (var pool in seed.Values)
            foreach (var idText in pool.Keys)
                maxId = Math.Max(maxId, int.Parse(idText));
        doc.NextId = maxId;

        foreach (var (typeName, pool) in seed)
        {
            var type = _desc.FindType(typeName)!;
            var poolDict = new Dictionary<int, StoredObject>();
            doc.Extents[typeName] = poolDict;
            foreach (var (idText, fields) in pool)
            {
                var id = int.Parse(idText);
                poolDict[id] = new StoredObject(typeName, id, SeededFields(type, fields));
            }
        }

        doc.Root = new StoredRef("Db", int.Parse(seed["Db"].Keys.Single()));
        return doc;
    }

    private Dictionary<string, StoredValue> SeededFields(TypeDefinition type, JsonElement fields)
    {
        var result = new Dictionary<string, StoredValue>();
        foreach (var prop in type.Props ?? [])
        {
            var has = fields.TryGetProperty(prop.Name, out var f);

            switch (prop.Cardinality)
            {
                case Cardinality.Set:
                {
                    var members = new Dictionary<int, StoredValue>();
                    if (has)
                        foreach (var m in f.EnumerateArray())
                            members[m.GetInt32()] = new StoredRef(prop.Type, m.GetInt32());
                    result[prop.Name] = new StoredSet(MintId(), members);
                    break;
                }
                case Cardinality.Dictionary:
                    // Seeding dictionary entries: a later slice (dicts are not in the
                    // Code runtime yet); the node still gets its intrinsic id.
                    result[prop.Name] = new StoredDict(MintId(), new());
                    break;
                default:
                    if (_desc.IsObjectType(prop.Type))
                    {
                        if (has) result[prop.Name] = new StoredRef(prop.Type, f.GetInt32());
                        // absent → unset reference
                    }
                    else
                    {
                        var bt = ResolveTypeDef(prop.Type).BaseType;
                        result[prop.Name] = new StoredLeaf(has ? SeededScalar(f, bt) : DefaultBase(bt));
                    }
                    break;
            }
        }
        return result;
    }

    private static NodeValue SeededScalar(JsonElement v, BaseType bt) => bt switch
    {
        BaseType.Bool     => new BoolValue(v.GetBoolean()),
        BaseType.Int      => new IntValue(v.GetInt32()),
        BaseType.Decimal  => new DecimalValue(v.GetDecimal()),
        BaseType.Text     => new TextValue(v.GetString() ?? ""),
        // A seeded enum value is its value name — text-shaped (loader-validated membership).
        BaseType.Enum     => new TextValue(v.GetString() ?? ""),
        // A password is never seeded from initialData (the loader FORBIDS it — a literal password
        // in the app document is plaintext-in-source), so this arm is defensive only; were it
        // reached, it stores as text like the value's hash would.
        BaseType.Password => new TextValue(v.GetString() ?? ""),
        BaseType.Date     => new DateValue(DateOnly.Parse(v.GetString() ?? "")),
        BaseType.DateTime => new DateTimeValue(DateTimeOffset.Parse(v.GetString() ?? "")),
        _ => throw new InvalidOperationException($"No scalar seed for {bt}"),
    };

    private StoredValue InitialRootValue()
    {
        var db = _desc.Db();
        return db is { BaseType: BaseType.Object }
            ? new StoredRef("Db", 1)
            : new StoredLeaf(DefaultBase(db?.BaseType ?? BaseType.Bool));
    }
}
