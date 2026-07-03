namespace DeEnv.Storage;

// Where a hosted instance's files live. Storage is FULLY ID-BASED: every instance is found purely
// by its kernel id, never by the registry `app` field (which is a display NAME label, used for
// nothing functional). An instance's app document and its co-located sovereign store both live under
// instances/<id>/ — schema at instances/<id>/app.deenv, data at instances/<id>/app-data.json — so two
// instances are kept apart by id alone, which is the slice's data-sovereignty guarantee, and an
// instance survives a kernel restart because its id-dir is its stable, id-addressed home.
public static class AppPaths
{
    // The directory holding every hosted instance (<baseDir>/instances/) and one instance's id-dir
    // (<baseDir>/instances/<id>/), which holds its app document AND its co-located sovereign store.
    // Deleting an instance removes its whole id-dir — the kernel's id→location bookkeeping, an OS
    // concern, not an IInstanceStore op (the store speaks paths/nodes, not "drop my backing dir").
    public static string InstancesDir(string baseDir) => Path.Combine(baseDir, "instances");
    public static string IdDirFor(string baseDir, int id) => Path.Combine(InstancesDir(baseDir), id.ToString());

    // The app document for an instance id: <baseDir>/instances/<id>/app.deenv. The file name is the
    // fixed "app.deenv" for every instance — the id-dir, not the file name, is what distinguishes one
    // instance from another (so the `app` registry field can be a pure display label).
    public static string SchemaPathForId(string baseDir, int id) =>
        Path.Combine(IdDirFor(baseDir, id), "app.deenv");

    // The data file for an instance id: <baseDir>/instances/<id>/app-data.json, co-located with the
    // app document in the same id-dir. Derived from the id alone — never from the `app` name.
    public static string DataPathForId(string baseDir, int id) =>
        Path.Combine(IdDirFor(baseDir, id), "app-data.json");

    // The append-only changeset log and frozen genesis snapshot that ride BESIDE a data file (M13 slice 1
    // — DECISIONS.md "App versioning — the full design (M13 clump)", variant C). Derived by SUFFIX from
    // the data file's own path (one rule, no id/dir special-casing) so a bare temp-file store used
    // directly by a test (never routed through DataPathForId) still gets siblings that cannot collide with
    // another store in the same directory: "<dir>\<name>.json" → "<dir>\<name>.log.jsonl" +
    // "<dir>\<name>.genesis.json". Production: "instances/<id>/app-data.json" →
    // "instances/<id>/app-data.log.jsonl" + "instances/<id>/app-data.genesis.json".
    public static string LogPathForDataPath(string dataPath) => WithSuffix(dataPath, ".log.jsonl");
    public static string GenesisPathForDataPath(string dataPath) => WithSuffix(dataPath, ".genesis.json");

    public static string LogPathForId(string baseDir, int id) => LogPathForDataPath(DataPathForId(baseDir, id));
    public static string GenesisPathForId(string baseDir, int id) => GenesisPathForDataPath(DataPathForId(baseDir, id));

    // Strip the data file's own extension (usually ".json") and append the sibling's, so
    // "app-data.json" → "app-data" + suffix, not "app-data.json" + suffix.
    private static string WithSuffix(string dataPath, string suffix)
    {
        var dir = Path.GetDirectoryName(dataPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(dataPath);
        return Path.Combine(dir, stem + suffix);
    }
}
