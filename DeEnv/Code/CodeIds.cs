using DeEnv.Instance;

namespace DeEnv.Code;

// Assigns a unique Id to every CodeFunction in the ui/common AST at load time. Memo
// cache keys are (function id, argument identities), so functions — including the
// lambdas passed to where/orderBy — need stable, distinct ids. The ids ride in the
// AST shipped to the client (initUi), so both interpreters key the cache identically.
// Deterministic traversal order ⇒ deterministic ids.
public static class CodeIds
{
    public static void Assign(InstanceDescription desc)
    {
        var next = 0;

        void Walk(ICodeElement? node)
        {
            switch (node)
            {
                case null:
                    return;
                case CodeFunction fn:
                    fn.Id = ++next;
                    Walk(fn.Body);
                    break;
                case CodeBlock b:
                    foreach (var s in b.Statements) Walk(s);
                    break;
                case CodeReturn r:
                    Walk(r.Value);
                    break;
                case CodeVarDec v:
                    Walk(v.Value);
                    break;
                case CodeAssignment a:
                    Walk(a.Value);
                    break;
                case CodeIf i:
                    Walk(i.Condition); Walk(i.Body); Walk(i.ElseBody);
                    break;
                case CodeInfixOp op:
                    Walk(op.Left); Walk(op.Right);
                    break;
                case CodeCall c:
                    Walk(c.Fn);
                    foreach (var p in c.Params) Walk(p);
                    break;
                case CodeArray arr:
                    foreach (var it in arr.Items) Walk(it);
                    break;
                case CodeObject obj:
                    foreach (var p in obj.Props) Walk(p.Value);
                    break;
                case CodeTag tag:
                    foreach (var at in tag.Attributes) Walk(at.Value);
                    foreach (var ch in tag.Children) Walk(ch);
                    break;
                case CodeTagForEach fe:
                    Walk(fe.Collection);
                    foreach (var ch in fe.Body) Walk(ch);
                    break;
                case CodeTagIf ti:
                    Walk(ti.Condition);
                    foreach (var ch in ti.Body) Walk(ch);
                    foreach (var ch in ti.ElseBody) Walk(ch);
                    break;
                // leaves (symbol/int/text/bool/null): nothing to walk.
            }
        }

        // Common is walked even without a ui section, so a common-only document still
        // gets distinct function ids (memo keys must never collide).
        foreach (var v in desc.Ui?.Vars ?? []) Walk(v.Value);
        foreach (var f in desc.Ui?.Functions ?? []) Walk(f);
        Walk(desc.Ui?.Render);
        foreach (var f in desc.Common?.Functions ?? []) Walk(f);
    }
}
