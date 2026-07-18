using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeEnv.Storage;

// The append-only changeset log behind the store (M13 slice 1 — DECISIONS.md "App versioning — the full
// design (M13 clump)", variant C: log + genesis + head + WAL + fsck). One LogEntry per store commit,
// appended (JSONL, one object per line) to a file that sits BESIDE the data file — see
// AppPaths.LogPathForDataPath — under the SAME `_sync` lock the mutation itself holds, in the fixed crash
// order append→snapshot (JsonFileInstanceStore.Save). A frozen genesis snapshot (the doc as it stood
// BEFORE the first logged entry) lets a replay walk genesis→head and reproduce the live document exactly
// — the fsck invariant this whole clump sits on.
//
// Every write is captured at the STORED level (StoredValue, the same closed union StoreModel.cs already
// uses for the on-disk document) — literal old/new values, zero schema resolution, so replay never needs
// the app's TYPE information and can never drift from what the schema-aware write actually did. This is
// deliberately a LOWER layer than IInstanceStore's own model-terms API (paths/nodes): the log records what
// the store did to Db, not what the caller asked for.

// One append-only entry: everything one store commit did, plus who/when/why. Seq is the store's HEAD
// version AFTER this entry's writes (Db.Version, the same monotonic number CommitBatch's baseVersion
// guard already stamps) — so the log's seq and the store's version are the same counter by construction; a
// batch that bumps Version by more than one still emits exactly one entry (its final seq), which is why
// entry seqs are monotonic but may have GAPS relative to a naive per-write count.
//
// `Boundary` (M13 slice 4, additive — absent on every ordinary entry) marks a VERSIONED PUBLISH: the one
// log entry that materializes a whole schema-boundary changeset (an identity-diff apply — renames, adds,
// removes, conversions, reshapes — see DesignDiff/JsonFileInstanceStore.ApplyPublishBoundary). Replay does
// not interpret it (a boundary entry's Writes are ordinary LogWrites, applied exactly like any other
// entry's — no new replay semantics); it exists purely so a reader (fsck's caller, a future history UI)
// can POINT AT the moment a schema changed without guessing from the writes' shape.
public sealed record LogEntry(
    int Seq,
    DateTimeOffset At,
    int? Who,
    int? MsgId,
    int NextId,
    List<LogWrite> Writes,
    BoundaryMarker? Boundary = null);

// Which design commit a boundary entry's changeset was materialized from — the (design, commit) pair a
// history UI would use to explain "the schema changed here, to this commit." `BaseCommitId` (M13 slice 7,
// additive — nullable, absent on an entry written before this field existed) is the commit the publish
// DIFFED FROM (the target's pre-publish stamp) — recorded because publish diffs the stamped base against
// the head across an ARBITRARY commit distance (KernelHostActions.Publish), so a reader cannot recover
// "which commit was live immediately before this boundary" by walking `CommitId`'s own `parent` — that
// walks the design's ONE-STEP-AT-A-TIME DAG, not the actual (possibly multi-commit) span this publish
// crossed. Time-travel era resolution (KernelHost.ResolveEraDb) reads this directly instead of guessing.
public sealed record BoundaryMarker(int DesignId, int CommitId, int? BaseCommitId = null);

// The outcome of JsonFileInstanceStore.ApplyPublishBoundary: whether it wrote anything (an empty diff is a
// legitimate no-op — nothing to carry), plus the destructive fallout a caller must surface loudly —
// "TypeName/id.prop" cells that could not be converted (defaulted instead) and "TypeName/id.prop" cells
// whose cardinality reshape this slice does not support (left as-is; same fallback the non-boundary apply
// has). Never itself a failure — a boundary apply always SUCCEEDS at carrying what it can; these lists are
// what the publish report calls out as loud, not blocking.
public sealed record BoundaryApplyResult(
    bool Applied,
    IReadOnlyList<string> UnconvertibleCells,
    IReadOnlyList<string> UnsupportedReshapes,
    IReadOnlyList<string>? RestoredCells = null);

