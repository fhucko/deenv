using System.Text.Json;
using DeEnv.Designer;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Kernel;

// The kernel's implementation of the host-action seam for ONE hosted instance. Its actions either
// project a DESIGN (passed by its id — the designer passes one `Design` out of its `db.designs`
// set) into an app document, or address an instance by its id:
//   • publish(design, targetId) — project the design onto an EXISTING instance (resolved by id over
//     the live hosted set), replacing its document while PRESERVING its data (non-destructive apply).
//   • create(design, name) — project the design into a NEW instance with the given display NAME, via
//     the kernel create delegate (the C# create mechanism: write the doc, load, append the registry,
//     refresh the live set). NO ports — addressing is by path (`/apps/<name>` derives from the name).
//   • cloneInstance(sourceId) — copy an existing instance's app doc AND data into a NEW instance, via
//     the kernel clone delegate. NO ports — the clone gets a unique mount name derived from the source.
//   • delete(targetId) — remove an existing instance, via the kernel delete delegate.
//   • setDesign(design, targetId) — record (on the target's registry entry) that it now runs the
//     passed design, AND deploy it onto the target — the IDE's "Apply" (remember-then-publish). It is
//     publish + the registry write: the registry write half (via the kernel recordDesign delegate)
//     makes the reference explicit so the dropdown can pre-select it later.
//   • commitDesign(design, message) — snapshot the design at its CURRENT log position into an
//     immutable Commit row chained onto its main Branch (M13 slice 3 — DECISIONS "App versioning").
//     Acts purely on the CALLER's own store (no cross-instance kernel delegate needed, unlike
//     create/delete/clone). No write grants exist on Commit/Branch rows (the designer's own `access`
//     section denies create/edit/delete on both), so this is the ONLY path that can ever write one.
// The kernel constructs one of these per instance and threads it into WsHandler, so an action acts
// with the CALLING instance's own data as the design source (its data is the IDE holding the set of
// Designs it edits). A publish/create RESOLVES the passed design's id → its Design subtree in the
// caller's store (through IInstanceStore — never a raw file read) and projects it via SchemaBridge;
// an id that is not a member of the caller's `designs` is rejected, never a write to the wrong app.
// SchemaBridge surfaces its validation failure as a reject (it throws, WsHandler catches). The
// delete / clone delegates are type-distinct (Func<int,Task> vs Func<int,int,int,Task>) so positional
// mix-ups are compile errors; same-typed delegates (deleteInstance / restartInstance) use named args.
public sealed class KernelHostActions(
    string metaAppPath, string dataPath,
    Func<int, InstanceSpec?> resolveTarget,
    Func<string, string, int?, Task> createInstance,
    Func<int, Task> deleteInstance,
    Func<int, Task> cloneInstance,
    Func<int, int, Task> recordDesign,
    Func<int, Task> restartInstance,
    Func<int, string, Task> renameInstance) : IHostActions
{
    public void Run(string action, JsonElement args)
    {
        switch (action)
        {
            case "publish": Publish(args); break;
            case "create": Create(args); break;
            case "cloneInstance": Clone(args); break;
            case "delete": Delete(args); break;
            case "setDesign": SetDesign(args); break;
            case "rename": Rename(args); break;
            case "commitDesign": CommitDesign(args); break;
            default:
                throw new InvalidOperationException($"Unknown host action '{action}'.");
        }
    }

    // publish(design, targetId): project the PASSED design (one of the caller's `db.designs`) onto an
    // EXISTING target, PRESERVING the target's data (non-destructive apply). arg 0 is the design
    // object's id (resolved against the caller's store — see ResolveDesign), arg 1 the target id. Any
    // instance is a publish target (resolution is purely by id); an id that matches no hosted instance
    // is rejected — never a write to the wrong store. ProjectDesignDocument validates the design before
    // WriteDocument writes, so an invalid design (it throws) also writes nothing; WriteDocument keeps
    // existing data across an additive change and reseeds on an incompatible one (until the migration
    // slices carry it forward).
    private void Publish(JsonElement args)
    {
        var design = ResolveDesign(ArgInt(args, 0));
        var targetId = ArgInt(args, 1);
        var target = resolveTarget(targetId)
            ?? throw new InvalidOperationException(
                $"No instance with id {targetId} to publish to.");

        var appDoc = SchemaBridge.ProjectDesignDocument(design); // throws on an invalid design
        SchemaBridge.WriteDocument(appDoc, target.SchemaPath, target.DataPath);
        // Restart the target so the new schema and preserved data take effect immediately. Fire-and-forget:
        // the "ok" is sent before the restart begins, avoiding self-restart deadlock on the WS thread.
        _ = restartInstance(targetId);
    }

    // setDesign(design, targetId): the IDE's "Apply" — record (in the registry) that the target now runs
    // the passed design AND deploy it. arg 0 is the design object's id (a member of the caller's
    // `db.designs`, resolved against the caller's store), arg 1 the target instance id. Project FIRST so
    // an invalid design throws before any registry write or document overwrite (records nothing, writes
    // nothing); then record the reference (the kernel rewrites kernel.json's designId + refreshes the live
    // view); then write the projected doc, PRESERVING the target's data — exactly Publish (non-destructive
    // apply), with the registry write in front. An unknown target id is rejected before any work.
    private void SetDesign(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var targetId = ArgInt(args, 1);
        var design = ResolveDesign(designId);
        var target = resolveTarget(targetId)
            ?? throw new InvalidOperationException(
                $"No instance with id {targetId} to set a design on.");

        var appDoc = SchemaBridge.ProjectDesignDocument(design); // throws on an invalid design
        recordDesign(targetId, designId).GetAwaiter().GetResult();
        SchemaBridge.WriteDocument(appDoc, target.SchemaPath, target.DataPath);
        _ = restartInstance(targetId);
    }

    // create(design, name): project the PASSED design into a NEW instance with the given display NAME —
    // the sibling of publish (spawn rather than replace). arg 0 is the design object id, arg 1 the
    // display name (which ALSO becomes the mount path `/apps/<name>` — addressing is by path now, so
    // there are NO port args). ProjectDesignDocument validates the design first (throws, spawning
    // nothing, on an invalid one); then the kernel create delegate writes + loads it, recording the
    // design's id on the new instance's registry entry (so its dropdown pre-selects that design). The
    // delegate is async; we block on it (the WS dispatch is synchronous, no synchronization context to
    // deadlock on — a single-operator devops action).
    private void Create(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var design = ResolveDesign(designId);
        var name = ArgText(args, 1);

        var appDoc = SchemaBridge.ProjectDesignDocument(design); // throws on an invalid design
        createInstance(appDoc, name, designId).GetAwaiter().GetResult();
    }

    // cloneInstance(sourceId): copy an existing instance (app doc + data) into a NEW one — the
    // data-carrying sibling of create. arg 0 is the SOURCE instance id (a bare int, not a schema
    // object); there are NO port args (the clone gets a unique mount name derived from the source —
    // addressing is by path). The kernel clone delegate resolves the id and copies the files; an
    // unknown id throws (surfaced as the reject). Blocked on for the same reason as create.
    private void Clone(JsonElement args)
    {
        var sourceId = ArgInt(args, 0);
        cloneInstance(sourceId).GetAwaiter().GetResult();
    }

    // delete(targetId): remove an existing instance. arg 0 is the instance id (a bare int). The kernel
    // delete delegate resolves the id and stops + forgets it (removing its id-dir + data); an unknown
    // id throws, surfaced as the reject. Blocked on like create/clone.
    private void Delete(JsonElement args)
    {
        var id = ArgInt(args, 0);
        deleteInstance(id).GetAwaiter().GetResult();
    }

    // Resolve the passed design's id → its `Design` subtree in the CALLER's store (the IDE holds a
    // `db.designs` set of Designs). Reads through IInstanceStore (the model's terms — never a raw file
    // call): a design is `db.designs[id]`, so an id that is not a member of `designs` resolves to no
    // node and is rejected here, before any projection or write. Opening a fresh store over the
    // caller's own meta+data is fine — it is the same single-process data the caller renders from
    // (this action only READS it).
    private ObjectValue ResolveDesign(int designId)
    {
        var meta = InstanceDescriptionLoader.LoadFile(metaAppPath);
        var store = new JsonFileInstanceStore(dataPath, meta);
        var designPath = NodePath.Root.Field("designs").Key(designId.ToString());
        return store.ReadNode(designPath) as ObjectValue
            ?? throw new InvalidOperationException(
                $"No design with id {designId} in the designer's `designs` set.");
    }

    // commitDesign(design, message): snapshot the SHARED working copy (Figma model — no per-user
    // staging) at a marked log position and record it as an immutable Commit row, chaining onto the
    // design's main Branch. arg 0 the design's id (a member of the caller's `db.designs`), arg 1 the
    // commit message text.
    //
    // Consistency discipline (docs/plans/app-versioning-design.md §0 "commits are Figma-model
    // snapshots"): the store interface has no multi-read transaction, so text/logSeq is captured via an
    // OPTIMISTIC retry loop — read the head version, read + snapshot the design, re-read the head
    // version; if it moved, someone edited the design mid-read (a genuinely concurrent designer session,
    // near-zero on a solo operator) — retry. A bounded few attempts, then fail loudly (never silently
    // commit a torn text/logSeq pair). An interleaved edit landing AFTER the final re-check is fine — it
    // lands after this commit's mark, and the NEXT commit picks it up.
    //
    // Atomicity: the Commit row (message/at/design/parent/logSeq/text) + its link into `db.commits` land
    // in ONE CommitBatch (mint + set-link + the `parent`/`design` ref-links, all-or-none). idMap is a
    // DICTIONARY field — not reachable through CommitBatch (no dict-entry CommitMutation exists; see
    // JsonFileInstanceStore's own "dictionary mutation is not yet reachable through ctx.commit()" note) —
    // so its entries are written as a FOLLOW-UP pass, BEFORE the branch head advances. The branch's head
    // reference is written LAST: that is the commit's linearization point (before it, the new Commit row
    // exists and is linked into db.commits, but no branch calls it current yet — after it, the commit is
    // "the" head). A crash between the batch and the head-advance leaves an orphaned-but-harmless Commit
    // row (visible in db.commits, not yet anyone's head) rather than a half-written one.
    private void CommitDesign(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var message = ArgText(args, 1);

        var meta = InstanceDescriptionLoader.LoadFile(metaAppPath);
        var store = new JsonFileInstanceStore(dataPath, meta);
        var designPath = NodePath.Root.Field("designs").Key(designId.ToString());

        const int maxAttempts = 5;
        DesignSnapshot snap = null!;
        int logSeq = 0;
        for (var attempt = 1; ; attempt++)
        {
            var s1 = store.CurrentVersion;
            var design = store.ReadNode(designPath)
                ?? throw new InvalidOperationException(
                    $"No design with id {designId} in the designer's `designs` set.");
            snap = SchemaBridge.Snapshot(design); // throws SchemaValidationException on an invalid design
            var s2 = store.CurrentVersion;
            if (s1 == s2) { logSeq = s1; break; }
            if (attempt >= maxAttempts)
                throw new InvalidOperationException(
                    $"commitDesign: the design kept changing while snapshotting it ({maxAttempts} attempts) — try again.");
        }

        var branch = FindMainBranch(store, designId)
            ?? throw new InvalidOperationException($"Design {designId} has no main branch to commit onto.");
        NodeValue? headField = branch.Fields.Fields.GetValueOrDefault("head");
        int? parentHeadId = headField is ReferenceValue { TargetId: { } h } ? h : null;

        const int commitTemp = -1;
        var commitFields = new Dictionary<string, NodeValue>
        {
            ["message"] = new TextValue(message),
            ["at"]      = new DateTimeValue(DateTimeOffset.UtcNow),
            ["logSeq"]  = new IntValue(logSeq),
            ["text"]    = new TextValue(snap.Text),
        };
        var creates = new List<CommitCreate>
        {
            new CommitCreate(commitTemp, "Commit", new ObjectValue(commitFields)),
        };
        var mutations = new List<CommitMutation>
        {
            new RefLinkMutation(commitTemp, "design", designId, "Design"),
        };
        if (parentHeadId.HasValue)
            mutations.Add(new RefLinkMutation(commitTemp, "parent", parentHeadId, "Commit"));

        var commitsSetId = (store.ReadNode(NodePath.Root.Field("commits")) as SetValue)?.Id
            ?? throw new InvalidOperationException("The designer's `db.commits` set is missing.");
        mutations.Add(new SetLinkMutation(commitsSetId, commitTemp));

        var result = store.CommitBatch(creates, mutations);
        var commitId = result.Creates.Single(c => c.TempId == commitTemp).RealId;

        // idMap entries: a follow-up pass (dict mutation cannot ride CommitBatch — see the doc above),
        // written BEFORE the head advances so the commit is never observably "current" with a partial map.
        var idMapPath = NodePath.Root.Field("commits").Key(commitId.ToString()).Field("idMap");
        foreach (var (path, id) in snap.IdMap)
            store.WriteDictionaryEntry(idMapPath, new TextValue(path), new IntValue(id));

        // The linearization point: advance the main branch's head to the new commit — the LAST write of
        // this action, so a crash before it leaves an orphaned-but-harmless Commit row (in db.commits,
        // not yet anyone's head) rather than a half-written commit.
        store.WriteReference(branch.Id, "head", commitId, "Commit");
    }

    // The design's `main` Branch row (its `workingCopy` reference points at the design) — returned WITH
    // its own intrinsic id (ReadExtent keys the extent by id) so the caller can advance its `head` by id.
    private static (int Id, ObjectValue Fields)? FindMainBranch(IInstanceStore store, int designId)
    {
        foreach (var (id, branch) in store.ReadExtent("Branch"))
            if (branch.Fields.GetValueOrDefault("name") is TextValue { Text: "main" }
                && branch.Fields.GetValueOrDefault("workingCopy") is ReferenceValue { TargetId: var t } && t == designId)
                return (id, branch);
        return null;
    }

    // rename(id, name): update an instance's display label in the registry. arg 0 is the instance id,
    // arg 1 the new label text. The rename delegate updates the live spec and rewrites kernel.json.
    private void Rename(JsonElement args)
    {
        var id = ArgInt(args, 0);
        var name = ArgText(args, 1);
        renameInstance(id, name).GetAwaiter().GetResult();
    }

    // Read a required int argument from the evaluated-args JSON array. Code scalars ship as
    // { type, value }; accept a bare JSON number too (defensive). Anything else is a bad call.
    private static int ArgInt(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
            throw new InvalidOperationException($"host action expects an argument at position {index}.");
        var arg = args[index];
        if (arg.ValueKind == JsonValueKind.Number)
            return arg.GetInt32();
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        throw new InvalidOperationException($"host action expects an integer argument at position {index}.");
    }

    // Read a required text argument from the evaluated-args JSON array. Code scalars ship as
    // { type, value }; accept a bare JSON string too (defensive). Anything else is a bad call.
    private static string ArgText(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
            throw new InvalidOperationException($"host action expects an argument at position {index}.");
        var arg = args[index];
        if (arg.ValueKind == JsonValueKind.String)
            return arg.GetString()!;
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;
        throw new InvalidOperationException($"host action expects a text argument at position {index}.");
    }
}
