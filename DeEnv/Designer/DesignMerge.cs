using DeEnv.Code;
using DeEnv.Instance;

namespace DeEnv.Designer;

// The lineage-keyed three-way structural merge between two design branches (M13 slice 5). Inputs are
// three commits' cached DesignSnapshot.Text (base = the max-logSeq common ancestor, source/target = the
// two heads being merged) — the SAME per-commit cache slice 2/4 already build, so merge computes with
// zero replay, exactly like publish's identity diff.
//
// LINEAGE (not raw row id) is the join key here — unlike DesignDiffer (which diffs two commits of the
// SAME design lineage, so a MetaType/MetaProp's own row id IS its identity across the diff). A branch
// clone (sys.createBranch) mints FRESH MetaType/MetaProp/Design rows with a NEW id but records an
// `origin` pointing back at the row it was cloned from (flattened — see KernelHostActions.CreateBranch's
// own doc), so base/source/target may all be talking about the SAME logical prop through THREE different
// physical row ids. `lineageOf` resolves a row id to its lineage anchor by reading the CURRENT live
// store (the row's `origin` field if the row still exists there and is non-zero, else the row id itself)
// — a defensive, current-state read: a row referenced only by an old commit's IdMap, since removed from
// the design and GC'd, is not itself a data-integrity problem (see the type's own doc), it just anchors
// to itself (the only lineage it can still name).
public static class DesignMerger
{
    // A type row keyed by lineage, its definition plus its position in that commit's idMap (creation
    // order — the tiebreak OrderBySpine renormalizes against).
    private sealed record TypeRow(TypeDefinition Type, int Order);

    // A prop row keyed by lineage, its definition, its OWNING type's lineage, and its idMap position.
    private sealed record PropRow(PropDefinition Prop, int OwningTypeLineage, int Order);

    private sealed record LineageMaps(Dictionary<int, TypeRow> Types, Dictionary<int, PropRow> Props);

    // A resolution picks "source" or "target" for one conflict, keyed by MergeConflict.Id (the SAME stable
    // id the report exposes) — the operator re-runs mergeBranch with these after seeing a MergeReport's
    // conflicts. Applying a resolution SPLICES the picked side's value in place of flagging that conflict
    // (no separate post-process — resolving at the exact point a conflict would otherwise be recorded is
    // what lets a resolved meta-field/fn/rule feed into everything downstream that depends on it, e.g. a
    // resolved type rename feeding the props still keyed to that type).
    public static readonly IReadOnlyDictionary<string, string> NoResolutions = new Dictionary<string, string>();

    // Compute the merge between `source` and `target`, relative to their common ancestor `base` — all
    // three as cached snapshots (the DESIGN doc's cached artifacts, never a replay). `lineageOf` resolves
    // a MetaType/MetaProp row id to its lineage anchor (see the type's own doc); threaded in so this stays
    // a pure function over already-read data, no store dependency here. `resolutions` (default none) picks
    // a side for any conflict the CALLER already knows about (a re-run after a prior conflicting attempt);
    // any conflict without a resolution still refuses.
    public static MergeComputation Compute(
        DesignSnapshot @base, DesignSnapshot source, DesignSnapshot target, Func<int, int> lineageOf,
        IReadOnlyDictionary<string, string>? resolutions = null)
    {
        var baseDesc = InstanceDescriptionLoader.Load(@base.Text);
        var sourceDesc = InstanceDescriptionLoader.Load(source.Text);
        var targetDesc = InstanceDescriptionLoader.Load(target.Text);

        var conflicts = new List<MergeConflict>();
        var accessChanges = new List<AccessChangeItem>();
        var res = resolutions ?? NoResolutions;

        var typeResult = MergeTypes(baseDesc, sourceDesc, targetDesc, @base.IdMap, source.IdMap, target.IdMap, lineageOf, conflicts, res);
        var codeResult = MergeCode(baseDesc, sourceDesc, targetDesc, conflicts, res);
        var accessResult = MergeAccess(baseDesc, sourceDesc, targetDesc, conflicts, accessChanges, res);
        var initialData = MergeInitialData(@base.Text, source.Text, target.Text, conflicts, res);

        return new MergeComputation(typeResult, codeResult, accessResult, initialData, conflicts, accessChanges);
    }

    // Whether `resolutions` names `conflictId` with a recognized "source"/"target" pick — the one lookup
    // every conflict site shares. An id present with an UNRECOGNIZED take value (neither "source" nor
    // "target") is treated as unresolved (never silently guessed).
    private static bool TryTakeSide(IReadOnlyDictionary<string, string> resolutions, string conflictId, out bool takeSource)
    {
        takeSource = false;
        if (!resolutions.TryGetValue(conflictId, out var take)) return false;
        if (take == "source") { takeSource = true; return true; }
        return take == "target";
    }

