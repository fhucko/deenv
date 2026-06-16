using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Code;
using DeEnv.Instance;
using DeEnv.Storage;

namespace DeEnv.Http;

public sealed class SsrRenderer
{
    private readonly IInstanceStore _store;
    private readonly InstanceDescription _desc;
    private readonly TypeResolver _resolver;
    private readonly ClientSessionStore? _sessions;

    // The ui actually rendered/shipped: the app's `fn render()`, or — the default — the
    // self-hosted generic UI (objectForm/refEditor/setTable/dictTable/leafForm + synthesized
    // per-type views). Every page is code-owned now; there is no C# auto-form. The canonical
    // _desc stays pristine for printing; _ui carries the render-time synthesis.
    private readonly InstanceUi? _ui;

    // The infra port (where /ws and /js are served) — injected into the page so the client
    // loads its bundle and opens its WebSocket against it, keeping the app port a clean,
    // reserved-path-free data URL space.
    private readonly int _infraPort;

    // Names of the synthesized framework members (the generic library + descriptor registries)
    // — placed in the system scope, above the custom code, so they never pollute the app scope.
    private readonly IReadOnlySet<string> _systemNames;

    // The kernel's instance registry as a live DATA cell (app + ports per hosted instance), surfaced
    // to image Code as the read-only `instances` system global. Read PER RENDER (`.Current`), so every
    // fresh render reflects the kernel's CURRENT instances — a newly-created instance shows up on every
    // instance's next request, not a frozen boot snapshot. A var-shaped cell (not a pull-function), so a
    // future live-update path can hang change-notification on it. Defaults to empty (no kernel ⇒ no list).
    private readonly LiveRegistry _registry;

    public SsrRenderer(IInstanceStore store, InstanceDescription desc, ClientSessionStore? sessions = null,
        int infraPort = 0, LiveRegistry? registry = null)
    {
        _store = store;
        _desc = desc;
        _resolver = new TypeResolver(desc);
        _sessions = sessions;
        _infraPort = infraPort;
        _registry = registry ?? new LiveRegistry();
        (_ui, _systemNames) = GenericUi.Effective(desc);
    }

    // The rendered HTML plus the first-paint HTTP status (200 unless code set it, e.g. the
    // self-hosted NotFound view sets 404).
    public (string Html, int Status) Render(string urlPath) => RenderUi(urlPath);

    // ── the rendering-function decision ─────────────────────────────────────────

    private enum ViewKind { Render, Type, NotFound }

    private sealed record ViewMatch(ViewKind Kind, CodeFunction Fn, UiView? View, NodePath? TargetPath);

    // Which render function (if any) owns this URL:
    //   1. `fn render()` — the fully-custom UI, owns the whole URL space;
    //   2. else, the synthesized generic view for the routed node (object page, or a
    //      reference / set route) — the self-hosted generic UI is the default, so this
    //      covers every app without a custom render, as long as no traversal segment
    //      walks INTO a dictionary entry (those entry pages still fall to the C# form);
    //   3. else null: the C# auto-form (a dict route/entry, the `/~/{id}` id-route).
    private ViewMatch? ResolveView(string urlPath)
    {
        var ui = _ui;
        if (ui == null) return null;

        if (ui.Render != null)
            return new ViewMatch(ViewKind.Render, ui.Render, null, null);

        if (ui.Views is not { Count: > 0 }) return null;
        var nodePath = ParsePath(urlPath);
        var typeInfo = _resolver.ResolveType(nodePath);
        if (typeInfo == null) return null;

        // A set route (/notes): the self-hosted set table, bound to the OWNER object that
        // holds the set (keyed by owner type + prop) — like a reference route.
        if (typeInfo is { Cardinality: Cardinality.Set, Type.BaseType: BaseType.Object })
            return ResolveOwnerBoundView(ui, nodePath);

        // A dictionary route (/settings): the self-hosted dict table, bound to the OWNER.
        if (typeInfo is { Cardinality: Cardinality.Dictionary })
            return ResolveOwnerBoundView(ui, nodePath);

        // A SCALAR dictionary entry (/settings/<key>): a single value reached by traversing a
        // dict. The shared leaf editor renders it, bound to the entry object (its value
        // persists path-addressed). Object entries fall through to the object view below.
        if (typeInfo is { Cardinality: Cardinality.Single, Type.BaseType: not BaseType.Object }
            && _resolver.TraversesDictionary(nodePath))
        {
            var leaf = ui.Views.FirstOrDefault(v => v.Type == GenericUi.LeafViewType);
            return leaf == null ? null : new ViewMatch(ViewKind.Type, leaf.Fn, leaf, nodePath);
        }

        if (typeInfo is not { Cardinality: Cardinality.Single, Type.BaseType: BaseType.Object }) return null;

        // A single-reference route (e.g. /lead): the reference editor, bound to the PARENT
        // object — never the (maybe-unset) target — so an unset reference is the empty
        // editor, not NotFound.
        if (typeInfo.IsReference)
            return ResolveOwnerBoundView(ui, nodePath);

        // An ordinary object page (a set member / the routed object): the object view
        // (Prop == null excludes the synthesized reference / set views).
        var typeView = ui.Views.FirstOrDefault(v => v.Type == typeInfo.Type.Name && v.Prop == null);
        return typeView == null ? null : new ViewMatch(ViewKind.Type, typeView.Fn, typeView, nodePath);
    }

