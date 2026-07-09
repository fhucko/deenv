using DeEnv.Code.Parsing;
using DeEnv.Http;
using DeEnv.Instance;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Code;

// M12 eval-degrade-banner (ux review, item 3) — SsrRenderer.DegradeBannerText decides what an operator
// sees on the canvas when sys.evalContext's BuildEvalContext degrades. Pinned DIRECTLY (no host, no
// browser — cheap, the MountBaseTests precedent for a static helper): the DESIGNER-FACING exception
// family (SchemaValidationException, CodeParseException — the shapes an operator's OWN authoring
// mistakes throw) ships its message VERBATIM, wrapped in the actionable "fix the design" template; any
// OTHER exception (a genuine bug / infra failure) ships a GENERIC, calmer text instead — the full detail
// still always reaches Console.Error inside BuildEvalContext's own catch (untested here; that's a
// side-effecting log line, not this helper's job).
public sealed class EvalDegradeBannerTextTests
{
    [Test]
    public async Task A_SchemaValidationException_ships_its_message_verbatim_in_the_actionable_template() =>
        await Assert.That(SsrRenderer.DegradeBannerText(new SchemaValidationException("Type 'Db' has baseType 'object' but no fields.")))
            .IsEqualTo("Preview data unavailable: Type 'Db' has baseType 'object' but no fields. — fix the design, then Refresh values.");

    [Test]
    public async Task A_CodeParseException_ships_its_message_verbatim_in_the_actionable_template() =>
        await Assert.That(SsrRenderer.DegradeBannerText(new CodeParseException("Unexpected token at 1:5.")))
            .IsEqualTo("Preview data unavailable: Unexpected token at 1:5. — fix the design, then Refresh values.");

    // Any OTHER exception family — a genuine bug/infra failure, not an authoring mistake — must NOT leak
    // its (possibly unrelated, stack-trace-adjacent) message to the canvas: a plain InvalidOperationException
    // stands in for "anything unexpected", proving the scoping is by TYPE, not by content.
    [Test]
    public async Task Any_other_exception_ships_the_generic_text_never_its_own_message() =>
        await Assert.That(SsrRenderer.DegradeBannerText(new InvalidOperationException("some internal detail")))
            .IsEqualTo("Preview data unavailable — see the server log.");
}
