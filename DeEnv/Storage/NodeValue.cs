namespace DeEnv.Storage;

public abstract record NodeValue;

public sealed record BoolValue(bool Value) : NodeValue;
public sealed record IntValue(int Value) : NodeValue;
public sealed record DecimalValue(decimal Value) : NodeValue;
public sealed record TextValue(string Text) : NodeValue;
public sealed record DateValue(DateOnly Value) : NodeValue;
public sealed record DateTimeValue(DateTimeOffset Value) : NodeValue;

// Object node: field name → value.
// Dictionary-typed fields appear as DictionaryValue (entries loaded inline).
public sealed record ObjectValue(
    IReadOnlyDictionary<string, NodeValue> Fields) : NodeValue;

// Dictionary node: typed key → entry value.
// Key is a NodeValue matching the prop's declared keyType.
// Record structural equality means IntValue(42) works as a dictionary key.
public sealed record DictionaryValue(
    IReadOnlyDictionary<NodeValue, NodeValue> Entries) : NodeValue;