    // A view that owns the route of a prop (a reference or a set), keyed by (owner type,
    // prop) and bound to the parent object that holds it.
    private ViewMatch? ResolveOwnerBoundView(InstanceUi ui, NodePath nodePath)
    {
        var prop = nodePath.Segments[^1];
        var parentPath = NodePath.FromSegments(nodePath.Segments.Take(nodePath.Segments.Count - 1));
        var ownerType = _resolver.ResolveType(parentPath)?.Type.Name;
        var view = (ui.Views ?? []).FirstOrDefault(v => v.Type == ownerType && v.Prop == prop);
        return view == null ? null : new ViewMatch(ViewKind.Type, view.Fn, view, parentPath);
    }

    // The routed object of a type view was deleted (or a reference is unset): the
    // page is NotFound, not a code error.
    private sealed class ViewTargetNotFoundException : Exception;

    // ── code-owned UI (view pages; `fn render()` is the root view) ──────────────

    // The synthesized NotFound view (a self-hosted 404 page), used for an unrouted URL or a
    // deleted view target.
    private ViewMatch NotFoundMatch()
    {
        var v = (_ui?.Views ?? []).FirstOrDefault(x => x.Type == GenericUi.NotFoundViewType)
            ?? throw new InvalidOperationException("No NotFound view was synthesized.");
        return new ViewMatch(ViewKind.NotFound, v.Fn, v, null);
    }

    // Resolve the view for this URL (an unrouted URL falls to the self-hosted NotFound) and
    // render it. A runtime error on first paint becomes an SSR error page.
    private (string Html, int Status) RenderUi(string urlPath) =>
        RenderPage(urlPath, ResolveView(urlPath) ?? NotFoundMatch());

