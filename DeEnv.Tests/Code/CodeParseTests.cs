using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Code;
using DeEnv.Code.Parsing;
using DeEnv.Instance;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Stage 1 of the text-syntax milestone: the expression grammar. Each case parses a
// text expression and compares the produced AST — serialized with the shared wire
// options — against the JSON the hand-written form would declare, so text and JSON
// provably mean the same tree.
public sealed class CodeParseTests
{
    private static async Task AssertParsesTo(string text, string expectedAstJson)
    {
        var ast = CodeParse.ParseExpression(text);
        var actual = JsonSerializer.SerializeToNode<ICodeValue>(ast, SchemaJson.Options)!;
        var expected = JsonNode.Parse(expectedAstJson)!;
        // Tree equality (property order doesn't matter); on mismatch fall through to
        // the string comparison purely for a readable failure message.
        if (!JsonNode.DeepEquals(actual, expected))
            await Assert.That(actual.ToJsonString()).IsEqualTo(expected.ToJsonString());
    }

    // ── literals ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Int_literals_including_zero_and_negative()
    {
        await AssertParsesTo("0", """{ "type": "int", "value": 0 }""");
        await AssertParsesTo("-7", """{ "type": "int", "value": -7 }""");
        await AssertParsesTo("42", """{ "type": "int", "value": 42 }""");
    }

    [Test]
    public async Task Text_literal_with_escapes()
    {
        await AssertParsesTo("\"a\\\"b\\\\c\\nd\"", """{ "type": "text", "value": "a\"b\\c\nd" }""");
    }

    [Test]
    public async Task Bool_null_and_symbol()
    {
        await AssertParsesTo("true", """{ "type": "bool", "value": true }""");
        await AssertParsesTo("null", """{ "type": "null" }""");
        await AssertParsesTo("selectedUser", """{ "type": "symbol", "name": "selectedUser" }""");
    }

    [Test]
    public async Task Array_and_object_literals()
    {
        await AssertParsesTo("[]", """{ "type": "array", "items": [] }""");
        await AssertParsesTo("[1, 2]",
            """
            { "type": "array", "items": [ { "type": "int", "value": 1 }, { "type": "int", "value": 2 } ] }
            """);
        await AssertParsesTo("{ name: \"\", checked: false }",
            """
            { "type": "object", "props": [
              { "name": "name", "value": { "type": "text", "value": "" } },
              { "name": "checked", "value": { "type": "bool", "value": false } } ] }
            """);
    }

    // ── precedence ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Multiplication_binds_tighter_than_addition()
    {
        await AssertParsesTo("2 * 3 + 4",
            """
            { "type": "infixOp", "op": "add",
              "left": { "type": "infixOp", "op": "multiply",
                "left": { "type": "int", "value": 2 }, "right": { "type": "int", "value": 3 } },
              "right": { "type": "int", "value": 4 } }
            """);
    }

    [Test]
    public async Task Parentheses_override_precedence()
    {
        await AssertParsesTo("2 * (3 + 4)",
            """
            { "type": "infixOp", "op": "multiply",
              "left": { "type": "int", "value": 2 },
              "right": { "type": "infixOp", "op": "add",
                "left": { "type": "int", "value": 3 }, "right": { "type": "int", "value": 4 } } }
            """);
    }

    [Test]
    public async Task Comparison_and_logic_nest_correctly()
    {
        // a == b && c — equality binds tighter than &&.
        await AssertParsesTo("a == b && c",
            """
            { "type": "infixOp", "op": "and",
              "left": { "type": "infixOp", "op": "equals",
                "left": { "type": "symbol", "name": "a" }, "right": { "type": "symbol", "name": "b" } },
              "right": { "type": "symbol", "name": "c" } }
            """);
    }

    // ── postfix chains ──────────────────────────────────────────────────────────

    [Test]
    public async Task Member_access_chains_left()
    {
        await AssertParsesTo("db.users",
            """
            { "type": "infixOp", "op": "objectProp",
              "left": { "type": "symbol", "name": "db" },
              "right": { "type": "symbol", "name": "users" } }
            """);
    }

    [Test]
    public async Task Calls_and_members_chain_in_postfix_order()
    {
        // db.tasks.where(p).orderBy(k) — call result flows into the next member access.
        await AssertParsesTo("db.tasks.where(p).orderBy(k)",
            """
            { "type": "call",
              "fn": { "type": "infixOp", "op": "objectProp",
                "left": { "type": "call",
                  "fn": { "type": "infixOp", "op": "objectProp",
                    "left": { "type": "infixOp", "op": "objectProp",
                      "left": { "type": "symbol", "name": "db" },
                      "right": { "type": "symbol", "name": "tasks" } },
                    "right": { "type": "symbol", "name": "where" } },
                  "params": [ { "type": "symbol", "name": "p" } ] },
                "right": { "type": "symbol", "name": "orderBy" } },
              "params": [ { "type": "symbol", "name": "k" } ] }
            """);
    }

    // ── lambdas & assignment ────────────────────────────────────────────────────

    [Test]
    public async Task Inline_lambda_sugars_to_a_returning_body()
    {
        // fn.Body is concretely typed (CodeBlock), so it carries no "type" discriminator.
        await AssertParsesTo("(x) => x.done == false",
            """
            { "type": "fn", "name": null, "params": [ { "name": "x" } ], "id": 0, "serverOnly": false,
              "body": { "statements": [ { "type": "return", "value":
                { "type": "infixOp", "op": "equals",
                  "left": { "type": "infixOp", "op": "objectProp",
                    "left": { "type": "symbol", "name": "x" }, "right": { "type": "symbol", "name": "done" } },
                  "right": { "type": "bool", "value": false } } } ] } }
            """);
    }

    [Test]
    public async Task Assignment_is_a_value()
    {
        // assign.Target is concretely typed (CodeSymbol) — no "type" discriminator.
        await AssertParsesTo("path = \"/\"",
            """
            { "type": "assign", "target": { "name": "path" },
              "value": { "type": "text", "value": "/" } }
            """);
    }

    [Test]
    public async Task An_iife_parses()
    {
        await AssertParsesTo("((n) => n + 1)(41)",
            """
            { "type": "call",
              "fn": { "type": "fn", "name": null, "params": [ { "name": "n" } ], "id": 0, "serverOnly": false,
                "body": { "statements": [ { "type": "return", "value":
                  { "type": "infixOp", "op": "add",
                    "left": { "type": "symbol", "name": "n" }, "right": { "type": "int", "value": 1 } } } ] } },
              "params": [ { "type": "int", "value": 41 } ] }
            """);
    }

    // ── errors ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_broken_expression_reports_its_position()
    {
        var ex = await Assert.That(() => CodeParse.ParseExpression("a + ")).Throws<CodeParseException>();
        await Assert.That(ex!.Message).Contains("line 1");
    }

    [Test]
    public async Task A_keyword_is_not_a_symbol()
    {
        await Assert.That(() => CodeParse.ParseExpression("return")).Throws<CodeParseException>();
    }
}
