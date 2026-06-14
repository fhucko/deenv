namespace DeEnv.Storage;

// Where an app's files live. Each app document owns its own data file, named after
// the app file's stem (instance.app → instance-data.json) and CO-LOCATED with the
// resolved app document's own directory, so switching apps never mixes data and a
// created instance's data lives beside its app doc (in its id-dir) — which is what
// lets it resolve correctly on a kernel restart. For a baseDir-relative app (every
// registry entry today) the data still lands in baseDir, identical to before; it
// only differs for an app doc in a subdir or at a rooted path elsewhere.
public static class AppPaths
{
    public static string DataFileNameFor(string appFile) =>
        Path.GetFileNameWithoutExtension(appFile) + "-data.json";

    public static string SchemaPath(string appFile, string baseDir) =>
        Path.IsPathRooted(appFile) ? appFile : Path.Combine(baseDir, appFile);

    public static string DataPath(string appFile, string baseDir) =>
        Path.Combine(
            Path.GetDirectoryName(SchemaPath(appFile, baseDir))!,
            DataFileNameFor(appFile));

    // The directory holding created instances, and the relative app-document path for a
    // created instance's id (forward-slashed so it reads the same in the registry on any OS).
    public static string InstancesDir(string baseDir) => Path.Combine(baseDir, "instances");
    public static string CreatedAppRelative(int id) => $"instances/{id}/app.app";

    // The on-disk directory for a created instance's id (<baseDir>/instances/<id>/), which holds its
    // app document AND its co-located sovereign store. Deleting an instance removes this whole
    // directory — the kernel's id→location bookkeeping, an OS concern, not an IInstanceStore op.
    public static string IdDirFor(string baseDir, int id) => Path.Combine(InstancesDir(baseDir), id.ToString());
}
