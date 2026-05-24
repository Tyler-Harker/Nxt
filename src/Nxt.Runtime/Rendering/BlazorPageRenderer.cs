using Nxt.Routing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Nxt.Rendering;

/// <summary>
/// Renders Blazor (.razor) components to HTML using the framework's <see cref="HtmlRenderer"/>.
/// This produces static markup; interactive routes layer Blazor's Server/WASM runtimes on top.
/// </summary>
public sealed class BlazorPageRenderer : IPageRenderer
{
    public bool CanRender(PageKind kind) => kind == PageKind.Blazor;

    public async Task<string> RenderAsync(
        HttpContext httpContext,
        RouteDescriptor route,
        IReadOnlyDictionary<string, string?> routeValues,
        CancellationToken cancellationToken = default)
    {
        if (route.ComponentType is null)
            throw new InvalidOperationException($"Blazor route '{route.UrlPattern}' has no ComponentType.");

        var renderer = httpContext.RequestServices.GetRequiredService<HtmlRenderer>();

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = BuildParameters(route.ComponentType, routeValues);

            // If a layout is declared, render the layout with the page as Body.
            if (route.LayoutType is not null)
            {
                var wrapperParams = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Layout"] = route.LayoutType,
                    ["Page"] = route.ComponentType,
                    ["PageParameters"] = parameters,
                });
                var output = await renderer.RenderComponentAsync<LayoutHost>(wrapperParams);
                return output.ToHtmlString();
            }
            else
            {
                var output = await renderer.RenderComponentAsync(route.ComponentType, parameters);
                return output.ToHtmlString();
            }
        });
    }

    private static ParameterView BuildParameters(Type componentType, IReadOnlyDictionary<string, string?> routeValues)
    {
        if (routeValues.Count == 0) return ParameterView.Empty;

        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in componentType.GetProperties())
        {
            if (routeValues.TryGetValue(prop.Name, out var raw) && raw is not null)
            {
                dict[prop.Name] = ConvertParam(raw, prop.PropertyType);
            }
        }
        return ParameterView.FromDictionary(dict);
    }

    private static object? ConvertParam(string raw, Type target)
    {
        if (target == typeof(string)) return raw;
        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        try { return Convert.ChangeType(raw, underlying); }
        catch { return raw; }
    }
}

/// <summary>
/// Hosts a page inside a layout component. Used internally by <see cref="BlazorPageRenderer"/>.
/// </summary>
internal sealed class LayoutHost : ComponentBase
{
    [Parameter] public Type Layout { get; set; } = default!;
    [Parameter] public Type Page { get; set; } = default!;
    [Parameter] public ParameterView PageParameters { get; set; }

    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenComponent(0, Layout);
        builder.AddAttribute(1, "Body", (RenderFragment)(b =>
        {
            b.OpenComponent(0, Page);
            foreach (var kv in PageParameters.ToDictionary())
            {
                b.AddAttribute(1, kv.Key, kv.Value);
            }
            b.CloseComponent();
        }));
        builder.CloseComponent();
    }
}