    // ── types + props (per-entity, per-meta-field, existence rules) ────────────────────────────────

    private static MergedTypes MergeTypes(
        InstanceDescription baseDesc, InstanceDescription sourceDesc, InstanceDescription targetDesc,
        IReadOnlyDictionary<string, int> baseIdMap, IReadOnlyDictionary<string, int> sourceIdMap,
        IReadOnlyDictionary<string, int> targetIdMap, Func<int, int> lineageOf, List<MergeConflict> conflicts,
        IReadOnlyDictionary<string, string> resolutions)
    {
        var baseMaps = ByLineage(baseIdMap, baseDesc, lineageOf);
        var sourceMaps = ByLineage(sourceIdMap, sourceDesc, lineageOf);
        var targetMaps = ByLineage(targetIdMap, targetDesc, lineageOf);

        var mergedTypes = new List<MergedType>();
        var everyTypeLineage = baseMaps.Types.Keys.Union(sourceMaps.Types.Keys).Union(targetMaps.Types.Keys);

        foreach (var lineage in everyTypeLineage)
        {
            var inBase = baseMaps.Types.TryGetValue(lineage, out var baseRow);
            var inSource = sourceMaps.Types.TryGetValue(lineage, out var sourceRow);
            var inTarget = targetMaps.Types.TryGetValue(lineage, out var targetRow);

            if (!inBase)
            {
                // Added on one (or, degenerately, "both" — unreachable: a fresh lineage is unique to the
                // side that minted it) side. Take whichever side has it, preferring target for the
                // both-present case (never actually hit in practice).
                var winner = inTarget ? targetRow! : sourceRow!;
                mergedTypes.Add(new MergedType(lineage, winner.Type));
                continue;
            }
            if (!inSource && !inTarget) continue; // deleted both sides — nothing to carry
            if (!inSource || !inTarget)
            {
                var survivor = inSource ? sourceRow! : targetRow!;
                if (TypeChanged(baseRow!.Type, survivor.Type))
                {
                    var conflictId = MergeConflict.Existence(
                        lineage, "type", baseRow.Type.Name, DescribeType(baseRow.Type),
                        inSource ? DescribeType(sourceRow!.Type) : "(deleted)",
                        inTarget ? DescribeType(targetRow!.Type) : "(deleted)").Id;
                    if (TryTakeSide(resolutions, conflictId, out var takeSource))
                    {
                        // "take source" on a deleted-elsewhere conflict means KEEP the surviving (modified)
                        // side; "take target" means the delete wins (nothing carried) — the intuitive reading
                        // of "which side's outcome do you want" for an existence conflict.
                        if ((takeSource && inSource) || (!takeSource && inTarget))
                            mergedTypes.Add(new MergedType(lineage, survivor.Type));
                    }
                    else
                    {
                        conflicts.Add(MergeConflict.Existence(
                            lineage, "type", baseRow.Type.Name, DescribeType(baseRow.Type),
                            inSource ? DescribeType(sourceRow!.Type) : "(deleted)",
                            inTarget ? DescribeType(targetRow!.Type) : "(deleted)"));
                    }
                }
                continue; // deleted + untouched (or deleted + modified, handled above) — nothing else carried
            }

            var name = MergeField(lineage, "type", "name", baseRow!.Type.Name, sourceRow!.Type.Name, targetRow!.Type.Name, conflicts, resolutions);
            var baseTypeField = MergeField(lineage, "type", "baseType",
                BaseTypeWord(baseRow.Type.BaseType), BaseTypeWord(sourceRow.Type.BaseType), BaseTypeWord(targetRow.Type.BaseType), conflicts, resolutions);
            var valuesField = MergeField(lineage, "type", "values",
                ValuesWord(baseRow.Type), ValuesWord(sourceRow.Type), ValuesWord(targetRow.Type), conflicts, resolutions);

            mergedTypes.Add(new MergedType(lineage, new TypeDefinition(
                name ?? targetRow.Type.Name,
                baseTypeField != null ? ParseBaseTypeWord(baseTypeField) : targetRow.Type.BaseType,
                targetRow.Type.Props, // rebuilt below from the per-prop pass
                valuesField != null ? SplitValues(valuesField) : targetRow.Type.Values)));
        }

        var propsByOwningType = new Dictionary<int, List<MergedProp>>();
        var everyPropLineage = baseMaps.Props.Keys.Union(sourceMaps.Props.Keys).Union(targetMaps.Props.Keys);

        foreach (var lineage in everyPropLineage)
        {
            var inBase = baseMaps.Props.TryGetValue(lineage, out var baseRow);
            var inSource = sourceMaps.Props.TryGetValue(lineage, out var sourceRow);
            var inTarget = targetMaps.Props.TryGetValue(lineage, out var targetRow);

            if (!inBase)
            {
                // Added on one side (including a brand-new prop on a brand-new type — every merged type's
                // Props list is built EXCLUSIVELY from this accumulator, below, so a new type's props must
                // flow through here too or MergedType.Props would silently miss them).
                var winner = inTarget ? targetRow! : sourceRow!;
                AddProp(propsByOwningType, winner.OwningTypeLineage, new MergedProp(lineage, winner.Prop));
                continue;
            }
            if (!inSource && !inTarget) continue;
            if (!inSource || !inTarget)
            {
                var survivor = inSource ? sourceRow! : targetRow!;
                if (PropChanged(baseRow!.Prop, survivor.Prop))
                {
                    var conflictId = MergeConflict.Existence(
                        lineage, "prop", baseRow.Prop.Name, DescribeProp(baseRow.Prop),
                        inSource ? DescribeProp(sourceRow!.Prop) : "(deleted)",
                        inTarget ? DescribeProp(targetRow!.Prop) : "(deleted)").Id;
                    if (TryTakeSide(resolutions, conflictId, out var takeSource))
                    {
                        if ((takeSource && inSource) || (!takeSource && inTarget))
                        {
                            var winningOwner = inSource ? sourceRow!.OwningTypeLineage : targetRow!.OwningTypeLineage;
                            AddProp(propsByOwningType, winningOwner, new MergedProp(lineage, survivor.Prop));
                        }
                    }
                    else
                    {
                        conflicts.Add(MergeConflict.Existence(
                            lineage, "prop", baseRow.Prop.Name, DescribeProp(baseRow.Prop),
                            inSource ? DescribeProp(sourceRow!.Prop) : "(deleted)",
                            inTarget ? DescribeProp(targetRow!.Prop) : "(deleted)"));
                    }
                }
                continue;
            }

            var propName = MergeField(lineage, "prop", "name", baseRow!.Prop.Name, sourceRow!.Prop.Name, targetRow!.Prop.Name, conflicts, resolutions);
            var propType = MergeField(lineage, "prop", "type", baseRow.Prop.Type, sourceRow.Prop.Type, targetRow.Prop.Type, conflicts, resolutions);
            var cardinality = MergeField(lineage, "prop", "cardinality",
                CardinalityWord(baseRow.Prop.Cardinality), CardinalityWord(sourceRow.Prop.Cardinality), CardinalityWord(targetRow.Prop.Cardinality), conflicts, resolutions);
            var keyType = MergeField(lineage, "prop", "keyType",
                baseRow.Prop.KeyType ?? "", sourceRow.Prop.KeyType ?? "", targetRow.Prop.KeyType ?? "", conflicts, resolutions);
            var multiline = MergeField(lineage, "prop", "multiline",
                baseRow.Prop.Multiline.ToString(), sourceRow.Prop.Multiline.ToString(), targetRow.Prop.Multiline.ToString(), conflicts, resolutions);

            var mergedProp = new PropDefinition(
                propName ?? targetRow.Prop.Name,
                propType ?? targetRow.Prop.Type,
                cardinality != null ? ParseCardinalityWord(cardinality) : targetRow.Prop.Cardinality,
                keyType is { Length: > 0 } ? keyType : null,
                Multiline: multiline != null && bool.Parse(multiline));

            // A prop's owning type never itself changes across a merge (MetaProp rows are never
            // re-parented from one MetaType to another by any authoring surface) — prefer target's record
            // of the owner, falling back to source's for a target-side-deleted-then-reintroduced edge.
            var owningLineage = targetRow.OwningTypeLineage;
            AddProp(propsByOwningType, owningLineage, new MergedProp(lineage, mergedProp));
        }

        // Rebuild each merged type's Props in TARGET-SPINE order (both-side reorders never conflict —
        // settled cosmetic policy): lineages target already had keep target's relative order; a
        // source-only-added lineage (absent from target) is appended in its source-relative order.
        // `Props` KEEPS its lineage (a List<MergedProp>, not folded into plain PropDefinition) — the apply
        // step (KernelHostActions.ApplyMergeToTarget) needs each prop's lineage to know whether the
        // target already has a row for it, so this must not be discarded here.
        var finalTypes = new List<MergedType>();
        foreach (var mt in mergedTypes)
        {
            var owned = propsByOwningType.GetValueOrDefault(mt.Lineage, []);
            var ordered = OrderBySpine(owned, p => p.Lineage,
                targetOrder: targetMaps.Props.ToDictionary(kv => kv.Key, kv => kv.Value.Order),
                sourceOrder: sourceMaps.Props.ToDictionary(kv => kv.Key, kv => kv.Value.Order));
            finalTypes.Add(mt with { Type = mt.Type with { Props = ordered.Count > 0 ? [.. ordered.Select(p => p.Prop)] : null }, Props = ordered });
        }

        var orderedTypes = OrderBySpine(finalTypes, t => t.Lineage,
            targetOrder: targetMaps.Types.ToDictionary(kv => kv.Key, kv => kv.Value.Order),
            sourceOrder: sourceMaps.Types.ToDictionary(kv => kv.Key, kv => kv.Value.Order));

        return new MergedTypes(orderedTypes);
    }

