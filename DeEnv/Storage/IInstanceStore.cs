namespace DeEnv.Storage;

public interface IInstanceStore
{
    // Read the node at path. Null = path does not resolve.
    // Object nodes include DictionaryValue for dictionary-typed fields.
    NodeValue? ReadNode(NodePath path);

    // ── the post-write version (optimistic-concurrency anti-clobber, review finding 3) ──
    // Every MUTATING method returns the store's HEAD version AFTER its write, captured INSIDE the same
    // `_sync` lock as the write (see JsonFileInstanceStore.BumpVersion). The WS handler reports THAT as
    // the reply's `newVersion`, never a second `CurrentVersion` read — a concurrent commit from another
    // session (WS runs on shared thread-pool threads) could land in the gap between a write returning and
    // a separate version read, over-counting the reported version and letting the client re-pin its ctx
    // base too high = a missed clobber. Returning the under-lock version closes that window.

    // Write a base-type (leaf) value at path. Returns the post-write store version.
    int WriteLeaf(NodePath path, NodeValue value);

    // Write an object node's leaf fields at path. Dictionary-typed fields are left
    // untouched (they are navigation boundaries). Used by object-form Save. Returns the post-write version.
    int WriteObject(NodePath path, ObjectValue value);

    // Write a single leaf field on an extent object addressed by its intrinsic id.
    // The Code runtime addresses objects by identity (not path); a two-way-bound
    // prop write persists this way. Throws if no object carries the id. Returns the post-write version.
    int WriteField(int objectId, string prop, NodeValue value);

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
    //
    // baseVersion (optimistic-concurrency anti-clobber guard — DECISIONS.md "App versioning — the full
    // design (M13 clump)", §0's baseVersion bullet): the store version the committing ctx last knew. Null
    // = no check (a caller with no version concept, e.g. a test harness building a batch directly — kept
    // nullable for compatibility, not a permanent opt-out: every real WS commit supplies it). When
    // present, the check and the apply run in ONE critical section (this method's existing `_sync` lock)
    // — the staleness check must never be a separate call from the apply, or two concurrent commits from
    // the same stale base could both pass the check before either applies.
    //
    // FIELD-LEVEL (M13 slice 6): a stale base is NOT rejected outright. For each EXISTING object this batch
    // WRITES A FIELD OF (a FieldWriteMutation's ObjectRef, a RefLinkMutation's OwnerRef — a SetLinkMutation
    // writes no field, so set add/remove COMMUTE and never conflict; a fresh create is exempt) whose
    // last-modified version > baseVersion, the batch's written fields are compared against the fields the
    // interleaved commits (log entries with seq > baseVersion) wrote to that SAME object:
    //   • no field overlap → AUTO-MERGE: apply (both sides' disjoint edits survive — no reject, no retry);
    //   • any field overlap → REJECT the WHOLE batch (ConflictException, store untouched — all-or-none)
    //     with a per-field {base (from the log), mine, theirs (current)} payload the generic form renders.
    // Object-granular pre-filter, field-granular decision: a commit touching only objects/fields unchanged
    // since baseVersion applies even when OTHER objects/fields advanced (disjoint interleaved commits
    // auto-merge). The old whole-object reject is gone — the disjoint case that used to reject now merges.
    //
    // Returns the created-object remaps AND the post-commit store version (captured under the same lock —
    // review finding 3), which the handler reports as CommitResponse.newVersion so the client re-pins the
    // committing ctx's base to a value its own commit actually produced, never an over-counted one. Called
    // even for an EMPTY batch (no creates, no mutations): it then mutates nothing (Version unchanged) and
    // returns the current version, so the handler needs no separate no-op path / CurrentVersion read.
    CommitResult CommitBatch(
        IReadOnlyList<CommitCreate> creates, IReadOnlyList<CommitMutation> mutations, int? baseVersion = null);

