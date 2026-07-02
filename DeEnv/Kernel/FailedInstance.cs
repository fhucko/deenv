using GenHTTP.Api.Content;
using GenHTTP.Api.Protocol;
using GenHTTP.Modules.IO;

namespace DeEnv.Kernel;

// One instance that FAILED to load at kernel boot, kept aside so the failure stays LOUD without
// taking the kernel (or its sibling instances) down: the kernel logs the full error at boot, and the
// instance's mount answers an explicit 503 — never a silent 404 (the mount exists; its instance is
// broken). Boot-time isolation only — runtime fault/resource isolation is the deferred
// distributed-runtime pillar. Recovery is operator-level: fix the instance's files (or publish a
// corrected document) and restart the kernel.
public sealed class FailedInstance(InstanceSpec spec, Exception error)
{
    public InstanceSpec Spec { get; } = spec;
    public Exception Error { get; } = error;

    // The mount's stand-in: every path under /apps/<name> (page, /ws, /js) answers 503 naming the
    // instance — the visitor learns it is down; the WHY stays in the kernel log, not the public page.
    public IHandler Handler { get; } = new UnavailableHandler(spec.App);

    private sealed class UnavailableHandler(string name) : IHandler
    {
        public ValueTask PrepareAsync() => ValueTask.CompletedTask;

        public ValueTask<IResponse?> HandleAsync(IRequest request) =>
            new(request.Respond()
                .Status(ResponseStatus.ServiceUnavailable)
                .Content($"The instance '{name}' failed to start — see the kernel log.")
                .Type(ContentType.TextPlain)
                .Build());
    }
}