public sealed record RestorationPlan(
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, StoredValue>> PropValues,
    IReadOnlyDictionary<int, IReadOnlyList<StoredObject>> TypeObjects);

// The genesis snapshot: the document as it stood BEFORE the first logged entry, written once (frozen) the
// first time any mutating store method runs. GenesisSeq is the store version genesis was taken at (0 for a
// store whose very first mutation is what freezes it) — replay starts here and walks every log entry with
// Seq > GenesisSeq forward to reproduce the live document (the fsck invariant).
public sealed record GenesisFile(int GenesisSeq, Db Db);

// A single write inside an entry — a closed union, stored-level and literal (replay applies these with
// ZERO schema resolution, no GC, no re-minting: exactly what happened, nothing derived). The `kind`
// discriminator is written by LogWriteConverter, mirroring StoredValueConverter's `type` tag pattern.
[JsonConverter(typeof(LogWriteConverter))]
public abstract record LogWrite;

// A leaf/object-field write OR a ref set/clear (Old/New are StoredRefs there) on the extent object ObjectId.
// New == null means the field was removed; Old == null means the field was previously absent.
public sealed record FieldWrite(int ObjectId, string Prop, StoredValue? Old, StoredValue? New) : LogWrite;

// An extent insert: a fresh object minted with these fields (nested StoredSet/StoredDict ids included, as
// minted — so replay never re-mints a collection id differently than the live write did).
public sealed record Create(int Id, string TypeName, Dictionary<string, StoredValue> Fields) : LogWrite;

// An extent removal: the FULL prior object (feeds future history-resurrection — §0 recovery in the design
// doc). Recorded for every object CollectGarbage sweeps, not just an explicit single-object remove, because
// replay never runs GC itself (a durable log must not depend on future GC code behaving identically).
public sealed record Remove(int Id, StoredObject Old) : LogWrite;

// Set member add/remove, addressed by the set's own intrinsic id (mirrors IInstanceStore's by-id set ops).
public sealed record SetLink(int SetId, int MemberId) : LogWrite;
public sealed record SetUnlink(int SetId, int MemberId) : LogWrite;

// Dictionary entry set/remove. DictId is the StoredDict's own intrinsic id (StoreModel.cs's StoredDict
// carries one, like a set). New == null means the entry was removed; Old == null means it was previously
// absent (a fresh key).
public sealed record DictSet(int DictId, string Key, StoredValue? Old, StoredValue? New) : LogWrite;
public sealed record DictRemove(int DictId, string Key, StoredValue Old) : LogWrite;

// List mutations, addressed by the list's own intrinsic id (mirrors set/dict by-id ops). No list-version
// field — list OCC is deferred. Replay applies these literally (no GC, no re-mint).
public sealed record ListReplace(int ListId, List<StoredValue> OldItems, List<StoredValue> NewItems) : LogWrite;
public sealed record ListInsert(int ListId, int Index, StoredValue Item) : LogWrite;
public sealed record ListRemoveAt(int ListId, int Index, StoredValue OldItem) : LogWrite;
public sealed record ListMove(int ListId, int From, int To) : LogWrite;

// A write that targets the DOCUMENT ROOT directly — reachable only for a scalar-typed Db (WriteLeafCore's
// `path.IsRoot` branch: `_db.Root = new StoredLeaf(value)`). An object-typed Db's root is a StoredRef, and
// every write that could target it (WriteObjectCore's WalkToObject-on-root) actually writes the ROOT
// OBJECT's fields — which is a FieldWrite on that object's id, never a RootWrite (verified against
// JsonFileInstanceStore: WriteObjectCore never assigns `_db.Root`).
public sealed record RootWrite(StoredValue? Old, StoredValue? New) : LogWrite;

