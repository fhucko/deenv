using System.Text.Json;
using DeEnv.Designer;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Kernel;

// The kernel's implementation of the host-action seam for ONE hosted instance. Its actions either
// project a DESIGN (passed by its id — the designer passes one `Design` out of its `db.designs`
// set) into an app document, or address an instance by its id:
//   • publish(design, targetId) — project the design onto an EXISTING instance (resolved by id over
//     the live hosted set), replacing its document and resetting its data.
//   • create(design, appPort, infraPort) — project the design into a NEW instance on the given
//     ports, via the kernel create delegate (the C# create mechanism: write the doc, hot-start,
//     append the registry, refresh the live set).
//   • cloneInstance(sourceId, appPort, infraPort) — copy an existing instance's app doc AND data
//     into a NEW instance on the given ports, via the kernel clone delegate.
//   • delete(targetId) — remove an existing instance, via the kernel delete delegate.
//   • setDesign(design, targetId) — record (on the target's registry entry) that it now runs the
//     passed design, AND deploy it onto the target — the IDE's "Apply" (remember-then-publish). It is
//     publish + the registry write: the registry write half (via the kernel recordDesign delegate)
//     makes the reference explicit so the dropdown can pre-select it later.
// The kernel constructs one of these per instance and threads it into WsHandler, so an action acts
// with the CALLING instance's own data as the design source (its data is the IDE holding the set of
// Designs it edits). A publish/create RESOLVES the passed design's id → its Design subtree in the
// caller's store (through IInstanceStore — never a raw file read) and projects it via SchemaBridge;
// an id that is not a member of the caller's `designs` is rejected, never a write to the wrong app.
// SchemaBridge surfaces its validation failure as a reject (it throws, WsHandler catches). The
// delete / clone delegates are type-distinct (Func<int,Task> vs Func<int,int,int,Task>) so positional
// mix-ups are compile errors; same-typed delegates (deleteInstance / restartInstance) use named args.
public sealed class KernelHostActions(
    string metaAppPath, string dataPath,
    Func<int, InstanceSpec?> resolveTarget,
    Func<string, string, int, int, int?, Task> createInstance,
    Func<int, Task> deleteInstance,
    Func<int, int, int, Task> cloneInstance,
    Func<int, int, Task> recordDesign,
    Func<int, Task> restartInstance,
    Func<int, string, Task> renameInstance) : IHostActions
{
    public void Run(string action, JsonElement args)
    {
        switch (action)
        {
            case "publish": Publish(args); break;
            case "create": Create(args); break;
            case "cloneInstance": Clone(args); break;
            case "delete": Delete(args); break;
            case "setDesign": SetDesign(args); break;
            case "rename": Rename(args); break;
            default:
                throw new InvalidOperationException($"Unknown host action '{action}'.");
        }
    }

    // publish(design, targetId): project the PASSED design (one of the caller's `db.designs`) onto an
    // EXISTING target and reset the target's data. arg 0 is the design object's id (resolved against
    // the caller's store — see ResolveDesign), arg 1 the target id. Any instance is a publish target
    // (resolution is purely by id); an id that matches no hosted instance is rejected — never a write
    // to the wrong store. ProjectDesignDocument validates the design before WriteDocument writes, so an
    // invalid design (it throws) also writes nothing. No migration on reset (that is M11).
    private void Publish(JsonElement args)
    {
        var design = ResolveDesign(ArgInt(args, 0));
        var targetId = ArgInt(args, 1);
        var target = resolveTarget(targetId)
            ?? throw new InvalidOperationException(
                $"No instance with id {targetId} to publish to.");

        var appDoc = SchemaBridge.ProjectDesignDocument(design); // throws on an invalid design
        SchemaBridge.WriteDocument(appDoc, target.SchemaPath, target.DataPath);
        // Restart the target so the new schema and reset data take effect immediately. Fire-and-forget:
        // the "ok" is sent before the restart begins, avoiding self-restart deadlock on the WS thread.
        _ = restartInstance(targetId);
    }

    // setDesign(design, targetId): the IDE's "Apply" — record (in the registry) that the target now runs
    // the passed design AND deploy it. arg 0 is the design object's id (a member of the caller's
    // `db.designs`, resolved against the caller's store), arg 1 the target instance id. Project FIRST so
    // an invalid design throws before any registry write or document overwrite (records nothing, writes
    // nothing); then record the reference (the kernel rewrites kernel.json's designId + refreshes the live
    // view); then write the projected doc + reset the target's data — exactly Publish, with the registry
    // write in front. An unknown target id is rejected before any work. No migration on reset (that is M11).
    private void SetDesign(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var targetId = ArgInt(args, 1);
        var design = ResolveDesign(designId);
        var target = resolveTarget(targetId)
            ?? throw new InvalidOperationException(
                $"No instance with id {targetId} to set a design on.");

        var appDoc = SchemaBridge.ProjectDesignDocument(design); // throws on an invalid design
        recordDesign(targetId, designId).GetAwaiter().GetResult();
        SchemaBridge.WriteDocument(appDoc, target.SchemaPath, target.DataPath);
        _ = restartInstance(targetId);
    }

    // create(design, name, appPort, infraPort): project the PASSED design into a NEW instance with the
    // given display label on the given ports — the sibling of publish (spawn rather than replace). arg 0
    // is the design object id, arg 1 the display label, args 2/3 the ports. ProjectDesignDocument
    // validates the design first (throws, spawning nothing, on an invalid one); then the kernel create
    // delegate writes + hot-starts it, recording the design's id on the new instance's registry entry
    // (so its dropdown pre-selects that design, like a seeded one). The delegate is async (it binds
    // ports); we block on it because the WS dispatch is synchronous and there is no synchronization
    // context to deadlock on (a single-operator devops action).
    private void Create(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var design = ResolveDesign(designId);
        var name = ArgText(args, 1);
        var appPort = ArgInt(args, 2);
        var infraPort = ArgInt(args, 3);

        var appDoc = SchemaBridge.ProjectDesignDocument(design); // throws on an invalid design
        createInstance(appDoc, name, appPort, infraPort, designId).GetAwaiter().GetResult();
    }

    // cloneInstance(sourceId, appPort, infraPort): copy an existing instance (app doc + data) into a
    // NEW one on the given ports — the data-carrying sibling of create. arg 0 is the SOURCE instance
    // id (a bare int, not a schema object), args 1/2 the new ports. The kernel clone delegate resolves
    // the id and copies the files; an unknown id throws (surfaced as the reject). Blocked on for the
    // same reason as create (the WS dispatch is synchronous, no synchronization context to deadlock on).
    private void Clone(JsonElement args)
    {
        var sourceId = ArgInt(args, 0);
        var appPort = ArgInt(args, 1);
        var infraPort = ArgInt(args, 2);
        cloneInstance(sourceId, appPort, infraPort).GetAwaiter().GetResult();
    }

    // delete(targetId): remove an existing instance. arg 0 is the instance id (a bare int). The kernel
    // delete delegate resolves the id and stops + forgets it (removing its id-dir + data); an unknown
    // id throws, surfaced as the reject. Blocked on like create/clone.
    private void Delete(JsonElement args)
    {
        var id = ArgInt(args, 0);
        deleteInstance(id).GetAwaiter().GetResult();
    }

    // Resolve the passed design's id → its `Design` subtree in the CALLER's store (the IDE holds a
    // `db.designs` set of Designs). Reads through IInstanceStore (the model's terms — never a raw file
    // call): a design is `db.designs[id]`, so an id that is not a member of `designs` resolves to no
    // node and is rejected here, before any projection or write. Opening a fresh store over the
    // caller's own meta+data is fine — it is the same single-process data the caller renders from
    // (this action only READS it).
    private ObjectValue ResolveDesign(int designId)
    {
        var meta = InstanceDescriptionLoader.LoadFile(metaAppPath);
        var store = new JsonFileInstanceStore(dataPath, meta);
        var designPath = NodePath.Root.Field("designs").Key(designId.ToString());
        return store.ReadNode(designPath) as ObjectValue
            ?? throw new InvalidOperationException(
                $"No design with id {designId} in the designer's `designs` set.");
    }

    // rename(id, name): update an instance's display label in the registry. arg 0 is the instance id,
    // arg 1 the new label text. The rename delegate updates the live spec and rewrites kernel.json.
    private void Rename(JsonElement args)
    {
        var id = ArgInt(args, 0);
        var name = ArgText(args, 1);
        renameInstance(id, name).GetAwaiter().GetResult();
    }

    // Read a required int argument from the evaluated-args JSON array. Code scalars ship as
    // { type, value }; accept a bare JSON number too (defensive). Anything else is a bad call.
    private static int ArgInt(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
            throw new InvalidOperationException($"host action expects an argument at position {index}.");
        var arg = args[index];
        if (arg.ValueKind == JsonValueKind.Number)
            return arg.GetInt32();
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        throw new InvalidOperationException($"host action expects an integer argument at position {index}.");
    }

    // Read a required text argument from the evaluated-args JSON array. Code scalars ship as
    // { type, value }; accept a bare JSON string too (defensive). Anything else is a bad call.
    private static string ArgText(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
            throw new InvalidOperationException($"host action expects an argument at position {index}.");
        var arg = args[index];
        if (arg.ValueKind == JsonValueKind.String)
            return arg.GetString()!;
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;
        throw new InvalidOperationException($"host action expects a text argument at position {index}.");
    }
}
