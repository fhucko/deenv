namespace DeEnv.Storage;

public interface IInstanceStore
{
    // Read the node at path. Null = path does not resolve.
    // Object nodes include DictionaryValue for dictionary-typed fields.
    NodeValue? ReadNode(NodePath path);

    // Write a base-type (leaf) value at path.
    void WriteLeaf(NodePath path, NodeValue value);

    // Write an object node's leaf fields at path. Dictionary-typed fields are left
    // untouched (they are navigation boundaries). Used by object-form Save.
    void WriteObject(NodePath path, ObjectValue value);

    // Build a default-valued entry for the dictionary's element type WITHOUT
    // persisting it. Used to render the "new entry" form. Entries may be any type.
    NodeValue NewEntryTemplate(NodePath dictPath);

    // Create a dictionary entry under an auto-generated (auto-incremented) key.
    // Returns the new key. Only valid for numeric (auto) key generation.
    NodeValue CreateEntry(NodePath dictPath, NodeValue value);

    // Create a dictionary entry under a caller-supplied key (manual key generation).
    // Throws if an entry with that key already exists.
    void CreateEntry(NodePath dictPath, NodeValue key, NodeValue value);

    // Add or overwrite a dictionary entry.
    // Key must be a NodeValue matching the prop's declared keyType.
    void WriteDictionaryEntry(NodePath path, NodeValue key, NodeValue value);

    // Remove a dictionary entry. No-op if key is absent.
    void RemoveDictionaryEntry(NodePath path, NodeValue key);

    // Generate the next key for a dictionary at path.
    // Numeric keyType: IntValue(max + 1), or IntValue(1) if empty.
    NodeValue NextKey(NodePath path);
}
