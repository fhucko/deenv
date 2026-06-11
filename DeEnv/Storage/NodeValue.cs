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

// Set node: object identity → the resolved member object. A set holds references
// (ids) into a per-type extent; reads resolve them to the member object so the
// member is addressable by its own identity (the int key). The set itself carries
// an intrinsic Id (it is a mutable container) so the runtime, cache, and mutations
// can reference it stably across renders.
public sealed record SetValue(
    int Id,
    IReadOnlyDictionary<int, NodeValue> Members) : NodeValue;

// A single object-typed prop: a reference into an extent. TargetId is null when
// the reference is unset (nothing chosen yet).
public sealed record ReferenceValue(int? TargetId, string TypeName) : NodeValue;
