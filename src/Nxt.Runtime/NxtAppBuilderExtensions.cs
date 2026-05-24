using Nxt.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Nxt;

public static class NxtAppBuilderExtensions
{
    /// <summary>
    /// Inserts every middleware class the source generator discovered (sorted by <see cref="MiddlewareAttribute.Order"/>)
    /// into the request pipeline. Middleware classes must define an <c>InvokeAsync(HttpContext, RequestDelegate)</c>
    /// method, matching ASP.NET's convention.
    /// </summary>
    public static IApplicationBuilder UseNxtMiddleware(this IApplicationBuilder app)
    {
        foreach (var descriptor in MiddlewareTable.Middleware)
        {
            var captured = descriptor;
            app.Use(async (ctx, next) =>
            {
                var middleware = ctx.RequestServices.GetRequiredService(captured.MiddlewareType);
                var method = captured.MiddlewareType.GetMethod("InvokeAsync")
                    ?? throw new InvalidOperationException(
                        $"Middleware '{captured.MiddlewareType.FullName}' must define InvokeAsync(HttpContext, RequestDelegate).");
                var task = (Task?)method.Invoke(middleware, new object[] { ctx, (RequestDelegate)(_ => next()) });
                if (task is not null) await task;
            });
        }
        return app;
    }
}
