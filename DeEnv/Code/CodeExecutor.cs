using DeEnv.Storage;

namespace DeEnv.Code;

// Tree-walking interpreter for the Code AST. Ported from the app15 prototype and
// extended with collection system-functions (add / remove / where / orderBy) and
// optional persistence of db-backed mutations through IInstanceStore.
//
// `where`/`orderBy` return derived (read-only, transient) collections that reuse
// the original item references — preserving object identity for reconciliation.
public sealed class CodeExecutor
{
    private static readonly HashSet<string> CollectionMethods = ["add", "remove", "setEntry", "where", "orderBy", "any"];

    private readonly IInstanceStore? _store;

    public CodeExecutor(IInstanceStore? store = null) => _store = store;

    // ── statements ──────────────────────────────────────────────────────────────

    public IExecValue? ExecuteStatement(ICodeStatement statement, ExecScope scope, ExecContext context)
    {
        switch (statement)
        {
            case CodeAssignment assignment: ExecuteAssignment(assignment, scope, context); return null;
            case CodeBlock block:           return ExecuteBlock(block, scope, context);
            case CodeVarDec varDec:         ExecuteVarDec(varDec, scope, context); return null;
            case CodeFunction function:     ExecuteFunction(function, scope); return null;
            case CodeReturn ret:            return ExecuteValue(ret.Value, scope, context);
            case CodeCall call:             ExecuteCall(call, scope, context); return null;
            case CodeIf codeIf:             return ExecuteIf(codeIf, scope, context);
            default: throw new NotImplementedException($"Statement {statement.GetType().Name}");
        }
    }

    private IExecValue? ExecuteIf(CodeIf codeIf, ExecScope scope, ExecContext context)
    {
        var condition = ExecuteValue(codeIf.Condition, scope, context) as ExecBool
            ?? throw new CodeRuntimeException("Result of if condition is not boolean.");
        var code = condition.Value ? codeIf.Body : codeIf.ElseBody;
        return code == null ? null : ExecuteStatement(code, scope, context);
    }

    private static ExecFunction ExecuteFunction(CodeFunction function, ExecScope scope)
    {
        var fn = new ExecFunction { Function = function, Scope = scope };
        if (function.Name != null)
            scope.Items[function.Name] = new ExecScopeItem { Value = fn, IsReadOnly = true };
        return fn;
    }

    private void ExecuteVarDec(CodeVarDec varDec, ExecScope scope, ExecContext context)
    {
        if (scope.Items.ContainsKey(varDec.Name))
            throw new CodeRuntimeException($"Variable '{varDec.Name}' already exists.");
        var value = varDec.Value == null ? new ExecNull() : ExecuteValue(varDec.Value, scope, context);
        scope.Items[varDec.Name] = new ExecScopeItem { Value = value, IsReadOnly = false };
    }

    private void ExecuteAssignment(CodeAssignment assignment, ExecScope scope, ExecContext context) =>
        AssignAndReturn(assignment, scope, context);

    // Assign to a var (a symbol) or an object field (`obj.member`) and return the value.
    // A field write sets the prop in place — the same effect two-way binding has; the
    // client twin additionally invalidates/persists.
    private IExecValue AssignAndReturn(CodeAssignment assignment, ExecScope scope, ExecContext context)
    {
        var value = ExecuteValue(assignment.Value, scope, context);
        switch (assignment.Target)
        {
            case CodeSymbol sym:
            {
                var itemScope = FindScope(sym.Name, scope);
                var item = itemScope.Items[sym.Name];
                if (item.IsReadOnly)
                    throw new CodeRuntimeException($"Symbol '{sym.Name}' is read only.");
                item.Value = value;
                break;
            }
            case CodeInfixOp { Op: CodeInfixOpType.ObjectProp, Left: var left, Right: CodeSymbol member }:
            {
                if (ExecuteValue(left, scope, context) is not ExecObject obj)
                    throw new CodeRuntimeException("Cannot assign a field on a non-object.");
                obj.Props[member.Name] = value;
                break;
            }
            default:
                throw new CodeRuntimeException("Invalid assignment target.");
        }
        return value;
    }

