using DeEnv.Instance;

namespace DeEnv.Storage;

// The startup guard: structural validation of an EXISTING data document against the
// running app's types, before the instance starts serving. It catches a data file
// left behind by a different or older app — unknown extent types, stored fields the
// app does not declare, a stored kind contradicting the declaration, legacy documents
// without intrinsic collection ids, dangling references — and fails loudly with the
// file and the offending detail named, instead of half-working over stale data
// (mutations silently rejected, reloads losing changes).
//
// Validation runs over the TYPED StoreDoc (node kinds are a closed union), so the walk
// pattern-matches on the value subtype and never sniffs a string key — the dictionary /
// object ambiguity that produced the old "must be of type 'JsonValue'" GC bug cannot
// arise here either.
//
// Deliberately tolerant of additive schema evolution: a declared prop missing from
// the stored fields is fine (reads fall back to defaults). It never reseeds over an
// existing file; the error names the remedy and leaves the decision to the user.
public static class StoredDataValidator
{
    public static void Validate(StoreDoc doc, InstanceDescription desc, string filePath) =>
        new Walk(desc, filePath).Document(doc);

    private sealed class Walk(InstanceDescription desc, string filePath)
    {
        // Extent ids per type, collected up front so references can be checked.
        private readonly Dictionary<string, HashSet<int>> _ids = new();

        private void Fail(string detail) => throw new StoredDataException(
            $"Data file '{filePath}' does not match the running app: {detail} " +
            "Delete or move the file to reseed it from the app's initialData.");

        public void Document(StoreDoc doc)
        {
            CollectExtentIds(doc);

            foreach (var (typeName, pool) in doc.Extents)
            {
                var type = desc.FindType(typeName)!; // known: CollectExtentIds checked
                foreach (var (idText, entry) in pool)
                    Fields(typeName, idText.ToString(), entry.Fields, type);
            }

            Root(doc);
        }

        // First pass over the extents: every type must be a declared object type, and
        // every entry envelope well-formed (id matches the key, typeName matches the
        // extent); collect ids for the reference checks.
        private void CollectExtentIds(StoreDoc doc)
        {
            foreach (var (typeName, pool) in doc.Extents)
            {
                var type = desc.FindType(typeName);
                if (type is null)
                    Fail($"the data has an extent of type '{typeName}', which the app does not declare.");
                if (type!.BaseType != BaseType.Object)
                    Fail($"the data has an extent of type '{typeName}', which is not an object type.");

                var ids = _ids[typeName] = new HashSet<int>();
                foreach (var (id, entry) in pool)
                {
                    if (entry.Id != id || entry.TypeName != typeName)
                        Fail($"the extent entry '{typeName}/{id}' is malformed.");
                    ids.Add(id);
                }
            }
        }

        private void Fields(string typeName, string id, Dictionary<string, StoredValue> fields, TypeDefinition type)
        {
            foreach (var (name, node) in fields)
            {
                var prop = type.Props?.FirstOrDefault(p => p.Name == name);
                if (prop is null)
                {
                    Fail($"stored object {typeName}/{id} has a field '{name}' the app does not declare.");
                    return;
                }

                var where = $"field '{name}' on {typeName}/{id}";
                switch (prop.Cardinality)
                {
                    case Cardinality.Set:
                        if (node is not StoredSet set)
                        {
                            Fail($"{where} is declared 'set of {prop.Type}' but is stored as '{KindOf(node)}'.");
                            break;
                        }
                        CollectionId(set.Id, where);
                        foreach (var (memberId, member) in set.Members)
                        {
                            Reference(member, prop.Type, where);
                            if (member is not StoredRef mref || mref.Id != memberId)
                                Fail($"{where} has a member keyed '{memberId}' that does not match its reference.");
                        }
                        break;

                    case Cardinality.Dictionary:
                        if (node is not StoredDict dict)
                        {
                            Fail($"{where} is declared 'dictionary of {prop.Type}' but is stored as '{KindOf(node)}'.");
                            break;
                        }
                        CollectionId(dict.Id, where);
                        foreach (var (_, entry) in dict.Entries)
                        {
                            if (desc.IsObjectType(prop.Type))
                                Reference(entry, prop.Type, where);
                            else
                                Scalar(entry, prop.Type, where);
                        }
                        break;

                    default:
                        if (desc.IsObjectType(prop.Type))
                            Reference(node, prop.Type, where);
                        else
                            Scalar(node, prop.Type, where);
                        break;
                }
            }
        }

