using Nxt.Rendering;
using Nxt.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nxt;

public static class NxtServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything Nxt needs: page renderers, route table, ISR cache, MVC/Razor view engine,
    /// Blazor HTML rendering, and any middleware classes the source generator discovered.
    /// Call this from <c>Program.cs</c>:
    /// <code>builder.Services.AddNxt();</code>
    /// </summary>
    public static IServiceCollection AddNxt(this IServiceCollection services, Action<NxtOptions>? configure = null)
    {
        var options = new NxtOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Resolve generator-emitted pending routes to RouteDescriptors now that the user's
        // assembly (and any referenced ones) are loaded.
        NxtStartupBridge.ResolveAll();

        // Pull in MVC's Razor view engine so .cshtml pages can render.
        services.AddControllersWithViews();

        // Blazor static rendering — required for .razor pages. Interactive Server is built-in;
        // interactive WebAssembly requires the consumer to add Microsoft.AspNetCore.Components.WebAssembly.Server
        // themselves and call AddInteractiveWebAssemblyComponents() (v2 feature for us).
        var razor = services.AddRazorComponents();
        razor.AddInteractiveServerComponents();

        services.AddScoped<HtmlRenderer>(sp => new HtmlRenderer(sp, sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));

        // Renderers
        services.AddScoped<IPageRenderer, BlazorPageRenderer>();
        services.AddScoped<IPageRenderer, RazorPageRenderer>();
        services.AddScoped<PageRendererDispatcher>();

        // SSG / ISR
        services.AddSingleton<StaticPageCache>(sp =>
        {
            var cache = new StaticPageCache { OutputDirectory = options.StaticOutputDirectory };
            return cache;
        });

        services.AddSingleton<StaticSiteGenerator>();

        if (options.EnableIsr)
            services.AddHostedService<IsrRevalidationWorker>();

        // Generated registration of middleware as services
        foreach (var m in MiddlewareTable.Middleware)
            services.TryAddScoped(m.MiddlewareType);

        // Generated registration of API handler classes as services
        foreach (var ep in ApiEndpointTable.Endpoints)
            services.TryAddScoped(ep.HandlerType);

        return services;
    }
}

public sealed class NxtOptions
{
    /// <summary>Where SSG/ISR output is persisted to disk. Defaults to <c>wwwroot/_nxt_static</c>.</summary>
    public string? StaticOutputDirectory { get; set; } = "wwwroot/_nxt_static";

    /// <summary>Whether the ISR background revalidation worker is enabled.</summary>
    public bool EnableIsr { get; set; } = true;

    /// <summary>Pages folder relative to the app root.</summary>
    public string PagesDirectory { get; set; } = "Pages";
}
