using DeEnv.Instance;

namespace DeEnv.Code;

// Structural validation of the `ui` / `common` Code AST at load time. This is NOT
// a type-checker (deferred per DECISIONS.md) — type mismatches surface as runtime
// errors. It enforces three structural rules so a broken component fails to load
// rather than blowing up obscurely on first paint:
//
//   1. Every symbol reference resolves to a declared name (lexical scope), with
//      `db` and the section's vars/functions in the top scope.
//   2. Every assignment targets a declared, writable symbol (params, function
//      names and `db` are read-only; vars and varDecs are writable).
//   3. A two-way `value`/`checked` binding on an <input> whose target is a bare
//      symbol must target a writable symbol (so the binding can write back).
//
// The right-hand side of an object-prop access (`x.field`) is a member name, not a
// symbol, so it is not resolved against the scope (validating real props is the
// deferred type-checker's job).
public static class CodeValidator
{
    public static void Validate(InstanceDescription desc)
    {
        var ui = desc.Ui;
        if (ui == null) return;

        // Top scope: db (read-only) + ui vars (writable) + all function names.
        var top = new Scope(null);
        top.Declare("db", writable: false);
        foreach (var v in ui.Vars ?? []) top.Declare(v.Name, writable: true);
        foreach (var f in ui.Functions ?? []) DeclareNamed(f, top);
        foreach (var f in desc.Common?.Functions ?? []) DeclareNamed(f, top);

        foreach (var v in ui.Vars ?? [])
            if (v.Value != null) ValidateValue(v.Value, top);

        foreach (var f in ui.Functions ?? []) ValidateFunction(f, top);
        foreach (var f in desc.Common?.Functions ?? []) ValidateFunction(f, top);

        if (ui.Render == null)
            throw new SchemaValidationException("The 'ui' section must define a 'render' function.");
        ValidateFunction(ui.Render, top);

        // Client-run code (render + non-server-only functions) must not reference a
        // server-only function — those are never shipped, so a reference would fault
        // on the client. Server-side code (var initializers, server-only functions)
        // may. (A sensitive *field* read by client-run code is caught at render time;
        // the static type-aware check needs the deferred type-checker.)
        var serverOnly = new HashSet<string>();
        foreach (var f in (ui.Functions ?? []).Concat(desc.Common?.Functions ?? []))
            if (f.ServerOnly && f.Name != null) serverOnly.Add(f.Name);

        if (serverOnly.Count > 0)
        {
            CheckNoServerOnlyRefs(ui.Render, serverOnly);
            foreach (var f in ui.Functions ?? []) if (!f.ServerOnly) CheckNoServerOnlyRefs(f, serverOnly);
            foreach (var f in desc.Common?.Functions ?? []) if (!f.ServerOnly) CheckNoServerOnlyRefs(f, serverOnly);
        }
    }

    // Walks client-run code for a symbol naming a server-only function. The right
    // side of object-prop access is a member name, not a symbol, so it is skipped.
    private static void CheckNoServerOnlyRefs(ICodeElement node, HashSet<string> serverOnly)
    {
        switch (node)
        {
            case CodeSymbol s when serverOnly.Contains(s.Name):
                throw new SchemaValidationException(
                    $"Client-run code references server-only function '{s.Name}'. Compute it in a " +
                    $"var initializer (server-side) and read the result, which ships as state.");
            case CodeFunction fn: CheckNoServerOnlyRefs(fn.Body, serverOnly); break;
            case CodeBlock b: foreach (var s in b.Statements) CheckNoServerOnlyRefs(s, serverOnly); break;
            case CodeReturn r: CheckNoServerOnlyRefs(r.Value, serverOnly); break;
            case CodeVarDec v when v.Value != null: CheckNoServerOnlyRefs(v.Value, serverOnly); break;
            case CodeAssignment a: CheckNoServerOnlyRefs(a.Value, serverOnly); break;
            case CodeIf i:
                CheckNoServerOnlyRefs(i.Condition, serverOnly);
                CheckNoServerOnlyRefs(i.Body, serverOnly);
                if (i.ElseBody != null) CheckNoServerOnlyRefs(i.ElseBody, serverOnly);
                break;
            case CodeInfixOp op:
                CheckNoServerOnlyRefs(op.Left, serverOnly);
                if (op.Op != CodeInfixOpType.ObjectProp) CheckNoServerOnlyRefs(op.Right, serverOnly);
                break;
            case CodeCall c:
                CheckNoServerOnlyRefs(c.Fn, serverOnly);
                foreach (var p in c.Params) CheckNoServerOnlyRefs(p, serverOnly);
                break;
            case CodeArray arr: foreach (var it in arr.Items) CheckNoServerOnlyRefs(it, serverOnly); break;
            case CodeObject obj: foreach (var p in obj.Props) CheckNoServerOnlyRefs(p.Value, serverOnly); break;
            case CodeTag tag:
                foreach (var at in tag.Attributes) CheckNoServerOnlyRefs(at.Value, serverOnly);
                foreach (var ch in tag.Children) CheckNoServerOnlyRefs(ch, serverOnly);
                break;
            case CodeTagForEach fe:
                CheckNoServerOnlyRefs(fe.Collection, serverOnly);
                foreach (var ch in fe.Body) CheckNoServerOnlyRefs(ch, serverOnly);
                break;
            case CodeTagIf ti:
                CheckNoServerOnlyRefs(ti.Condition, serverOnly);
                foreach (var ch in ti.Body) CheckNoServerOnlyRefs(ch, serverOnly);
                foreach (var ch in ti.ElseBody) CheckNoServerOnlyRefs(ch, serverOnly);
                break;
        }
    }

    private static void DeclareNamed(CodeFunction fn, Scope scope)
    {
        if (fn.Name != null) scope.Declare(fn.Name, writable: false);
    }

