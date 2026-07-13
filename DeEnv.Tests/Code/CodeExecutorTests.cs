using System.Text.Json.Nodes;
using DeEnv.Code;
using DeEnv.Instance;
using DeEnv.Storage;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Stage 1 execution tests for the Code interpreter: expressions, statements,
// objects/arrays, where/orderBy lambdas, functions, and a transient-add that
// persists through the M5 store.
public sealed class CodeExecutorTests
{
    // ── tiny AST builders ─────────────────────────────────────────────────────────

    private static CodeInt Int(int v) => new() { Value = v };
    private static CodeText Text(string v) => new() { Value = v };
    private static CodeBool Bool(bool v) => new() { Value = v };
    private static CodeSymbol Sym(string n) => new() { Name = n };
    private static CodeInfixOp Op(CodeInfixOpType t, ICodeValue l, ICodeValue r) =>
        new() { Op = t, Left = l, Right = r };
    private static CodeInfixOp Prop(ICodeValue target, string name) =>
        new() { Op = CodeInfixOpType.ObjectProp, Left = target, Right = Sym(name) };

    private static IExecValue Eval(ICodeValue v, ExecScope? scope = null) =>
        new CodeExecutor().ExecuteValue(v, scope ?? new ExecScope(), new ExecContext());

    // Runs statements directly in `scope` (so declarations are visible to later
    // ExecuteValue calls), returning the first non-null result.
    private static IExecValue? Run(ExecScope scope, CodeExecutor exec, ExecContext ctx, params ICodeStatement[] body)
    {
        foreach (var s in body)
        {
            var r = exec.ExecuteStatement(s, scope, ctx);
            if (r != null) return r;
        }
        return null;
    }

    // ── expressions ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Int_literal_evaluates()
    {
        var r = Eval(Int(5));
        await Assert.That(((ExecInt)r).Value).IsEqualTo(5);
    }

    [Test]
    public async Task Arithmetic_evaluates()
    {
        var r = Eval(Op(CodeInfixOpType.Add, Op(CodeInfixOpType.Multiply, Int(2), Int(3)), Int(4)));
        await Assert.That(((ExecInt)r).Value).IsEqualTo(10);
    }

    [Test]
    public async Task Comparison_and_logic_evaluate()
    {
        var gt = Eval(Op(CodeInfixOpType.MoreThan, Int(3), Int(1)));
        await Assert.That(((ExecBool)gt).Value).IsTrue();

        var and = Eval(Op(CodeInfixOpType.And,
            Op(CodeInfixOpType.Equals, Int(1), Int(1)),
            Op(CodeInfixOpType.LessThan, Int(2), Int(1))));
        await Assert.That(((ExecBool)and).Value).IsFalse();
    }

    [Test]
    public async Task Object_literal_and_prop_access()
    {
        var obj = new CodeObject { Props = [new CodeObjectProp { Name = "n", Value = Int(7) }] };
        var scope = new ExecScope();
        var exec = new CodeExecutor();
        var ctx = new ExecContext();
        Run(scope, exec, ctx, new CodeVarDec { Name = "o", Value = obj });
        var r = exec.ExecuteValue(Prop(Sym("o"), "n"), scope, ctx);
        await Assert.That(((ExecInt)r).Value).IsEqualTo(7);
    }

    // ── statements ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Block_with_vardec_and_return()
    {
        var scope = new ExecScope();
        var exec = new CodeExecutor();
        var r = Run(scope, exec, new ExecContext(),
            new CodeVarDec { Name = "x", Value = Int(4) },
            new CodeReturn { Value = Op(CodeInfixOpType.Add, Sym("x"), Int(1)) });
        await Assert.That(((ExecInt)r!).Value).IsEqualTo(5);
    }

    [Test]
    public async Task If_statement_selects_branch()
    {
        var scope = new ExecScope();
        var exec = new CodeExecutor();
        var r = Run(scope, exec, new ExecContext(),
            new CodeIf
            {
                Condition = Op(CodeInfixOpType.MoreThan, Int(2), Int(1)),
                Body = new CodeReturn { Value = Text("yes") },
                ElseBody = new CodeReturn { Value = Text("no") },
            });
        await Assert.That(((ExecText)r!).Value).IsEqualTo("yes");
    }

