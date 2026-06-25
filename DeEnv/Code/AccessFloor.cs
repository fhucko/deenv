using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Code;

// The access-control floor (M-auth) — the non-bypassable check that sits BELOW Code, on the store seam.
// It decides, per principal, what may be READ (which objects load into the `db` graph that ships to the
// client — consulted by DbBridge) and what may be WRITTEN (which create/edit/delete mutations are
// accepted — consulted by WsHandler). A denied read object never enters the graph (a set member is
// omitted, a single reference becomes null); a denied write is rejected before it touches the store.
//
// What the floor gates (after the floor-hardening pass): a where-query reads the graph the read floor
// already gated; the extent listing (sys.extent — the reference picker's candidates AND a custom
// render's own listing) is now gated too (CodeExecutor.ExecuteExtent threads this floor into
// DbBridge.LoadExtent, omitting read-denied rows). The object-graph mutation seams (set-member
// create/delete, object-field + reference edit, AND the path-addressed `write` onto a set member's
// scalar field) are all gated.
// ponytail: the ONE remaining ungated app path is DICTIONARY ENTRIES — a dict entry's READ (DbBridge
// does not gate dict members) and its WRITE (WsHandler addEntry/removeEntry on a dict, and the
// path-addressed `write` onto a dict-entry value). They stay deferred TOGETHER (read + write in
// lockstep), to be gated when dict reads are; until then a ruled dict entry is reachable.
//
// This is NOT callable from app Code: it is a kernel-floor helper the DbBridge / WsHandler consult.
//
// A rule's condition is an ordinary Code expression (Code-as-data AST) the EXISTING interpreter
// evaluates over a scope { currentUser, object } — `currentUser` is the principal (an ExecObject, or
// ExecNull when anonymous), `object` the candidate being loaded. Property access on a null currentUser
// fails CLOSED (null.prop → null, never a throw — see CodeExecutor.ExecuteInfixOp), so a role condition
// with no principal evaluates to false (deny). That is the deny-by-default anonymous case.
//
// ponytail: TYPE-level rules, the `{ currentUser, object }` condition scope, and `==`/null as the only
// condition surface are THIS slice's ceiling. The READ floor (DbBridge) and the WRITE floor (WsHandler)
// both consult this same evaluator; per-field rules, `now`/`client`/`db`/cross-row reads in conditions,
// and richer operators all layer on additively after.
public sealed class AccessFloor
{
    private readonly IReadOnlyList<AccessRule> _rules;
    private readonly IExecValue _currentUser;
    private readonly CodeExecutor _executor;

    // `rules` is the app's access ruleset; `currentUser` the bound principal as an ExecObject (its scalar
    // fields), or ExecNull when there is no principal. The executor evaluates each applicable condition.
    public AccessFloor(IReadOnlyList<AccessRule> rules, IExecValue currentUser)
    {
        _rules = rules;
        _currentUser = currentUser;
        _executor = new CodeExecutor();
    }

    // True when access to the floor is DORMANT — no rules at all, so every type loads (today's behavior).
    // A fast path so the common (solo-app) case pays nothing and the DbBridge can skip building a scope.
    public bool Dormant => _rules.Count == 0;

