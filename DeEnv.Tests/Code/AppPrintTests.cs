using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Code;
using DeEnv.Instance;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Stage 4 of the text-syntax milestone: the printer. parse(print(desc)) must equal
// desc (structural equality over the serialized description), and the canonical
// printed form is a fixpoint — printing it again reproduces it byte for byte.
public sealed class AppPrintTests
{
    private static async Task AssertRoundTrips(string appText)
    {
        var first = AppParse.Parse(appText);
        var printed = AppPrint.Print(first);
        var second = AppParse.Parse(printed);

        var a = JsonSerializer.SerializeToNode(first, SchemaJson.Options)!;
        var b = JsonSerializer.SerializeToNode(second, SchemaJson.Options)!;
        if (!JsonNode.DeepEquals(a, b))
            await Assert.That(b.ToJsonString()).IsEqualTo(a.ToJsonString());

        await Assert.That(AppPrint.Print(second)).IsEqualTo(printed);
    }

    [Test]
    public async Task The_todo_app_round_trips()
    {
        await AssertRoundTrips(File.ReadAllText(InstanceContext.AppFixture(2)));
    }

    [Test]
    public async Task The_code_fixtures_round_trip()
    {
        foreach (var app in InstanceContext.CodeFixtureApps)
            await AssertRoundTrips(app);
    }

    [Test]
    public async Task The_crm_and_designer_documents_round_trip()
    {
        await AssertRoundTrips(File.ReadAllText(InstanceContext.AppFixture(3)));
        await AssertRoundTrips(File.ReadAllText(InstanceContext.AppFixture(1)));
    }

    // The committed devlog app (instances/5) — migrated to a `password password` User field (the M-auth
    // `password` type) — round-trips AND LOADS cleanly (full validation, not just parse): proves the
    // migration is a valid document, the `password` type parses/prints/validates in a real committed app.
    [Test]
    public async Task The_devlog_document_round_trips_and_loads()
    {
        var text = File.ReadAllText(InstanceContext.AppFixture(5));
        await AssertRoundTrips(text);
        var desc = InstanceDescriptionLoader.Load(text); // full semantic validation
        var user = desc.FindType("User");
        await Assert.That(user!.Props!.Any(p => p.Type == "password")).IsTrue();
    }

    // An enum type (`Name: enum` + an indented value list) round-trips: parse∘print is the
    // identity and the printed form is a fixpoint, with the values in declared order.
    [Test]
    public async Task The_enum_fixture_round_trips()
    {
        await AssertRoundTrips(InstanceContext.EnumFixtureApp);
    }

    // A `text multiline` prop (the presentation attribute) round-trips: the trailing `multiline`
    // keyword is parsed onto the prop and printed back after the type, so parse∘print is the
    // identity and the printed form is a fixpoint. A plain `text`/`int` prop prints with no keyword.
    [Test]
    public async Task The_multiline_fixture_round_trips()
    {
        await AssertRoundTrips(InstanceContext.MultilineFixtureApp);
    }

    // The M-auth `access` section round-trips: parse∘print is the identity and the printed form is a
    // fixpoint, with the ruleset grouped by type. The fixture carries one type-level read rule. (The access
    // fixture's User now declares a `password password` field — so this also covers the `password` type's
    // round-trip; the dedicated test below isolates it.)
    [Test]
    public async Task The_access_fixture_round_trips()
    {
        await AssertRoundTrips(InstanceContext.AccessFixtureApp);
    }

    // A `password`-typed prop (`name password` on a type — the M-auth `password` type, a leaf base name that
    // maps to text but is its OWN BaseType member) round-trips: parse∘print is the identity (the prop prints
    // its declared type "password" back) and the printed form is a fixpoint. Proves BaseType.Password does not
    // break the printer the way a text-alias would have.
    [Test]
    public async Task A_password_typed_prop_round_trips()
    {
        await AssertRoundTrips("""
        types
            Db
                users set of User
            User
                name text
                password password
        """);
    }