    private static void AddProp(Dictionary<int, List<MergedProp>> map, int owningTypeLineage, MergedProp prop)
    {
        if (!map.TryGetValue(owningTypeLineage, out var list)) map[owningTypeLineage] = list = [];
        list.Add(prop);
    }

    // Target-spine order: a lineage target already carries keeps TARGET's relative order; a lineage ONLY
    // source carries (added on the branch, absent from target) is appended in its source-relative order.
    private static List<T> OrderBySpine<T>(
        IReadOnlyList<T> items, Func<T, int> lineageOf,
        IReadOnlyDictionary<int, int> targetOrder, IReadOnlyDictionary<int, int> sourceOrder) =>
        items
            .OrderBy(i => targetOrder.TryGetValue(lineageOf(i), out var to) ? to
                : 1_000_000 + (sourceOrder.TryGetValue(lineageOf(i), out var so) ? so : 0))
            .ThenBy(lineageOf)
            .ToList();

    // Invert a commit's idMap (path → row-id) into lineage → (definition, owning-type-lineage, order),
    // keeping each row's idMap position as a deterministic creation-order tiebreak for OrderBySpine.
    private static LineageMaps ByLineage(IReadOnlyDictionary<string, int> idMap, InstanceDescription desc, Func<int, int> lineageOf)
    {
        var byInsertOrder = idMap.OrderBy(kv => kv.Value).ToList();
        var types = new Dictionary<int, TypeRow>();
        var typeOrder = 0;
        foreach (var (path, id) in byInsertOrder)
        {
            if (path.Contains('.')) continue;
            var def = desc.FindType(path);
            if (def is null) continue; // a leftover/stale idMap entry the projection never printed
            types[lineageOf(id)] = new TypeRow(def, typeOrder++);
        }

        var props = new Dictionary<int, PropRow>();
        var propOrder = 0;
        foreach (var (path, id) in byInsertOrder)
        {
            var dot = path.IndexOf('.');
            if (dot < 0) continue;
            var typeName = path[..dot];
            var propName = path[(dot + 1)..];
            var typeDef = desc.FindType(typeName);
            var propDef = typeDef?.Props?.FirstOrDefault(p => p.Name == propName);
            if (typeDef is null || propDef is null) continue;
            var owningLineage = idMap.TryGetValue(typeName, out var owningId) ? lineageOf(owningId) : lineageOf(id);
            props[lineageOf(id)] = new PropRow(propDef, owningLineage, propOrder++);
        }

        return new LineageMaps(types, props);
    }

