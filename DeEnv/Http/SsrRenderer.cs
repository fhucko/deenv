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

    public SsrRenderer(IInstanceStore store, InstanceDescription desc, ClientSessionStore? sessions = null, int infraPort = 0)
    {
        _store = store;
        _desc = desc;
        _resolver = new TypeResolver(desc);
        _sessions = sessions;
        _infraPort = infraPort;
        _ui = GenericUi.Effective(desc);
    }

    public string Render(string urlPath)
    {
        // Every page is code-owned now: a fully-custom `fn render()`, or the self-hosted
        // generic UI (the default). A URL no view matches — an unknown path, or a bare
        // scalar field route nothing links to — is NotFound.
        if (_ui != null && ResolveView(urlPath) != null)
            return RenderUi(urlPath);
        return NotFoundPage(urlPath, ParsePath(urlPath));
    }

    // ── the rendering-function decision ─────────────────────────────────────────

    private enum ViewKind { Render, Type }

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

    // Execute the resolved view over a prepared top scope and serialize the
    // resulting tag tree to HTML. A runtime error on first paint becomes an SSR
    // error page; a missing view target becomes the NotFound page.
    private string RenderUi(string urlPath)
    {
        var context = new ExecContext();
        try
        {
            var (result, title, scope, match, targetId) = ExecuteRender(urlPath, context);
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

            // A type view keeps the generic breadcrumb chrome (plain links) around
            // its content; path views and the root render own the page.
            var breadcrumbs = match.Kind == ViewKind.Type ? Breadcrumbs(match.TargetPath!) : "";

            return UiLayout(title, breadcrumbs, body.ToString(), ScriptSafe(initData), ScriptSafe(initUi), clientId, _infraPort);
        }
        catch (ViewTargetNotFoundException)
        {
            return NotFoundPage(urlPath, ParsePath(urlPath));
        }
        catch (CodeRuntimeException ex)
        {
            // A user-code error: its message belongs on the page.
            return Layout("Error", $"<main><h1>Error</h1><p>{Escape(ex.Message)}</p></main>");
        }
        catch (Exception ex)
        {
            // An engine bug: log the details, show nothing internal.
            Console.Error.WriteLine($"SSR render of '{urlPath}' failed: {ex}");
            return Layout("Error", "<main><h1>Error</h1><p>Internal error.</p></main>");
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
        var (_, _, scope, _, _) = ExecuteRender(urlPath, context, sessionVars, warmDb);
        return ClientState.Serialize(scope, context);
    }

    private (IExecTagChild Result, string Title, ExecScope Scope, ViewMatch Match, int? TargetId) ExecuteRender(
        string urlPath, ExecContext context, IReadOnlyDictionary<string, IExecValue>? sessionVars = null,
        ExecObject? warmDb = null)
    {
        var ui = _ui!;
        var exec = new CodeExecutor(_store);
        var scope = new ExecScope();

        // db root (the object graph), read-only at the top. A recompute reuses the warm
        // graph the session holds (already reflecting the client's mutations) instead of
        // reloading from storage.
        var db = warmDb ?? DbBridge.LoadRoot(_store, _desc, context);
        scope.Items["db"] = new ExecScopeItem { Value = db, IsReadOnly = true };

        // Functions first (close over the same top scope → mutual recursion) so var
        // initializers may call them — including server-only ones.
        foreach (var f in _desc.Common?.Functions ?? []) DefineFunction(f, scope);
        foreach (var f in ui.Functions ?? []) DefineFunction(f, scope);

        // UI/session state. Each initializer is a memoized computation (`var:<name>`),
        // so its inputs become dependencies (not shipped) and only the resulting value
        // is shipped in scope. `path` is the requested URL (routing).
        foreach (var v in ui.Vars ?? [])
        {
            var value = v.Value is { } init
                ? CodeExecutor.Memoize($"var:{v.Name}", context, () => exec.ExecuteValue(init, scope, context))
                : new ExecNull();
            scope.Items[v.Name] = new ExecScopeItem { Value = value, IsReadOnly = false };
        }
        if (scope.Items.ContainsKey("path"))
            scope.Items["path"] = new ExecScopeItem { Value = new ExecText { Value = urlPath }, IsReadOnly = false };

        // Client-held session vars (a refetch) override their just-computed values, so the
        // re-render sees the same UI state the client has. Computed vars (e.g. a filtered
        // list) are not shipped by the client and so recompute fresh here. Read-only items
        // (db, functions) are never overridable.
        if (sessionVars != null)
            foreach (var (name, value) in sessionVars)
                if (scope.Items.TryGetValue(name, out var it) && !it.IsReadOnly) it.Value = value;

        // The rendering-function decision, re-resolved here so the refetch path
        // (RenderState → ExecuteRender) renders the same view as the page did.
        var match = ResolveView(urlPath)
            ?? throw new CodeRuntimeException("No view matches this URL.");

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

        var result = exec.InvokeFunction(match.Fn, args, scope, context);

        if (result is not IExecTagChild child)
            throw new CodeRuntimeException("The view did not return a renderable value.");

        // Title: the app's `title` var when set; a type-view page falls back to the
        // generic page title (its node path), other code pages to "DeEnv".
        var title = scope.Items.TryGetValue("title", out var t) && t.Value is ExecText titleText
            ? titleText.Value
            : match.Kind == ViewKind.Type ? PageTitle(match.TargetPath!) : "DeEnv";
        return (child, title, scope, match, targetId);
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

    private void SerializeChild(IExecTagChild child, StringBuilder sb)
    {
        switch (child)
        {
            case ExecTag tag:
                SerializeTag(tag, sb);
                break;
            case ExecArray coll:
                // foreach / where / orderBy flatten into the child stream.
                foreach (var item in coll.Items) SerializeChild(item.Value, sb);
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

    private void SerializeTag(ExecTag tag, StringBuilder sb)
    {
        sb.Append('<').Append(tag.Name);
        foreach (var (name, value) in tag.Attributes)
            AppendCodeAttribute(sb, name, value);
        sb.Append('>');

        if (VoidElements.Contains(tag.Name)) return;

        foreach (var child in tag.Children) SerializeChild(child, sb);
        sb.Append("</").Append(tag.Name).Append('>');
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

    // ── not found ─────────────────────────────────────────────────────────────

    private string NotFoundPage(string urlPath, NodePath path)
    {
        var parent = path.IsRoot ? "/" : ParentUrl(path);
        var body = $"""
            {Breadcrumbs(path)}
            <main>
              <p>Not found: <code>{Escape(urlPath)}</code></p>
              <p><a href="{Escape(parent)}">← Back</a></p>
            </main>
        """;
        return Layout("Not found", body);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // A static shell for the NotFound page (no client script — there is nothing to hydrate).
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

    private static string ParentUrl(NodePath path)
    {
        var segs = path.Segments.Take(path.Segments.Count - 1);
        return "/" + string.Join("/", segs);
    }

    private static NodePath ParsePath(string urlPath)
    {
        var segs = urlPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return NodePath.FromSegments(segs);
    }

    private static string Escape(string s) =>
        System.Net.WebUtility.HtmlEncode(s);
}