    // A richer `access` section — a `where`-conditioned rule, a multi-verb rule, and a `*` (all-verbs)
    // rule, across two types — round-trips: the verb list prints space-joined, the optional condition
    // prints back via CodePrint, and rules group by type in first-appearance order (the canonical form).
    [Test]
    public async Task A_multi_verb_access_section_round_trips()
    {
        await AssertRoundTrips("""
        types
            Db
                tasks set of Task
            Task
                title text
            User
                name text

        access
            Task
                read where title == "published"
                read create edit where currentUser.role == "Member"
                *
            User
                read
        """);
    }

    // The host-action `sys` subject in the access section round-trips: it parses as a subject like a type
    // block (reserved keyword, not a user type), prints back grouped with the type rules in appearance
    // order, and is a fixpoint. The fixture carries both a data (type) rule and the `sys` rule.
    [Test]
    public async Task A_sys_host_action_access_rule_round_trips()
    {
        await AssertRoundTrips("""
        types
            Db
                tasks set of Task
            Task
                title text
            Role enum
                Admin
            User
                name text
                role Role

        access
            Task
                read where currentUser.role == "Admin"
            sys
                * where currentUser.role == "Admin"
        """);
    }

    // A bare (unconditional) `sys` rule round-trips too — the form the designer uses for now (an open
    // host-action grant): `*` with no `where`.
    [Test]
    public async Task A_bare_sys_access_rule_round_trips()
    {
        await AssertRoundTrips("""
        types
            Db
                designs set of Design
            Design
                label text

        access
            sys
                *
        """);
    }

    // `sys` is RESERVED and cannot be a TYPE name (it is the framework namespace AND the access `sys`
    // subject) — declaring `sys` as a type fails to load, exactly like the framework/user separation the
    // guard enforces elsewhere. Proves the access `sys` subject can never collide with a user type.
    [Test]
    public async Task A_type_named_sys_is_rejected()
    {
        var ex = await Assert.That(() => InstanceDescriptionLoader.Load("""
            types
                Db
                    things set of sys
                sys
                    label text
            """)).Throws<SchemaValidationException>();
        await Assert.That(ex!.Message).Contains("sys");
        await Assert.That(ex!.Message).Contains("reserved");
    }

    // ── locked (M13 sugar for `create edit delete where false`) ────────────────

    // `locked` round-trips: it parses to EXACTLY the rule `create edit delete where false` already
    // means (proving it is pure sugar, not a new AccessRule shape — no new field, no flag), and the
    // printer canonicalizes back to `locked` (not the older `where false` spelling), a fixpoint on
    // reprint. AssertRoundTrips already checks parse∘print is the identity AND print∘parse is a
    // fixpoint; this additionally asserts the SEMANTIC content (the exact verbs+condition a
    // hand-written `where false` line would produce) so a future refactor can't silently change what
    // `locked` denies without failing here.
    [Test]
    public async Task A_locked_type_round_trips_to_the_where_false_shape_and_prints_as_locked()
    {
        const string app = """
        types
            Db
                things set of Thing
            Thing
                title text

        access
            Thing
                locked
        """;
        await AssertRoundTrips(app);

        var desc = AppParse.Parse(app);
        var rule = desc.Rules!.Single(r => r.Type == "Thing");
        await Assert.That(rule.Verbs.ToHashSet().SetEquals(AppParse.WriteVerbs)).IsTrue();
        await Assert.That(rule.When).IsTypeOf<CodeBool>();
        await Assert.That(((CodeBool)rule.When!).Value).IsFalse();

        // The printed form spells it `locked`, not `create edit delete where false` — the canonical
        // upgrade this slice makes (AppPrint.PrintAccess collapses the recognized shape).
        var printed = AppPrint.Print(desc);
        await Assert.That(printed).Contains("locked");
        await Assert.That(printed).DoesNotContain("where false");
    }

