using Nxt.Routing;
using Microsoft.AspNetCore.Http;

namespace Nxt.Rendering;

/// <summary>
/// Renders a matched route into an HTML string. Implementations exist for both
/// Blazor (.razor via HtmlRenderer) and Razor (.cshtml via the view engine).
/// </summary>
public interface IPageRenderer
{
    bool CanRender(PageKind kind);

    Task<string> RenderAsync(
        HttpContext httpContext,
        RouteDescriptor route,
        IReadOnlyDictionary<string, string?> routeValues,
        CancellationToken cancellationToken = default);
}
