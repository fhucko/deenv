namespace DeEnv.Storage;

// Where an app's files live. Each app document owns its own data file, named after
// the app file's stem (instance.app → instance-data.json), so switching apps never
// mixes data. Data files always land in the run directory, even when the app
// document itself is given as a rooted path somewhere else.
public static class AppPaths
{
    public static string DataFileNameFor(string appFile) =>
        Path.GetFileNameWithoutExtension(appFile) + "-data.json";

    public static string SchemaPath(string appFile, string baseDir) =>
        Path.IsPathRooted(appFile) ? appFile : Path.Combine(baseDir, appFile);

    public static string DataPath(string appFile, string baseDir) =>
        Path.Combine(baseDir, DataFileNameFor(appFile));
}
