using System.Text.Json;
using DeEnv.Designer;
using DeEnv.Http;

namespace DeEnv.Kernel;

// The kernel's implementation of the host-action seam for ONE hosted instance. It knows the
// acting (designer) instance's own meta/data paths and resolves a target instance id to its
// app+data paths via a kernel-supplied resolver over the LIVE hosted set — so the resolver
// reflects instances created after this one started (a publish target need not predate the
// designer). The kernel constructs one of these per instance and threads it into WsHandler.
//
// Today the only action is "publish": run the M4 schema export server-side, with the CALLING
// instance acting as the designer (its data is the meta-schema) and a target instance resolved
// by id. SchemaBridge is unchanged — this is just the kernel-side wiring that gives it the
// right four paths and surfaces its validation failure as a reject (it throws, WsHandler
// catches). create/switch/delete will join as further named actions here later.
public sealed class KernelHostActions(
    string metaAppPath, string dataPath, Func<int, InstanceSpec?> resolveTarget) : IHostActions
{
    public void Run(string action, JsonElement args)
    {
        switch (action)
        {
            case "publish": Publish(args); break;
            default:
                throw new InvalidOperationException($"Unknown host action '{action}'.");
        }
    }

    // sys.publish(targetId): export the calling instance's data as the target instance's app
    // document and reset the target's data. The args are the Code call's evaluated arguments as
    // a JSON array — arg 0 is the target id (a Code int ships as { type:"int", value }). A target
    // id of 0 or one that matches no hosted instance (a boot instance has no id; an unknown id) is
    // rejected — never a write to the wrong store. SchemaBridge validates the design before
    // writing, so an invalid design (it throws) also writes nothing.
    private void Publish(JsonElement args)
    {
        var targetId = ArgInt(args, 0);
        var target = resolveTarget(targetId)
            ?? throw new InvalidOperationException(
                $"No instance with id {targetId} to publish to — only kernel-created instances " +
                "are publish targets (a boot instance has no id).");

        SchemaBridge.Export(metaAppPath, dataPath, target.SchemaPath, target.DataPath);
    }

    // Read a required int argument from the evaluated-args JSON array. Code scalars ship as
    // { type, value }; accept a bare JSON number too (defensive). Anything else is a bad call.
    private static int ArgInt(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
            throw new InvalidOperationException($"publish expects an argument at position {index}.");
        var arg = args[index];
        if (arg.ValueKind == JsonValueKind.Number)
            return arg.GetInt32();
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        throw new InvalidOperationException("publish expects an integer target id.");
    }
}