    private IExecValue? ExecuteBlock(CodeBlock block, ExecScope scope, ExecContext context)
    {
        var innerScope = new ExecScope { Parent = scope };
        foreach (var statement in block.Statements)
        {
            var value = ExecuteStatement(statement, innerScope, context);
            if (value != null) return value;
        }
        return null;
    }

    // ── values ────────────────────────────────────────────────────────────────────

    public IExecValue ExecuteValue(ICodeValue value, ExecScope scope, ExecContext context) => value switch
    {
        CodeInt codeInt        => new ExecInt { Value = codeInt.Value },
        CodeFunction codeFn    => ExecuteFunction(codeFn, scope),
        CodeCall codeCall      => ExecuteCall(codeCall, scope, context),
        CodeTag codeTag        => ExecuteTag(codeTag, scope, context),
        CodeText codeText      => new ExecText { Value = codeText.Value },
        CodeBool codeBool      => new ExecBool { Value = codeBool.Value },
        CodeInfixOp codeInfix  => ExecuteInfixOp(codeInfix, scope, context),
        CodeSymbol codeSymbol  => ExecuteSymbol(codeSymbol, scope, context),
        CodeObject codeObject  => ExecuteObject(codeObject, scope, context),
        CodeArray codeArray    => ExecuteArray(codeArray, scope, context),
        CodeAssignment assign  => ExecuteAssignmentValue(assign, scope, context),
        CodeNull               => new ExecNull(),
        _ => throw new NotImplementedException($"Value {value.GetType().Name}"),
    };

    private IExecValue ExecuteAssignmentValue(CodeAssignment assignment, ExecScope scope, ExecContext context) =>
        AssignAndReturn(assignment, scope, context);

    private ExecArray ExecuteArray(CodeArray codeArray, ExecScope scope, ExecContext context)
    {
        var items = codeArray.Items
            .Select(p => new ExecItem { Key = --context.LastId.Value, Value = ExecuteValue(p, scope, context) })
            .ToList();
        return new ExecArray { Items = items, Id = --context.LastId.Value, Kind = ArrayKind.List };
    }

    private ExecObject ExecuteObject(CodeObject codeObject, ExecScope scope, ExecContext context)
    {
        var props = new Dictionary<string, IExecValue>();
        foreach (var prop in codeObject.Props)
            props[prop.Name] = ExecuteValue(prop.Value, scope, context);
        return new ExecObject { Props = props, Id = --context.LastId.Value };
    }

    private static IExecValue ExecuteSymbol(CodeSymbol codeSymbol, ExecScope scope, ExecContext context)
    {
        var itemScope = FindScope(codeSymbol.Name, scope);
        var item = itemScope.Items[codeSymbol.Name];
        if (itemScope.IsTop)
        {
            // A writable top-scope var read inside a computation is a dependency:
            // assigning the var must invalidate the cached result. (Read-only items —
            // db, functions — can never be reassigned, so they are not deps.)
            if (!item.IsReadOnly && context.DepStack.Count > 0)
                context.DepStack.Peek().Vars.Add(new VarDep(codeSymbol.Name));
            OnValueAccessed(context, item.Value);
        }
        return item.Value;
    }

    private static void OnValueAccessed(ExecContext context, IExecValue value)
    {
        if (context.DepStack.Count > 0)
        {
            // Inside a computation: a pending leaf — promoted only if the result is tags.
            if (value is ExecObject o) context.LeafStack.Peek().Props.Add((o, null));
            else if (value is ExecArray c) context.LeafStack.Peek().Items.Add((c, null));
            return;
        }
        if (value is ExecObject obj) context.AccessedObjectProps.Add((obj, null));
        else if (value is ExecArray coll) context.AccessedItems.Add((coll, null));
    }

