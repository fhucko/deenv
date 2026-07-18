using System.Text.Json;
using DeEnv.Code;

namespace DeEnv.Instance;

public static class InstanceDescriptionLoader
{
    // An instance is described by ONE app text document (types + initialData +
    // common + ui — see AppParse). JSON is internal only: the in-memory model,
    // the wire, and storage. A syntax error fails the load with its position; the
    // semantic validations below run on the parsed description.
    public static InstanceDescription Load(string appText)
    {
        InstanceDescription desc;
        try
        {
            desc = AppParse.Parse(appText);
        }
        catch (DeEnv.Code.Parsing.CodeParseException ex)
        {
            throw new SchemaValidationException(ex.Message);
        }

        ValidateDescription(desc);
        CodeIds.Assign(desc); // number every CodeFunction for stable memo-cache keys
        return desc;
    }

    public static InstanceDescription LoadFile(string path) =>
        Load(File.ReadAllText(path));

    // The semantic validation pipeline, also used by the designer bridge on a
    // machine-built description before it is printed and published.
    public static void ValidateDescription(InstanceDescription desc)
    {
        Validate(desc);
        ValidateInitialData(desc);
        CodeValidator.Validate(desc);
    }

    private static void Validate(InstanceDescription desc)
    {
        // Type names must be unique. This single rule subsumes "more than one Db":
        // a duplicate Db is just a duplicate name.
        var seen = new HashSet<string>();
        foreach (var name in desc.AllTypes().Select(t => t.Name))
        {
            // `sys` is RESERVED: it is the Code system namespace AND the access section's host-action
            // subject (AccessFloor.SysSubject). A user type named `sys` would share key-space with that
            // framework vocabulary — the very collision the system/user separation forbids — so it is
            // rejected at load, exactly as `Db` is special-cased above (a framework name, not a user one).
            if (name == Code.AccessFloor.SysSubject)
                throw new SchemaValidationException(
                    "'sys' is a reserved name (the framework namespace and the access `sys` subject) " +
                    "and cannot be used as a type name.");

            if (!seen.Add(name))
                throw new SchemaValidationException(
                    $"Duplicate type name '{name}': every type must have a unique name.");
        }

        // The root type 'Db' must exist (uniqueness already forbids a second one).
        if (!seen.Contains("Db"))
            throw new SchemaValidationException(
                "Schema document must define exactly one root type named 'Db'.");

        // The root 'Db' must be an OBJECT type — it holds the app's data (props: scalars,
        // references, sets, dictionaries). A base-typed root (e.g. `Db: bool`) is rejected:
        // it is a degenerate shape the generic UI would have to special-case, and every real
        // app's root is an object. (See DECISIONS.md / INSTANCE_DESCRIPTION_FORMAT.md.)
        if (desc.Db()?.BaseType != BaseType.Object)
            throw new SchemaValidationException(
                "The root type 'Db' must be an object type (it holds the app's data).");

        // An invalid baseType / cardinality is rejected by the enum converter while
        // parsing (a JSON error), so it never reaches here — this validates structure.
        foreach (var type in desc.AllTypes())
        {
            if (type.BaseType == BaseType.Object)
            {
                if (type.Props == null || type.Props.Count == 0)
                    throw new SchemaValidationException(
                        $"Type '{type.Name}' has baseType 'object' but no fields.");

                var propNames = new HashSet<string>();
                foreach (var prop in type.Props)
                {
                    if (!propNames.Add(prop.Name))
                        throw new SchemaValidationException(
                            $"Type '{type.Name}' has duplicate prop name '{prop.Name}'.");

                    ValidateProp(prop, type.Name, seen, desc);
                }
            }
            else if (type.BaseType == BaseType.Enum)
            {
                ValidateEnum(type);
            }
            else if (type.Props != null)
            {
                throw new SchemaValidationException(
                    $"Type '{type.Name}' has props but baseType is not 'object'.");
            }
        }
    }

