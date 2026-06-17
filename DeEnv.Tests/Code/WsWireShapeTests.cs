using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeEnv.Http;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Code;

// Pins the WS wire shape (the C#↔TS contract — see DeEnv/Instance/ws.ts) at the byte level
// after WsHandler's incoming/outgoing parsing was retyped (records instead of hand-built
// JsonObject / TryGetProperty). The end-to-end suite exercises real WS round-trips, but these
// are the cheap, exact-string guards on the typed model itself: a representative request must
// deserialize into WsRequest, and each representative response must serialize to the IDENTICAL
// JSON bytes the old literals produced — including the WhenWritingNull-conditional fields
// (arrayAdd.tempId, setReferenceField.newId) and the WithId correlation-id append.
public sealed class WsWireShapeTests
{
    // Mirror WsHandler._jsonOpts (compact, camelCase naming policy, omit-null-when-writing)
    // so these strings are the wire bytes.
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Opts);

    // Reproduce WsHandler.WithId: when the request carried a numeric id, every reply (success AND
    // error) gets `id` appended LAST. The bytes must match, so the test asserts against the same
    // post-processing the handler applies.
    private static string WithId(string json, int? id)
    {
        if (id is null) return json;
        var node = JsonNode.Parse(json)!;
        node["id"] = id.Value;
        return node.ToJsonString(Opts);
    }

    // ── incoming: a representative arrayAdd request deserializes into WsRequest ──────────

    [Test]
    public async Task An_arrayAdd_request_deserializes_into_WsRequest()
    {
        const string json =
            """{"op":"arrayAdd","id":7,"clientId":"c1","setId":3,"tempId":-2,"typeName":"Item","value":{"props":{"title":{"type":"text","value":"x"}}}}""";

        var req = JsonSerializer.Deserialize<WsRequest>(json, Opts)!;

        await Assert.That(req.Op).IsEqualTo("arrayAdd");
        await Assert.That(req.Id).IsEqualTo(7);
        await Assert.That(req.ClientId).IsEqualTo("c1");
        await Assert.That(req.SetId).IsEqualTo(3);
        await Assert.That(req.TempId).IsEqualTo(-2);
        await Assert.That(req.TypeName).IsEqualTo("Item");
        // The value body stays a raw JsonElement (schema-driven parsing happens later, untouched).
        await Assert.That(req.Value.HasValue).IsTrue();
        await Assert.That(req.Value!.Value.ValueKind).IsEqualTo(JsonValueKind.Object);
    }

    // A request WITHOUT an id leaves Id null (so WithId appends nothing — the no-correlation case).
    [Test]
    public async Task A_request_without_a_correlation_id_leaves_Id_null()
    {
        var req = JsonSerializer.Deserialize<WsRequest>("""{"op":"hello","clientId":"c1"}""", Opts)!;
        await Assert.That(req.Op).IsEqualTo("hello");
        await Assert.That(req.Id).IsNull();
        await Assert.That(req.ClientId).IsEqualTo("c1");
    }

    // ── outgoing: arrayAdd WITH a tempId (set add from the client) ───────────────────────

    [Test]
    public async Task ArrayAdd_with_a_tempId_serializes_to_the_exact_wire_bytes()
    {
        var resp = new ArrayAddResponse
        {
            NewId = 42,
            Collections = new() { ["notes"] = new CollectionInfo { Id = 43, ElementTypeName = "Note" } },
            TempId = -2,
        };

        await Assert.That(Serialize(resp)).IsEqualTo(
            """{"op":"arrayAdd","newId":42,"collections":{"notes":{"id":43,"elementTypeName":"Note"}},"tempId":-2}""");
    }

    // ── outgoing: arrayAdd WITHOUT a tempId — the field is OMITTED, not null ──────────────

    [Test]
    public async Task ArrayAdd_without_a_tempId_omits_the_field()
    {
        var resp = new ArrayAddResponse
        {
            NewId = 42,
            Collections = new(),
            TempId = null,
        };

        await Assert.That(Serialize(resp)).IsEqualTo(
            """{"op":"arrayAdd","newId":42,"collections":{}}""");
    }

    // ── outgoing: setReferenceField — newId present (create-new) vs omitted (link/clear) ──

    [Test]
    public async Task SetReferenceField_includes_newId_only_when_minted()
    {
        await Assert.That(Serialize(new SetReferenceFieldResponse { NewId = 99 }))
            .IsEqualTo("""{"op":"setReferenceField","ok":true,"newId":99}""");
        await Assert.That(Serialize(new SetReferenceFieldResponse { NewId = null }))
            .IsEqualTo("""{"op":"setReferenceField","ok":true}""");
    }

    // ── outgoing: the simple ok responses ────────────────────────────────────────────────

    [Test]
    public async Task The_simple_ok_responses_serialize_to_their_exact_bytes()
    {
        await Assert.That(Serialize(new WriteResponse { Path = "/a/b" }))
            .IsEqualTo("""{"op":"write","path":"/a/b","ok":true}""");
        await Assert.That(Serialize(new AddEntryResponse { Path = "/d", Key = "k" }))
            .IsEqualTo("""{"op":"addEntry","path":"/d","ok":true,"key":"k"}""");
        await Assert.That(Serialize(new RemoveEntryResponse { Path = "/d" }))
            .IsEqualTo("""{"op":"removeEntry","path":"/d","ok":true}""");
        await Assert.That(Serialize(new HelloResponse { SessionAlive = true }))
            .IsEqualTo("""{"op":"hello","sessionAlive":true}""");
        await Assert.That(Serialize(new ObjectPropChangeResponse()))
            .IsEqualTo("""{"op":"objectPropChange","ok":true}""");
        await Assert.That(Serialize(new ArrayRemoveResponse()))
            .IsEqualTo("""{"op":"arrayRemove","ok":true}""");
        await Assert.That(Serialize(new HostActionResponse()))
            .IsEqualTo("""{"op":"hostAction","ok":true}""");
        await Assert.That(Serialize(new AckRemapResponse()))
            .IsEqualTo("""{"op":"ackRemap","ok":true}""");
    }

    // ── outgoing: an error, and an error WITH a correlation id (WithId appends id last) ───

    [Test]
    public async Task An_error_with_a_correlation_id_appends_id_last()
    {
        // The bare error (uncorrelated send — e.g. a failed refetch carries no id).
        var bare = Serialize(new ErrorResponse { Error = "boom" });
        await Assert.That(bare).IsEqualTo("""{"error":"boom"}""");

        // A correlated error: WithId appends `id` LAST onto whatever the handler produced.
        await Assert.That(WithId(bare, 7)).IsEqualTo("""{"error":"boom","id":7}""");
    }

    // ── outgoing: refetch wraps the raw client-state node unreshaped ─────────────────────

    [Test]
    public async Task Refetch_wraps_the_state_node_inline()
    {
        var state = new JsonObject { ["leaves"] = new JsonObject(), ["scope"] = new JsonObject() };
        var resp = new RefetchResponse { State = state };
        await Assert.That(Serialize(resp))
            .IsEqualTo("""{"op":"refetch","state":{"leaves":{},"scope":{}}}""");
    }
}