    private (string Html, int Status) RenderPage(string urlPath, ViewMatch match)
    {
        var context = new ExecContext();
        try
        {
            var (result, title, scope, _, targetId, status) = ExecuteRender(urlPath, context, forcedMatch: match);
            var body = new StringBuilder();
            SerializeChild(result, body);

            // Mint a session and ship its clientId, so the WS can claim it (hello) and a
            // later milestone can hang per-client push on it. The id is all the client
            // needs; a refetch re-renders over a fresh store load. See ClientSession.
            var clientId = _sessions?.Create().Id ?? "";

            // First-paint state: only what the client-run render accessed (access-scoped,
            // sensitive fields denied) + the client-facing AST. Server-only functions and
            // the var initializers (which may compute from withheld data) never ship — the
            // client re-defines client functions and reads var *values* from initData.
            var initData = ClientState.Serialize(scope, context).ToJsonString();
            var clientUi = new InstanceUi(
                Vars: null,
                Functions: _ui!.Functions?.Where(f => !f.ServerOnly).ToList(),
                Render: _ui.Render,
                Views: _ui.Views);
            var clientCommon = _desc.Common?.Functions is { } common
                ? new InstanceCommon(common.Where(f => !f.ServerOnly).ToList())
                : null;

            // The resolved rendering-function decision, so the client re-renders the
            // same view: which view, and (type views) the routed object's id.
            var viewInfo = new JsonObject { ["kind"] = match.Kind.ToString().ToLowerInvariant() };
            if (match.View != null) viewInfo["index"] = _ui.Views!.ToList().IndexOf(match.View);
            if (targetId is { } id) viewInfo["objectId"] = id;

            var initUi = new JsonObject
            {
                ["ui"] = JsonSerializer.SerializeToNode(clientUi, SchemaJson.Options),
                ["common"] = clientCommon is null ? null : JsonSerializer.SerializeToNode(clientCommon, SchemaJson.Options),
                ["view"] = viewInfo,
            }.ToJsonString();

            // A type view (and the NotFound page) keeps the generic breadcrumb chrome (plain
            // links) around its content, so a missing page can still navigate back up; a
            // full-custom render owns the whole page.
            var breadcrumbs = match.Kind switch
            {
                ViewKind.Type => Breadcrumbs(match.TargetPath!),
                ViewKind.NotFound => Breadcrumbs(ParsePath(urlPath)),
                _ => "",
            };

            return (UiLayout(title, breadcrumbs, body.ToString(), ScriptSafe(initData), ScriptSafe(initUi), clientId, _infraPort),
                status);
        }
        catch (ViewTargetNotFoundException)
        {
            // The routed object was deleted (or a reference is unset): render the self-hosted
            // NotFound view (404), unless we were already rendering it.
            return match.Kind == ViewKind.NotFound
                ? (Layout("Not found", "<main><h1>Not found</h1></main>"), 404)
                : RenderPage(urlPath, NotFoundMatch());
        }
        catch (CodeRuntimeException ex)
        {
            // A user-code error: its message belongs on the page.
            return (Layout("Error", $"<main><h1>Error</h1><p>{Escape(ex.Message)}</p></main>"), 200);
        }
        catch (Exception ex)
        {
            // An engine bug: log the details, show nothing internal.
            Console.Error.WriteLine($"SSR render of '{urlPath}' failed: {ex}");
            return (Layout("Error", "<main><h1>Error</h1><p>Internal error.</p></main>"), 200);
        }
    }

    // Neutralise "</script>" (and any "<") so an embedded JSON literal can't break out
    // of the inline <script> element.
    private static string ScriptSafe(string json) => json.Replace("<", "\\u003c");

    // Re-render to a client-state payload ({ leaves, scope, cache }) without producing
    // HTML. Used by the WS `refetch` (Stage 4b): after a mutation the client re-asks for
    // the entries it cannot recompute locally (hidden deps). The render runs over the
    // session's warm db graph (kept in sync with the client's mutations) with the client's
    // session vars, and returns authoritative state.
    // `lastIdFloor` is the client's current transient-id counter: the re-render mints
    // its transients below it, so shipped negative ids never collide with the drafts
    // the client already holds.
    public JsonObject RenderState(
        string urlPath, IReadOnlyDictionary<string, IExecValue>? sessionVars, ExecObject? warmDb, int lastIdFloor = 0)
    {
        var context = new ExecContext();
        context.LastId.Value = Math.Min(0, lastIdFloor);
        var (_, _, scope, _, _, _) = ExecuteRender(urlPath, context, sessionVars, warmDb);
        return ClientState.Serialize(scope, context);
    }