    private static bool TypeChanged(TypeDefinition a, TypeDefinition b) =>
        a.Name != b.Name || a.BaseType != b.BaseType || ValuesWord(a) != ValuesWord(b);

    private static bool PropChanged(PropDefinition a, PropDefinition b) =>
        a.Name != b.Name || a.Type != b.Type || a.Cardinality != b.Cardinality
        || (a.KeyType ?? "") != (b.KeyType ?? "") || a.Multiline != b.Multiline;

    private static string DescribeType(TypeDefinition t) => $"{t.Name} ({BaseTypeWord(t.BaseType)})";
    private static string DescribeProp(PropDefinition p) => $"{p.Name}: {p.Type} ({CardinalityWord(p.Cardinality)})";

    private static string BaseTypeWord(BaseType bt) => bt switch
    {
        BaseType.Object => "object",
        BaseType.Enum => "enum",
        _ => BaseTypes.NameOf(bt),
    };
    private static BaseType ParseBaseTypeWord(string word) => word switch
    {
        "object" => BaseType.Object,
        "enum" => BaseType.Enum,
        _ => BaseTypes.Parse(word),
    };
    private static string ValuesWord(TypeDefinition t) => string.Join(",", t.Values ?? []);
    private static List<string>? SplitValues(string word) => word.Length == 0 ? null : [.. word.Split(',')];
    private static string CardinalityWord(Cardinality c) => c switch
    {
        Cardinality.Set => "set",
        Cardinality.Dictionary => "dictionary",
        _ => "single",
    };
    private static Cardinality ParseCardinalityWord(string word) => word switch
    {
        "set" => Cardinality.Set,
        "dictionary" => Cardinality.Dictionary,
        _ => Cardinality.Single,
    };