    // The store's current HEAD version (StoreDoc.Version) — bumped on every mutating write. Shipped to the
    // client (SSR first paint + refetch) so it can remember "the version I last saw" and stamp a ctx's
    // baseVersion from it. Read-only; taking the lock so a reader never observes a version bumped mid-write.
    int CurrentVersion { get; }

    // Create a dictionary entry under a caller-supplied (manual) key.
    // Throws if an entry with that key already exists. Returns the post-write version.
    int CreateEntry(NodePath dictPath, NodeValue key, NodeValue value);

    // Add or overwrite a dictionary entry.
    // Key must be a NodeValue matching the prop's declared keyType. Returns the post-write version.
    int WriteDictionaryEntry(NodePath path, NodeValue key, NodeValue value);

    // Remove a dictionary entry. No-op if key is absent. Returns the post-write version.
    int RemoveDictionaryEntry(NodePath path, NodeValue key);

    // ── object model (identity, references, sets, GC) ───────────────────────────

    // Mint a new object of `typeName` into its per-type extent and return its intrinsic identity. The
    // object is not yet referenced (link it before GC). Returns the minted ID (not the version) — every WS
    // create path links it in with a following AddToSet/WriteReference whose returned version is the one
    // reported, so this method's own version is never the reply value and its id-returning contract stays.
    int CreateObject(string typeName, ObjectValue fields);

    // Add an existing object (by identity) as a member of the set at setPath. Returns the post-write version.
    int AddToSet(NodePath setPath, int id);

    // Add/remove a member by the set's own intrinsic id (a set has one identity but
    // may be reached by many paths). The Code runtime addresses sets this way. Return the post-write version.
    int AddToSet(int setId, int objectId);
    int RemoveFromSet(int setId, int objectId);

    // The declared element type of the set carrying this intrinsic id, or null when
    // no set does. Lets a mutation be validated against the schema before it lands.
    string? SetElementType(int setId);

    // Drop a member reference from the set at setPath, then collect unreachable
    // objects (mark-sweep from the root). Returns the post-write version.
    int RemoveFromSet(NodePath setPath, int id);

    // Point a single object-typed prop at an object (or clear it with null),
    // then collect unreachable objects. Returns the post-write version.
    int SetReference(NodePath fieldPath, int? id);