    private static void ValidateFunction(CodeFunction fn, Scope parent)
    {
        var scope = new Scope(parent);
        foreach (var p in fn.Params) scope.Declare(p.Name, writable: false);
        ValidateBlock(fn.Body, scope);
    }

    private static void ValidateBlock(CodeBlock block, Scope parent)
    {
        var scope = new Scope(parent);
        foreach (var s in block.Statements) ValidateStatement(s, scope);
    }

    private static void ValidateStatement(ICodeStatement statement, Scope scope)
    {
        switch (statement)
        {
            case CodeBlock block:
                ValidateBlock(block, scope);
                break;
            case CodeVarDec varDec:
                if (varDec.Value != null) ValidateValue(varDec.Value, scope);
                scope.Declare(varDec.Name, writable: true);
                break;
            case CodeFunction fn:
                DeclareNamed(fn, scope);
                ValidateFunction(fn, scope);
                break;
            case CodeReturn ret:
                ValidateValue(ret.Value, scope);
                break;
            case CodeAssignment assign:
                ValidateAssignment(assign, scope);
                break;
            case CodeCall call:
                ValidateValue(call, scope);
                break;
            case CodeIf codeIf:
                ValidateValue(codeIf.Condition, scope);
                ValidateStatement(codeIf.Body, scope);
                if (codeIf.ElseBody != null) ValidateStatement(codeIf.ElseBody, scope);
                break;
            default:
                throw new SchemaValidationException($"Unsupported statement '{statement.GetType().Name}' in code.");
        }
    }

    private static void ValidateAssignment(CodeAssignment assign, Scope scope)
    {
        if (!scope.TryResolve(assign.Target.Name, out var writable))
            throw new SchemaValidationException($"Assignment to undeclared symbol '{assign.Target.Name}'.");
        if (!writable)
            throw new SchemaValidationException($"Assignment to read-only symbol '{assign.Target.Name}'.");
        ValidateValue(assign.Value, scope);
    }

    private static void ValidateValue(ICodeValue value, Scope scope)
    {
        switch (value)
        {
            case CodeInt or CodeBool or CodeText or CodeNull:
                break;
            case CodeSymbol symbol:
                if (!scope.TryResolve(symbol.Name, out _))
                    throw new SchemaValidationException($"Unknown symbol '{symbol.Name}'.");
                break;
            case CodeArray array:
                foreach (var item in array.Items) ValidateValue(item, scope);
                break;
            case CodeObject obj:
                foreach (var prop in obj.Props) ValidateValue(prop.Value, scope);
                break;
            case CodeInfixOp infix:
                ValidateValue(infix.Left, scope);
                // The right side of object-prop access is a member name, not a symbol.
                if (infix.Op != CodeInfixOpType.ObjectProp) ValidateValue(infix.Right, scope);
                break;
            case CodeCall call:
                ValidateValue(call.Fn, scope);
                foreach (var arg in call.Params) ValidateValue(arg, scope);
                break;
            case CodeFunction fn:
                ValidateFunction(fn, scope);
                break;
            case CodeAssignment assign:
                ValidateAssignment(assign, scope);
                break;
            case CodeTag tag:
                ValidateTag(tag, scope);
                break;
            default:
                throw new SchemaValidationException($"Unsupported value '{value.GetType().Name}' in code.");
        }
    }

    private static void ValidateTag(CodeTag tag, Scope scope)
    {
        foreach (var attr in tag.Attributes)
        {
            ValidateValue(attr.Value, scope);
            if (tag.Name == "input" && attr.Name is "value" or "checked")
                ValidateTwoWayTarget(attr.Value, attr.Name, scope);
        }
        foreach (var child in tag.Children) ValidateTagChild(child, scope);
    }

    // A two-way binding writes back through its target. A bare-symbol target must be
    // writable; a prop chain (item.field) is always assignable; anything else is a
    // one-way binding with no lvalue requirement.
    private static void ValidateTwoWayTarget(ICodeValue expr, string attrName, Scope scope)
    {
        if (expr is CodeSymbol symbol
            && scope.TryResolve(symbol.Name, out var writable) && !writable)
            throw new SchemaValidationException(
                $"Two-way binding '{attrName}' targets read-only symbol '{symbol.Name}'.");
    }

    private static void ValidateTagChild(ICodeTagChild child, Scope scope)
    {
        switch (child)
        {
            case CodeTagForEach forEach:
                ValidateValue(forEach.Collection, scope);
                var loopScope = new Scope(scope);
                loopScope.Declare(forEach.Item.Name, writable: false);
                foreach (var c in forEach.Body) ValidateTagChild(c, loopScope);
                break;
            case CodeTagIf tagIf:
                ValidateValue(tagIf.Condition, scope);
                foreach (var c in tagIf.Body) ValidateTagChild(c, scope);
                foreach (var c in tagIf.ElseBody) ValidateTagChild(c, scope);
                break;
            case ICodeValue value:
                ValidateValue(value, scope);
                break;
            default:
                throw new SchemaValidationException($"Unsupported tag child '{child.GetType().Name}' in code.");
        }
    }

    // Lexical scope for validation: name → writable. Mirrors the executor's
    // read-only rules (params/function-names/db read-only; vars/varDecs writable).
    private sealed class Scope(CodeValidator.Scope? parent)
    {
        private readonly Dictionary<string, bool> _items = [];
        private readonly Scope? _parent = parent;

        public void Declare(string name, bool writable) => _items[name] = writable;

        public bool TryResolve(string name, out bool writable)
        {
            if (_items.TryGetValue(name, out writable)) return true;
            if (_parent != null) return _parent.TryResolve(name, out writable);
            writable = false;
            return false;
        }
    }
}