    // Per-meta-field policy: `order` NEVER conflicts (never routed here — callers renormalize via
    // OrderBySpine instead). Every OTHER meta-field: changed-one-side → take it; both-same → take;
    // both-different → resolved (per `resolutions`) or CONFLICT (recorded; target's own value returned as
    // a placeholder — a conflicted merge never applies, so the placeholder is never actually written).
    private static string? MergeField(
        int lineage, string kind, string field, string baseVal, string sourceVal, string targetVal,
        List<MergeConflict> conflicts, IReadOnlyDictionary<string, string> resolutions)
    {
        if (sourceVal == targetVal) return targetVal; // both agree (incl. both unchanged)
        if (baseVal == targetVal) return sourceVal; // only source changed it
        if (baseVal == sourceVal) return targetVal; // only target changed it
        var conflictId = MergeConflict.Meta(lineage, kind, field, baseVal, sourceVal, targetVal).Id;
        if (TryTakeSide(resolutions, conflictId, out var takeSource)) return takeSource ? sourceVal : targetVal;
        conflicts.Add(MergeConflict.Meta(lineage, kind, field, baseVal, sourceVal, targetVal));
        return targetVal;
    }

    // ── code sections (common + ui): whole-fn merge keyed by NAME ───────────────────────────────────

    private static MergedCode MergeCode(
        InstanceDescription baseDesc, InstanceDescription sourceDesc, InstanceDescription targetDesc,
        List<MergeConflict> conflicts, IReadOnlyDictionary<string, string> resolutions)
    {
        var commonFns = MergeFnList(
            baseDesc.Common?.Functions ?? [], sourceDesc.Common?.Functions ?? [], targetDesc.Common?.Functions ?? [],
            "common", conflicts, resolutions);
        var uiFns = MergeFnList(
            baseDesc.Ui?.Functions ?? [], sourceDesc.Ui?.Functions ?? [], targetDesc.Ui?.Functions ?? [],
            "ui", conflicts, resolutions);

        // `render` is itself a name-keyed fn (its own slot, outside Ui.Functions) — merge it the same way,
        // treating "no render" (auto/generic UI) as simply absent from that side's fn set.
        var baseRender = baseDesc.Ui?.Render is { } br ? new[] { br } : [];
        var sourceRender = sourceDesc.Ui?.Render is { } sr ? new[] { sr } : [];
        var targetRender = targetDesc.Ui?.Render is { } tr ? new[] { tr } : [];
        var mergedRenderList = MergeFnList(baseRender, sourceRender, targetRender, "ui", conflicts, resolutions);
        var mergedRender = mergedRenderList.FirstOrDefault(f => f.Name == "render");

        // `ui` vars merge the same way, keyed by name, whole-value (a both-sides-different edit conflicts
        // exactly like a fn body).
        var vars = MergeVarList(baseDesc.Ui?.Vars ?? [], sourceDesc.Ui?.Vars ?? [], targetDesc.Ui?.Vars ?? [], conflicts, resolutions);

        return new MergedCode(commonFns, uiFns, mergedRender, vars);
    }

    // A function's canonical text for comparison — the MULTI-LINE form (CodePrint.Function), NOT the inline
    // CodePrint.Value form (which throws "no inline text form" on a body containing a JSX <tag>, e.g. a
    // `fn render()`). Same-body fns print byte-identical here; a differing body prints differently.
    private static string FnText(CodeFunction fn)
    {
        var sb = new System.Text.StringBuilder();
        CodePrint.Function(sb, fn, "");
        return sb.ToString();
    }

