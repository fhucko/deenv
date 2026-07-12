using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeEnv.Storage;

// The TYPED in-memory model of a stored document. Replaces the raw System.Text.Json
// DOM the store used to manipulate: node kinds are now a closed, compiler-checked
// union (StoredValue), so a generic walk can never read a user field key named "type"
// as a structural tag — the old duck-typing bug is made unrepresentable.
//
// The ON-DISK JSON shape is unchanged (so existing data files load with no migration):
//   { "extents": { "<Type>": { "<id>": { "type":"object","typeName":T,"id":N,"fields":{…} } } },
//     "root": <value>, "nextId": <int> }
// Value forms (the `type` discriminator is a fixed structural word):
//   scalar leaf       { "type":"text"|"int"|"bool"|"decimal"|"date"|"datetime", "value": X }
//   object reference  { "type":"object", "typeName":T, "id":N }   // no `fields` — that marks an extent entry
//   set               { "type":"set", "id":N, "members": { "<memberId>": <object-ref> } }
//   dictionary        { "type":"dictionary", "id":N, "entries": { "<keyString>": <leaf-or-object-ref> } }
//
// Mutability: minting bumps NextId; writes mutate Extents / Fields / Members / Entries
// dictionaries in place (the records hold MUTABLE dictionaries, so an in-place edit
// needs no record rebuild). The leaf format lives in ONE place — the custom converter
// below — so the read/write shapes cannot drift.

// The whole document. A mutable class (minting and writes edit it in place). Property
// names map to the on-disk keys (extents/root/nextId/version) via the store's camelCase
// PropertyNamingPolicy — no per-property name attributes.
public sealed class Db
{
    public Dictionary<string, Dictionary<int, StoredObject>> Extents { get; set; } = new();

    // A StoredRef for an object-typed Db; a StoredLeaf for a scalar Db.
    public StoredValue? Root { get; set; }

    public int NextId { get; set; }

    // Monotonically increasing HEAD of this store: bumped by JsonFileInstanceStore on every mutating
    // write (persisted so it survives a restart). Named with the M13 app-versioning design in mind
    // (DECISIONS.md "App versioning — the full design (M13 clump)") — this becomes the log's commit
    // seq once a durable change-log lands; today it is the optimistic-concurrency stamp a ctx.commit()
    // is checked against (JsonFileInstanceStore.CommitBatch's baseVersion guard). Starts at 0 (a
    // brand-new/legacy doc with no counter yet), so the first mutation bumps it to 1.
    public int Version { get; set; }
}

// An extent entry: a stored object's authoritative fields. The get-only `Type` makes
// writes emit `"type":"object"` (matching the on-disk shape); reads ignore it. Names
// (typeName/id/fields/type) come from the store's camelCase PropertyNamingPolicy.
public sealed record StoredObject(
    string TypeName,
    int Id,
    Dictionary<string, StoredValue> Fields)
{
    public string Type => "object";
}

// A stored value held in a field, member, or dictionary entry. Closed union; the
// custom JsonConverter<StoredValue> (below) reads/writes every subtype.
[JsonConverter(typeof(StoredValueConverter))]
public abstract record StoredValue;

// A scalar leaf: wraps the scalar NodeValue (Bool/Int/Decimal/Text/Date/DateTime).
public sealed record StoredLeaf(NodeValue Scalar) : StoredValue;

// An object reference into an extent (the id-only form; no fields — those live in the
// extent entry).
public sealed record StoredRef(string TypeName, int Id) : StoredValue;

// A set: intrinsic id + members keyed by member id (values are StoredRef).
public sealed record StoredSet(int Id, Dictionary<int, StoredValue> Members) : StoredValue;

// A dictionary: intrinsic id + entries keyed by the manual key string (values are
// StoredLeaf or StoredRef).
public sealed record StoredDict(int Id, Dictionary<string, StoredValue> Entries) : StoredValue;

