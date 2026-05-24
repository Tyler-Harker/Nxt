using System.Text.RegularExpressions;

namespace Nxt.Routing;

/// <summary>
/// Matches a request path against an Nxt route pattern.
/// Patterns use ASP.NET-style braces: <c>/blog/{slug}</c>, <c>/files/{*all}</c>.
/// </summary>
internal static class NxtRouteMatcher
{
    public static bool TryMatch(string pattern, string path, out Dictionary<string, string?> values)
    {
        values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var regex = ToRegex(pattern);
        var m = regex.Match(path);
        if (!m.Success) return false;
        foreach (Group group in m.Groups)
        {
            if (group.Name is null or "0") continue;
            if (int.TryParse(group.Name, out _)) continue;
            values[group.Name] = Uri.UnescapeDataString(group.Value);
        }
        return true;
    }

    private static Regex ToRegex(string pattern)
    {
        // Convert /blog/{slug} → ^/blog/(?<slug>[^/]+)$
        // Convert /files/{*all} → ^/files/(?<all>.*)$
        var sb = new System.Text.StringBuilder("^");
        var parts = pattern.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            sb.Append('/');
            if (part.StartsWith("{*") && part.EndsWith("}"))
            {
                var name = part[2..^1];
                sb.Append("(?<").Append(name).Append(">.*)");
            }
            else if (part.StartsWith('{') && part.EndsWith("}"))
            {
                var name = part[1..^1].TrimEnd('?');
                sb.Append("(?<").Append(name).Append(">[^/]+)");
            }
            else
            {
                sb.Append(Regex.Escape(part));
            }
        }
        if (parts.Length == 0) sb.Append('/');
        sb.Append("/?$");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
