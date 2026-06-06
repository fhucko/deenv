namespace DeEnv.Instance;

/// <summary>
/// Raised when a schema document is malformed or violates a validation rule.
/// Carries a clear, specific message naming the offending type or prop so a
/// bad document fails loudly at load time rather than obscurely later.
/// </summary>
public sealed class SchemaValidationException(string message) : Exception(message);
