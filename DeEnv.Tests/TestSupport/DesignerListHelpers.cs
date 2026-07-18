using DeEnv.Storage;

namespace DeEnv.Tests.TestSupport;

// After Slice 4, designer Meta* trees use list props (not set + order). Tests that mint
// design structure must append via list mutations — AddToSet throws / EnsureSet fails on lists.
public static class DesignerListHelpers
{
    /// <summary>Append an object ref to a list-typed prop path (e.g. designs/7/types).</summary>
    public static void AppendToList(IInstanceStore store, NodePath listPath, int memberId, string elementType)
    {
        var node = store.ReadNode(listPath)
            ?? throw new InvalidOperationException($"No list at {listPath}.");
        if (node is not ListValue list)
            throw new InvalidOperationException($"Expected list at {listPath}, got {node.GetType().Name}.");
        store.CommitBatch([], [
            new ListInsertMutation(list.Id, list.Items.Count, new StoredRef(elementType, memberId)),
        ]);
    }

    /// <summary>Remove first occurrence of memberId from a list-typed prop path.</summary>
    public static void RemoveFromList(IInstanceStore store, NodePath listPath, int memberId)
    {
        var node = store.ReadNode(listPath)
            ?? throw new InvalidOperationException($"No list at {listPath}.");
        if (node is not ListValue list)
            throw new InvalidOperationException($"Expected list at {listPath}, got {node.GetType().Name}.");
        RemoveFromListId(store, list, memberId);
    }

    /// <summary>Append using an already-loaded ListValue (avoids path walk).</summary>
    public static void AppendToListId(IInstanceStore store, ListValue list, int memberId, string elementType) =>
        store.CommitBatch([], [
            new ListInsertMutation(list.Id, list.Items.Count, new StoredRef(elementType, memberId)),
        ]);

    public static IReadOnlyList<int> ObjectMemberIds(ListValue list) =>
        list.Items.OfType<ReferenceValue>()
            .Where(r => r.TargetId is int)
            .Select(r => r.TargetId!.Value)
            .ToList();

    public static void RemoveFromListId(IInstanceStore store, ListValue list, int memberId)
    {
        var idx = -1;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.Items[i] is ReferenceValue { TargetId: int tid } && tid == memberId)
            { idx = i; break; }
        }
        if (idx < 0) return;
        store.CommitBatch([], [new ListRemoveAtMutation(list.Id, idx)]);
    }
}