    private (IExecTagChild Result, string Title, ExecScope Scope, ViewMatch Match, int? TargetId, int Status) ExecuteRender(
        string urlPath, ExecContext context, IReadOnlyDictionary<string, IExecValue>? sessionVars = null,
        ExecObject? warmDb = null, ViewMatch? forcedMatch = null)
    {
        var ui = _ui!;
        var exec = new CodeExecutor(_store);

        // Three scopes, so the generic-UI internals are OUTSIDE userspace (not just above it):
        //   system   — framework state (db, path, status), the shared parent both can read;
        //   internal — the synthesized generic library + descriptor registries (__descs/
        //              __dictDescs), a SIBLING of app, so user code can never reach them;
        //   app      — the user's own vars/functions/render.
        var system = new ExecScope { IsTop = true };
        var internalScope = new ExecScope { Parent = system, IsTop = true };
        var app = new ExecScope { Parent = system, IsTop = true };

        // db root (the object graph), read-only. A recompute reuses the warm graph the
        // session holds (already reflecting the client's mutations) instead of reloading.
        var db = warmDb ?? DbBridge.LoadRoot(_store, _desc, context);
        system.Items["db"] = new ExecScopeItem { Value = db, IsReadOnly = true };

        // `sys` is the framework namespace object, read-only: it holds the less-common framework
        // members (the builtins are dispatched by the sys-rooted-callee rule; `sys.instances` is the
        // one stateful member). `sys.instances` is the kernel's instance registry — the list of
        // instances this kernel hosts (app name + ports), provided so image Code can render the list
        // itself (the first kernel-as-data read path). Built PER RENDER, so every render reflects the
        // kernel's CURRENT instances; empty when there is no kernel.
        system.Items["sys"] = new ExecScopeItem
        {
            Value = new ExecObject
            {
                Id = --context.LastId.Value,
                Props = new Dictionary<string, IExecValue> { ["instances"] = BuildRegistry(context) },
            },
            IsReadOnly = true,
        };

        // `status` is a framework state var (the first-paint HTTP status); the view may assign
        // it (e.g. NotFound sets `status = 404`). Read back after render. Default 200.
        system.Items["status"] = new ExecScopeItem { Value = new ExecInt { Value = 200 }, IsReadOnly = false };

        // Functions first (close over their scope → mutual recursion) so var initializers may
        // call them. The synthesized generic library goes in the internal scope; the user's
        // own functions (common + ui) go in the app scope.
        foreach (var f in _desc.Common?.Functions ?? []) DefineFunction(f, app);
        foreach (var f in ui.Functions ?? []) DefineFunction(f, _systemNames.Contains(f.Name ?? "") ? internalScope : app);

        // UI/session state. Each initializer is a memoized computation (`var:<name>`),
        // evaluated in its own scope. The synthesized registries (__descs/__dictDescs) go in
        // the internal scope; the user's vars go in the app scope.
        foreach (var v in ui.Vars ?? [])
        {
            var target = _systemNames.Contains(v.Name) ? internalScope : app;
            var value = v.Value is { } init
                ? CodeExecutor.Memoize($"var:{v.Name}", context, () => exec.ExecuteValue(init, target, context))
                : new ExecNull();
            target.Items[v.Name] = new ExecScopeItem { Value = value, IsReadOnly = false };
        }

        // Client-held session vars (a refetch) override the user's just-computed values, so the
        // re-render sees the same UI state the client has. Computed vars (e.g. a filtered list)
        // are not shipped by the client and so recompute fresh here.
        if (sessionVars != null)
            foreach (var (name, value) in sessionVars)
                if (app.Items.TryGetValue(name, out var it) && !it.IsReadOnly) it.Value = value;

        // `path` is framework-provided (not declared by the app), always the requested URL —
        // the request is authoritative for routing. It lives in the system scope (writable;
        // the client mirrors it to location.pathname and updates it on navigation).
        system.Items["path"] = new ExecScopeItem { Value = new ExecText { Value = urlPath }, IsReadOnly = false };

        // The rendering-function decision: the caller's forced match (RenderPage passes the
        // resolved view, incl. NotFound), or re-resolved here so the refetch path
        // (RenderState → ExecuteRender) renders the same view as the page did.
        var match = forcedMatch ?? ResolveView(urlPath)
            ?? throw new CodeRuntimeException("No view matches this URL.");

        // The synthesized generic views run in the INTERNAL scope (they use the library +
        // registries); a custom `fn render()` runs in the APP scope. Either way the chain
        // reaches system (db/path/status).
        var renderScope = match.Kind == ViewKind.Render ? app : internalScope;

        // A type view's parameters: (1) the routed object, found by walking the URL
        // segments through the SAME graph instance bound as `db` (so leaves, session
        // mirroring and identity all line up); (2) the request URL as `base`, so the
        // generic UI builds nested member links (nest(base, prop) → /notes/3). Bound as
        // call arguments — never top-scope vars — so they can't be overridden or shipped.
        int? targetId = null;
        IExecValue[] args = [];
        if (match.Kind == ViewKind.Type)
        {
            var target = FindTarget(db, match.TargetPath!) ?? throw new ViewTargetNotFoundException();
            targetId = target.Id;
            args = [target, new ExecText { Value = urlPath }];
        }

        var result = exec.InvokeFunction(match.Fn, args, renderScope, context);

        if (result is not IExecTagChild child)
            throw new CodeRuntimeException("The view did not return a renderable value.");

        // Title: the app's `title` var (in the app scope) when set; a type-view page falls back
        // to the generic page title (its node path), the NotFound page to "Not found", else "DeEnv".
        var title = app.Items.TryGetValue("title", out var t) && t.Value is ExecText titleText
            ? titleText.Value
            : match.Kind == ViewKind.Type ? PageTitle(match.TargetPath!)
            : match.Kind == ViewKind.NotFound ? "Not found"
            : "DeEnv";

        // The first-paint HTTP status: the (possibly view-assigned) `status` system var.
        var status = system.Items.TryGetValue("status", out var s) && s.Value is ExecInt si ? si.Value : 200;
        // Ship the render scope (internal for a generic view → its __descs; app for a custom
        // render → its vars) plus the system parent (ClientState walks up).
        return (child, title, renderScope, match, targetId, status);
    }

