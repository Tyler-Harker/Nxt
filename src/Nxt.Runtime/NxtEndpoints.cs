using System.Reflection;
using Nxt.Rendering;
using Nxt.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Nxt;

public static class NxtEndpoints
{
    /// <summary>
    /// Maps every file-based page and API endpoint discovered by the source generator.
    /// Call this from <c>Program.cs</c> after <c>builder.Build()</c>:
    /// <code>app.MapNxt();</code>
    /// </summary>
    public static IEndpointRouteBuilder MapNxt(this IEndpointRouteBuilder endpoints)
    {
        MapPages(endpoints);
        MapApiRoutes(endpoints);
        return endpoints;
    }

    private static void MapPages(IEndpointRouteBuilder endpoints)
    {
        foreach (var route in RouteTable.Routes)
        {
            // Blazor (.razor) pages are handled by MapRazorComponents<Root> downstream,
            // which gives them real interactivity. Skip them here to avoid double-mapping.
            if (route.Kind == PageKind.Blazor) continue;
            var captured = route;
            // ASP.NET's route engine handles parameter binding for us; we just hand it the pattern.
            endpoints.MapGet(captured.UrlPattern, async (HttpContext ctx) =>
            {
                var cache = ctx.RequestServices.GetRequiredService<StaticPageCache>();
                var dispatcher = ctx.RequestServices.GetRequiredService<PageRendererDispatcher>();

                var routeValues = ExtractRouteValues(ctx);

                // Static + ISR: serve from cache, regenerate when stale (request-time fallback).
                if (captured.RenderMode is RenderMode.Static or RenderMode.IncrementalStatic)
                {
                    var url = ctx.Request.Path.Value ?? captured.UrlPattern;
                    if (cache.TryGet(url, out var cached) && (!cached.IsStale || captured.RenderMode == RenderMode.Static))
                    {
                        return Results.Content(cached.Html, "text/html; charset=utf-8");
                    }

                    var fresh = await dispatcher.RenderAsync(ctx, captured, routeValues);
                    cache.Set(url, fresh, captured);
                    return Results.Content(fresh, "text/html; charset=utf-8");
                }

                // SSR (and interactive — interactivity is layered on by Blazor's own runtime).
                var html = await dispatcher.RenderAsync(ctx, captured, routeValues);
                return Results.Content(html, "text/html; charset=utf-8");
            });
        }
    }

    private static void MapApiRoutes(IEndpointRouteBuilder endpoints)
    {
        foreach (var ep in ApiEndpointTable.Endpoints)
        {
            var captured = ep;
            var method = captured.HandlerType.GetMethod(captured.HandlerMethod,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    $"API handler method '{captured.HandlerType.Name}.{captured.HandlerMethod}' not found.");

            var pattern = "/api/" + captured.UrlPattern.TrimStart('/');

            endpoints.MapMethods(pattern, new[] { captured.HttpMethod }, async (HttpContext ctx) =>
            {
                var instance = method.IsStatic
                    ? null
                    : ActivatorUtilities.CreateInstance(ctx.RequestServices, captured.HandlerType);

                var args = await BindArgumentsAsync(ctx, method);
                var result = method.Invoke(instance, args);

                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    var resultProp = task.GetType().GetProperty("Result");
                    result = resultProp?.GetValue(task);
                    if (result is null || result is VoidTaskResult) return Results.Ok();
                }

                return result switch
                {
                    IResult ir => ir,
                    null => Results.Ok(),
                    string s => Results.Text(s),
                    _ => Results.Json(result),
                };
            });
        }
    }

    private static IReadOnlyDictionary<string, string?> ExtractRouteValues(HttpContext ctx)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ctx.Request.RouteValues)
            dict[kv.Key] = kv.Value?.ToString();
        return dict;
    }

    private static async Task<object?[]> BindArgumentsAsync(HttpContext ctx, MethodInfo method)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType == typeof(HttpContext)) { args[i] = ctx; continue; }
            if (ctx.Request.RouteValues.TryGetValue(p.Name!, out var rv))
            {
                args[i] = ConvertParam(rv?.ToString(), p.ParameterType);
                continue;
            }
            if (ctx.Request.Query.TryGetValue(p.Name!, out var qv))
            {
                args[i] = ConvertParam(qv.ToString(), p.ParameterType);
                continue;
            }
            if (p.ParameterType.IsClass && p.ParameterType != typeof(string)
                && ctx.Request.HasJsonContentType())
            {
                args[i] = await ctx.Request.ReadFromJsonAsync(p.ParameterType);
                continue;
            }
            args[i] = p.HasDefaultValue ? p.DefaultValue
                : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null);
        }
        return args;
    }

    private static object? ConvertParam(string? raw, Type t)
    {
        if (raw is null) return t.IsValueType ? Activator.CreateInstance(t) : null;
        if (t == typeof(string)) return raw;
        var underlying = Nullable.GetUnderlyingType(t) ?? t;
        try { return Convert.ChangeType(raw, underlying); } catch { return raw; }
    }

    private readonly struct VoidTaskResult { }
}
