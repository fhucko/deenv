using DeEnv.Storage;

namespace DeEnv.Instance;

public record ResolvedTypeInfo(
    TypeDefinition Type,
    Cardinality Cardinality,
    string? KeyTypeName);

public sealed class TypeResolver
{
    private readonly InstanceDescription _desc;

    public TypeResolver(InstanceDescription desc) => _desc = desc;

    // Returns type info at the given path, or null if the path doesn't resolve.
    public ResolvedTypeInfo? ResolveType(NodePath path)
    {
        var db = _desc.Db();
        if (db == null) return null;

        var currentType = db;
        var currentCardinality = Cardinality.Single;
        string? currentKeyTypeName = null;

        foreach (var segment in path.Segments)
        {
            if (currentCardinality == Cardinality.Dictionary || currentCardinality == Cardinality.Set)
            {
                // Segment addresses a member: a dictionary key, or a set member's
                // identity. Either way, descend into the (single) element.
                currentCardinality = Cardinality.Single;
                currentKeyTypeName = null;
            }
            else if (currentType.BaseType == BaseType.Object)
            {
                // Segment is a field name.
                var prop = currentType.Props?.FirstOrDefault(p => p.Name == segment);
                if (prop == null) return null;

                var resolved = ResolveTypeName(prop.Type);
                if (resolved == null) return null;

                currentType = resolved;
                currentCardinality = prop.Cardinality;
                // Only a dictionary carries a key type; single and set do not.
                currentKeyTypeName = prop.Cardinality == Cardinality.Dictionary ? (prop.KeyType ?? "text") : null;
            }
            else
            {
                return null; // can't navigate into a leaf
            }
        }

        return new ResolvedTypeInfo(currentType, currentCardinality, currentKeyTypeName);
    }

    private TypeDefinition? ResolveTypeName(string name)
    {
        if (BaseTypes.IsName(name))
            return BaseTypes.Leaf(name); // synthetic leaf TypeDefinition

        return _desc.FindType(name);
    }
}
