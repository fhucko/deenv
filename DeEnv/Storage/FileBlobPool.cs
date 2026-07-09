namespace DeEnv.Storage;

// The one IBlobPool implementation: a flat OS directory (see AppPaths.BlobsDirFor*). The ONLY
// filesystem-touching type for blobs — every HTTP edge speaks through the interface, never File.*
// directly (the rung-H IO-seam guard — see IBlobPool's own comment).
public sealed class FileBlobPool(string dir) : IBlobPool
{
    public Stream OpenWrite(string tempName)
    {
        Directory.CreateDirectory(dir);
        return new FileStream(Path.Combine(dir, tempName), FileMode.Create, FileAccess.Write, FileShare.None);
    }

    public string CommitBlob(string tempName, string hash, string ext)
    {
        var finalName = $"{hash}.{ext}";
        var finalPath = Path.Combine(dir, finalName);
        var tempPath = Path.Combine(dir, tempName);
        if (File.Exists(finalPath))
        {
            // Dedup: identical bytes are already pooled under this name (the caller only gets here
            // after hashing what it wrote, so this is a genuine content match, not a guess).
            File.Delete(tempPath);
            return finalName;
        }
        try
        {
            File.Move(tempPath, finalPath);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            // Benign concurrent race: another upload of the SAME bytes committed between our
            // existence check and the move. Our temp is now redundant.
            File.Delete(tempPath);
        }
        return finalName;
    }

    public void DeleteTemp(string tempName)
    {
        var path = Path.Combine(dir, tempName);
        if (File.Exists(path)) File.Delete(path);
    }

    public Stream? OpenRead(string name)
    {
        var path = Path.Combine(dir, name);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public bool Exists(string name) => File.Exists(Path.Combine(dir, name));
}
