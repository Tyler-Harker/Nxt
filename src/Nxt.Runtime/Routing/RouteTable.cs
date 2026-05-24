namespace Nxt.Routing;

/// <summary>
/// Process-wide registry of all routes discovered by the source generator.
/// The generator emits a static initializer that populates this at startup.
/// </summary>
public static class RouteTable
{
    private static readonly List<RouteDescriptor> _routes = new();

    public static IReadOnlyList<RouteDescriptor> Routes => _routes;

    /// <summary>Called by generated code at app startup.</summary>
    public static void Register(RouteDescriptor descriptor) => _routes.Add(descriptor);
}

/// <summary>
/// One API endpoint discovered by the source generator.
/// </summary>
public sealed record ApiEndpointDescriptor(
    string UrlPattern,
    string HttpMethod,
    Type HandlerType,
    string HandlerMethod);

public static class ApiEndpointTable
{
    private static readonly List<ApiEndpointDescriptor> _endpoints = new();
    public static IReadOnlyList<ApiEndpointDescriptor> Endpoints => _endpoints;
    public static void Register(ApiEndpointDescriptor d) => _endpoints.Add(d);
}

/// <summary>
/// One middleware discovered by the source generator.
/// </summary>
public sealed record MiddlewareDescriptor(Type MiddlewareType, int Order);

public static class MiddlewareTable
{
    private static readonly List<MiddlewareDescriptor> _middleware = new();
    public static IReadOnlyList<MiddlewareDescriptor> Middleware =>
        _middleware.OrderBy(m => m.Order).ToList();
    public static void Register(MiddlewareDescriptor d) => _middleware.Add(d);
}
