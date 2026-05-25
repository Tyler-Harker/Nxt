using Microsoft.AspNetCore.Builder;

namespace Nxt;

/// <summary>
/// Hooks into Nxt's bootstrap. Each callback runs at a well-defined slot in the pipeline so
/// apps can register services and middleware without needing to fork the framework.
///
/// Pipeline order:
/// <code>
///   builder = WebApplication.CreateBuilder(args)
///   builder.Services.AddNxt()
///   [ConfigureServices]                ← register DbContext, Identity, etc.
///   app = builder.Build()
///
///   if Development: UseDeveloperExceptionPage
///   UseStaticFiles
///   [ConfigureBeforeRouting]           ← global request-level concerns (e.g. UseForwardedHeaders)
///   UseRouting
///   [ConfigureMiddleware]              ← UseAuthentication / UseAuthorization / UseSession
///   UseAntiforgery
///   UseNxtMiddleware (your [Middleware] classes)
///   MapNxt + MapRazorComponents&lt;Root&gt;
///   [ConfigureAfterEndpoints]          ← extra map calls (MapHub, MapGet, fallback)
///
///   app.RunAsync()
/// </code>
/// </summary>
public sealed class NxtHostOptions
{
    /// <summary>Register DI services. Runs before <c>builder.Build()</c>.</summary>
    public Action<WebApplicationBuilder>? ConfigureServices { get; set; }

    /// <summary>Add middleware that must run before <c>UseRouting</c> (rare —
    /// most things should go in <see cref="ConfigureMiddleware"/>).</summary>
    public Action<WebApplication>? ConfigureBeforeRouting { get; set; }

    /// <summary>Add middleware that runs between <c>UseRouting</c> and endpoint mapping.
    /// This is where <c>UseAuthentication</c>, <c>UseAuthorization</c>, <c>UseSession</c>, etc. belong.</summary>
    public Action<WebApplication>? ConfigureMiddleware { get; set; }

    /// <summary>Map additional endpoints (e.g. <c>MapHub</c>, custom <c>MapGet</c>, fallback handlers)
    /// after Nxt has mapped its file-based pages and APIs.</summary>
    public Action<WebApplication>? ConfigureAfterEndpoints { get; set; }

    /// <summary>Chain extra Razor-components endpoint conventions onto the result of
    /// <c>MapRazorComponents&lt;Root&gt;</c>. Use this to add <c>AddInteractiveWebAssemblyRenderMode()</c>
    /// or <c>AddAdditionalAssemblies(...)</c> for a Blazor WebAssembly client project.</summary>
    public Action<RazorComponentsEndpointConventionBuilder>? ConfigureRazorEndpoints { get; set; }
}