    [Test]
    public async Task Function_define_and_call()
    {
        var scope = new ExecScope();
        var exec = new CodeExecutor();
        var ctx = new ExecContext();
        // fn add1(n) { return n + 1 }
        Run(scope, exec, ctx, new CodeFunction
        {
            Name = "add1",
            Params = [new CodeFunctionParam { Name = "n" }],
            Body = new CodeBlock { Statements = [new CodeReturn { Value = Op(CodeInfixOpType.Add, Sym("n"), Int(1)) }] },
        });
        var r = exec.ExecuteValue(new CodeCall { Fn = Sym("add1"), Params = [Int(41)] }, scope, ctx);
        await Assert.That(((ExecInt)r).Value).IsEqualTo(42);
    }

    // ── call-depth guard (M12 FG, grill F3c) ─────────────────────────────────────────

    // fn Rec() { return Rec() } — a named fn self-binds into its own defining scope at creation
    // (ExecuteFunction), so this is genuine unbounded interpreter-level recursion with no base case.
    // Pre-guard this blows the REAL C# call stack — an uncatchable StackOverflowException (process
    // death) — which is exactly why this test asserts the NEW behavior rather than exercising the old
    // one (that would kill the test host). The guard makes it a normal, catchable CodeRuntimeException
    // at the shared twin-pinned threshold instead.
    [Test]
    public async Task Unbounded_self_recursion_throws_a_call_depth_error_not_a_stack_overflow()
    {
        var scope = new ExecScope();
        var rec = new CodeFunction
        {
            Name = "Rec",
            Params = [],
            Body = new CodeBlock { Statements = [new CodeReturn { Value = new CodeCall { Fn = Sym("Rec"), Params = [] } }] },
        };
        var call = new CodeCall { Fn = rec, Params = [] };

        CodeRuntimeException? caught = null;
        try { new CodeExecutor().ExecuteValue(call, scope, new ExecContext()); }
        catch (CodeRuntimeException ex) { caught = ex; }
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).IsEqualTo("Call depth exceeded 256 — runaway recursion?");
    }

    // The GUARD-TRIP leak proof (arch review): the test above only proves a BODY-thrown error unwinds
    // cleanly (RunBody's own try/finally around ExecuteBlock always ran) — false assurance, since the
    // depth-limit check ITSELF used to throw BEFORE that try, so the throwing frame's own increment was
    // never undone. Latent in C# (a fresh ExecContext per request), but real the moment a context is
    // REUSED (a long-lived session, or the designer canvas re-evaluating the same context repeatedly):
    // each caught trip would erode the ceiling by 1 until a legitimate shallow call eventually throws
    // spuriously. Trips TWICE on the SAME context/scope (`Rec` self-binds into `scope` once, reused for
    // both calls): pre-fix the second trip would occur one call SHALLOWER than the first; post-fix both
    // trips are identical and CallDepth returns to 0 after each.
    [Test]
    public async Task Tripping_the_guard_does_not_leak_call_depth()
    {
        var scope = new ExecScope();
        var exec = new CodeExecutor();
        var ctx = new ExecContext();
        var rec = new CodeFunction
        {
            Name = "Rec",
            Params = [],
            Body = new CodeBlock { Statements = [new CodeReturn { Value = new CodeCall { Fn = Sym("Rec"), Params = [] } }] },
        };

        CodeRuntimeException? first = null;
        try { exec.ExecuteValue(new CodeCall { Fn = rec, Params = [] }, scope, ctx); }
        catch (CodeRuntimeException ex) { first = ex; }
        await Assert.That(first).IsNotNull();
        await Assert.That(ctx.CallDepth).IsEqualTo(0); // the throwing frame's own increment unwound too

        // A second trip on the SAME context via the now-bound `Rec` symbol — same message/threshold as
        // the first, proving nothing eroded between trips.
        CodeRuntimeException? second = null;
        try { exec.ExecuteValue(new CodeCall { Fn = Sym("Rec"), Params = [] }, scope, ctx); }
        catch (CodeRuntimeException ex) { second = ex; }
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.Message).IsEqualTo(first!.Message);
        await Assert.That(ctx.CallDepth).IsEqualTo(0);
    }

    // The no-false-positive proof: fn Sum(n) recurses DATA-BOUNDED to depth 100 — representative of
    // real recursion depth (e.g. the designer's own renderNodeEditor walking a design tree) — well
    // under the 256 guard threshold, and must still return the correct value untouched by the guard.
    [Test]
    public async Task Data_bounded_recursion_well_under_the_limit_returns_correctly()
    {
        var scope = new ExecScope();
        var exec = new CodeExecutor();
        var ctx = new ExecContext();
        Run(scope, exec, ctx, new CodeFunction
        {
            Name = "Sum",
            Params = [new CodeFunctionParam { Name = "n" }],
            Body = new CodeBlock
            {
                Statements =
                [
                    new CodeIf
                    {
                        Condition = Op(CodeInfixOpType.LessThanOrEqual, Sym("n"), Int(0)),
                        Body = new CodeReturn { Value = Int(0) },
                    },
                    new CodeReturn
                    {
                        Value = Op(CodeInfixOpType.Add, Sym("n"),
                            new CodeCall { Fn = Sym("Sum"), Params = [Op(CodeInfixOpType.Subtract, Sym("n"), Int(1))] }),
                    },
                ],
            },
        });

        var result = exec.ExecuteValue(new CodeCall { Fn = Sym("Sum"), Params = [Int(100)] }, scope, ctx);
        await Assert.That(((ExecInt)result).Value).IsEqualTo(5050); // 100+99+…+1+0
    }

    // A caught-and-degraded evaluation (RenderTree's per-node error handling, or any future canvas
    // catch) must not LEAK call depth: RunBody's finally decrements even when the body throws, so a
    // depth-32 recursion that fails partway through leaves the SAME context able to run a full
    // depth-100 recursion afterward without tripping the guard early.
    [Test]
    public async Task A_caught_error_mid_recursion_does_not_leak_call_depth()
    {
        var scope = new ExecScope();
        var exec = new CodeExecutor();
        var ctx = new ExecContext();
        // fn Boom(n) { if (n <= 0) throw-shaped (unknown field access); return Boom(n - 1) }
        // 40 levels of real recursion, then a runtime error (an absent field read) that must be
        // caught by the CALLER without leaving context.CallDepth polluted for the next call.
        Run(scope, exec, ctx, new CodeFunction
        {
            Name = "Boom",
            Params = [new CodeFunctionParam { Name = "n" }],
            Body = new CodeBlock
            {
                Statements =
                [
                    new CodeIf
                    {
                        Condition = Op(CodeInfixOpType.LessThanOrEqual, Sym("n"), Int(0)),
                        Body = new CodeReturn { Value = Prop(new CodeObject { Props = [] }, "missing") }, // throws
                    },
                    new CodeReturn
                    {
                        Value = new CodeCall { Fn = Sym("Boom"), Params = [Op(CodeInfixOpType.Subtract, Sym("n"), Int(1))] },
                    },
                ],
            },
        });

        Exception? caught = null;
        try { exec.ExecuteValue(new CodeCall { Fn = Sym("Boom"), Params = [Int(40)] }, scope, ctx); }
        catch (CodeRuntimeException ex) { caught = ex; }
        await Assert.That(caught).IsNotNull();
        await Assert.That(ctx.CallDepth).IsEqualTo(0); // fully unwound, nothing leaked

        // The same context can now run a full depth-100 legitimate recursion untouched.
        Run(scope, exec, ctx, new CodeFunction
        {
            Name = "Sum2",
            Params = [new CodeFunctionParam { Name = "n" }],
            Body = new CodeBlock
            {
                Statements =
                [
                    new CodeIf
                    {
                        Condition = Op(CodeInfixOpType.LessThanOrEqual, Sym("n"), Int(0)),
                        Body = new CodeReturn { Value = Int(0) },
                    },
                    new CodeReturn
                    {
                        Value = Op(CodeInfixOpType.Add, Sym("n"),
                            new CodeCall { Fn = Sym("Sum2"), Params = [Op(CodeInfixOpType.Subtract, Sym("n"), Int(1))] }),
                    },
                ],
            },
        });
        var result = exec.ExecuteValue(new CodeCall { Fn = Sym("Sum2"), Params = [Int(100)] }, scope, ctx);
        await Assert.That(((ExecInt)result).Value).IsEqualTo(5050);
    }

    // ── collection system-functions ────────────────────────────────────────────────

    // arr = [ {p:1}, {p:3}, {p:2} ]; arr.where(x => x.p > 1).orderBy(x => x.p) → p = [2, 3]
    [Test]
    public async Task Where_and_orderBy_with_lambdas()
    {
        var scope = new ExecScope();
        var exec = new CodeExecutor();
        var ctx = new ExecContext();

        CodeObject Item(int p) => new() { Props = [new CodeObjectProp { Name = "p", Value = Int(p) }] };
        var arr = new CodeArray { Items = [Item(1), Item(3), Item(2)] };

        // x => x.p > 1
        var predicate = new CodeFunction
        {
            Params = [new CodeFunctionParam { Name = "x" }],
            Body = new CodeBlock { Statements = [new CodeReturn { Value = Op(CodeInfixOpType.MoreThan, Prop(Sym("x"), "p"), Int(1)) }] },
        };
        // x => x.p
        var keySelector = new CodeFunction
        {
            Params = [new CodeFunctionParam { Name = "x" }],
            Body = new CodeBlock { Statements = [new CodeReturn { Value = Prop(Sym("x"), "p") }] },
        };

        Run(scope, exec, ctx, new CodeVarDec { Name = "arr", Value = arr });
        var filtered = new CodeCall { Fn = Prop(Sym("arr"), "where"), Params = [predicate] };
        var ordered = new CodeCall { Fn = Prop(filtered, "orderBy"), Params = [keySelector] };
        var result = (ExecArray)exec.ExecuteValue(ordered, scope, ctx);

        var ps = result.Items.Select(i => ((ExecInt)((ExecObject)i.Value).Props["p"]).Value).ToList();
        await Assert.That(ps).IsEquivalentTo(new[] { 2, 3 });
    }

    // ── scan-method harvest (single/any ship membership for client replay) ──────────

    CodeObject PItem(int p) => new() { Props = [new CodeObjectProp { Name = "p", Value = Int(p) }] };
    CodeFunction PredEq(int p) => new()
    {
        Params = [new CodeFunctionParam { Name = "x" }],
        Body = new CodeBlock { Statements = [new CodeReturn { Value = Op(CodeInfixOpType.Equals, Prop(Sym("x"), "p"), Int(p)) }] },
    };

    // A `.single(pred)` over a set in OUTPUT POSITION (a custom `fn render()`, DepStack empty) must harvest
    // the set's membership into the client state — else the client replays `.single` over an EMPTY array and
    // gets null/stale (the E1 bug: a set freshly repopulated by a host-action ack, consumed only by `.single`,
    // never by a foreach). Twin of ExecuteTagForEach's per-row leaf recording; `where`/`orderBy` avoid the
    // trap only because a foreach (or the shipped cache result) carries their membership.
    [Test]
    public async Task Single_in_output_position_ships_the_scanned_membership()
    {
        var scope = new ExecScope();
        var exec = new CodeExecutor();
        var ctx = new ExecContext();

        Run(scope, exec, ctx, new CodeVarDec { Name = "arr", Value = new CodeArray { Items = [PItem(1), PItem(2)] } });
        var srcArr = (ExecArray)scope.Items["arr"].Value;

        var single = new CodeCall { Fn = Prop(Sym("arr"), "single"), Params = [PredEq(2)] };
        var matched = (ExecObject)exec.ExecuteValue(single, scope, ctx);

        var arrays = (JsonObject)ClientState.Serialize(scope, ctx)["leaves"]!["arrays"]!;
        await Assert.That(arrays.ContainsKey(srcArr.Id.ToString())).IsTrue(); // membership harvested (was absent before the fix)
        var shippedIds = ((JsonArray)arrays[srcArr.Id.ToString()]!["items"]!)
            .Select(i => i!["value"]!["id"]!.GetValue<int>()).ToList();
        await Assert.That(shippedIds).Contains(matched.Id); // the matched member ships so the client re-finds it
    }

    // `.any(pred)` shares the exact defect (E1's section guard is `design.render.any(x => true)`): a set
    // consumed only by `.any` must still ship its membership, or the client replays `.any` over empty → false.
    [Test]
    public async Task Any_in_output_position_ships_the_scanned_membership()
    {
        var scope = new ExecScope();
        var exec = new CodeExecutor();
        var ctx = new ExecContext();

        Run(scope, exec, ctx, new CodeVarDec { Name = "arr", Value = new CodeArray { Items = [PItem(1), PItem(2)] } });
        var srcArr = (ExecArray)scope.Items["arr"].Value;

        var any = new CodeCall { Fn = Prop(Sym("arr"), "any"), Params = [PredEq(2)] };
        await Assert.That(((ExecBool)exec.ExecuteValue(any, scope, ctx)).Value).IsTrue();

        var arrays = (JsonObject)ClientState.Serialize(scope, ctx)["leaves"]!["arrays"]!;
        await Assert.That(arrays.ContainsKey(srcArr.Id.ToString())).IsTrue();
        await Assert.That(((JsonArray)arrays[srcArr.Id.ToString()]!["items"]!).Count).IsGreaterThan(0);
    }

    // ── memoization (Stage 4) ────────────────────────────────────────────────────────

    // A where computation is memoized: the context holds an entry whose result is the
    // derived array and whose deps record the source membership + each item's read prop.
    [Test]
    public async Task Where_is_memoized_with_result_and_dependencies()
    {
        var scope = new ExecScope();
        var exec = new CodeExecutor();
        var ctx = new ExecContext();

        CodeObject Item(int p) => new() { Props = [new CodeObjectProp { Name = "p", Value = Int(p) }] };
        var arr = new CodeArray { Items = [Item(1), Item(3), Item(2)] };
        var predicate = new CodeFunction
        {
            Params = [new CodeFunctionParam { Name = "x" }],
            Body = new CodeBlock { Statements = [new CodeReturn { Value = Op(CodeInfixOpType.MoreThan, Prop(Sym("x"), "p"), Int(1)) }] },
        };

        Run(scope, exec, ctx, new CodeVarDec { Name = "arr", Value = arr });
        var where = new CodeCall { Fn = Prop(Sym("arr"), "where"), Params = [predicate] };
        var result = (ExecArray)exec.ExecuteValue(where, scope, ctx);

        var entry = ctx.Memo.Values.Single(e => e.Key.StartsWith("where:"));
        await Assert.That(ReferenceEquals(entry.Result, result)).IsTrue();
        await Assert.That(entry.Deps.Members.Count).IsGreaterThan(0);              // observed source membership
        await Assert.That(entry.Deps.Props.Any(d => d.Prop == "p")).IsTrue();      // read each item's p
    }

    // ── transient-add through the M5 store ──────────────────────────────────────────

    [Test]
    public async Task Add_transient_object_mints_into_extent_and_links_set()
    {
        var desc = InstanceDescriptionLoader.Load("""
        types
            Db
                users set of User
            User
                name text
        """);
        var dataPath = Path.GetTempFileName();
        var store = new JsonFileInstanceStore(dataPath, desc);

        var ctx = new ExecContext();
        var db = DbBridge.LoadRoot(store, desc, ctx);
        var scope = new ExecScope();
        scope.Items["db"] = new ExecScopeItem { Value = db, IsReadOnly = true };
        var exec = new CodeExecutor(store);

        // db.users.add({ name: "Ada" })
        var add = new CodeCall
        {
            Fn = Prop(Prop(Sym("db"), "users"), "add"),
            Params = [new CodeObject { Props = [new CodeObjectProp { Name = "name", Value = Text("Ada") }] }],
        };
        Run(scope, exec, ctx, add);

        var extent = store.ReadExtent("User");
        await Assert.That(extent.Count).IsEqualTo(1);
        await Assert.That(((TextValue)extent.Values.First().Fields["name"]).Text).IsEqualTo("Ada");

        // The set now lists the new member.
        var users = store.ReadNode(NodePath.Root.Field("users")) as SetValue;
        await Assert.That(users!.Members.Count).IsEqualTo(1);
    }

    // ── sys.renderTree (M12 client-computable canvas) ────────────────────────────────

    // A MetaNode row (positive store ids, like real design rows) → the expected tag tree: element with
    // data-node provenance, a literal attr applied, a non-literal + an event attr both skipped, children
    // ORDERED by `order`, a literal leaf as text, a non-literal leaf as an expr chip, a nested element.
    [Test]
    public async Task RenderTree_builds_the_expected_tag_tree_over_a_seeded_node()
    {
        ExecObject Node(int id, params (string, IExecValue)[] props) =>
            new() { Id = id, Props = props.ToDictionary(p => p.Item1, p => p.Item2) };
        ExecArray Set(int id, params ExecObject[] members) =>
            new() { Id = id, Kind = ArrayKind.Set, Items = [.. members.Select(m => new ExecItem { Key = m.Id, Value = m })] };
        ExecText T(string v) => new() { Value = v };
        ExecInt I(int v) => new() { Value = v };

        var attrClass = Node(201, ("name", T("class")), ("value", T("\"box\"")), ("order", I(0)));   // literal → applied
        var attrEvent = Node(202, ("name", T("onmouseover")), ("value", T("\"x\"")), ("order", I(1))); // event → skipped
        var attrExpr  = Node(203, ("name", T("id")), ("value", T("user.id")), ("order", I(2)));       // non-literal → skipped

        var chipLeaf = Node(302, ("tag", T("")), ("expr", T("items.count")), ("order", I(0)));         // chip
        var textLeaf = Node(301, ("tag", T("")), ("expr", T("\"hello\"")), ("order", I(1)));           // literal text
        var childEl  = Node(303, ("tag", T("b")), ("expr", T("")), ("order", I(2)),
            ("attrs", Set(330)), ("children", Set(331)));

        var root = Node(100, ("tag", T("div")), ("expr", T("")), ("order", I(0)),
            ("attrs", Set(210, attrExpr, attrClass, attrEvent)),          // inserted OUT of order
            ("children", Set(220, textLeaf, chipLeaf, childEl)));         // inserted OUT of order

        var scope = new ExecScope();
        scope.Items["root"] = new ExecScopeItem { Value = root, IsReadOnly = true };
        var call = new CodeCall { Fn = Prop(Sym("sys"), "renderTree"), Params = [Sym("root")] };
        var tag = (ExecTag)new CodeExecutor().ExecuteValue(call, scope, new ExecContext());

        // Element: data-node provenance + the one literal attr; the event + non-literal attrs are gone.
        await Assert.That(tag.Name).IsEqualTo("div");
        await Assert.That(((ExecText)tag.Attributes["data-node"]).Value).IsEqualTo("100");
        await Assert.That(((ExecText)tag.Attributes["class"]).Value).IsEqualTo("box");
        await Assert.That(tag.Attributes.Count).IsEqualTo(2);

        // Children ORDERED by `order`: chip (0), literal text (1), nested element (2).
        await Assert.That(tag.Children.Length).IsEqualTo(3);

        var chip = (ExecTag)tag.Children[0];
        await Assert.That(chip.Name).IsEqualTo("span");
        await Assert.That(((ExecText)chip.Attributes["class"]).Value).IsEqualTo("expr-chip");
        await Assert.That(((ExecText)chip.Attributes["data-node"]).Value).IsEqualTo("302");
        await Assert.That(((ExecText)chip.Children[0]).Value).IsEqualTo("items.count");

        await Assert.That(((ExecText)tag.Children[1]).Value).IsEqualTo("hello");

        var nested = (ExecTag)tag.Children[2];
        await Assert.That(nested.Name).IsEqualTo("b");
        await Assert.That(((ExecText)nested.Attributes["data-node"]).Value).IsEqualTo("303");
        await Assert.That(nested.Children.Length).IsEqualTo(0);
    }

    // An INVALID node (tag AND expr both empty) → a visible empty chip, never silent nothing.
    [Test]
    public async Task RenderTree_empty_node_renders_a_visible_empty_chip()
    {
        var node = new ExecObject { Id = 7, Props = new()
            { ["tag"] = new ExecText { Value = "" }, ["expr"] = new ExecText { Value = "" }, ["order"] = new ExecInt { Value = 0 } } };
        var scope = new ExecScope();
        scope.Items["n"] = new ExecScopeItem { Value = node, IsReadOnly = true };
        var call = new CodeCall { Fn = Prop(Sym("sys"), "renderTree"), Params = [Sym("n")] };
        var tag = (ExecTag)new CodeExecutor().ExecuteValue(call, scope, new ExecContext());

        await Assert.That(tag.Name).IsEqualTo("span");
        await Assert.That(((ExecText)tag.Attributes["class"]).Value).IsEqualTo("expr-chip is-empty");
        await Assert.That(((ExecText)tag.Attributes["data-node"]).Value).IsEqualTo("7");
        await Assert.That(((ExecText)tag.Children[0]).Value).IsEqualTo("(empty)");
    }

    // The walk records the node fields + set memberships it reads as accessed leaves (output position),
    // so the server ships the subgraph and the client can replay renderTree — the liveness substrate.
    [Test]
    public async Task RenderTree_harvests_the_node_subgraph_as_accessed_leaves()
    {
        var child = new ExecObject { Id = 40, Props = new()
            { ["tag"] = new ExecText { Value = "" }, ["expr"] = new ExecText { Value = "\"x\"" }, ["order"] = new ExecInt { Value = 0 } } };
        var kids = new ExecArray { Id = 41, Kind = ArrayKind.Set, Items = [new ExecItem { Key = 40, Value = child }] };
        var root = new ExecObject { Id = 30, Props = new()
            { ["tag"] = new ExecText { Value = "div" }, ["expr"] = new ExecText { Value = "" },
              ["order"] = new ExecInt { Value = 0 }, ["attrs"] = new ExecArray { Id = 42, Kind = ArrayKind.Set, Items = [] },
              ["children"] = kids } };

        var scope = new ExecScope();
        scope.Items["root"] = new ExecScopeItem { Value = root, IsReadOnly = true };
        var ctx = new ExecContext();
        var call = new CodeCall { Fn = Prop(Sym("sys"), "renderTree"), Params = [Sym("root")] };
        new CodeExecutor().ExecuteValue(call, scope, ctx);

        // The root's tag + the children-set membership + the child's expr are demanded data.
        await Assert.That(ctx.AccessedObjectProps.Contains((root, "tag"))).IsTrue();
        await Assert.That(ctx.AccessedObjectProps.Contains((root, "children"))).IsTrue();
        await Assert.That(ctx.AccessedItems.Any(i => i.Item1 == kids)).IsTrue();
        await Assert.That(ctx.AccessedObjectProps.Contains((child, "expr"))).IsTrue();
    }

    // Review fix 1: an int-SHAPED attr value outside Int32 range (deenv's int is 32-bit) must NOT crash the
    // server (int.Parse would throw OverflowException, 500ing the whole render) — it is classified NON-
    // LITERAL and the attr is skipped, exactly like a bare expression. Value is 2^31 (one past Int32.MaxValue).
    [Test]
    public async Task RenderTree_an_out_of_range_int_attr_is_skipped_not_a_crash()
    {
        var attr = new ExecObject { Id = 501, Props = new()
            { ["name"] = new ExecText { Value = "big" }, ["value"] = new ExecText { Value = "2147483648" }, ["order"] = new ExecInt { Value = 0 } } };
        var attrs = new ExecArray { Id = 502, Kind = ArrayKind.Set, Items = [new ExecItem { Key = 501, Value = attr }] };
        var node = new ExecObject { Id = 500, Props = new()
            { ["tag"] = new ExecText { Value = "div" }, ["expr"] = new ExecText { Value = "" }, ["order"] = new ExecInt { Value = 0 },
              ["attrs"] = attrs, ["children"] = new ExecArray { Id = 503, Kind = ArrayKind.Set, Items = [] } } };
        var scope = new ExecScope();
        scope.Items["n"] = new ExecScopeItem { Value = node, IsReadOnly = true };
        var call = new CodeCall { Fn = Prop(Sym("sys"), "renderTree"), Params = [Sym("n")] };

        var tag = (ExecTag)new CodeExecutor().ExecuteValue(call, scope, new ExecContext()); // must not throw

        await Assert.That(tag.Attributes.ContainsKey("big")).IsFalse();
        await Assert.That(tag.Attributes.Count).IsEqualTo(1); // data-node only
    }

    // Review fix 3: a user MetaAttr literally named "data-node" must not clobber the provenance id the walk
    // itself stamps — it is skipped like an event attr, so data-node always carries the node's own intrinsic id.
    [Test]
    public async Task RenderTree_a_user_attr_named_data_node_is_skipped_never_clobbers_provenance()
    {
        var attr = new ExecObject { Id = 601, Props = new()
            { ["name"] = new ExecText { Value = "data-node" }, ["value"] = new ExecText { Value = "\"clobber\"" }, ["order"] = new ExecInt { Value = 0 } } };
        var attrs = new ExecArray { Id = 602, Kind = ArrayKind.Set, Items = [new ExecItem { Key = 601, Value = attr }] };
        var node = new ExecObject { Id = 600, Props = new()
            { ["tag"] = new ExecText { Value = "div" }, ["expr"] = new ExecText { Value = "" }, ["order"] = new ExecInt { Value = 0 },
              ["attrs"] = attrs, ["children"] = new ExecArray { Id = 603, Kind = ArrayKind.Set, Items = [] } } };
        var scope = new ExecScope();
        scope.Items["n"] = new ExecScopeItem { Value = node, IsReadOnly = true };
        var call = new CodeCall { Fn = Prop(Sym("sys"), "renderTree"), Params = [Sym("n")] };

        var tag = (ExecTag)new CodeExecutor().ExecuteValue(call, scope, new ExecContext());

        await Assert.That(((ExecText)tag.Attributes["data-node"]).Value).IsEqualTo("600"); // the node's OWN id, not "clobber"
        await Assert.That(tag.Attributes.Count).IsEqualTo(1);
    }

    // Review fix 2: an ABSENT prop on a MetaNode/MetaAttr row now THROWS (matching every other read in both
    // twins), instead of silently swallowing to ExecNull — a transient/incomplete draft (S4) must surface as
    // a genuine miss so the dep still gets recorded and a client refetch can heal it.
    [Test]
    public async Task RenderTree_an_absent_field_throws_instead_of_swallowing()
    {
        var node = new ExecObject { Id = 700, Props = new() { ["tag"] = new ExecText { Value = "div" } } }; // no "expr"/"order"/"attrs"/"children"
        var scope = new ExecScope();
        scope.Items["n"] = new ExecScopeItem { Value = node, IsReadOnly = true };
        var call = new CodeCall { Fn = Prop(Sym("sys"), "renderTree"), Params = [Sym("n")] };

        await Assert.That(() => new CodeExecutor().ExecuteValue(call, scope, new ExecContext()))
            .Throws<CodeRuntimeException>();
    }

    // T6b-4b (R7 addressing): a dictionary surfaces as an ExecArray that carries its OWNER's address
    // (OwnerRef = the db object id, DictProp = the dict prop name), and each entry ExecObject carries
    // the same owner address + its Key — so the client can persist through id-addressed dictAdd/dictRemove
    // relations instead of the path-addressed addEntry/removeEntry ops.
    [Test]
    public async Task A_dict_array_and_its_entries_carry_their_owner_address()
    {
        var desc = InstanceDescriptionLoader.Load("""
            types
                Db
                    settings dict of text by text
                    configs dict of Config by text
                Config
                    name text
            """);
        var dataPath = Path.GetTempFileName();
        try
        {
            var store = new JsonFileInstanceStore(dataPath, desc);
            store.CreateEntry(NodePath.Root.Field("settings"), new TextValue("theme"), new TextValue("dark"));
            var cfg = store.CreateObject("Config", new ObjectValue(new Dictionary<string, NodeValue>
                { ["name"] = new TextValue("api") }));
            var cfgRead = store.ReadById(cfg);
            if (cfgRead is { } cfgTuple)
                store.CreateEntry(NodePath.Root.Field("configs"), new TextValue("api"), cfgTuple.Fields);

            var ctx = new ExecContext();
            var db = DbBridge.LoadRoot(store, desc, ctx);

            // Scalar dict (settings): the array carries the owner address.
            var settingsArr = (ExecArray)db.Props["settings"];
            await Assert.That(settingsArr.OwnerRef).IsEqualTo(1);
            await Assert.That(settingsArr.DictProp).IsEqualTo("settings");
            var settingsEntry = (ExecObject)settingsArr.Items.Single().Value;
            await Assert.That(settingsEntry.OwnerRef).IsEqualTo(1);
            await Assert.That(settingsEntry.DictProp).IsEqualTo("settings");
            await Assert.That(settingsEntry.Key).IsEqualTo("theme");

            // Object dict (configs): same addressing on the array + entry.
            var configsArr = (ExecArray)db.Props["configs"];
            await Assert.That(configsArr.OwnerRef).IsEqualTo(1);
            await Assert.That(configsArr.DictProp).IsEqualTo("configs");
            var configEntry = (ExecObject)configsArr.Items.Single().Value;
            await Assert.That(configEntry.OwnerRef).IsEqualTo(1);
            await Assert.That(configEntry.DictProp).IsEqualTo("configs");
            await Assert.That(configEntry.Key).IsEqualTo("api");

            // The wire (ClientState.Serialize) emits the SAME ownerRef/dictProp/key — the emit code reads
            // these exact model fields, so the dict array + entry arrive at the client addressable. (A full
            // render that accesses the dict in output position is covered by the CodeClient/render suites;
            // here we assert the model side, which is what 4b populates.)
        }
        finally
        {
            if (File.Exists(dataPath)) File.Delete(dataPath);
        }
    }
}
