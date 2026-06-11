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
    private static readonly HashSet<string> CollectionMethods = ["add", "remove", "where", "orderBy"];

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

    private void ExecuteAssignment(CodeAssignment assignment, ExecScope scope, ExecContext context)
    {
        var itemScope = FindScope(assignment.Target.Name, scope);
        var item = itemScope.Items[assignment.Target.Name];
        if (item.IsReadOnly)
            throw new CodeRuntimeException($"Symbol '{assignment.Target.Name}' is read only.");
        item.Value = ExecuteValue(assignment.Value, scope, context);
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

    private IExecValue ExecuteAssignmentValue(CodeAssignment assignment, ExecScope scope, ExecContext context)
    {
        ExecuteAssignment(assignment, scope, context);
        return FindScope(assignment.Target.Name, scope).Items[assignment.Target.Name].Value;
    }

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
        if (itemScope.Parent == null)
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

            // DepStack empty → output position: a displayed leaf. Non-empty → inside a
            // computation: a dependency, plus a pending leaf (shipped only if the
            // computation's result is a tag tree — i.e. it IS display).
            if (context.DepStack.Count == 0) context.AccessedObjectProps.Add((obj, member.Name));
            else
            {
                context.DepStack.Peek().Props.Add(new PropDep(obj.Id, member.Name));
                context.LeafStack.Peek().Props.Add((obj, member.Name));
            }
            OnValueAccessed(context, value);
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

    private static int AsInt(IExecValue v) => v is ExecInt i ? i.Value
        : throw new CodeRuntimeException("Expected an int.");
    private static bool AsBool(IExecValue v) => v is ExecBool b ? b.Value
        : throw new CodeRuntimeException("Expected a bool.");

    // ── calls (functions + collection system-functions) ─────────────────────────

    private IExecValue ExecuteCall(CodeCall codeCall, ExecScope scope, ExecContext context)
    {
        var callee = ExecuteValue(codeCall.Fn, scope, context);
        return callee switch
        {
            ExecFunction fn       => CallFunction(fn, codeCall.Params, scope, context),
            ExecSysFunction sysFn => CallSysFunction(sysFn, codeCall.Params, scope, context),
            _ => throw new CodeRuntimeException("Target of a call is not a function."),
        };
    }

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
            for (var i = 0; i < argVals.Length; i++)
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

    private static bool ContainsTags(IExecValue result) =>
        result is ExecTag || (result is ExecArray a && a.Items.Any(i => i.Value is ExecTag));

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
