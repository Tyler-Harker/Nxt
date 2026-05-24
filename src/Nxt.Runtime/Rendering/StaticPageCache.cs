using System.Collections.Concurrent;
using Nxt.Routing;

namespace Nxt.Rendering;

/// <summary>
/// In-memory cache for SSG output and ISR revalidation tracking.
/// Backs onto disk when an <see cref="OutputDirectory"/> is set so prerendered
/// pages survive process restarts.
/// </summary>
public sealed class StaticPageCache
{
    private readonly ConcurrentDictionary<string, CachedPage> _cache = new();

    /// <summary>Directory used to persist prerendered HTML to disk. Set by SSG build.</summary>
    public string? OutputDirectory { get; set; }

    public bool TryGet(string url, out CachedPage page) => _cache.TryGetValue(url, out page!);

    public void Set(string url, string html, RouteDescriptor route)
    {
        var cached = new CachedPage(html, DateTimeOffset.UtcNow, route.RevalidateSeconds);
        _cache[url] = cached;
        TryPersist(url, html);
    }

    public IEnumerable<KeyValuePair<string, CachedPage>> All() => _cache;

    private void TryPersist(string url, string html)
    {
        if (string.IsNullOrEmpty(OutputDirectory)) return;
        var path = Path.Combine(OutputDirectory, ToFileName(url));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, html);
    }

    private static string ToFileName(string url)
    {
        var trimmed = url.Trim('/');
        if (string.IsNullOrEmpty(trimmed)) return "index.html";
        return trimmed.Replace('/', Path.DirectorySeparatorChar) + ".html";
    }
}

public sealed record CachedPage(string Html, DateTimeOffset GeneratedAt, int RevalidateSeconds)
{
    public bool IsStale => RevalidateSeconds > 0 &&
        DateTimeOffset.UtcNow - GeneratedAt > TimeSpan.FromSeconds(RevalidateSeconds);
}
