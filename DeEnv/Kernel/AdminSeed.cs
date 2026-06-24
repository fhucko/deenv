using DeEnv.Code;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Kernel;

// Bootstrap the FIRST admin (M-auth login sub-slice 1d). An app whose schema carries access rules is
// deny-by-default: with no Admin-role `User` present, no one can ever log in (a role condition fails
// closed for an anonymous principal, and you cannot become a principal without an account). So the
// FIRST admin must be seeded from OUTSIDE the access system — a kernel/server-side operation taking
// operator-provided credentials. It cannot be a gated WS action (that would already require being an
// admin: chicken-and-egg).
//
// This is the seed OPERATION, taking credentials as INPUT; how the operator UI collects them (a
// designer create-form, a first-publish-with-rules prompt) is a later slice. It is invoked directly
// against an instance's store + description.
//
// It writes through the store seam (IInstanceStore — never a raw file write), using the `User` field
// convention (UserConvention) and the kernel password hash (AuthCrypto). It is IDEMPOTENT: if a User
// already holds the admin role, it does nothing (a second seed never duplicates the admin).
//
// ponytail: the admin ROLE value is an INPUT (the policy the operator/designer owns — which enum value
// the rules treat as admin), not derived from the rule conditions. Parsing the role literal out of a
// condition AST is fragile and out of this slice; the designer scaffold that turns auth on knows the
// value it granted and passes it here. The role is validated to be a real member of `User.role`'s enum
// so a typo cannot seed an unusable admin.
public static class AdminSeed
{
    // Seed an Admin `User` into `store` (idempotently) so an app with access rules has a loginable
    // operator. `name` is the login identifier (UserConvention.NameField), `password` the plaintext
    // hashed server-side (AuthCrypto, the only place passwords are handled), `adminRole` the enum value
    // the rules grant admin access to (e.g. "Admin"). Returns the existing admin's id when one already
    // exists (no write), else the new admin's id. The seeded User is linked into the root's `set of
    // User` (the `users` convention) when one exists, so it is a real graph member and survives GC
    // (CreateObject alone mints into the extent; a later GC-triggering mutation would sweep an
    // unreferenced object).
    public static int Seed(IInstanceStore store, InstanceDescription desc, string name, string password, string adminRole)
    {
        // The app must declare the User type + its `role` field as an enum, and `adminRole` must be a
        // member of that enum — else the seed would write an unusable admin (a role the rules never
        // match). Fail loudly: a bootstrap with bad inputs is an operator error, caught here.
        var userType = desc.FindType(UserConvention.TypeName)
            ?? throw new InvalidOperationException(
                $"Cannot seed an admin: the app declares no '{UserConvention.TypeName}' type.");
        var roleProp = userType.Props?.FirstOrDefault(p => p.Name == RoleField)
            ?? throw new InvalidOperationException(
                $"Cannot seed an admin: '{UserConvention.TypeName}' has no '{RoleField}' field.");
        if (!desc.IsEnumType(roleProp.Type) || !desc.EnumAccepts(roleProp.Type, adminRole) || adminRole.Length == 0)
            throw new InvalidOperationException(
                $"Cannot seed an admin: '{adminRole}' is not a value of the enum '{roleProp.Type}'.");

        // Idempotent: if any User already holds the admin role, leave it — never duplicate the admin.
        if (FindAdmin(store, adminRole) is { } existingId) return existingId;

        // Mint the admin through the store seam: name + role + the hashed password (the same passwordHash
        // field a `login` verifies against). The hash is self-describing (AuthCrypto), so a later login
        // re-derives against its own stored parameters.
        var id = store.CreateObject(UserConvention.TypeName, new ObjectValue(new Dictionary<string, NodeValue>
        {
            [UserConvention.NameField] = new TextValue(name),
            [RoleField] = new TextValue(adminRole),
            [UserConvention.PasswordHashField] = new TextValue(AuthCrypto.Hash(password)),
        }));

        // Link it into the root's `set of User` (the baked-in `users` convention) so it is reachable from
        // Db and never collected by GC. Done through the store seam by path. When the app declares no
        // such set the admin lives in the extent (still loginable — login reads the extent, not the
        // graph); a baked-in User shape always provides the set.
        if (UsersSetPath(desc) is { } usersPath)
            store.AddToSet(usersPath, id);

        return id;
    }

    // The conventional `role` field on User. The framework already knows User + name + passwordHash
    // (UserConvention); the role is a per-app enum the rules read as `currentUser.role`. Kept local —
    // it is the seed's policy input, not (yet) a framework-reserved field name like passwordHash.
    private const string RoleField = "role";

    // The id of an existing User holding `adminRole`, else null — the idempotency probe. Reads the
    // User extent (the same pool login resolves against) through the store seam.
    private static int? FindAdmin(IInstanceStore store, string adminRole)
    {
        foreach (var (id, fields) in store.ReadExtent(UserConvention.TypeName))
            if (fields.Fields.GetValueOrDefault(RoleField) is TextValue { Text: var role } && role == adminRole)
                return id;
        return null;
    }

    // The path to the root's first `set of User` prop (the `users` convention), or null when the app
    // declares none. A type-level lookup over the Db's props — addressing by path keeps the seed working
    // through the store seam (AddToSet materializes the set node if a fresh store hasn't yet).
    private static NodePath? UsersSetPath(InstanceDescription desc)
    {
        var usersProp = desc.Db()?.Props?.FirstOrDefault(p =>
            p.Cardinality == Cardinality.Set && p.Type == UserConvention.TypeName);
        return usersProp is null ? null : NodePath.Root.Field(usersProp.Name);
    }
}
