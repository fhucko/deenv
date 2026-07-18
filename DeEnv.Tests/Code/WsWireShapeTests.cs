using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
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

    // ── retired op: `arrayAdd` is deleted (T6b) — any frame carrying it must be REJECTED ──
    // The client no longer sends `arrayAdd` (arrayAdd/arrayRemove buffer into `commit` relations);
    // a stray/old frame hitting the server must fail LOUD (Unknown op), never silently persist.

    [Test]
    public async Task A_retired_arrayAdd_op_is_rejected_as_unknown()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    items set of Item
                Item
                    title text
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            var sessions = new ClientSessionStore();
            var ws = new WsHandler(store, desc, sessions);
            var json = """{"op":"arrayAdd","id":7,"clientId":"c1","setId":3,"tempId":-2,"typeName":"Item","value":{"props":{"title":{"type":"text","value":"x"}}}}""";
            var result = ws.ProcessMessage(json);
            await Assert.That(result).Contains("Unknown op");
        }
        finally
        {
            File.Delete(dataPath);
        }
    }

    // ── retired ops: write/addEntry/removeEntry deleted (T6b-4d) — frames must be REJECTED ──
    // Dict mutations now route exclusively through `dictAdd`/`dictRemove` commit relations (T6b-4c).
    // Any stray or legacy frame must fail loudly as "Unknown op".

    [Test]
    public async Task Retired_dict_ops_are_rejected_as_unknown()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    settings dict of text
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            var sessions = new ClientSessionStore();
            var ws = new WsHandler(store, desc, sessions);

            // path write on a dict entry (now dictAdd whole-entry)
            var writeJson = """{"op":"write","id":1,"clientId":"c1","path":"/settings/foo","value":{"type":"text","value":"bar"}}""";
            var r1 = ws.ProcessMessage(writeJson);
            await Assert.That(r1).Contains("Unknown op");

            // addEntry on dict
            var addJson = """{"op":"addEntry","id":2,"clientId":"c1","path":"/settings","key":"newk","value":{"type":"text","value":"x"}}""";
            var r2 = ws.ProcessMessage(addJson);
            await Assert.That(r2).Contains("Unknown op");

            // removeEntry on dict
            var remJson = """{"op":"removeEntry","id":3,"clientId":"c1","path":"/settings","key":"foo"}""";
            var r3 = ws.ProcessMessage(remJson);
            await Assert.That(r3).Contains("Unknown op");
        }
        finally
        {
            File.Delete(dataPath);
        }
    }

    // ── outgoing: commit — the reply the ctx re-pin hinges on (finding 2) ─────────────────
    //
    // `newVersion` is the store's post-commit version, captured under the store lock (finding 3), that the
    // client re-pins the committing ctx's baseVersion to — so pinning its exact wire shape matters most.
    // idMap OMITTED (edits-only commit) vs PRESENT (a create's negId→realId + its minted collections).

    [Test]
    public async Task Commit_serializes_newVersion_with_and_without_an_idMap()
    {
        // Edits-only commit: no idMap → the field is omitted (WhenWritingNull), newVersion serializes last.
        await Assert.That(Serialize(new CommitResponse { IdMap = null, NewVersion = 5 }))
            .IsEqualTo("""{"op":"commit","ok":true,"newVersion":5}""");

        // A commit that created an object: idMap present (one remap + its minted collections), then newVersion.
        var withMap = new CommitResponse
        {
            IdMap = new[]
            {
                new CommitIdMapEntry
                {
                    TempId = -1,
                    RealId = 42,
                    Collections = new() { ["lines"] = new CollectionInfo { Id = 43, ElementTypeName = "Line", Kind = "set" } },
                },
            },
            NewVersion = 5,
        };
        await Assert.That(Serialize(withMap)).IsEqualTo(
            """{"op":"commit","ok":true,"idMap":[{"tempId":-1,"realId":42,"collections":{"lines":{"id":43,"elementTypeName":"Line","kind":"set"}}}],"newVersion":5}""");
    }

    // ── outgoing: the simple ok responses ────────────────────────────────────────────────
    //
    // Every mutating op's reply now carries newVersion (optimistic-concurrency anti-clobber —
    // DECISIONS.md "App versioning — the full design (M13 clump)"): the client must learn the store's
    // HEAD after ANY of its own writes, not just a `commit`'s, or it silently drifts behind its own
    // history (see ws.ts's onWsMessage doc). It serializes LAST (declared last on each record).

    [Test]
    public async Task The_simple_ok_responses_serialize_to_their_exact_bytes()
    {
        await Assert.That(Serialize(new HelloResponse { SessionAlive = true }))
            .IsEqualTo("""{"op":"hello","sessionAlive":true}""");
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
