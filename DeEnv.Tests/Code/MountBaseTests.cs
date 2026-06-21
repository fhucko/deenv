using DeEnv.Http;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Code;

// The mount-base seam (SsrRenderer.MountUrl / StripBase) — the C# half of front-edge addressing. The
// instance is base-UNAWARE (root-relative `path` + links); the base is applied only at the edges. These
// pin the two helpers directly (cheap, no host): the SSR prefixes emitted root-relative links with the
// mount, and the WS refetch strips the mount off the client's full location.pathname. The TS twins
// (ui.ts mountUrl/stripBase) mirror these exactly so SSR and hydrate agree.
public sealed class MountBaseTests
{
    // ── MountUrl: prefix a root-relative URL with the mount (link/breadcrumb/asset emission) ──

    [Test]
    [Arguments("/apps/todo", "/", "/apps/todo")]            // the instance root → the bare mount
    [Arguments("/apps/todo", "/notes/2", "/apps/todo/notes/2")]
    [Arguments("/apps/todo", "/designs", "/apps/todo/designs")]
    [Arguments("/", "/notes/2", "/notes/2")]               // root-mounted → identity
    [Arguments("/", "/", "/")]
    public async Task MountUrl_prefixes_root_relative_urls(string @base, string url, string expected) =>
        await Assert.That(SsrRenderer.MountUrl(@base, url)).IsEqualTo(expected);

    [Test]
    [Arguments("/apps/todo", "https://x.com/a", "https://x.com/a")] // absolute → untouched
    [Arguments("/apps/todo", "//cdn/x", "//cdn/x")]                 // protocol-relative → untouched
    [Arguments("/apps/todo", "#frag", "#frag")]                    // fragment → untouched
    [Arguments("/apps/todo", "rel/path", "rel/path")]              // relative → untouched
    public async Task MountUrl_leaves_non_root_relative_urls(string @base, string url, string expected) =>
        await Assert.That(SsrRenderer.MountUrl(@base, url)).IsEqualTo(expected);

    // ── StripBase: recover the root-relative path from a full location.pathname (WS refetch) ──

    [Test]
    [Arguments("/apps/todo", "/apps/todo/notes/2", "/notes/2")]
    [Arguments("/apps/todo", "/apps/todo", "/")]            // the instance root
    [Arguments("/apps/todo", "/apps/todo/", "/")]           // with a trailing slash
    [Arguments("/", "/notes/2", "/notes/2")]               // root-mounted → identity
    [Arguments("/apps/todo", "/notes/2", "/notes/2")]      // already root-relative (domain-root case) → unchanged
    public async Task StripBase_recovers_root_relative_paths(string @base, string full, string expected) =>
        await Assert.That(SsrRenderer.StripBase(@base, full)).IsEqualTo(expected);

    // MountUrl ∘ StripBase round-trips a real navigation: the client pushes mountUrl(path), the browser
    // sends it back, the server strips it to the original root-relative path. (The "/" root case is the
    // identity both ways; this pins the path-mounted case.)
    [Test]
    [Arguments("/apps/todo", "/notes/2")]
    [Arguments("/apps/todo", "/")]
    [Arguments("/", "/notes/2")]
    public async Task StripBase_inverts_MountUrl(string @base, string path) =>
        await Assert.That(SsrRenderer.StripBase(@base, SsrRenderer.MountUrl(@base, path))).IsEqualTo(path);
}
