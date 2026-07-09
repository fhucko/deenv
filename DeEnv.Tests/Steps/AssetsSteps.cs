using System.Text.Json;
using System.Text.RegularExpressions;
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

    [Given("the assets app is running")]
    public async Task GivenAssetsAppRunning()
    {
        ctx.Description = InstanceContext.AssetsDb();
        await ctx.EnsureServerAsync();
    }

    // The Background above already started the (browser-less) server; this just adds a page — the
    // browser scenario's own extra Given, mirroring EnsureServerAndBrowserAsync's idempotent reuse of
    // whatever server EnsureServerAsync already started.
    [Given("a browser is open on the assets app")]
    public async Task GivenBrowserOpenOnAssetsApp() => await ctx.EnsureServerAndBrowserAsync();

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

    private async Task UploadAsync(byte[] bytes, string contentType)
    {
        _lastBytes = bytes;
        using var http = new HttpClient();
        using var content = new ByteArrayContent(bytes);
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        var response = await http.PostAsync(ctx.AssetBaseUrl + "/assets", content);
        _lastStatus = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            _lastUploadedName = doc.RootElement.GetProperty("name").GetString() ?? "";
            _uploadedNames.Add(_lastUploadedName);
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

    [Then("no temp file remains in the pool")]
    public void ThenNoTempFileRemainsInPool()
    {
        var blobsDir = AppPaths.BlobsDirForDataPath(ctx.DataFilePath);
        if (!Directory.Exists(blobsDir)) return; // never created = trivially no temp file
        var temps = Directory.GetFiles(blobsDir, ".tmp-*");
        if (temps.Length > 0)
            throw new Exception($"Expected no temp file in the pool, found: {string.Join(", ", temps)}");
    }
}
