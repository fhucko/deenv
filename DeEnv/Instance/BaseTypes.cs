namespace DeEnv.Instance;

// The leaf base-type vocabulary: the lowercase names a prop uses in its `type` /
// `keyType` reference (e.g. "text", "int") and their BaseType. `object` is not here
// — it is only a type's own baseType, never a prop's referenced type. Lives outside
// the data models (which carry no logic); the loader and store consult it.
public static class BaseTypes
{
    private static readonly Dictionary<string, BaseType> ByName = new()
    {
        ["bool"]     = BaseType.Bool,
        ["int"]      = BaseType.Int,
        ["decimal"]  = BaseType.Decimal,
        ["text"]     = BaseType.Text,
        ["date"]     = BaseType.Date,
        ["datetime"] = BaseType.DateTime,
    };

    public static IReadOnlyCollection<string> Names => ByName.Keys;

    public static bool IsName(string name) => ByName.ContainsKey(name);

    public static BaseType Parse(string name) => ByName[name];

    // Reverse lookup, for printing a description back to app text.
    public static string NameOf(BaseType baseType) =>
        ByName.First(p => p.Value == baseType).Key;

    // A synthetic TypeDefinition standing in for a leaf base type referenced by name.
    public static TypeDefinition Leaf(string name) => new(name, Parse(name));
}
