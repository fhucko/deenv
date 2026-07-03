using System.Text.Json;

namespace DeEnv.Http;

// The seam for SERVER-SIDE host actions — devops effects that run with kernel authority,
// outside the instance's own data (publish/create/clone/delete an instance; switch later). It is
// the C# side of the `sys.publish(...)` channel: the client fires a `hostAction` WS message,
// WsHandler.HandleHostAction reads (action, args) and calls Run, and an action's failure
// throws (ProcessMessage's catch turns it into the client's `{ error }` reject). Constructed
// PER INSTANCE and threaded into WsHandler exactly like LiveRegistry — so an action knows
// which instance is acting (e.g. publish uses the caller's own data as the meta-schema).
//
// Kernel-vs-image line (DECISIONS "C# is the kernel — app logic belongs in the app"): this is
// the irreducible C# MECHANISM (it touches the file system / another instance's store, an OS
// boundary). The operator-facing command/UI is image Code over `sys.*`. The named actions are
// "publish", "create", "cloneInstance" and "delete"; switch joins later on this same seam.
public interface IHostActions
{
    // Run the named host action with its raw arguments (the Code call's evaluated args, as a
    // JSON array on the wire). Throws on any failure — an unknown action, a bad argument, or
    // the action's own rejection (e.g. an invalid design) — and WsHandler surfaces the message
    // to the client as a reject.
    //
    // Returns a structured REPORT object for an action that produces one (M13 slice 4 — `publish`'s
    // identity-diff plan/outcome: applied/dryRun/renames/adds/removes/conversions/…), serialized onto
    // the hostAction reply's `report` field; null (every OTHER action) means the plain `{ ok:true }`
    // reply, unchanged. This is the ONE approved wire widening for this slice — nothing else changes.
    object? Run(string action, JsonElement args);
}

// The reject-everything seam. Two uses: (1) a host WITHOUT a kernel (TestInstanceServer, a bare
// InstanceApp.Build) — no kernel ⇒ no host actions; (2) a kernel-hosted instance that is NOT the
// operator/design host — host actions are operator devops (create/delete/clone/publish another
// instance) and must run ONLY from the design host, never from an ordinary app's WS (a public app's
// socket must not be able to delete instances). Either way every action errors with an honest reject
// rather than a silent no-op. `reason` names why (server log + client reject); the kernel supplies a
// real KernelHostActions only for the design host (KernelHost.HostActionsFor).
public sealed class NoHostActions(string? reason = null) : IHostActions
{
    public object? Run(string action, JsonElement args) =>
        throw new InvalidOperationException(
            $"Host action '{action}' is unavailable — {reason ?? "this instance is not hosted by a kernel"}.");
}
