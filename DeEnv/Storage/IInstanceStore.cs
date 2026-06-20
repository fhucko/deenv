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

    // Write a single leaf field on an extent object addressed by its intrinsic id.
    // The Code runtime addresses objects by identity (not path); a two-way-bound
    // prop write persists this way. Throws if no object carries the id.
    void WriteField(int objectId, string prop, NodeValue value);

    // Build a default-valued entry for the dictionary's (or set's) element type
    // WITHOUT persisting it. Used to render the "new entry" form.
    NodeValue NewEntryTemplate(NodePath path);

    // Create a dictionary entry under a caller-supplied (manual) key.
    // Throws if an entry with that key already exists.
    void CreateEntry(NodePath dictPath, NodeValue key, NodeValue value);

    // Add or overwrite a dictionary entry.
    // Key must be a NodeValue matching the prop's declared keyType.
    void WriteDictionaryEntry(NodePath path, NodeValue key, NodeValue value);

    // Remove a dictionary entry. No-op if key is absent.
    void RemoveDictionaryEntry(NodePath path, NodeValue key);

    // ── object model (identity, references, sets, GC) ───────────────────────────

    // Mint a new object of `typeName` into its per-type extent and return its
    // intrinsic identity. The object is not yet referenced (link it before GC).
    int CreateObject(string typeName, ObjectValue fields);

    // Add an existing object (by identity) as a member of the set at setPath.
    void AddToSet(NodePath setPath, int id);

    // Add/remove a member by the set's own intrinsic id (a set has one identity but
    // may be reached by many paths). The Code runtime addresses sets this way.
    void AddToSet(int setId, int objectId);
    void RemoveFromSet(int setId, int objectId);

    // The declared element type of the set carrying this intrinsic id, or null when
    // no set does. Lets a mutation be validated against the schema before it lands.
    string? SetElementType(int setId);

    // Drop a member reference from the set at setPath, then collect unreachable
    // objects (mark-sweep from the root).
    void RemoveFromSet(NodePath setPath, int id);

    // Point a single object-typed prop at an object (or clear it with null),
    // then collect unreachable objects.
    void SetReference(NodePath fieldPath, int? id);

    // Like SetReference, but the owning object is addressed by its intrinsic id (not a
    // path) — how the Code runtime persists a reference field set from the self-hosted
    // reference editor. `targetTypeName` is the prop's declared type. Collects GC after.
    void WriteReference(int objectId, string prop, int? targetId, string targetTypeName);

    // All objects currently in a type's extent, by identity. Used for the
    // pick-existing candidate list.
    IReadOnlyDictionary<int, ObjectValue> ReadExtent(string typeName);

    // Resolve a bare reference (the /~/{id} route). Null if no object has that id.
    (string TypeName, ObjectValue Fields)? ReadById(int id);

    // Reinitialize the data to the schema's initial document (the initialData seed
    // when the schema carries one, else the default empty root). Destructive; used
    // for a FRESH publish (a target with no prior data — apply otherwise PRESERVES
    // existing data) and by tests.
    void Reset();
}