    private static List<CodeFunction> MergeFnList(
        IReadOnlyList<CodeFunction> baseFns, IReadOnlyList<CodeFunction> sourceFns, IReadOnlyList<CodeFunction> targetFns,
        string section, List<MergeConflict> conflicts, IReadOnlyDictionary<string, string> resolutions)
    {
        string TextOf(CodeFunction fn) => FnText(fn);
        var baseByName = baseFns.Where(f => f.Name != null).ToDictionary(f => f.Name!, TextOf);
        var sourceByName = sourceFns.Where(f => f.Name != null).ToDictionary(f => f.Name!);
        var targetByName = targetFns.Where(f => f.Name != null).ToDictionary(f => f.Name!);

        var everyName = baseByName.Keys.Union(sourceByName.Keys).Union(targetByName.Keys);
        var result = new List<CodeFunction>();
        foreach (var name in everyName)
        {
            var inBase = baseByName.TryGetValue(name, out var baseText);
            var inSource = sourceByName.TryGetValue(name, out var sourceFn);
            var inTarget = targetByName.TryGetValue(name, out var targetFn);

            if (!inBase)
            {
                if (inSource && inTarget && TextOf(sourceFn!) != TextOf(targetFn!))
                {
                    var conflictId = MergeConflict.Fn(section, name, "(new)", TextOf(sourceFn!), TextOf(targetFn!)).Id;
                    if (TryTakeSide(resolutions, conflictId, out var takeSource))
                        result.Add(takeSource ? sourceFn! : targetFn!);
                    else
                    {
                        conflicts.Add(MergeConflict.Fn(section, name, "(new)", TextOf(sourceFn!), TextOf(targetFn!)));
                        result.Add(targetFn!);
                    }
                    continue;
                }
                result.Add(inTarget ? targetFn! : sourceFn!);
                continue;
            }
            if (!inSource && !inTarget) continue; // deleted both sides
            if (!inSource || !inTarget)
            {
                var survivorText = inSource ? TextOf(sourceFn!) : TextOf(targetFn!);
                if (survivorText != baseText)
                {
                    var conflictId = MergeConflict.Fn(section, name, baseText!, inSource ? survivorText : "(deleted)", inTarget ? survivorText : "(deleted)").Id;
                    if (TryTakeSide(resolutions, conflictId, out var takeSource))
                    {
                        if ((takeSource && inSource) || (!takeSource && inTarget)) result.Add(inSource ? sourceFn! : targetFn!);
                    }
                    else
                    {
                        conflicts.Add(MergeConflict.Fn(section, name, baseText!, inSource ? survivorText : "(deleted)", inTarget ? survivorText : "(deleted)"));
                    }
                }
                continue;
            }

            var sourceText = TextOf(sourceFn!);
            var targetText = TextOf(targetFn!);
            if (sourceText == targetText) { result.Add(targetFn!); continue; } // both-same (incl. both unchanged)
            if (baseText == targetText) { result.Add(sourceFn!); continue; } // only source changed
            if (baseText == sourceText) { result.Add(targetFn!); continue; } // only target changed
            {
                var conflictId = MergeConflict.Fn(section, name, baseText!, sourceText, targetText).Id;
                if (TryTakeSide(resolutions, conflictId, out var takeSource)) { result.Add(takeSource ? sourceFn! : targetFn!); continue; }
            }
            conflicts.Add(MergeConflict.Fn(section, name, baseText!, sourceText, targetText));
            result.Add(targetFn!); // placeholder — never applied while conflicts remain
        }
        return result;
    }

    private static List<UiVar> MergeVarList(
        IReadOnlyList<UiVar> baseVars, IReadOnlyList<UiVar> sourceVars, IReadOnlyList<UiVar> targetVars,
        List<MergeConflict> conflicts, IReadOnlyDictionary<string, string> resolutions)
    {
        string TextOf(UiVar v) => v.Value != null ? CodePrint.Value(v.Value) : "";
        var baseByName = baseVars.ToDictionary(v => v.Name, TextOf);
        var sourceByName = sourceVars.ToDictionary(v => v.Name);
        var targetByName = targetVars.ToDictionary(v => v.Name);

        var everyName = baseByName.Keys.Union(sourceByName.Keys).Union(targetByName.Keys);
        var result = new List<UiVar>();
        foreach (var name in everyName)
        {
            var inBase = baseByName.TryGetValue(name, out var baseText);
            var inSource = sourceByName.TryGetValue(name, out var sourceVar);
            var inTarget = targetByName.TryGetValue(name, out var targetVar);

            if (!inBase)
            {
                if (inSource && inTarget && TextOf(sourceVar!) != TextOf(targetVar!))
                {
                    var conflictId = MergeConflict.Fn("ui", name, "(new)", TextOf(sourceVar!), TextOf(targetVar!)).Id;
                    if (TryTakeSide(resolutions, conflictId, out var takeSource))
                        result.Add(takeSource ? sourceVar! : targetVar!);
                    else
                    {
                        conflicts.Add(MergeConflict.Fn("ui", name, "(new)", TextOf(sourceVar!), TextOf(targetVar!)));
                        result.Add(targetVar!);
                    }
                    continue;
                }
                result.Add(inTarget ? targetVar! : sourceVar!);
                continue;
            }
            if (!inSource && !inTarget) continue;
            if (!inSource || !inTarget)
            {
                var survivorText = inSource ? TextOf(sourceVar!) : TextOf(targetVar!);
                if (survivorText != baseText)
                {
                    var conflictId = MergeConflict.Fn("ui", name, baseText!, inSource ? survivorText : "(deleted)", inTarget ? survivorText : "(deleted)").Id;
                    if (TryTakeSide(resolutions, conflictId, out var takeSource))
                    {
                        if ((takeSource && inSource) || (!takeSource && inTarget)) result.Add(inSource ? sourceVar! : targetVar!);
                    }
                    else
                    {
                        conflicts.Add(MergeConflict.Fn("ui", name, baseText!, inSource ? survivorText : "(deleted)", inTarget ? survivorText : "(deleted)"));
                    }
                }
                continue;
            }

            var sourceText = TextOf(sourceVar!);
            var targetText = TextOf(targetVar!);
            if (sourceText == targetText) { result.Add(targetVar!); continue; }
            if (baseText == targetText) { result.Add(sourceVar!); continue; }
            if (baseText == sourceText) { result.Add(targetVar!); continue; }
            {
                var conflictId = MergeConflict.Fn("ui", name, baseText!, sourceText, targetText).Id;
                if (TryTakeSide(resolutions, conflictId, out var takeSource)) { result.Add(takeSource ? sourceVar! : targetVar!); continue; }
            }
            conflicts.Add(MergeConflict.Fn("ui", name, baseText!, sourceText, targetText));
            result.Add(targetVar!);
        }
        return result;
    }

