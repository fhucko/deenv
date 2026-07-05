using DeEnv.Instance;

namespace DeEnv.Designer;

// The structural, IDENTITY-based diff between two design commits (M13 slice 4 — the MVP payoff:
// a rename finally carries data through a deploy instead of reseeding). Inputs are each commit's cached
// DesignSnapshot (M13 slice 2: canonical printed text + a name-path→intrinsic-id map); this joins the two
// idMaps BY ID — never by name — so a rename (same id, different name-path) is told apart from a genuine
// remove+add (different ids that happen to share a name). Endpoints only: base = the instance's stamped
// commit, target = the design's head commit; edit-path independence falls out for free.
//
// The map keys are "TypeName" (a type-level entry) and "TypeName.propName" (a prop-level entry) — see
// SchemaBridge.Snapshot's own doc. A prop id's OWN identity never changes when its OWNING TYPE is renamed
// (only the type-level entry's name-path changes then), so a prop is reported as renamed only when its
// SIMPLE prop name differs — a path change caused solely by the owning type's rename is not a second,
// spurious prop rename (it is exactly reproduced by applying the type rename alone).
public static class DesignDiffer
{
    // Compute the diff from `base` (the instance's stamped commit) to `target` (the design's head commit).
    // Parses both cached texts (InstanceDescriptionLoader.Load — the same validated loader every app
    // document goes through) and walks each idMap. Throws SchemaValidationException if either cached text
    // fails to parse (a commit's cache is built from an already-validated design, so this should not
    // happen in practice — a defensive, honest failure rather than a silent skip).
    public static DesignDiff Compute(DesignSnapshot @base, DesignSnapshot target)
    {
        var baseDesc = InstanceDescriptionLoader.Load(@base.Text);
        var targetDesc = InstanceDescriptionLoader.Load(target.Text);

        // Invert each idMap: intrinsic id → its CURRENT name-path in that commit ("Type" or "Type.prop").
        var baseById = Invert(@base.IdMap);
        var targetById = Invert(target.IdMap);

        var typeRenames = new List<TypeRename>();
        var propRenames = new List<PropRename>();
        var typeAdds = new List<TypeAdd>();
        var adds = new List<PropAdd>();
        var removes = new List<PropRemove>();
        var typeRemoves = new List<TypeRemove>();
        var conversions = new List<ScalarConvert>();
        var cardinalityChanges = new List<CardinalityChange>();

        var everyId = baseById.Keys.Union(targetById.Keys);
        foreach (var id in everyId)
        {
            var inBase = baseById.TryGetValue(id, out var basePath);
            var inTarget = targetById.TryGetValue(id, out var targetPath);

            if (inBase && inTarget)
            {
                if (basePath == targetPath) continue; // identity, unchanged path — nothing to report

                var baseIsType = !basePath!.Contains('.');
                var targetIsType = !targetPath!.Contains('.');
                if (baseIsType && targetIsType)
                {
                    // A type-level id whose name changed: a TYPE rename.
                    typeRenames.Add(new TypeRename(basePath, targetPath, id));
                }
                else if (!baseIsType && !targetIsType)
                {
                    // A prop-level id. Report a PROP rename only when the prop's OWN simple name changed —
                    // a path change caused purely by the owning type's rename (same simple prop name) is
                    // not a separate event; applying the type rename alone already reproduces it.
                    var (_, baseProp) = SplitPropPath(basePath);
                    var (targetType, targetProp) = SplitPropPath(targetPath);
                    if (baseProp != targetProp)
                        propRenames.Add(new PropRename(targetType, baseProp, targetProp, id));
                }
                // (a type-shaped id becoming prop-shaped, or vice versa, cannot happen — MetaType/MetaProp
                // rows are never re-purposed from one kind to the other.)
                continue;
            }

            if (inTarget && !inBase)
            {
                // Only in the target: an ADD. A type-level id → a whole new type (its props, if any,
                // report as their own per-id adds too — each is independently new). A prop-level id whose
                // OWNING TYPE also only exists in the target is folded into that type add (nothing to
                // migrate cell-by-cell for a type that doesn't exist in the base at all).
                if (!targetPath!.Contains('.'))
                {
                    typeAdds.Add(new TypeAdd(targetPath, id));
                    continue;
                }
                var (owningType, propName) = SplitPropPath(targetPath);
                if (baseDesc.FindType(owningType) is null)
                    continue; // the owning type is ALSO new — covered by the type as a whole
                adds.Add(new PropAdd(owningType, propName, id));
                continue;
            }

            // Only in the base: a REMOVE.
            if (!basePath!.Contains('.'))
            {
                typeRemoves.Add(new TypeRemove(basePath));
                continue;
            }
            var (baseOwningType, baseRemovedProp) = SplitPropPath(basePath);
            if (targetDesc.FindType(baseOwningType) is null)
                continue; // the owning type itself was removed — covered by the type remove, not a per-prop one
            removes.Add(new PropRemove(baseOwningType, baseRemovedProp));
        }

        // Scalar conversions + cardinality reshapes: same-id props present in BOTH commits whose declared
        // shape changed. Computed over the RENAME-RESOLVED prop (the id, not the name, identifies it across
        // an endpoint rename) by reading each side's declared prop off its OWN description via the id→path
        // map, so a renamed-AND-retyped prop is diffed correctly in one pass.
        foreach (var (id, targetPath) in targetById)
        {
            if (targetPath.Contains('.') && baseById.TryGetValue(id, out var basePath) && basePath.Contains('.'))
            {
                var (baseTypeName, baseProp) = SplitPropPath(basePath);
                var (targetTypeName, targetProp) = SplitPropPath(targetPath);
                var baseDef = baseDesc.FindType(baseTypeName)?.Props?.FirstOrDefault(p => p.Name == baseProp);
                var targetDef = targetDesc.FindType(targetTypeName)?.Props?.FirstOrDefault(p => p.Name == targetProp);
                if (baseDef is null || targetDef is null) continue;

                if (baseDef.Cardinality != targetDef.Cardinality)
                    cardinalityChanges.Add(new CardinalityChange(
                        targetTypeName, targetProp, baseDef.Cardinality, targetDef.Cardinality));
                else if (baseDef.Cardinality == Cardinality.Single
                         && !targetDesc.IsObjectType(targetDef.Type)
                         && !baseDesc.IsObjectType(baseDef.Type)
                         && baseDef.Type != targetDef.Type)
                    conversions.Add(new ScalarConvert(targetTypeName, targetProp, baseDef.Type, targetDef.Type));
            }
        }

        return new DesignDiff(typeRenames, propRenames, typeAdds, adds, removes, typeRemoves, conversions, cardinalityChanges);
    }

