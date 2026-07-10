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

    // The upload ticket's mirror of the above (assets slice 2, docs/plans/assets-design.md §2): the
    // happy path, cross-instance scope binding (the ONE guard stopping instance A's ticket writing
    // instance B's pool), and cross-protocol non-replay in BOTH directions (a session cookie token must
    // not verify as a ticket, and a ticket must not verify as a session cookie — they share the same
    // Sign/secret machinery, so the tag prefix is the only thing keeping the two shapes apart).
    [Test]
    public async Task Upload_ticket_verification_binds_to_instance_and_never_cross_protocol_replays()
    {
        var desc = InstanceContext.AccessFixtureDb();
        var path = Path.GetTempFileName();
        var store = new JsonFileInstanceStore(path, desc);
        var auth = TokenAuth.Ephemeral();
        var hash = AuthCrypto.Hash("hunter2");
        store.WriteField(InstanceContext.AccessAdminId, InstanceContext.AccessPasswordField, new TextValue(hash));
        var now = DateTimeOffset.UtcNow;

        var (ticket, _) = auth.MintTicket(1, InstanceContext.AccessAdminId, now);

        // Happy path.
        await Assert.That(auth.VerifyTicket(ticket, 1, now)).IsEqualTo(InstanceContext.AccessAdminId);
        // (a) Cross-instance scope binding: a ticket minted for instance 1 must not verify for instance 2.
        await Assert.That(auth.VerifyTicket(ticket, 2, now)).IsNull();
        // (b) A session cookie token presented as a ticket must not verify.
        var cookieToken = auth.Mint(1, InstanceContext.AccessAdminId, hash, now);
        await Assert.That(auth.VerifyTicket(cookieToken, 1, now)).IsNull();
        // (c) A ticket presented as a session cookie token must not verify (the reverse direction).
        await Assert.That(auth.Verify(ticket, 1, store, desc, now)).IsNull();

        try { File.Delete(path); } catch { /* best-effort */ }
    }
}
