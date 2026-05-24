using System.Reflection;
using System.Runtime.CompilerServices;
using Nxt.Routing;
using Microsoft.AspNetCore.Components;

namespace Nxt;

/// <summary>
/// Buffer for routes emitted by the source generator before <c>AddNxt()</c> is called.
/// The generator emits a <see cref="ModuleInitializerAttribute"/> that calls <see cref="RegisterPending"/>
/// per file-discovered page; <see cref="ResolveAll"/> then walks the loaded assemblies to bind each
/// pending route to its compiled component type.
/// </summary>
public static class NxtStartupBridge
{
    private static readonly List<PendingRoute> _pending = new();
    private static bool _resolved;
    private static readonly HashSet<Assembly> _userAssemblies = new();

    public static IReadOnlyCollection<Assembly> UserAssemblies => _userAssemblies;

    public static void RegisterPending(PendingRoute pending) => _pending.Add(pending);

    /// <summary>Called by <c>AddNxt()</c> once at startup.</summary>
    public static void ResolveAll()
    {
        if (_resolved) return;
        _resolved = true;

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !IsFrameworkAssembly(a))
            .ToArray();

        foreach (var pending in _pending)
        {
            Type? componentType = null;
            string? viewPath = null;

            if (pending.Kind == PageKind.Blazor)
            {
                componentType = ResolveBlazorComponent(assemblies, pending.FilePath, pending.UrlPattern)
                    ?? throw new InvalidOperationException(
                        $"Could not locate compiled Blazor component for '{pending.FilePath}' (URL '{pending.UrlPattern}'). " +
                        "Ensure the file lives under Pages/ and the project compiled cleanly.");
                if (componentType.Assembly is { } asm)
                    _userAssemblies.Add(asm);
            }
            else
            {
                viewPath = "/" + pending.FilePath.Replace('\\', '/').TrimStart('/');
            }

            Type? layoutType = null;
            if (pending.LayoutTypeName is not null)
                layoutType = ResolveTypeByName(assemblies, pending.LayoutTypeName);

            var route = new RouteDescriptor(
                UrlPattern: pending.UrlPattern,
                Kind: pending.Kind,
                RenderMode: pending.RenderMode,
                ComponentType: componentType,
                ViewPath: viewPath,
                LayoutType: layoutType,
                RevalidateSeconds: pending.RevalidateSeconds);

            RouteTable.Register(route);
        }
    }

    /// <summary>
    /// Finds a Blazor component compiled from the given file path. Blazor's RouteAttribute provides
    /// the most reliable match when present; otherwise we fall back to matching by class name.
    /// </summary>
    private static Type? ResolveBlazorComponent(Assembly[] assemblies, string filePath, string urlPattern)
    {
        // 1. Match by [Route] attribute (Blazor emits this from @page directives).
        foreach (var asm in assemblies)
        {
            foreach (var t in SafeGetTypes(asm))
            {
                if (!typeof(IComponent).IsAssignableFrom(t)) continue;
                var routeAttrs = t.GetCustomAttributes<RouteAttribute>();
                foreach (var r in routeAttrs)
                {
                    if (PatternsEquivalent(r.Template, urlPattern)) return t;
                }
            }
        }

        // 2. Match by derived class name. /Pages/Blog/Post.razor → namespace ends in ".Pages.Blog", class "Post".
        var (expectedNamespaceSuffix, expectedClassName) = DeriveTypeNameFromPath(filePath);
        foreach (var asm in assemblies)
        {
            foreach (var t in SafeGetTypes(asm))
            {
                if (!typeof(IComponent).IsAssignableFrom(t)) continue;
                if (t.Name == expectedClassName &&
                    (t.Namespace?.EndsWith(expectedNamespaceSuffix, StringComparison.Ordinal) ?? false))
                    return t;
            }
        }
        return null;
    }

    private static Type? ResolveTypeByName(Assembly[] assemblies, string fullName)
    {
        foreach (var asm in assemblies)
        {
            var t = asm.GetType(fullName, throwOnError: false);
            if (t is not null) return t;
        }
        return Type.GetType(fullName, throwOnError: false);
    }

    private static (string NamespaceSuffix, string ClassName) DeriveTypeNameFromPath(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        var withoutExt = normalized[..normalized.LastIndexOf('.')];
        var parts = withoutExt.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var className = SanitizeIdentifier(parts[^1]);
        var nsSuffix = "." + string.Join(".", parts[..^1].Select(SanitizeIdentifier));
        return (nsSuffix, className);
    }

    private static string SanitizeIdentifier(string raw)
    {
        // Blazor's RazorSDK transforms file names with invalid C# identifier chars by replacing
        // them with underscores (e.g. [slug] → _slug_).
        var chars = raw.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        return new string(chars);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private static bool IsFrameworkAssembly(Assembly asm)
    {
        var name = asm.GetName().Name ?? "";
        return name.StartsWith("System.") || name.StartsWith("Microsoft.")
            || name == "mscorlib" || name == "netstandard";
    }

    private static bool PatternsEquivalent(string a, string b)
    {
        static string Normalize(string p) => "/" + p.Trim('/').ToLowerInvariant();
        return Normalize(a) == Normalize(b);
    }
}

/// <summary>One entry emitted by the source generator for the runtime to bind at startup.</summary>
public sealed record PendingRoute(
    string UrlPattern,
    string FilePath,
    PageKind Kind,
    RenderMode RenderMode,
    string? LayoutTypeName,
    int RevalidateSeconds);
