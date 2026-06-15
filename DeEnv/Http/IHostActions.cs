using System.Text.Json;

namespace DeEnv.Http;

// The seam for SERVER-SIDE host actions — devops effects that run with kernel authority,
// outside the instance's own data (publish a schema, and later create/switch/delete). It is
// the C# side of the `sys.publish(...)` channel: the client fires a `hostAction` WS message,
// WsHandler.HandleHostAction reads (action, args) and calls Run, and an action's failure
// throws (ProcessMessage's catch turns it into the client's `{ error }` reject). Constructed
// PER INSTANCE and threaded into WsHandler exactly like LiveRegistry — so an action knows
// which instance is acting (e.g. publish uses the caller's own data as the meta-schema).
//
// Kernel-vs-image line (DECISIONS "C# is the kernel — app logic belongs in the app"): this is
// the irreducible C# MECHANISM (it touches the file system / another instance's store, an OS
// boundary). The operator-facing command/UI is image Code over `sys.*`. Today the only action
// is "publish"; create/switch/delete join later as further named actions on this same seam.
public interface IHostActions
{
    // Run the named host action with its raw arguments (the Code call's evaluated args, as a
    // JSON array on the wire). Throws on any failure — an unknown action, a bad argument, or
    // the action's own rejection (e.g. an invalid design) — and WsHandler surfaces the message
    // to the client as a reject. Returns normally on success.
    void Run(string action, JsonElement args);
}

// The default for any host WITHOUT a kernel (TestInstanceServer, a bare InstanceApp.Build): no
// kernel ⇒ no host actions. Every action errors, so an app that calls `sys.publish(...)` on a
// kernel-less host gets an honest reject rather than a silent no-op. The kernel supplies a real
// implementation (KernelHostActions) for its hosted instances.
public sealed class NoHostActions : IHostActions
{
    public void Run(string action, JsonElement args) =>
        throw new InvalidOperationException(
            $"Host action '{action}' is unavailable — this instance is not hosted by a kernel.");
}
