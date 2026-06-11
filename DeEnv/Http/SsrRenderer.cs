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

    public SsrRenderer(IInstanceStore store, InstanceDescription desc)
    {
        _store = store;
        _desc = desc;
        _resolver = new TypeResolver(desc);
    }

    public string Render(string urlPath)
    {
        // Code owns all routing when a `ui` section exists; the generic auto-form
        // below is the fallback only when there is no `ui`.
        if (_desc.Ui != null)
            return RenderUi(urlPath);

        var nodePath = ParsePath(urlPath);

        // id-route: /~/{id} follows a bare reference to its object in the extent.
        if (nodePath.Segments.Count >= 1 && nodePath.Segments[0] == "~")
            return RenderIdRoute(urlPath, nodePath);

        var typeInfo = _resolver.ResolveType(nodePath);

        if (typeInfo == null)
            return NotFoundPage(urlPath, nodePath);

        var node = _store.ReadNode(nodePath);
        if (node == null)
            return NotFoundPage(urlPath, nodePath);

        return Page(urlPath, nodePath, typeInfo, node);
    }

    // ── code-owned UI (the `ui` section) ────────────────────────────────────────

    // Execute the render fn over a prepared top scope and serialize the resulting
    // tag tree to HTML. A runtime error on first paint becomes an SSR error page.
    private string RenderUi(string urlPath)
    {
        var context = new ExecContext();
        try
        {
            var (result, title, scope) = ExecuteRender(urlPath, context);
            var body = new StringBuilder();
            SerializeChild(result, body);

            // First-paint state: only what the client-run render accessed (access-scoped,
            // sensitive fields denied) + the client-facing AST. Server-only functions and
            // the var initializers (which may compute from withheld data) never ship — the
            // client re-defines client functions and reads var *values* from initData.
            var initData = ClientState.Serialize(scope, context, _desc).ToJsonString();
            var clientUi = new InstanceUi(
                Vars: null,
                Functions: _desc.Ui!.Functions?.Where(f => !f.ServerOnly).ToList(),
                Render: _desc.Ui.Render);
            var clientCommon = _desc.Common?.Functions is { } common
                ? new InstanceCommon(common.Where(f => !f.ServerOnly).ToList())
                : null;
            var initUi = new JsonObject
            {
                ["ui"] = JsonSerializer.SerializeToNode(clientUi, SchemaJson.Options),
                ["common"] = clientCommon is null ? null : JsonSerializer.SerializeToNode(clientCommon, SchemaJson.Options),
            }.ToJsonString();

            return UiLayout(title, body.ToString(), ScriptSafe(initData), ScriptSafe(initUi));
        }
        catch (CodeRuntimeException ex)
        {
            return Layout("Error", $"<main><h1>Error</h1><p>{Escape(ex.Message)}</p></main>");
        }
    }

    // Neutralise "</script>" (and any "<") so an embedded JSON literal can't break out
    // of the inline <script> element.
    private static string ScriptSafe(string json) => json.Replace("<", "\\u003c");

    private (IExecTagChild Result, string Title, ExecScope Scope) ExecuteRender(string urlPath, ExecContext context)
    {
        var ui = _desc.Ui!;
        var exec = new CodeExecutor(_store);
        var scope = new ExecScope();

        // db root (the object graph), read-only at the top.
        var db = DbBridge.LoadRoot(_store, _desc, context);
        scope.Items["db"] = new ExecScopeItem { Value = db, IsReadOnly = true };

        // Functions first (close over the same top scope → mutual recursion) so var
        // initializers may call them — including server-only ones.
        foreach (var f in _desc.Common?.Functions ?? []) DefineFunction(f, scope);
        foreach (var f in ui.Functions ?? []) DefineFunction(f, scope);

        // UI/session state. Initializers run server-side with access recording
        // suppressed (their inputs stay on the server; only the resulting value is
        // shipped). `path` is the requested URL (routing).
        context.Suppress++;
        foreach (var v in ui.Vars ?? [])
        {
            var value = v.Value == null ? new ExecNull() : exec.ExecuteValue(v.Value, scope, context);
            scope.Items[v.Name] = new ExecScopeItem { Value = value, IsReadOnly = false };
        }
        context.Suppress--;
        if (scope.Items.ContainsKey("path"))
            scope.Items["path"] = new ExecScopeItem { Value = new ExecText { Value = urlPath }, IsReadOnly = false };

        var renderFn = ui.Render ?? throw new CodeRuntimeException("The 'ui' section has no render function.");
        var result = exec.InvokeFunction(renderFn, [], scope, context);

        if (result is not IExecTagChild child)
            throw new CodeRuntimeException("The render function did not return a renderable value.");

        var title = scope.Items.TryGetValue("title", out var t) && t.Value is ExecText titleText
            ? titleText.Value
            : "DeEnv";
        return (child, title, scope);
    }

    // Page shell for a code-owned UI: the SSR body is the first paint; the deferred
    // bundle hydrates and takes over rendering from window.initUi / window.initData.
    private static string UiLayout(string title, string body, string initData, string initUi) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>{{Escape(title)}}</title>
          <script>window.initData={{initData}};window.initUi={{initUi}};</script>
          <script defer src="/ui-js"></script>
        </head>
        <body>{{body}}</body>
        </html>
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
            case ExecArray arr:
                // foreach / where / orderBy flatten into the child stream.
                foreach (var item in arr.Items) SerializeChild(item.Value, sb);
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

    // ── page layout ───────────────────────────────────────────────────────────

    private string Page(string urlPath, NodePath path, ResolvedTypeInfo typeInfo, NodeValue node)
    {
        var body = new StringBuilder();
        body.AppendLine(Breadcrumbs(path));
        body.AppendLine("<main>");
        AppendNodeContent(body, path, typeInfo, node);
        body.AppendLine("</main>");
        return Layout(PageTitle(path), body.ToString());
    }

    // id-route page: render the object form for whatever object carries this id.
    private string RenderIdRoute(string urlPath, NodePath path)
    {
        if (path.Segments.Count < 2 || !int.TryParse(path.Segments[1], out var id))
            return NotFoundPage(urlPath, path);

        var hit = _store.ReadById(id);
        if (hit == null) return NotFoundPage(urlPath, path);

        var type = _desc.FindType(hit.Value.TypeName);
        if (type == null) return NotFoundPage(urlPath, path);

        var body = new StringBuilder();
        body.AppendLine(Breadcrumbs(path));
        body.AppendLine("<main>");
        AppendObjectForm(body, path, type, hit.Value.Fields);
        body.AppendLine("</main>");
        return Layout(PageTitle(path), body.ToString());
    }

    private void AppendNodeContent(StringBuilder sb, NodePath path, ResolvedTypeInfo typeInfo, NodeValue node)
    {
        if (typeInfo.Cardinality == Cardinality.Dictionary && node is DictionaryValue dictVal)
        {
            AppendDictionary(sb, path, typeInfo, dictVal);
            return;
        }

        if (node is SetValue setVal)
        {
            var title = path.IsRoot ? "Db" : Humanize(path.Segments[^1]);
            AppendSetTable(sb, path, title, typeInfo.Type.Name, setVal);
            return;
        }

        if (node is ReferenceValue refVal)
        {
            AppendReferenceEditor(sb, path, refVal);
            return;
        }

        switch (node)
        {
            case ObjectValue obj:
                AppendObjectForm(sb, path, typeInfo.Type, obj);
                break;
            default:
                // Base-typed leaf node (the root, or a `dictionary of <base>` entry).
                AppendLeafForm(sb, path, node);
                break;
        }
    }

    // ── leaf form (one input + Save → `write`) ─────────────────────────────────

    private static void AppendLeafForm(StringBuilder sb, NodePath path, NodeValue value)
    {
        var pathAttr = Escape(path.ToString());
        var label = path.IsRoot ? "Db" : Escape(Humanize(path.Segments[^1]));
        sb.AppendLine($"""<form id="node-form" data-kind="leaf" data-path="{pathAttr}">""");
        sb.AppendLine($"  <div class=\"field\"><label>{label}</label>");
        AppendInput(sb, pathAttr, value);
        sb.AppendLine("  </div>");
        sb.AppendLine("""  <div class="actions"><button type="submit">Save</button></div>""");
        sb.AppendLine("</form>");
    }

    // ── object form (Save → `writeObject`) ─────────────────────────────────────

    private void AppendObjectForm(StringBuilder sb, NodePath path, TypeDefinition type, ObjectValue obj)
    {
        var pathAttr = Escape(path.ToString());
        sb.AppendLine($"""<form id="node-form" data-kind="object" data-path="{pathAttr}">""");
        sb.AppendLine($"  <h2>{Escape(type.Name)}</h2>");

        foreach (var prop in type.Props ?? [])
        {
            if (!obj.Fields.TryGetValue(prop.Name, out var fieldVal))
                continue;

            var fieldPath = path.Field(prop.Name);

            if (prop.Cardinality == Cardinality.Dictionary && fieldVal is DictionaryValue dictVal)
            {
                // The dictionary renders its own navigable list title (see AppendDictionaryTable).
                sb.AppendLine("  <div class=\"field\">");
                AppendDictionaryTable(sb, fieldPath, Humanize(prop.Name), prop.Type, dictVal);
                sb.AppendLine("  </div>");
            }
            else if (fieldVal is SetValue setVal)
            {
                sb.AppendLine("  <div class=\"field\">");
                AppendSetTable(sb, fieldPath, Humanize(prop.Name), prop.Type, setVal);
                sb.AppendLine("  </div>");
            }
            else if (fieldVal is ReferenceValue)
            {
                // A single reference renders as a link to its own editor/target page.
                sb.AppendLine("  <div class=\"field\">");
                sb.AppendLine($"    <label>{Escape(Humanize(prop.Name))}</label>");
                sb.AppendLine($"    <a href=\"{Escape(fieldPath.ToString())}\">{Escape(Humanize(prop.Name))}</a>");
                sb.AppendLine("  </div>");
            }
            else
            {
                sb.AppendLine($"  <div class=\"field\">");
                sb.AppendLine($"    <label>{Escape(Humanize(prop.Name))}</label>");
                AppendInput(sb, Escape(fieldPath.ToString()), fieldVal, prop.Name);
                sb.AppendLine("  </div>");
            }
        }

        sb.AppendLine("""  <div class="actions"><button type="submit">Save</button></div>""");
        sb.AppendLine("</form>");
    }

    // Renders a single value input. `fieldName` (when set) lets the client collect
    // it into a writeObject `fields` map by name.
    private static void AppendInput(StringBuilder sb, string pathAttr, NodeValue value, string? fieldName = null)
    {
        var fieldAttr = fieldName is null ? "" : $" data-field=\"{Escape(fieldName)}\"";
        switch (value)
        {
            case BoolValue b:
                sb.AppendLine($"""    <input type="checkbox" data-path="{pathAttr}"{fieldAttr}{(b.Value ? " checked" : "")}>""");
                break;
            case TextValue t:
                sb.AppendLine($"""    <input type="text" data-path="{pathAttr}"{fieldAttr} value="{Escape(t.Text)}">""");
                break;
            case IntValue i:
                sb.AppendLine($"""    <input type="number" data-path="{pathAttr}"{fieldAttr} value="{i.Value}">""");
                break;
            case DecimalValue d:
                sb.AppendLine($"""    <input type="number" step="any" data-path="{pathAttr}"{fieldAttr} value="{d.Value.ToString(CultureInfo.InvariantCulture)}">""");
                break;
            case DateValue d:
                sb.AppendLine($"""    <input type="date" data-path="{pathAttr}"{fieldAttr} value="{d.Value:yyyy-MM-dd}">""");
                break;
            default:
                sb.AppendLine($"""    <span data-path="{pathAttr}">{Escape(value.ToString() ?? "")}</span>""");
                break;
        }
    }

    // ── dictionary table ──────────────────────────────────────────────────────

    private void AppendDictionary(StringBuilder sb, NodePath path, ResolvedTypeInfo typeInfo, DictionaryValue dictVal)
    {
        var title = path.IsRoot ? "Db" : Humanize(path.Segments[^1]);
        AppendDictionaryTable(sb, path, title, typeInfo.Type.Name, dictVal);
    }

    private void AppendDictionaryTable(
        StringBuilder sb,
        NodePath dictPath,
        string title,
        string elementTypeName,
        DictionaryValue dictVal)
    {
        var entryType = _desc.FindType(elementTypeName);   // null when the element is a base type
        var cols = entryType?.Props?
            .Where(p => p.Cardinality == Cardinality.Single && !_desc.IsObjectType(p.Type))
            .Select(p => p.Name).ToList() ?? [];
        var isObjectEntry = entryType is { BaseType: BaseType.Object };

        var dictPathAttr = Escape(dictPath.ToString());

        // Navigable list title (links to the dictionary's own page).
        sb.AppendLine($"<h3 class=\"list-title\"><a href=\"{dictPathAttr}\">{Escape(title)}</a></h3>");

        sb.AppendLine("<table>");
        sb.AppendLine("  <thead><tr>");
        sb.AppendLine("    <th>Key</th>");
        if (isObjectEntry)
            foreach (var col in cols)
                sb.AppendLine($"    <th>{Escape(Humanize(col))}</th>");
        else
            sb.AppendLine("    <th>Value</th>");
        sb.AppendLine("    <th></th>");
        sb.AppendLine("  </tr></thead>");
        sb.AppendLine("  <tbody>");
        foreach (var (key, entryVal) in dictVal.Entries)
        {
            var keyStr = KeyString(key);
            var entryPath = dictPath.Key(keyStr);
            var entryUrl = Escape(entryPath.ToString());
            sb.AppendLine($"    <tr data-nav=\"{entryUrl}\">");
            sb.AppendLine($"      <td><a href=\"{entryUrl}\">{Escape(keyStr)}</a></td>");
            if (isObjectEntry && entryVal is ObjectValue objVal)
            {
                foreach (var col in cols)
                {
                    var cell = objVal.Fields.TryGetValue(col, out var fv) ? DisplayValue(fv) : "";
                    sb.AppendLine($"      <td>{Escape(cell)}</td>");
                }
            }
            else
            {
                sb.AppendLine($"      <td>{Escape(DisplayValue(entryVal))}</td>");
            }
            sb.AppendLine($"      <td><button type=\"button\" data-delentry=\"{dictPathAttr}\" data-key=\"{Escape(keyStr)}\">Delete</button></td>");
            sb.AppendLine("    </tr>");
        }
        sb.AppendLine("  </tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine($"<button type=\"button\" data-newentry=\"{dictPathAttr}\" data-collection=\"dictionary\">New</button>");
    }

    // ── set table (members keyed by their own identity) ─────────────────────────

    private void AppendSetTable(StringBuilder sb, NodePath setPath, string title, string elementTypeName, SetValue setVal)
    {
        var entryType = _desc.FindType(elementTypeName);
        var cols = entryType?.Props?
            .Where(p => p.Cardinality == Cardinality.Single && !_desc.IsObjectType(p.Type))
            .Select(p => p.Name).ToList() ?? [];

        var setPathAttr = Escape(setPath.ToString());

        sb.AppendLine($"<h3 class=\"list-title\"><a href=\"{setPathAttr}\">{Escape(title)}</a></h3>");
        sb.AppendLine("<table>");
        sb.AppendLine("  <thead><tr>");
        sb.AppendLine("    <th>Id</th>");
        foreach (var col in cols)
            sb.AppendLine($"    <th>{Escape(Humanize(col))}</th>");
        sb.AppendLine("    <th></th>");
        sb.AppendLine("  </tr></thead>");
        sb.AppendLine("  <tbody>");
        foreach (var (id, member) in setVal.Members)
        {
            var entryUrl = Escape(setPath.Key(id.ToString()).ToString());
            sb.AppendLine($"    <tr data-nav=\"{entryUrl}\">");
            sb.AppendLine($"      <td><a href=\"{entryUrl}\">{id}</a></td>");
            if (member is ObjectValue objVal2)
                foreach (var col in cols)
                {
                    var cell = objVal2.Fields.TryGetValue(col, out var fv) ? DisplayValue(fv) : "";
                    sb.AppendLine($"      <td>{Escape(cell)}</td>");
                }
            sb.AppendLine($"      <td><button type=\"button\" data-delentry=\"{setPathAttr}\" data-key=\"{id}\">Delete</button></td>");
            sb.AppendLine("    </tr>");
        }
        sb.AppendLine("  </tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine($"<button type=\"button\" data-newentry=\"{setPathAttr}\" data-collection=\"set\">New</button>");
    }

    // ── reference editor (pick existing or create new) ──────────────────────────

    private void AppendReferenceEditor(StringBuilder sb, NodePath path, ReferenceValue refVal)
    {
        var pathAttr = Escape(path.ToString());
        var typeName = refVal.TypeName;
        var type = _desc.FindType(typeName);

        sb.AppendLine($"""<form id="node-form" data-kind="reference" data-path="{pathAttr}">""");
        sb.AppendLine($"  <h2>{Escape(typeName)}</h2>");
        sb.AppendLine($"""  <div data-ref data-type="{Escape(typeName)}" data-current="existing">""");

        // Mode toggle stays outside the toggled sections so both buttons are always clickable.
        sb.AppendLine("    <div class=\"ref-toggle\">");
        sb.AppendLine("""      <button type="button" data-mode="existing">Use existing</button>""");
        sb.AppendLine("""      <button type="button" data-mode="new">Create new</button>""");
        sb.AppendLine("    </div>");

        sb.AppendLine("    <div class=\"ref-existing\">");
        sb.AppendLine("      <select data-pick>");
        foreach (var (id, obj) in _store.ReadExtent(typeName))
            sb.AppendLine($"        <option value=\"{id}\">{Escape(LabelOf(obj))}</option>");
        sb.AppendLine("      </select>");
        sb.AppendLine("    </div>");

        sb.AppendLine("    <div class=\"ref-new\">");
        foreach (var prop in type?.Props ?? [])
            if (prop.Cardinality == Cardinality.Single && !_desc.IsObjectType(prop.Type))
            {
                sb.AppendLine($"      <label>{Escape(Humanize(prop.Name))}</label>");
                AppendNamedInput(sb, prop.Name, ResolveBaseType(prop.Type));
            }
        sb.AppendLine("    </div>");

        sb.AppendLine("  </div>");
        sb.AppendLine("""  <div class="actions"><button type="submit">Save</button></div>""");
        sb.AppendLine("</form>");
    }

    private static void AppendNamedInput(StringBuilder sb, string name, BaseType bt)
    {
        var n = Escape(name);
        var html = bt switch
        {
            BaseType.Bool     => $"""<input type="checkbox" name="{n}">""",
            BaseType.Int      => $"""<input type="number" name="{n}" value="0">""",
            BaseType.Decimal  => $"""<input type="number" step="any" name="{n}" value="0">""",
            BaseType.Date     => $"""<input type="date" name="{n}">""",
            _                 => $"""<input type="text" name="{n}" value="">"""
        };
        sb.AppendLine($"      {html}");
    }

    private static BaseType ResolveBaseType(string name) => name switch
    {
        "bool"     => BaseType.Bool,
        "int"      => BaseType.Int,
        "decimal"  => BaseType.Decimal,
        "date"     => BaseType.Date,
        "datetime" => BaseType.DateTime,
        _          => BaseType.Text
    };

    private static string LabelOf(ObjectValue obj)
    {
        foreach (var (_, v) in obj.Fields)
            if (v is TextValue t) return t.Text;
        return "";
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

    private static string Layout(string title, string body) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>{Escape(title)}</title>
          <style>{Css}</style>
          <script type="module" src="/js"></script>
        </head>
        <body>
        {body}
        </body>
        </html>
        """;

    private const string Css = """
        body { font-family: system-ui, Arial, sans-serif; margin: 2rem; color: #222; }
        h2 { font-size: 1.6rem; margin: 0 0 1rem; }
        nav.breadcrumbs { margin-bottom: 1rem; color: #666; }
        .field { margin: 0.5rem 0; }
        .field > label { display: inline-block; min-width: 10rem; font-weight: 600; }
        table { border-collapse: collapse; margin: 0.25rem 0 0.75rem; }
        th, td { border: 1px solid #bbb; padding: 0.4rem 0.8rem; text-align: left; }
        th { background: #f3f3f3; }
        .list-title { font-size: 1.3rem; margin: 1.25rem 0 0.5rem; }
        .list-title a { text-decoration: none; color: #1a56b8; }
        .list-title a:hover { text-decoration: underline; }
        .actions { margin-top: 1rem; }
        button { margin-right: 0.4rem; }
        .create-form { border: 1px solid #bbb; padding: 0.75rem 1rem; margin: 0.5rem 0; background: #fafafa; }
        .create-form .error { color: #b00; }
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

    private static string DisplayValue(NodeValue v) => v switch
    {
        BoolValue b  => b.Value ? "✓" : "",
        TextValue t  => t.Text,
        IntValue i   => i.Value.ToString(CultureInfo.InvariantCulture),
        DecimalValue d => d.Value.ToString(CultureInfo.InvariantCulture),
        DateValue d  => d.Value.ToString("yyyy-MM-dd"),
        _ => v.ToString() ?? ""
    };

    private static string KeyString(NodeValue key) => key switch
    {
        IntValue i   => i.Value.ToString(),
        TextValue t  => t.Text,
        BoolValue b  => b.Value.ToString().ToLowerInvariant(),
        _ => key.ToString() ?? ""
    };

    private static NodePath ParsePath(string urlPath)
    {
        var segs = urlPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return NodePath.FromSegments(segs);
    }

    private static string Escape(string s) =>
        System.Net.WebUtility.HtmlEncode(s);

    // "companyName" -> "Company name", "shipped" -> "Shipped", "key_type" -> "Key type".
    internal static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '_' or '-')
            {
                sb.Append(' ');
                continue;
            }
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