    // Walk URL segments through the loaded object graph: a set member segment is the
    // item's identity key, a field segment is a prop. Null when anything is missing
    // (deleted member, unset reference) — the caller renders NotFound.
    private static ExecObject? FindTarget(ExecObject root, NodePath path)
    {
        IExecValue current = root;
        foreach (var segment in path.Segments)
        {
            if (current is ExecArray { Kind: ArrayKind.Dict } dict)
                // A dict entry segment is its key; match the entry carrying that __key.
                current = dict.Items.FirstOrDefault(i =>
                    (i.Value as ExecObject)?.Props.GetValueOrDefault(DbBridge.EntryKeyProp) is ExecText k
                    && k.Value == segment)?.Value ?? new ExecNull();
            else if (current is ExecArray arr && int.TryParse(segment, out var id))
                current = arr.Items.FirstOrDefault(i => i.Key == id)?.Value ?? new ExecNull();
            else if (current is ExecObject obj && obj.Props.TryGetValue(segment, out var value))
                current = value;
            else
                return null;
        }
        return current as ExecObject;
    }

    // Page shell for a code page: optional generic chrome (a type view keeps the
    // breadcrumbs) around the `#app` mount the client reconciles into; an inline bootstrap
    // injects the bundle from the infra port (/js), which hydrates from window.initUi /
    // window.initData. The chrome CSS ships only when there IS chrome — a full-takeover
    // page stays unstyled (the app's own look).
    private static string UiLayout(
        string title, string breadcrumbs, string body, string initData, string initUi, string clientId, int infraPort) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>{{Escape(title)}}</title>{{(breadcrumbs.Length > 0 ? $"\n  <style>{ViewChromeCss}</style>" : "")}}
          <script>window.initData={{initData}};window.initUi={{initUi}};window.initClientId="{{clientId}}";window.initInfraPort={{infraPort}};</script>
          <script>(function(){var s=document.createElement("script");s.src=location.protocol+"//"+location.hostname+":"+window.initInfraPort+"/js";document.head.appendChild(s);})();</script>
        </head>
        <body>{{breadcrumbs}}<div id="app">{{body}}</div></body>
        </html>
        """;

    private const string ViewChromeCss = """
        body { font-family: system-ui, Arial, sans-serif; margin: 2rem; color: #222; }
        nav.breadcrumbs { margin-bottom: 1rem; color: #666; }
        """;

    private static void DefineFunction(CodeFunction fn, ExecScope scope)
    {
        if (fn.Name == null) return;
        scope.Items[fn.Name] = new ExecScopeItem
        {
            Value = new ExecFunction { Function = fn, Scope = scope },
            IsReadOnly = true,
        };
    }

    // HTML void elements: rendered without a closing tag or children.
    private static readonly HashSet<string> VoidElements =
        ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"];

    // `selectValue` is the bound value of an enclosing <select> (null otherwise), threaded so an
    // <option> can mark itself `selected` when its own `value` matches the selection — the SSR half
    // of <select> two-way binding (see SerializeTag's select branch).
    private void SerializeChild(IExecTagChild child, StringBuilder sb, IExecValue? selectValue = null)
    {
        switch (child)
        {
            case ExecTag tag:
                SerializeTag(tag, sb, selectValue);
                break;
            case ExecArray coll:
                // foreach / where / orderBy flatten into the child stream (e.g. a select's options
                // built by foreach), so the select's value carries through the flattening.
                foreach (var item in coll.Items) SerializeChild(item.Value, sb, selectValue);
                break;
            case ExecText text:
                sb.Append(Escape(text.Value));
                break;
            case ExecInt i:
                sb.Append(Escape(i.Value.ToString(CultureInfo.InvariantCulture)));
                break;
            case ExecBool b:
                sb.Append(b.Value ? "true" : "false");
                break;
            // null / nothing / functions render nothing.
        }
    }

    private void SerializeTag(ExecTag tag, StringBuilder sb, IExecValue? selectValue = null)
    {
        // A <textarea>'s value is its TEXT CONTENT, not a `value` attribute (browsers ignore
        // `value` on <textarea>), so its bound `value` is emitted as escaped content below and
        // skipped in the attribute loop. The client twin (ui.ts) mirrors this: it sets the
        // .value property, never the attribute.
        var isTextarea = tag.Name == "textarea";

        // A <select value={x}> drives which <option> is `selected` — `value` is not real HTML on a
        // <select> (the browser ignores it), so it is skipped in the attribute loop and instead passed
        // down to the option children. An <option> whose own `value` equals the selection gets `selected`.
        var isSelect = tag.Name == "select";
        var childSelectValue =
            isSelect && tag.Attributes.TryGetValue("value", out var selVal) ? selVal : null;
        var isSelectedOption =
            tag.Name == "option" && selectValue != null
            && tag.Attributes.TryGetValue("value", out var optVal) && ScalarsEqual(optVal, selectValue);

        sb.Append('<').Append(tag.Name);
        foreach (var (name, value) in tag.Attributes)
        {
            if ((isTextarea || isSelect) && name == "value") continue;
            AppendCodeAttribute(sb, name, value);
        }
        if (isSelectedOption) sb.Append(" selected");
        sb.Append('>');

        if (VoidElements.Contains(tag.Name)) return;

        // The bound value as the first content (HTML-escaped, same scalar handling as
        // AppendCodeAttribute/SerializeChild), then any literal children.
        if (isTextarea && tag.Attributes.TryGetValue("value", out var v))
            AppendTextareaValue(sb, v);

        foreach (var child in tag.Children) SerializeChild(child, sb, childSelectValue);
        sb.Append("</").Append(tag.Name).Append('>');
    }

    // Equality for two scalar exec values, used to match an <option>'s value against the enclosing
    // <select>'s selection. Cross-type int/text compare by their canonical text (an int option value
    // authored as a number must match a text-typed selection that holds its digits, and vice versa) —
    // the same lenient text coercion the client uses (option.value is always a DOM string).
    private static bool ScalarsEqual(IExecValue a, IExecValue b) =>
        ScalarText(a) is { } at && ScalarText(b) is { } bt && at == bt;

    private static string? ScalarText(IExecValue v) => v switch
    {
        ExecText t => t.Value,
        ExecInt i => i.Value.ToString(CultureInfo.InvariantCulture),
        ExecBool b => b.Value ? "true" : "false",
        _ => null,
    };

    // The scalar value bound to a <textarea>, as HTML-escaped element content. Non-scalar
    // values (an unset/null bind) render nothing; the bool case is unreachable for a value
    // bind but handled for completeness, matching SerializeChild.
    private static void AppendTextareaValue(StringBuilder sb, IExecValue value)
    {
        switch (value)
        {
            case ExecText t:
                sb.Append(Escape(t.Value));
                break;
            case ExecInt i:
                sb.Append(Escape(i.Value.ToString(CultureInfo.InvariantCulture)));
                break;
        }
    }

    // Only scalar attribute values become HTML attributes. Function values (event
    // handlers like onClick) and objects/arrays/null are wired on the client, not
    // serialized. A bool attribute follows HTML semantics: present iff true.
    private static void AppendCodeAttribute(StringBuilder sb, string name, IExecValue value)
    {
        switch (value)
        {
            case ExecText t:
                sb.Append(' ').Append(name).Append("=\"").Append(Escape(t.Value)).Append('"');
                break;
            case ExecInt i:
                sb.Append(' ').Append(name).Append("=\"").Append(i.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
                break;
            case ExecBool b:
                if (b.Value) sb.Append(' ').Append(name);
                break;
        }
    }

    // Build the read-only `instances` Code collection from the registry snapshot: one row per
    // hosted instance, { id, app, port, assetsPort, designId } scalars. A transient List (negative ids),
    // like a where/orderBy result — an app reads the rows in output position, so they ship as leaves and
    // the list survives hydration. Empty when there is no kernel. `id` is the host-action address (e.g.
    // sys.publish(db, i.id)), unique per instance and the sole key to its files; `app` is a display
    // name only; `designId` is the explicit reference to the IDE design this instance runs (0 = none),
    // read by the IDE to pre-select the design dropdown. clone/delete/publish work on ANY instance, so
    // there is no created/boot flag.
    private ExecArray BuildRegistry(ExecContext context)
    {
        var items = new List<ExecItem>();
        foreach (var info in _registry.Current)
            items.Add(new ExecItem
            {
                Key = --context.LastId.Value,
                Value = new ExecObject
                {
                    Id = --context.LastId.Value,
                    Props = new Dictionary<string, IExecValue>
                    {
                        ["id"] = new ExecInt { Value = info.Id },
                        ["app"] = new ExecText { Value = info.App },
                        ["port"] = new ExecInt { Value = info.Port },
                        ["assetsPort"] = new ExecInt { Value = info.AssetsPort },
                        ["designId"] = new ExecInt { Value = info.DesignId ?? 0 },
                    },
                },
            });
        return new ExecArray { Items = items, Id = --context.LastId.Value, Kind = ArrayKind.List };
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // A static shell for an engine-level fallback page (an SSR error, or a NotFound when
    // even the synthesized NotFound view is unavailable). Normal pages use UiLayout.
    private static string Layout(string title, string body) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>{Escape(title)}</title>
          <style>{Css}</style>
        </head>
        <body>
        {body}
        </body>
        </html>
        """;

    private const string Css = """
        body { font-family: system-ui, Arial, sans-serif; margin: 2rem; color: #222; }
        nav.breadcrumbs { margin-bottom: 1rem; color: #666; }
        """;

    private static string Breadcrumbs(NodePath path)
    {
        var sb = new StringBuilder("<nav class=\"breadcrumbs\"><a href=\"/\">Db</a>");
        var url = "";
        foreach (var seg in path.Segments)
        {
            url += "/" + seg;
            sb.Append($" / <a href=\"{Escape(url)}\">{Escape(seg)}</a>");
        }
        sb.Append("</nav>");
        return sb.ToString();
    }

    private static string PageTitle(NodePath path) =>
        path.IsRoot ? "Db" : "Db / " + string.Join(" / ", path.Segments);

    private static NodePath ParsePath(string urlPath)
    {
        var segs = urlPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return NodePath.FromSegments(segs);
    }

    private static string Escape(string s) =>
        System.Net.WebUtility.HtmlEncode(s);
}
