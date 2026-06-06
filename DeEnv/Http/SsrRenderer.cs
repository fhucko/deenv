using System.Globalization;
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
                AppendDictionaryTable(sb, fieldPath, Humanize(prop.Name), prop.TypeName, prop.KeyGeneration, dictVal);
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
        AppendDictionaryTable(sb, path, title, typeInfo.Type.Name,
            typeInfo.KeyGeneration ?? KeyGeneration.Manual, dictVal);
    }

    private void AppendDictionaryTable(
        StringBuilder sb,
        NodePath dictPath,
        string title,
        string elementTypeName,
        KeyGeneration keyGen,
        DictionaryValue dictVal)
    {
        var entryType = _desc.FindType(elementTypeName);   // null when the element is a base type
        var cols = entryType?.Props?.Where(p => p.Cardinality != Cardinality.Dictionary)
                                    .Select(p => p.Name).ToList() ?? [];
        var isObjectEntry = entryType is { BaseType: BaseType.Object };

        var dictPathAttr = Escape(dictPath.ToString());
        var keyGenAttr = keyGen == KeyGeneration.Auto ? "auto" : "manual";

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
        sb.AppendLine($"<button type=\"button\" data-newentry=\"{dictPathAttr}\" data-keygen=\"{keyGenAttr}\">New</button>");
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
