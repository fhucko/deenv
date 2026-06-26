namespace DeEnv.Instance;

// Query helpers over an InstanceDescription. Kept out of the record so the schema
// models stay pure data (no behaviour) — see the serialization convention.
public static class InstanceDescriptionQuery
{
    public static IReadOnlyList<TypeDefinition> AllTypes(this InstanceDescription desc) =>
        desc.Types ?? [];

    public static TypeDefinition? Db(this InstanceDescription desc) =>
        desc.AllTypes().FirstOrDefault(t => t.Name == "Db");

    public static TypeDefinition? FindType(this InstanceDescription desc, string name) =>
        desc.AllTypes().FirstOrDefault(t => t.Name == name);

    public static bool IsObjectType(this InstanceDescription desc, string name) =>
        desc.FindType(name)?.BaseType == BaseType.Object;

    public static bool IsEnumType(this InstanceDescription desc, string name) =>
        desc.FindType(name)?.BaseType == BaseType.Enum;

    // The underlying VALUE base type a scalar prop of this declared type stores/transfers as.
    // A leaf base name maps to itself; a declared enum stores as Text (its value name); a
    // `password` stores/transfers as Text (the hash is plain text — see BaseType.Password); any
    // other declared type (an object) has no scalar base. Used wherever a scalar prop's value
    // is coerced (the WS write path) or its stored tag checked (the startup guard), so an enum
    // and a password are handled exactly like text with no new value-kind.
    public static BaseType? ScalarBaseOf(this InstanceDescription desc, string typeName)
    {
        if (BaseTypes.IsName(typeName))
            // `password` coerces/validates as Text (its two chokepoints key on the prop's DECLARED
            // type name, not on this base — so the rest of the write path sees Text and never crashes).
            return BaseTypes.Parse(typeName) == BaseType.Password ? BaseType.Text : BaseTypes.Parse(typeName);
        return desc.FindType(typeName)?.BaseType switch
        {
            BaseType.Enum => BaseType.Text,
            _ => null, // an object type is not a scalar
        };
    }

    // True when the named type declares the named prop as a `password`-typed single scalar — the
    // predicate the two M-auth `password`-type chokepoints key on: the READ blank (DbBridge /
    // AccessFloor.ScalarObject ship "" for such a leaf) and the WRITE hash (WsHandler PBKDF2s such a
    // plaintext before the store). Keyed on the DECLARED prop type (not a field-name convention), so
    // it is the honest analogue of the old by-name passwordHash exclusion and works for any password
    // field on any type. A type/prop the schema does not declare → false.
    public static bool IsPasswordProp(this InstanceDescription desc, string? typeName, string propName)
    {
        if (typeName is null) return false;
        var prop = desc.FindType(typeName)?.Props?.FirstOrDefault(p => p.Name == propName);
        return prop is { Cardinality: Cardinality.Single, Type: "password" };
    }

    // True when `value` is an allowed member of the enum type `typeName` (off-list → false).
    // A non-enum type name is never constrained, so it returns true (the caller's other checks
    // apply). The empty string is always allowed: an unset enum field's value is "".
    public static bool EnumAccepts(this InstanceDescription desc, string typeName, string value)
    {
        if (desc.FindType(typeName) is not { BaseType: BaseType.Enum, Values: { } values }) return true;
        return value == "" || values.Contains(value);
    }
}
