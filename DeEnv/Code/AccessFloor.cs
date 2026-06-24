using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Code;

// The access-control floor (M-auth) — the non-bypassable check that sits BELOW Code, on the store seam.
// It decides, per principal, what may be READ (which objects load into the `db` graph that ships to the
// client — consulted by DbBridge) and what may be WRITTEN (which create/edit/delete mutations are
// accepted — consulted by WsHandler). A denied read object never enters the graph (a set member is
// omitted, a single reference becomes null); a denied write is rejected before it touches the store. No
// app path — a custom render, a where-query, a mutation — can route around it.
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

    // May the principal READ an object of `typeName` (the candidate `obj` already loaded as an ExecObject)?
    // The DbBridge floor consults this while loading the graph: a denied object never enters the graph.
    public bool CanRead(string typeName, ExecObject obj) => Can("read", typeName, obj);

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
        catch (CodeRuntimeException)
        {
            return false; // a malformed/uncomputable condition denies — fail closed
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
            if (value is IntValue or TextValue or BoolValue or DecimalValue or DateValue or DateTimeValue)
                props[name] = DbBridge.ScalarToExec(value);
        return new ExecObject { Props = props, Id = id, TypeName = typeName };
    }
}
