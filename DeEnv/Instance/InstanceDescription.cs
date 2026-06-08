using System.Text.Json.Serialization;

namespace DeEnv.Instance;

public enum BaseType { Bool, Int, Decimal, Text, Date, DateTime, Object }

public enum Cardinality { Single, Dictionary, Set }

public record PropDefinition(
    [property: JsonPropertyName("name")]    string Name,
    [property: JsonPropertyName("type")]    string TypeName,
    [property: JsonPropertyName("cardinality")] string? CardinalityRaw = null,
    [property: JsonPropertyName("keyType")] string? KeyTypeName = null,
    [property: JsonPropertyName("nullable")] bool Nullable = false)
{
    public Cardinality Cardinality => CardinalityRaw switch
    {
        "dictionary" => Cardinality.Dictionary,
        "set"        => Cardinality.Set,
        _            => Cardinality.Single
    };

    // Effective key type for a dictionary prop (text when unspecified).
    public string EffectiveKeyType => KeyTypeName ?? "text";
}

public record TypeDefinition(
    [property: JsonPropertyName("name")]     string Name,
    [property: JsonPropertyName("baseType")] string BaseTypeRaw,
    [property: JsonPropertyName("props")]    IReadOnlyList<PropDefinition>? Props = null)
{
    public BaseType BaseType => BaseTypeRaw switch
    {
        "bool"     => BaseType.Bool,
        "int"      => BaseType.Int,
        "decimal"  => BaseType.Decimal,
        "text"     => BaseType.Text,
        "date"     => BaseType.Date,
        "datetime" => BaseType.DateTime,
        "object"   => BaseType.Object,
        _          => throw new InvalidOperationException($"Unknown baseType '{BaseTypeRaw}'")
    };
}

public record InstanceDescription(
    [property: JsonPropertyName("types")] IReadOnlyList<TypeDefinition>? Types = null)
{
    public IReadOnlyList<TypeDefinition> AllTypes => Types ?? [];

    public TypeDefinition? Db => AllTypes.FirstOrDefault(t => t.Name == "Db");

    public TypeDefinition? FindType(string name) =>
        AllTypes.FirstOrDefault(t => t.Name == name);

    public bool IsObjectType(string name) => FindType(name)?.BaseType == BaseType.Object;
}