// The ONE place the on-disk value shapes are read and written. Switches on the `type`
// discriminator: scalar tags → StoredLeaf (the matching NodeValue scalar), `object`
// → StoredRef, `set` → StoredSet, `dictionary` → StoredDict (members/entries read
// recursively). Deliberately LENIENT about a missing intrinsic `id` (defaults to 0)
// and a missing members/entries slot (defaults to empty) so a malformed/legacy
// document still deserializes and is then rejected by StoredDataValidator with a
// precise message — rather than failing here with a generic deserialization error.
public sealed class StoredValueConverter : JsonConverter<StoredValue>
{
    public override StoredValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var tagEl)
            || tagEl.ValueKind != JsonValueKind.String)
            throw new JsonException("A stored value must be a tagged object.");

        var tag = tagEl.GetString()!;
        return tag switch
        {
            "object" => new StoredRef(
                root.TryGetProperty("typeName", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString()! : "",
                IdOf(root)),
            "set" => new StoredSet(IdOf(root), ReadIntMap(root, "members", options)),
            "dictionary" => new StoredDict(IdOf(root), ReadStringMap(root, "entries", options)),
            _ => new StoredLeaf(LeafFrom(tag, root)),
        };
    }

    public override void Write(Utf8JsonWriter writer, StoredValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case StoredLeaf leaf:
                WriteLeaf(writer, leaf.Scalar);
                break;
            case StoredRef r:
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WriteString("typeName", r.TypeName);
                writer.WriteNumber("id", r.Id);
                writer.WriteEndObject();
                break;
            case StoredSet s:
                writer.WriteStartObject();
                writer.WriteString("type", "set");
                writer.WriteNumber("id", s.Id);
                writer.WritePropertyName("members");
                WriteIntMap(writer, s.Members, options);
                writer.WriteEndObject();
                break;
            case StoredDict d:
                writer.WriteStartObject();
                writer.WriteString("type", "dictionary");
                writer.WriteNumber("id", d.Id);
                writer.WritePropertyName("entries");
                WriteStringMap(writer, d.Entries, options);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"Cannot write stored value {value.GetType().Name}.");
        }
    }

    // ── leaf scalar format (mirrors the old ToTagged / LeafFromTagged exactly) ──

    private static NodeValue LeafFrom(string tag, JsonElement obj)
    {
        var v = obj.TryGetProperty("value", out var ve) ? ve : default;
        return tag switch
        {
            "bool"     => new BoolValue(v.GetBoolean()),
            "int"      => new IntValue(v.GetInt32()),
            "decimal"  => new DecimalValue(v.GetDecimal()),
            "text"     => new TextValue(v.GetString() ?? ""),
            "date"     => new DateValue(DateOnly.Parse(v.GetString() ?? "")),
            "datetime" => new DateTimeValue(DateTimeOffset.Parse(v.GetString() ?? "")),
            _ => throw new JsonException($"Unknown stored value tag '{tag}'."),
        };
    }

    private static void WriteLeaf(Utf8JsonWriter writer, NodeValue scalar)
    {
        writer.WriteStartObject();
        switch (scalar)
        {
            case BoolValue b:
                writer.WriteString("type", "bool");
                writer.WriteBoolean("value", b.Value);
                break;
            case IntValue i:
                writer.WriteString("type", "int");
                writer.WriteNumber("value", i.Value);
                break;
            case DecimalValue d:
                writer.WriteString("type", "decimal");
                writer.WriteNumber("value", d.Value);
                break;
            case TextValue t:
                writer.WriteString("type", "text");
                writer.WriteString("value", t.Text);
                break;
            case DateValue d:
                writer.WriteString("type", "date");
                writer.WriteString("value", d.Value.ToString("yyyy-MM-dd"));
                break;
            case DateTimeValue dt:
                writer.WriteString("type", "datetime");
                writer.WriteString("value", dt.Value.ToString("O"));
                break;
            default:
                throw new JsonException($"Cannot tag {scalar.GetType().Name} as a leaf.");
        }
        writer.WriteEndObject();
    }

    // ── member/entry maps ──────────────────────────────────────────────────────

    private static int IdOf(JsonElement obj) =>
        obj.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
            ? idEl.GetInt32()
            : 0; // legacy/malformed: 0 → rejected by the validator ("no intrinsic id")

    private Dictionary<int, StoredValue> ReadIntMap(JsonElement obj, string slot, JsonSerializerOptions options)
    {
        var map = new Dictionary<int, StoredValue>();
        if (obj.TryGetProperty(slot, out var slotEl) && slotEl.ValueKind == JsonValueKind.Object)
            foreach (var prop in slotEl.EnumerateObject())
                if (int.TryParse(prop.Name, out var key))
                    map[key] = Deserialize(prop.Value, options);
        return map;
    }

    private Dictionary<string, StoredValue> ReadStringMap(JsonElement obj, string slot, JsonSerializerOptions options)
    {
        var map = new Dictionary<string, StoredValue>();
        if (obj.TryGetProperty(slot, out var slotEl) && slotEl.ValueKind == JsonValueKind.Object)
            foreach (var prop in slotEl.EnumerateObject())
                map[prop.Name] = Deserialize(prop.Value, options);
        return map;
    }

    private StoredValue Deserialize(JsonElement element, JsonSerializerOptions options)
    {
        // Recurse through the converter so nested member/entry values use the same shapes.
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(element.GetRawText()));
        reader.Read();
        return Read(ref reader, typeof(StoredValue), options)!;
    }

    private void WriteIntMap(Utf8JsonWriter writer, Dictionary<int, StoredValue> map, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in map)
        {
            writer.WritePropertyName(k.ToString());
            Write(writer, v, options);
        }
        writer.WriteEndObject();
    }

    private void WriteStringMap(Utf8JsonWriter writer, Dictionary<string, StoredValue> map, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in map)
        {
            writer.WritePropertyName(k);
            Write(writer, v, options);
        }
        writer.WriteEndObject();
    }
}
