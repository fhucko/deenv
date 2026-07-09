using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeEnv.Instance;
using DeEnv.Storage;
using GenHTTP.Api.Content;
using GenHTTP.Api.Protocol;
using GenHTTP.Modules.IO;

namespace DeEnv.Http;

// The per-instance blob pool's two HTTP edges (docs/plans/assets-design.md): POST uploads raw bytes
// in (returns the content-hash name), GET serves them back out by that name. Lives on the ASSET tree
// (InstanceApp — a sibling of /ws, /js, /session), never the app tree, so the app URL space stays
// reserved-path-free. DORMANT-OPEN in this slice — no upload ticket/auth (that is slice 2's job per
// the design doc's §2; today's upload floor mirrors every other dormant-app write, which is already
// fully open). Speaks to disk ONLY through IBlobPool — never File.* directly (the distributed-ACID
// rung-H IO-seam guard, docs/plans/distributed-acid-design.md rung H: new storage IO goes behind a
// seam).
//
// GenHTTP STREAMING FINDING (Task 0 spike, assets slice 1 build — recorded here, not in a committed
// test, per the build brief): a raw-socket probe (a throttled, delayed multi-write POST against a
// bare GenHTTP.Engine.Internal 10.5.3 host) proved this engine does NOT hand a Content-Length-declared
// request body to the handler incrementally. A 2 MB body sent over ~1.8s of delayed writes was received
// by GenHTTP in full BEFORE HandleAsync ever ran (the handler started at the 1.89s mark — right after
// the client's LAST write — and then read the entire 2 MB back in ~7ms, i.e. it was already fully
// resident). So the design doc's "hash while streaming, abort mid-flight to bound RAM" claim DOES NOT
// HOLD for this HTTP layer today: GenHTTP itself buffers the whole request body before this handler is
// invoked, regardless of the streaming read loop below. That loop and the mid-stream cap check are kept
// anyway — they are still the CORRECT behavior (never persist bytes past the cap; delete the temp; never
// buffer the file a SECOND time in our own memory) and are forward-looking if GenHTTP's buffering
// behavior ever changes — but they do NOT bound PEAK RAM the way the design assumed; a client can still
// force GenHTTP to hold up to the full cap (10 MB) in ITS OWN buffer before we see a single byte. Flagged
// loudly per the build brief; not silently absorbed. (Chunked Transfer-Encoding requests failed outright
// against this engine version in the same probe — moot for our real client, which POSTs a File with a
// known Content-Length, never chunked.)
public sealed class AssetsHandler(IBlobPool pool) : IHandler
{
    private static readonly IReadOnlyDictionary<string, string> ContentTypeToExt = new Dictionary<string, string>
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/gif"] = "gif",
        ["image/webp"] = "webp",
        // No SVG — deliberate (assets-design.md §6): an inline-served SVG executes script in the
        // response's own origin, a phishing/XSS vector this allowlist exists to close.
    };

    private static readonly IReadOnlyDictionary<string, string> ExtToContentType =
        ContentTypeToExt.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key);

    // A bare content hash + extension — the ENTIRE anti-path-traversal defense on the serve edge
    // (assets-design.md §3): reject anything else with 404 BEFORE any filesystem use.
    private static readonly Regex NamePattern =
        new(@"^[0-9a-f]{64}\.[a-z0-9]+$", RegexOptions.Compiled);

    private const long MaxUploadBytes = 10 * 1024 * 1024; // 10 MB (assets-design.md §2)
    private const int ReadBufferSize = 64 * 1024;

    public ValueTask PrepareAsync() => ValueTask.CompletedTask;

    public ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        if (request.Method.KnownMethod == RequestMethod.Options)
            return new ValueTask<IResponse?>(Cors(request, request.Respond().Status(ResponseStatus.NoContent)).Build());

        var remaining = request.Target.GetRemaining();
        var rest = remaining.IsRoot ? "" : remaining.ToString().Trim('/');

        if (request.Method.KnownMethod == RequestMethod.Post && rest.Length == 0)
            return HandleUploadAsync(request);

        if (request.Method.KnownMethod == RequestMethod.Get && rest.Length > 0)
            return new ValueTask<IResponse?>(HandleServe(request, rest));

        return new ValueTask<IResponse?>(request.Respond().Status(ResponseStatus.NotFound).Build());
    }

    // ── upload (bytes in) ───────────────────────────────────────────────────────────────
    private async ValueTask<IResponse?> HandleUploadAsync(IRequest request)
    {
        var contentType = request.ContentType.RawType;
        if (contentType is null || !ContentTypeToExt.TryGetValue(contentType, out var ext))
            return Cors(request, request.Respond().Status(ResponseStatus.UnsupportedMediaType)).Build();
        // A body-less POST (no Content stream at all) — the /session precedent (ContentHandler
        // SessionHandler) treats this the same defensive way.
        if (request.Content is null)
            return Cors(request, request.Respond().Status(ResponseStatus.BadRequest)).Build();

        var tempName = ".tmp-" + Guid.NewGuid().ToString("N");
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var overCap = false;
        await using (var dest = pool.OpenWrite(tempName))
        {
            var buffer = new byte[ReadBufferSize];
            int n;
            while ((n = await request.Content.ReadAsync(buffer)) > 0)
            {
                total += n;
                if (total > MaxUploadBytes) { overCap = true; break; }
                hasher.AppendData(buffer, 0, n);
                await dest.WriteAsync(buffer.AsMemory(0, n));
            }
        }
        if (overCap)
        {
            pool.DeleteTemp(tempName);
            // Drain whatever the client still has queued so the connection is left in a usable state
            // for the response (a half-read body can wedge a keep-alive connection) — bounded so a
            // client that never stops sending cannot hang this handler forever.
            var drain = new byte[ReadBufferSize];
            long drained = 0;
            int n;
            while (drained < 8 * MaxUploadBytes && (n = await request.Content.ReadAsync(drain)) > 0)
                drained += n;
            return Cors(request, request.Respond().Status(ResponseStatus.RequestEntityTooLarge)).Build();
        }

        var hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
        var name = pool.CommitBlob(tempName, hash, ext);
        var body = JsonSerializer.Serialize(new { name }, SchemaJson.Options);
        return Cors(request, request.Respond().Content(body).Type(ContentType.ApplicationJson)).Build();
    }

    // ── serve (bytes out) ───────────────────────────────────────────────────────────────
    private IResponse? HandleServe(IRequest request, string name)
    {
        if (!NamePattern.IsMatch(name))
            return request.Respond().Status(ResponseStatus.NotFound).Build();

        var ext = name[(name.LastIndexOf('.') + 1)..];
        // Unreachable given the regex+allowlist are built from the SAME extension set above — kept as
        // a defensive fallback, never a live branch.
        if (!ExtToContentType.TryGetValue(ext, out var contentType))
            return request.Respond().Status(ResponseStatus.NotFound).Build();

        var stream = pool.OpenRead(name);
        if (stream is null)
        {
            // A well-formed but absent hash: erasure/never-uploaded. The generic UI's <img> shows its
            // broken-image state — that IS the erasure UX (assets-design.md §3), no extra code.
            return request.Respond().Status(ResponseStatus.NotFound).Build();
        }

        return request.Respond()
            .Content(stream, (ulong)stream.Length)
            .Type(FlexibleContentType.Parse(contentType))
            // Content-addressed: the URL's bytes can never change, so this is free CDN-grade caching —
            // and the erasure-vs-cache trade it buys is named as a ceiling in the design doc.
            .Header("Cache-Control", "public, max-age=31536000, immutable")
            .Header("X-Content-Type-Options", "nosniff")
            .Build();
    }

    // Same-HOST CORS echo (ignores port — what makes the dev two-port setup work: the app page's
    // origin and the asset port's origin share a hostname, just different ports), mirroring
    // SessionHandler's own same-host check for the SAME reason (a cross-port browser call with no
    // ambient cookie to fall back on). Deliberately NOT shared code with SessionHandler (a small,
    // self-contained duplicate here is safer than reshaping a working, auth-adjacent handler for a
    // ~10-line save). Prod's dedicated assets.deenv.org domain (a genuinely different HOSTNAME) will
    // need same-SITE matching instead — deferred to the slice that builds that domain.
    private static IResponseBuilder Cors(IRequest request, IResponseBuilder response) =>
        request.Headers.TryGetValue("Origin", out var origin)
        && request.Host is { } host
        && SameHostOrigin(origin, host)
            ? response
                .Header("Access-Control-Allow-Origin", origin)
                .Header("Access-Control-Allow-Methods", "POST, OPTIONS")
                .Header("Access-Control-Allow-Headers", "Content-Type")
            : response;

    private static bool SameHostOrigin(string origin, string host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, host.Split(':')[0], StringComparison.OrdinalIgnoreCase);
}

public sealed class AssetsHandlerBuilder(IBlobPool pool) : IHandlerBuilder
{
    public IHandler Build() => new AssetsHandler(pool);
}
