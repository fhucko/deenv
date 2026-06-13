using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Code;
using DeEnv.Code.Parsing;
using DeEnv.Instance;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Stage 2 of the text-syntax milestone: statements, indentation blocks, tags, and the
// common/ui document grammar. As in CodeParseTests, parsed ASTs are compared against
// the JSON the hand-written form would declare.
public sealed class CodeParseDocumentTests
{
    private static async Task AssertJson<T>(T actualNode, string expectedJson)
    {
        var actual = JsonSerializer.SerializeToNode(actualNode, SchemaJson.Options)!;
        var expected = JsonNode.Parse(expectedJson)!;
        if (!JsonNode.DeepEquals(actual, expected))
            await Assert.That(actual.ToJsonString()).IsEqualTo(expected.ToJsonString());
    }

    // ── statements & blocks ─────────────────────────────────────────────────────

    [Test]
    public async Task A_function_with_var_if_else_chain_and_return()
    {
        var fn = Run(CodeParse.NamedFunction(""),
            "fn classify(n)\n" +
            "    var label = \"\"\n" +
            "    if n > 10\n" +
            "        label = \"big\"\n" +
            "    else if n > 5\n" +
            "        label = \"mid\"\n" +
            "    else\n" +
            "        label = \"small\"\n" +
            "    return label\n");

        await AssertJson(fn,
            """
            { "name": "classify", "params": [ { "name": "n" } ], "id": 0, "serverOnly": false,
              "body": { "statements": [
                { "type": "varDec", "name": "label", "value": { "type": "text", "value": "" } },
                { "type": "if",
                  "condition": { "type": "infixOp", "op": "moreThan",
                    "left": { "type": "symbol", "name": "n" }, "right": { "type": "int", "value": 10 } },
                  "body": { "type": "block", "statements": [
                    { "type": "assign", "target": { "type": "symbol", "name": "label" }, "value": { "type": "text", "value": "big" } } ] },
                  "elseBody": { "type": "if",
                    "condition": { "type": "infixOp", "op": "moreThan",
                      "left": { "type": "symbol", "name": "n" }, "right": { "type": "int", "value": 5 } },
                    "body": { "type": "block", "statements": [
                      { "type": "assign", "target": { "type": "symbol", "name": "label" }, "value": { "type": "text", "value": "mid" } } ] },
                    "elseBody": { "type": "block", "statements": [
                      { "type": "assign", "target": { "type": "symbol", "name": "label" }, "value": { "type": "text", "value": "small" } } ] } } } ,
                { "type": "return", "value": { "type": "symbol", "name": "label" } }
              ] } }
            """);
    }

    [Test]
    public async Task A_call_statement_and_a_multiline_lambda()
    {
        var fn = Run(CodeParse.NamedFunction(""),
            "fn addUser()\n" +
            "    db.users.add(newUser)\n" +
            "    newUser = getNewUser()\n");

        await AssertJson(fn,
            """
            { "name": "addUser", "params": [], "id": 0, "serverOnly": false,
              "body": { "statements": [
                { "type": "call",
                  "fn": { "type": "infixOp", "op": "objectProp",
                    "left": { "type": "infixOp", "op": "objectProp",
                      "left": { "type": "symbol", "name": "db" }, "right": { "type": "symbol", "name": "users" } },
                    "right": { "type": "symbol", "name": "add" } },
                  "params": [ { "type": "symbol", "name": "newUser" } ] },
                { "type": "assign", "target": { "type": "symbol", "name": "newUser" },
                  "value": { "type": "call", "fn": { "type": "symbol", "name": "getNewUser" }, "params": [] } }
              ] } }
            """);
    }

    // ── tags ────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_tag_tree_with_attributes_foreach_and_if()
    {
        var fn = Run(CodeParse.NamedFunction(""),
            "fn render()\n" +
            "    return <main>\n" +
            "        <h1>\n" +
            "            \"Tasks\"\n" +
            "        foreach t in db.tasks\n" +
            "            <div class=\"row\">\n" +
            "                t.title\n" +
            "                if t.done\n" +
            "                    <span class=\"done\">\n" +
            "                        \"done\"\n" +
            "                else\n" +
            "                    <span class=\"open\">\n" +
            "                        \"open\"\n");

        await AssertJson(fn,
            """
            { "name": "render", "params": [], "id": 0, "serverOnly": false,
              "body": { "statements": [ { "type": "return", "value":
                { "type": "tag", "name": "main", "attributes": [], "children": [
                  { "type": "tag", "name": "h1", "attributes": [],
                    "children": [ { "type": "text", "value": "Tasks" } ] },
                  { "type": "foreach", "item": { "name": "t" },
                    "collection": { "type": "infixOp", "op": "objectProp",
                      "left": { "type": "symbol", "name": "db" }, "right": { "type": "symbol", "name": "tasks" } },
                    "body": [
                      { "type": "tag", "name": "div",
                        "attributes": [ { "name": "class", "value": { "type": "text", "value": "row" } } ],
                        "children": [
                          { "type": "infixOp", "op": "objectProp",
                            "left": { "type": "symbol", "name": "t" }, "right": { "type": "symbol", "name": "title" } },
                          { "type": "if",
                            "condition": { "type": "infixOp", "op": "objectProp",
                              "left": { "type": "symbol", "name": "t" }, "right": { "type": "symbol", "name": "done" } },
                            "body": [ { "type": "tag", "name": "span",
                              "attributes": [ { "name": "class", "value": { "type": "text", "value": "done" } } ],
                              "children": [ { "type": "text", "value": "done" } ] } ],
                            "elseBody": [ { "type": "tag", "name": "span",
                              "attributes": [ { "name": "class", "value": { "type": "text", "value": "open" } } ],
                              "children": [ { "type": "text", "value": "open" } ] } ] }
                        ] }
                    ] }
                ] } } ] } }
            """);
    }

