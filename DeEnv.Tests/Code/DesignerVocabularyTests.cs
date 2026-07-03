using System.Text.Json;
using DeEnv.Code;
using DeEnv.Instance;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Guard: the operator designer's type-editor dropdowns (kind / cardinality / prop-type) are populated from
// three vocabulary lists declared as `ui` vars in DeEnv/instances/1/app.deenv — scalarTypes / typeKinds /
// cardinalities. That vocabulary is SYSTEM data (the base scalar types, the authorable type kinds, the prop
// cardinalities). It is deliberately kept in the designer's own system code — NOT injected into the
// user-editable meta-schema (system and user content stay separate) and NOT placed on the global `sys`
// namespace (it would be dead weight in every user app). Keeping it in Code means the literal lives outside
// C#, so this test pins each list to the matching C# enum projection (the single source of truth): adding or
// removing a BaseType / Cardinality member in C# fails here until the designer's vocabulary is updated to
// match. Compared as sorted sets — the designer is free to order the options for display however it likes.
public sealed class DesignerVocabularyTests
{
    private static readonly string DesignerApp =
        Path.Combine(AppContext.BaseDirectory, "instances", "1", "app.deenv");

    [Test]
    public async Task Scalar_types_match_the_leaf_base_types() =>
        // The prop-type picker's built-in options: every BaseType EXCEPT the two non-leaf kinds (a prop's
        // type is a concrete scalar or a named type, never "object"/"enum" themselves).
        await Assert.That(Vocab("scalarTypes")).IsEqualTo(Expected(
            Enum.GetValues<BaseType>().Where(b => b is not (BaseType.Object or BaseType.Enum))));

    [Test]
    public async Task Type_kinds_match_the_authorable_base_kinds() =>
        // The kind toggle's options: exactly the two non-leaf BaseTypes a designed type can be.
        await Assert.That(Vocab("typeKinds")).IsEqualTo(Expected(new[] { BaseType.Object, BaseType.Enum }));

    [Test]
    public async Task Cardinalities_match_the_cardinality_enum() =>
        await Assert.That(Vocab("cardinalities")).IsEqualTo(Expected(Enum.GetValues<Cardinality>()));

    // System/user separation guard: the framework's reserved access-subject keyword `sys` (the C# source
    // of truth, AccessFloor.SysSubject — the host-action authority subject AND the Code namespace) must be
    // REJECTED as a user type name, symmetric with the load-time rejection in InstanceDescriptionLoader.
    // Pinned to the C# constant so a future reserved subject added there without wiring the type-name
    // rejection fails here — framework vocabulary can never share key-space with a user type.
    [Test]
    public async Task The_reserved_sys_subject_cannot_be_a_user_type_name()
    {
        var app = $$"""
            types
                Db
                    things set of {{AccessFloor.SysSubject}}
                {{AccessFloor.SysSubject}}
                    label text
            """;
        var ex = await Assert.That(() => InstanceDescriptionLoader.Load(app)).Throws<SchemaValidationException>();
        await Assert.That(ex!.Message).Contains(AccessFloor.SysSubject);
        await Assert.That(ex!.Message).Contains("reserved");
    }

    // Guard: every FRAMEWORK-OWNED history type must carry `locked` in the designer's own access section
    // (DeEnv/instances/1/app.deenv). `Commit`/`Branch` (M13's design-history rows) are writable ONLY through
    // `sys.commitDesign` — a host action that writes through the store seam BELOW the client write floor —
    // so their client-facing write floor must deny every create/edit/delete (see DECISIONS.md "Design
    // commits" / DesignCommit.feature "A client cannot edit a commit or move a branch head"). This list is
    // the C# SOURCE OF TRUTH: add a future framework history type here the moment it exists, so forgetting
    // its `locked` rule fails THIS BUILD — a red test, not a silent security gap found later. (Read via
    // AppParse.IsLockedShape, the SAME shape recognition AppPrint uses to canonicalize `where false` →
    // `locked` — so this checks the type carries the rule in EFFECT, not merely the literal word `locked`
    // in the source text.)
    private static readonly string[] FrameworkHistoryTypes = ["Commit", "Branch"]; // add here when a new one lands

    [Test]
    public async Task Every_framework_history_type_is_locked_in_the_designer_document()
    {
        var desc = AppParse.Parse(File.ReadAllText(DesignerApp));
        var rules = desc.Rules ?? [];
        foreach (var typeName in FrameworkHistoryTypes)
        {
            var typeRules = rules.Where(r => r.Type == typeName).ToList();
            await Assert.That(typeRules.Count == 1 && AppParse.IsLockedShape(typeRules[0].Verbs, typeRules[0].When))
                .IsTrue();
        }
    }

    // The string items of a designer `ui` var declared as an array literal (e.g. `var typeKinds = [...]`),
    // sorted + comma-joined so the comparison is order-independent with a readable failure message.
    private static string Vocab(string name)
    {
        var ui = AppParse.Parse(File.ReadAllText(DesignerApp)).Ui
            ?? throw new InvalidOperationException("the designer has no ui section");
        var v = ui.Vars?.FirstOrDefault(x => x.Name == name)
            ?? throw new InvalidOperationException($"the designer's ui has no var '{name}'");
        if (v.Value is not CodeArray arr)
            throw new InvalidOperationException($"designer var '{name}' is not an array literal");
        var items = arr.Items.Select(i => i is CodeText t ? t.Value
            : throw new InvalidOperationException($"var '{name}' has a non-text item"));
        return string.Join(",", items.OrderBy(s => s, StringComparer.Ordinal));
    }

    // The expected vocabulary: the enum members camelCased (the wire/Code spelling, e.g. DateTime → dateTime),
    // sorted + comma-joined to match Vocab(...).
    private static string Expected<T>(IEnumerable<T> values) where T : struct, Enum =>
        string.Join(",", values
            .Select(v => JsonNamingPolicy.CamelCase.ConvertName(v.ToString()!))
            .OrderBy(s => s, StringComparer.Ordinal));
}
