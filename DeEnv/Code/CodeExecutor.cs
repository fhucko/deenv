using DeEnv.Instance;
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

    // typeName → the type's descriptor literal (a pure data CodeObject — { name, labelProp,
    // props }), built by GenericUi from the schema and threaded in the same way as
    // _store. `sys.schema(typeName)` evaluates the matching literal under the memo cache, so
    // the self-hosted UI reads a type's shape WITHOUT a standing `__descs` global. Empty for a
    // custom-render app (which uses no descriptors).
    private readonly IReadOnlyDictionary<string, CodeObject> _descriptors;

    // The schema-driven URL→type resolver, threaded in like _store/_descriptors so the
    // `sys.resolve(path)` builtin can do the cardinality-walk that decides a URL's view kind. This
    // walk USED to live in C# (the now-deleted SsrRenderer.ResolveView per-URL dispatch); the
    // generic-UI collapse moved it into Code — `sys.resolve` is the sole source of truth, called by
    // the framework-synthesized generic `fn render()`. Null for a bare executor (conformance) —
    // `sys.resolve` then throws, as it needs the schema. The client twin (codeExec.ts) has no
    // resolver/schema, so it ports the walk over the SHIPPED descriptors instead — proven identical
    // by the SelfHostedUi SSR+hydrate "resolve probe" scenarios.
    private readonly TypeResolver? _resolver;

    // The access READ floor (M-auth), threaded in by the renderer so the extent listing is gated. The
    // renderer builds ONE floor over the bound principal + the ruleset and passes it BOTH to
    // DbBridge.LoadRoot (which gates the graph) AND here, so `sys.extent(type)` reads the SAME floor: a
    // read-denied row never enters the candidate list a reference picker (or a custom render's own
    // listing) sees. Null for a bare executor (a condition evaluator, conformance, the client twin has
    // no floor) ⇒ no extent gating, exactly as a dormant app. See ExecuteExtent.
    private readonly AccessFloor? _floor;

    public CodeExecutor(IInstanceStore? store = null, IReadOnlyDictionary<string, CodeObject>? descriptors = null,
        TypeResolver? resolver = null, AccessFloor? floor = null)
    {
        _store = store;
        _descriptors = descriptors ?? new Dictionary<string, CodeObject>();
        _resolver = resolver;
        _floor = floor;
    }

    // ── statements ──────────────────────────────────────────────────────────────

    public IExecValue? ExecuteStatement(ICodeStatement statement, ExecScope scope, ExecContext context)
    {
        switch (statement)
        {
            case CodeAssignment assignment: ExecuteAssignment(assignment, scope, context); return null;
            case CodeBlock block:           return ExecuteBlock(block, scope, context);
            case CodeVarDec varDec:         ExecuteVarDec(varDec, scope, context); return null;
            case CodeFunction function:     ExecuteFunction(function, scope, context); return null;
            case CodeReturn ret:            return ExecuteValue(ret.Value, scope, context);
            case CodeCall call:             ExecuteCall(call, scope, context); return null;
            case CodeIf codeIf:             return ExecuteIf(codeIf, scope, context);
            case CodeAmbient ambient:       ExecuteAmbient(ambient, scope, context); return null;
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

    private static ExecFunction ExecuteFunction(CodeFunction function, ExecScope scope, ExecContext context)
    {
        var fn = new ExecFunction { Function = function, Scope = scope, CapturedAmbient = context.Ambient };
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

    // `ambient name = value` — push a dynamic-scope binding; the enclosing block pops it on exit.
    private void ExecuteAmbient(CodeAmbient ambient, ExecScope scope, ExecContext context) =>
        context.Ambient = new AmbientFrame(ambient.Name, ExecuteValue(ambient.Value, scope, context), context.Ambient);

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
                // READ-ONLY harvest (client data layer, slice 4): the write stages into the throwaway overlay,
                // never the loaded graph — so the prop ships at its STORE value (the harvest reads the data the
                // handler READ, not what it wrote) while the handler still reads its own write back below
                // (RecordPropAccess/the overlay-first read). Discarded with the context. Checked FIRST so a
                // read-only invoke never mutates in place regardless of the ambient ctx.
                if (context.ReadOnly)
                {
                    if (!context.ReadOnlyOverlay.TryGetValue(obj, out var ov)) context.ReadOnlyOverlay[obj] = ov = [];
                    ov[member.Name] = value;
                }
                // In a staging context the write stages — the live object is untouched until commit.
                // Gated to persisted (positive-id) objects: a transient draft (sys.new, id<0) writes
                // live, so a create-form's draft is not entangled in the surrounding edit transaction.
                // (id>0 is today's proxy for "real identity in the live store". A just-added object
                // still awaiting its negative→real remap is also id<0 but has no route, so nothing
                // renders it in a staging form — revisit this gate if that ever changes.)
                else if (obj.Id > 0 && NearestStagingCtx(context) is { } staging)
                {
                    if (!staging.Staged.TryGetValue(obj, out var fields)) staging.Staged[obj] = fields = [];
                    fields[member.Name] = value;
                }
                else
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
        var savedAmbient = context.Ambient;   // ambient provides in this block pop on exit
        try
        {
            foreach (var statement in block.Statements)
            {
                var value = ExecuteStatement(statement, innerScope, context);
                if (value != null) return value;
            }
            return null;
        }
        finally { context.Ambient = savedAmbient; }
    }

    // ── values ────────────────────────────────────────────────────────────────────

    public IExecValue ExecuteValue(ICodeValue value, ExecScope scope, ExecContext context) => value switch
    {
        CodeInt codeInt        => new ExecInt { Value = codeInt.Value },
        CodeFunction codeFn    => ExecuteFunction(codeFn, scope, context),
        CodeCall codeCall      => ExecuteCall(codeCall, scope, context),
        // A tag in VALUE position whose name resolves to a function is a component (a root/returned
        // component) — run it slot-keyed and yield its view value; otherwise it's an HTML element.
        CodeTag codeTag        => TryResolveComponent(codeTag.Name, scope) is { } component
                                      ? ExecuteComponentValue(codeTag, component, scope, context)
                                      : ExecuteTag(codeTag, scope, context),
        CodeText codeText      => new ExecText { Value = codeText.Value },
        CodeBool codeBool      => new ExecBool { Value = codeBool.Value },
        CodeInfixOp codeInfix  => ExecuteInfixOp(codeInfix, scope, context),
        CodeNot codeNot        => ExecuteNot(codeNot, scope, context),
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
        var itemScope = TryFindScope(codeSymbol.Name, scope);
        if (itemScope == null) return ResolveAmbient(codeSymbol.Name, context);  // dynamic-scope fallback
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

    private static ExecScope? TryFindScope(string name, ExecScope scope)
    {
        for (ExecScope? s = scope; s != null; s = s.Parent)
            if (s.Items.ContainsKey(name)) return s;
        return null;
    }

    // Dynamic-scope resolution up the ambient frame chain — the fallback when a symbol is not
    // in any lexical scope. Throws the same not-found error a lexical miss used to.
    private static IExecValue ResolveAmbient(string name, ExecContext context)
    {
        for (var f = context.Ambient; f != null; f = f.Parent)
            if (f.Name == name) return f.Value;
        throw new CodeRuntimeException($"Variable '{name}' not found.");
    }

    // ── data context (the ambient `ctx` overlay) ─────────────────────────────────
    // ponytail: resolves ambient `ctx` per prop access (linear walk); cache on the context if it matters.

    // The nearest ambient data context (well-known name `ctx`), or null when none is provided.
    private static ExecCtx? NearestCtx(ExecContext context)
    {
        for (var f = context.Ambient; f != null; f = f.Parent)
            if (f.Name == "ctx") return f.Value as ExecCtx;
        return null;
    }

    // The nearest STAGING context — a sub-context; the live root (and "no context") stages nothing.
    private static ExecCtx? NearestStagingCtx(ExecContext context) =>
        NearestCtx(context) is { Live: false } c ? c : null;

    // The staged value for (object, field) anywhere up the active context chain, or null.
    private static IExecValue? NearestStagedValue(ExecObject obj, string prop, ExecContext context)
    {
        for (var c = NearestCtx(context); c != null; c = c.Parent)
            if (c.Staged.TryGetValue(obj, out var fields) && fields.TryGetValue(prop, out var v))
                return v;
        return null;
    }

    // `ctx.new()` (a staging child), `ctx.discard()` (drop staged), `ctx.commit()` (flush staged to
    // the parent context, or to the live object when the parent is the live root).
    private static IExecValue CallCtxMethod(ExecCtxMethod m, IExecValue[] args)
    {
        switch (m.Method)
        {
            // ctx.new(autosave): autosave=true → the live parent (writes persist); else a staging child.
            case "new": return args.Length > 0 && args[0] is ExecBool { Value: true } ? m.Ctx : new ExecCtx { Parent = m.Ctx, Live = false };
            case "discard": m.Ctx.Staged.Clear(); return new ExecNothing();
            case "commit":
                foreach (var (obj, fields) in m.Ctx.Staged)
                    foreach (var (prop, val) in fields)
                        if (m.Ctx.Parent is { Live: false } p)
                        {
                            if (!p.Staged.TryGetValue(obj, out var pf)) p.Staged[obj] = pf = [];
                            pf[prop] = val;
                        }
                        else
                            obj.Props[prop] = val;   // committed to the live object (the client also persists)
                m.Ctx.Staged.Clear();
                return new ExecNothing();
            default: throw new CodeRuntimeException($"Unknown context method '{m.Method}'.");
        }
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

            // A data context: `ctx.dirty` (a bool) or a bound method (`ctx.new`/`commit`/`discard`).
            if (target is ExecCtx ctx)
                return member.Name == "dirty"
                    ? new ExecBool { Value = ctx.Staged.Count > 0 }
                    : new ExecCtxMethod { Ctx = ctx, Method = member.Name };

            // Property access on null/nothing FAILS CLOSED: it yields null, never a throw. This is the
            // M-auth obligation that makes a currentUser-dependent access condition DENY (not error) for
            // an anonymous request — `currentUser.role == "Admin"` with `currentUser == null` reads
            // `null.role` → null, and `null == "Admin"` is false. Null PROPAGATES (a chain like `a.b.c`
            // over a null `a` collapses to null), kept in lockstep with the TS twin (codeExec.ts). A
            // missing field on a REAL object is still an error below (a genuine bug, not the null case).
            if (target is ExecNull or ExecNothing)
                return new ExecNull();

            if (target is not ExecObject obj)
                throw new CodeRuntimeException($"Cannot read '{member.Name}' on a non-object.");

            // READ-ONLY harvest overlay read (client data layer, slice 4): a value the handler itself wrote
            // this run wins, so it reads its own writes back (branch-correctness) — but the read is NOT
            // recorded as an accessed prop (the value came from the handler, not the store; recording it would
            // ship a fabricated leaf). A prop the handler did NOT write falls through to the store value below
            // and IS recorded — that is the real demanded data.
            if (context.ReadOnly && context.ReadOnlyOverlay.TryGetValue(obj, out var rov)
                && rov.TryGetValue(member.Name, out var rovValue))
                return rovValue;

            // Overlay read: in a staging context the staged value for this (object, field) wins.
            if (NearestStagedValue(obj, member.Name, context) is { } stagedValue)
            {
                RecordPropAccess(obj, member.Name, stagedValue, context);
                return stagedValue;
            }
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
        if (NearestStagedValue(obj, name.Value, context) is { } staged)
        {
            RecordPropAccess(obj, name.Value, staged, context);
            return staged;
        }
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
    //
    // The read floor (M-auth) gates the listing the SAME way it gates the graph: a row the principal
    // may not READ is omitted, so it never becomes a pick candidate (nor leaks via a custom render's
    // own `sys.extent(...)`). The floor is passed into LoadExtent; null (a bare executor / dormant app)
    // keeps every row. Gating happens inside the Memoize computation, so the SHIPPED `extent:*` entry
    // already excludes denied rows (the client receives only what it may see).
    private IExecValue ExecuteExtent(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 1)
            throw new CodeRuntimeException("extent(typeName) takes one argument.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecText typeName)
            throw new CodeRuntimeException("extent() expects a text type name.");
        if (_store == null)
            throw new CodeRuntimeException("extent() requires a store.");
        return Memoize($"extent:{typeName.Value}", context,
            () => DbBridge.LoadExtent(_store, typeName.Value, context, _floor));
    }

    // canWrite(typeName, verb): the bound principal's WRITE capability for a type + verb (create/edit/delete)
    // — SERVER-RESOLVED from the access floor (the floor never crosses the wire) and shipped as a cached
    // bool, exactly like extent/schema; the client reads the cache (a miss → refetch). The self-hosted UI
    // reads it to hide write affordances (Save/New/Remove) a read-only principal cannot use — the floor still
    // RE-decides every real write, so this only governs what the UI OFFERS, never what it ALLOWS.
    //
    // Evaluated over a throwaway EMPTY target object, so it is EXACT for principal-only conditions
    // (`currentUser.role == "Admin"`) and a PERMISSIVE over-approximation for target-referencing rules
    // (same caveat as SsrRenderer's canManageUsers). No floor (a non-UI executor) ⇒ allow-all, like a
    // dormant app. The result refreshes on login/logout via the full refetch (as extent does).
    private IExecValue ExecuteCanWrite(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 2)
            throw new CodeRuntimeException("canWrite(typeName, verb) takes two arguments.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecText typeName)
            throw new CodeRuntimeException("canWrite() expects a text type name.");
        if (ExecuteValue(call.Params[1], scope, context) is not ExecText verb)
            throw new CodeRuntimeException("canWrite() expects a text verb.");
        return Memoize($"canWrite:{typeName.Value}:{verb.Value}", context, () =>
        {
            if (_floor == null) return new ExecBool { Value = true }; // no enforcement ⇒ allow (dormant)
            var target = new ExecObject { Props = [], Id = 0, TypeName = typeName.Value };
            return new ExecBool { Value = _floor.CanWrite(verb.Value, typeName.Value, target) };
        });
    }

    // canRead(typeName): may the principal read ANY member of the type — a CONSERVATIVE, data-free capability
    // (AccessFloor.CanReadType), server-resolved + shipped like canWrite. The self-hosted UI reads it to HIDE
    // a collection/route whose element type the principal cannot read (an admin-only `users` set is hidden
    // from anonymous, /users 404s) without shipping the role. Errs toward READABLE (never hides a collection
    // with admissible members). No floor ⇒ readable (dormant).
    private IExecValue ExecuteCanRead(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 1)
            throw new CodeRuntimeException("canRead(typeName) takes one argument.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecText typeName)
            throw new CodeRuntimeException("canRead() expects a text type name.");
        return Memoize($"canRead:{typeName.Value}", context,
            () => new ExecBool { Value = _floor?.CanReadType(typeName.Value) ?? true });
    }

    // schema(typeName): a type's descriptor — { name, labelProp, props } — the reflective
    // shape the self-hosted generic UI walks (objectForm/refEditor/setTable). The replacement for
    // the old `__descs` registry global: the descriptor is computed from the schema (the literal
    // GenericUi threads in), keyed by type, and shipped so the client reuses it (like extent).
    //
    // The descriptor is pure, deterministic, immutable schema data with NO dependencies, so it is
    // cached DIRECTLY (an empty-deps CacheEntry) rather than through Memoize: Memoize's factory
    // guard refuses to cache a transient negative-id OBJECT minted inside the computation (it
    // assumes a `getNewX()`-style mutable factory), but a descriptor is a constant — caching it is
    // correct, and the cache entry is exactly what ships it to the client (DtValue ships a negative-id
    // object complete). A cache hit returns the SAME descriptor for the rest of the render (no rebuild).
    private IExecValue ExecuteSchema(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length is not (1 or 2))
            throw new CodeRuntimeException("schema(typeName[, propName]) takes one or two arguments.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecText typeName)
            throw new CodeRuntimeException("schema() expects a text type name.");
        // Two-arg form: a specific PROP's descriptor (a dict prop at its root route), keyed
        // "Type/prop" — the replacement for the old `__dictDescs` registry.
        var lookup = typeName.Value;
        if (call.Params.Length == 2)
        {
            if (ExecuteValue(call.Params[1], scope, context) is not ExecText propName)
                throw new CodeRuntimeException("schema() expects a text prop name.");
            lookup += "/" + propName.Value;
        }
        var key = $"schema:{lookup}";
        if (context.Memo.TryGetValue(key, out var cached)) return cached.Result;
        if (!_descriptors.TryGetValue(lookup, out var literal))
            throw new CodeRuntimeException($"No descriptor for '{lookup}'.");
        // Evaluate the descriptor literal in a FRESH EMPTY scope: it is a pure data literal (text /
        // bool / int / nested objects + arrays of the same), so it reads no variables — the empty
        // scope makes that structural (any stray symbol would fail loudly, not capture a binding).
        var descriptor = MarkConstant(ExecuteValue(literal, new ExecScope(), context));
        context.Memo[key] = new CacheEntry { Key = key, Result = descriptor, Deps = new Deps() };
        return descriptor;
    }

    // Mark a freshly-evaluated descriptor tree as Constant — recursively, through every nested
    // ExecObject and ExecArray. A descriptor is provably constant and user-data-free (built by
    // GenericUi.TypeDescriptor/PropDesc from schema metadata only, evaluated in a fresh empty scope
    // so it cannot reference `db`), so ClientState may ship the WHOLE tree (every prop + item) rather
    // than only the accessed parts. This is what fixes the empty-array bug (a descriptor's nested
    // `props`/`values`/`valueProps` must arrive full so a consumer that walks it sees every entry),
    // and — being descriptor-SPECIFIC rather than negative-id-based — it cannot ship a where/orderBy/
    // literal collection's full membership (those are never Constant). Returns its argument for chaining.
    private static IExecValue MarkConstant(IExecValue value)
    {
        switch (value)
        {
            case ExecObject o:
                o.Constant = true;
                foreach (var pv in o.Props.Values) MarkConstant(pv);
                break;
            case ExecArray a:
                a.Constant = true;
                foreach (var item in a.Items) MarkConstant(item.Value);
                break;
        }
        return value;
    }

    // Eagerly cache EVERY descriptor (type + prop) into the memo so they all ship on first paint.
    // sys.schema only ships a descriptor that RAN server-side; a component composing sys.schema(...)
    // over rows that don't exist yet (an empty set seeded with none) would otherwise hit a client
    // cache-miss when a row is added later. Descriptors are static, pure schema data (no deps, no
    // user data), so caching them all up front is cheap and removes that sharp edge. Called once per
    // render before the client state is serialized.
    public void PrewarmDescriptors(ExecContext context)
    {
        foreach (var (name, literal) in _descriptors)
        {
            var key = $"schema:{name}";
            if (!context.Memo.ContainsKey(key))
                context.Memo[key] = new CacheEntry { Key = key, Result = MarkConstant(ExecuteValue(literal, new ExecScope(), context)), Deps = new Deps() };
        }
    }

    private static readonly string[] CapabilityVerbs = ["create", "edit", "delete"];

    // Eagerly compute every TYPE's WRITE capabilities (create/edit/delete) into the memo, exactly like
    // PrewarmDescriptors does for schema descriptors — so a CLIENT navigation to a not-yet-server-rendered
    // view finds the sys.canWrite bool in the SHIPPED cache instead of MISSING. Without this, a freshly-
    // created object's ObjectForm reads sys.canWrite(type, "edit") that the prior set page never shipped (it
    // only shipped create/delete), so the miss throws "Value not available" → a refetch that disrupts the
    // transient-id navigation and the new object's form never renders. Cheap: types × 3 verbs, bools only.
    public void PrewarmCapabilities(ExecContext context)
    {
        if (_floor == null) return; // no enforcement ⇒ no gating ⇒ nothing to ship
        foreach (var name in _descriptors.Keys)
        {
            if (name.Contains('/')) continue; // skip "Type/prop" dict-prop descriptor keys — not types
            foreach (var verb in CapabilityVerbs)
            {
                var key = $"canWrite:{name}:{verb}";
                if (context.Memo.ContainsKey(key)) continue;
                var target = new ExecObject { Props = [], Id = 0, TypeName = name };
                context.Memo[key] = new CacheEntry
                {
                    Key = key,
                    Result = new ExecBool { Value = _floor.CanWrite(verb, name, target) },
                    Deps = new Deps(),
                };
            }
            var readKey = $"canRead:{name}";
            if (!context.Memo.ContainsKey(readKey))
                context.Memo[readKey] = new CacheEntry
                {
                    Key = readKey,
                    Result = new ExecBool { Value = _floor.CanReadType(name) },
                    Deps = new Deps(),
                };
        }
    }

    // new(desc): a FRESH object of a type, built REFLECTIVELY from its descriptor — each scalar prop
    // set to its baseType default (bool→false, int→0, every
    // other leaf/enum → ""). The constructor for the self-hosted UI's drafts: a create-new form's
    // blank state, and the seed of ObjectForm's edit draft. A fresh object every call (no shared template → no aliasing).
    //
    // Privacy-trivial by construction: it reads NO source object — only the (already-shipped)
    // descriptor — and emits constant defaults, so there is nothing private to leak and nothing to
    // ship. The descriptor is plain schema data the client already has (sys.schema), so the client's
    // setup re-run mints the same defaults.
    //
    // Two descriptor shapes, the two the self-hosted UI passes:
    //   • a TYPE descriptor { name, labelProp, props, … } → one field per SCALAR entry of `props`
    //     (object/set/dictionary props are collections, never in a draft).
    //   • a dictionary PROP descriptor { baseType:"dictionary", isScalar, element, valueProps, … } →
    //     a scalar dict gets a single `value` (defaulted by `element`); an object dict gets one field
    //     per `valueProps` entry. (Mirrors the draft shape the old `blank` seeded for each.)
    private IExecValue ExecuteNew(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 1)
            throw new CodeRuntimeException("new(desc) takes one argument.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecObject desc)
            throw new CodeRuntimeException("new() expects a descriptor object.");
        var props = new Dictionary<string, IExecValue>();
        if (desc.Props.TryGetValue("baseType", out var bt) && bt is ExecText { Value: "dictionary" })
        {
            if (desc.Props.TryGetValue("isScalar", out var sc) && sc is ExecBool { Value: true })
                props["value"] = DefaultExec((desc.Props.GetValueOrDefault("element") as ExecText)?.Value ?? "");
            else
                foreach (var vp in DescriptorProps(desc, "valueProps"))
                    props[PropName(vp)] = DefaultExec(PropBaseType(vp));
        }
        else
            foreach (var p in DescriptorProps(desc, "props"))
                props[PropName(p)] = DefaultProp(p);
        return new ExecObject { Props = props, Id = --context.LastId.Value };
    }

    // The scalar baseType default (kept in lockstep with
    // codeExec.ts's defaultValue). A leaf-only switch: enum values are text-shaped, so they default ""
    // by falling through, exactly as the old `blank` literal did.
    private static IExecValue DefaultExec(string baseType) => baseType switch
    {
        "bool" => new ExecBool { Value = false },
        "int" => new ExecInt { Value = 0 },
        _ => new ExecText { Value = "" },
    };

    // A prop's COMPLETE default value — mirrors DbBridge.LoadObject's per-cardinality shape (and the TS
    // twin defaultProp) so sys.new and a stored load yield the SAME shape: a scalar → its leaf default; a
    // single object (reference) → null (unset); a set/dict → an EMPTY array of the matching kind carrying
    // its element type. Id 0 marks a draft-local empty collection — exactly what the store yields for an
    // absent set/dict, and add/remove only persists to the server once Id > 0. A complete draft is why
    // the generic table's reference/set columns (`if field(m, prop) != null`) no longer throw on a member
    // freshly minted by the SetTable create-form's `set.add(sys.new(desc))`.
    private static IExecValue DefaultProp(ExecObject prop) => PropBaseType(prop) switch
    {
        "object" => new ExecNull(),
        "set" => new ExecArray { Items = [], Id = 0, Kind = ArrayKind.Set, ElementTypeName = PropElement(prop) },
        "dictionary" => new ExecArray { Items = [], Id = 0, Kind = ArrayKind.Dict, ElementTypeName = PropElement(prop) },
        var b => DefaultExec(b),
    };

    // A descriptor's prop list (`props` / `valueProps`) — a Code array of prop-descriptor objects.
    private static IEnumerable<ExecObject> DescriptorProps(ExecObject desc, string field) =>
        desc.Props.TryGetValue(field, out var v) && v is ExecArray arr
            ? arr.Items.Select(i => i.Value).OfType<ExecObject>()
            : [];

    private static string PropName(ExecObject prop) => (prop.Props.GetValueOrDefault("name") as ExecText)?.Value ?? "";
    private static string PropBaseType(ExecObject prop) => (prop.Props.GetValueOrDefault("baseType") as ExecText)?.Value ?? "";
    private static string PropElement(ExecObject prop) => (prop.Props.GetValueOrDefault("element") as ExecText)?.Value ?? "";

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

    // segment(path, n): the n-th "/"-delimited segment of `path` as text ("" if out of
    // range) — the URL-DESTRUCTURING twin of nest. RAW split on "/" indexed by `n` (the
    // leading slash yields an empty first segment: segment("/instances/5", 0) == ""). The
    // framework does the string work; Code gains no general string ops.
    private IExecValue ExecuteSegment(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 2)
            throw new CodeRuntimeException("segment(path, n) takes two arguments.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecText path)
            throw new CodeRuntimeException("segment() expects a text path.");
        var n = AsInt(ExecuteValue(call.Params[1], scope, context));
        var parts = path.Value.Split('/');
        return new ExecText { Value = n >= 0 && n < parts.Length ? parts[n] : "" };
    }

    // toInt(text): parse `text` to an int; 0 on empty or non-numeric (defensive, mirroring
    // existing input coercion). Strict (only an optional leading "-" then digits) so the
    // server/client twins agree exactly on every input — "5x" is non-numeric → 0 on both.
    private IExecValue ExecuteToInt(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (ExecuteValue(call.Params[0], scope, context) is not ExecText text)
            throw new CodeRuntimeException("toInt() expects a text value.");
        return new ExecInt { Value = int.TryParse(text.Value, out var n) ? n : 0 };
    }

    // id(obj): an object's intrinsic int identity — the read companion to nest (which already
    // stringifies obj.Id for a link). Pure: a route compares this to a parsed segment to find
    // the addressed object; unlike field() it records no prop dependency and never writes back.
    private IExecValue ExecuteId(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (ExecuteValue(call.Params[0], scope, context) is not ExecObject obj)
            throw new CodeRuntimeException("id() expects an object.");
        return new ExecInt { Value = obj.Id };
    }

    // resolve(pathText): the URL→view-kind dispatch, now in Code (it replaced the deleted C#
    // SsrRenderer.ResolveView). Resolves a URL to its view-KIND plus the bound object(s), as ONE
    // object:
    //   { kind, target, parent, prop, typeName, parentType }
    // kind ∈ object | set | ref | dict | leaf | notFound (the six view outcomes the synthesized
    // generic render switches on). target is the routed object (object page / scalar-dict-entry
    // leaf), else null; parent is the OWNER object for an owner-bound route (set/ref/dict), else
    // null; prop the owner-bound prop name; typeName the type whose descriptor to fetch (object→its
    // type, set→element, ref→target, else ""); parentType the owner type (the dict's
    // sys.schema(parentType, prop)), else "".
    //
    // The cardinality DECISION reuses the threaded TypeResolver (one server-side source of truth);
    // the object BINDING walks the SAME `db` graph bound in scope
    // (a FindTarget-style member/field walk). The client twin (codeExec.ts) has no schema, so it
    // ports the identical walk over the SHIPPED descriptors + its own db graph; both must produce
    // the same result, proven by the SelfHostedUi resolve-probe SSR+hydrate scenarios. Pure /
    // not memoized (a fresh resolution per render, like nest/segment/id).
    private IExecValue ExecuteResolve(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 1)
            throw new CodeRuntimeException("resolve(path) takes one argument.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecText pathText)
            throw new CodeRuntimeException("resolve() expects a text path.");
        if (_resolver == null)
            throw new CodeRuntimeException("resolve() requires a schema resolver.");

        // The db graph to bind against: the SAME read-only root the render reads as `db`, so the
        // bound objects share identity with everything else the render touches (leaves line up).
        var db = FindScope("db", scope).Items["db"].Value as ExecObject
            ?? throw new CodeRuntimeException("resolve() requires a db root object.");

        var path = ParseUrlPath(pathText.Value);
        var typeInfo = _resolver.ResolveType(path);

        // An unrouted URL (a bad segment, a leaf navigated into): the self-hosted NotFound outcome.
        if (typeInfo == null) return ResolveResult(context, "notFound");

        // A set route (object set): owner-bound — bind the OWNER, fetch the ELEMENT type's descriptor.
        if (typeInfo is { Cardinality: Cardinality.Set, Type.BaseType: BaseType.Object })
            return OwnerBound(context, db, path, "set", typeName: typeInfo.Type.Name);

        // A dictionary route: owner-bound — bind the OWNER; the dict reads sys.schema(parentType, prop),
        // so typeName is "" and parentType carries the owner's type.
        if (typeInfo is { Cardinality: Cardinality.Dictionary })
            return OwnerBound(context, db, path, "dict", parentType: OwnerType(path));

        // A SCALAR dictionary entry (a single non-object reached by traversing a dict): the shared
        // leaf editor, bound to the entry object.
        if (typeInfo is { Cardinality: Cardinality.Single, Type.BaseType: not BaseType.Object }
            && _resolver.TraversesDictionary(path))
            return ResolveResult(context, "leaf", target: FindTarget(db, path, context));

        if (typeInfo is not { Cardinality: Cardinality.Single, Type.BaseType: BaseType.Object })
            return ResolveResult(context, "notFound");

        // A single-reference route (the last field is a single object reference): owner-bound — bind
        // the OWNER (never the maybe-unset target), fetch the TARGET type's descriptor.
        if (typeInfo.IsReference)
            return OwnerBound(context, db, path, "ref", typeName: typeInfo.Type.Name);

        // An ordinary object page (the Db root, or a set/object-dict member): bind the routed object.
        return ResolveResult(context, "object",
            target: FindTarget(db, path, context), typeName: typeInfo.Type.Name);
    }

    // An owner-bound route (set / ref / dict): the parent is the object owning the final prop
    // (path minus its last segment), the prop is that last segment.
    private IExecValue OwnerBound(ExecContext context, ExecObject db, NodePath path, string kind,
        string typeName = "", string parentType = "")
    {
        var prop = path.Segments[^1];
        var parentPath = NodePath.FromSegments(path.Segments.Take(path.Segments.Count - 1));
        var parent = FindTarget(db, parentPath, context);
        return ResolveResult(context, kind, parent: parent, prop: prop, typeName: typeName, parentType: parentType);
    }

    // The owner type for an owner-bound route — the type at the path minus its last segment,
    // e.g. "Db" for /settings.
    private string OwnerType(NodePath path) =>
        _resolver!.ResolveType(NodePath.FromSegments(path.Segments.Take(path.Segments.Count - 1)))?.Type.Name ?? "";

    // Build the resolve result object. A negative transient id (a render-local literal); its scalar
    // fields ship complete (ClientState ships a negative-id object whole), and target/parent are the
    // already-bound graph objects (which ship via the FindTarget walk's recorded leaves).
    private ExecObject ResolveResult(ExecContext context, string kind,
        IExecValue? target = null, IExecValue? parent = null, string prop = "", string typeName = "", string parentType = "") =>
        new()
        {
            Id = --context.LastId.Value,
            Props = new Dictionary<string, IExecValue>
            {
                ["kind"] = new ExecText { Value = kind },
                ["target"] = target ?? new ExecNull(),
                ["parent"] = parent ?? new ExecNull(),
                ["prop"] = new ExecText { Value = prop },
                ["typeName"] = new ExecText { Value = typeName },
                ["parentType"] = new ExecText { Value = parentType },
            },
        };

    // The breadcrumb/title LABEL for the final segment of `urlPath`, reusing the resolve walk so the
    // chrome shows human-readable labels instead of schema-internal identifiers:
    //   • a MEMBER route (a set member / object-dict entry — kind=object on a non-root path) → the
    //     bound object's labelProp value (e.g. "/notes/4" → the note's title), so an opaque id reads
    //     as a label;
    //   • a SCALAR-DICT entry (kind=leaf — the user's own literal key) → the RAW segment verbatim
    //     (e.g. "/settings/ORD-001" → "ORD-001", NOT humanized "Ord 001"), so we never mangle the
    //     key the user typed;
    //   • anything else (a prop-name segment — a set/ref/dict ROUTE, or NotFound) → null, signalling
    //     the caller to HUMANIZE the raw segment (e.g. "/notes" → "Notes").
    // Falls back to null (humanize the raw segment) whenever the object or its label is missing, so a
    // deleted/unshipped node never blanks the trail. Reuses ExecuteResolve (the one URL→target source
    // of truth) over the SAME scope/context the render uses, and reads the label through the canonical
    // leaf-recording prop accessor (RecordPropAccess — the same path `sys.field`/`.member` use), so an
    // INTERMEDIATE path object's labelProp leaf actually SHIPS (FindTarget records a descended ancestor
    // only as (obj, null), which ClientState drops) — the client's syncBreadcrumbs then re-resolves the
    // identical label on a ≥3-deep route. The client twin is ui.ts's segmentLabel.
    public string? SegmentLabel(string urlPath, ExecScope scope, ExecContext context)
    {
        var call = new CodeCall
        {
            Fn = new CodeSymbol { Name = "resolve" },
            Params = [new CodeText { Value = urlPath }],
        };
        if (ExecuteResolve(call, scope, context) is not ExecObject r) return null;
        if (r.Props.GetValueOrDefault("kind") is not ExecText kind) return null;

        // A scalar-dict entry: the segment IS the user's literal key — show it verbatim, never humanized.
        if (kind.Value == "leaf")
            return urlPath.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segs ? segs[^1] : null;

        if (kind.Value != "object") return null;
        if (r.Props.GetValueOrDefault("target") is not ExecObject target) return null;
        if (r.Props.GetValueOrDefault("typeName") is not ExecText { Value: var typeName }) return null;

        // The type's labelProp, read off the descriptor literal (the same source sys.schema(...) and the
        // generic SetTable use). An empty labelProp (a type with no scalar) → no label.
        var labelProp = _descriptors.GetValueOrDefault(typeName)?.Props
            .FirstOrDefault(p => p.Name == "labelProp")?.Value is CodeText { Value: var lp } && lp.Length > 0 ? lp : null;
        if (labelProp == null) return null;
        if (!target.Props.TryGetValue(labelProp, out var labelValue)) return null;

        // Record the read as an accessed LEAF (the canonical accessor) so the ancestor object's label
        // prop ships to the client — exactly what makes the twin trails byte-identical on a deep route.
        RecordPropAccess(target, labelProp, labelValue, context);
        return labelValue is ExecText { Value: var label } && label.Length > 0 ? label : null;
    }

    // Walk URL segments through the loaded `db` graph to BIND the routed object — the twin of
    // SsrRenderer.FindTarget. A set member segment is the member's identity id; a dict entry segment
    // is its __key; a field segment is a prop. Each step records its read as an accessed leaf
    // (membership + the descended item + the bound object) so the GRAPH PATH ships — the client's own
    // resolve walk then re-binds the same nodes on hydrate. Null when anything is missing.
    private static IExecValue? FindTarget(ExecObject root, NodePath path, ExecContext context)
    {
        IExecValue current = root;
        context.AccessedObjectProps.Add((root, null));
        foreach (var segment in path.Segments)
        {
            if (current is ExecArray { Kind: ArrayKind.Dict } dict)
            {
                context.AccessedItems.Add((dict, null));
                var item = dict.Items.FirstOrDefault(i =>
                    (i.Value as ExecObject)?.Props.GetValueOrDefault(DbBridge.EntryKeyProp) is ExecText k
                    && k.Value == segment);
                if (item == null) return null;
                context.AccessedItems.Add((dict, item));
                current = item.Value;
            }
            else if (current is ExecArray arr && int.TryParse(segment, out var id))
            {
                context.AccessedItems.Add((arr, null));
                var item = arr.Items.FirstOrDefault(i => i.Key == id);
                if (item == null) return null;
                context.AccessedItems.Add((arr, item));
                current = item.Value;
            }
            else if (current is ExecObject obj && obj.Props.TryGetValue(segment, out var value))
            {
                context.AccessedObjectProps.Add((obj, segment));
                current = value;
                if (current is ExecObject co) context.AccessedObjectProps.Add((co, null));
            }
            else
            {
                return null;
            }
        }
        return current as ExecObject;
    }

    private static NodePath ParseUrlPath(string urlPath)
    {
        var segs = urlPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return NodePath.FromSegments(segs);
    }

    private static int AsInt(IExecValue v) => v is ExecInt i ? i.Value
        : throw new CodeRuntimeException("Expected an int.");
    private static bool AsBool(IExecValue v) => v is ExecBool b ? b.Value
        : throw new CodeRuntimeException("Expected a bool.");

    // Unary `!`: negate a bool operand. The operand's reads are tracked by ExecuteValue.
    private IExecValue ExecuteNot(CodeNot codeNot, ExecScope scope, ExecContext context) =>
        new ExecBool { Value = !AsBool(ExecuteValue(codeNot.Operand, scope, context)) };

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
            ExecCtxMethod ctxFn   => CallCtxMethod(ctxFn, [.. codeCall.Params.Select(p => ExecuteValue(p, scope, context))]),
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
        "schema" => ExecuteSchema(call, scope, context),
        "canWrite" => ExecuteCanWrite(call, scope, context),
        "canRead" => ExecuteCanRead(call, scope, context),
        "nest" => ExecuteNest(call, scope, context),
        "segment" => ExecuteSegment(call, scope, context),
        "toInt" => ExecuteToInt(call, scope, context),
        "id" => ExecuteId(call, scope, context),
        "new" => ExecuteNew(call, scope, context),
        "resolve" => ExecuteResolve(call, scope, context),
        // setRef(obj, prop, value) persists on the client (the reference editor). Server-side
        // (SSR / refetch) never runs the click handler, so it no-ops.
        "setRef" => new ExecNothing(),
        // publish(schema, targetId), create(schema, name), cloneInstance(sourceId), delete(targetId),
        // setDesign(schema, targetId) and rename(id, name) are SERVER-ONLY host
        // actions (the host-action channel). They run only when the client fires the event hook →
        // the `hostAction` WS op; the SSR/refetch renderer never runs them, so here they no-op
        // (exactly like setRef). No conformance case: a host effect returns nothing and is outside
        // the conformance contract — so create/clone dropping their port args (path addressing) is
        // a server-only-plumbing change, not a reactive-semantics one.
        "publish" => new ExecNothing(),
        "create" => new ExecNothing(),
        "cloneInstance" => new ExecNothing(),
        "delete" => new ExecNothing(),
        "setDesign" => new ExecNothing(),
        "rename" => new ExecNothing(),
        // login(name, password) is a CLIENT-only host effect (M-auth login UI): the client fires the
        // event hook → a `login` WS op (whose reply drives a refetch); the SSR/refetch renderer never
        // runs it, so here it no-ops like the host actions. A host effect returns NOTHING and is OUTSIDE
        // the conformance contract — no conformance case; the bind happens on the connection, not here.
        "login" => new ExecNothing(),
        // logout() — the MIRROR of login (M-auth login UI 1e-2): a CLIENT-only host effect that clears the
        // session's principal back to anonymous. The client fires the `logout` hook → a `logout` WS op
        // whose reply drives a refetch (the page swaps the root view back to the anonymous gate at the same
        // URL). Like login it no-ops in the SSR/refetch renderer — the unbind lives on the connection, not
        // here — and is OUTSIDE the conformance contract (no conformance case).
        "logout" => new ExecNothing(),
        // setPassword(user, newPassword) — a CLIENT-only host effect (M-auth user admin): the client fires
        // the `setPassword` hook → the gated setPassword WS op (write floor's User `edit`). Like login it
        // no-ops in the SSR/refetch renderer and is OUTSIDE the conformance contract (no conformance case).
        "setPassword" => new ExecNothing(),
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

    // Invoke an indexed onClick handler closure READ-ONLY to harvest its data footprint (client data layer,
    // slice 4 — the action-miss round-trip). After RenderState reproduced the render and indexed the
    // handlers, the server looks up the one the client clicked (by its (slot, fn-id) key) and runs it here:
    //   • at the TOP level (DepStack empty) so its reads record as displayed LEAVES — exactly what
    //     ClientState.Serialize ships — rather than as private deps of an enclosing computation; and
    //   • under ReadOnly, so its writes stage into a DISCARDABLE overlay (ReadOnlyOverlay), never the loaded
    //     graph — so a written prop still ships at its STORE value (the harvest demands the data the handler
    //     READ, not what it wrote) while the handler reads its own write back, and the one store-touching
    //     effect (a db set add/remove) is suppressed. So the invoke is EFFECT-FREE by construction, and its
    //     reads stay floor-gated (the graph was floor-loaded).
    // A missing key (the handler is not in this render — a stale/foreign intent) is a no-op: nothing is
    // harvested, the client re-renders over whatever shipped. The closure's captured ambient (its birthplace
    // `ctx`) is restored by InvokeClosure, exactly like the client invoking it. No-throw: a runtime error in
    // the handler is swallowed (the harvest is best-effort — whatever it read BEFORE the error still ships,
    // and the client's own re-invoke surfaces a genuine bug), so a planning invoke never fails the refetch.
    public void InvokeHandlerForHarvest(string handlerKey, ExecContext context)
    {
        if (context.HandlerIndex is not { } index || !index.TryGetValue(handlerKey, out var handler)) return;
        var wasReadOnly = context.ReadOnly;
        var wasBypass = context.MemoBypass;
        context.ReadOnly = true;
        // Bypass the memo cache for the WHOLE handler run — exactly as the client runs a handler under
        // memoBypass (ui.ts runWithMemoBypass). WITHOUT this, a fn the handler calls (e.g. `bump(c)`) would be
        // wrapped in Memoize, pushing a Deps frame; its reads would then record as private DEPENDENCIES (and
        // pending leaves dropped when the fn returns no tags), NOT as displayed LEAVES — so the harvest would
        // ship nothing. Bypassing keeps DepStack empty throughout, so every read records straight as a leaf
        // (ClientState ships it). This is the heart of "the view/action is the query": the handler's reads ARE
        // the demanded data.
        context.MemoBypass = true;
        try { InvokeClosure(handler, [], context); }
        catch (Exception ex) when (ex is CodeRuntimeException or InvalidOperationException)
        {
            // Best-effort harvest: the reads made before the error already recorded as leaves and ship; the
            // client's authoritative re-invoke (over the merged data) re-runs the handler and surfaces a real
            // bug there. A planning invoke must never break the refetch.
        }
        finally { context.ReadOnly = wasReadOnly; context.MemoBypass = wasBypass; }
    }

    // Like InvokeFunction but on an ExecFunction closure — restores its captured ambient (RunBody),
    // so a component's setup body and its returned render view resolve the ambient context that was
    // provided inside the component's body.
    private IExecValue InvokeClosure(ExecFunction fn, IReadOnlyList<IExecValue> args, ExecContext context)
    {
        var callScope = new ExecScope { Parent = fn.Scope };
        for (var i = 0; i < args.Count && i < fn.Function.Params.Length; i++)
            callScope.Items[fn.Function.Params[i].Name] = new ExecScopeItem { Value = args[i], IsReadOnly = true };
        return RunBody(fn, callScope, context);
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
            return RunBody(fn, callScope, context);
        });
    }

    // Run a function/closure body, restoring its captured ambient bindings first (null = a top-level
    // fn → flows down to the live ambient). Mirrors the block save/restore so the call site's ambient
    // returns afterward.
    private IExecValue RunBody(ExecFunction fn, ExecScope callScope, ExecContext context)
    {
        var savedAmbient = context.Ambient;
        if (fn.CapturedAmbient != null) context.Ambient = fn.CapturedAmbient;
        try { return ExecuteBlock(fn.Function.Body, callScope, context) ?? new ExecNothing(); }
        finally { context.Ambient = savedAmbient; }
    }

    // Run `compute` as a memoized computation: capture its dependencies in a fresh
    // Deps, store the (key → result, deps) entry, and fold its deps into the caller's
    // (a caller transitively depends on what its callees read).
    public static IExecValue Memoize(string key, ExecContext context, Func<IExecValue> compute)
    {
        // Memo bypass (client data layer, slice 4 — the action-miss harvest): run the compute directly, with
        // NO Deps frame and NO caching, so the reads inside stay in output position and harvest as leaves
        // (the twin of the client's runWithMemoBypass). Only set while invoking a handler to plan its fetch.
        if (context.MemoBypass) return compute();
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

    // The captured-environment part of a where/orderBy memo key: the lambda's free
    // variables vary per call (e.g. a foreach loop var the predicate closes over), yet the
    // lambda AST id is the SAME node every iteration — keying on (collection id, lambda id)
    // alone collides and returns the first call's result for all. Fold in the lambda's
    // captured NON-top scope values: walk its scope chain up while !IsTop (the transient
    // frames — fn calls, blocks, foreach items — that hold the closed-over locals), and key
    // each bound item by ArgKey. Top scopes are excluded: globals are stable and the
    // collection id already covers the data. Names are sorted so the two interpreters
    // enumerate a scope identically. Over-keying on all captured locals (a superset of the
    // actual free vars) only costs an extra recompute; it is never stale.
    private static string ClosureKey(ExecFunction lambda)
    {
        var key = "";
        for (var s = lambda.Scope; s is { IsTop: false }; s = s.Parent)
            foreach (var name in s.Items.Keys.OrderBy(n => n, StringComparer.Ordinal))
                key += ":" + name + "=" + ArgKey(s.Items[name].Value);
        return key;
    }

    // Invoke a lambda with one already-evaluated argument (for where/orderBy).
    private IExecValue InvokeLambda(ExecFunction fn, IExecValue arg, ExecContext context)
    {
        var callScope = new ExecScope { Parent = fn.Scope };
        if (fn.Function.Params.Length > 0)
            callScope.Items[fn.Function.Params[0].Name] = new ExecScopeItem { Value = arg, IsReadOnly = true };
        return RunBody(fn, callScope, context);
    }

    private IExecValue CallSysFunction(ExecSysFunction sysFn, ICodeValue[] args, ExecScope scope, ExecContext context)
    {
        switch (sysFn.Method)
        {
            case "add":
                AddToCollection(sysFn.Target, ExecuteValue(args[0], scope, context), context);
                return new ExecNothing();
            case "remove":
                RemoveFromCollection(sysFn.Target, ExecuteValue(args[0], scope, context), context);
                return new ExecNothing();
            // setEntry(key, value): a dictionary create/replace. Dict entries persist through
            // the PATH-addressed addEntry WS op (handled on the client); server-side (SSR /
            // refetch never runs a click handler) it no-ops, like setRef.
            case "setEntry":
                return new ExecNothing();
            case "where":
            {
                var lambda = AsLambda(args[0], scope, context);
                return Memoize($"where:a{sysFn.Target.Id}:fn{lambda.Function.Id}{ClosureKey(lambda)}", context,
                    () => Where(sysFn.Target, lambda, context));
            }
            case "orderBy":
            {
                var lambda = AsLambda(args[0], scope, context);
                return Memoize($"orderBy:a{sysFn.Target.Id}:fn{lambda.Function.Id}{ClosureKey(lambda)}", context,
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

    private void AddToCollection(ExecArray coll, IExecValue value, ExecContext context)
    {
        // Persist to a db set (addressed by its intrinsic id); a set member is keyed
        // by its own object identity. A transient object (negative id) is minted into
        // the extent first — its id flips positive, so it is now "in db" by definition.
        // READ-ONLY (client data layer, slice 4 — a planning re-invoke): the in-memory add still happens (so
        // a later read in the handler sees it + harvests) but the STORE is left untouched — the graph is
        // discarded after harvesting, so this is the one store-touching effect the read-only invoke suppresses.
        if (coll is { Kind: ArrayKind.Set, ElementTypeName: { } elemType } && _store != null
            && !context.ReadOnly && value is ExecObject obj)
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

    private void RemoveFromCollection(ExecArray coll, IExecValue value, ExecContext context)
    {
        var item = coll.Items.FirstOrDefault(i => ReferenceEquals(i.Value, value)
            || (i.Value is ExecObject a && value is ExecObject b && a.Id == b.Id));
        if (item != null) coll.Items.Remove(item);

        // READ-ONLY (slice 4): the in-memory remove stands but the store is left untouched (see AddToCollection).
        if (coll.Kind == ArrayKind.Set && _store != null && !context.ReadOnly && value is ExecObject obj && obj.Id > 0)
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
        // Index this element's onClick handler closure by its (render-slot, lambda fn-id) key (client data
        // layer, slice 4). The slot path is LIVE here — ExecuteTagChildren pushed this element's static index
        // (and any enclosing foreach row identity) before rendering it — so the key matches exactly the one
        // the CLIENT reports for the handler it clicked (the same twin-stable derivation). The server's
        // action-miss harvest then looks the closure up + invokes it read-only. Only built when a render
        // opted in (HandlerIndex non-null, i.e. an action-carrying refetch), so a normal render is unchanged.
        if (context.HandlerIndex is { } index
            && attrs.GetValueOrDefault("onClick") is ExecFunction handler)
            index[HandlerKey(context.SlotPath, handler.Function.Id)] = handler;
        var children = ExecuteTagChildren(codeTag.Children, scope, context);
        return new ExecTag { Name = codeTag.Name, Attributes = attrs, Children = children };
    }

    // The address of an onClick handler closure: its enclosing render-slot path joined with its lambda's
    // twin-stable fn-id. Built identically on both twins (the client stamps it on the closure at render so a
    // click can report it; the server indexes by it during the reproduced render) — the slot path alone is
    // not unique (every foreach row's button shares the lambda AST), so the fn-id disambiguates the static
    // shape and the slot the row. Keep in lockstep with codeExec.ts handlerKey.
    public static string HandlerKey(IReadOnlyList<string> slotPath, int fnId) =>
        string.Join("/", slotPath) + "#fn" + fnId;

    private IExecTagChild[] ExecuteTagChild(ICodeTagChild child, ExecScope scope, ExecContext context) => child switch
    {
        // A tag whose name resolves to a function in scope is a COMPONENT (run-once setup,
        // slot-keyed identity); any other tag name is an HTML element.
        CodeTag codeTag when TryResolveComponent(codeTag.Name, scope) is { } component
                                      => ExecuteComponent(codeTag, component, scope, context),
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
        // Push each child's STATIC AST index onto the slot path while it renders, so a
        // component child keys on its render-tree position (robust to a hidden conditional
        // sibling — the static indices don't shift). Balanced push/pop keeps the path clean.
        for (var i = 0; i < body.Length; i++)
        {
            context.SlotPath.Add(i.ToString());
            try { children.AddRange(ExecuteTagChild(body[i], innerScope, context)); }
            finally { context.SlotPath.RemoveAt(context.SlotPath.Count - 1); }
        }
        return [.. children];
    }

    // ── components (Milestone 11) ───────────────────────────────────────────────

    // A tag is a component iff its name resolves to a function in the scope chain (pure
    // name-resolution): <div> is an element because `div` is unbound; <noteForm> is a
    // component because `noteForm` is a function. Non-throwing — a name not in scope, or
    // bound to a non-function, is an HTML element. Stops at the first binding (shadowing).
    private static ExecFunction? TryResolveComponent(string name, ExecScope scope)
    {
        for (var s = scope; s != null; s = s.Parent)
            if (s.Items.TryGetValue(name, out var item))
                return item.Value as ExecFunction;
        return null;
    }

    // Render a component tag (<noteForm desc={...}>). The component runs ONCE PER RENDER-TREE
    // SLOT (keyed on the slot path, not its argument identities), so its local state survives
    // re-renders even when an argument is a fresh object each render. The body returns its
    // reactive view (a render closure); we invoke that — itself slot-keyed, so it recomputes
    // on its own dependencies and never collides with another slot — to produce the tags,
    // which splice into the parent's children (the component tag is not itself an element).
    // A component in tag-child position: render it and splice its view into the parent's children.
    private IExecTagChild[] ExecuteComponent(CodeTag tag, ExecFunction component, ExecScope scope, ExecContext context) =>
        SpliceView(ExecuteComponentValue(tag, component, scope, context));

    // Run a component (slot-keyed setup + the auto-invoked reactive view) and return its VIEW VALUE.
    // This is the form used in VALUE / return position — a component at the page root, e.g. a
    // synthesized `view(parent) → return <refEditor …>`. The tag-child form (ExecuteComponent)
    // splices this value into the parent's children instead.
    private IExecValue ExecuteComponentValue(CodeTag tag, ExecFunction component, ExecScope scope, ExecContext context)
    {
        // Attributes evaluate in the CALLER's context (their deps are the caller's), in tag order,
        // exactly like ordinary call arguments — so a rebuilt-literal descriptor is fresh each render.
        var attrs = tag.Attributes.ToDictionary(a => a.Name, a => ExecuteValue(a.Value, scope, context));
        // The slot path is the chain of static child indices PLUS each enclosing `foreach`'s per-row
        // identity segment (ExecuteTagForEach), so a component inside a list gets a distinct,
        // identity-stable key per row — its state moves with the member across reorder/remove. A
        // value/root component keys on the (empty/root) path — one component per render, so unique.
        var slotKey = "comp:" + string.Join("/", context.SlotPath);
        // `key={...}` is a RESERVED directive (not a param): its value folds into the slot key, so
        // changing it gives the component a NEW identity — a caller-controlled "reset when X changes".
        // Absent → identity is the slot path alone (the zero-config default).
        if (attrs.TryGetValue("key", out var keyVal)) slotKey += "#" + ArgKey(keyVal);
        var args = BindParams(component, attrs);
        var view = Memoize(slotKey, context, () => InvokeClosure(component, args, context));
        if (view is ExecFunction renderClosure)
        {
            // Seed (client data layer, slice 1a): if a state seed targets THIS slot, overwrite the
            // setup's locals with the seeded values BEFORE invoking the view — so the server reproduces
            // the client's exact component state (e.g. a popup the client toggled open) and harvests the
            // data that state demands. The render closure captures the setup's local scope (where its
            // `var state …` lives), so the seed writes straight into renderClosure.Scope. Whole-object
            // overwrite (v1): replace the named var's value wholesale; a var absent from the closure scope
            // is ignored (a seed for a different component shape can never inject a new local). No seed for
            // this slot → the setup's own defaults stand (today's behavior, byte-identical).
            ApplySeed(slotKey, renderClosure.Scope, context);
            view = Memoize(slotKey + ":view", context,
                () => InvokeClosure(renderClosure, [], context));
        }
        return view;
    }

    // Overwrite the seeded locals for `slotKey` in the component setup's scope (client data layer,
    // slice 1a). Each (varName → value) replaces that var's value wholesale (whole-object overwrite,
    // v1) — but only when the var actually exists in the setup scope, so a stale/foreign seed can
    // never inject a new local. Twin of codeExec.ts's applySeed; both must overwrite identically.
    private static void ApplySeed(string slotKey, ExecScope setupScope, ExecContext context)
    {
        if (context.Seed is not { } seed || !seed.TryGetValue(slotKey, out var vars)) return;
        foreach (var (name, value) in vars)
            if (setupScope.Items.TryGetValue(name, out var item)) item.Value = value;
    }

    // Bind evaluated attributes to the component's params BY NAME (desc={d} → the `desc` param), in
    // param order so InvokeFunction binds them positionally; a param with no matching attribute binds
    // to null. `key` is the reserved reset directive, never a param.
    private static IExecValue[] BindParams(ExecFunction component, IReadOnlyDictionary<string, IExecValue> attrs)
    {
        var ps = component.Function.Params;
        var args = new IExecValue[ps.Length];
        for (var i = 0; i < ps.Length; i++)
            args[i] = ps[i].Name != "key" && attrs.TryGetValue(ps[i].Name, out var v) ? v : new ExecNull();
        return args;
    }

    // A component's view splices into the parent's children: a single tag/value becomes one
    // child; an array (a fragment) splices flat.
    private static IExecTagChild[] SpliceView(IExecValue view) => view switch
    {
        ExecArray arr => [.. arr.Items.Select(i => (IExecTagChild)i.Value)],
        _             => [view],
    };

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
            // Push a per-row segment onto the slot path so a component inside this row keys on the
            // member's IDENTITY — its object id (else the item key), the SAME key the DOM
            // reconciler uses (codeExec.ts) — so each row's component state is independent and moves
            // with the object across reorder/insert/remove, not with the row position.
            var rowKey = item.Value is ExecObject o ? o.Id : item.Key;
            context.SlotPath.Add("row" + rowKey);
            try { children.AddRange(ExecuteTagChildren(codeForEach.Body, itemScope, context)); }
            finally { context.SlotPath.RemoveAt(context.SlotPath.Count - 1); }
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
