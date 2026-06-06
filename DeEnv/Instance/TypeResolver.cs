using DeEnv.Storage;

namespace DeEnv.Instance;

public record ResolvedTypeInfo(
    TypeDefinition Type,
    Cardinality Cardinality,
    string? KeyTypeName,
    KeyGeneration? KeyGeneration = null);

public sealed class TypeResolver
{
    private static readonly HashSet<string> BaseTypeNames =
        ["bool", "int", "decimal", "text", "date", "datetime"];

    private readonly InstanceDescription _desc;

    public TypeResolver(InstanceDescription desc) => _desc = desc;

    // Returns type info at the given path, or null if the path doesn't resolve.
    public ResolvedTypeInfo? ResolveType(NodePath path)
    {
        var db = _desc.Db;
        if (db == null) return null;

        var currentType = db;
        var currentCardinality = Cardinality.Single;
        string? currentKeyTypeName = null;
        KeyGeneration? currentKeyGeneration = null;

        foreach (var segment in path.Segments)
        {
            if (currentCardinality == Cardinality.Dictionary)
            {
                // Segment is a dictionary key — descend into the entry.
                currentCardinality = Cardinality.Single;
                currentKeyTypeName = null;
                currentKeyGeneration = null;
            }
            else if (currentType.BaseType == BaseType.Object)
            {
                // Segment is a field name.
                var prop = currentType.Props?.FirstOrDefault(p => p.Name == segment);
                if (prop == null) return null;

                var resolved = ResolveTypeName(prop.TypeName);
                if (resolved == null) return null;

                currentType = resolved;
                currentCardinality = prop.Cardinality;
                if (prop.Cardinality == Cardinality.Dictionary)
                {
                    currentKeyTypeName = prop.EffectiveKeyType;
                    currentKeyGeneration = prop.KeyGeneration;
                }
                else
                {
                    currentKeyTypeName = null;
                    currentKeyGeneration = null;
                }
            }
            else
            {
                return null; // can't navigate into a leaf
            }
        }

        return new ResolvedTypeInfo(currentType, currentCardinality, currentKeyTypeName, currentKeyGeneration);
    }

    private TypeDefinition? ResolveTypeName(string name)
    {
        if (BaseTypeNames.Contains(name))
            return new TypeDefinition(name, name); // synthetic leaf TypeDefinition

        return _desc.FindType(name);
    }
}
