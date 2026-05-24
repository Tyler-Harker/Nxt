namespace Nxt.Routing;

/// <summary>
/// How a page should be rendered.
/// </summary>
public enum RenderMode
{
    /// <summary>Server-rendered on every request.</summary>
    Server,
    /// <summary>Pre-rendered at build time, served from disk.</summary>
    Static,
    /// <summary>Pre-rendered, cached, periodically regenerated.</summary>
    IncrementalStatic,
    /// <summary>SSR + Blazor interactive server (SignalR).</summary>
    InteractiveServer,
    /// <summary>SSR + Blazor WebAssembly hydration.</summary>
    InteractiveWebAssembly,
}

/// <summary>
/// The component technology backing a page.
/// </summary>
public enum PageKind
{
    /// <summary>A Blazor (.razor) component.</summary>
    Blazor,
    /// <summary>A Razor (.cshtml) view.</summary>
    Razor,
}

/// <summary>
/// One file-based route discovered by the source generator.
/// </summary>
public sealed record RouteDescriptor(
    string UrlPattern,
    PageKind Kind,
    RenderMode RenderMode,
    Type? ComponentType,
    string? ViewPath,
    Type? LayoutType,
    int RevalidateSeconds);
