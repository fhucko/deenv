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

        // The SYSTEM scope holds the framework-provided symbols ABOVE the custom code: the
        // bare state vars db (read-only), path (the request URL) and status (the first-paint
        // HTTP status, default 200), plus the `sys` namespace object. The less-common framework
        // names live UNDER `sys` (sys.instances; the builtins sys.field/humanize/extent/setRef/
        // nest/clone) rather than as bare symbols — so they are reached as member access on
        // `sys`, never resolved as bare symbols here. User vars/functions live in a child `top`
        // scope: they read these by walking up but never collide (a user var simply shadows).
        var system = new Scope(null);
        system.Declare("db", writable: false);
        system.Declare("path", writable: true);
        system.Declare("status", writable: true);
        system.Declare("sys", writable: false); // the framework namespace (instances + the builtins)

        var top = new Scope(system);
        foreach (var v in ui.Vars ?? [])
        {
            if (top.IsDeclaredLocally(v.Name))
                throw new SchemaValidationException($"Duplicate ui var '{v.Name}'.");
            top.Declare(v.Name, writable: true);
        }
        foreach (var f in ui.Functions ?? []) DeclareNamed(f, top);
        foreach (var f in desc.Common?.Functions ?? []) DeclareNamed(f, top);

        foreach (var v in ui.Vars ?? [])
            if (v.Value != null) ValidateValue(v.Value, top);

        foreach (var f in ui.Functions ?? []) ValidateFunction(f, top);
        foreach (var f in desc.Common?.Functions ?? []) ValidateFunction(f, top);

        // An app is fully custom (`fn render()`) or fully auto (the default self-hosted
        // generic UI when there is no render). A ui section with only vars/helpers and no
        // render is valid: the generic UI takes over.
        if (ui.Render != null) ValidateFunction(ui.Render, top);
    }

    // The fixed arities of the `sys` builtins, mirroring each impl's argument-count check in
    // CodeExecutor. Used to preserve a load-time arity guard now the builtins are namespaced.
    // NOTE: a new sys builtin lives in THREE places — both interpreters' dispatch (CodeExecutor +
    // codeExec.ts) AND this map — or it silently loses its load-time arity guard.
    private static readonly Dictionary<string, int> BuiltinArities = new()
    {
        ["field"] = 2,
        ["humanize"] = 1,
        ["extent"] = 1,
        ["canWrite"] = 2, // (typeName, verb) — server-resolved write capability, shipped like extent/schema
        ["canRead"] = 1,  // (typeName) — server-resolved read capability (hide unreadable collections/routes)
        ["setRef"] = 3,
        ["publish"] = 2, // (schema, targetId)
        ["create"] = 2,  // (schema, name) — addressing is by path, so no port args
        ["cloneInstance"] = 1, // (sourceId) — the clone's mount name derives from the source
        ["delete"] = 1,  // (targetId)
        ["rename"] = 2,  // (id, name)
        ["setDesign"] = 2, // (schema, targetId)
        ["login"] = 2,   // (name, password) — a client-only host effect (the session→principal bind)
        ["logout"] = 0,  // () — the mirror of login: a client-only host effect (clear the principal)
        ["setPassword"] = 2, // (user, newPassword) — a client-only host effect (gated User-edit; user admin)
        ["nest"] = 2,
        ["segment"] = 2,
        ["toInt"] = 1,
        ["id"] = 1,
        ["new"] = 1,
        ["resolve"] = 1, // (pathText) → { kind, target, parent, prop, typeName, parentType }
    };

    // A callee of the form `sys.<name>` (a member access on the bare `sys` symbol) — the
    // namespaced builtins. Mirrors CodeExecutor.IsSysBuiltin (the runtime dispatch rule).
    private static bool IsSysBuiltinCallee(ICodeValue fn, out string name)
    {
        if (fn is CodeInfixOp { Op: CodeInfixOpType.ObjectProp, Left: CodeSymbol { Name: "sys" }, Right: CodeSymbol member })
        {
            name = member.Name;
            return true;
        }
        name = "";
        return false;
    }

    private static void DeclareNamed(CodeFunction fn, Scope scope)
    {
        if (fn.Name != null) scope.Declare(fn.Name, writable: false, arity: fn.Params.Length);
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
                // The executor throws on a same-scope redeclare; fail at load instead.
                if (scope.IsDeclaredLocally(varDec.Name))
                    throw new SchemaValidationException($"Variable '{varDec.Name}' is declared twice in the same block.");
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
        switch (assign.Target)
        {
            case CodeSymbol sym:
                if (!scope.TryResolve(sym.Name, out var writable))
                    throw new SchemaValidationException($"Assignment to undeclared symbol '{sym.Name}'.");
                if (!writable)
                    throw new SchemaValidationException($"Assignment to read-only symbol '{sym.Name}'.");
                break;
            // An object-field lvalue (`obj.member`) is always assignable; validate that the
            // object expression resolves (the member name itself is not scope-checked).
            case CodeInfixOp { Op: CodeInfixOpType.ObjectProp, Left: var left }:
                ValidateValue(left, scope);
                break;
            default:
                throw new SchemaValidationException("Invalid assignment target.");
        }
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
            case CodeNot not:
                ValidateValue(not.Operand, scope);
                break;
            case CodeCall call:
                ValidateValue(call.Fn, scope);
                foreach (var arg in call.Params) ValidateValue(arg, scope);
                // Static arity check for a direct call of a NAMED function (vars
                // holding functions have unknown arity and are checked at runtime).
                if (call.Fn is CodeSymbol callee
                    && scope.TryResolveArity(callee.Name, out var arity)
                    && call.Params.Length != arity)
                    throw new SchemaValidationException(
                        $"'{callee.Name}' takes {arity} argument(s) but is called with {call.Params.Length}.");
                // The same fixed-arity guard for a `sys.<builtin>(...)` call (the namespaced
                // builtins). Preserves the load-time check the bare builtins had via their
                // per-impl argument-count throw; a `sys.field()` with the wrong count fails to
                // load rather than at first paint.
                if (IsSysBuiltinCallee(call.Fn, out var builtin)
                    && BuiltinArities.TryGetValue(builtin, out var builtinArity)
                    && call.Params.Length != builtinArity)
                    throw new SchemaValidationException(
                        $"'sys.{builtin}' takes {builtinArity} argument(s) but is called with {call.Params.Length}.");
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

    // Lexical scope for validation: name → (writable, arity for named functions).
    // Mirrors the executor's read-only rules (params/function-names/db read-only;
    // vars/varDecs writable).
    private sealed class Scope(CodeValidator.Scope? parent)
    {
        private readonly Dictionary<string, (bool Writable, int? Arity)> _items = [];
        private readonly Scope? _parent = parent;

        public void Declare(string name, bool writable, int? arity = null) => _items[name] = (writable, arity);

        public bool IsDeclaredLocally(string name) => _items.ContainsKey(name);

        public bool TryResolve(string name, out bool writable)
        {
            if (_items.TryGetValue(name, out var item)) { writable = item.Writable; return true; }
            if (_parent != null) return _parent.TryResolve(name, out writable);
            writable = false;
            return false;
        }

        public bool TryResolveArity(string name, out int arity)
        {
            if (_items.TryGetValue(name, out var item))
            {
                arity = item.Arity ?? 0;
                return item.Arity != null;
            }
            if (_parent != null) return _parent.TryResolveArity(name, out arity);
            arity = 0;
            return false;
        }
    }
}
