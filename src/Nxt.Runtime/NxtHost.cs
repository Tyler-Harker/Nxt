using Nxt.Rendering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Nxt;

/// <summary>
/// One-call bootstrapper that mirrors Next.js's zero-config feel. A typical <c>Program.cs</c>
/// looks like:
/// <code>
/// await NxtHost.RunAsync(args);
/// </code>
/// Apps that need to wire services or middleware pass a configuration callback:
/// <code>
/// await NxtHost.RunAsync(args, opts =>
/// {
///     opts.ConfigureServices = builder => builder.Services.AddDbContext&lt;AppDbContext&gt;(...);
///     opts.ConfigureMiddleware = app => { app.UseAuthentication(); app.UseAuthorization(); };
/// });
/// </code>
/// See <see cref="NxtHostOptions"/> for every hook + the position it slots into the pipeline.
/// </summary>
public static class NxtHost
{
    /// <summary>Builds, configures, and runs the Nxt app. Returns when the host shuts down.</summary>
    public static async Task RunAsync(string[] args, Action<NxtHostOptions>? configure = null)
    {
        var opts = new NxtHostOptions();
        configure?.Invoke(opts);

        var builder = WebApplication.CreateBuilder(args);

        // Enable static-web-assets in every environment (not just Development) so
        // MapStaticAssets can serve files from referenced projects' manifests — required
        // for Blazor WASM bundles, Razor Class Library assets, etc.
        builder.WebHost.UseStaticWebAssets();

        builder.Services.AddNxt();
        opts.ConfigureServices?.Invoke(builder);

        var app = builder.Build();
        ConfigurePipeline(app, opts);

        // SSG prerender mode — invoked by `nxt build`. Render static pages then exit.
        if (Environment.GetEnvironmentVariable("NXT_PRERENDER") is "1")
        {
            using var scope = app.Services.CreateScope();
            var ssg = scope.ServiceProvider.GetRequiredService<StaticSiteGenerator>();
            await ssg.PrerenderAllAsync();
            return;
        }

        await app.RunAsync();
    }

    /// <summary>Backward-compatible overload — services + post-endpoint hook only.
    /// Prefer the <see cref="NxtHostOptions"/> overload for new code.</summary>
    public static Task RunAsync(string[] args,
        Action<WebApplicationBuilder>? configureServices,
        Action<WebApplication>? configureApp = null)
        => RunAsync(args, opts =>
        {
            opts.ConfigureServices = configureServices;
            opts.ConfigureAfterEndpoints = configureApp;
        });

    private static void ConfigurePipeline(WebApplication app, NxtHostOptions opts)
    {
        if (app.Environment.IsDevelopment())
            app.UseDeveloperExceptionPage();

        opts.ConfigureBeforeRouting?.Invoke(app);
        opts.ConfigureMiddleware?.Invoke(app);
        app.UseAntiforgery();
        app.UseNxtMiddleware();
        // .NET 9's static-web-assets endpoint — required for Blazor framework files served
        // by referenced WASM-client projects (_framework/blazor.web.js, dotnet.wasm, etc.).
        // Safe to call even when no manifest is present — it's a no-op.
        app.MapStaticAssets();
        app.MapNxt();

        // Blazor pipeline — handles every .razor page discovered by the generator. The
        // generator emits [Route] + (optional) [RenderModeInteractiveServer] attributes on
        // each page partial; MapRazorComponents<Root> finds them via reflection.
        var blazor = app.MapRazorComponents<Components.Root>()
            .AddInteractiveServerRenderMode();
        foreach (var asm in NxtStartupBridge.UserAssemblies)
            blazor.AddAdditionalAssemblies(asm);
        opts.ConfigureRazorEndpoints?.Invoke(blazor);

        opts.ConfigureAfterEndpoints?.Invoke(app);
    }
}