    // ── anonymousLockedOut (the auto-mode login gate signal, M-auth login UI) ────
    //
    // True when the app has `read` rules AND none of them grants ANONYMOUS access — i.e. an un-logged-in
    // visitor can read NOTHING. Computed purely from the RULES (never the data), so it is correct even
    // when every list is empty: it is a static property of the ruleset, the same answer whether the store
    // has zero rows or a thousand. Shipped to Code as a read-only system var beside `currentUser`; the
    // synthesized generic render uses it to gate an anonymous request to a <LoginForm> (an app where
    // anonymous can read SOMETHING — a `read where status == "published"` rule — is NOT gated, so "public"
    // pages stay public).
    //
    // FLOOR-CONSISTENT: it mirrors the read floor's own deny-by-default-AMONG-THE-RULED semantics (see
    // Can). The floor gates a type for read ONLY when a `read` rule names it; a type with no read rule is
    // UNRULED and stays readable. So the gate must require at least one READ rule to exist — a dormant app
    // (no rules) AND an app with only WRITE rules (no read rule → reads allowed) both leave anonymous able
    // to read, hence both are NOT locked out. Only once a read rule exists, and no read rule grants
    // anonymous, is an anonymous visitor shut out everywhere. (The task framed this as "rules-exist AND no
    // read rule grants anonymous"; the write-only-rules edge it did not enumerate resolves to NOT-gated to
    // stay consistent with the floor, which would let anonymous read freely there — gating it would show a
    // login form over publicly-readable data.)
    //
    // "Grants anonymous" is a STATIC check over the condition AST: a read rule grants anonymous when its
    // condition is absent (an unconditional `read`) OR does not REFERENCE `currentUser` (a data-only
    // condition like `status == "published"`, which can hold with no principal). A condition that
    // references `currentUser` (a role/identity gate) is treated as NOT granting anonymous, because with
    // a null principal it fails closed (deny). This is data-free by construction — it inspects the rule,
    // not a live evaluation, so it never needs an object or the store.
    // ponytail: the static check is a sound APPROXIMATION. A condition like `currentUser == null`
    // references currentUser yet genuinely grants anonymous — so this would mark such an app locked-out
    // even though anonymous reads succeed. Acceptable for the UI gate (a deliberate `read where
    // currentUser == null` is an unusual way to spell a public rule; a bare `read` is the normal one);
    // a precise check (eval the condition with currentUser=null) is data-coupled for `object`-reading
    // conditions, so it was rejected here. Tighten only if a real app hits the edge.
    public static bool AnonymousLockedOut(IReadOnlyList<AccessRule> rules)
    {
        var readRules = rules.Where(r => Grants(r, "read")).ToList();
        if (readRules.Count == 0) return false; // no read rule → reads unruled → anonymous reads freely
        // Locked out iff NO read rule grants anonymous (every read rule is currentUser-gated).
        return !readRules.Any(r => r.When == null || !ReferencesCurrentUser(r.When));
    }

    // Does a condition AST reference the `currentUser` symbol anywhere? A structural walk over the value
    // tree (the same node families a condition is built from — infix ops, calls, not, object/array
    // literals, tags). The principal is named by the bare symbol `currentUser`; a `currentUser.role`
    // access is `objectProp(currentUser, role)`, so finding the symbol covers member access too.
    private static bool ReferencesCurrentUser(ICodeValue value) => value switch
    {
        CodeSymbol s => s.Name == "currentUser",
        CodeInfixOp op => ReferencesCurrentUser(op.Left) || ReferencesCurrentUser(op.Right),
        CodeNot n => ReferencesCurrentUser(n.Operand),
        CodeCall c => ReferencesCurrentUser(c.Fn) || c.Params.Any(ReferencesCurrentUser),
        CodeArray a => a.Items.Any(ReferencesCurrentUser),
        CodeObject o => o.Props.Any(p => ReferencesCurrentUser(p.Value)),
        CodeAssignment asn => ReferencesCurrentUser(asn.Target) || ReferencesCurrentUser(asn.Value),
        _ => false, // literals (int/bool/text/null), functions, tags — no bare currentUser reference
    };

    // May the principal READ an object of `typeName` (the candidate `obj` already loaded as an ExecObject)?
    // The DbBridge floor consults this while loading the graph: a denied object never enters the graph.
    public bool CanRead(string typeName, ExecObject obj) => Can("read", typeName, obj);

