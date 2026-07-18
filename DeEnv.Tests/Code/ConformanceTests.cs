using System.Text.Json;
using DeEnv.Code;
using DeEnv.Instance;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Runs the shared conformance suite (DeEnv/Code/conformance.json) through the C#
// interpreter. The same file is executed by the TypeScript interpreter in Stage 3,
// so any drift between the two evaluation cores fails here or there.
//
// This also proves the polymorphic JSON round-trip: each case's `expr` is the exact
// AST shape a hand-written instance.schema.json carries.
public sealed class ConformanceTests
{
    private static readonly JsonSerializerOptions JsonOpts = SchemaJson.Options;

    private sealed record Suite(Case[] Cases);
    // A case is either a single `Expr` (evaluated once) or the lifecycle protocol — optional
    // `Setup` statements run once into a retained scope, then `Renders` value-exprs evaluated in
    // order against that same scope+context, with `Expect` checked on the LAST render. `Seed`
    // (client data layer, slice 1a) is an optional { slotKey → { varName → value-expr } } map
    // installed as context.Seed before the renders — its value-exprs evaluate once (like Setup),
    // exercising the component-state seeding path the server uses.
    public sealed record Case(string Name, Expectation Expect, JsonElement? Expr = null,
        string? Text = null, JsonElement? Setup = null, JsonElement? Renders = null,
        JsonElement? Seed = null);
    public sealed record Expectation(string Kind, JsonElement Value);

