namespace DeEnv.Code;

// Small text helpers shared by the C# renderer and the Code `humanize` builtin (and
// mirrored by the TS interpreter, pinned by the conformance suite).
public static class TextUtil
{
    // "companyName" -> "Company name", "shipped" -> "Shipped", "key_type" -> "Key type".
    // The canonical implementation; SsrRenderer and the `humanize` builtin both call it.
    public static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder(name.Length + 4);
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
