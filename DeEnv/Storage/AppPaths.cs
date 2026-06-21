namespace DeEnv.Storage;

// Where a hosted instance's files live. Storage is FULLY ID-BASED: every instance is found purely
// by its kernel id, never by the registry `app` field (which is a display NAME label, used for
// nothing functional). An instance's app document and its co-located sovereign store both live under
// instances/<id>/ — schema at instances/<id>/app.app, data at instances/<id>/app-data.json — so two
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

    // The app document for an instance id: <baseDir>/instances/<id>/app.app. The file name is the
    // fixed "app.app" for every instance — the id-dir, not the file name, is what distinguishes one
    // instance from another (so the `app` registry field can be a pure display label).
    public static string SchemaPathForId(string baseDir, int id) =>
        Path.Combine(IdDirFor(baseDir, id), "app.app");

    // The data file for an instance id: <baseDir>/instances/<id>/app-data.json, co-located with the
    // app document in the same id-dir. Derived from the id alone — never from the `app` name.
    public static string DataPathForId(string baseDir, int id) =>
        Path.Combine(IdDirFor(baseDir, id), "app-data.json");
}
