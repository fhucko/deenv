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

    // Apply a batch of field-writes under ONE held lock + ONE Save() — model-term mutations (id/prop/value),
    // not a flat KV blob. Every entry in the batch is applied in-memory first, then the file is written once
    // (OS-atomic temp-file-then-move). The batch is ALL-OR-NOTHING at the application layer: the caller
    // must validate every edit BEFORE calling this (WsHandler.HandleCommit does so); an exception thrown
    // from a StoredLeaf or a missing id is a bug, not a user error. No on-disk format change.
    void WriteFieldBatch(IReadOnlyList<(int ObjectId, string Prop, NodeValue Value)> edits);

    // Apply a whole changeset (atomic-commit Step B) — creates + relations + field edits — under ONE held
    // lock + ONE Save(), so it persists all-or-none (OS-atomic temp-file-then-move). A MODEL-TERM mutation
    // list (CLAUDE rule 6): a closed union of create / set-link / ref-link / field-write, NOT a flat blob.
    // The store mints each create (allocating its real id), builds the tempId→realId map, applies the
    // mutations with their object references remapped (a negative tempId resolves to its just-minted real id;
    // a positive id passes through), links + writes, then Saves ONCE. Returns the idMap (tempId→realId) plus
    // each created object's minted nested-collection ids, so the caller can re-key the client's optimistic
    // graph. The caller VALIDATES every create/edit (schema + access floor + password hash) BEFORE calling —
    // an exception here is a bug, not a user error. No on-disk format change.
    IReadOnlyList<CommitCreateResult> CommitBatch(
        IReadOnlyList<CommitCreate> creates, IReadOnlyList<CommitMutation> mutations);

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

// ── atomic-commit batch (Step B) — a model-term changeset (CLAUDE rule 6) ──────────────────────

// A create in a commit batch: mint an object of TypeName with these (already validated + password-hashed)
// scalar Fields. TempId is the client's transient negative id, the key the batch's mutations + the returned
// idMap reference it by until it is minted to a real id.
public sealed record CommitCreate(int TempId, string TypeName, ObjectValue Fields);

// One mutation in a commit batch, a closed union over the object-graph write seams. An *Ref field is an
// OBJECT REFERENCE: a positive real id, or a negative tempId resolved to the create's just-minted real id.
public abstract record CommitMutation;
// Add a member (MemberRef) to the set with intrinsic id SetId.
public sealed record SetLinkMutation(int SetId, int MemberRef) : CommitMutation;
// Point OwnerRef's single-reference prop at TargetRef (null = clear); TargetType is the prop's declared type.
public sealed record RefLinkMutation(int OwnerRef, string Prop, int? TargetRef, string TargetType) : CommitMutation;
// Write a single scalar leaf field on the object with intrinsic id ObjectRef.
public sealed record FieldWriteMutation(int ObjectRef, string Prop, NodeValue Value) : CommitMutation;

// The result of minting one create in a commit batch: the tempId→realId mapping plus the minted object's
// nested COLLECTION props (their own intrinsic ids + element types), so the caller re-keys the client's
// optimistic transient arrays (else later adds into them would silently not persist — mirrors arrayAdd).
public sealed record CommitCreateResult(int TempId, int RealId, IReadOnlyDictionary<string, CommitCollection> Collections);
public sealed record CommitCollection(int Id, string ElementTypeName);
