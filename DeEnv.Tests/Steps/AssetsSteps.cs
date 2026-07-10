using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeEnv.Code;
using DeEnv.Http;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for Assets.feature (docs/plans/assets-design.md) — the content-addressed blob pool's two HTTP
// edges, driven directly over HTTP against the asset port (ctx.AssetBaseUrl), and the `image` scalar's
// store round-trip via ctx.Store directly (no WS, no browser — a plain data-layer check, the same
// class as StoredDataSteps' "reopened store" scenarios). "The store is opened again on the same data
// file" is REUSED verbatim from StoredDataSteps.cs (it just rebuilds ctx.Store from ctx.DataFilePath).
//
// The cookie-auth scenarios (§2, slice 2b — supersedes the slice-2 ticket) obtain a REAL session cookie
// the exact way a browser does: POST /session (the SessionHandler HTTP endpoint) with a cookie-aware
// HttpClient, then reuse that SAME client for the upload POST so its CookieContainer attaches the cookie
// automatically — no manual header slicing, no second auth path in the test harness.
[Binding]
public sealed class AssetsSteps(InstanceContext ctx)
{
    private byte[] _lastBytes = [];
    private int _lastStatus;
    private string _lastUploadedName = "";
    private readonly List<string> _uploadedNames = [];

    // GET-response state (headers read into plain fields — HttpResponseMessage itself is disposed
    // right after each request).
    private byte[] _lastGetBody = [];
    private string _lastContentType = "";
    private string _lastCacheControl = "";
    private string _lastNosniff = "";

