using DeEnv.Instance;

namespace DeEnv.Code;

// The access-control read floor (M-auth) — the non-bypassable check that sits BELOW Code, on the
// store→runtime seam (DbBridge). It decides whether an object of a given type may load into the `db`
// graph that ships to the client. A denied object never enters the graph (a set member is omitted, a
// single reference becomes null), so no app path — a custom render, a where-query — can route around it.
//
// This is NOT callable from app Code: it is a kernel-floor helper the DbBridge consults while loading.
//
// A rule's condition is an ordinary Code expression (Code-as-data AST) the EXISTING interpreter
// evaluates over a scope { currentUser, object } — `currentUser` is the principal (an ExecObject, or
// ExecNull when anonymous), `object` the candidate being loaded. Property access on a null currentUser
// fails CLOSED (null.prop → null, never a throw — see CodeExecutor.ExecuteInfixOp), so a role condition
// with no principal evaluates to false (deny). That is the deny-by-default anonymous case.
//
// ponytail: READ-only enforcement, TYPE-level rules, the `{ currentUser, object }` condition scope, and
// `==`/null as the only condition surface are THIS slice's ceiling. create/edit/delete verbs parse but
// are not checked here (writes are the WsHandler floor, a later slice); per-field rules, `now`/`client`/
// `db`/cross-row reads in conditions, and richer operators all layer on additively after.
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
    //
    // Deny-by-default AMONG THE RULED subjects: if NO rule grants `read` (or `*`) to this type, the type is
    // unruled → allow (it is not subject to the ruleset for reads). If one or more do, allow iff at least
    // one such rule's condition holds — an absent condition (When == null) is an unconditional grant, a
    // present one is evaluated over { currentUser, object }. So a `Milestone read where currentUser.role ==
    // "Admin"` rule makes Milestone ruled-for-read: an admin passes, a member/anonymous is denied.
    public bool CanRead(string typeName, ExecObject obj)
    {
        if (Dormant) return true;

        var applicable = _rules.Where(r => r.Type == typeName && Grants(r, "read")).ToList();
        if (applicable.Count == 0) return true; // unruled type → not gated for reads

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
}
