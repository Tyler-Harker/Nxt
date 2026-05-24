using Nxt.Rendering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nxt;

/// <summary>
/// One-call bootstrapper that mirrors Next.js's zero-config feel. A typical <c>Program.cs</c>
/// looks like:
/// <code>
/// await NxtHost.RunAsync(args);
/// </code>
/// Apps that need to register their own services can use <see cref="CreateBuilder"/> and
/// <see cref="ConfigureApp"/> separately for full control.
/// </summary>
public static class NxtHost
{
    /// <summary>Builds, configures, and runs the Nxt app. Returns when the host shuts down.</summary>
    public static async Task RunAsync(string[] args, Action<WebApplicationBuilder>? configureServices = null, Action<WebApplication>? configureApp = null)
    {
        var builder = CreateBuilder(args);
        configureServices?.Invoke(builder);
        var app = builder.Build();
        ConfigureApp(app);
        configureApp?.Invoke(app);

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

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddNxt();
        return builder;
    }

    public static WebApplication ConfigureApp(WebApplication app)
    {
        if (app.Environment.IsDevelopment())
            app.UseDeveloperExceptionPage();

        app.UseStaticFiles();
        app.UseRouting();
        app.UseAntiforgery();
        app.UseNxtMiddleware();
        app.MapNxt();

        // Blazor pipeline — handles every .razor page discovered by the generator. The
        // generator emits [Route] + (optional) [RenderModeInteractiveServer] attributes on
        // each page partial; MapRazorComponents<Root> finds them via reflection and renders
        // them with proper interactivity (SignalR for server mode, WASM hydration for wasm).
        var builder = app.MapRazorComponents<Components.Root>()
            .AddInteractiveServerRenderMode()
            .AddInteractiveWebAssemblyRenderMode();
        foreach (var asm in NxtStartupBridge.UserAssemblies)
            builder.AddAdditionalAssemblies(asm);

        return app;
    }
}
