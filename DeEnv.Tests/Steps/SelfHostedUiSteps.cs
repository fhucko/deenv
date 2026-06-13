using DeEnv.Storage;
using DeEnv.Tests.TestSupport;
using Reqnroll;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace DeEnv.Tests.Steps;

// Steps for SelfHostedUi.feature — the self-hosted generic UI (milestone 9, slice 1).
// The fixture opts in with `generic` and has no hand-written views, so the all-scalar
// Note object page is rendered by the Code `objectForm` library. Page-kind and
// navigation steps ("I open", "the page is a code page", "… auto-form", "the page
// shows", "the store eventually has …") are reused from the other step bindings.
[Binding]
public sealed class SelfHostedUiSteps(InstanceContext ctx)
{
    [Given("the self-hosted form app is running")]
    public async Task GivenSelfHostedFormAppRunning()
    {
        ctx.Description = InstanceContext.SelfHostedFormDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // objectForm gives each field input (and its label) the class of its prop name
    // (class={p.name}).
    [When("I fill the {string} field with {string}")]
    public async Task WhenFillField(string field, string value) =>
        await ctx.Page!.Locator($"input.{field}").FillAsync(value);

    [Then("the {string} field is a {string} input")]
    public async Task ThenFieldInputKind(string field, string kind)
    {
        var type = await ctx.Page!.Locator($"input.{field}").GetAttributeAsync("type");
        await Assert.That(type).IsEqualTo(kind);
    }

    [Then("the {string} label reads {string}")]
    public async Task ThenLabelReads(string field, string text)
    {
        var actual = await ctx.Page!.Locator($"label.{field}").InnerTextAsync();
        await Assert.That(actual.Trim()).IsEqualTo(text);
    }

    // A set rendered inline links each member by its nested member URL (path-walk),
    // e.g. /notes/2 — not the /~/<id> id-route.
    [Then("a set member open link points at {string}")]
    public async Task ThenSetMemberLink(string href) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('a.set-open')].some(e => e.getAttribute('href') === {JsString(href)})");

    // Click the inline member link and follow it. End-to-end parity: the link string is
    // built by `nest` on the server (SSR) AND re-built by the client on hydrate (from
    // location.pathname) — following it confirms both agree and that the nested URL resolves
    // to a self-hosted page (not a C# fallback / not-found).
    [When("I follow the set member open link")]
    public async Task WhenFollowSetMember() =>
        await ctx.Page!.Locator("a.set-open").First.ClickAsync();

