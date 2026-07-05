using DeEnv.Instance;

namespace DeEnv.Code;

// Static AST scan: does an app's Code CALL a host-action builtin (`sys.create` / `sys.delete` /
// `sys.cloneInstance` / `sys.publish` / `sys.rename` / `sys.setDesign` / `sys.commitDesign`) anywhere in
// its `ui`/`common` sections? This is what the kernel uses to decide which instance gets a REAL
// KernelHostActions seam — wiring is driven by actual code USE (the AST), not by schema SHAPE. An
// instance whose Code never calls a host action gets NoHostActions (the seam is unwired, so a
// `hostAction` frame errors); one that does gets the real seam, and its host actions are then gated by
// the app's own `sys` access rule (AccessFloor.CanHostAction — the authority check, distinct from this
// WIRING check).
//
// The detector may be SIMPLE and conservative: a miss fails closed (an unwired seam → the action errors),
// and a false hit is harmless (the access rule still gates every action). So it looks only for the exact
// `sys.<hostAction>(...)` call shape — the same `sys`-rooted-callee rule CodeExecutor/CodeValidator use —
// with no data-flow analysis. It walks the whole Code tree (statements, values, tags) once.
public static class HostActionScan
{
    // The host-action builtins, keyed off the ops KernelHostActions.Run dispatches (the authoritative
    // list). A `sys.<name>(...)` call with `name` in this set is a host-action call. Kept here beside the
    // scan; the arity list (CodeValidator.BuiltinArities) is the other place these names appear.
    //
    // `createBranch`/`mergeBranch` (M13 slice 5) join the set with M13 Track-B B4 — their Branch UI landed,
    // so they are wired into Code lockstep (the same "wire lockstep, all at once" rule the Commit-button
    // slice's commitDesign addition follows: HostActionScan + CodeValidator arities + both interpreters +
    // the UI, all in one slice).
    private static readonly HashSet<string> HostActionBuiltins =
        ["create", "delete", "cloneInstance", "publish", "rename", "setDesign", "commitDesign", "revertCommit", "createBranch", "mergeBranch"];

    // True when any Code in `desc` (ui vars/functions/render + common functions) calls a host-action
    // builtin. Used by KernelHost.HostActionsFor to wire the real seam only for a host-action-using app.
    public static bool UsesHostActions(InstanceDescription desc)
    {
        foreach (var v in desc.Ui?.Vars ?? [])
            if (v.Value != null && ValueUses(v.Value)) return true;
        foreach (var f in desc.Ui?.Functions ?? [])
            if (FunctionUses(f)) return true;
        if (desc.Ui?.Render is { } render && FunctionUses(render)) return true;
        foreach (var f in desc.Common?.Functions ?? [])
            if (FunctionUses(f)) return true;
        return false;
    }

    // Is `fn` the callee of a host-action call? A `sys.<name>` member access on the bare `sys` symbol
    // whose member is a host-action builtin — the same shape CodeValidator.IsSysBuiltinCallee matches.
    private static bool IsHostActionCallee(ICodeValue fn) =>
        fn is CodeInfixOp { Op: CodeInfixOpType.ObjectProp, Left: CodeSymbol { Name: "sys" }, Right: CodeSymbol member }
        && HostActionBuiltins.Contains(member.Name);

    private static bool FunctionUses(CodeFunction fn) => BlockUses(fn.Body);

    private static bool BlockUses(CodeBlock block) => block.Statements.Any(StatementUses);

    private static bool StatementUses(ICodeStatement statement) => statement switch
    {
        CodeBlock block => BlockUses(block),
        CodeVarDec varDec => varDec.Value != null && ValueUses(varDec.Value),
        CodeAmbient ambient => ValueUses(ambient.Value),
        CodeFunction fn => BlockUses(fn.Body),
        CodeReturn ret => ValueUses(ret.Value),
        CodeAssignment assign => ValueUses(assign.Target) || ValueUses(assign.Value),
        CodeCall call => ValueUses(call),
        CodeIf codeIf => ValueUses(codeIf.Condition) || StatementUses(codeIf.Body)
                         || (codeIf.ElseBody != null && StatementUses(codeIf.ElseBody)),
        _ => false,
    };

    private static bool ValueUses(ICodeValue value) => value switch
    {
        CodeCall call => IsHostActionCallee(call.Fn) || ValueUses(call.Fn) || call.Params.Any(ValueUses),
        CodeInfixOp infix => ValueUses(infix.Left) || ValueUses(infix.Right),
        CodeNot not => ValueUses(not.Operand),
        CodeTernary ternary => ValueUses(ternary.Condition) || ValueUses(ternary.Then) || ValueUses(ternary.Else),
        CodeArray array => array.Items.Any(ValueUses),
        CodeObject obj => obj.Props.Any(p => ValueUses(p.Value)),
        CodeAssignment assign => ValueUses(assign.Target) || ValueUses(assign.Value),
        CodeFunction fn => BlockUses(fn.Body),
        CodeTag tag => TagUses(tag),
        _ => false, // CodeInt/Bool/Text/Null/Symbol — no call
    };

    private static bool TagUses(CodeTag tag) =>
        tag.Attributes.Any(a => ValueUses(a.Value)) || tag.Children.Any(TagChildUses);

    private static bool TagChildUses(ICodeTagChild child) => child switch
    {
        CodeTagForEach forEach => ValueUses(forEach.Collection) || forEach.Body.Any(TagChildUses),
        CodeTagIf tagIf => ValueUses(tagIf.Condition) || tagIf.Body.Any(TagChildUses) || tagIf.ElseBody.Any(TagChildUses),
        ICodeValue value => ValueUses(value),
        _ => false,
    };
}