    // ── access rules: rule-granular by (subject, verbs) identity ────────────────────────────────────

    private static MergedAccess MergeAccess(
        InstanceDescription baseDesc, InstanceDescription sourceDesc, InstanceDescription targetDesc,
        List<MergeConflict> conflicts, List<AccessChangeItem> accessChanges, IReadOnlyDictionary<string, string> resolutions)
    {
        string KeyOf(AccessRule r) => $"{r.Type}|{string.Join(',', r.Verbs)}";
        string CondOf(AccessRule r) => r.When != null ? CodePrint.Value(r.When) : "";

        var baseRules = (baseDesc.Rules ?? []).ToDictionary(KeyOf);
        var sourceRules = (sourceDesc.Rules ?? []).ToDictionary(KeyOf);
        var targetRules = (targetDesc.Rules ?? []).ToDictionary(KeyOf);

        var everyKey = baseRules.Keys.Union(sourceRules.Keys).Union(targetRules.Keys);
        var result = new List<AccessRule>();
        foreach (var key in everyKey)
        {
            var inBase = baseRules.TryGetValue(key, out var baseRule);
            var inSource = sourceRules.TryGetValue(key, out var sourceRule);
            var inTarget = targetRules.TryGetValue(key, out var targetRule);

            if (!inBase)
            {
                // Added on one or BOTH sides: a genuine union (each grant was intended — settled, §1).
                // Both-added with DIFFERING conditions is itself a same-subject+verbs conflict.
                if (inSource && inTarget)
                {
                    if (CondOf(sourceRule!) != CondOf(targetRule!))
                    {
                        var conflictId = MergeConflict.Access(key, "(new)", CondOf(sourceRule!), CondOf(targetRule!)).Id;
                        if (TryTakeSide(resolutions, conflictId, out var takeSource))
                        {
                            result.Add(takeSource ? sourceRule! : targetRule!);
                            accessChanges.Add(new AccessChangeItem(key, "added (both branches, resolved)", takeSource ? CondOf(sourceRule!) : CondOf(targetRule!)));
                        }
                        else
                        {
                            conflicts.Add(MergeConflict.Access(key, "(new)", CondOf(sourceRule!), CondOf(targetRule!)));
                            result.Add(targetRule!); // placeholder
                        }
                        continue;
                    }
                    result.Add(targetRule!);
                    accessChanges.Add(new AccessChangeItem(key, "added (both branches)", CondOf(targetRule!)));
                    continue;
                }
                var added = inSource ? sourceRule! : targetRule!;
                result.Add(added);
                // EVERY add since base is surfaced (settled: the must-see block lists every access
                // difference the merge's RESULT carries relative to the common ancestor — not just the
                // incoming half). A source-only add is "incoming"; a target-only add is one the target
                // branch introduced since base, still part of the merged access picture the operator must
                // review (deny-by-default means any grant widens access — combination effects included).
                accessChanges.Add(new AccessChangeItem(key, inSource ? "added (incoming)" : "added (target)", CondOf(added)));
                continue;
            }
            if (!inSource && !inTarget) continue; // removed both sides
            if (!inSource || !inTarget)
            {
                // Removed on exactly one side — access removal always narrows (never widens), so it is
                // never a conflict by this slice's policy; still reported as a must-see change.
                if (!inSource) accessChanges.Add(new AccessChangeItem(key, "removed (incoming)", CondOf(baseRule!)));
                continue; // the removal wins — nothing carried forward for this key
            }

            var baseCond = CondOf(baseRule!);
            var sourceCond = CondOf(sourceRule!);
            var targetCond = CondOf(targetRule!);
            if (sourceCond == targetCond) { result.Add(targetRule!); continue; }
            if (baseCond == targetCond) { result.Add(sourceRule!); accessChanges.Add(new AccessChangeItem(key, "condition changed (incoming)", sourceCond)); continue; }
            if (baseCond == sourceCond) { result.Add(targetRule!); continue; } // only target changed — no incoming change
            {
                var conflictId = MergeConflict.Access(key, baseCond, sourceCond, targetCond).Id;
                if (TryTakeSide(resolutions, conflictId, out var takeSource))
                {
                    result.Add(takeSource ? sourceRule! : targetRule!);
                    if (takeSource) accessChanges.Add(new AccessChangeItem(key, "condition changed (resolved, incoming)", sourceCond));
                    continue;
                }
            }
            conflicts.Add(MergeConflict.Access(key, baseCond, sourceCond, targetCond));
            result.Add(targetRule!); // placeholder
        }
        return new MergedAccess(result);
    }

