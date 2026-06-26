using DeEnv.Instance;

namespace DeEnv.Code;

// The `User`-type convention (M-auth). The framework knows ONE type by name — `User` — and its `name`
// field (the login identifier). The CREDENTIAL field is no longer a reserved field NAME: it is whichever
// field the schema declares with the `password` TYPE (BaseType.Password). These remaining names are
// FRAMEWORK vocabulary, so per the system/user-separation rule they live here in the system layer (pinned
// to their C# source by a guard test) rather than being woven into a user-editable meta-schema.
//
// The credential is handled at the kernel floor (never from app Code) by the `password` TYPE's two
// chokepoints — the load boundary blanks a `password`-typed leaf to "" (DbBridge / AccessFloor) and the WS
// write layer PBKDF2-hashes a plaintext (WsHandler) — so the secret cannot reach the client even through a
// custom render. The login lookup resolves the principal by `name` and verifies against the User's
// password-typed field, read RAW from the store (the store keeps the hash; only the value headed to a
// client/condition is blanked).
//
// ponytail: only `User` + `name` + the password TYPE are conventional this slice. The publish-time
// inject/merge of the User shape and a richer principal layer on additively.
public static class UserConvention
{
    // The conventional type name the framework treats as the principal type.
    public const string TypeName = "User";

    // The login identifier field — looked up by the `login` action to resolve a principal by name.
    public const string NameField = "name";

    // The name of the User's CREDENTIAL field — the (first) `password`-typed prop the schema declares on
    // User — or null when the app declares no password field (then login is impossible, the dormant case).
    // The credential is found BY TYPE (BaseType.Password), not by a reserved field name: the login floor
    // reads this field raw from the store to verify, and the seed/set-password write paths target it.
    public static string? PasswordFieldName(InstanceDescription desc) =>
        desc.FindType(TypeName)?.Props?.FirstOrDefault(p =>
            p.Cardinality == Cardinality.Single && p.Type == "password")?.Name;
}
