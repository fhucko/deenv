using System.Text.Json;
using DeEnv.Designer;
using DeEnv.Http;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Kernel;

// The kernel's implementation of the host-action seam for ONE hosted instance. Its actions either
// project a DESIGN (passed by its id — the designer passes one `Design` out of its `db.designs`
// set) into an app document, or address an instance by its id:
//   • publish(design, targetId, dryRun?) — VERSIONED when the design has a committed head (M13 slice 4):
//     diffs the target's STAMPED commit against the design's HEAD commit by IDENTITY (renames carry data
//     — see DesignDiff), materializes the changeset as ONE boundary-marked log entry (history preserved),
//     stamps the target to the new head, and returns a structured PublishReport. UNVERSIONED fallback when
//     the design has no commits yet, or the target was never stamped (one-time name-match apply, then
//     stamped) — the pre-slice-4 by-name apply (SchemaBridge.WriteDocument).
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
    Func<int, string, Task> renameInstance,
    // M13 slice 4 — the instance's versioning stamp (kernel.json's RegistryEntry.PublishedCommitId): read
    // the CURRENT stamp for a target id (null = unstamped), and persist a NEW one after a successful
    // versioned publish. Mirrors recordDesign's shape (a plain per-target registry read/write pair).
    Func<int, int?> readPublishedCommitId,
    Func<int, int, Task> stampPublishedCommit) : IHostActions
{
    public object? Run(string action, JsonElement args) => action switch
    {
        "publish"      => Publish(args),
        "create"       => Create(args),
        "cloneInstance" => Clone(args),
        "delete"       => Delete(args),
        "setDesign"    => SetDesign(args),
        "rename"       => Rename(args),
        "commitDesign" => CommitDesign(args),
        _ => throw new InvalidOperationException($"Unknown host action '{action}'."),
    };

    // publish(design, targetId, dryRun?): project the design's committed HEAD (never its working copy —
    // design doc §4) onto an EXISTING target. arg 0 the design object's id (resolved against the caller's
    // store — see ResolveDesign), arg 1 the target id, arg 2 an OPTIONAL bare bool (default false) — a
    // dry run computes the SAME report and changes NOTHING.
    //
    // VERSIONED path (the design has ≥1 commit): diff the target's stamped commit (or, if unstamped, treat
    // the diff as "everything is new" and fall back — see below) against the design's HEAD commit BY
    // IDENTITY (DesignDiff), materialize the changeset as one boundary-marked log entry
    // (JsonFileInstanceStore.ApplyPublishBoundary — history preserved, unlike the unversioned re-baseline),
    // stamp the target to the new head, and return the structured PublishReport.
    //
    // UNVERSIONED fallback (the design has NO commits yet, OR the target has never been stamped): the
    // pre-slice-4 by-name apply (SchemaBridge.ProjectDesignDocument + WriteDocument) — a target's FIRST
    // versioned publish after this slice lands falls back exactly once (`fallbackNameMatched: true`), then
    // gets stamped so every publish after that is rename-safe.
    private PublishReport Publish(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var design = ResolveDesign(designId);
        var targetId = ArgInt(args, 1);
        var dryRun = ArgBoolOptional(args, 2, defaultValue: false);
        var target = resolveTarget(targetId)
            ?? throw new InvalidOperationException(
                $"No instance with id {targetId} to publish to.");

        var meta = InstanceDescriptionLoader.LoadFile(metaAppPath);
        var store = new JsonFileInstanceStore(dataPath, meta);
        var head = FindHeadCommit(store, designId);

        if (head is null)
        {
            // No commit exists for this design yet — nothing to diff against. The pre-slice-4 behavior:
            // project the CURRENT working copy and apply by name. Not reported as a "fallback" (that term
            // is reserved for an unstamped TARGET against a design that DOES have commits) — there is no
            // identity diff possible here at all.
            var workingDoc = SchemaBridge.ProjectDesignDocument(design); // throws on an invalid design
            if (!dryRun)
            {
                SchemaBridge.WriteDocument(workingDoc, target.SchemaPath, target.DataPath);
                _ = restartInstance(targetId);
            }
            return new PublishReport
            {
                Applied = !dryRun, DryRun = dryRun, BaseCommit = null, TargetCommit = 0,
                UncommittedDrift = false, Renames = [], Adds = [], Removes = [], Conversions = [],
                Cardinality = [], FallbackNameMatched = false,
            };
        }

        var (headCommitId, headFields) = head.Value;
        var headText = TextOf(headFields, "text");
        var headIdMap = IdMapOf(headFields);
        var headSnapshot = new DesignSnapshot(headText, headIdMap);

        // Uncommitted working-copy drift: the design's LIVE state may have moved past its own head commit
        // (an edit made after the last commit) — Snapshot(workingCopy).Text != head.text. Reported, never
        // published: publish always deploys the committed head, never the working copy.
        var workingSnapshot = SchemaBridge.Snapshot(design); // throws on an invalid design
        var uncommittedDrift = workingSnapshot.Text != headText;

        var stampedCommitId = readPublishedCommitId(targetId);
        var stampedFields = stampedCommitId is { } stamped ? FindCommit(store, stamped) : null;

        if (stampedFields is null)
        {
            // Unstamped (or a stamp naming a commit this store no longer has — defensive): the one-time
            // name-match fallback — the pre-slice-4 by-name apply (WriteDocument), carrying whatever a
            // by-name apply can, then stamping so the NEXT publish is identity-diffed and rename-safe.
            if (!dryRun)
            {
                SchemaBridge.WriteDocument(headText, target.SchemaPath, target.DataPath);
                stampPublishedCommit(targetId, headCommitId).GetAwaiter().GetResult();
                _ = restartInstance(targetId);
            }
            return new PublishReport
            {
                Applied = !dryRun, DryRun = dryRun, BaseCommit = null, TargetCommit = headCommitId,
                UncommittedDrift = uncommittedDrift, Renames = [], Adds = [], Removes = [], Conversions = [],
                Cardinality = [], FallbackNameMatched = true,
            };
        }

        // ── the versioned path: diff the STAMPED commit against the HEAD commit by identity ──
        var baseSnapshot = new DesignSnapshot(TextOf(stampedFields, "text"), IdMapOf(stampedFields));
        var diff = DesignDiffer.Compute(baseSnapshot, headSnapshot);
        var targetDesc = InstanceDescriptionLoader.Load(headText);

        // Compute the boundary plan EVEN ON A DRY RUN — one code path for both (ApplyPublishBoundary's own
        // `dryRun` flag skips its two disk-touching side effects, so a preview reports the SAME
        // unconvertible/unsupported cells a real publish would produce, never a second implementation of
        // the same conversion rules that could drift from the real one).
        var boundaryResult = diff.IsEmpty
            ? new BoundaryApplyResult(false, [], [])
            : JsonFileInstanceStore.ApplyPublishBoundary(
                target.DataPath, diff, targetDesc, new BoundaryMarker(designId, headCommitId), dryRun);

        var report = BuildReport(diff, boundaryResult, applied: !dryRun, dryRun, stampedCommitId, headCommitId,
            uncommittedDrift, fallbackNameMatched: false);

        if (dryRun) return report; // prove-it: nothing below ran — no file write, no log, no stamp, no restart

        // Write the target's app document FROM the commit's cached text (the publish artifact) — always,
        // even when the diff itself is empty (the design may have gained sections/commits with no
        // structural change; the target must still run the head's exact document).
        File.WriteAllText(target.SchemaPath, headText);

        stampPublishedCommit(targetId, headCommitId).GetAwaiter().GetResult();
        _ = restartInstance(targetId);

        return report;
    }

    private static PublishReport BuildReport(
        DesignDiff diff, BoundaryApplyResult boundaryResult, bool applied, bool dryRun, int? baseCommit,
        int targetCommit, bool uncommittedDrift, bool fallbackNameMatched) => new()
    {
        Applied = applied,
        DryRun = dryRun,
        BaseCommit = baseCommit,
        TargetCommit = targetCommit,
        UncommittedDrift = uncommittedDrift,
        Renames = [
            .. diff.TypeRenames.Select(r => new RenameReportItem(r.FromName, r.ToName)),
            .. diff.PropRenames.Select(r => new RenameReportItem($"{r.TypeName}.{r.FromProp}", $"{r.TypeName}.{r.ToProp}")),
        ],
        Adds = [.. diff.Adds.Select(a => $"{a.TypeName}.{a.PropName}")],
        Removes = [
            .. diff.Removes.Select(r => new RemoveReportItem($"{r.TypeName}.{r.PropName}")),
            .. diff.TypeRemoves.Select(r => new RemoveReportItem(r.TypeName)),
        ],
        Conversions = [.. diff.Conversions.Select(c =>
        {
            var path = $"{c.TypeName}.{c.PropName}";
            var unconvertible = boundaryResult.UnconvertibleCells.Where(cell => CellMatches(cell, c.TypeName, c.PropName)).ToList();
            return new ConversionReportItem(path, c.FromType, c.ToType, unconvertible);
        })],
        Cardinality = [.. diff.CardinalityChanges.Select(c =>
        {
            var unsupported = boundaryResult.UnsupportedReshapes.Any(cell => CellMatches(cell, c.TypeName, c.PropName));
            return new CardinalityReportItem(
                $"{c.TypeName}.{c.PropName}", c.FromCardinality.ToString(), c.ToCardinality.ToString(), unsupported);
        })],
        FallbackNameMatched = fallbackNameMatched,
    };

    // Whether a BoundaryApplyResult cell string ("TypeName/objectId.propName" — see
    // JsonFileInstanceStore.ApplyPublishBoundary's unconvertibleCells/unsupportedReshapes) belongs to
    // exactly this (typeName, propName) pair. An EXACT split (never EndsWith/StartsWith substring
    // matching), so a prop named e.g. "qty" is never conflated with one named "bigQty" on the same type.
    private static bool CellMatches(string cell, string typeName, string propName)
    {
        var slash = cell.IndexOf('/');
        if (slash < 0 || cell[..slash] != typeName) return false;
        var dot = cell.IndexOf('.', slash + 1);
        return dot >= 0 && cell[(dot + 1)..] == propName;
    }

    // setDesign(design, targetId): the IDE's "Apply" — record (in the registry) that the target now runs
    // the passed design AND deploy it. arg 0 is the design object's id (a member of the caller's
    // `db.designs`, resolved against the caller's store), arg 1 the target instance id. Project FIRST so
    // an invalid design throws before any registry write or document overwrite (records nothing, writes
    // nothing); then record the reference (the kernel rewrites kernel.json's designId + refreshes the live
    // view); then write the projected doc, PRESERVING the target's data — exactly Publish (non-destructive
    // apply), with the registry write in front. An unknown target id is rejected before any work.
    private object? SetDesign(JsonElement args)
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
        return null;
    }

    // create(design, name): project the PASSED design into a NEW instance with the given display NAME —
    // the sibling of publish (spawn rather than replace). arg 0 is the design object id, arg 1 the
    // display name (which ALSO becomes the mount path `/apps/<name>` — addressing is by path now, so
    // there are NO port args). ProjectDesignDocument validates the design first (throws, spawning
    // nothing, on an invalid one); then the kernel create delegate writes + loads it, recording the
    // design's id on the new instance's registry entry (so its dropdown pre-selects that design). The
    // delegate is async; we block on it (the WS dispatch is synchronous, no synchronization context to
    // deadlock on — a single-operator devops action).
    private object? Create(JsonElement args)
    {
        var designId = ArgInt(args, 0);
        var design = ResolveDesign(designId);
        var name = ArgText(args, 1);

        var appDoc = SchemaBridge.ProjectDesignDocument(design); // throws on an invalid design
        createInstance(appDoc, name, designId).GetAwaiter().GetResult();
        return null;
    }

    // cloneInstance(sourceId): copy an existing instance (app doc + data) into a NEW one — the
    // data-carrying sibling of create. arg 0 is the SOURCE instance id (a bare int, not a schema
    // object); there are NO port args (the clone gets a unique mount name derived from the source —
    // addressing is by path). The kernel clone delegate resolves the id and copies the files; an
    // unknown id throws (surfaced as the reject). Blocked on for the same reason as create.
    private object? Clone(JsonElement args)
    {
        var sourceId = ArgInt(args, 0);
        cloneInstance(sourceId).GetAwaiter().GetResult();
        return null;
    }

    // delete(targetId): remove an existing instance. arg 0 is the instance id (a bare int). The kernel
    // delete delegate resolves the id and stops + forgets it (removing its id-dir + data); an unknown
    // id throws, surfaced as the reject. Blocked on like create/clone.
    private object? Delete(JsonElement args)
    {
        var id = ArgInt(args, 0);
        deleteInstance(id).GetAwaiter().GetResult();
        return null;
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
    // Atomicity: the WHOLE commit is ONE CommitBatch (review fix 3) — the Commit row create + its
    // `design`/`parent` ref-links + its link into `db.commits` + EVERY idMap dict entry + the branch-head
    // advance, all-or-none under the store's single lock and ONE log entry (a design commit IS a single
    // atomic changeset in the data log). The idMap rides the batch via DictWriteMutation (server-side-only
    // vocabulary — see its doc); there is no longer a follow-up write phase and thus no crash window where
    // a commit is observable in db.commits with a partial idMap or no head. The batch's own all-or-none
    // guarantee (throws untouched-on-failure) is the linearization point — atomicity is structural now.
    private object? CommitDesign(JsonElement args)
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

        var commitsSetId = (store.ReadNode(NodePath.Root.Field("commits")) as SetValue)?.Id
            ?? throw new InvalidOperationException("The designer's `db.commits` set is missing.");

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
        // The whole changeset, referencing the not-yet-minted commit by its tempId (the store resolves it
        // after minting): design + parent refs, the db.commits link, one dict write per idMap entry, and
        // the branch-head advance — issued as ONE batch so the commit is never observable half-written.
        var mutations = new List<CommitMutation>
        {
            new RefLinkMutation(commitTemp, "design", designId, "Design"),
        };
        if (parentHeadId.HasValue)
            mutations.Add(new RefLinkMutation(commitTemp, "parent", parentHeadId, "Commit"));
        mutations.Add(new SetLinkMutation(commitsSetId, commitTemp));
        foreach (var (path, id) in snap.IdMap)
            mutations.Add(new DictWriteMutation(commitTemp, "idMap", new TextValue(path), new IntValue(id)));
        mutations.Add(new RefLinkMutation(branch.Id, "head", commitTemp, "Commit"));

        store.CommitBatch(creates, mutations);
        return null;
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

    // The design's `main` Branch's HEAD commit — its (id, fields), or null when the branch has no head yet
    // (a design with zero commits — the pre-slice-4 fallback path in Publish). Reads the branch, then
    // resolves its `head` reference into the Commit extent.
    private static (int Id, ObjectValue Fields)? FindHeadCommit(IInstanceStore store, int designId)
    {
        if (FindMainBranch(store, designId) is not { } branch) return null;
        if (branch.Fields.Fields.GetValueOrDefault("head") is not ReferenceValue { TargetId: { } headId }) return null;
        return FindCommit(store, headId) is { } fields ? (headId, fields) : null;
    }

    // A Commit row by its own intrinsic id, or null if no such commit exists (a stale stamp naming a
    // commit this store no longer has — defensive, since Commit rows are never deleted by any grant).
    private static ObjectValue? FindCommit(IInstanceStore store, int commitId) =>
        store.ReadById(commitId) is (var typeName, var fields) && typeName == "Commit" ? fields : null;

    private static string TextOf(ObjectValue fields, string prop) =>
        fields.Fields.GetValueOrDefault(prop) is TextValue t ? t.Text : "";

    // The Commit's cached idMap dict field ("name-path" → intrinsic id) reconstructed into the plain
    // Dictionary<string,int> DesignSnapshot/DesignDiffer expect.
    private static IReadOnlyDictionary<string, int> IdMapOf(ObjectValue fields)
    {
        var map = new Dictionary<string, int>();
        if (fields.Fields.GetValueOrDefault("idMap") is DictionaryValue dict)
            foreach (var (key, value) in dict.Entries)
                if (key is TextValue { Text: var path } && value is IntValue { Value: var id })
                    map[path] = id;
        return map;
    }

    // rename(id, name): update an instance's display label in the registry. arg 0 is the instance id,
    // arg 1 the new label text. The rename delegate updates the live spec and rewrites kernel.json.
    private object? Rename(JsonElement args)
    {
        var id = ArgInt(args, 0);
        var name = ArgText(args, 1);
        renameInstance(id, name).GetAwaiter().GetResult();
        return null;
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

    // Read an OPTIONAL bare bool argument (M13 slice 4 — publish's `dryRun`): a call that omits the
    // argument gets `defaultValue`, matching "minimal by default" (dryRun is opt-in). Code scalars ship as
    // { type, value }; a bare JSON bool is accepted too (defensive, matching ArgInt/ArgText's leniency).
    private static bool ArgBoolOptional(JsonElement args, int index, bool defaultValue)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
            return defaultValue;
        var arg = args[index];
        if (arg.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return arg.GetBoolean();
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("value", out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return v.GetBoolean();
        throw new InvalidOperationException($"host action expects a boolean argument at position {index}.");
    }
}