        // A stored collection carries an intrinsic id (legacy files have none — id 0 —
        // and the code UI addresses collections by id).
        private void CollectionId(int id, string where)
        {
            if (id == 0)
                Fail($"{where} has no intrinsic id (a legacy data file).");
        }

        // A stored object reference: of exactly the declared type, pointing at an object
        // that exists in that type's extent.
        private void Reference(StoredValue? node, string declaredType, string where)
        {
            if (node is not StoredRef reference)
            {
                Fail($"{where} is declared as a reference to '{declaredType}' but is stored as '{KindOf(node)}'.");
                return;
            }
            if (reference.TypeName != declaredType)
                Fail($"{where} is declared as a reference to '{declaredType}' but references a '{reference.TypeName}'.");
            if (!(_ids.TryGetValue(declaredType, out var ids) && ids.Contains(reference.Id)))
                Fail($"{where} references object {reference.Id} of type '{declaredType}', which is not stored.");
        }

        // A stored scalar: its tag must be the declared type's base type. An enum value is
        // text-shaped, and must additionally be a declared member of its enum (or empty) —
        // the startup twin of the WS write-path check (WsHandler.HandleObjectPropChange).
        private void Scalar(StoredValue? node, string declaredType, string where)
        {
            var expected = BaseTag(declaredType);
            if (node is not StoredLeaf leaf || ScalarTag(leaf.Scalar) != expected)
            {
                Fail($"{where} is declared '{declaredType}' but is stored as '{KindOf(node)}'.");
                return;
            }
            // Only an enum constrains its value set; its stored value is a string (tag "text").
            if (desc.IsEnumType(declaredType)
                && leaf.Scalar is TextValue text
                && !desc.EnumAccepts(declaredType, text.Text))
                Fail($"{where} holds '{text.Text}', which is not a value of enum '{declaredType}'.");
        }

        private void Root(StoreDoc doc)
        {
            var db = desc.Db()!;
            if (doc.Root is null)
            {
                Fail("the document has no root.");
                return;
            }

            if (db.BaseType == BaseType.Object)
                Reference(doc.Root, db.Name, "the root");
            else
                Scalar(doc.Root, db.Name, "the root");
        }

        // The structural kind word a stored value reports — for the same "stored as 'X'"
        // diagnostics the old raw-DOM validator produced (a leaf reports its scalar tag).
        private static string KindOf(StoredValue? node) => node switch
        {
            null => "nothing",
            StoredRef => "object",
            StoredSet => "set",
            StoredDict => "dictionary",
            StoredLeaf leaf => ScalarTag(leaf.Scalar),
            _ => node.GetType().Name,
        };

        private static string ScalarTag(NodeValue scalar) => scalar switch
        {
            BoolValue => "bool",
            IntValue => "int",
            DecimalValue => "decimal",
            TextValue => "text",
            DateValue => "date",
            DateTimeValue => "datetime",
            _ => scalar.GetType().Name,
        };

        // The tag a scalar of this declared type is stored with ("text", "bool", …). An enum
        // value is stored as text (its value name), so its tag is "text" too.
        private string BaseTag(string typeName)
        {
            var baseType = BaseTypes.IsName(typeName)
                ? BaseTypes.Parse(typeName)
                : desc.FindType(typeName)?.BaseType switch
                  {
                      BaseType.Enum => BaseType.Text,
                      { } bt => bt,
                      null => throw new InvalidOperationException($"Unknown type '{typeName}'."),
                  };
            return baseType.ToString().ToLowerInvariant();
        }
    }
}
