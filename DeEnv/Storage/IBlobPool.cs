namespace DeEnv.Storage;

// The append-only, content-addressed blob pool a hosted instance's binary assets (images, v1) live
// in — docs/plans/assets-design.md. One per instance, rooted at AppPaths.BlobsDirFor* — a sibling of
// the JSON store, NEVER referenced by it (JsonFileInstanceStore.Reset/Fsck are deliberately inert to
// the pool; see their comments). A NEW, small storage-IO seam, SIBLING to IInstanceStore — not a
// widening of it: IInstanceStore speaks the model's terms (paths/nodes/dictionary entries); a blob is
// raw bytes with no model shape at all, so it earns its own interface rather than stretching the
// store's. This is also the distributed-ACID rung-H IO-seam guard (docs/plans/distributed-acid-
// design.md rung H): new storage IO goes behind a seam, never a direct File.* call at an HTTP edge.
//
// Write protocol (the upload edge is the ONLY writer): OpenWrite a caller-chosen temp name, stream
// the POST body into it while hashing, then CommitBlob(tempName, hash, ext) renames it to its final
// content-addressed name `<hash>.<ext>` — a no-op (temp discarded, no overwrite) when that name
// already exists, so a concurrent identical upload races benignly onto the same bytes (dedup). NO
// method on this interface ever deletes a COMMITTED blob — the pool is append-only by construction;
// only DeleteTemp exists, and only for an abandoned temp file (e.g. a size-cap abort).
public interface IBlobPool
{
    // Open a fresh, exclusively-owned file for writing under a caller-chosen temp name (a random
    // string so two concurrent uploads never collide before either commits).
    Stream OpenWrite(string tempName);

    // Rename the temp file to its final content-addressed name and return that name. The caller must
    // have closed the write stream first. A no-op (temp file deleted, no overwrite) when the target
    // already exists — the dedup case; the caller's bytes are provably identical (same hash) so the
    // existing pool file is already correct.
    string CommitBlob(string tempName, string hash, string ext);

    // Discard an abandoned temp file (e.g. a size-cap abort mid-upload) — never a committed blob.
    void DeleteTemp(string tempName);

    // Open a committed blob for reading by its final name, or null if absent (a dangling reference —
    // erasure/compaction, later milestones, or simply a name that was never uploaded).
    Stream? OpenRead(string name);

    bool Exists(string name);
}

// A blob pool for a host with none wired (mirrors DeEnv.Http.NoHostActions — the "nothing here"
// default a collaborator param falls back to when omitted): GET/serve reports "absent" (OpenRead
// null, Exists false), matching an ordinary dangling-hash 404. An upload attempt is a genuine
// misconfiguration (bytes would vanish, never readable back), so it throws loudly instead of
// silently accepting them.
public sealed class NoBlobPool : IBlobPool
{
    public Stream OpenWrite(string tempName) =>
        throw new InvalidOperationException("This host has no blob pool configured.");

    public string CommitBlob(string tempName, string hash, string ext) =>
        throw new InvalidOperationException("This host has no blob pool configured.");

    public void DeleteTemp(string tempName) { }

    public Stream? OpenRead(string name) => null;

    public bool Exists(string name) => false;
}
