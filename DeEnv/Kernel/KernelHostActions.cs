using System.Text.Json;
using DeEnv.Code;
using DeEnv.Designer;
using DeEnv.Http;

namespace DeEnv.Kernel;

// The kernel's implementation of the host-action seam for ONE hosted instance. Its actions either
// project a SCHEMA OBJECT (passed by its id — the designer passes `db`, the root) into an app
// document, or address an instance by its id:
//   • publish(schema, targetId) — project onto an EXISTING instance (resolved by id over the live
//     hosted set), replacing its document and resetting its data.
//   • create(schema, appPort, infraPort) — project into a NEW instance on the given ports, via the
//     kernel create delegate (the C# create mechanism: write the doc, hot-start, append the
//     registry, refresh the live set).
//   • cloneInstance(sourceId, appPort, infraPort) — copy an existing instance's app doc AND data
//     into a NEW instance on the given ports, via the kernel clone delegate.
//   • delete(targetId) — remove an existing instance, via the kernel delete delegate.
// The kernel constructs one of these per instance and threads it into WsHandler, so an action acts
// with the CALLING instance's own data as the schema source (its data is the meta-schema it
// designs). SchemaBridge is unchanged — this is the kernel-side wiring that gives it the right
// paths and surfaces its validation failure as a reject (it throws, WsHandler catches). The delete /
// clone delegates are type-distinct (Func<int,Task> vs Func<int,int,int,Task>) so positional
// mix-ups are compile errors; the call site uses named args for clarity.
public sealed class KernelHostActions(
    string metaAppPath, string dataPath,
    Func<int, InstanceSpec?> resolveTarget,
    Func<string, int, int, Task> createInstance,
    Func<int, Task> deleteInstance,
    Func<int, int, int, Task> cloneInstance) : IHostActions
{
    public void Run(string action, JsonElement args)
    {
        switch (action)
        {
            case "publish": Publish(args); break;
            case "create": Create(args); break;
            case "cloneInstance": Clone(args); break;
            case "delete": Delete(args); break;
            default:
                throw new InvalidOperationException($"Unknown host action '{action}'.");
        }
    }

    // publish(schema, targetId): project the calling instance's schema onto an EXISTING target and
    // reset the target's data. arg 0 is the schema object's id (the root — see RequireRootSchema),
    // arg 1 the target id. Any instance is a publish target (resolution is purely by id); an id that
    // matches no hosted instance is rejected — never a write to the wrong store. SchemaBridge.Export
    // validates the design before writing, so an invalid design (it throws) also writes nothing.
    private void Publish(JsonElement args)
    {
        RequireRootSchema(ArgInt(args, 0));
        var targetId = ArgInt(args, 1);
        var target = resolveTarget(targetId)
            ?? throw new InvalidOperationException(
                $"No instance with id {targetId} to publish to.");

        SchemaBridge.Export(metaAppPath, dataPath, target.SchemaPath, target.DataPath);
    }

    // create(schema, appPort, infraPort): project the calling instance's schema into a NEW instance
    // on the given ports — the sibling of publish (spawn rather than replace). arg 0 is the schema
    // object id (root), args 1/2 the ports. ProjectDocument validates the design first (throws,
    // spawning nothing, on an invalid one); then the kernel create delegate writes + hot-starts it.
    // The delegate is async (it binds ports); we block on it because the WS dispatch is synchronous
    // and there is no synchronization context to deadlock on (a single-operator devops action).
    private void Create(JsonElement args)
    {
        RequireRootSchema(ArgInt(args, 0));
        var appPort = ArgInt(args, 1);
        var infraPort = ArgInt(args, 2);

        var appDoc = SchemaBridge.ProjectDocument(metaAppPath, dataPath); // throws on an invalid design
        createInstance(appDoc, appPort, infraPort).GetAwaiter().GetResult();
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

    // Today only the caller's own root object (`db`, DbBridge.RootId) is a valid schema to project:
    // the designer designs one schema, which is its root. A non-root schema object — selecting one
    // design out of a managed SET of apps — is a future extension (it needs id→subtree resolution
    // over the caller's store); reject anything else loudly rather than silently projecting the root.
    private static void RequireRootSchema(int schemaId)
    {
        if (schemaId != DbBridge.RootId)
            throw new InvalidOperationException(
                $"Only the root schema object (db) can be projected today; got object id {schemaId}.");
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
}