    // May the principal read ANY member of `typeName` — a CONSERVATIVE, DATA-FREE capability (no object, no
    // store), the read counterpart to canWrite. The generic UI reads it (via sys.canRead) to HIDE a
    // collection/route whose element type the principal cannot read at all (e.g. an admin-only `users` set
    // is hidden from anonymous, and /users 404s) WITHOUT shipping the role. It ERRS TOWARD READABLE so it
    // never hides a collection that has members the per-member read floor WOULD admit (a false-negative
    // here would make readable data vanish — far worse than canWrite's permissive false-positive). Readable
    // UNLESS the type is read-ruled AND every read rule is principal-only AND fails for this principal:
    //   - unruled for read (no read rule) → readable (the floor only restricts what is ruled);
    //   - a bare `read` (no condition) → readable (public);
    //   - a condition referencing `object` (e.g. `status == "published"`) → some member could match → readable;
    //   - a principal-only condition (`currentUser.role == "Admin"`) → evaluated EXACTLY for this principal.
    public bool CanReadType(string typeName)
    {
        if (Dormant) return true;
        var readRules = _rules.Where(r => r.Type == typeName && Grants(r, "read")).ToList();
        if (readRules.Count == 0) return true; // unruled for read → readable
        foreach (var rule in readRules)
        {
            if (rule.When == null) return true;            // bare read → public
            if (ReferencesObject(rule.When)) return true;  // reads the target → some member could match
            if (EvaluateCondition(rule.When, new ExecObject { Props = [], Id = 0, TypeName = typeName }))
                return true;                               // principal-only condition holds for this principal
        }
        return false;
    }

    // Does a condition reference the `object` symbol (the candidate member)? Mirrors ReferencesCurrentUser.
    // A rule that reads the target could hold for SOME member, so CanReadType treats it as readable (show).
    private static bool ReferencesObject(ICodeValue value) => value switch
    {
        CodeSymbol s => s.Name == "object",
        CodeInfixOp op => ReferencesObject(op.Left) || ReferencesObject(op.Right),
        CodeNot n => ReferencesObject(n.Operand),
        CodeCall c => ReferencesObject(c.Fn) || c.Params.Any(ReferencesObject),
        CodeArray a => a.Items.Any(ReferencesObject),
        CodeObject o => o.Props.Any(p => ReferencesObject(p.Value)),
        CodeAssignment asn => ReferencesObject(asn.Target) || ReferencesObject(asn.Value),
        _ => false,
    };

    // May the principal perform a WRITE `verb` (create | edit | delete) on `obj` of `typeName`? The
    // WsHandler mutation floor consults this: a denied create/edit/delete is rejected, the store untouched.
    // For an EDIT/DELETE `obj` is the existing target (its current scalar fields); for a CREATE it is the
    // about-to-be-created value (so a condition like `where object.status == "draft"` reads the new data).
    public bool CanWrite(string verb, string typeName, ExecObject obj) => Can(verb, typeName, obj);

    // Deny-by-default AMONG THE RULED subjects, for ONE verb: if NO rule grants `verb` (or `*`) to this
    // type, the type is unruled for that verb → allow (not subject to the ruleset). If one or more do,
    // allow iff at least one such rule's condition holds — an absent condition (When == null) is an
    // unconditional grant, a present one is evaluated over { currentUser, object }. So a `Milestone edit
    // where currentUser.role == "Admin"` rule makes Milestone ruled-for-edit: an admin passes, a
    // member/anonymous is denied. A dormant floor (no rules) allows everything (today's behavior).
    private bool Can(string verb, string typeName, ExecObject obj)
    {
        if (Dormant) return true;

        var applicable = _rules.Where(r => r.Type == typeName && Grants(r, verb)).ToList();
        if (applicable.Count == 0) return true; // unruled type for this verb → not gated

        foreach (var rule in applicable)
            if (rule.When == null || EvaluateCondition(rule.When, obj))
                return true;
        return false;
    }

    // A rule grants a verb when its verb list names that verb or the `*` wildcard (all verbs).
    private static bool Grants(AccessRule rule, string verb) =>
        rule.Verbs.Contains(verb) || rule.Verbs.Contains("*");