    [Test]
    public async Task A_bound_attribute_and_an_inline_handler()
    {
        var fn = Run(CodeParse.NamedFunction(""),
            "fn render()\n" +
            "    return <main>\n" +
            "        <input class=\"new-name\" value={newName}>\n" +
            "        <button onClick={() => selectUser(user)}>\n" +
            "            \"Select\"\n");

        await AssertJson(fn,
            """
            { "name": "render", "params": [], "id": 0, "serverOnly": false,
              "body": { "statements": [ { "type": "return", "value":
                { "type": "tag", "name": "main", "attributes": [], "children": [
                  { "type": "tag", "name": "input", "attributes": [
                      { "name": "class", "value": { "type": "text", "value": "new-name" } },
                      { "name": "value", "value": { "type": "symbol", "name": "newName" } }
                    ], "children": [] },
                  { "type": "tag", "name": "button", "attributes": [
                      { "name": "onClick", "value": { "type": "fn", "name": null, "params": [], "id": 0, "serverOnly": false,
                        "body": { "statements": [ { "type": "return", "value":
                          { "type": "call", "fn": { "type": "symbol", "name": "selectUser" },
                            "params": [ { "type": "symbol", "name": "user" } ] } } ] } } }
                    ], "children": [ { "type": "text", "value": "Select" } ] }
                ] } } ] } }
            """);
    }

    // ── the document ────────────────────────────────────────────────────────────

    [Test]
    public async Task A_document_maps_common_and_ui_onto_the_schema_sections()
    {
        var (common, ui) = CodeParse.ParseDocument(
            "common\n" +
            "    server fn hash(p)\n" +
            "        return p\n" +
            "    fn double(n)\n" +
            "        return n * 2\n" +
            "\n" +
            "ui\n" +
            "    var path = \"/\"\n" +
            "    var selected\n" +
            "    fn pick(u)\n" +
            "        selected = u\n" +
            "    fn render()\n" +
            "        return <main>\n" +
            "            \"hello\"\n");

        await Assert.That(common!.Functions!.Count).IsEqualTo(2);
        await Assert.That(common.Functions[0].Name).IsEqualTo("hash");
        await Assert.That(common.Functions[0].ServerOnly).IsTrue();
        await Assert.That(common.Functions[1].Name).IsEqualTo("double");
        await Assert.That(common.Functions[1].ServerOnly).IsFalse();

        await Assert.That(ui.Vars!.Count).IsEqualTo(2);
        await Assert.That(ui.Vars[0].Name).IsEqualTo("path");
        await Assert.That(ui.Vars[1].Name).IsEqualTo("selected");
        await Assert.That(ui.Vars[1].Value).IsNull();
        await Assert.That(ui.Functions!.Count).IsEqualTo(1);
        await Assert.That(ui.Functions[0].Name).IsEqualTo("pick");
        await Assert.That(ui.Render).IsNotNull();
        await Assert.That(ui.Render!.Name).IsEqualTo("render");
    }

    [Test]
    public async Task A_ui_section_without_render_loads_and_defaults_to_the_generic_ui()
    {
        // No `fn render()` is fine: the self-hosted generic UI is the default. A ui section
        // may still carry vars/helpers the generic library does not use.
        var desc = InstanceDescriptionLoader.Load(
            "types\n" +
            "    Db\n" +
            "        note: text\n" +
            "\n" +
            "ui\n" +
            "    var path = \"/\"\n");
        await Assert.That(desc.Ui!.Render).IsNull();
    }

    [Test]
    public async Task A_document_parse_error_reports_its_line()
    {
        var ex = await Assert.That(() => CodeParse.ParseDocument(
            "ui\n" +
            "    var path = \"/\"\n" +
            "    fn render()\n" +
            "        return <main>\n" +
            "            oops =\n"))
            .Throws<CodeParseException>();
        await Assert.That(ex!.Message).Contains("line 5");
    }

    private static T Run<T>(Parser<T> parser, string text) => Parse.Run(parser, text);
}