    private IExecValue ExecuteInfixOp(CodeInfixOp codeInfixOp, ExecScope scope, ExecContext context)
    {
        if (codeInfixOp.Op == CodeInfixOpType.ObjectProp)
        {
            if (codeInfixOp.Right is not CodeSymbol member)
                throw new CodeRuntimeException("Object-prop access expects a symbol on the right.");
            var target = ExecuteValue(codeInfixOp.Left, scope, context);

            // A collection method (db.users.add / .where / …) binds to its target.
            if (target is ExecArray coll && CollectionMethods.Contains(member.Name))
                return new ExecSysFunction { Target = coll, Method = member.Name };

            if (target is not ExecObject obj)
                throw new CodeRuntimeException($"Cannot read '{member.Name}' on a non-object.");
            if (!obj.Props.TryGetValue(member.Name, out var value))
                throw new CodeRuntimeException($"Unknown field '{member.Name}'.");

            RecordPropAccess(obj, member.Name, value, context);
            return value;
        }

        var left = ExecuteValue(codeInfixOp.Left, scope, context);
        var right = ExecuteValue(codeInfixOp.Right, scope, context);
        return codeInfixOp.Op switch
        {
            CodeInfixOpType.Add             => new ExecInt { Value = AsInt(left) + AsInt(right) },
            CodeInfixOpType.Subtract        => new ExecInt { Value = AsInt(left) - AsInt(right) },
            CodeInfixOpType.Multiply        => new ExecInt { Value = AsInt(left) * AsInt(right) },
            CodeInfixOpType.Divide          => new ExecInt { Value = AsInt(left) / AsInt(right) },
            CodeInfixOpType.Modulo          => new ExecInt { Value = AsInt(left) % AsInt(right) },
            CodeInfixOpType.Equals          => new ExecBool { Value = Equals(left.Value, right.Value) },
            CodeInfixOpType.NotEquals       => new ExecBool { Value = !Equals(left.Value, right.Value) },
            CodeInfixOpType.LessThan        => new ExecBool { Value = AsInt(left) < AsInt(right) },
            CodeInfixOpType.LessThanOrEqual => new ExecBool { Value = AsInt(left) <= AsInt(right) },
            CodeInfixOpType.MoreThan        => new ExecBool { Value = AsInt(left) > AsInt(right) },
            CodeInfixOpType.MoreThanOrEqual => new ExecBool { Value = AsInt(left) >= AsInt(right) },
            CodeInfixOpType.And             => new ExecBool { Value = AsBool(left) && AsBool(right) },
            CodeInfixOpType.Or              => new ExecBool { Value = AsBool(left) || AsBool(right) },
            _ => throw new NotImplementedException($"Infix op {codeInfixOp.Op}"),
        };
    }

    // Record a prop read (the displayed-leaf / dependency bookkeeping shared by static
    // `obj.member` access and dynamic `field(obj, name)`): DepStack empty → an output
    // leaf; inside a computation → a dependency plus a pending leaf.
    private static void RecordPropAccess(ExecObject obj, string name, IExecValue value, ExecContext context)
    {
        if (context.DepStack.Count == 0) context.AccessedObjectProps.Add((obj, name));
        else
        {
            context.DepStack.Peek().Props.Add(new PropDep(obj.Id, name));
            context.LeafStack.Peek().Props.Add((obj, name));
        }
        OnValueAccessed(context, value);
    }

