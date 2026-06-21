using GenHTTP.Api.Content;
using GenHTTP.Api.Protocol;
using GenHTTP.Api.Routing;
using GenHTTP.Modules.IO;

namespace DeEnv.Kernel;

// The kernel's front-edge path router: ONE handler at the front of each shared host (the app host and
// the asset host), dispatching `apps/<name>/…` to instance `<name>`'s handler. It is the path
// equivalent of the old per-instance port multiplexing — a single front-router over the kernel-owned
// instance set replaces N bound port pairs, with prefix→instance resolution KERNEL-owned (over the
// live set), so a future reverse-proxy-to-per-instance-process stays open.
//
// It matches the reserved `apps` segment, then the instance NAME segment, then ADVANCES the routing
// pointer past BOTH — so the instance's own handler (reached via `resolve`) sees `Target.GetRemaining()`
// as the instance's ROOT-RELATIVE path (e.g. `/notes/2`, or `/ws`), keeping the instance mount-unaware.
// Resolution is by NAME against the LIVE set each request (a closure the kernel supplies over its
// instance dictionary), so a created/renamed/deleted instance is routed (or stops being routed) with
// no host rebind. An unmatched path (not `apps/<name>`, or an unknown name) is a 404.
public sealed class PathRouter : IHandler
{
    private readonly Func<string, IHandler?> _resolve;
    private readonly Func<IReadOnlyList<string>> _names; // for the helpful root listing

    public PathRouter(Func<string, IHandler?> resolve, Func<IReadOnlyList<string>> names)
    {
        _resolve = resolve;
        _names = names;
    }

    public ValueTask PrepareAsync() => ValueTask.CompletedTask;

    public async ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        var target = request.Target;

        // The bare kernel root (no path) lists the mount points — a convenience landing page, not a
        // routed instance (the app URL space proper lives under /apps/<name>).
        if (target.Current is null)
            return Listing(request);

        if (!string.Equals(target.Current.Value, "apps", StringComparison.Ordinal))
            return NotFound(request, $"No route for '/{target.GetRemaining().ToString().Trim('/')}'.");
        target.Advance(); // consume "apps"

        if (target.Current is null)
            return Listing(request);

        var name = target.Current.Value;
        if (_resolve(name) is not { } handler)
            return NotFound(request, $"No instance named '{name}'.");
        target.Advance(); // consume the instance name → the instance handler sees a root-relative target

        return await handler.HandleAsync(request);
    }

    // A minimal index of the mount points, so hitting the kernel root (or /apps) is informative rather
    // than a bare 404 in local dev.
    private IResponse Listing(IRequest request)
    {
        var links = string.Join("", _names().OrderBy(n => n).Select(n =>
            $"<li><a href=\"/apps/{System.Net.WebUtility.HtmlEncode(n)}/\">{System.Net.WebUtility.HtmlEncode(n)}</a></li>"));
        var html = $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>DeEnv</title></head>" +
                   $"<body><h1>DeEnv</h1><ul>{links}</ul></body></html>";
        return request.Respond().Content(html).Type(ContentType.TextHtml).Build();
    }

    private static IResponse NotFound(IRequest request, string message) =>
        request.Respond().Status(ResponseStatus.NotFound).Content(message).Type(ContentType.TextPlain).Build();
}
