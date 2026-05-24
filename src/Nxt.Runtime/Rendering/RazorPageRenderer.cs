using System.IO;
using Nxt.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Nxt.Rendering;

/// <summary>
/// Renders Razor (.cshtml) views to HTML. Resolves the view by absolute path
/// (the source generator emits paths like <c>/Pages/about.cshtml</c>).
/// </summary>
public sealed class RazorPageRenderer : IPageRenderer
{
    public bool CanRender(PageKind kind) => kind == PageKind.Razor;

    public async Task<string> RenderAsync(
        HttpContext httpContext,
        RouteDescriptor route,
        IReadOnlyDictionary<string, string?> routeValues,
        CancellationToken cancellationToken = default)
    {
        if (route.ViewPath is null)
            throw new InvalidOperationException($"Razor route '{route.UrlPattern}' has no ViewPath.");

        var services = httpContext.RequestServices;
        var viewEngine = services.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = services.GetRequiredService<ITempDataProvider>();

        var actionContext = new ActionContext(
            httpContext,
            httpContext.GetRouteData() ?? new RouteData(),
            new ActionDescriptor());

        var result = viewEngine.GetView(executingFilePath: null, viewPath: route.ViewPath, isMainPage: true);
        if (!result.Success)
        {
            // Try FindView fallback
            result = viewEngine.FindView(actionContext, route.ViewPath, isMainPage: true);
            if (!result.Success)
                throw new FileNotFoundException($"Could not locate Razor view at '{route.ViewPath}'. Searched: {string.Join(", ", result.SearchedLocations ?? Array.Empty<string>())}");
        }

        var view = result.View;
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = new RouteValueDictionary(routeValues),
        };
        foreach (var kv in routeValues)
            viewData[kv.Key] = kv.Value;

        await using var sw = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            view,
            viewData,
            new TempDataDictionary(httpContext, tempDataProvider),
            sw,
            new HtmlHelperOptions());

        await view.RenderAsync(viewContext);
        return sw.ToString();
    }
}