    // field(obj, name): dynamic by-name prop access — the reflective twin of `obj.member`,
    // for the self-hosted generic UI (the prop name comes from schema data at runtime, so
    // it cannot be a static member symbol). Same dependency bookkeeping as `.member`; the
    // client twin (codeExec.ts) additionally returns a setValue so a bound input writes back.
    private IExecValue ExecuteField(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 2)
            throw new CodeRuntimeException("field(obj, name) takes two arguments.");
        var target = ExecuteValue(call.Params[0], scope, context);
        var nameVal = ExecuteValue(call.Params[1], scope, context);
        if (target is not ExecObject obj)
            throw new CodeRuntimeException("field() expects an object.");
        if (nameVal is not ExecText name)
            throw new CodeRuntimeException("field() expects a text field name.");
        if (!obj.Props.TryGetValue(name.Value, out var value))
            throw new CodeRuntimeException($"Unknown field '{name.Value}'.");
        RecordPropAccess(obj, name.Value, value, context);
        return value;
    }

    // humanize(text): a prop name → a human label ("companyName" → "Company name").
    // Pure; runs identically on server and client (TextUtil / codeExec.ts twin).
    private IExecValue ExecuteHumanize(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 1)
            throw new CodeRuntimeException("humanize(text) takes one argument.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecText text)
            throw new CodeRuntimeException("humanize() expects a text value.");
        return new ExecText { Value = TextUtil.Humanize(text.Value) };
    }

    // extent(typeName): all objects of a type (the reference picker's candidates), as a
    // memoized computation keyed by type — its displayed result ships and the client
    // reuses it; a mint/setRef stales `extent:*` so the next render refetches a fresh list.
    private IExecValue ExecuteExtent(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 1)
            throw new CodeRuntimeException("extent(typeName) takes one argument.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecText typeName)
            throw new CodeRuntimeException("extent() expects a text type name.");
        if (_store == null)
            throw new CodeRuntimeException("extent() requires a store.");
        return Memoize($"extent:{typeName.Value}", context, () => DbBridge.LoadExtent(_store, typeName.Value, context));
    }

    // clone(obj): a fresh object with the source's SCALAR props copied (shallow; scalars
    // are immutable so the values are shared). Used to mint a new draft from a type's
    // blank template — a generic component's create-new state.
    private IExecValue ExecuteClone(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 1)
            throw new CodeRuntimeException("clone(obj) takes one argument.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecObject obj)
            throw new CodeRuntimeException("clone() expects an object.");
        var props = new Dictionary<string, IExecValue>();
        foreach (var (name, v) in obj.Props)
            if (v is ExecInt or ExecText or ExecBool or ExecNull) props[name] = v;
        return new ExecObject { Props = props, Id = --context.LastId.Value };
    }

    // nest(base, seg): a URL path-join ("/notes" + a member → "/notes/3") — Code has no
    // string concatenation, so building nested member links needs a builtin. `seg` is a
    // text (a prop name, or — for a future dictionary route — a text/int key) or an object
    // (→ its intrinsic id). A trailing "/" on base is trimmed so nest("/", "x") == "/x". One
    // primitive covers prop names, set members, and dict keys — no further URL builtin.
    private IExecValue ExecuteNest(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 2)
            throw new CodeRuntimeException("nest(base, seg) takes two arguments.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecText baseText)
            throw new CodeRuntimeException("nest() expects a text base path.");
        var segStr = ExecuteValue(call.Params[1], scope, context) switch
        {
            ExecObject obj => obj.Id.ToString(),
            ExecInt i => i.Value.ToString(),
            ExecText t => t.Value,
            _ => throw new CodeRuntimeException("nest() expects a text or object segment."),
        };
        return new ExecText { Value = baseText.Value.TrimEnd('/') + "/" + segStr };
    }

    private static int AsInt(IExecValue v) => v is ExecInt i ? i.Value
        : throw new CodeRuntimeException("Expected an int.");
    private static bool AsBool(IExecValue v) => v is ExecBool b ? b.Value
        : throw new CodeRuntimeException("Expected a bool.");

    // ── calls (functions + collection system-functions) ─────────────────────────

    private IExecValue ExecuteCall(CodeCall codeCall, ExecScope scope, ExecContext context)
    {
        // Built-ins live under the `sys` namespace (sys.field / sys.humanize / …): a call
        // whose callee is a member access on the `sys` root routes the member name through
        // the builtin switch, with call.Params unchanged. Recognized SYNTACTICALLY (the left
        // is the `sys` symbol) — `sys` is a real object value, but it carries no builtin
        // props, so this never evaluates `sys.field` as an object-prop access. Builtins are
        // call-position only (not passable bare values); first-class builtins stay deferred.
        if (IsSysBuiltin(codeCall.Fn, out var name) && ExecuteBuiltin(name, codeCall, scope, context) is { } builtinResult)
            return builtinResult;

        var callee = ExecuteValue(codeCall.Fn, scope, context);
        return callee switch
        {
            ExecFunction fn       => CallFunction(fn, codeCall.Params, scope, context),
            ExecSysFunction sysFn => CallSysFunction(sysFn, codeCall.Params, scope, context),
            _ => throw new CodeRuntimeException("Target of a call is not a function."),
        };
    }

    // A callee of the form `sys.<name>` (a member access on the bare `sys` symbol). The
    // generic-UI library and any image Code reach the builtins only through this namespace.
    private static bool IsSysBuiltin(ICodeValue fn, out string name)
    {
        if (fn is CodeInfixOp { Op: CodeInfixOpType.ObjectProp, Left: CodeSymbol { Name: "sys" }, Right: CodeSymbol member })
        {
            name = member.Name;
            return true;
        }
        name = "";
        return false;
    }

    // Dispatch a `sys.<name>(...)` builtin. Returns null for an unknown member, so the caller
    // falls through to ordinary callee resolution (which will then fail to read the missing
    // prop on `sys` — the same "unknown field" error a typo would give).
    private IExecValue? ExecuteBuiltin(string name, CodeCall call, ExecScope scope, ExecContext context) => name switch
    {
        "field" => ExecuteField(call, scope, context),
        "humanize" => ExecuteHumanize(call, scope, context),
        "extent" => ExecuteExtent(call, scope, context),
        "nest" => ExecuteNest(call, scope, context),
        "clone" => ExecuteClone(call, scope, context),
        // setRef(obj, prop, value) persists on the client (the reference editor). Server-side
        // (SSR / refetch) never runs the click handler, so it no-ops.
        "setRef" => new ExecNothing(),
        // publish(targetId) is a SERVER-ONLY host action (the sys.publish channel). It runs only
        // when the client fires the event hook → the `hostAction` WS op; the SSR/refetch renderer
        // never publishes, so here it no-ops (exactly like setRef). No conformance case: a host
        // effect returns nothing and is outside the conformance contract.
        "publish" => new ExecNothing(),
        _ => null,
    };

    // Invoke a function with already-evaluated arguments in a child of `scope`.
    // Used by the SSR renderer to call the render fn (no args) over a prepared
    // top scope (db + ui vars + functions).
    public IExecValue InvokeFunction(CodeFunction fn, IReadOnlyList<IExecValue> args, ExecScope scope, ExecContext context)
    {
        var callScope = new ExecScope { Parent = scope };
        for (var i = 0; i < args.Count && i < fn.Params.Length; i++)
            callScope.Items[fn.Params[i].Name] = new ExecScopeItem { Value = args[i], IsReadOnly = true };
        return ExecuteBlock(fn.Body, callScope, context) ?? new ExecNothing();
    }

    private IExecValue CallFunction(ExecFunction fn, ICodeValue[] args, ExecScope scope, ExecContext context)
    {
        // Args evaluate in the caller's context (their deps are the caller's); the body
        // is memoized by (function id, arg identities) with its own captured deps.
        var argVals = new IExecValue[args.Length];
        for (var i = 0; i < args.Length; i++) argVals[i] = ExecuteValue(args[i], scope, context);

        return Memoize(MemoKey($"fn:{fn.Function.Id}", argVals), context, () =>
        {
            var callScope = new ExecScope { Parent = fn.Scope };
            // Bind min(args, params): extra args are ignored, missing params stay
            // unbound (a later read fails with "Variable not found"). The validator
            // catches static arity mismatches on named functions.
            for (var i = 0; i < argVals.Length && i < fn.Function.Params.Length; i++)
                callScope.Items[fn.Function.Params[i].Name] = new ExecScopeItem { Value = argVals[i], IsReadOnly = true };
            return ExecuteBlock(fn.Function.Body, callScope, context) ?? new ExecNothing();
        });
    }

    // Run `compute` as a memoized computation: capture its dependencies in a fresh
    // Deps, store the (key → result, deps) entry, and fold its deps into the caller's
    // (a caller transitively depends on what its callees read).
    public static IExecValue Memoize(string key, ExecContext context, Func<IExecValue> compute)
    {
        var deps = new Deps();
        var leaves = new LeafFrame();
        var lastIdBefore = context.LastId.Value;
        context.DepStack.Push(deps);
        context.LeafStack.Push(leaves);
        IExecValue result;
        try { result = compute(); }
        finally { context.DepStack.Pop(); context.LeafStack.Pop(); }

        // An identity-creating computation — its result is a transient OBJECT minted
        // inside it (`getNewUser()`-style factory) — is not pure: caching it would hand
        // every caller the same mutable instance. Never cache those. (A derived ARRAY
        // — a where/orderBy result — is also freshly minted but pure in content, and
        // stays cacheable.)
        if (!(result is ExecObject { Id: < 0 } o && o.Id < lastIdBefore))
            context.Memo[key] = new CacheEntry { Key = key, Result = result, Deps = deps };
        if (context.DepStack.Count > 0) context.DepStack.Peek().Merge(deps);

        // A tag-valued result IS display: its result cannot ship (the client re-renders
        // it), so everything it read becomes a displayed leaf. A value result ships, so
        // its reads stay private dependencies and the pending leaves are dropped.
        if (ContainsTags(result)) PromoteLeaves(context, leaves);
        return result;
    }

    private static bool ContainsTags(IExecValue result) => result switch
    {
        ExecTag => true,
        ExecArray a => a.Items.Any(i => ContainsTags(i.Value)), // nested arrays of tags too
        _ => false,
    };

    private static void PromoteLeaves(ExecContext context, LeafFrame leaves)
    {
        if (context.LeafStack.Count > 0)
        {
            // Still inside an outer computation: bubble up (promoted iff IT is tags too).
            var parent = context.LeafStack.Peek();
            parent.Props.UnionWith(leaves.Props);
            parent.Items.UnionWith(leaves.Items);
            return;
        }
        context.AccessedObjectProps.UnionWith(leaves.Props);
        context.AccessedItems.UnionWith(leaves.Items);
    }

    private static string MemoKey(string callee, IReadOnlyList<IExecValue> args) =>
        args.Count == 0 ? callee : callee + "|" + string.Join(",", args.Select(ArgKey));

    private static string ArgKey(IExecValue v) => v switch
    {
        ExecObject o => "o" + o.Id,
        ExecArray a  => "a" + a.Id,
        ExecInt i         => "i" + i.Value,
        ExecBool b   => "b" + (b.Value ? 1 : 0),
        ExecText t   => "t" + t.Value.Length + ":" + t.Value, // length-prefixed: delimiter-safe
        ExecNull     => "n",
        _            => "?",
    };

    // Invoke a lambda with one already-evaluated argument (for where/orderBy).
    private IExecValue InvokeLambda(ExecFunction fn, IExecValue arg, ExecContext context)
    {
        var callScope = new ExecScope { Parent = fn.Scope };
        if (fn.Function.Params.Length > 0)
            callScope.Items[fn.Function.Params[0].Name] = new ExecScopeItem { Value = arg, IsReadOnly = true };
        return ExecuteBlock(fn.Function.Body, callScope, context) ?? new ExecNothing();
    }

    private IExecValue CallSysFunction(ExecSysFunction sysFn, ICodeValue[] args, ExecScope scope, ExecContext context)
    {
        switch (sysFn.Method)
        {
            case "add":
                AddToCollection(sysFn.Target, ExecuteValue(args[0], scope, context));
                return new ExecNothing();
            case "remove":
                RemoveFromCollection(sysFn.Target, ExecuteValue(args[0], scope, context));
                return new ExecNothing();
            // setEntry(key, value): a dictionary create/replace. Dict entries persist through
            // the PATH-addressed addEntry WS op (handled on the client); server-side (SSR /
            // refetch never runs a click handler) it no-ops, like setRef.
            case "setEntry":
                return new ExecNothing();
            case "where":
            {
                var lambda = AsLambda(args[0], scope, context);
                return Memoize($"where:a{sysFn.Target.Id}:fn{lambda.Function.Id}", context,
                    () => Where(sysFn.Target, lambda, context));
            }
            case "orderBy":
            {
                var lambda = AsLambda(args[0], scope, context);
                return Memoize($"orderBy:a{sysFn.Target.Id}:fn{lambda.Function.Id}", context,
                    () => OrderBy(sysFn.Target, lambda, context));
            }
            case "any":
            {
                var lambda = AsLambda(args[0], scope, context);
                RecordMembership(sysFn.Target, context);
                return new ExecBool
                {
                    Value = sysFn.Target.Items.Any(
                        item => InvokeLambda(lambda, item.Value, context) is ExecBool { Value: true }),
                };
            }
            default:
                throw new CodeRuntimeException($"Unknown collection method '{sysFn.Method}'.");
        }
    }

    private ExecFunction AsLambda(ICodeValue arg, ExecScope scope, ExecContext context) =>
        ExecuteValue(arg, scope, context) as ExecFunction
        ?? throw new CodeRuntimeException("Expected a lambda argument.");

    private void AddToCollection(ExecArray coll, IExecValue value)
    {
        // Persist to a db set (addressed by its intrinsic id); a set member is keyed
        // by its own object identity. A transient object (negative id) is minted into
        // the extent first — its id flips positive, so it is now "in db" by definition.
        if (coll is { Kind: ArrayKind.Set, ElementTypeName: { } elemType } && _store != null && value is ExecObject obj)
        {
            if (obj.Id < 0)
            {
                obj.Id = _store.CreateObject(elemType, DbBridge.ToObjectValue(obj));
                obj.TypeName = elemType;
            }
            _store.AddToSet(coll.Id, obj.Id);
            coll.Items.Add(new ExecItem { Key = obj.Id, Value = obj });
        }
        else
        {
            coll.Items.Add(new ExecItem { Key = NextItemId(coll), Value = value });
        }
    }

    private void RemoveFromCollection(ExecArray coll, IExecValue value)
    {
        var item = coll.Items.FirstOrDefault(i => ReferenceEquals(i.Value, value)
            || (i.Value is ExecObject a && value is ExecObject b && a.Id == b.Id));
        if (item != null) coll.Items.Remove(item);

        if (coll.Kind == ArrayKind.Set && _store != null && value is ExecObject obj && obj.Id > 0)
            _store.RemoveFromSet(coll.Id, obj.Id);
    }

    private ExecArray Where(ExecArray coll, ExecFunction predicate, ExecContext context)
    {
        RecordMembership(coll, context);
        var items = coll.Items
            .Where(item => InvokeLambda(predicate, item.Value, context) is ExecBool { Value: true })
            .ToList();
        return new ExecArray { Items = items, Id = --context.LastId.Value, Kind = ArrayKind.List };
    }

    private ExecArray OrderBy(ExecArray coll, ExecFunction keySelector, ExecContext context)
    {
        RecordMembership(coll, context);
        var items = coll.Items
            .Select(item => (item, key: InvokeLambda(keySelector, item.Value, context)))
            .OrderBy(p => p.key, ExecValueComparer.Instance)
            .Select(p => p.item)
            .ToList();
        return new ExecArray { Items = items, Id = --context.LastId.Value, Kind = ArrayKind.List };
    }

    private static int NextItemId(ExecArray coll) =>
        coll.Items.Count == 0 ? 1 : coll.Items.Max(i => i.Key) + 1;

    // A where/orderBy observes the source collection's membership: an add/remove to it
    // can change the result, so it is a dependency of the surrounding computation.
    private static void RecordMembership(ExecArray coll, ExecContext context)
    {
        if (context.DepStack.Count > 0) context.DepStack.Peek().Members.Add(new MemberDep(coll.Id));
    }

    // ── tags ──────────────────────────────────────────────────────────────────────

    public ExecTag ExecuteTag(CodeTag codeTag, ExecScope scope, ExecContext context)
    {
        var attrs = codeTag.Attributes.ToDictionary(p => p.Name, p => ExecuteValue(p.Value, scope, context));
        var children = ExecuteTagChildren(codeTag.Children, scope, context);
        return new ExecTag { Name = codeTag.Name, Attributes = attrs, Children = children };
    }

    private IExecTagChild[] ExecuteTagChild(ICodeTagChild child, ExecScope scope, ExecContext context) => child switch
    {
        CodeTag codeTag               => [ExecuteTag(codeTag, scope, context)],
        CodeTagForEach codeForEach    => ExecuteTagForEach(codeForEach, scope, context),
        CodeTagIf codeTagIf           => ExecuteTagIf(codeTagIf, scope, context),
        ICodeValue codeValue          => [ExecuteValue(codeValue, scope, context)],
        _ => throw new NotImplementedException($"Tag child {child.GetType().Name}"),
    };

    private IExecTagChild[] ExecuteTagChildren(ICodeTagChild[] body, ExecScope scope, ExecContext context)
    {
        var children = new List<IExecTagChild>();
        var innerScope = new ExecScope { Parent = scope };
        foreach (var child in body)
            children.AddRange(ExecuteTagChild(child, innerScope, context));
        return [.. children];
    }

    private IExecTagChild[] ExecuteTagIf(CodeTagIf codeTagIf, ExecScope scope, ExecContext context)
    {
        var condition = ExecuteValue(codeTagIf.Condition, scope, context) as ExecBool
            ?? throw new CodeRuntimeException("Result of if condition is not boolean.");
        var code = condition.Value ? codeTagIf.Body : codeTagIf.ElseBody;
        return ExecuteTagChildren(code, scope, context);
    }

    private IExecTagChild[] ExecuteTagForEach(CodeTagForEach codeForEach, ExecScope scope, ExecContext context)
    {
        var collection = ExecuteValue(codeForEach.Collection, scope, context) as ExecArray
            ?? throw new CodeRuntimeException("foreach target is not a collection.");
        // Inside a computation, iterating observes membership (an add/remove changes
        // the output) and each item is a pending leaf of the surrounding tag fn.
        RecordMembership(collection, context);
        var children = new List<IExecTagChild>();
        foreach (var item in collection.Items)
        {
            if (context.DepStack.Count == 0) context.AccessedItems.Add((collection, item));
            else context.LeafStack.Peek().Items.Add((collection, item));
            OnValueAccessed(context, item.Value);
            var itemScope = new ExecScope { Parent = scope };
            itemScope.Items[codeForEach.Item.Name] = new ExecScopeItem { Value = item.Value, IsReadOnly = true };
            children.AddRange(ExecuteTagChildren(codeForEach.Body, itemScope, context));
        }
        return [.. children];
    }

    // ── scope lookup ────────────────────────────────────────────────────────────

    private static ExecScope FindScope(string name, ExecScope scope)
    {
        if (scope.Items.ContainsKey(name)) return scope;
        if (scope.Parent != null) return FindScope(name, scope.Parent);
        throw new CodeRuntimeException($"Variable '{name}' not found.");
    }
}

// Orders scalar exec values (int / text / bool) for orderBy.
public sealed class ExecValueComparer : IComparer<IExecValue>
{
    public static readonly ExecValueComparer Instance = new();

    public int Compare(IExecValue? x, IExecValue? y) => (x, y) switch
    {
        (ExecInt a, ExecInt b)   => a.Value.CompareTo(b.Value),
        (ExecText a, ExecText b) => string.Compare(a.Value, b.Value, StringComparison.Ordinal),
        (ExecBool a, ExecBool b) => a.Value.CompareTo(b.Value),
        _ => throw new CodeRuntimeException("orderBy key is not a comparable scalar."),
    };
}

// A runtime error in user code (bad type, unknown field, etc.). Surfaced to the
// client as an error reply / error state; never a server crash.
public sealed class CodeRuntimeException(string message) : Exception(message);