    public static IEnumerable<Func<Case>> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "conformance.json");
        var suite = JsonSerializer.Deserialize<Suite>(File.ReadAllText(path), JsonOpts)!;
        foreach (var c in suite.Cases) yield return () => c;
    }

    public static IEnumerable<Func<Case>> TextCases()
    {
        foreach (var c in Cases())
            if (c().Text != null) yield return c;
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Conformance_case_evaluates(Case c)
    {
        // "error" (M12 FG) is the one non-value expectation: the case must THROW rather than
        // produce a scalar/tag result (a runaway-recursive fn, pinning the call-depth guard).
        // Handled separately from AssertExpectation, which only ever sees a successful result.
        if (c.Expect.Kind == "error")
        {
            Exception? caught = null;
            try { EvaluateCase(c); }
            catch (Exception ex) { caught = ex; }
            await Assert.That(caught).IsNotNull();
            await Assert.That(caught!.Message).IsEqualTo(c.Expect.Value.GetString());
            return;
        }
        var result = EvaluateCase(c);
        await AssertExpectation(c, result);
    }

    // A single-expr case evaluates `Expr` once; a lifecycle case runs `Setup` statements once
    // and then each `Renders` expr against ONE retained scope+context (so the memo cache and the
    // component slot identities persist across the render sequence), returning the last result.
    private static IExecValue EvaluateCase(Case c)
    {
        var executor = new CodeExecutor();
        var scope = new ExecScope();
        var context = new ExecContext();
        // The live root data context as ambient `ctx` (the framework provides this in the real app),
        // so cases can open staging sub-contexts via `ctx.new()`.
        context.Ambient = new AmbientFrame("ctx", new ExecCtx { Live = true }, null);
        // A persisted (positive-id) object the overlay cases stage onto — staging is gated to
        // persisted objects (a transient draft writes live), so the test object needs a real id.
        scope.Items["o"] = new ExecScopeItem
        {
            Value = new ExecObject { Id = 100, Props = new() { ["f"] = new ExecInt { Value = 1 } } },
            IsReadOnly = false,
        };
        if (c.Renders is not { } renders)
            return executor.ExecuteValue(c.Expr!.Value.Deserialize<ICodeValue>(JsonOpts)!, scope, context);

        if (c.Setup is { } setup)
            foreach (var stmt in setup.EnumerateArray())
                executor.ExecuteStatement(stmt.Deserialize<ICodeStatement>(JsonOpts)!, scope, context);
        // Install the component-state seed (slice 1a): evaluate each slot's var value-exprs once
        // against the retained scope/context (like Setup) and thread them into context.Seed, so the
        // renders below reproduce a seeded component exactly as the server's Render does.
        if (c.Seed is { } seedJson)
        {
            var seed = new Dictionary<string, IReadOnlyDictionary<string, IExecValue>>();
            foreach (var slot in seedJson.EnumerateObject())
            {
                var vars = new Dictionary<string, IExecValue>();
                foreach (var v in slot.Value.EnumerateObject())
                    vars[v.Name] = executor.ExecuteValue(v.Value.Deserialize<ICodeValue>(JsonOpts)!, scope, context);
                seed[slot.Name] = vars;
            }
            context.Seed = seed;
        }
        IExecValue result = new ExecNothing();
        foreach (var render in renders.EnumerateArray())
            result = executor.ExecuteValue(render.Deserialize<ICodeValue>(JsonOpts)!, scope, context);
        return result;
    }

    // The same cases through the TEXT form (Stage 1 of the text-syntax milestone):
    // parsing the case's text must evaluate to exactly what the AST form does.
    [Test]
    [MethodDataSource(nameof(TextCases))]
    public async Task Conformance_text_form_parses_and_evaluates(Case c)
    {
        var expr = CodeParse.ParseExpression(c.Text!);
        var result = new CodeExecutor().ExecuteValue(expr, new ExecScope(), new ExecContext());
        await AssertExpectation(c, result);
    }

    private static async Task AssertExpectation(Case c, IExecValue result)
    {
        switch (c.Expect.Kind)
        {
            case "int":
                await Assert.That(((ExecInt)result).Value).IsEqualTo(c.Expect.Value.GetInt32());
                break;
            case "text":
                await Assert.That(((ExecText)result).Value).IsEqualTo(c.Expect.Value.GetString());
                break;
            case "bool":
                await Assert.That(((ExecBool)result).Value).IsEqualTo(c.Expect.Value.GetBoolean());
                break;
            case "intList":
            {
                var got = ((IExecCollection)result).Items.Select(i => ((ExecInt)i.Value).Value);
                var want = c.Expect.Value.EnumerateArray().Select(e => e.GetInt32());
                await Assert.That(string.Join(",", got)).IsEqualTo(string.Join(",", want));
                break;
            }
            case "nothing":
                await Assert.That(result is ExecNothing).IsTrue();
                break;
            case "null":
                await Assert.That(result is ExecNull).IsTrue();
                break;
            case "tag":
                // A tag-tree result (sys.renderTree) has no scalar form; compare a canonical string
                // serialization, twin-identical with codeExec.ts's serializeTree in runConformance.
                await Assert.That(SerializeTree(result)).IsEqualTo(c.Expect.Value.GetString());
                break;
            default:
                throw new InvalidOperationException($"Unknown expect kind '{c.Expect.Kind}' in case '{c.Name}'.");
        }
    }

    // Canonical string form of a rendered tag tree: `<name attr="v"…>children…</name>` with attributes
    // sorted ordinally (DOM order is a browser concern), text children inline. Twin of codeExec.ts's
    // serializeTree — the two must produce the same string for the SAME tree, so the tag conformance case
    // proves both interpreters build an identical canvas.
    private static string SerializeTree(IExecTagChild node) => node switch
    {
        ExecTag t => "<" + t.Name
            + string.Concat(t.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => " " + k + "=\"" + ScalarText(t.Attributes[k]) + "\""))
            + ">" + string.Concat(t.Children.Select(SerializeTree)) + "</" + t.Name + ">",
        // An ARRAY child splices FLAT (recursively) — the same flattening production does (SsrRenderer
        // .SerializeChild / ui.ts flatten), so a for/if row's evaluated instances (S6b returns an IExecCollection)
        // serialize as if spliced into the parent, with no wrapper. Twin of codeExec.ts serializeTree.
        IExecCollection a => string.Concat(a.Items.Select(i => SerializeTree(i.Value))),
        ExecText x => x.Value,
        ExecInt i => i.Value.ToString(),
        ExecBool b => b.Value ? "true" : "false",
        _ => "",
    };

    private static string ScalarText(IExecValue v) => v switch
    {
        ExecText t => t.Value,
        ExecInt i => i.Value.ToString(),
        ExecBool b => b.Value ? "true" : "false",
        _ => "",
    };
}