    // A hand-written `create edit delete where false` line — the OLDER spelling `locked` replaces —
    // still parses and is byte-for-byte CANONICALIZED to `locked` on print: an app committed before
    // this slice keeps working, and re-printing it (e.g. the designer bridge publishing over it)
    // upgrades the spelling automatically. Proves `locked` is additive sugar, not a breaking parse
    // change.
    [Test]
    public async Task An_old_where_false_idiom_prints_as_locked()
    {
        var desc = AppParse.Parse("""
        types
            Db
                things set of Thing
            Thing
                title text

        access
            Thing
                create edit delete where false
        """);
        var printed = AppPrint.Print(desc);
        await Assert.That(printed).Contains("        locked\n");
        await Assert.That(printed).DoesNotContain("where false");
    }

    // `locked` must be the ONLY rule under its subject: pairing it with another grant on the SAME
    // type is a loader error (ambiguous — which one governs?), not silently merged.
    [Test]
    public async Task Locked_plus_another_grant_on_the_same_subject_is_rejected()
    {
        var ex = await Assert.That(() => InstanceDescriptionLoader.Load("""
            types
                Db
                    things set of Thing
                Thing
                    title text

            access
                Thing
                    locked
                    read
            """)).Throws<SchemaValidationException>();
        await Assert.That(ex!.Message).Contains("Thing");
        await Assert.That(ex!.Message).Contains("locked");
        await Assert.That(ex!.Message).Contains("ONLY");
    }

    // `locked` under the `sys` subject is meaningless (sys governs host-action authority, not a data
    // type's write floor) and is rejected at load with a clear message.
    [Test]
    public async Task Locked_under_the_sys_subject_is_rejected()
    {
        var ex = await Assert.That(() => InstanceDescriptionLoader.Load($$"""
            types
                Db
                    designs set of Design
                Design
                    label text

            access
                {{AccessFloor.SysSubject}}
                    locked
            """)).Throws<SchemaValidationException>();
        await Assert.That(ex!.Message).Contains(AccessFloor.SysSubject);
        await Assert.That(ex!.Message).Contains("locked");
    }

    // A subject may appear AT MOST ONCE in the access section. This is the cross-block guarantee the
    // per-block sole-rule check ALONE cannot give: `Thing / locked` in one block and `Thing / create
    // where true` in another would BOTH land in the flat desc.Rules, and AccessFloor.Can ORs across
    // every rule for a subject — so the grant would silently un-do the lock (CanWrite("create","Thing")
    // → true), the exact bypass the lock exists to prevent. Rejecting duplicate subject blocks closes
    // it AND makes the sole-rule check complete by construction (one subject = one block). This is the
    // review-BLOCK probe verbatim: without the check it loads clean and the floor allows the write.
    [Test]
    public async Task A_locked_subject_repeated_in_a_second_block_with_a_grant_is_rejected()
    {
        var ex = await Assert.That(() => InstanceDescriptionLoader.Load("""
            types
                Db
                    things set of Thing
                Thing
                    title text

            access
                Thing
                    locked
                Thing
                    create where true
            """)).Throws<SchemaValidationException>();
        await Assert.That(ex!.Message).Contains("Thing");
    }

    // The same rule for the PLAIN case (two ordinary grant blocks for one subject) — duplicate subject
    // blocks were always ambiguous noise, now they are a bypass vector, so both directions are rejected
    // by the one general rule (not a locked-only special case).
    [Test]
    public async Task A_subject_repeated_across_two_grant_blocks_is_rejected()
    {
        var ex = await Assert.That(() => InstanceDescriptionLoader.Load("""
            types
                Db
                    things set of Thing
                Thing
                    title text

            access
                Thing
                    read
                Thing
                    create where currentUser != null
            """)).Throws<SchemaValidationException>();
        await Assert.That(ex!.Message).Contains("Thing");
    }

