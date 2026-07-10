using DeEnv.Code;
using DeEnv.Http;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

public sealed class TokenAuthTests
{
    [Test]
    public async Task Token_verification_rejects_tamper_foreign_instance_and_password_change()
    {
        var desc = InstanceContext.AccessFixtureDb();
        var path = Path.GetTempFileName();
        var store = new JsonFileInstanceStore(path, desc);
        var auth = TokenAuth.Ephemeral();
        var hash = AuthCrypto.Hash("hunter2");
        store.WriteField(InstanceContext.AccessAdminId, InstanceContext.AccessPasswordField, new TextValue(hash));

        var token = auth.Mint(1, InstanceContext.AccessAdminId, hash, DateTimeOffset.UtcNow);

        await Assert.That(auth.Verify(token, 1, store, desc, DateTimeOffset.UtcNow)).IsEqualTo(InstanceContext.AccessAdminId);
        await Assert.That(auth.Verify(token + "x", 1, store, desc, DateTimeOffset.UtcNow)).IsNull();
        await Assert.That(auth.Verify(token, 2, store, desc, DateTimeOffset.UtcNow)).IsNull();
        await Assert.That(auth.Verify(token, 1, store, desc, DateTimeOffset.UtcNow.AddDays(31))).IsNull();

        store.WriteField(InstanceContext.AccessAdminId, InstanceContext.AccessPasswordField,
            new TextValue(AuthCrypto.Hash("changed")));
        await Assert.That(auth.Verify(token, 1, store, desc, DateTimeOffset.UtcNow)).IsNull();

        try { File.Delete(path); } catch { /* best-effort */ }
    }
}
