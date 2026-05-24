using Nxt;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SampleApp.Middleware;

/// <summary>Auto-registered request logger — the source generator finds <c>[Middleware]</c> classes
/// and wires them into the pipeline ordered by <see cref="MiddlewareAttribute.Order"/>.</summary>
[Middleware(order: 10)]
public class RequestLogger(ILogger<RequestLogger> logger)
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await next(context);
        logger.LogInformation("{Method} {Path} → {Status} in {ElapsedMs}ms",
            context.Request.Method, context.Request.Path, context.Response.StatusCode, sw.ElapsedMilliseconds);
    }
}