    // The check fires at the PARSE layer (AccessSection mapping), not only in the loader — so it holds
    // for every entry point (AppParse.Parse, used by the round-trip/printer paths, as well as
    // InstanceDescriptionLoader.Load). Pinning the layer keeps the guarantee from silently narrowing to
    // "only when fully loaded".
    [Test]
    public async Task Duplicate_access_subjects_are_rejected_at_the_parse_layer()
    {
        await Assert.That(() => AppParse.Parse("""
            types
                Db
                    things set of Thing
                Thing
                    title text

            access
                Thing
                    read
                Thing
                    create
            """)).Throws<SchemaValidationException>();
    }

    // ── expression printing: minimal parentheses ────────────────────────────────

    [Test]
    public async Task Parentheses_print_only_where_precedence_requires_them()
    {
        await AssertPrints("2 * (3 + 4)", "2 * (3 + 4)");
        await AssertPrints("2 * 3 + 4", "2 * 3 + 4");
        await AssertPrints("(2 + 3) * 4", "(2 + 3) * 4");
        await AssertPrints("a - b - c", "a - b - c");          // left-assoc: no parens
        await AssertPrints("a - (b - c)", "a - (b - c)");      // right nesting kept
        await AssertPrints("db.tasks.where(x => x.done == false)",
                           "db.tasks.where(x => x.done == false)");
        // The parenthesized single-param form normalizes to the bare form.
        await AssertPrints("db.tasks.where((x) => x.done == false)",
                           "db.tasks.where(x => x.done == false)");
        await AssertPrints("((n) => n + 1)(41)", "(n => n + 1)(41)");
        // Unary `!`: tighter than the binary ops (no parens on a member/comparison operand),
        // but it parenthesizes a lower-precedence operand; `!!x` nests bare.
        await AssertPrints("!a", "!a");
        await AssertPrints("!a.b", "!a.b");
        await AssertPrints("!a == b", "!a == b");
        await AssertPrints("!(a == b)", "!(a == b)");
        await AssertPrints("!!a", "!!a");
    }

    [Test]
    public async Task Ternary_prints_and_parenthesizes_a_nested_condition()
    {
        await AssertPrints("a == b ? \"yes\" : \"no\"", "a == b ? \"yes\" : \"no\"");
        // Right-associative: the trailing ternary lives in the else, no parens needed.
        await AssertPrints("a ? b : c ? d : e", "a ? b : c ? d : e");
        // A ternary in CONDITION position must be parenthesized (it binds looser than ternary itself).
        await AssertPrints("(a ? b : c) ? d : e", "(a ? b : c) ? d : e");
        // A ternary as an operand of a string concat (the KebabMenu-style class={cond ? x : y}).
        await AssertPrints("cond ? \"open\" : \"closed\"", "cond ? \"open\" : \"closed\"");
    }

    [Test]
    public async Task Block_lambda_prints_as_a_braced_statement_list()
    {
        await AssertPrints("() => { f(); g() }", "() => { f(); g() }");
        await AssertPrints("() => { x = 1; y = 2 }", "() => { x = 1; y = 2 }");
        // A single-return lambda still prints as the inline sugar (not a block).
        await AssertPrints("x => x.done", "x => x.done");
    }

    private static async Task AssertPrints(string source, string expected)
    {
        var printed = CodePrint.Value(CodeParse.ParseExpression(source));
        await Assert.That(printed).IsEqualTo(expected);
        // The printed form parses back to the same tree.
        var a = JsonSerializer.SerializeToNode<ICodeValue>(CodeParse.ParseExpression(source), SchemaJson.Options)!;
        var b = JsonSerializer.SerializeToNode<ICodeValue>(CodeParse.ParseExpression(printed), SchemaJson.Options)!;
        await Assert.That(JsonNode.DeepEquals(a, b)).IsTrue();
    }
}
