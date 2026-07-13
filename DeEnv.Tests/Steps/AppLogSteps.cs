using System.Text.Json;
using DeEnv.Instance;
using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// AppLog.feature — the append-only changeset log behind the store (M13 slice 1). Every scenario is
// in-process against a JsonFileInstanceStore over a fresh per-scenario TEMP DIRECTORY (the log/genesis
// siblings need a home to sit beside "app-data.json" in, the same production naming AppPaths derives),
// mirroring the StoreConcurrencyTests/AtomicCommitSteps template — no browser, no WS server: this whole
// clump is synchronous in-process, so no polling/fixed sleeps are needed anywhere here.
//
// "Seeded with a note whose title is X" means X is the note's title FROM CONSTRUCTION (baked into the
// fixture's initialData), not written through the store afterward — seeding must not itself be a logged
// mutation, or "genesis holds the ORIGINAL value" scenarios could never observe a value that predates
// every tracked write. The Db-with-Note fixture mirrors ConcurrencyFixtureDb's shape (Db.notes set of
// Note; Note.title text, Note.count int) but with the scenario's own starting title/count baked in.
[Binding]
public sealed class AppLogSteps(InstanceContext ctx)
{
    private string _dataPath = "";
    private string _logPath = "";
    private string _genesisPath = "";

    // A friendly alias ("n") → the seeded Note's real id (always 2 — the fixture's one seeded Note).
    private readonly Dictionary<string, int> _noteIds = new();

    // ── Background: a fresh store over the Db-with-Note fixture ────────────────────────────────────

    [Given("a fresh instance store over the Db-with-Note fixture")]
    public void GivenFreshStore()
    {
        // No note seeded yet at this point — a later "the store is seeded with a note ..." step supplies
        // the actual starting title/count and (re)builds the store over them, since the desired starting
        // values aren't known until that step runs. This Given only proves the fixture SHAPE is available;
        // NoteFixtureStore (below) is what every scenario's seeding step actually calls.
        _noteIds.Clear();
    }

    // Build a fresh store whose seeded Note (id 2) already carries the given title/count — baked into
    // initialData, so it predates every write this scenario goes on to make (the correct baseline for a
    // "genesis holds the ORIGINAL value" assertion). A new temp directory each time keeps the log/genesis
    // siblings isolated per store.
    private void NoteFixtureStore(string title, int count)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deenv-applog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dataPath = Path.Combine(dir, "app-data.json");
        _logPath = AppPaths.LogPathForDataPath(_dataPath);
        _genesisPath = AppPaths.GenesisPathForDataPath(_dataPath);