// The ONE place LogEntry/LogWrite are read and written — mirrors StoredValueConverter exactly: a `kind`
// discriminator selects the concrete record, StoredValue fields recurse through the shared converter
// already wired into JsonFileInstanceStore.Opts (no new value encoding — a LogWrite's Old/New are exactly
// the same tagged shape as an on-disk field value).
public sealed class LogWriteConverter : JsonConverter<LogWrite>
{
    public override LogWrite Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kindEl)
            || kindEl.ValueKind != JsonValueKind.String)
            throw new JsonException("A log write must be a tagged object.");

        StoredValue? Val(string prop) =>
            root.TryGetProperty(prop, out var el) && el.ValueKind != JsonValueKind.Null
                ? Reparse(el, options) : null;
        int Int(string prop) => root.GetProperty(prop).GetInt32();
        string Str(string prop) => root.GetProperty(prop).GetString()!;

        return kindEl.GetString() switch
        {
            "fieldWrite" => new FieldWrite(Int("objectId"), Str("prop"), Val("old"), Val("new")),
            "create"     => new Create(Int("id"), Str("typeName"), ReadFields(root, options)),
            "remove"     => new Remove(Int("id"), ReadStoredObject(root.GetProperty("old"), options)),
            "setLink"    => new SetLink(Int("setId"), Int("memberId")),
            "setUnlink"  => new SetUnlink(Int("setId"), Int("memberId")),
            "dictSet"    => new DictSet(Int("dictId"), Str("key"), Val("old"), Val("new")),
            "dictRemove" => new DictRemove(Int("dictId"), Str("key"), Val("old")!),
            "listReplace" => new ListReplace(Int("listId"), ReadItemList(root, "oldItems", options), ReadItemList(root, "newItems", options)),
            "listInsert"  => new ListInsert(Int("listId"), Int("index"), Val("item")!),
            "listRemoveAt" => new ListRemoveAt(Int("listId"), Int("index"), Val("oldItem")!),
            "listMove"    => new ListMove(Int("listId"), Int("from"), Int("to")),
            "rootWrite"  => new RootWrite(Val("old"), Val("new")),
            var kind     => throw new JsonException($"Unknown log write kind '{kind}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, LogWrite value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case FieldWrite(var objectId, var prop, var old, var @new):
                writer.WriteString("kind", "fieldWrite");
                writer.WriteNumber("objectId", objectId);
                writer.WriteString("prop", prop);
                WriteVal(writer, "old", old, options);
                WriteVal(writer, "new", @new, options);
                break;
            case Create(var id, var typeName, var fields):
                writer.WriteString("kind", "create");
                writer.WriteNumber("id", id);
                writer.WriteString("typeName", typeName);
                writer.WritePropertyName("fields");
                WriteFields(writer, fields, options);
                break;
            case Remove(var id, var old):
                writer.WriteString("kind", "remove");
                writer.WriteNumber("id", id);
                writer.WritePropertyName("old");
                WriteStoredObject(writer, old, options);
                break;
            case SetLink(var setId, var memberId):
                writer.WriteString("kind", "setLink");
                writer.WriteNumber("setId", setId);
                writer.WriteNumber("memberId", memberId);
                break;
            case SetUnlink(var setId, var memberId):
                writer.WriteString("kind", "setUnlink");
                writer.WriteNumber("setId", setId);
                writer.WriteNumber("memberId", memberId);
                break;
            case DictSet(var dictId, var key, var old, var @new):
                writer.WriteString("kind", "dictSet");
                writer.WriteNumber("dictId", dictId);
                writer.WriteString("key", key);
                WriteVal(writer, "old", old, options);
                WriteVal(writer, "new", @new, options);
                break;
            case DictRemove(var dictId, var key, var old):
                writer.WriteString("kind", "dictRemove");
                writer.WriteNumber("dictId", dictId);
                writer.WriteString("key", key);
                WriteVal(writer, "old", old, options);
                break;
            case ListReplace(var listId, var oldItems, var newItems):
                writer.WriteString("kind", "listReplace");
                writer.WriteNumber("listId", listId);
                WriteItemList(writer, "oldItems", oldItems, options);
                WriteItemList(writer, "newItems", newItems, options);
                break;
            case ListInsert(var listId, var index, var item):
                writer.WriteString("kind", "listInsert");
                writer.WriteNumber("listId", listId);
                writer.WriteNumber("index", index);
                WriteVal(writer, "item", item, options);
                break;
            case ListRemoveAt(var listId, var index, var oldItem):
                writer.WriteString("kind", "listRemoveAt");
                writer.WriteNumber("listId", listId);
                writer.WriteNumber("index", index);
                WriteVal(writer, "oldItem", oldItem, options);
                break;
            case ListMove(var listId, var from, var to):
                writer.WriteString("kind", "listMove");
                writer.WriteNumber("listId", listId);
                writer.WriteNumber("from", from);
                writer.WriteNumber("to", to);
                break;
            case RootWrite(var old, var @new):
                writer.WriteString("kind", "rootWrite");
                WriteVal(writer, "old", old, options);
                WriteVal(writer, "new", @new, options);
                break;
            default:
                throw new JsonException($"Cannot write log write {value.GetType().Name}.");
        }
        writer.WriteEndObject();
    }

    // ── shared helpers ──────────────────────────────────────────────────────────

    private static void WriteVal(Utf8JsonWriter writer, string prop, StoredValue? v, JsonSerializerOptions options)
    {
        writer.WritePropertyName(prop);
        if (v is null) writer.WriteNullValue();
        else JsonSerializer.Serialize(writer, v, options);
    }

    private static void WriteItemList(
        Utf8JsonWriter writer, string prop, List<StoredValue> items, JsonSerializerOptions options)
    {
        writer.WritePropertyName(prop);
        writer.WriteStartArray();
        foreach (var item in items)
            JsonSerializer.Serialize(writer, item, options);
        writer.WriteEndArray();
    }

    private static List<StoredValue> ReadItemList(JsonElement root, string prop, JsonSerializerOptions options)
    {
        var items = new List<StoredValue>();
        if (root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
                items.Add(Reparse(item, options));
        return items;
    }

    private static void WriteFields(
        Utf8JsonWriter writer, Dictionary<string, StoredValue> fields, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in fields)
        {
            writer.WritePropertyName(k);
            JsonSerializer.Serialize(writer, v, options);
        }
        writer.WriteEndObject();
    }

    private static Dictionary<string, StoredValue> ReadFields(JsonElement root, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, StoredValue>();
        if (root.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Object)
            foreach (var prop in fieldsEl.EnumerateObject())
                result[prop.Name] = Reparse(prop.Value, options);
        return result;
    }

    private static void WriteStoredObject(Utf8JsonWriter writer, StoredObject obj, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("typeName", obj.TypeName);
        writer.WriteNumber("id", obj.Id);
        writer.WritePropertyName("fields");
        WriteFields(writer, obj.Fields, options);
        writer.WriteEndObject();
    }

    private static StoredObject ReadStoredObject(JsonElement el, JsonSerializerOptions options) =>
        new(el.GetProperty("typeName").GetString()!, el.GetProperty("id").GetInt32(), ReadFields(el, options));

    // Re-run a JsonElement through the shared StoredValueConverter (already wired into
    // JsonFileInstanceStore.Opts) so a LogWrite's Old/New values use the EXACT same tagged shape as an
    // on-disk field value — one leaf/ref/set/dict format, never a second one for the log.
    private static StoredValue Reparse(JsonElement el, JsonSerializerOptions options)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(el.GetRawText()));
        reader.Read();
        return JsonSerializer.Deserialize<StoredValue>(ref reader, options)!;
    }
}
