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

    // ── object model (extent-mode schemas: identity, references, sets, GC) ──────

    // Mint a new object of `typeName` into its per-type extent and return its
    // intrinsic identity. The object is not yet referenced (link it before GC).
    int CreateObject(string typeName, ObjectValue fields);

    // Add an existing object (by identity) as a member of the set at setPath.
    void AddToSet(NodePath setPath, int id);

    // Drop a member reference from the set at setPath, then collect unreachable
    // objects (mark-sweep from the root).
    void RemoveFromSet(NodePath setPath, int id);

    // Point a single object-typed prop at an object (or clear it with null),
    // then collect unreachable objects.
    void SetReference(NodePath fieldPath, int? id);

    // All objects currently in a type's extent, by identity. Used for the
    // pick-existing candidate list.
    IReadOnlyDictionary<int, ObjectValue> ReadExtent(string typeName);

    // Resolve a bare reference (the /~/{id} route). Null if no object has that id.
    (string TypeName, ObjectValue Fields)? ReadById(int id);
}
