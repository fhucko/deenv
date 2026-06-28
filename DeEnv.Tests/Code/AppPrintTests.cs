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
