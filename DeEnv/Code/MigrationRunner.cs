using DeEnv.Code.Parsing;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Code;

public sealed record MigrationRunReport(int CommitId, string Message, IReadOnlyList<string> Types, int ObjectsMigrated);

public static class MigrationRunner
{
    public static MigrationRunReport Run(
        string source, int commitId, string message,
        StoreDoc oldDoc, InstanceDescription oldDesc,
        StoreDoc newDoc, InstanceDescription newDesc,
        List<LogWrite> writes)
    {
        var fns = Parse.Run(CodeParse.Section("migration"), "migration\n" + IndentForSection(source))
            .OfType<CodeFunction>()
            .ToList();
        if (fns.Count == 0)
            return new MigrationRunReport(commitId, message, [], 0);

        foreach (var fn in fns)
            if (IsDictionaryElementType(newDesc, fn.Name ?? ""))
                throw new InvalidOperationException("dictionary migration not supported yet");

        var context = new ExecContext();
        var oldRoot = DbBridge.LoadRoot(new JsonFileInstanceStore(oldDoc, oldDesc), oldDesc, context);
        var newRoot = DbBridge.LoadRoot(new JsonFileInstanceStore(newDoc, newDesc), newDesc, context);
        var oldById = Index(oldRoot);
        var newById = Index(newRoot);
        var executor = new CodeExecutor();
        var migrated = 0;

        foreach (var fn in fns)
        {
            var typeName = fn.Name ?? throw new InvalidOperationException("Migration function must be named.");
            if (!newDoc.Extents.TryGetValue(typeName, out var extent)) continue;
            var type = newDesc.FindType(typeName)
                ?? throw new InvalidOperationException($"Migration type '{typeName}' is not in the target schema.");

            foreach (var id in extent.Keys.ToList())
            {
                if (!newById.TryGetValue(id, out var newObj))
                    throw new InvalidOperationException($"Migration could not load {typeName}/{id}.");
                var before = Snapshot(newObj);
                var scope = new ExecScope
                {
                    Items =
                    {
                        ["new"] = new ExecScopeItem { Value = newObj, IsReadOnly = true },
                        ["oldDb"] = new ExecScopeItem { Value = oldRoot, IsReadOnly = true },
                    },
                };
                IExecValue oldArg = oldById.TryGetValue(id, out var oldObj) ? oldObj : new ExecNull();
                try
                {
                    executor.InvokeFunction(fn, [oldArg], scope, new ExecContext());
                }
                catch (Exception ex) when (ex is CodeRuntimeException or InvalidOperationException)
                {
                    throw new InvalidOperationException($"migration {commitId} failed: {ex.Message}", ex);
                }
                Harvest(type, id, before, newObj, newDoc, writes);
                migrated++;
            }
        }

        return new MigrationRunReport(commitId, message, [.. fns.Select(f => f.Name!)], migrated);
    }

    private static Dictionary<int, ExecObject> Index(ExecObject root)
    {
        var result = new Dictionary<int, ExecObject>();
        void Walk(IExecValue value)
        {
            switch (value)
            {
                case ExecObject o when !result.TryAdd(o.Id, o):
                    return;
                case ExecObject o:
                    foreach (var child in o.Props.Values) Walk(child);
                    break;
                case ExecArray a:
                    foreach (var item in a.Items) Walk(item.Value);
                    break;
            }
        }
        Walk(root);
        return result;
    }

    private static Dictionary<string, IExecValue> Snapshot(ExecObject obj) =>
        obj.Props.ToDictionary(kv => kv.Key, kv => kv.Value);

    private static void Harvest(
        TypeDefinition type, int id, Dictionary<string, IExecValue> before,
        ExecObject obj, StoreDoc doc, List<LogWrite> writes)
    {
        foreach (var (name, after) in obj.Props)
        {
            if (before.TryGetValue(name, out var oldExec) && SameScalar(oldExec, after)) continue;
            var prop = type.Props?.FirstOrDefault(p => p.Name == name)
                ?? throw new InvalidOperationException($"Migration wrote undeclared field {type.Name}.{name}.");
            if (prop.Cardinality == Cardinality.Dictionary)
                throw new InvalidOperationException("dictionary migration not supported yet");
            if (prop.Cardinality != Cardinality.Single || !BaseTypes.IsName(prop.Type))
                throw new InvalidOperationException($"Migration writes to non-scalar field {type.Name}.{name} are not supported yet.");

            var scalar = ScalarForDeclared(after, prop.Type, type.Name, name);
            var leaf = new StoredLeaf(scalar);
            var stored = doc.Extents[type.Name][id];
            var oldStored = stored.Fields.GetValueOrDefault(name);
            writes.Add(new FieldWrite(id, name, oldStored, leaf));
            stored.Fields[name] = leaf;
        }
    }

    private static bool SameScalar(IExecValue a, IExecValue b) => (a, b) switch
    {
        (ExecInt x, ExecInt y) => x.Value == y.Value,
        (ExecText x, ExecText y) => x.Value == y.Value,
        (ExecBool x, ExecBool y) => x.Value == y.Value,
        (ExecNull, ExecNull) => true,
        _ => ReferenceEquals(a, b),
    };

    private static NodeValue ScalarForDeclared(IExecValue value, string declared, string typeName, string propName)
    {
        if (declared is not ("int" or "text" or "bool"))
            throw new InvalidOperationException($"Migration writes to {typeName}.{propName} type {declared} are not supported yet.");
        return (declared, value) switch
        {
            ("int", ExecInt i) => new IntValue(i.Value),
            ("text", ExecText t) => new TextValue(t.Value),
            ("bool", ExecBool b) => new BoolValue(b.Value),
            _ => throw new InvalidOperationException(
                $"Migration wrote {ValueName(value)} to {typeName}.{propName}, expected {declared}."),
        };
    }

    private static string ValueName(IExecValue value) => value switch
    {
        ExecInt => "Int",
        ExecText => "Text",
        ExecBool => "Bool",
        ExecNull => "Null",
        ExecArray => "collection",
        ExecObject => "object",
        _ => value.GetType().Name,
    };

    private static bool IsDictionaryElementType(InstanceDescription desc, string typeName) =>
        (desc.Types ?? []).Any(t => (t.Props ?? []).Any(p => p.Cardinality == Cardinality.Dictionary && p.Type == typeName));

    private static string IndentForSection(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Length == 0 ? "" : "    " + line));
}