    // Like SetReference, but the owning object is addressed by its intrinsic id (not a
    // path) — how the Code runtime persists a reference field set from the self-hosted
    // reference editor. `targetTypeName` is the prop's declared type. Collects GC after. Returns the version.
    int WriteReference(int objectId, string prop, int? targetId, string targetTypeName);

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

// Thrown by CommitBatch when the caller's baseVersion is stale AND the batch's fields OVERLAP interleaved
// changes to the SAME field(s) — a genuine same-field collision (M13 slice 6). NOT thrown for a stale base
// whose fields are DISJOINT from what changed meanwhile: those AUTO-MERGE (apply, no reject — the design's
// "disjoint interleaved commits auto-merge, no OCC retry storms"; set add/remove commute; creates never
// conflict). An optimistic-concurrency reject, not a validation error — the batch was well-formed, just
// based on data someone else has since changed the same field of. WsHandler maps this to a commit-rejection
// reply that carries BOTH the user-facing `{ error }` (the existing rollback/global-error-banner path, so a
// custom `fn render()` that ignores conflicts still shows something loud — no silent clobber) AND the
// structured `conflicts` payload the generic form's coarse banner renders.
//
// Subclass of StaleBaseException: the ONE real `catch (StaleBaseException)` call site (PublishSteps.cs's
// stale-publish-boundary scenario) keeps catching it, and the cross-restart guard tests (AppLogSteps.cs),
// which assert via `.Throws<ConflictException>()` rather than a catch block, keep matching the now-more-
// specific type. Its Message still mentions "reload" for both — a same-field collision IS a specific kind
// of stale-base rejection (the kind the design surfaces; the disjoint kind now auto-merges and throws
// nothing at all). Conflicts are TRANSIENT (§2): this list is wire/ctx state built at reject time from the
// log + the live store, never a persisted row.
public sealed class ConflictException(string message, IReadOnlyList<ConflictField> conflicts)
    : StaleBaseException(message)
{
    public IReadOnlyList<ConflictField> Conflicts { get; } = conflicts;
}

// One conflicted field in a rejected commit (M13 slice 6 — DECISIONS.md / app-versioning-design.md §2's
// per-field `{base, mine, theirs}`). Object = the extent id the collision is on (+ TypeName as a cheap human
// anchor); Field = the prop name (dict conflicts, which never arise from a WIRE commit, would key on
// prop+key — not surfaced here). Base = the value the committing draft SAW at its base — read straight from
// the LOG (§7's old-value logging exists for exactly this: the FIRST interleaved write's `old` for that
// field). Mine = the value this commit tried to write. Theirs = the CURRENT stored value (what an
// interleaved commit left). All three are scalar leaves (a wire commit only writes scalar fields + single
// refs); a ref-field conflict carries the ref-id-as-int shape the store holds. Nullable: an absent field
// (never previously set, or cleared) is a legitimate null on any of the three.
public sealed record ConflictField(
    int Object, string TypeName, string Field, NodeValue? Base, NodeValue? Mine, NodeValue? Theirs);

// Thrown by CommitBatch when the caller's baseVersion is stale for an object the batch EDITS (an
// optimistic-concurrency reject, not a validation error — the batch was well-formed, just based on data
// someone else has since changed). WsHandler maps this to the ordinary `{ error }` commit-rejection reply
// (the existing rollback/global-error-banner path); the message is user-facing. As of M13 slice 6 the
// field-level analysis in CommitBatch throws the ConflictException subclass (above) on a same-field
// collision and AUTO-MERGES a disjoint stale base — so a bare StaleBaseException is no longer thrown by the
// commit path itself, but the type is retained as the base class the two catch sites target.
public class StaleBaseException(string message) : Exception(message);

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
// Upsert a SCALAR dictionary entry (Key → Value) into the `Prop` dictionary field of the object with
// intrinsic id OwnerRef. SERVER-SIDE VOCABULARY ONLY (M13 slice 3, review fix 3): this exists so
// sys.commitDesign can carry a Commit's `idMap` entries in the SAME atomic CommitBatch as the Commit's
// creation, closing the crash window a separate post-batch WriteDictionaryEntry loop opened. The WIRE
// `commit` op does NOT accept dict mutations from clients — WsHandler.HandleCommit constructs no
// DictWriteMutation from a wire message; ctx.commit dict support stays a later slice. Value is a scalar
// leaf (the idMap values are ints); object-valued dict entries are not a commit-batch case (the standalone
// WriteDictionaryEntry keeps that path). Batch semantics mirror FieldWriteMutation: the owner must resolve
// (pre-validated), staleness + version attribution key on OwnerRef, and it logs the same DictSet the
// standalone WriteDictionaryEntry emits (so fsck/replay stay total).
public sealed record DictWriteMutation(int OwnerRef, string Prop, NodeValue Key, NodeValue Value) : CommitMutation;

// The result of minting one create in a commit batch: the tempId→realId mapping plus the minted object's
// nested COLLECTION props (their own intrinsic ids + element types), so the caller re-keys the client's
// optimistic transient arrays (else later adds into them would silently not persist — mirrors arrayAdd).
public sealed record CommitCreateResult(int TempId, int RealId, IReadOnlyDictionary<string, CommitCollection> Collections);
public sealed record CommitCollection(int Id, string ElementTypeName);

// A whole commit's result: the per-create remaps + the post-commit store Version, both captured under the
// store's single lock (review finding 3) so the reply's newVersion is the exact version this commit
// produced — never a separately-read, possibly-over-counted value.
public sealed record CommitResult(IReadOnlyList<CommitCreateResult> Creates, int Version);
