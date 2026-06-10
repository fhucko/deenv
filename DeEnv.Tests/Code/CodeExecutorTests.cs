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

    // ── transient-add through the M5 store ──────────────────────────────────────────

    [Test]
    public async Task Add_transient_object_mints_into_extent_and_links_set()
    {
        var desc = InstanceDescriptionLoader.Load("""
        {
          "types": [
            { "name": "Db", "baseType": "object",
              "props": [{ "name": "users", "type": "User", "cardinality": "set" }] },
            { "name": "User", "baseType": "object",
              "props": [{ "name": "name", "type": "text" }] }
          ]
        }
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
}
