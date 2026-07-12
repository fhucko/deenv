namespace DeEnv.Storage;

// The ONE apply implementation shared by boot-replay (JsonFileInstanceStore.ReconcileLogOnBoot) and fsck
// (JsonFileInstanceStore.Fsck) — literally applies a LogEntry's writes to a Db, stored-level, with
// ZERO schema resolution, no GC, and no re-minting: exactly what the live write did, nothing derived. A
// durable log must reproduce the past exactly even if today's GC/resolver code later changes behavior, so
// replay never calls back into either.
public static class AppLogReplay
{
    // Apply one entry's writes to doc IN PLACE (Db/StoredObject/StoredSet/StoredDict all hold
    // mutable dictionaries — the same in-place-edit style JsonFileInstanceStore itself uses) and stamp the
    // entry's own Seq/NextId, then return doc (so callers can Aggregate/fold entry after entry).
    public static Db Apply(Db db, LogEntry entry)
    {
        foreach (var write in entry.Writes)
            ApplyWrite(db, write);
        db.Version = entry.Seq;
        db.NextId = entry.NextId;
        return db;
    }

    private static void ApplyWrite(Db db, LogWrite write)
    {
        switch (write)
        {
            case FieldWrite(var objectId, var prop, _, var @new):
            {
                var obj = ExtentEntryById(db, objectId)
                    ?? throw new StoredDataException(
                        $"Replay: FieldWrite targets object {objectId}, which does not exist.");
                if (@new is null) obj.Fields.Remove(prop);
                else obj.Fields[prop] = @new;
                break;
            }
            case Create(var id, var typeName, var fields):
            {
                if (!db.Extents.TryGetValue(typeName, out var pool))
                    db.Extents[typeName] = pool = new();
                pool[id] = new StoredObject(typeName, id, new Dictionary<string, StoredValue>(fields));
                break;
            }
            case Remove(var id, var old):
            {
                if (db.Extents.TryGetValue(old.TypeName, out var pool))
                    pool.Remove(id);
                break;
            }
            case SetLink(var setId, var memberId):
            {
                var set = FindSetNode(db, setId)
                    ?? throw new StoredDataException(
                        $"Replay: SetLink targets set {setId}, which does not exist.");
                var member = ExtentEntryById(db, memberId)
                    ?? throw new StoredDataException(
                        $"Replay: SetLink targets member {memberId}, which does not exist.");
                set.Members[memberId] = new StoredRef(member.TypeName, memberId);
                break;
            }
            case SetUnlink(var setId, var memberId):
            {
                if (FindSetNode(db, setId) is { } set) set.Members.Remove(memberId);
                break;
            }
            case DictSet(var dictId, var key, _, var @new):
            {
                var dict = FindDictNode(db, dictId)
                    ?? throw new StoredDataException(
                        $"Replay: DictSet targets dictionary {dictId}, which does not exist.");
                if (@new is null) dict.Entries.Remove(key);
                else dict.Entries[key] = @new;
                break;
            }
            case DictRemove(var dictId, var key, _):
            {
                if (FindDictNode(db, dictId) is { } dict) dict.Entries.Remove(key);
                break;
            }
            case RootWrite(_, var @new):
            {
                db.Root = @new;
                break;
            }
            default:
                throw new StoredDataException($"Replay: unknown log write {write.GetType().Name}.");
        }
    }

    // ── lookups (mirror JsonFileInstanceStore's own private helpers — replay walks a doc the same way) ──

    private static StoredObject? ExtentEntryById(Db db, int id)
    {
        foreach (var pool in db.Extents.Values)
            if (pool.GetValueOrDefault(id) is { } entry)
                return entry;
        return null;
    }

    private static StoredSet? FindSetNode(Db db, int setId)
    {
        foreach (var pool in db.Extents.Values)
            foreach (var entry in pool.Values)
                foreach (var fv in entry.Fields.Values)
                    if (fv is StoredSet set && set.Id == setId)
                        return set;
        return null;
    }

    private static StoredDict? FindDictNode(Db db, int dictId)
    {
        foreach (var pool in db.Extents.Values)
            foreach (var entry in pool.Values)
                foreach (var fv in entry.Fields.Values)
                    if (fv is StoredDict dict && dict.Id == dictId)
                        return dict;
        return null;
    }

    // ── deep content equality (the fsck invariant's actual check) ──────────────────────────────────
    //
    // A genuine structural compare, NOT a serialized-text compare: Db's Extents/Fields/Members/
    // Entries are all plain Dictionary<,>, which has no value Equals (and .NET does not promise
    // enumeration order matches insertion order across two independently-built dictionaries that
    // happen to hold the same entries) — so two docs holding IDENTICAL data could serialize to
    // different JSON text purely from key ordering, which would make a text-compare fsck spuriously
    // FAIL a genuinely-correct replay. Comparing key-by-key (order-independent) is the only check that
    // actually verifies "genesis replayed to head reproduces the current data," which is the entire
    // point of fsck.
    public static bool Equivalent(Db a, Db b)
    {
        if (a.Version != b.Version || a.NextId != b.NextId) return false;
        if (!ValueEqual(a.Root, b.Root)) return false;
        if (a.Extents.Count != b.Extents.Count) return false;
        foreach (var (typeName, poolA) in a.Extents)
        {
            if (!b.Extents.TryGetValue(typeName, out var poolB) || poolA.Count != poolB.Count) return false;
            foreach (var (id, objA) in poolA)
                if (!poolB.TryGetValue(id, out var objB) || !ObjectEqual(objA, objB))
                    return false;
        }
        return true;
    }

    private static bool ObjectEqual(StoredObject a, StoredObject b)
    {
        if (a.TypeName != b.TypeName || a.Id != b.Id || a.Fields.Count != b.Fields.Count) return false;
        foreach (var (name, valA) in a.Fields)
            if (!b.Fields.TryGetValue(name, out var valB) || !ValueEqual(valA, valB))
                return false;
        return true;
    }

    private static bool ValueEqual(StoredValue? a, StoredValue? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return (a, b) switch
        {
            (StoredLeaf la, StoredLeaf lb) => Equals(la.Scalar, lb.Scalar),
            (StoredRef ra, StoredRef rb) => ra.TypeName == rb.TypeName && ra.Id == rb.Id,
            (StoredSet sa, StoredSet sb) => sa.Id == sb.Id && MapEqual(sa.Members, sb.Members),
            (StoredDict da, StoredDict db) => da.Id == db.Id && MapEqual(da.Entries, db.Entries),
            _ => false, // different concrete kinds
        };
    }

    private static bool MapEqual<TKey>(Dictionary<TKey, StoredValue> a, Dictionary<TKey, StoredValue> b)
        where TKey : notnull
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var v2) || !ValueEqual(v, v2))
                return false;
        return true;
    }
}