    [Given("the self-hosted dict app is running")]
    public async Task GivenSelfHostedDictAppRunning()
    {
        ctx.Description = InstanceContext.SelfHostedDictDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    [Given("the self-hosted scalar dict app is running")]
    public async Task GivenSelfHostedScalarDictAppRunning()
    {
        ctx.Description = InstanceContext.SelfHostedScalarDictDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    // A scalar dictionary entry's value, read at its path (/<dict>/<key>).
    [Then("the dict entry {string} eventually has value {string}")]
    public async Task ThenDictEntryHasValue(string key, string value) =>
        await EventuallyAsync(() =>
            ctx.Store!.ReadNode(NodePath.FromSegments(["settings", key])) is TextValue t && t.Text == value);

    // ── component-local state (creation prototype) ─────────────────────────────────

    [Given("the component form app is running")]
    public async Task GivenComponentFormAppRunning()
    {
        ctx.Description = InstanceContext.ComponentFormDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    [When("I fill the draft title with {string}")]
    public async Task WhenFillDraftTitle(string value) =>
        await ctx.Page!.Locator("input.draft-title").FillAsync(value);

    [When("I click create")]
    public async Task WhenClickCreate() =>
        await ctx.Page!.Locator("button.create").ClickAsync();

    [Then("the note list eventually shows {string}")]
    public async Task ThenNoteListShows(string title) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.note-row')].some(e => e.textContent.includes({JsString(title)}))");

    [Then("the draft title is empty")]
    public async Task ThenDraftTitleEmpty() =>
        await ctx.Page!.WaitForFunctionAsync("() => document.querySelector('input.draft-title')?.value === ''");

    // ── references (slice 2) ───────────────────────────────────────────────────────

    [Given("the self-hosted reference app is running")]
    public async Task GivenSelfHostedRefAppRunning()
    {
        ctx.Description = InstanceContext.SelfHostedRefDb();
        await ctx.EnsureServerAndBrowserAsync();
    }

    [Then("a reference candidate {string} is offered")]
    public async Task ThenCandidateOffered(string label) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('button.ref-pick')].some(e => e.textContent.trim() === {JsString(label)})");

    [When("I pick the reference candidate {string}")]
    public async Task WhenPickCandidate(string label) =>
        await ctx.Page!.Locator("button.ref-pick", new() { HasTextString = label }).First.ClickAsync();

    [When("I clear the reference")]
    public async Task WhenClearReference() =>
        await ctx.Page!.Locator("button.ref-clear").First.ClickAsync();

    // The reference create-new, set add, and dict new forms class their inputs by prop name.
    [When("I fill the new {string} with {string}")]
    public async Task WhenFillNewField(string field, string value) =>
        await ctx.Page!.Locator($".ref-new input.{field}, .set-new input.{field}, .dict-new input.{field}").First.FillAsync(value);

    // ── dictionaries ───────────────────────────────────────────────────────────────

    [When("I fill the new key with {string}")]
    public async Task WhenFillNewKey(string key) =>
        await ctx.Page!.Locator("input.dict-key").First.FillAsync(key);

    [When("I add the dict entry")]
    public async Task WhenAddDictEntry() =>
        await ctx.Page!.Locator("button.dict-add").First.ClickAsync();

    [When("a dict row eventually shows {string}")]
    [Then("a dict row shows {string}")]
    [Then("a dict row eventually shows {string}")]
    public async Task ThenDictRowShows(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.dict-row')].some(e => e.textContent.includes({JsString(text)}))");

    [When("I remove the dict row {string}")]
    public async Task WhenRemoveDictRow(string key) =>
        await ctx.Page!.Locator(".dict-row", new() { HasTextString = key })
            .Locator("button.dict-remove").First.ClickAsync();

    [Then("no dict row eventually shows {string}")]
    public async Task ThenNoDictRow(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => ![...document.querySelectorAll('.dict-row')].some(e => e.textContent.includes({JsString(text)}))");

    [When("I create the new object")]
    public async Task WhenCreateNewObject() =>
        await ctx.Page!.Locator("button.ref-create").First.ClickAsync();

    [When("I add to the set")]
    public async Task WhenAddToSet() =>
        await ctx.Page!.Locator("button.set-add").First.ClickAsync();

    [Then("a set row shows {string}")]
    [Then("a set row eventually shows {string}")]
    public async Task ThenSetRowShows(string text) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => [...document.querySelectorAll('.set-row')].some(e => e.textContent.includes({JsString(text)}))");

    [Then("the current reference is {string}")]
    public async Task ThenCurrentReference(string label) =>
        await ctx.Page!.WaitForFunctionAsync(
            $"() => document.querySelector('.ref-current')?.textContent.includes({JsString(label)})");

    // Reading a set single-reference route follows the reference and returns the target
    // object (ReadNode resolves it), so a points-at check is a text field on that object.
    [Then("the {string} reference eventually points at {string}")]
    public async Task ThenReferencePointsAt(string path, string label) =>
        await EventuallyAsync(() =>
        {
            var segs = path.Trim('/').Split('/', System.StringSplitOptions.RemoveEmptyEntries);
            return ctx.Store!.ReadNode(NodePath.FromSegments(segs)) is ObjectValue ov
                && ov.Fields.Values.OfType<TextValue>().Any(t => t.Text == label);
        });

    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static async Task EventuallyAsync(System.Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = System.DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (System.DateTime.UtcNow < deadline)
        {
            try { if (condition()) return; }
            catch (System.IO.IOException) { /* store mid-write — retry */ }
            await Task.Delay(50);
        }
        bool final;
        try { final = condition(); } catch (System.IO.IOException) { final = false; }
        await Assert.That(final).IsTrue();
    }
}
