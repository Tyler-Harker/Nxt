using Nxt.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nxt.Rendering;

/// <summary>
/// Walks the route table and prerenders every <see cref="RenderMode.Static"/> and
/// <see cref="RenderMode.IncrementalStatic"/> page into the cache (and to disk via
/// <see cref="StaticPageCache.OutputDirectory"/>). Invoked by the CLI at build time
/// (<c>nxt build</c>) and re-usable from app code if needed.
/// </summary>
public sealed class StaticSiteGenerator(
    IServiceScopeFactory scopeFactory,
    StaticPageCache cache,
    ILogger<StaticSiteGenerator> logger)
{
    public async Task PrerenderAllAsync(CancellationToken ct = default)
    {
        var candidates = RouteTable.Routes
            .Where(r => r.RenderMode is RenderMode.Static or RenderMode.IncrementalStatic)
            .Where(r => !HasRouteParameters(r.UrlPattern))
            .ToList();

        logger.LogInformation("Prerendering {Count} static pages", candidates.Count);

        foreach (var route in candidates)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<PageRendererDispatcher>();
            var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            ctx.Request.Path = route.UrlPattern;

            var html = await dispatcher.RenderAsync(ctx, route, new Dictionary<string, string?>(), ct);
            cache.Set(route.UrlPattern, html, route);
            logger.LogInformation("  ✓ {Url}", route.UrlPattern);
        }
    }

    private static bool HasRouteParameters(string pattern) =>
        pattern.Contains('{');
}