    // An enum type carries ≥1 value name, all unique, and no props (an enum is not an object).
    private static void ValidateEnum(TypeDefinition type)
    {
        if (type.Props != null)
            throw new SchemaValidationException(
                $"Enum type '{type.Name}' has props, but an enum carries only value names.");
        if (type.Values == null || type.Values.Count == 0)
            throw new SchemaValidationException(
                $"Enum type '{type.Name}' has no values: an enum must list at least one value.");
        var values = new HashSet<string>();
        foreach (var value in type.Values)
            if (!values.Add(value))
                throw new SchemaValidationException(
                    $"Enum type '{type.Name}' has duplicate value '{value}'.");
    }

    // initialData structure: known types, declared fields, exactly one Db entry,
    // globally-unique positive ids, and set members / single refs pointing at
    // existing entries of the right type.
    private static void ValidateInitialData(InstanceDescription desc)
    {
        if (desc.InitialData?.Extents is not { } seed) return;

        var ids = new HashSet<int>();
        foreach (var (typeName, pool) in seed)
        {
            if (desc.FindType(typeName) is null)
                throw new SchemaValidationException($"initialData references unknown type '{typeName}'.");
            foreach (var idText in pool.Keys)
            {
                if (!int.TryParse(idText, out var id) || id <= 0)
                    throw new SchemaValidationException(
                        $"initialData id '{idText}' in '{typeName}' is not a positive integer.");
                if (!ids.Add(id))
                    throw new SchemaValidationException(
                        $"initialData id {id} is used more than once (ids are global).");
            }
        }

        if (!seed.TryGetValue("Db", out var dbPool) || dbPool.Count != 1)
            throw new SchemaValidationException("initialData must contain exactly one 'Db' entry (the root).");

        foreach (var (typeName, pool) in seed)
        {
            var type = desc.FindType(typeName)!;
            foreach (var (idText, fields) in pool)
            {
                if (fields.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new SchemaValidationException(
                        $"initialData entry '{typeName}/{idText}' must be an object of fields.");
                foreach (var field in fields.EnumerateObject())
                {
                    var prop = type.Props?.FirstOrDefault(p => p.Name == field.Name)
                        ?? throw new SchemaValidationException(
                            $"initialData entry '{typeName}/{idText}' has unknown field '{field.Name}'.");

                    // A `password`-typed field is FORBIDDEN in initialData (the M-auth `password` type): a
                    // literal password in the app document would be PLAINTEXT-in-source (never hashed, since
                    // the WS write hash is the only hashing path), which is never wanted. The first admin's
                    // credential is seeded out-of-band (AdminSeed, env-var); a password is set/changed through
                    // the gated form edit. So seeding one in the document is a load error, caught here.
                    // Same forbid for a list-of-password (every slot would be plaintext-in-source).
                    if (prop.Type == "password"
                        && prop.Cardinality is Cardinality.Single or Cardinality.List)
                        throw new SchemaValidationException(
                            $"initialData entry '{typeName}/{idText}' sets the `password`-typed field " +
                            $"'{field.Name}': a password may not be seeded in the app document (it would be " +
                            $"plaintext in source). Set it via the gated form edit, or seed an admin out-of-band.");

                    if (prop.Cardinality == Cardinality.Set)
                        foreach (var m in field.Value.EnumerateArray())
                            RequireRef(seed, prop.Type, m.GetInt32(), $"{typeName}/{idText}.{field.Name}");
                    else if (prop.Cardinality == Cardinality.List && desc.IsObjectType(prop.Type))
                        foreach (var m in field.Value.EnumerateArray())
                            RequireRef(seed, prop.Type, m.GetInt32(), $"{typeName}/{idText}.{field.Name}");
                    else if (prop.Cardinality == Cardinality.Single && desc.IsObjectType(prop.Type))
                        RequireRef(seed, prop.Type, field.Value.GetInt32(), $"{typeName}/{idText}.{field.Name}");
                }
            }
        }
    }

    private static void RequireRef(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>> seed,
        string typeName, int id, string where)
    {
        if (!seed.TryGetValue(typeName, out var pool) || !pool.ContainsKey(id.ToString()))
            throw new SchemaValidationException(
                $"initialData '{where}' references id {id}, but '{typeName}' has no entry with that id.");
    }

    private static void ValidateProp(PropDefinition prop, string typeName, HashSet<string> typeNames, InstanceDescription desc)
    {
        if (!BaseTypes.IsName(prop.Type) && !typeNames.Contains(prop.Type))
            throw new SchemaValidationException(
                $"Prop '{prop.Name}' on type '{typeName}' references unknown type '{prop.Type}'.");

        // `multiline` is a presentation attribute for a single text prop only. The grammar already
        // confines it to a single prop (a set/dict/list never parses it), so this guards the remaining
        // case: a single non-text prop (`boss User multiline`). Rejected with a clear message,
        // matching how the other structural prop constraints are enforced here.
        if (prop.Multiline && (prop.Cardinality != Cardinality.Single || prop.Type != "text"))
            throw new SchemaValidationException(
                $"Prop '{prop.Name}' on type '{typeName}' is marked 'multiline', " +
                $"but 'multiline' is only valid on a single 'text' prop.");

        if (prop.Cardinality == Cardinality.Dictionary
            && prop.KeyType != null
            && !BaseTypes.IsName(prop.KeyType))
            throw new SchemaValidationException(
                $"Prop '{prop.Name}' on type '{typeName}' has unknown keyType '{prop.KeyType}'.");

        // A `password`-typed value is VALUE-ONLY — never a dict KEY (the M-auth `password` type). A dict key
        // is ADDRESSING: it goes in the URL (`/<dict>/<key>`, so logs/history/the address bar), ships to the
        // client as the entry label, and the WS write hash chokepoint transforms field VALUES, never keys —
        // so a `password` key would be stored, addressed, AND shipped as PLAINTEXT (worse than a value, and
        // un-blankable since the key IS the identity). `image` is excluded from the SAME clause for a
        // different reason: a dict key must be a stable, meaningful IDENTIFIER, and a content hash makes an
        // unreadable, unstable one (re-uploading the same field mints a different hash — the "key" would
        // silently change identity). Both are VALUE-only scalars; the set is a two-member allowlist, not
        // "the password clause" reused as-is (checked even though `BaseTypes.IsName(...)` is true above —
        // registering these as base NAMES is what makes this explicit forbid necessary).
        var forbiddenKeyBases = new[] { BaseType.Password, BaseType.Image };
        if (prop.Cardinality == Cardinality.Dictionary
            && prop.KeyType != null
            && BaseTypes.IsName(prop.KeyType)
            && forbiddenKeyBases.Contains(BaseTypes.Parse(prop.KeyType)))
            throw new SchemaValidationException(
                $"Prop '{prop.Name}' on type '{typeName}' uses a '{prop.KeyType}' dictionary key, but a " +
                $"{prop.KeyType} is value-only: a key is addressing (it appears in the URL and ships as a " +
                $"label) and {prop.KeyType} values are not stable, readable identifiers. Use a non-secret, " +
                $"non-content-addressed key type.");

        // A set is a collection of object references keyed by member identity, so
        // its element type must be an object type and it carries no key fields. A base
        // leaf or an enum (a scalar, not an object) is rejected.
        if (prop.Cardinality == Cardinality.Set)
        {
            if (BaseTypes.IsName(prop.Type) || desc.IsEnumType(prop.Type))
                throw new SchemaValidationException(
                    $"Prop '{prop.Name}' on type '{typeName}' is a set of '{prop.Type}', " +
                    $"but a set's element type must be an object type.");
            if (prop.KeyType != null)
                throw new SchemaValidationException(
                    $"Prop '{prop.Name}' on type '{typeName}' is a set and cannot declare keyType " +
                    $"(set members are keyed by their own identity).");
        }

        // A list is an ordered sequence of slots: object refs (duplicates allowed) OR scalars
        // (including password/image). No keyType. Intentional asymmetry with set (object-only).
        if (prop.Cardinality == Cardinality.List)
        {
            if (prop.KeyType != null)
                throw new SchemaValidationException(
                    $"Prop '{prop.Name}' on type '{typeName}' is a list and cannot declare keyType " +
                    $"(list slots are ordered, not keyed).");
        }
    }
}
