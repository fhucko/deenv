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
}
