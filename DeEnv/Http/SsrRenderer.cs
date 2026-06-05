using System.Text;
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
        var nodePath = ParsePath(urlPath);
        var typeInfo = _resolver.ResolveType(nodePath);

        if (typeInfo == null)
            return NotFoundPage(urlPath, nodePath);

        var node = _store.ReadNode(nodePath);
        if (node == null)
            return NotFoundPage(urlPath, nodePath);

        return Page(urlPath, nodePath, typeInfo, node);
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

    private void AppendNodeContent(StringBuilder sb, NodePath path, ResolvedTypeInfo typeInfo, NodeValue node)
    {
        if (typeInfo.Cardinality == Cardinality.Dictionary && node is DictionaryValue dictVal)
        {
            AppendDictionary(sb, path, typeInfo, dictVal);
            return;
        }

        switch (node)
        {
            case BoolValue b:
                AppendBoolRoot(sb, path, b);
                break;
            case ObjectValue obj:
                AppendObjectForm(sb, path, typeInfo.Type, obj);
                break;
            default:
                sb.AppendLine($"<p>{Escape(node.ToString() ?? "")}</p>");
                break;
        }
    }

    // ── bool root (checkbox) ──────────────────────────────────────────────────

    private static void AppendBoolRoot(StringBuilder sb, NodePath path, BoolValue b)
    {
        var pathAttr = Escape(path.ToString());
        var checkedAttr = b.Value ? " checked" : "";
        sb.AppendLine($"""
            <form id="node-form" data-path="{pathAttr}">
              <label>
                <input type="checkbox" id="node-bool"{checkedAttr} data-path="{pathAttr}">
                Db
              </label>
            </form>
        """);
    }

    // ── object form ───────────────────────────────────────────────────────────

    private void AppendObjectForm(StringBuilder sb, NodePath path, TypeDefinition type, ObjectValue obj)
    {
        var pathAttr = Escape(path.ToString());
        sb.AppendLine($"""<form id="node-form" data-path="{pathAttr}">""");
        sb.AppendLine($"  <h2>{Escape(type.Name)}</h2>");

        foreach (var prop in type.Props ?? [])
        {
            if (!obj.Fields.TryGetValue(prop.Name, out var fieldVal))
                continue;

            var fieldPath = path.Field(prop.Name);
            sb.AppendLine($"  <div class=\"field\">");
            sb.AppendLine($"    <label>{Escape(prop.Name)}</label>");

            if (prop.Cardinality == Cardinality.Dictionary && fieldVal is DictionaryValue dictVal)
            {
                var entryType = _desc.FindType(prop.TypeName);
                AppendDictionaryTable(sb, fieldPath, prop, entryType, dictVal);
            }
            else
            {
                AppendFieldInput(sb, fieldPath, prop, fieldVal);
            }

            sb.AppendLine("  </div>");
        }

        sb.AppendLine("""
              <div class="actions">
                <button type="submit">Save</button>
              </div>
            </form>
        """);
    }

    private static void AppendFieldInput(StringBuilder sb, NodePath fieldPath, PropDefinition prop, NodeValue value)
    {
        var pathAttr = Escape(fieldPath.ToString());
        switch (value)
        {
            case BoolValue b:
                var checkedAttr = b.Value ? " checked" : "";
                sb.AppendLine($"""    <input type="checkbox" data-path="{pathAttr}"{checkedAttr}>""");
                break;
            case TextValue t:
                sb.AppendLine($"""    <input type="text" data-path="{pathAttr}" value="{Escape(t.Text)}">""");
                break;
            case IntValue i:
                sb.AppendLine($"""    <input type="number" data-path="{pathAttr}" value="{i.Value}">""");
                break;
            case DecimalValue d:
                sb.AppendLine($"""    <input type="number" step="any" data-path="{pathAttr}" value="{d.Value}">""");
                break;
            case DateValue d:
                sb.AppendLine($"""    <input type="date" data-path="{pathAttr}" value="{d.Value:yyyy-MM-dd}">""");
                break;
            default:
                sb.AppendLine($"""    <span data-path="{pathAttr}">{Escape(value.ToString() ?? "")}</span>""");
                break;
        }
    }

    // ── dictionary table ──────────────────────────────────────────────────────

    private void AppendDictionary(StringBuilder sb, NodePath path, ResolvedTypeInfo typeInfo, DictionaryValue dictVal)
    {
        var entryType = _desc.FindType(typeInfo.Type.Name);
        AppendDictionaryTable(sb, path, null, entryType, dictVal);
    }

    private static void AppendDictionaryTable(
        StringBuilder sb,
        NodePath dictPath,
        PropDefinition? prop,
        TypeDefinition? entryType,
        DictionaryValue dictVal)
    {
        var cols = entryType?.Props?.Select(p => p.Name).ToList() ?? [];
        sb.AppendLine("<table>");
        sb.AppendLine("  <thead><tr>");
        sb.AppendLine("    <th>Key</th>");
        foreach (var col in cols)
            sb.AppendLine($"    <th>{Escape(col)}</th>");
        sb.AppendLine("  </tr></thead>");
        sb.AppendLine("  <tbody>");
        foreach (var (key, entryVal) in dictVal.Entries)
        {
            var keyStr = KeyString(key);
            var entryPath = dictPath.Key(keyStr);
            var entryUrl = Escape(entryPath.ToString());
            sb.AppendLine($"    <tr data-nav=\"{entryUrl}\">");
            sb.AppendLine($"      <td><a href=\"{entryUrl}\">{Escape(keyStr)}</a></td>");
            if (entryVal is ObjectValue objVal)
            {
                foreach (var col in cols)
                {
                    var cell = objVal.Fields.TryGetValue(col, out var fv) ? DisplayValue(fv) : "";
                    sb.AppendLine($"      <td>{Escape(cell)}</td>");
                }
            }
            sb.AppendLine("    </tr>");
        }
        sb.AppendLine("  </tbody>");
        sb.AppendLine("</table>");
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
          <script type="module" src="/js"></script>
        </head>
        <body>
        {body}
        </body>
        </html>
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
        IntValue i   => i.Value.ToString(),
        DecimalValue d => d.Value.ToString(),
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
}
