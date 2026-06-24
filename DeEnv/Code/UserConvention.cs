namespace DeEnv.Code;

// The `User`-type convention (M-auth). The framework knows ONE type by name — `User` — and two of its
// fields by name: `name` (the login identifier) and `passwordHash` (the salted PBKDF2 hash that
// authenticates a login). These names are FRAMEWORK vocabulary, so per the system/user-separation rule
// they live here in the system layer (pinned to their C# source by a guard test) rather than being woven
// into a user-editable meta-schema or hung on the global `sys` namespace.
//
// This slice uses the convention for exactly two things, both at the kernel floor (never from app Code):
//   • the login lookup resolves the principal by `name` and verifies against `passwordHash`;
//   • the load boundary (DbBridge / AccessFloor) STRUCTURALLY excludes `passwordHash` from every shipped
//     graph — RULE-INDEPENDENTLY (a dormant app with no access rules must still never ship it), so the
//     secret cannot reach the client even through a custom render that reads the field in-graph.
//
// ponytail: only `User` + `name` + `passwordHash` are conventional this slice. The publish-time
// inject/merge of the User shape, a richer principal, and any other reserved field layer on additively.
public static class UserConvention
{
    // The conventional type name the framework treats as the principal type.
    public const string TypeName = "User";

    // The login identifier field — looked up by the `login` action to resolve a principal by name.
    public const string NameField = "name";

    // The secret field: a self-describing PBKDF2 string (see AuthCrypto). NEVER enters a shipped graph
    // and is never set from the client — the structural exclusion at the load boundary enforces the read
    // half; this slice only verifies against it (the write half — setPassword — is a later slice).
    public const string PasswordHashField = "passwordHash";

    // True for the password-hash field ON the User type — the predicate the load loops consult to skip it.
    // Keyed on BOTH the type name and the field name so a same-named field on any OTHER type is unaffected
    // (the secret is a User-type convention, not a global field-name ban). Rule-independent by construction:
    // it reads only the type/field names, never the access ruleset, so a dormant app excludes it too.
    public static bool IsHiddenField(string? typeName, string fieldName) =>
        typeName == TypeName && fieldName == PasswordHashField;
}
