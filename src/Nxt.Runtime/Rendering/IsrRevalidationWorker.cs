using Nxt.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nxt.Rendering;

/// <summary>
/// Background worker that periodically checks the static cache for stale entries and
/// regenerates them off the request path. This is the ISR ("Incremental Static Regeneration")
/// equivalent of Next.js.
/// </summary>
public sealed class IsrRevalidationWorker(
    IServiceScopeFactory scopeFactory,
    StaticPageCache cache,
    ILogger<IsrRevalidationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var (url, page) in cache.All().ToArray())
                {
                    if (!page.IsStale) continue;
                    await RegenerateAsync(url, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ISR revalidation tick failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task RegenerateAsync(string url, CancellationToken ct)
    {
        var route = RouteTable.Routes.FirstOrDefault(r => MatchesExact(r.UrlPattern, url));
        if (route is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<PageRendererDispatcher>();

        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.Request.Path = url;

        var html = await dispatcher.RenderAsync(httpContext, route, new Dictionary<string, string?>(), ct);
        cache.Set(url, html, route);
        logger.LogInformation("ISR regenerated {Url}", url);
    }

    // Simple equality match — parametric routes are typically not ISR candidates
    // unless a GetStaticPaths-equivalent enumerates them at build time.
    private static bool MatchesExact(string pattern, string url) =>
        string.Equals(pattern.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