    // ── initialData: whole-section 3-way TEXT compare (rarely edited post-launch — settled §1) ──────

    private static string MergeInitialData(
        string baseText, string sourceText, string targetText, List<MergeConflict> conflicts,
        IReadOnlyDictionary<string, string> resolutions)
    {
        var baseSection = DesignerSeed.SplitSections(baseText).GetValueOrDefault("initialData", "");
        var sourceSection = DesignerSeed.SplitSections(sourceText).GetValueOrDefault("initialData", "");
        var targetSection = DesignerSeed.SplitSections(targetText).GetValueOrDefault("initialData", "");

        if (sourceSection == targetSection) return targetSection;
        if (baseSection == targetSection) return sourceSection;
        if (baseSection == sourceSection) return targetSection;
        var conflict = new MergeConflict("initialData", "initialData", "initialData", null, baseSection, sourceSection, targetSection);
        if (TryTakeSide(resolutions, conflict.Id, out var takeSourceSection)) return takeSourceSection ? sourceSection : targetSection;
        conflicts.Add(conflict);
        return targetSection; // placeholder
    }
}

// ── result shapes ────────────────────────────────────────────────────────────────────────────────────

// The whole merge computation: the merged structural/code/access/initialData results PLUS every
// conflict found (empty = clean) and the must-see access-change list (populated even on a clean merge —
// settled: access changes are ALWAYS surfaced, §1).
public sealed record MergeComputation(
    MergedTypes Types, MergedCode Code, MergedAccess Access, string InitialData,
    IReadOnlyList<MergeConflict> Conflicts, IReadOnlyList<AccessChangeItem> AccessChanges)
{
    public bool IsClean => Conflicts.Count == 0;
}

// One merged type by LINEAGE (not a physical row id — the apply step resolves lineage → the target's own
// row, minting a new one when this lineage has no row in target yet).
// `Props` (default empty) is the SAME props the folded `Type.Props` shows, but keeping each prop's
// lineage — the apply step needs it; `Type` alone (plain PropDefinition, no identity) does not carry it.
public sealed record MergedType(int Lineage, TypeDefinition Type, IReadOnlyList<MergedProp> Props = null!)
{
    public IReadOnlyList<MergedProp> Props { get; init; } = Props ?? [];
}
public sealed record MergedProp(int Lineage, PropDefinition Prop);
public sealed record MergedTypes(IReadOnlyList<MergedType> Types);
public sealed record MergedCode(
    IReadOnlyList<CodeFunction> CommonFunctions, IReadOnlyList<CodeFunction> UiFunctions,
    CodeFunction? Render, IReadOnlyList<UiVar> UiVars);
public sealed record MergedAccess(IReadOnlyList<AccessRule> Rules);

// A conflict unit: `Kind` = meta | existence | fn | initialData | access; `Path` names the entity
// (a type/prop's last-known name, a fn's name, "initialData", or the access rule's subject|verbs key);
// `Field` the specific meta-field for a `meta` conflict (null otherwise — a whole-unit conflict for the
// other kinds). Carries the three raw text values for the report (never applied while any conflict remains).
public sealed record MergeConflict(string Kind, string Path, string Id, string? Field, string? Base, string Source, string Target)
{
    public static MergeConflict Meta(int lineage, string kind, string field, string baseVal, string sourceVal, string targetVal) =>
        new("meta", $"{kind}:{lineage}", $"{kind}:{lineage}:{field}", field, baseVal, sourceVal, targetVal);

    public static MergeConflict Existence(int lineage, string kind, string path, string @base, string source, string target) =>
        new("existence", path, $"{kind}:{lineage}", null, @base, source, target);

    public static MergeConflict Fn(string section, string name, string @base, string source, string target) =>
        new("fn", name, $"{section}.{name}", null, @base, source, target);

    public static MergeConflict Access(string key, string @base, string source, string target) =>
        new("access", key, $"access:{key}", null, @base, source, target);
}

// One access difference the merge introduces relative to the TARGET — always populated (even a clean
// merge), per the settled "access changes are always surfaced" policy.
public sealed record AccessChangeItem(string RuleKey, string Change, string Condition);
