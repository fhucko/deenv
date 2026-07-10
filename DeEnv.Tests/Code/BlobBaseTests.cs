using DeEnv.Http;

namespace DeEnv.Tests.Code;

// Pins the two shapes of the blob SERVE base (SsrRenderer.BlobBase, assets-design.md §Origin):
// derived from the asset authority in dev; the dedicated blob domain carrying the instance in its
// PATH when DEENV_PUBLIC_BLOB_BASE is set (slice 4). The upload URL deliberately does NOT use this
// base (ws.ts uploadBlob posts same-origin) — these tests cover serve resolution only.
public class BlobBaseTests
{
    [Test]
    public async Task Dev_shape_derives_from_asset_authority_and_mount_base()
    {
        await Assert.That(SsrRenderer.BlobBase("/apps/demo", "localhost:8081", "demo", null))
            .IsEqualTo("//localhost:8081/apps/demo/assets");
        await Assert.That(SsrRenderer.BlobBase("/", "", "demo", null))
            .IsEqualTo("/assets");
    }

    [Test]
    public async Task Prod_shape_is_the_blob_domain_with_the_instance_in_the_path()
    {
        await Assert.That(SsrRenderer.BlobBase("/", "", "devlog", "https://assets.deenv.org"))
            .IsEqualTo("https://assets.deenv.org/devlog");
        // The override wins regardless of authority/base — serve moves wholesale to the blob domain.
        await Assert.That(SsrRenderer.BlobBase("/apps/demo", "localhost:8081", "demo", "https://assets.deenv.org"))
            .IsEqualTo("https://assets.deenv.org/demo");
    }
}
