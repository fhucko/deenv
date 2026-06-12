namespace DeEnv.Storage;

// An existing data file does not match the running app's types. Raised by the
// startup guard (StoredDataValidator) before the instance starts serving: the
// store fails loudly instead of running over stale data, and it never reseeds
// over an existing file — deleting or moving the file is a deliberate user act.
public sealed class StoredDataException(string message) : Exception(message);