    // Evaluate a rule condition over a fresh scope { currentUser, object } and a THROWAWAY context, so the
    // floor's reads (e.g. the principal's role) never pollute the render's accessed-leaf set — the
    // principal's fields are NOT shipped to the client just because the floor read them (privacy holds).
    // A condition that does not evaluate to a bool fails CLOSED (treated as deny), never throwing the
    // render down: the floor errs to deny, the safe default for an access decision.
    private bool EvaluateCondition(ICodeValue when, ExecObject obj)
    {
        var scope = new ExecScope();
        scope.Items["currentUser"] = new ExecScopeItem { Value = _currentUser, IsReadOnly = true };
        scope.Items["object"] = new ExecScopeItem { Value = obj, IsReadOnly = true };
        try
        {
            return _executor.ExecuteValue(when, scope, new ExecContext()) is ExecBool { Value: true };
        }
        catch (Exception)
        {
            // ANY evaluation fault denies — fail closed. Not just CodeRuntimeException: an arithmetic
            // fault (integer `/` or `%` by a zero divisor throws DivideByZeroException, see
            // CodeExecutor.ExecuteInfixOp) is a .NET exception, not a CodeRuntimeException, and must NOT
            // escape to crash the SSR render (a render-time DoS). An access decision errs to DENY — the
            // safe default — so the broad catch is correct here (the floor never grants on a faulting
            // condition), distinct from the render's own error handling.
            // ponytail: the C#/TS interpreter twins DIVERGE on `/0` (C# throws DivideByZeroException; the
            // TS twin yields Infinity/NaN). That is a conformance gap to settle as its own twin/conformance
            // change; it is OUT OF SCOPE here. This catch only stops the C# fault from crashing the render.
            return false;
        }
    }

    // ── principal + candidate object construction (shared by both floors) ────────
    //
    // Both the read floor (SsrRenderer) and the write floor (WsHandler) build the principal and the
    // candidate object the SAME way — scalar fields only, resolved through the store seam — so the two
    // floors decide over identical inputs. Keeping this in ONE place is the consistency guarantee.

    // The bound principal as an ExecObject of its SCALAR fields — enough for a condition like
    // `currentUser.role == "Admin"`. Loaded by id through the storage seam (NOT the graph floor, so the
    // principal is always resolvable to decide its own access). A null id (anonymous) or an id with no
    // object (a stale/deleted principal) → ExecNull, which fails every role condition closed (deny).
    // Object/collection fields are omitted: a condition reads scalar facts, and the floor evaluates over a
    // throwaway context, so reading only scalars is also a privacy floor on what a condition could touch.
    // ponytail: scalar-only + by-id is this slice's ceiling; a richer principal (the User's references/sets
    // for graph-position conditions) layers on with the membership operator.
    public static IExecValue LoadPrincipal(IInstanceStore store, int? principalUserId)
    {
        if (principalUserId is not { } id || store.ReadById(id) is not { } hit) return new ExecNull();
        return ScalarObject(hit.TypeName, id, hit.Fields);
    }

    // A candidate object (the access decision's `object`) as an ExecObject of its SCALAR fields — what a
    // condition like `where object.status == "draft"` reads. Built from a store ObjectValue (an existing
    // edit/delete target) the same scalar-only way the principal is.
    public static ExecObject ScalarObject(string typeName, int id, ObjectValue fields)
    {
        var props = new Dictionary<string, IExecValue>();
        foreach (var (name, value) in fields.Fields)
            // RULE-INDEPENDENT: never carry the User password hash into a principal/candidate ExecObject.
            // The principal becomes the `currentUser` system var, so excluding it here ALSO keeps the
            // hash out of any condition's reach and out of the shipped currentUser scope.
            if (value is IntValue or TextValue or BoolValue or DecimalValue or DateValue or DateTimeValue
                && !UserConvention.IsHiddenField(typeName, name))
                props[name] = DbBridge.ScalarToExec(value);
        return new ExecObject { Props = props, Id = id, TypeName = typeName };
    }
}
