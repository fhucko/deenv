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
    // A leaf base name maps to itself; a declared enum stores as Text (its value name); any
    // other declared type (an object) has no scalar base. Used wherever a scalar prop's value
    // is coerced (the WS write path) or its stored tag checked (the startup guard), so an enum
    // is handled exactly like text with no new value-kind.
    public static BaseType? ScalarBaseOf(this InstanceDescription desc, string typeName)
    {
        if (BaseTypes.IsName(typeName)) return BaseTypes.Parse(typeName);
        return desc.FindType(typeName)?.BaseType switch
        {
            BaseType.Enum => BaseType.Text,
            _ => null, // an object type is not a scalar
        };
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
