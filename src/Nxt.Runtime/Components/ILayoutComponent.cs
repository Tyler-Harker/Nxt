using Microsoft.AspNetCore.Components;

namespace Nxt.Components;

/// <summary>
/// Marker for Nxt layouts. Any Blazor component with a <c>Body</c> RenderFragment parameter
/// can serve as a layout. Inheriting <see cref="LayoutComponentBase"/> automatically satisfies this.
/// </summary>
public interface ILayoutComponent { }
