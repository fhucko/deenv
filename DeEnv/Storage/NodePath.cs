namespace DeEnv.Storage;

public sealed class NodePath
{
    public static readonly NodePath Root = new([]);

    private NodePath(string[] segments) => Segments = segments;

    public IReadOnlyList<string> Segments { get; }
    public bool IsRoot => Segments.Count == 0;

    public NodePath Field(string name) => new([..Segments, name]);
    public NodePath Key(string key)   => new([..Segments, key]);

    public static NodePath FromSegments(IEnumerable<string> segments)
        => new(segments.ToArray());

    public override string ToString()
        => IsRoot ? "/" : "/" + string.Join("/", Segments);
}
