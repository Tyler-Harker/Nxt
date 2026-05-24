namespace Nxt;

/// <summary>
/// Marks a page (Blazor component or Razor view) as a candidate for static generation at build time.
/// Equivalent to Next.js <c>getStaticProps</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class StaticPageAttribute : Attribute { }

/// <summary>
/// Marks a static page for incremental regeneration. The page is served from cache;
/// after <see cref="Seconds"/> seconds it is re-rendered in the background and the cache is replaced.
/// Equivalent to Next.js <c>revalidate</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class RevalidateAttribute(int seconds) : Attribute
{
    public int Seconds { get; } = seconds;
}

/// <summary>
/// Opts a Blazor page into interactive server-side rendering with SignalR. The Razor compiler
/// emits these attributes from <c>@rendermode InteractiveServer</c> directives in .razor files;
/// the Nxt source generator emits them from <c>@* @nxt:interactive-server *@</c>
/// directives. Blazor's endpoint pipeline finds any <see cref="Microsoft.AspNetCore.Components.RenderModeAttribute"/>
/// subclass on a routed component and wires interactivity accordingly.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class InteractiveServerAttribute : Microsoft.AspNetCore.Components.RenderModeAttribute
{
    public override Microsoft.AspNetCore.Components.IComponentRenderMode Mode
        => Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer;
}

/// <summary>
/// Opts a Blazor page into interactive WebAssembly rendering. See <see cref="InteractiveServerAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class InteractiveWebAssemblyAttribute : Microsoft.AspNetCore.Components.RenderModeAttribute
{
    public override Microsoft.AspNetCore.Components.IComponentRenderMode Mode
        => Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveWebAssembly;
}

/// <summary>
/// Marks a class as an API endpoint. The class methods named after HTTP verbs (GET, POST, PUT, DELETE, PATCH)
/// are mapped to that route. If absent, the source generator falls back to file-path conventions for files
/// under <c>Api/</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ApiRouteAttribute(string template) : Attribute
{
    public string Template { get; } = template;
}

/// <summary>
/// Marks a class as middleware that is auto-registered in the request pipeline.
/// Lower <see cref="Order"/> runs earlier.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class MiddlewareAttribute(int order = 100) : Attribute
{
    public int Order { get; } = order;
}

/// <summary>
/// Declares the layout component that should wrap this page. The layout type must implement
/// <see cref="Components.ILayoutComponent"/> or extend <see cref="Microsoft.AspNetCore.Components.LayoutComponentBase"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class UseLayoutAttribute(Type layoutType) : Attribute
{
    public Type LayoutType { get; } = layoutType;
}
