using Nxt.Routing;
using Microsoft.AspNetCore.Http;

namespace Nxt.Rendering;

/// <summary>
/// Resolves the correct <see cref="IPageRenderer"/> for a route's <see cref="PageKind"/>.
/// </summary>
public sealed class PageRendererDispatcher(IEnumerable<IPageRenderer> renderers)
{
    private readonly IPageRenderer[] _renderers = renderers.ToArray();

    public Task<string> RenderAsync(
        HttpContext httpContext,
        RouteDescriptor route,
        IReadOnlyDictionary<string, string?> routeValues,
        CancellationToken cancellationToken = default)
    {
        var renderer = _renderers.FirstOrDefault(r => r.CanRender(route.Kind))
            ?? throw new InvalidOperationException($"No IPageRenderer registered for PageKind '{route.Kind}'.");
        return renderer.RenderAsync(httpContext, route, routeValues, cancellationToken);
    }
}