    private static Dictionary<int, string> Invert(IReadOnlyDictionary<string, int> idMap)
    {
        var byId = new Dictionary<int, string>();
        foreach (var (path, id) in idMap)
            byId[id] = path; // an id is unique to one name-path within a single commit's map, by construction
        return byId;
    }

    // "Type.prop" -> ("Type", "prop"). Only the FIRST dot matters (a prop name itself never contains one).
    private static (string TypeName, string PropName) SplitPropPath(string path)
    {
        var dot = path.IndexOf('.');
        return (path[..dot], path[(dot + 1)..]);
    }
}

// A type whose name changed, same intrinsic id (TypeId — the designer's own MetaType row id).
public sealed record TypeRename(string FromName, string ToName, int TypeId);

// A prop whose simple name changed, same intrinsic id (PropId), reported under the TARGET (current) type
// name — the owning type may itself have been renamed independently.
public sealed record PropRename(string TypeName, string FromProp, string ToProp, int PropId);

public sealed record TypeAdd(string TypeName, int TypeId);

// A prop that exists only in the target, on a type that ALSO exists in the base (a genuine additive
// change on an existing type) — a brand-new type's own props are not reported individually.
public sealed record PropAdd(string TypeName, string PropName, int PropId);

// A prop that existed in the base but not the target, on a type that STILL exists in the target
// (destructive — the stored value is dropped).
public sealed record PropRemove(string TypeName, string PropName);

// A whole type present in the base but absent from the target (destructive — its extent is dropped).
public sealed record TypeRemove(string TypeName);

// A same-id prop whose declared SCALAR type changed (cardinality unchanged, neither side an object
// reference) — the existing ConvertScalar semantics carry the value, defaulting + reporting an
// unconvertible cell.
public sealed record ScalarConvert(string TypeName, string PropName, string FromType, string ToType);

// A same-id prop whose CARDINALITY changed (single/set/dictionary) — the existing apply's reshape
// semantics (today: single object ref -> one-member set) carry what they can; anything else is reported
// as an unsupported reshape, same fallback the current apply has.
public sealed record CardinalityChange(string TypeName, string PropName, Cardinality FromCardinality, Cardinality ToCardinality);

// The whole diff between two design commits, by identity.
public sealed record DesignDiff(
    IReadOnlyList<TypeRename> TypeRenames,
    IReadOnlyList<PropRename> PropRenames,
    IReadOnlyList<TypeAdd> TypeAdds,
    IReadOnlyList<PropAdd> Adds,
    IReadOnlyList<PropRemove> Removes,
    IReadOnlyList<TypeRemove> TypeRemoves,
    IReadOnlyList<ScalarConvert> Conversions,
    IReadOnlyList<CardinalityChange> CardinalityChanges)
{
    public bool IsEmpty =>
        TypeRenames.Count == 0 && PropRenames.Count == 0 && TypeAdds.Count == 0 && Adds.Count == 0 && Removes.Count == 0
        && TypeRemoves.Count == 0 && Conversions.Count == 0 && CardinalityChanges.Count == 0;
}