    // Own unique temp DIRECTORY, not the InstanceContext default's bare Path.GetTempFileName() — see
    // GivenRuledAssetsAppRunning's doc for why: AppPaths.BlobsDirForDataPath is "<data file's
    // directory>/blobs", and GetTempFileName() always creates its file directly in the shared OS temp
    // root, so every Assets scenario using the default would resolve to the SAME pool directory and
    // pollute each other's "pool is empty" assertions under parallel execution (a committed blob from
    // one scenario shows up in another's rejection check). Own directory ⇒ own pool, for every scenario
    // in this feature (not just the ruled ones).
    [Given("the assets app is running")]
    public async Task GivenAssetsAppRunning()
    {
        ctx.Description = InstanceContext.AssetsDb();
        var dir = Path.Combine(Path.GetTempPath(), "deenv-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ctx.DataFilePath = Path.Combine(dir, "data.json");
        await ctx.EnsureServerAsync();
    }

    // The Background above already started the (browser-less) server; this just adds a page — the
    // browser scenario's own extra Given, mirroring EnsureServerAndBrowserAsync's idempotent reuse of
    // whatever server EnsureServerAsync already started.
    [Given("a browser is open on the assets app")]
    public async Task GivenBrowserOpenOnAssetsApp() => await ctx.EnsureServerAndBrowserAsync();

    // Assets slice 3 (§4, the composition proof): the same own-temp-dir reasoning as
    // GivenAssetsAppRunning, but swaps in the custom-render fixture (AssetsCustomUiDb) instead of the
    // dormant generic-UI one — a fully-custom `ui fn render()` composing the public ImageInput component
    // + sys.assetUrl, no ObjectForm involved. The Background already started a server bound to the
    // GENERIC-UI description (AssetsDb) — EnsureServerAsync is a no-op once ctx.Server is non-null (see
    // GivenBrowserOpenOnAssetsApp's own comment), so that server must be torn down first, exactly like
    // GivenRuledAssetsAppRunning does for the ruled fixture.
    [Given("a browser is open on the custom-photo assets app")]
    public async Task GivenBrowserOpenOnCustomPhotoAssetsApp()
    {
        if (ctx.Server != null) { await ctx.Server.DisposeAsync(); ctx.Server = null; }
        ctx.Description = InstanceContext.AssetsCustomUiDb();
        var dir = Path.Combine(Path.GetTempPath(), "deenv-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ctx.DataFilePath = Path.Combine(dir, "data.json");
        await ctx.EnsureServerAndBrowserAsync();
    }

    [When("I reload the page")]
    public async Task WhenIReloadThePage() => await ctx.Page!.ReloadAsync();

    // Reads the hydrated <img class="custom-photo"> src directly — proving the custom render's own
    // sys.assetUrl(db.photo) composition produced a real pool URL, not just that SOME element appeared.
    [Then("the custom photo thumbnail src matches the pool pattern for extension {string}")]
    public async Task ThenCustomPhotoThumbnailSrcMatchesPoolPattern(string ext)
    {
        var img = ctx.Page!.Locator("img.custom-photo").First;
        await img.WaitForAsync();
        var src = await img.GetAttributeAsync("src") ?? "";
        var name = src.Contains('/') ? src[(src.LastIndexOf('/') + 1)..] : src;
        await Assert.That(Regex.IsMatch(name, $"^[0-9a-f]{{64}}\\.{Regex.Escape(ext)}$")).IsTrue();
    }

    // ── the ruled instance (assets slice 2, §2) ──────────────────────────────────

    // The Background's own "the assets app is running" already started a DORMANT server this scenario —
    // EnsureServerAsync is a no-op once Server is non-null, and AssetsHandler bakes its dormant/ruled
    // posture in at construction (from the description it was BUILT with), so swapping ctx.Description
    // alone would not change the running server's posture. This tears that server down and starts a
    // fresh one bound to the RULED fixture instead.
    //
    // The fresh data file lives in its OWN unique temp DIRECTORY, not a bare Path.GetTempFileName() —
    // AppPaths.BlobsDirForDataPath derives the pool dir as "<data file's directory>/blobs", and
    // GetTempFileName() always creates its file directly in the shared OS temp root, so two scenarios
    // using the default pattern would resolve to the SAME "<temp>/blobs" directory and pollute each
    // other's "no temp file remains" assertion under parallel execution (caught by a real, non-flaky
    // full-suite failure — a leftover .tmp- file from a sibling scenario). Own directory ⇒ own pool.
    [Given("the ruled assets app is running")]
    public async Task GivenRuledAssetsAppRunning()
    {
        if (ctx.Server != null) { await ctx.Server.DisposeAsync(); ctx.Server = null; }
        ctx.Description = InstanceContext.AssetsRuledDb();
        var dir = Path.Combine(Path.GetTempPath(), "deenv-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ctx.DataFilePath = Path.Combine(dir, "data.json");
        await ctx.EnsureServerAsync();
    }

    // Seed the fixture's User (id 2, "Alice") with a real PBKDF2 hash — the same pre-hashed-write
    // pattern LoginSteps' "the admin user has the password" uses, written raw through the store seam.
    [Given("the seeded user has the password {string}")]
    public void GivenSeededUserPassword(string password) =>
        ctx.Store!.WriteField(InstanceContext.AssetsRuledUserId, "password", new TextValue(AuthCrypto.Hash(password)));

    // A cookie-aware client for the session that just logged in — reused by the next upload step so its
    // CookieContainer attaches the Set-Cookie'd session cookie automatically, the exact browser mechanism
    // (assets slice 2b: upload auth is the ambient session cookie, not a minted ticket).
    private HttpClient? _cookieClient;

    // POST /session — the REAL SessionHandler HTTP endpoint (the same one the browser's persistLogin
    // calls, ws.ts), so the cookie this captures is byte-identical to what a real login produces. A
    // failed login still leaves `_cookieClient` set (no cookie was set, so the next upload rides none —
    // exercised implicitly by "ruled + garbage cookie" using its OWN client instead).
    [When("the session logs in as {string} with password {string} via the session endpoint")]
    public async Task WhenLogsInViaSessionEndpoint(string name, string password)
    {
        _cookieClient = new HttpClient(new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        });
        var body = JsonSerializer.Serialize(new { name, password });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await _cookieClient.PostAsync(ctx.AssetBaseUrl + "/session", content);
        await Assert.That(response.IsSuccessStatusCode).IsTrue();
        var replyBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(replyBody);
        await Assert.That(doc.RootElement.GetProperty("ok").GetBoolean()).IsTrue();
    }

    [When("I upload {int} random bytes as {string} with the session's cookie")]
    public async Task WhenIUploadWithSessionCookie(int count, string contentType)
    {
        var bytes = new byte[count];
        new Random(4242).NextBytes(bytes);
        await UploadAsync(bytes, contentType, client: _cookieClient);
    }

    [When("I upload {int} random bytes as {string} with no cookie")]
    public async Task WhenIUploadWithNoCookie(int count, string contentType)
    {
        var bytes = new byte[count];
        new Random(4242).NextBytes(bytes);
        await UploadAsync(bytes, contentType);
    }

    // A garbage Cookie header under the REAL per-instance cookie name (TokenAuth.CookiePrefix +
    // TestInstanceServer.InstanceId) — TokenAuth.Verify must reject it (wrong shape, no valid signature)
    // exactly like it would reject a tampered real cookie.
    [When("I upload {int} random bytes as {string} with a garbage cookie")]
    public async Task WhenIUploadWithGarbageCookie(int count, string contentType)
    {
        var bytes = new byte[count];
        new Random(4242).NextBytes(bytes);
        var cookieHeader = $"{TokenAuth.CookiePrefix}{TestInstanceServer.InstanceId}=not-a-real-cookie";
        await UploadAsync(bytes, contentType, rawCookieHeader: cookieHeader);
    }

    // CSRF belt-and-braces (assets slice 2b, §2): a POST whose Origin does not match the request's own
    // host must be rejected 403 BEFORE any auth/MIME/disk work — proven here independent of dormant/ruled
    // posture (the Background's dormant server is enough; the Origin check runs unconditionally).
    [When("I upload {int} random bytes as {string} with the Origin header {string}")]
    public async Task WhenIUploadWithForeignOrigin(int count, string contentType, string origin)
    {
        var bytes = new byte[count];
        new Random(4242).NextBytes(bytes);
        await UploadAsync(bytes, contentType, originOverride: origin);
    }

    // Playwright sets the file directly from an in-memory buffer (FilePayload) — no real file on disk,
    // no OS file-picker dialog — which fires the SAME input+change events wireEvents (ui.ts) listens for
    // on a real user pick, exercising the whole ImageInput → uploadBlob → sys.field persist path.
    [When("I upload a real image file to the photo field")]
    public async Task WhenIUploadRealImageFileToPhotoField()
    {
        var bytes = new byte[40];
        new Random(99).NextBytes(bytes);
        var input = ctx.Page!.Locator("input[type=file]").First;
        await input.SetInputFilesAsync(new Microsoft.Playwright.FilePayload
        {
            Name = "photo.png",
            MimeType = "image/png",
            Buffer = bytes,
        });
    }

    // ── upload ────────────────────────────────────────────────────────────────

    [When("I upload {int} random bytes as {string}")]
    public async Task WhenIUploadRandomBytes(int count, string contentType)
    {
        var bytes = new byte[count];
        new Random(4242).NextBytes(bytes); // deterministic content is fine — the pool addresses by hash, not identity
        await UploadAsync(bytes, contentType);
    }

    [When("I upload the same bytes again as {string}")]
    public async Task WhenIUploadSameBytesAgain(string contentType) => await UploadAsync(_lastBytes, contentType);

    // `client` — when supplied, reuse a cookie-aware HttpClient (the login flow's) so its CookieContainer
    // attaches the session cookie automatically; null uses a fresh cookie-less client (the "no cookie"
    // and CSRF scenarios). `rawCookieHeader` sends a LITERAL Cookie header (the garbage-cookie scenario,
    // which needs a cookie NAME the CookieContainer would never produce on its own). `originOverride`
    // sends an explicit Origin header (the CSRF scenario) instead of the plain same-host-implicit case
    // (a bare HttpClient sends no Origin at all, which the handler treats as "not a browser" and allows).
    private async Task UploadAsync(byte[] bytes, string contentType,
        HttpClient? client = null, string? rawCookieHeader = null, string? originOverride = null)
    {
        _lastBytes = bytes;
        var http = client ?? new HttpClient();
        try
        {
            using var content = new ByteArrayContent(bytes);
            content.Headers.Remove("Content-Type");
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            using var request = new HttpRequestMessage(HttpMethod.Post, ctx.AssetBaseUrl + "/assets") { Content = content };
            if (rawCookieHeader != null) request.Headers.TryAddWithoutValidation("Cookie", rawCookieHeader);
            if (originOverride != null) request.Headers.TryAddWithoutValidation("Origin", originOverride);
            var response = await http.SendAsync(request);
            _lastStatus = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                _lastUploadedName = doc.RootElement.GetProperty("name").GetString() ?? "";
                _uploadedNames.Add(_lastUploadedName);
            }
        }
        finally
        {
            if (client == null) http.Dispose();
        }
    }

    [Then("the uploaded name matches the pool pattern for extension {string}")]
    public async Task ThenUploadedNameMatchesPattern(string ext)
    {
        await Assert.That(_lastStatus).IsEqualTo(200);
        await Assert.That(Regex.IsMatch(_lastUploadedName, $"^[0-9a-f]{{64}}\\.{Regex.Escape(ext)}$")).IsTrue();
    }

    [Then("both uploads returned the same name")]
    public async Task ThenBothUploadsSameName()
    {
        await Assert.That(_uploadedNames.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(_uploadedNames[^1]).IsEqualTo(_uploadedNames[^2]);
    }

    // ── serve ─────────────────────────────────────────────────────────────────

    [When("I GET the uploaded blob")]
    public async Task WhenIGetUploadedBlob() => await GetAsync(_lastUploadedName);

    [When("I GET the asset name {string}")]
    public async Task WhenIGetAssetName(string name) => await GetAsync(name);

    [When("I GET a well-formed but absent blob name")]
    public async Task WhenIGetAbsentBlobName() => await GetAsync(new string('0', 64) + ".png");

    private async Task GetAsync(string name)
    {
        using var http = new HttpClient();
        // The candidate name is sent as ONE opaque path segment (Uri.EscapeDataString) rather than
        // spliced raw into the URL string — so a traversal probe like "../../secret.txt" reaches the
        // SERVER's own name-shape guard (AssetsHandler's regex) as the literal string it is, instead of
        // being dot-segment-collapsed by the CLIENT's own URI normalization before the request is even
        // sent (which would test .NET's URI parser, not this handler).
        var response = await http.GetAsync(ctx.AssetBaseUrl + "/assets/" + Uri.EscapeDataString(name));
        _lastStatus = (int)response.StatusCode;
        _lastGetBody = await response.Content.ReadAsByteArrayAsync();
        _lastContentType = response.Content.Headers.ContentType?.MediaType ?? "";
        _lastCacheControl = response.Headers.CacheControl?.ToString() ?? "";
        _lastNosniff = response.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join(",", v) : "";
    }

    [Then("the asset response status is {int}")]
    public async Task ThenAssetResponseStatus(int code) => await Assert.That(_lastStatus).IsEqualTo(code);

    [Then("the asset response body is the uploaded bytes")]
    public async Task ThenAssetResponseBodyIsUploadedBytes() =>
        await Assert.That(Convert.ToBase64String(_lastGetBody)).IsEqualTo(Convert.ToBase64String(_lastBytes));

    [Then("the asset response content type is {string}")]
    public async Task ThenAssetResponseContentTypeIs(string contentType) =>
        await Assert.That(_lastContentType).IsEqualTo(contentType);

    [Then("the asset response is cacheable, immutable, and marked nosniff")]
    public async Task ThenAssetResponseCacheableImmutableNosniff()
    {
        await Assert.That(_lastCacheControl).Contains("immutable");
        await Assert.That(_lastCacheControl).Contains("max-age=31536000");
        await Assert.That(_lastNosniff).IsEqualTo("nosniff");
    }

    // ── the `image` scalar's store round-trip ────────────────────────────────────

    [When("I set the Db {string} field to the uploaded name")]
    public void WhenSetDbFieldToUploadedName(string field) =>
        ctx.Store!.WriteField(1, field, new TextValue(_lastUploadedName));

    [Then("the store eventually has a {string} whose {string} field is the uploaded name")]
    public async Task ThenStoreHasFieldEqualToUploadedName(string typeName, string field) =>
        await Polling.EventuallyAsync(() => DbFieldIs(ctx.Store!, field, _lastUploadedName),
            $"the store's {typeName}.{field} to read '{_lastUploadedName}'");

    [Then("the reopened store's {string} {string} field is the uploaded name")]
    public async Task ThenReopenedStoreFieldIsUploadedName(string typeName, string field) =>
        await Assert.That(DbFieldIs(ctx.Store!, field, _lastUploadedName)).IsTrue();

    private static bool DbFieldIs(IInstanceStore store, string field, string expected) =>
        store.ReadById(1) is { Fields: var obj }
        && obj.Fields.TryGetValue(field, out var v)
        && v is TextValue t
        && t.Text == expected;

    // ── the size cap ──────────────────────────────────────────────────────────

    // Asserts the pool directory is EMPTY, not just free of `.tmp-*` names — the rejection scenarios
    // (the 10 MB cap, and the assets-slice-2b cookie/CSRF rejections) protect a "nothing touches disk"
    // floor, not merely "no leftover temp file": a regression that moved the auth check to AFTER the
    // write loop (so a rejected upload still streamed bytes to a temp file, or even committed one) must
    // fail here. A `.tmp-*`-only glob would miss a COMMITTED blob landing before the reject — this
    // catches that too, since a rejection must never produce ANY pool file, named or not.
    [Then("no temp file remains in the pool")]
    public void ThenNoTempFileRemainsInPool()
    {
        var blobsDir = AppPaths.BlobsDirForDataPath(ctx.DataFilePath);
        if (!Directory.Exists(blobsDir)) return; // never created = trivially empty
        var files = Directory.GetFiles(blobsDir);
        if (files.Length > 0)
            throw new Exception($"Expected the pool to be empty, found: {string.Join(", ", files)}");
    }
}