        ctx.Description = InstanceDescriptionLoader.Load($$"""
            types
                Db
                    notes set of Note
                Note
                    title text
                    count int

            initialData
                Db 1
                    notes: [2]
                Note 2
                    title: "{{title}}"
                    count: {{count}}
            """);
        ctx.Store = new JsonFileInstanceStore(_dataPath, ctx.Description);
        _noteIds["n"] = 2;
    }

    [Given("the store is seeded with a note {string}")]
    public void GivenSeededNoteBare(string alias)
    {
        NoteFixtureStore("Seeded", 0);
        _noteIds[alias] = 2;
    }

    [Given("the store is seeded with a note {string} whose title is {string}")]
    public void GivenSeededNoteTitle(string alias, string title)
    {
        NoteFixtureStore(title, 0);
        _noteIds[alias] = 2;
    }

    [Given("the store is seeded with a note {string} whose title is {string} and count is {int}")]
    public void GivenSeededNoteTitleCount(string alias, string title, int count)
    {
        NoteFixtureStore(title, count);
        _noteIds[alias] = 2;
    }

    private int AliasId(string alias) => _noteIds[alias];

    // ── field writes ─────────────────────────────────────────────────────────────────────────────

    [When("the title of note {string} is written to {string}")]
    public void WhenTitleWritten(string alias, string title) =>
        ctx.Store!.WriteField(AliasId(alias), "title", new TextValue(title));

    [Given("the title of note {string} is written to {string}")]
    public void GivenTitleWritten(string alias, string title) => WhenTitleWritten(alias, title);

    // ── batch commits ────────────────────────────────────────────────────────────────────────────
    //
    // Both steps below capture the log/version BASELINE as their first statement — before the action they
    // perform — so the paired "grew by exactly one" / "did not grow" / "unchanged" Then steps compare a
    // DELTA, not an absolute count that would depend on how many prior Given/seed writes already logged.

    [When("a single commit writes note {string} title to {string} and count to {int}")]
    public void WhenBatchCommit(string alias, string title, int count)
    {
        CaptureBaseline();
        var id = AliasId(alias);
        ctx.Store!.CommitBatch([], [
            new FieldSetMutation(id, "title", new TextValue(title)),
            new FieldSetMutation(id, "count", new IntValue(count)),
        ]);
    }

    [When("an empty commit is applied")]
    public void WhenEmptyCommit()
    {
        CaptureBaseline();
        ctx.Store!.CommitBatch([], []);
    }

    private void CaptureBaseline()
    {
        _logCountBefore = ReadLogLines().Count;
        _versionBefore = ctx.Store!.CurrentVersion;
    }

    [When("three separate writes are committed")]
    public void WhenThreeWrites()
    {
        var id = AliasId("n");
        ctx.Store!.CommitBatch([], [new FieldSetMutation(id, "title", new TextValue("W1"))]);
        ctx.Store!.CommitBatch([], [new FieldSetMutation(id, "title", new TextValue("W2"))]);
        ctx.Store!.CommitBatch([], [new FieldSetMutation(id, "title", new TextValue("W3"))]);
    }

    // ── the log/genesis files, read directly off disk ───────────────────────────────────────────

    private List<JsonElement> ReadLogLines() =>
        File.Exists(_logPath)
            ? File.ReadAllLines(_logPath).Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement).ToList()
            : [];

    private List<LogEntry> ReadRawLogEntries() =>
        File.Exists(_logPath)
            ? File.ReadAllLines(_logPath).Where(l => l.Length > 0)
                .Select(l => JsonSerializer.Deserialize<LogEntry>(l, StoreOpts)!).ToList()
            : [];

    [Then("the log's last entry has seq equal to the store's current version")]
    public async Task ThenLastEntrySeqEqualsVersion()
    {
        var last = ReadLogLines()[^1];
        await Assert.That(last.GetProperty("seq").GetInt32()).IsEqualTo(ctx.Store!.CurrentVersion);
    }

    [Then(@"the log's last entry records a write of note ""([^""]+)"" title from ""([^""]+)"" to ""([^""]+)""")]
    public async Task ThenLastEntryRecordsTitleWrite(string alias, string from, string to)
    {
        var id = AliasId(alias);
        var last = ReadLogLines()[^1];
        var writes = last.GetProperty("writes").EnumerateArray().ToList();
        var hit = writes.FirstOrDefault(w =>
            w.GetProperty("kind").GetString() == "fieldWrite"
            && w.GetProperty("objectId").GetInt32() == id
            && w.GetProperty("prop").GetString() == "title");
        await Assert.That(hit.ValueKind).IsNotEqualTo(JsonValueKind.Undefined);
        await Assert.That(hit.GetProperty("old").GetProperty("value").GetString()).IsEqualTo(from);
        await Assert.That(hit.GetProperty("new").GetProperty("value").GetString()).IsEqualTo(to);
    }

    // Baseline captured by CaptureBaseline() (called from WhenBatchCommit/WhenEmptyCommit, above) —
    // read by the delta-comparing Then steps below.
    private int _logCountBefore;
    private int _versionBefore;

    [Then("the log grew by exactly one entry")]
    public async Task ThenLogGrewByOne() =>
        await Assert.That(ReadLogLines().Count).IsEqualTo(_logCountBefore + 1);

    [Then(@"that entry's writes include title ""([^""]+)"" to ""([^""]+)"" and count (\d+) to (\d+)")]
    public async Task ThenEntryIncludesBothWrites(string titleFrom, string titleTo, int countFrom, int countTo)
    {
        var writes = ReadLogLines()[^1].GetProperty("writes").EnumerateArray().ToList();
        var titleWrite = writes.Single(w => w.GetProperty("kind").GetString() == "fieldWrite"
            && w.GetProperty("prop").GetString() == "title");
        var countWrite = writes.Single(w => w.GetProperty("kind").GetString() == "fieldWrite"
            && w.GetProperty("prop").GetString() == "count");
        await Assert.That(titleWrite.GetProperty("old").GetProperty("value").GetString()).IsEqualTo(titleFrom);
        await Assert.That(titleWrite.GetProperty("new").GetProperty("value").GetString()).IsEqualTo(titleTo);
        await Assert.That(countWrite.GetProperty("old").GetProperty("value").GetInt32()).IsEqualTo(countFrom);
        await Assert.That(countWrite.GetProperty("new").GetProperty("value").GetInt32()).IsEqualTo(countTo);
    }

    [Then("that entry's seq equals the store's current version")]
    public async Task ThenEntrySeqEqualsVersion() =>
        await Assert.That(ReadLogLines()[^1].GetProperty("seq").GetInt32()).IsEqualTo(ctx.Store!.CurrentVersion);

    [Then("the log did not grow")]
    public async Task ThenLogDidNotGrow() => await Assert.That(ReadLogLines().Count).IsEqualTo(_logCountBefore);

    [Then("the store's version is unchanged")]
    public async Task ThenVersionUnchanged() => await Assert.That(ctx.Store!.CurrentVersion).IsEqualTo(_versionBefore);

    // ── crash-repair: roll the snapshot back while the log keeps the entry, then reopen ────────────

    [When("the snapshot on disk is rolled back to before that write while the log keeps it")]
    public void WhenSnapshotRolledBack()
    {
        // Reconstruct the doc as it stood BEFORE the log's last entry (genesis replayed through every
        // entry except the last), and write THAT over the current, fully-caught-up "app-data.json" — the
        // log itself is left untouched, exactly simulating a crash that appended the entry but died before
        // its snapshot rewrite (the WAL's fixed append-then-snapshot order — see JsonFileInstanceStore.Save).
        var logEntries = ReadRawLogEntries();
        var genesis = JsonSerializer.Deserialize<GenesisFile>(File.ReadAllText(_genesisPath), StoreOpts)!;
        var rolledBackDoc = logEntries.Take(logEntries.Count - 1).Aggregate(genesis.Db, AppLogReplay.Apply);
        File.WriteAllText(_dataPath, JsonSerializer.Serialize(rolledBackDoc, StoreOpts));
    }

    private JsonFileInstanceStore? _reopenedStore;

    [When("a new store is opened over the same files")]
    public void WhenReopened() => _reopenedStore = new JsonFileInstanceStore(_dataPath, ctx.Description!);

    [Then("the reopened store reads note {string} title as {string}")]
    public async Task ThenReopenedTitleIs(string alias, string expected)
    {
        var hit = _reopenedStore!.ReadById(AliasId(alias));
        await Assert.That(hit).IsNotNull();
        var title = hit!.Value.Fields.Fields["title"];
        await Assert.That(title is TextValue { Text: var t } && t == expected).IsTrue();
    }

    [Then("the snapshot on disk again matches the log head")]
    public async Task ThenSnapshotMatchesLogHead() => await Assert.That(_reopenedStore!.Fsck()).IsTrue();

    // ── genesis ──────────────────────────────────────────────────────────────────────────────────

    [Then("genesis on disk still holds note {string} title as {string}")]
    public async Task ThenGenesisHoldsTitle(string alias, string expected)
    {
        var genesis = JsonSerializer.Deserialize<GenesisFile>(File.ReadAllText(_genesisPath), StoreOpts)!;
        var id = AliasId(alias);
        StoredObject? entry = null;
        foreach (var pool in genesis.Db.Extents.Values)
            if (pool.TryGetValue(id, out var e)) entry = e;
        await Assert.That(entry).IsNotNull();
        var title = entry!.Fields["title"];
        await Assert.That(title is StoredLeaf { Scalar: TextValue { Text: var t } } && t == expected).IsTrue();
    }

    [Then("genesis records the seq the log began from")]
    public async Task ThenGenesisRecordsSeq()
    {
        var genesis = JsonSerializer.Deserialize<GenesisFile>(File.ReadAllText(_genesisPath), StoreOpts)!;
        var firstEntrySeq = ReadLogLines()[0].GetProperty("seq").GetInt32();
        // Genesis's seq is the version BEFORE the first entry's writes landed — strictly less than the
        // first entry's own seq (a fresh store: genesis 0, first entry seq 1).
        await Assert.That(genesis.GenesisSeq).IsLessThan(firstEntrySeq);
    }

    // ── replay: genesis→head reproduces the live snapshot exactly ──────────────────────────────────

    [Given("the store is seeded with several notes")]
    public void GivenSeveralNotes()
    {
        // Switches to a richer fixture (Db.people set of Person, Db.lead Person, Db.notes set of Note,
        // Note.author Person) — the Db-with-Note fixture alone has no single-ref field to exercise "a
        // reference set" with. Reuses the shared SelfHostedRefDb fixture (InstanceContext) rather than
        // inventing yet another shape. Seeded: Person 2 "Ada", Person 3 "Grace", Note 4 "First note",
        // Note 5 "Authored note" (author 2).
        var dir = Path.Combine(Path.GetTempPath(), "deenv-applog-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dataPath = Path.Combine(dir, "app-data.json");
        _logPath = AppPaths.LogPathForDataPath(_dataPath);
        _genesisPath = AppPaths.GenesisPathForDataPath(_dataPath);
        ctx.Description = InstanceContext.SelfHostedRefDb();
        ctx.Store = new JsonFileInstanceStore(_dataPath, ctx.Description);
    }

    [Given("a mix of edits, a create, a set link, a set removal, and a reference set are committed")]
    public void GivenReplayMix()
    {
        // edit: rename a seeded Person.
        ctx.Store!.WriteField(2, "name", new TextValue("Ada Lovelace"));

        // create + set link: a fresh Person, linked into Db.people (CommitBatch mints + links in one go).
        var setId = FindDbSetId("people");
        ctx.Store.CommitBatch(
            [new CommitCreate(-1, "Person",
                new ObjectValue(new Dictionary<string, NodeValue> { ["name"] = new TextValue("Alan") }))],
            [new SetAddMutation(setId, -1)]);

        // set removal: drop Person 3 out of Db.people.
        ctx.Store.RemoveFromSet(setId, 3);

        // reference set: point Db.lead at Person 2.
        ctx.Store.WriteReference(1, "lead", 2, "Person");
    }

    private int FindDbSetId(string prop)
    {
        var hit = ctx.Store!.ReadById(1)!;
        var set = (SetValue)hit.Value.Fields.Fields[prop];
        return set.Id;
    }

    private Db? _replayed;

    [When("the log is replayed from genesis to head")]
    public void WhenReplayed()
    {
        var genesis = JsonSerializer.Deserialize<GenesisFile>(File.ReadAllText(_genesisPath), StoreOpts)!;
        _replayed = ReadRawLogEntries().Aggregate(genesis.Db, AppLogReplay.Apply);
    }

    [Then("the replayed data equals the live snapshot on disk")]
    public async Task ThenReplayedEqualsLive()
    {
        var live = JsonSerializer.Deserialize<Db>(File.ReadAllText(_dataPath), StoreOpts)!;
        await Assert.That(AppLogReplay.Equivalent(_replayed!, live)).IsTrue();
        // Also the store's own fsck over the SAME live files — the exact invariant JsonFileInstanceStore
        // exposes as its public API (Fsck()), not just this step's own hand-rolled replay above.
        await Assert.That(((JsonFileInstanceStore)ctx.Store!).Fsck()).IsTrue();
    }

    // ── seq/version monotonicity ────────────────────────────────────────────────────────────────

    [Then("each new log entry's seq is one greater than the previous")]
    public async Task ThenSeqsMonotonic()
    {
        var seqs = ReadLogLines().Select(e => e.GetProperty("seq").GetInt32()).ToList();
        for (var i = 1; i < seqs.Count; i++)
            await Assert.That(seqs[i]).IsEqualTo(seqs[i - 1] + 1);
    }

    [Then("the final entry's seq equals the store's current version")]
    public async Task ThenFinalSeqEqualsVersion() =>
        await Assert.That(ReadLogLines()[^1].GetProperty("seq").GetInt32()).IsEqualTo(ctx.Store!.CurrentVersion);

    // ── cross-restart baseVersion-guard integrity for a set-link member advance ───────────────────────

    private int _staleBase;

    [Given("the store version is remembered as a stale base")]
    public void GivenRememberStaleBase() => _staleBase = ctx.Store!.CurrentVersion;

    [When("note {string} is linked into its set by a batch")]
    public void WhenLinkNoteIntoSet(string alias)
    {
        // A pure SET LINK of the (already-member) note: advances THAT note's version via a lone SetLink
        // log write — no FieldWrite/Create for it. Post-restart this member advance is DISJOINT from a field
        // edit (set ops commute), so a later same-object title edit AUTO-MERGES (the auto-merge scenario).
        var setId = FindDbSetId("notes");
        ctx.Store!.CommitBatch([], [new SetAddMutation(setId, AliasId(alias))]);
    }

    [When("note {string} title is changed by a batch")]
    public void WhenChangeNoteTitle(string alias)
    {
        // A FIELD write on the note: advances THAT note's title version via a FieldWrite log entry — the
        // durable attribution the boot rebuild must restore so a later stale SAME-FIELD edit is caught.
        ctx.Store!.CommitBatch([], [new FieldSetMutation(AliasId(alias), "title", new TextValue("Interleaved edit"))]);
    }

    [Then("a commit editing note {string} title at the remembered stale base is rejected as a conflict")]
    public async Task ThenStaleTitleEditConflicts(string alias)
    {
        // The reopened store (the shared "a new store is opened over the same files" step) must reject: its
        // rebuilt map has to know the member's title advanced past _staleBase via the FieldWrite, so a stale
        // same-field commit is a same-field COLLISION — a ConflictException (a StaleBaseException subclass).
        var store = _reopenedStore!;
        await Assert.That(() =>
            store.CommitBatch(
                [], [new FieldSetMutation(AliasId(alias), "title", new TextValue("Stale edit"))], _staleBase))
            .Throws<ConflictException>();
    }

    [Then("a commit editing note {string} title at the remembered stale base is accepted")]
    public async Task ThenStaleTitleEditAutoMerges(string alias)
    {
        // A field edit after a pure SET LINK of the same object AUTO-MERGES across a restart: the set-link
        // membership change is disjoint from the title field, so no conflict — the commit applies.
        var store = _reopenedStore!;
        store.CommitBatch(
            [], [new FieldSetMutation(AliasId(alias), "title", new TextValue("Merged edit"))], _staleBase);
        await Assert.That(store.ReadById(AliasId(alias))!.Value.Fields.Fields["title"])
            .IsEqualTo((NodeValue)new TextValue("Merged edit"));
    }

    // ── shared JSON options for reading the log/genesis files exactly as the store writes them ─────

    private static readonly JsonSerializerOptions StoreOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new StoredValueConverter(), new LogWriteConverter() },
    };
}
