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
    private static readonly HashSet<string> CollectionMethods = ["add", "remove", "setEntry", "where", "orderBy", "any", "single"];

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

    // The structural commit-diff computer (M13 Track-B B2), injected by the renderer so `sys.diffCommits`
    // can compute a rename-aware diff between two commit objects. Threaded as a DELEGATE — not a direct
    // DesignDiffer call — because DesignDiffer lives in DeEnv.Designer, which this Code layer deliberately
    // never references (the interpreter core references only DeEnv.Instance + DeEnv.Storage). The delegate
    // traffics ONLY Code-layer types (two ExecObject commits + the context for minting transient ids in, an
    // IExecValue report out); its impl lives in SsrRenderer, where both Designer and Code are referenceable.
    // Null for a bare executor (conformance, a condition evaluator, the client twin) ⇒ `sys.diffCommits`
    // throws, as it needs the designer host.
    private readonly Func<ExecObject, ExecObject, ExecContext, IExecValue>? _commitDiff;

    // The dry-run publish-preview computer (M13 Track-B B3), injected by the renderer so
    // `sys.publishPreview(design, targetId)` can compute what a publish onto that target WOULD do — the
    // structured PublishReport (removes/conversions/cardinality with their destructive-cell detail). Like
    // _commitDiff it is a DELEGATE, not a direct KernelHostActions call, because the compute needs
    // CROSS-INSTANCE data (the target's own data file + published-commit stamp) that only the kernel can
    // reach — the interpreter core (and even SsrRenderer) has no kernel reference. The delegate traffics
    // ONLY Code-layer types (the design ExecObject + the target's int runtimeId + the context for minting
    // transient ids, an IExecValue report out); its impl is built BY the kernel and threaded through the
    // render path. Null for a bare executor (conformance, a condition evaluator, the client twin, a
    // non-kernel test instance) ⇒ `sys.publishPreview` throws, as it needs the designer host.
    private readonly Func<ExecObject, int, ExecContext, IExecValue>? _publishPreview;

    // The merge-preview computer (M13 Track-B B4), injected by the renderer so `sys.mergePreview(source,
    // target)` can compute what merging one design branch into another WOULD do — the structured MergeReport
    // (conflicts with base/source/target + the always-shown access changes) computed WITHOUT any write. Like
    // _commitDiff it is a DELEGATE, not a direct DesignMerger call, because the compute reaches DesignMerger
    // (DeEnv.Designer) which this Code layer never references. UNLIKE _publishPreview it is SELF-CONTAINED on
    // the DESIGNER's own store (both branches are Design rows there — no cross-instance/kernel data), so its
    // impl is built in SsrRenderer (which has the designer store), not by the kernel. The delegate traffics
    // ONLY Code-layer types (the two design ExecObjects + the render context for minting transient ids, an
    // IExecValue report out). Null for a bare executor (conformance, a condition evaluator, the client twin) ⇒
    // `sys.mergePreview` throws, as it needs the designer host.
    private readonly Func<ExecObject, ExecObject, ExecContext, IExecValue>? _mergePreview;

    // The eval-context computer (M12 CANVAS-EVAL-1), injected by the renderer so `sys.evalContext(design[,
    // refreshKey])` can ship the two things the canvas walk cannot make itself: a SYNTHETIC `db` graph (the
    // design's own `initialData` seeded into a throwaway store, read back, re-minted with distinct negative
    // ids + Constant) and a content-addressed map of PARSED expression ASTs (source text → serialized AST
    // JSON), which `sys.renderTree(node, ctx)` consumes to evaluate each non-literal leaf/attr against the
    // seed graph with the REAL interpreter. Like _mergePreview it is a DELEGATE, not a direct call, and it is
    // SELF-CONTAINED on the DESIGNER's own store (a design is a row there; the seed needs only the design node
    // + its `initialData`), so its impl is built in SsrRenderer (which has the designer store), not by the
    // kernel. Traffics only Code-layer types (the design ExecObject + the render context for minting the
    // payload's transient ids, an IExecValue payload out). Null for a bare executor (conformance, the client
    // twin, a non-designer host) ⇒ `sys.evalContext` throws — but the CANVAS still renders structurally (its
    // chips), since renderTree(node) with no ctx is the byte-identical pre-eval behavior.
    private readonly Func<ExecObject, ExecContext, IExecValue>? _evalContext;

    public CodeExecutor(IInstanceStore? store = null, IReadOnlyDictionary<string, CodeObject>? descriptors = null,
        TypeResolver? resolver = null, AccessFloor? floor = null, Func<ExecObject, ExecObject, ExecContext, IExecValue>? commitDiff = null,
        Func<ExecObject, int, ExecContext, IExecValue>? publishPreview = null,
        Func<ExecObject, ExecObject, ExecContext, IExecValue>? mergePreview = null,
        Func<ExecObject, ExecContext, IExecValue>? evalContext = null)
    {
        _store = store;
        _descriptors = descriptors ?? new Dictionary<string, CodeObject>();
        _resolver = resolver;
        _floor = floor;
        _commitDiff = commitDiff;
        _publishPreview = publishPreview;
        _mergePreview = mergePreview;
        _evalContext = evalContext;
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
        // Ternary: evaluate the condition, then ONLY the chosen branch (short-circuit). Twin of
        // codeExec.ts's "ternary" case.
        CodeTernary codeTernary => AsBool(ExecuteValue(codeTernary.Condition, scope, context))
                                      ? ExecuteValue(codeTernary.Then, scope, context)
                                      : ExecuteValue(codeTernary.Else, scope, context),
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
            case "discard": m.Ctx.Staged.Clear(); m.Ctx.Creates.Clear(); return new ExecNothing();
            case "commit":
            {
                var nested = m.Ctx.Parent is { Live: false }; // NON-final: transfer up into the parent ctx
                foreach (var (obj, fields) in m.Ctx.Staged)
                    foreach (var (prop, val) in fields)
                        if (nested)
                        {
                            if (!m.Ctx.Parent!.Staged.TryGetValue(obj, out var pf)) m.Ctx.Parent.Staged[obj] = pf = [];
                            pf[prop] = val;
                        }
                        else
                            obj.Props[prop] = val;   // committed to the live object (the client also persists)
                // Creates (atomic-commit Step B): a nested commit TRANSFERS them up into the parent (uniform
                // with edits); the FINAL commit is a server no-op — the C# twin renders ONCE, so the actual
                // persistence (mint + link + apply, all-or-none) lives in WsHandler.HandleCommit, not here.
                if (nested) m.Ctx.Parent!.Creates.AddRange(m.Ctx.Creates);
                m.Ctx.Staged.Clear();
                m.Ctx.Creates.Clear();
                return new ExecNothing();
            }
            // Conflict resolution (M13 slice 6 + Track-B B5): keep-mine (force re-commit at the fresh base) /
            // take-theirs (drop mine + refresh to theirs), and B5's per-field `resolveField(object, field,
            // take)`. CLIENT-only effects driven by a WS reply (codeExec.ts/ws.ts) — the C# twin renders once
            // and never witnesses a conflict, so these are server no-ops (present for twin parity, so an SSR
            // render that references them never throws "Unknown context method"). NO conformance case: like
            // ctx.status/ctx.conflicts, a conflict is a client-only WS-reply phenomenon with no dual semantics.
            case "keepMine":
            case "takeTheirs":
            case "resolveField":
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

            // A data context: `ctx.dirty` (a bool), `ctx.status` (the form-Save lifecycle), `ctx.conflicts`
            // (the same-field collision list — M13 slice 6), or a bound method (`ctx.new`/`commit`/`discard`/
            // `keepMine`/`takeTheirs`). `ctx.status` and `ctx.conflicts` are twin-PARITY only: the server
            // renders ONCE from a fresh store load, so a commit conflict is a CLIENT phenomenon (it lands on
            // a WS reply, in codeExec.ts/ws.ts) that the server never witnesses — so status is always "idle"
            // and conflicts is always the EMPTY list here, with no conformance case, only this read so SSR
            // does not crash when a form renders the indicator / the (never-shown-server-side) banner.
            if (target is ExecCtx ctx)
                return member.Name switch
                {
                    // dirty counts staged EDITS and staged CREATES (atomic-commit Step B): a form with only a
                    // staged create still has unsaved work. Twin of codeExec.ts's ctx.dirty read.
                    "dirty" => new ExecBool { Value = ctx.Staged.Count > 0 || ctx.Creates.Count > 0 },
                    "status" => new ExecText { Value = "idle" },
                    // conflicts: always EMPTY server-side (a conflict is a client-only WS-reply state). An
                    // empty list so `ctx.conflicts.any(...)` is false and `foreach c in ctx.conflicts` is a
                    // no-op — the coarse banner never renders on the SSR paint. Twin of codeExec.ts's read.
                    "conflicts" => new ExecArray { Items = [], Id = --context.LastId.Value, Kind = ArrayKind.List },
                    _ => new ExecCtxMethod { Ctx = ctx, Method = member.Name },
                };

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

        // `&&` / `||` SHORT-CIRCUIT (the fix for the real-data designer break): the right operand is
        // evaluated ONLY when the left doesn't already decide the result — so the universal null-guard
        // idiom `x != null && f(x)` never evaluates `f(null)`. Every guard in the apps was written
        // assuming this (it is what every mainstream language does); the old eager evaluation made those
        // guards decoration and threw on legitimate data (an Instance with no design). Dep note: when the
        // right side is skipped, its reads record no deps — correct, because the left side's own recorded
        // deps re-trigger evaluation whenever the guard flips. Twin of codeExec.ts's "and"/"or".
        if (codeInfixOp.Op == CodeInfixOpType.And)
            return new ExecBool { Value = AsBool(left) && AsBool(ExecuteValue(codeInfixOp.Right, scope, context)) };
        if (codeInfixOp.Op == CodeInfixOpType.Or)
            return new ExecBool { Value = AsBool(left) || AsBool(ExecuteValue(codeInfixOp.Right, scope, context)) };

        var right = ExecuteValue(codeInfixOp.Right, scope, context);
        return codeInfixOp.Op switch
        {
            // `+` is overloaded: a string operand makes it concatenation (both sides stringified),
            // otherwise integer addition. Kept in lockstep with codeExec.ts's "add" case.
            CodeInfixOpType.Add             => left is ExecText || right is ExecText
                                                   ? new ExecText { Value = AsText(left) + AsText(right) }
                                                   : new ExecInt { Value = AsInt(left) + AsInt(right) },
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
            () => DbBridge.LoadExtent(_store, typeName.Value, context, _floor, _resolver?.Schema));
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

    // diffCommits(from, to): the rename-aware STRUCTURAL diff between two design commits (M13 Track-B B2) —
    // the same vocabulary PublishReport uses (renames/adds/removes/conversions/cardinality). A SERVER-BACKED
    // READ, modeled on extent/schema/canRead: the server computes it and the cache entry ships to the client,
    // so the commit-detail page renders it on SSR and reuses it on a client-side refetch (a miss → "Value not
    // available" → refetch). NOT a host action (no async reply, no HostActionScan). Keyed by the two commits'
    // intrinsic ids so both twins produce the SAME key (the client reuses the shipped result under it). The
    // actual compute is the injected _commitDiff delegate (DesignDiffer lives in the Designer layer this core
    // cannot reference); no delegate ⇒ no designer host ⇒ throw.
    //
    // Cached DIRECTLY (an empty-deps CacheEntry), exactly like sys.schema and for the same reason: the report
    // is a freshly-minted transient (negative-id) object tree, which Memoize's factory guard refuses to cache
    // (it assumes a getNewX-style mutable factory). The report is pure, deterministic, user-data-free
    // structural metadata — caching it is correct, and the cache entry is precisely what ships it to the
    // client. A hit returns the SAME report for the rest of the render.
    private IExecValue ExecuteDiffCommits(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 2)
            throw new CodeRuntimeException("diffCommits(from, to) takes two arguments.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecObject from)
            throw new CodeRuntimeException("diffCommits() expects a commit object as its first argument.");
        if (ExecuteValue(call.Params[1], scope, context) is not ExecObject to)
            throw new CodeRuntimeException("diffCommits() expects a commit object as its second argument.");
        var key = $"diffCommits:{from.Id}:{to.Id}";
        if (context.Memo.TryGetValue(key, out var cached)) return cached.Result;
        var report = _commitDiff?.Invoke(from, to, context)
            ?? throw new CodeRuntimeException("diffCommits() requires a designer host context.");
        context.Memo[key] = new CacheEntry { Key = key, Result = report, Deps = new Deps() };
        return report;
    }

    // publishPreview(design, targetId): the dry-run PublishReport a `sys.publish(design, targetId)` WOULD
    // produce — the structural + destructive changes deploying this design onto that target would make (M13
    // Track-B B3). A SERVER-BACKED READ, modeled EXACTLY on diffCommits/schema/canRead: the server computes
    // it (the kernel-wired _publishPreview delegate, which reaches the target's own data file cross-instance)
    // and the cache entry ships to the client, so the editor's Publish section renders it on SSR and reuses
    // it on a client-side refetch (a miss → "Value not available" → refetch). NOT a host action (no async
    // reply, no HostActionScan) — and it changes NOTHING (the delegate computes with dryRun:true; the boundary
    // apply's own dryRun flag skips every disk-touching side effect). `sys.publish` (the confirmed Apply) is
    // the existing host action, unchanged. Keyed by the design's + target's ids so both twins produce the
    // SAME key (the client reuses the shipped result under it). No delegate ⇒ no designer host ⇒ throw.
    //
    // Cached DIRECTLY (an empty-deps CacheEntry), exactly like diffCommits/schema and for the same reason:
    // the report is a freshly-minted transient (negative-id) object tree, which Memoize's factory guard
    // refuses to cache. The report is pure structural metadata (a dry run reads the target's schema/data
    // but returns only the diff shape) — caching it is correct, and the cache entry is what ships it.
    private IExecValue ExecutePublishPreview(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 2)
            throw new CodeRuntimeException("publishPreview(design, targetId) takes two arguments.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecObject design)
            throw new CodeRuntimeException("publishPreview() expects a design object as its first argument.");
        if (ExecuteValue(call.Params[1], scope, context) is not ExecInt targetId)
            throw new CodeRuntimeException("publishPreview() expects an integer target id as its second argument.");
        var key = $"publishPreview:{design.Id}:{targetId.Value}";
        if (context.Memo.TryGetValue(key, out var cached)) return cached.Result;
        var report = _publishPreview?.Invoke(design, targetId.Value, context)
            ?? throw new CodeRuntimeException("publishPreview() requires a designer host context.");
        context.Memo[key] = new CacheEntry { Key = key, Result = report, Deps = new Deps() };
        return report;
    }

    // mergePreview(source, target): the MergeReport a `sys.mergeBranch(source, target)` WOULD produce — the
    // conflicts (each with base/source/target) + the always-surfaced access changes + any drift/no-op signal,
    // computed WITHOUT any write (M13 Track-B B4). A SERVER-BACKED READ, modeled EXACTLY on diffCommits/
    // publishPreview: the server computes it (the injected _mergePreview delegate, SELF-BUILT in SsrRenderer
    // over the designer's OWN store — both branches are Design rows there, no kernel/cross-instance data) and
    // the cache entry ships to the client, so the editor's Merge section renders it on SSR and reuses it on a
    // client-side refetch (a miss → "Value not available" → refetch). NOT a host action (no async reply, no
    // HostActionScan) — and it changes NOTHING (the delegate runs the shared merge-compute MINUS the apply
    // write). `sys.mergeBranch` (the confirmed Apply) is the host action, unchanged. Keyed by the two designs'
    // ids so both twins produce the SAME key (the client reuses the shipped result under it). No delegate ⇒ no
    // designer host ⇒ throw.
    //
    // Cached DIRECTLY (an empty-deps CacheEntry), exactly like diffCommits/publishPreview and for the same
    // reason: the report is a freshly-minted transient (negative-id) object tree, which Memoize's factory guard
    // refuses to cache. The report is pure structural metadata (type/prop/fn/rule names + old/new values, no
    // row data) — caching it is correct, and the cache entry is what ships it to the client.
    private IExecValue ExecuteMergePreview(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length != 2)
            throw new CodeRuntimeException("mergePreview(source, target) takes two arguments.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecObject source)
            throw new CodeRuntimeException("mergePreview() expects a design object as its first argument.");
        if (ExecuteValue(call.Params[1], scope, context) is not ExecObject target)
            throw new CodeRuntimeException("mergePreview() expects a design object as its second argument.");
        var key = $"mergePreview:{source.Id}:{target.Id}";
        if (context.Memo.TryGetValue(key, out var cached)) return cached.Result;
        var report = _mergePreview?.Invoke(source, target, context)
            ?? throw new CodeRuntimeException("mergePreview() requires a designer host context.");
        context.Memo[key] = new CacheEntry { Key = key, Result = report, Deps = new Deps() };
        return report;
    }


    // evalContext(design[, refreshKey]): the SERVER-BACKED eval context the canvas walk consumes (M12
    // CANVAS-EVAL-1) — the twin of execEvalContext. A SERVER-COMPUTED READ modeled EXACTLY on publishPreview/
    // mergePreview: the server builds the payload (the injected _evalContext delegate, SELF-BUILT in
    // SsrRenderer over the designer's own store) and the cache entry ships it to the client, so the editor's
    // canvas evaluates on SSR and reuses the SAME payload on a client refetch (a miss → "Value not available"
    // → refetch). The payload is ONE Constant ExecObject { db, exprs, ambients, params }: `db` a re-minted
    // synthetic seed graph, `exprs` a content-addressed source-text → { text, ast } map (ast = a serialized
    // AST JSON string), `ambients`/`params` reserved-empty for the follow-ups. Keyed by the design id +
    // stateKey ("default" in v1 — the reserved uses/state slot) + the optional refreshKey scalar, EMPTY deps
    // (never keyed on the design subgraph — the deliberate inversion of the S3a auto-live race: an edit to a
    // node's expr does NOT stale this entry, so it never forces a refetch that would clobber the optimistic
    // tree-editor mutation; the edited text simply misses the shipped map → its chip, until an explicit
    // Refresh bumps refreshKey). No delegate ⇒ no designer host ⇒ throw.
    //
    // Memo leak discipline (the shipped execPreviewRender pattern): a Refresh mints a NEW key for the SAME
    // design; nothing else evicts the prior generation (empty deps, never stale), so PRUNE any other
    // `evalContext:<designId>[:*]` entry right before computing a genuinely new key — bounding the cache to
    // one seed graph per design (the GC sweep reclaims the orphaned graph). Scoped to a real new-key miss: an
    // ordinary re-render with the SAME key is a plain cache HIT, undisturbed (evicting on every render would
    // force a miss that defeats the SPA flash guard — the S3a regression).
    private IExecValue ExecuteEvalContext(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length is not (1 or 2))
            throw new CodeRuntimeException("evalContext(design[, refreshKey]) takes one or two arguments.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecObject design)
            throw new CodeRuntimeException("evalContext() expects a design object as its first argument.");
        var key = $"evalContext:{design.Id}:default";
        if (call.Params.Length == 2)
            key += ":" + ScalarKeyPart(ExecuteValue(call.Params[1], scope, context));
        if (context.Memo.TryGetValue(key, out var cached)) return cached.Result;
        // A genuinely NEW key (a Refresh, or first compute): prune the prior generation for THIS design.
        var prefix = $"evalContext:{design.Id}";
        foreach (var k in context.Memo.Keys.ToList())
            if (k != key && (k == prefix || k.StartsWith(prefix + ":", StringComparison.Ordinal)))
                context.Memo.Remove(k);
        var payload = _evalContext?.Invoke(design, context)
            ?? throw new CodeRuntimeException("evalContext() requires a designer host context.");
        context.Memo[key] = new CacheEntry { Key = key, Result = payload, Deps = new Deps() };
        return payload;
    }

    // A scalar's contribution to a memo key (the evalContext refresh key) — twin-stable with codeExec.ts's
    // scalarKeyPart. Non-scalars contribute empty (a caller passes a scalar refresh token).
    private static string ScalarKeyPart(IExecValue v) => v switch
    {
        ExecInt i => i.Value.ToString(),
        ExecText t => t.Value,
        ExecBool b => b.Value ? "true" : "false",
        _ => "",
    };

    // renderTree(node[, ctx[, fns]]): the CLIENT-COMPUTABLE canvas (M12 S4 foundation) — turns a MetaNode row (tag/
    // expr/order scalars + attrs/children sets, the S1a structured-render schema) into a live tag tree built
    // from the rows. UNLIKE the server-backed reads (publishPreview et al., shipped as data), this is computed by BOTH
    // twins from row data the client already holds — no server delegate, no memo, no refetch. Every read of a
    // node field / set goes through the SAME dep-recording paths ordinary reads use (RecordPropAccess,
    // RecordMembership, RecordScannedItem), so an ordinary tree-editor edit (rename a tag, edit an attr, add/
    // remove a node) re-renders the canvas in the same interaction with no round-trip: on the server the walk
    // harvests the node subgraph into the client state (so the client can replay it); on the client the same
    // reads record deps so an edit invalidates the enclosing render.
    //
    // The optional SECOND arg is the eval context (a { db, exprs, … } payload from sys.evalContext) that lets
    // expression leaves/attrs evaluate client-side (CANVAS-EVAL-1); absent ⇒ every non-literal expr chips.
    // The optional THIRD arg (M12 F2) is the design's `fns` ROWS (`design.fns`, a live `set of MetaFn`) — NOT
    // part of ctx: ctx is refresh-gated server-shipped data, fns are LIVE rows the client already holds, which
    // is what makes editing a component body repaint every expansion SAME-FRAME. Absent ⇒ no tag can ever
    // resolve to a component, so every tag renders literally (today's exact behavior, byte-identical). No
    // singleton/global state is baked into the walk. Twin of codeExec.ts's execRenderTree; the literal rules +
    // chip/empty shapes + expansion semantics are pinned by conformance cases.
    private IExecValue ExecuteRenderTree(CodeCall call, ExecScope scope, ExecContext context)
    {
        if (call.Params.Length is not (1 or 2 or 3))
            throw new CodeRuntimeException("renderTree(node[, ctx[, fns]]) takes one to three arguments.");
        if (ExecuteValue(call.Params[0], scope, context) is not ExecObject node)
            throw new CodeRuntimeException("renderTree() expects a node object as its first argument.");
        // The optional eval context (M12 CANVAS-EVAL-1): a { db, exprs, … } payload (from sys.evalContext).
        // Present ⇒ non-literal leaf/attr expressions EVALUATE against the seed graph; absent ⇒ today's exact
        // chip behavior (the no-ctx conformance case stays byte-identical). A non-object 2nd arg is ignored.
        var ctx = call.Params.Length >= 2 && ExecuteValue(call.Params[1], scope, context) is ExecObject c ? c : null;
        // The optional fns rows (M12 F2): a non-array 3rd arg is ignored — same as absent, no expansion.
        var fns = call.Params.Length == 3 && ExecuteValue(call.Params[2], scope, context) is ExecArray f ? f : null;
        return BuildRenderTree(node, context, ctx, null, fns, new ExpansionState());
    }

    // Walk one MetaNode row → its rendered node. ELEMENT (tag non-empty): a `tag` element carrying
    // data-node=<id> (the provenance spine S4's click-to-select needs — on EVERY emitted element), its
    // literal attrs (ordered by `order`; non-literal and event `on*` attrs skipped — the canvas is display-
    // inert and can't evaluate expressions yet), and its recursively-rendered children (ordered by `order`).
    // LEAF (tag empty, expr non-empty): a literal expr → its unquoted value as a text child; otherwise an
    // EXPRESSION CHIP (span.expr-chip) holding the raw source — the interim placeholder until evaluation lands.
    // INVALID (tag AND expr empty): span.expr-chip.is-empty "(empty)" — a visible marker, never silent nothing.
    //
    // M12 F2 — at an ELEMENT row, BEFORE the literal-element arm, a tag that resolves against `fns` EXPANDS
    // into the matched component's OWN rendered content (see ExpandFn) — the runtime-faithful mirror of
    // TryResolveComponent/ExecuteComponentValue: `bindings` (the walk-local scope — loop vars, or an
    // enclosing expansion's own params) SHADOWS the fns lookup exactly like a scope binding stops runtime
    // resolution, so a tag bound in `bindings` is never looked up in `fns` at all.
    private IExecValue BuildRenderTree(ExecObject node, ExecContext context, ExecObject? ctx,
        Dictionary<string, IExecValue>? bindings, ExecArray? fns, ExpansionState expansion)
    {
        var id = node.Id;
        // The node budget (M12 F2 E4) counts every node visited while ALREADY inside an expansion (Depth>0)
        // — not just the invocations themselves — so a component whose body fans out breadth-wise (not just
        // recursively) still gets bounded. Counted here, ONCE, for every node kind (element/leaf/for/if).
        if (expansion.Depth > 0) expansion.Used++;
        // S6a/S6b control-flow rows. `kind` is the authoritative discriminator; it is read defensively
        // (ReadNodeTextOptional) so a legacy node predating the field — or a hand-built test node — reads
        // "" and falls to the tag/expr discrimination, never a hard miss. A real row always carries it
        // (M5-defaulted), so the dep is recorded and shipped exactly like tag/expr. WITHOUT ctx (or on ANY
        // eval failure) a for/if row renders as the S6a NO-CTX TEMPLATE; WITH ctx (S6b) BuildFor/BuildIf
        // EVALUATE the collection/condition against the seed graph and render the taken branch / per-item
        // instances (the row scope). `bindings` is the accumulated row scope (loop vars) layered onto {db}
        // in the isolated eval — null at the top level, extended per for-item as the walk recurses inward.
        var kind = ReadNodeTextOptional(node, "kind", context);
        if (kind == "for") return BuildFor(node, id, context, ctx, bindings, fns, expansion);
        if (kind == "if") return BuildIf(node, id, context, ctx, bindings, fns, expansion);
        var tag = ReadNodeText(node, "tag", context);
        var expr = ReadNodeText(node, "expr", context);
        if (tag.Length > 0)
        {
            // M12 F2 — resolve the tag against `fns` (a dep-recorded row read, so a rename re-renders
            // same-frame), UNLESS a walk-local binding shadows it (grill E1). A match EXPANDS — subject to
            // the depth cap + node budget (grill E4), past which it degrades to an honest component chip
            // rather than hanging either twin on a runaway recursive component.
            if (fns != null && (bindings == null || !bindings.ContainsKey(tag)) && ResolveFn(fns, tag, context) is { } fn)
            {
                if (expansion.Depth >= ExpansionDepthCap || expansion.Used >= ExpansionNodeBudget)
                    return Chip("component-chip", tag, id);
                var fnName = ReadNodeText(fn, "name", context);
                var bodyRoot = OrderedMembers(fn, "body", context).FirstOrDefault();
                if (fnName.Length == 0 || bodyRoot == null) return Chip("component-chip", fnName, id); // never guess
                return ExpandFn(fn, bodyRoot, node, context, ctx, bindings, fns, expansion);
            }
            var attributes = new Dictionary<string, IExecValue> { ["data-node"] = new ExecText { Value = id.ToString() } };
            foreach (var attr in OrderedMembers(node, "attrs", context))
            {
                var name = ReadNodeText(attr, "name", context);
                var value = ReadNodeText(attr, "value", context);
                // Event attrs are always inert; "data-node" is the RESERVED provenance attr this walk itself
                // stamps (above) — a user attr of that name is skipped so it can never clobber the id S4's
                // click-to-select depends on.
                if (name.Length == 0 || IsEventAttr(name) || name == "data-node") continue;
                if (LiteralValue(value) is { } literal) { attributes[name] = literal; continue; } // literal → applied
                // Non-literal + an eval context: evaluate the attr's expression against the seed graph; a
                // SCALAR result applies (the same scalar-to-text an attr takes), any throw / map-miss / non-
                // scalar is skipped (today's exact non-literal behavior). No ctx ⇒ skipped, unchanged.
                if (ctx != null && EvaluateCtxExpr(value, ctx, context, bindings) is (ExecText or ExecInt or ExecBool) and { } scalar)
                    attributes[name] = scalar;
            }
            var children = OrderedMembers(node, "children", context)
                .Select(c => (IExecTagChild)BuildRenderTree(c, context, ctx, bindings, fns, expansion))
                .ToArray();
            return new ExecTag { Name = tag, Attributes = attributes, Children = children };
        }
        if (expr.Length > 0)
        {
            if (IsLiteral(expr)) return new ExecText { Value = LiteralDisplay(expr) }; // a literal text/number/bool
            // Non-literal + an eval context: evaluate against the seed graph. A clean eval renders the value
            // (as a text child, the same scalar-to-text a rendered child takes); any throw (tier 2) or a
            // map-miss (tier 3, an edited-but-unrefreshed expr) falls to today's exact chip — the fallback is
            // already visually defined and never guesses.
            if (ctx != null && EvaluateCtxExpr(expr, ctx, context, bindings) is { } value)
                return new ExecText { Value = ChildText(value) };
            return Chip("expr-chip", expr, id);                                       // an expression placeholder
        }
        return Chip("expr-chip is-empty", "(empty)", id);                            // neither tag nor expr
    }

    // A `for` row (S6b, WITH ctx). Evaluate the `collection` source against the seed graph under the CURRENT
    // accumulated row scope (a nested for's collection may reference an outer loop var), then render the body
    // PER ITEM with the loop var bound — the instances REPLACE the template (real content: no badge, no
    // dashed marker, no wrapper element). Each item extends the bindings with {item → its value} and re-walks
    // the body; the per-item instances are spliced FLAT into an ExecArray (exactly how the real foreach
    // splices rows — SerializeChild/ui.ts flatten an array child, and each body element keeps its OWN
    // MetaNode data-node, so N instances share 1 template row's id — the S6a provenance decision). The array
    // carries the for-row's own id (inert in the display-inert canvas) so the walk mints nothing into
    // context.LastId (determinism unchanged). ANY failure — no collection source, an eval throw/miss, or a
    // non-collection result — DEGRADES to the S6a template (never guesses). A Set and a List both iterate via
    // .Items, so a synthetic-graph set and a where/orderBy list both instantiate cleanly.
    private IExecValue BuildFor(ExecObject node, int id, ExecContext context, ExecObject? ctx,
        Dictionary<string, IExecValue>? bindings, ExecArray? fns, ExpansionState expansion)
    {
        if (ctx != null && EvaluateCtxExpr(ReadNodeText(node, "collection", context), ctx, context, bindings) is ExecArray collection)
        {
            var item = ReadNodeText(node, "item", context);
            var body = OrderedMembers(node, "children", context).ToList();
            var instances = new List<ExecItem>();
            var key = 0;
            foreach (var member in collection.Items)
            {
                var itemBindings = new Dictionary<string, IExecValue>(bindings ?? []);
                if (item.Length > 0) itemBindings[item] = member.Value;
                foreach (var b in body)
                    instances.Add(new ExecItem { Key = key++, Value = BuildRenderTree(b, context, ctx, itemBindings, fns, expansion) });
            }
            return new ExecArray { Items = instances, Id = id, Kind = ArrayKind.List };
        }
        return BuildForTemplate(node, id, context, ctx, bindings, fns, expansion);
    }

    // An `if` row (S6b, WITH ctx). Evaluate the `condition` under the current row scope; the result MUST be a
    // bool — deenv truthiness belongs to the INTERPRETER, not the canvas, so a non-bool result (or any
    // eval failure → null) DEGRADES to the S6a both-branches template rather than inventing truthiness or
    // guessing a branch. A bool renders ONLY the taken branch's children FLAT (no then/else labels — the
    // taken branch is real content), spliced into an ExecArray exactly like BuildFor. A false condition with
    // an empty elseChildren yields an empty array → nothing (correct).
    private IExecValue BuildIf(ExecObject node, int id, ExecContext context, ExecObject? ctx,
        Dictionary<string, IExecValue>? bindings, ExecArray? fns, ExpansionState expansion)
    {
        if (ctx != null && EvaluateCtxExpr(ReadNodeText(node, "condition", context), ctx, context, bindings) is ExecBool cond)
        {
            var members = OrderedMembers(node, cond.Value ? "children" : "elseChildren", context).ToList();
            var items = new List<ExecItem>();
            var key = 0;
            foreach (var m in members)
                items.Add(new ExecItem { Key = key++, Value = BuildRenderTree(m, context, ctx, bindings, fns, expansion) });
            return new ExecArray { Items = items, Id = id, Kind = ArrayKind.List };
        }
        return BuildIfTemplate(node, id, context, ctx, bindings, fns, expansion);
    }

    // A `for` row → its NO-CTX / DEGRADE TEMPLATE (S6a): a <div class="for-template" data-node=id> holding a
    // small badge (the loop var name + the collection SOURCE as an expr-chip — unevaluated, honest) and the
    // body rendered ONCE. Body leaves referencing the item var render as chips (the item var is unbound in
    // the template — honest, never a guess); any OUTER row-scope binding still threads through (a fallback
    // for nested inside an evaluated outer for). Deterministic + twin-identical (pinned by the conformance
    // case).
    private IExecValue BuildForTemplate(ExecObject node, int id, ExecContext context, ExecObject? ctx,
        Dictionary<string, IExecValue>? bindings, ExecArray? fns, ExpansionState expansion)
    {
        var item = ReadNodeText(node, "item", context);
        var collection = ReadNodeText(node, "collection", context);
        var badge = new ExecTag
        {
            Name = "div",
            Attributes = new() { ["class"] = new ExecText { Value = "for-badge" } },
            Children = new IExecTagChild[]
            {
                new ExecTag
                {
                    Name = "span",
                    Attributes = new() { ["class"] = new ExecText { Value = "for-item" } },
                    Children = [new ExecText { Value = item }],
                },
                Chip("expr-chip", collection, id),
            },
        };
        var children = new List<IExecTagChild> { badge };
        children.AddRange(OrderedMembers(node, "children", context).Select(c => (IExecTagChild)BuildRenderTree(c, context, ctx, bindings, fns, expansion)));
        return new ExecTag
        {
            Name = "div",
            Attributes = new() { ["class"] = new ExecText { Value = "for-template" }, ["data-node"] = new ExecText { Value = id.ToString() } },
            Children = children.ToArray(),
        };
    }

    // An `if` row → its NO-CTX / DEGRADE TEMPLATE (S6a): a <div class="if-template" data-node=id> showing
    // the condition SOURCE as an expr-chip and BOTH branches, each wrapped + marked (then / else). The else
    // branch is OMITTED when `elseChildren` is empty. Never guesses a taken branch (evaluated taken-branch
    // selection is BuildIf); any OUTER row-scope binding still threads through the branch bodies.
    // Deterministic + twin-identical (pinned by the conformance case).
    private IExecValue BuildIfTemplate(ExecObject node, int id, ExecContext context, ExecObject? ctx,
        Dictionary<string, IExecValue>? bindings, ExecArray? fns, ExpansionState expansion)
    {
        var condition = ReadNodeText(node, "condition", context);
        var thenBody = OrderedMembers(node, "children", context).Select(c => (IExecTagChild)BuildRenderTree(c, context, ctx, bindings, fns, expansion)).ToList();
        var elseBody = OrderedMembers(node, "elseChildren", context).Select(c => (IExecTagChild)BuildRenderTree(c, context, ctx, bindings, fns, expansion)).ToList();
        var children = new List<IExecTagChild>
        {
            new ExecTag
            {
                Name = "div",
                Attributes = new() { ["class"] = new ExecText { Value = "if-badge" } },
                Children = [Chip("expr-chip", condition, id)],
            },
            Branch("if-branch if-then", "then", thenBody),
        };
        if (elseBody.Count > 0)
            children.Add(Branch("if-branch if-else", "else", elseBody));
        return new ExecTag
        {
            Name = "div",
            Attributes = new() { ["class"] = new ExecText { Value = "if-template" }, ["data-node"] = new ExecText { Value = id.ToString() } },
            Children = children.ToArray(),
        };
    }

    // A labeled branch wrapper for the if-template: <div class=cls><span class="branch-label">label</span>…body…</div>.
    private static ExecTag Branch(string cls, string label, List<IExecTagChild> body)
    {
        var children = new List<IExecTagChild>
        {
            new ExecTag
            {
                Name = "span",
                Attributes = new() { ["class"] = new ExecText { Value = "branch-label" } },
                Children = [new ExecText { Value = label }],
            },
        };
        children.AddRange(body);
        return new ExecTag { Name = "div", Attributes = new() { ["class"] = new ExecText { Value = cls } }, Children = children.ToArray() };
    }

    // ── M12 F2 — canvas expansion of design-component invocations ──────────────────────────────

    // The expansion cap/budget (grill E4): DEPTH bounds a self-recursive component chain (a cap alone would
    // still let a component looping seed data blow up N^depth in NODE COUNT before the cap ever fires), so a
    // total node BUDGET bounds the whole walk too. Shared, mutable, one instance PER top-level renderTree()
    // call (a "walk") — threaded through the recursion exactly like ctx/bindings/fns. Twin of codeExec.ts's
    // ExpansionState.
    private const int ExpansionDepthCap = 32;
    private const int ExpansionNodeBudget = 10_000;

    private sealed class ExpansionState
    {
        public int Depth;
        public int Used;
    }

    // Resolve a tag NAME against the `fns` rows (ordinary dep-recorded reads — RecordMembership + a `name`/
    // `order` read per row, so a rename or reorder re-renders same-frame). Duplicate names tie-break to the
    // LAST one in `order` (mirrors DefineFunction's last-wins for the mid-edit window before projection's
    // duplicate refusal fires); `>=` also keeps ties on equal `order` deterministic (last in iteration order
    // wins), twin-identical since both twins iterate `fns.Items` in the same shipped list order.
    private ExecObject? ResolveFn(ExecArray fns, string tag, ExecContext context)
    {
        RecordMembership(fns, context);
        ExecObject? best = null;
        var bestOrder = int.MinValue;
        foreach (var item in fns.Items)
        {
            RecordScannedItem(fns, item, context);
            if (item.Value is not ExecObject fn) continue;
            if (ReadNodeText(fn, "name", context) != tag) continue;
            var order = ReadNodeProp(fn, "order", context) is ExecInt n ? n.Value : 0;
            if (best == null || order >= bestOrder) { best = fn; bestOrder = order; }
        }
        return best;
    }

    // Expand a resolved MetaFn invocation — the row-walk analog of ExecuteComponentValue/BindParams, faithful
    // to runtime scoping WITHOUT running a component's setup/view split (there are no rows for that; a fn's
    // body root is a single value/element, matching the render import shape). `invocationNode` is the tag row
    // that matched (its `attrs` are the caller-side arguments; its `children` are DROPPED — runtime never
    // reads a component tag's children either). Params bind BY NAME: a literal attr value binds even with NO
    // ctx (tier-0, LiteralValue); a non-literal attr evaluates via EvaluateCtxExpr under the CALLER's current
    // `callerBindings`; a param with NO matching attr binds ExecNull (runtime truth — BindParams does this
    // too); a param whose attr is PRESENT but UNEVALUABLE (an event/lambda attr, an eval throw, an exprs-map
    // miss, or no ctx at all for a non-literal source) is left OUT of the body bindings entirely, so a body
    // reference to it misses scope and CHIPS — the one deliberate divergence from runtime (never guess a
    // value). A param literally NAMED "key" always binds ExecNull, NEVER reading a same-named attr (BindParams
    // :2334 excludes it too — `key` is the reserved slot-reset directive, not a real param). The body walks
    // with bindings = the params ONLY — the caller's own bindings do NOT leak in (runtime scoping: a
    // component sees its params, not the caller's locals). The result rides an ExecArray (ArrayKind.List,
    // carrying the INVOCATION row's id, inert) — the exact BuildFor splice idiom — so every expanded element
    // keeps its OWN body-row data-node (S4's future click-to-select spine).
    private IExecValue ExpandFn(ExecObject fn, ExecObject bodyRoot, ExecObject invocationNode, ExecContext context,
        ExecObject? ctx, Dictionary<string, IExecValue>? callerBindings, ExecArray fns, ExpansionState expansion)
    {
        var paramNames = ReadNodeText(fn, "params", context)
            .Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        var attrs = OrderedMembers(invocationNode, "attrs", context);
        var bodyBindings = new Dictionary<string, IExecValue>();
        foreach (var paramName in paramNames)
        {
            if (paramName == "key") { bodyBindings[paramName] = new ExecNull(); continue; } // reserved, never bound
            var attr = attrs.FirstOrDefault(a => ReadNodeText(a, "name", context) == paramName);
            if (attr == null) { bodyBindings[paramName] = new ExecNull(); continue; } // no attr ⇒ runtime null
            var value = ReadNodeText(attr, "value", context);
            if (LiteralValue(value) is { } literal) { bodyBindings[paramName] = literal; continue; }
            if (ctx != null && EvaluateCtxExpr(value, ctx, context, callerBindings) is { } evaluated)
                bodyBindings[paramName] = evaluated;
            // else: attr present but unevaluable — leave OUT of bodyBindings so a body reference chips.
        }
        expansion.Depth++;
        try
        {
            var result = BuildRenderTree(bodyRoot, context, ctx, bodyBindings, fns, expansion);
            return new ExecArray { Items = [new ExecItem { Key = 0, Value = result }], Id = invocationNode.Id, Kind = ArrayKind.List };
        }
        finally { expansion.Depth--; }
    }

    // Evaluate ONE canvas expression against the eval context's seed graph (M12 CANVAS-EVAL-1). Looks the
    // source text up in ctx.exprs (content-addressed) → deserializes the shipped AST JSON → runs it through
    // ExecuteValue (the SAME single dispatch ExecuteTag uses — the no-second-engine guard) over a FRESH,
    // ISOLATED scope+context so the designer's own render is UNTOUCHED:
    //   • a fresh ExecScope (NO parent) binding only `db` = the seed graph — the designer's live scope/db is
    //     never reached, and store-backed builtins (sys.extent/schema/…) are unreachable on the BARE executor
    //     below (no _store) so they throw → chip, IDENTICALLY to the client twin (which has no store either) —
    //     twin-symmetric by construction, not by luck (no SSR-vs-hydration flicker);
    //   • a fresh ExecContext with MemoBypass (so where/orderBy compute directly, never touching a shared memo
    //     — the twin of the client's memo-bypass eval, which avoids the id-0 lambda memo-key collision) and
    //     its OWN empty DepStack (the seed reads are NOT recorded as the designer render's deps);
    //   • a BARE `new CodeExecutor()` (store/floor/descriptors all null) — the same interpreter, store-less,
    //     so faithful over values-in-hand (db navigation, collections, arithmetic, pure sys builtins) and
    //     chipping everything store/floor-backed. `ambients`/`params` are reserved-empty in v1, so an expr
    //     referencing path/status/currentUser/row-vars simply misses scope → throws → chip (widens with the
    //     uses/S6 follow-ups). `bindings` (S6b) is the ROW SCOPE — the accumulated loop-var values layered
    //     onto {db} in the isolated scope (a nested for stacks its item onto the outer bindings the walk
    //     passed down), so `{note.title}` inside a `foreach note in db.notes` resolves against the current
    //     item. The bindings ride the SAME parent-less isolation as db (read-only, no pollution of the
    //     designer scope, no dep leakage — the fresh MemoBypass context owns its own DepStack).
    //     Returns the value, or null on any miss/parse-failure/throw (the caller chips).
    private IExecValue? EvaluateCtxExpr(string text, ExecObject ctx, ExecContext context, Dictionary<string, IExecValue>? bindings = null)
    {
        if (ctx.Props.GetValueOrDefault("exprs") is not ExecObject exprs) return null;
        if (exprs.Props.GetValueOrDefault(text) is not ExecObject entry) return null;
        if (entry.Props.GetValueOrDefault("ast") is not ExecText astText) return null;
        ICodeValue ast;
        try { ast = System.Text.Json.JsonSerializer.Deserialize<ICodeValue>(astText.Value, SchemaJson.Options)!; }
        catch { return null; }
        if (ast == null) return null;
        var evalScope = new ExecScope();
        if (ctx.Props.GetValueOrDefault("db") is { } seedDb)
            evalScope.Items["db"] = new ExecScopeItem { Value = seedDb, IsReadOnly = true };
        if (bindings != null)
            foreach (var (name, value) in bindings)
                evalScope.Items[name] = new ExecScopeItem { Value = value, IsReadOnly = true };
        try { return new CodeExecutor().ExecuteValue(ast, evalScope, new ExecContext { MemoBypass = true }); }
        catch { return null; }
    }

    // The display text of an evaluated leaf value — the same scalar-to-text the tag-child serializer uses
    // (SsrRenderer.SerializeChild / codeExec.ts scalarText): text as-is, int/bool their rendered form, and
    // any non-scalar (an object/array/null the leaf wasn't meant to produce) as empty. Twin-stable.
    private static string ChildText(IExecValue v) => v switch
    {
        ExecText t => t.Value,
        ExecInt i => i.Value.ToString(),
        ExecBool b => b.Value ? "true" : "false",
        _ => "",
    };

    // A span.expr-chip (or its .is-empty variant) with the node's provenance id and one text child.
    private static ExecTag Chip(string cls, string text, int id) => new()
    {
        Name = "span",
        Attributes = new() { ["class"] = new ExecText { Value = cls }, ["data-node"] = new ExecText { Value = id.ToString() } },
        Children = [new ExecText { Value = text }],
    };

    // Read a MetaNode/MetaAttr text field through the ordinary dep-recording prop path (RecordPropAccess), so
    // an edit to it stales the canvas. A staging overlay wins (a ctx draft edit reflects live). An ABSENT prop
    // THROWS "Unknown field" (review fix 2) — matching the standard `.member`/`sys.field` read AND the TS
    // twin's readNodeProp (which throws "Value not available"): a real MetaNode/MetaAttr row always carries
    // every declared field (M5 defaulting fills an absent int/text on load), so this never fires against a
    // complete row (proven by the green browser scenario); it only matters once S4 drafts a transient node
    // BEFORE every field is set — swallowing to null there would skip RecordPropAccess, so the server would
    // never harvest/ship the field and a client miss could never heal on refetch. Throwing (like every other
    // read) keeps the dep recorded and the miss refetchable.
    private IExecValue ReadNodeProp(ExecObject node, string name, ExecContext context)
    {
        if (NearestStagedValue(node, name, context) is { } staged)
        {
            RecordPropAccess(node, name, staged, context);
            return staged;
        }
        if (!node.Props.TryGetValue(name, out var value))
            throw new CodeRuntimeException($"Unknown field '{name}'.");
        RecordPropAccess(node, name, value, context);
        return value;
    }

    private string ReadNodeText(ExecObject node, string name, ExecContext context) =>
        ReadNodeProp(node, name, context) is ExecText t ? t.Value : "";

    // Like ReadNodeText, but tolerant of an ABSENT prop — returns "" instead of throwing. Used ONLY for the
    // `kind` discriminator so a legacy/incomplete node (one predating the field, or a hand-built test node
    // that omits it) reads as legacy rather than crashing the whole canvas walk. When the prop is PRESENT
    // (every real row — M5 defaults it) the dep is still recorded and the value shipped, exactly like
    // ReadNodeText; the graceful branch only spares the never-a-real-row absent case. Twin of readNodeTextOptional.
    private string ReadNodeTextOptional(ExecObject node, string name, ExecContext context)
    {
        if (NearestStagedValue(node, name, context) is { } staged)
        {
            RecordPropAccess(node, name, staged, context);
            return staged is ExecText st ? st.Value : "";
        }
        if (!node.Props.TryGetValue(name, out var value)) return "";
        RecordPropAccess(node, name, value, context);
        return value is ExecText t ? t.Value : "";
    }

    // The members of a node's `attrs`/`children` SET, ordered by each member's `order` field — observed
    // through the same reads a `node.children.orderBy(order)` foreach makes: a prop dep on the set, a
    // membership dep, and each scanned item recorded (so the server harvests the membership and the client
    // replays it, and an add/remove re-renders). Non-object / absent → empty.
    private List<ExecObject> OrderedMembers(ExecObject node, string setProp, ExecContext context)
    {
        if (ReadNodeProp(node, setProp, context) is not ExecArray set) return [];
        RecordMembership(set, context);
        // Read each member's `order` EXPLICITLY (not lazily inside OrderBy's key selector — a single-element
        // OrderBy can elide the selector, which would skip the `order` dep and NOT ship it, breaking the
        // client replay) — the same explicit per-member read the TS twin makes. RecordScannedItem harvests
        // the membership. OrderBy over the precomputed keys is a STABLE sort (twin of the TS Array.sort).
        var keyed = new List<(ExecObject Obj, int Order)>();
        foreach (var item in set.Items)
        {
            RecordScannedItem(set, item, context);
            if (item.Value is ExecObject o)
                keyed.Add((o, ReadNodeProp(o, "order", context) is ExecInt n ? n.Value : 0));
        }
        return keyed.OrderBy(p => p.Order).Select(p => p.Obj).ToList();
    }

    // ── render-tree literal rules (twin-identical with codeExec.ts; pinned by the conformance case) ──────
    // A leaf/attr value source is a LITERAL when it is ONE complete quoted string, an int, or a bool. Anything
    // else (an expression like `a + b`, a bare symbol, `"a" + b`) is non-literal → a chip (leaf) or a skip
    // (attr). Detection is a manual char-scan (not a Regex) so both interpreters agree byte-for-byte.
    private static bool IsLiteral(string s) => IsStringLiteral(s) || IsIntLiteral(s) || IsBoolLiteral(s);

    // The DISPLAY text of a literal LEAF: a string literal's unescaped content; an int/bool's raw source.
    private static string LiteralDisplay(string s) => IsStringLiteral(s) ? UnquoteString(s) : s;

    // The typed VALUE of a literal ATTR — string → text, int → int, bool → bool (the literal's own value);
    // null for a non-literal (the caller skips it). Review fix 1: an int-SHAPED source outside Int32 range
    // (deenv's `int` is 32-bit) is treated as NON-LITERAL here — TryParse (not Parse) so an overflow returns
    // null instead of throwing OverflowException (which would 500 the whole render); the twin TS literalValue
    // mirrors this with an explicit range check so classification agrees on both interpreters. The LEAF path
    // (LiteralDisplay) never parses — it shows the raw digits verbatim regardless of magnitude — so only the
    // ATTR path needed this guard.
    private static IExecValue? LiteralValue(string s) =>
        IsStringLiteral(s) ? new ExecText { Value = UnquoteString(s) }
        : IsIntLiteral(s) ? (int.TryParse(s, out var n) ? new ExecInt { Value = n } : null)
        : IsBoolLiteral(s) ? new ExecBool { Value = s == "true" }
        : null;

    private static bool IsEventAttr(string name) =>
        name.Length >= 2 && (name[0] is 'o' or 'O') && (name[1] is 'n' or 'N');

    private static bool IsBoolLiteral(string s) => s is "true" or "false";

    private static bool IsIntLiteral(string s)
    {
        if (s.Length == 0) return false;
        var i = 0;
        if (s[0] == '-') { if (s.Length == 1) return false; i = 1; }
        for (; i < s.Length; i++) if (s[i] is < '0' or > '9') return false;
        return true;
    }

    // Matches ^"([^"\\]|\\.)*"$ — a single complete quoted string: an unescaped `"` may appear ONLY as the
    // final char, and a `\` escapes the next char. So `"a" + b` (a quote inside) is NOT a literal.
    private static bool IsStringLiteral(string s)
    {
        if (s.Length < 2 || s[0] != '"') return false;
        var i = 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"') return i == s.Length - 1;   // a bare quote is valid only as the closing quote
            if (c == '\\') { if (i + 1 >= s.Length) return false; i += 2; continue; }  // no trailing backslash
            i++;
        }
        return false;   // never closed
    }

    // Strip the outer quotes of a confirmed string literal and unescape \" and \\ (other \x kept verbatim).
    private static string UnquoteString(string s)
    {
        var inner = s.Substring(1, s.Length - 2);
        var sb = new System.Text.StringBuilder(inner.Length);
        var i = 0;
        while (i < inner.Length)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length && inner[i + 1] is '"' or '\\')
            {
                sb.Append(inner[i + 1]);
                i += 2;
                continue;
            }
            sb.Append(inner[i]);
            i++;
        }
        return sb.ToString();
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

    // Stringify a scalar for `+` string concatenation. Only text/int/bool are valid; null throws.
    // Kept in lockstep with codeExec.ts's `asText` in the add case.
    private static string AsText(IExecValue v) => v switch
    {
        ExecText t => t.Value,
        ExecInt i => i.Value.ToString(),
        ExecBool b => b.Value ? "true" : "false",
        _ => throw new CodeRuntimeException("Cannot convert value to text."),
    };

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
        "diffCommits" => ExecuteDiffCommits(call, scope, context),
        "publishPreview" => ExecutePublishPreview(call, scope, context),
        "mergePreview" => ExecuteMergePreview(call, scope, context),
        "evalContext" => ExecuteEvalContext(call, scope, context),
        "renderTree" => ExecuteRenderTree(call, scope, context),
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
        // setDesign(schema, targetId), rename(id, name), commitDesign(design, message, migration),
        // revertCommit(design, commit), createBranch(design, name) and mergeBranch(source, target, resolutions?) are SERVER-ONLY host
        // actions (the host-action channel). They run only when the client fires the event hook → the
        // `hostAction` WS op; the SSR/refetch renderer never runs them, so here they no-op (exactly like
        // setRef). No conformance case: a host effect returns nothing and is outside the conformance
        // contract — so create/clone dropping their port args (path addressing) is a server-only-plumbing
        // change, not a reactive-semantics one. mergeBranch's structured `resolutions` arg crosses natively
        // via the client's scalarOf array/object serialization (ws.ts) and the server's existing
        // ArgResolutionsOptional — a wire-only change, not evaluation semantics.
        //
        // Each of these builtins also accepts an OPTIONAL trailing success-callback fn arg
        // (docs/plans/host-action-success-signal.md): the CLIENT twin (codeExec.ts) splits it out of the
        // wire args and invokes it only on the action's ok reply. That is pure client-side WS-reply
        // orchestration with no SSR/refetch counterpart — this C# switch is keyed on the call NAME alone
        // and never evaluates args, so a trailing callback (present or not) changes nothing here; the
        // case stays ExecNothing for every arity.
        "publish" => new ExecNothing(),
        "create" => new ExecNothing(),
        "cloneInstance" => new ExecNothing(),
        "delete" => new ExecNothing(),
        "setDesign" => new ExecNothing(),
        "rename" => new ExecNothing(),
        "commitDesign" => new ExecNothing(),
        "revertCommit" => new ExecNothing(),
        "createBranch" => new ExecNothing(),
        "mergeBranch" => new ExecNothing(),
        // importRender(design) (M12 X2a) — convert the design's text render into structured MetaNode rows,
        // server-side via SchemaBridge.ImportRender. A host action like the rest: it runs only on the WS op,
        // never during SSR/refetch render, so it no-ops here (outside the conformance contract).
        "importRender" => new ExecNothing(),
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
            // bug there. A planning invoke must never break the refetch. Log it (still swallowed) so an
            // operator can diagnose a handler that consistently fails to plan — mirrors the SSR-render-error
            // / WS-op-failure diagnostics; the same error also surfaces on the client's re-invoke.
            Console.Error.WriteLine($"Handler harvest planning of '{handlerKey}' failed: {ex.Message}");
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
        // Args evaluate in the caller's context (their deps are the caller's); the body is memoized by
        // (function id, RENDER SLOT, arg identities) with its own captured deps. The live SlotPath segment
        // keeps a RENDER-PROP call (a `body()`) distinct per call site: invoked at two child slots of one
        // component — or once per `foreach` row (a `row<id>` segment) — it shares the same lambda AST id and
        // (often) the same args, so id+args alone collide. This C# core is write-only (Memoize never serves a
        // read-hit), so the fold does not change what it COMPUTES; it must still match the TS twin's key
        // composition byte-for-byte (codeExec executeCall: "fn:" + id + "@" + slotPath.join("/")), so a `fn:`
        // result the server SHIPS (private inputs the client can't recompute) is found by the SAME key on the
        // client. Same render structure ⇒ same SlotPath ⇒ same key on both sides.
        var argVals = new IExecValue[args.Length];
        for (var i = 0; i < args.Length; i++) argVals[i] = ExecuteValue(args[i], scope, context);

        return Memoize(MemoKey($"fn:{fn.Function.Id}@{string.Join("/", context.SlotPath)}", argVals), context, () =>
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
                foreach (var item in sysFn.Target.Items)
                {
                    RecordScannedItem(sysFn.Target, item, context);
                    if (InvokeLambda(lambda, item.Value, context) is ExecBool { Value: true })
                        return new ExecBool { Value = true };
                }
                return new ExecBool { Value = false };
            }
            // single(predicate): the first member matching the predicate, or NULL when none match (it does
            // NOT throw on no-match — a "(choose…)" pick that matches nothing must CLEAR a ref, so null is the
            // needed result). Like `any`, it observes membership (an add/remove can change the answer). The
            // dialect had no single-element accessor on a collection (.where returns a collection, foreach is
            // render-only), so a handler could not pull one object out by a predicate — this adds it.
            case "single":
            {
                var lambda = AsLambda(args[0], scope, context);
                RecordMembership(sysFn.Target, context);
                foreach (var item in sysFn.Target.Items)
                {
                    RecordScannedItem(sysFn.Target, item, context);
                    if (InvokeLambda(lambda, item.Value, context) is ExecBool { Value: true })
                        return item.Value;
                }
                return new ExecNull();
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
        // Staging branch (atomic-commit Step B): a TRANSIENT (id<0) draft added to a SET under a STAGING ctx
        // STAGES — it joins the ctx's Creates instead of persisting, so the changeset commits all-or-none at
        // the outermost commit. The same id<0 discriminator the object-prop staging gate uses; an EXISTING
        // (id>0) member falls through to the live/store path below. Checked FIRST and gated only on the ctx
        // (not the store), so it runs identically to the client twin — including in the store-less conformance
        // harness, the ONLY place it runs on the server (the live AddToCollection mints in-store, below).
        if (coll.Kind == ArrayKind.Set && value is ExecObject { Id: < 0 } draft
            && NearestStagingCtx(context) is { } staging)
        {
            staging.Creates.Add(new StagedCreate(draft, new SetJoin(coll)));
            coll.Items.Add(new ExecItem { Key = draft.Id, Value = draft }); // optimistic row, local only
            return;
        }

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

    // Record an item OBSERVED by a scan (foreach's rows, or the members single/any test until they
    // short-circuit) as an accessed LEAF, so ClientState.Serialize harvests the membership the client must
    // replay the scan over. In output position it is a displayed leaf (AccessedItems); inside a computation it
    // is a pending leaf of the surrounding tag fn (LeafStack), promoted only if that fn returns tags — so a
    // scan used inside a where predicate stays private. RecordMembership alone is just an invalidation dep and
    // ships nothing: a set consumed ONLY by single/any (never a foreach) would otherwise ship empty and the
    // client's single/any would return null/false over it (the E1 host-action-repopulated-set bug). Server
    // harvesting only — the TS twin has no client-state to emit, so it records no leaves.
    private static void RecordScannedItem(ExecArray coll, ExecItem item, ExecContext context)
    {
        if (context.DepStack.Count == 0) context.AccessedItems.Add((coll, item));
        else context.LeafStack.Peek().Items.Add((coll, item));
        OnValueAccessed(context, item.Value);
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
        // A <select>'s onChange handler (RefSelect.applyPick) participates in the SAME action-miss harvest as
        // onClick: a client-toggled create form may first access its candidate collection inside the pick
        // handler, so it must be re-invokable read-only off the reported (slot, fn-id). Indexed identically.
        if (context.HandlerIndex is { } changeIndex
            && attrs.GetValueOrDefault("onChange") is ExecFunction changeHandler)
            changeIndex[HandlerKey(context.SlotPath, changeHandler.Function.Id)] = changeHandler;
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
            RecordScannedItem(collection, item, context);
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
