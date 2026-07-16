using DeEnv.Code;
using DeEnv.Instance;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Code;

// M-auth login UI sub-slice 1e-1 — the `anonymousLockedOut` signal (AccessFloor.AnonymousLockedOut). A
// fast, DETERMINISTIC, no-browser guard for the auto-mode login gate's input: it is computed from the
// RULES alone (data-independent), so these parse a real `access` section through the loader (the same
// path the app document takes) and assert the signal over the parsed AccessRule[] — no store, no render,
// no data seeded, proving the "correct even when every list is empty" property directly. The browser
// scenario (Access.feature) then proves the gate USES it end-to-end; this isolates the logic so a flaky
// browser run is not the only signal that the gate's condition is right.
public sealed class AnonymousLockedOutTests
{
    // A minimal app whose `access` section carries the given Milestone rule lines (each a verb list +
    // optional `where`), built the SAME way InstanceContext.AccessFixtureWithRules builds it (the proven
    // indentation). No initialData — the signal must not depend on data — so the rules are the only
    // variable. Each rule line is placed at the 8-space rule indent under the `Milestone` type block.
    private static IReadOnlyList<AccessRule> Rules(params string[] ruleLines) =>
        InstanceDescriptionLoader.Load(
            """
            types
                Db
                    milestones set of Milestone
                Milestone
                    title text
                    status text

            access
                Milestone

            """ +
            string.Concat(ruleLines.Select(l => "        " + l + "\n"))).Rules ?? [];

    private static bool Locked(params string[] ruleLines) => AccessFloor.AnonymousLockedOut(Rules(ruleLines));

    // A dormant app (no `access` section at all) is never gated — allow-all, today's behavior.
    [Test]
    public async Task A_dormant_app_with_no_rules_is_not_locked_out() =>
        await Assert.That(AccessFloor.AnonymousLockedOut(InstanceDescriptionLoader.Load("""
        types
            Db
                milestones set of Milestone
            Milestone
                title text
        """).Rules ?? [])).IsFalse();

    // A read rule gated on the principal's role grants NO anonymous read → locked out. This is the
    // access-fixture's exact rule and the gate's primary case.
    [Test]
    public async Task A_role_gated_read_rule_locks_anonymous_out() =>
        await Assert.That(Locked("read where currentUser.role == \"Admin\"")).IsTrue();

    // A bare `read` (no condition) grants anonymous unconditionally → NOT locked out (a fully public app).
    [Test]
    public async Task A_bare_read_rule_does_not_lock_anonymous_out() =>
        await Assert.That(Locked("read")).IsFalse();

    // A data-only read condition (no currentUser reference) CAN hold for an anonymous visitor → NOT locked
    // out: the static check sees no currentUser in `status == "published"`, so public rows stay public.
    [Test]
    public async Task A_data_only_read_condition_does_not_lock_anonymous_out() =>
        await Assert.That(Locked("read where status == \"published\"")).IsFalse();

    // Two read rules where the public one grants anonymous → NOT locked out, even though the other is
    // role-gated. ANY anonymous-granting read rule is enough (the floor allows iff any applicable rule holds).
    [Test]
    public async Task A_public_read_rule_alongside_a_role_rule_does_not_lock_out() =>
        await Assert.That(Locked(
            "read where currentUser.role == \"Admin\"",
            "read where status == \"published\"")).IsFalse();

    // Rules exist but NONE is a read rule (only a write rule): reads are UNRULED, so the floor lets an
    // anonymous visitor read freely → NOT locked out. (Gating here would show a login form over readable
    // data — the floor-consistency fix.)
    [Test]
    public async Task An_app_with_only_a_write_rule_is_not_locked_out() =>
        await Assert.That(Locked("edit where currentUser.role == \"Admin\"")).IsFalse();

    // A `*` (all-verbs) rule grants `read` too; gated on the principal → locks anonymous out.
    [Test]
    public async Task A_role_gated_wildcard_rule_locks_anonymous_out() =>
        await Assert.That(Locked("* where currentUser.role == \"Admin\"")).IsTrue();
}
