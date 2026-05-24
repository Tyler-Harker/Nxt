using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nxt.Generators;

/// <summary>
/// Roslyn incremental generator that scans <c>Pages/</c>, <c>Api/</c>, and <c>Middleware/</c>
/// in the consuming project and emits a <c>[ModuleInitializer]</c> registering the discovered
/// routes, endpoints, and middleware with the Nxt runtime.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class NxtRouteGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Pull MSBuildProjectDirectory + RootNamespace so we can compute relative paths and emit
        // partial classes in the user's namespace.
        var projectInfo = context.AnalyzerConfigOptionsProvider
            .Select((opts, _) =>
            {
                opts.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out var dir);
                opts.GlobalOptions.TryGetValue("build_property.RootNamespace", out var ns);
                return (Dir: dir ?? string.Empty, RootNamespace: ns ?? string.Empty);
            });
        var projectDir = projectInfo.Select((p, _) => p.Dir);

        // Pages: .razor + .cshtml files under Pages/ (excluding _layout.razor and other "_" files).
        var pageFiles = context.AdditionalTextsProvider
            .Where(f => IsUnder(f.Path, "Pages") && (HasExt(f.Path, ".razor") || HasExt(f.Path, ".cshtml")))
            .Combine(projectDir)
            .Select((pair, ct) => DescribePage(pair.Left, pair.Right, ct))
            .Where(d => d is not null);

        // Layouts: any "_layout.razor" file under Pages/. The convention mirrors Next.js's
        // app-router layout.tsx — a layout file applies to the page in its directory and below.
        // Multiple nested layouts: the nearest (deepest) wins for v1; nesting is a future feature.
        var layoutFiles = context.AdditionalTextsProvider
            .Where(f => HasExt(f.Path, ".razor")
                && string.Equals(Path.GetFileName(f.Path), "_layout.razor", StringComparison.OrdinalIgnoreCase)
                && IsUnder(f.Path, "Pages"))
            .Combine(projectDir)
            .Select((pair, _) => DescribeLayout(pair.Left, pair.Right))
            .Where(l => l is not null);

        // Discover three kinds of classes:
        //   1. [ApiRoute("...")] anywhere (explicit URL)
        //   2. Any class with HTTP-verb methods inside an "api/" folder (convention, colocated)
        //   3. [Middleware] anywhere
        var apiClasses = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (n, _) =>
                {
                    if (n is not ClassDeclarationSyntax cds) return false;
                    if (cds.AttributeLists.Count > 0) return true;
                    var path = n.SyntaxTree.FilePath;
                    if (string.IsNullOrEmpty(path)) return false;
                    var lower = path.Replace('\\', '/').ToLowerInvariant();
                    return lower.Contains("/api/");
                },
                transform: (ctx, _) => ExtractAttributedClass(ctx))
            .Where(d => d is not null);

        var combined = pageFiles.Collect()
            .Combine(apiClasses.Collect())
            .Combine(layoutFiles.Collect())
            .Combine(projectInfo);

        context.RegisterSourceOutput(combined, (spc, tuple) =>
        {
            var pages = tuple.Left.Left.Left.Where(p => p is not null).Cast<PageDescriptor>().ToList();
            var attributed = tuple.Left.Left.Right.Where(p => p is not null).Cast<AttributedClass>().ToList();
            var layouts = tuple.Left.Right.Where(l => l is not null).Cast<LayoutDescriptor>().ToList();
            var rootNs = tuple.Right.RootNamespace;

            // Layout inference: each page that doesn't already specify @nxt:layout gets
            // the nearest _layout.razor walking up from its directory.
            var resolvedPages = pages.Select(p => p with
            {
                LayoutTypeName = p.LayoutTypeName ?? FindNearestLayout(layouts, p.FilePath, rootNs),
            }).ToList();

            var source = Emit(resolvedPages, attributed);
            spc.AddSource("NxtGenerated.g.cs", SourceText.From(source, Encoding.UTF8));

            foreach (var page in resolvedPages.Where(p => p.Kind == "Blazor"))
            {
                var partial = EmitBlazorPagePartial(page, rootNs);
                if (partial is null) continue;
                var hint = SanitizeHintName(page.FilePath);
                spc.AddSource($"NxtPage.{hint}.g.cs", SourceText.From(partial, Encoding.UTF8));
            }

            // Nested layouts: for each _layout.razor that has a strictly-outer _layout, emit a
            // partial with [Layout(typeof(OuterLayout))]. Blazor follows the chain automatically,
            // so a page using the deepest layout transitively renders inside every ancestor.
            foreach (var layout in layouts)
            {
                var outer = FindOuterLayout(layouts, layout, rootNs);
                if (outer is null) continue;
                var partial = EmitLayoutPartial(layout, outer, rootNs);
                if (partial is null) continue;
                var hint = SanitizeHintName(layout.FilePath);
                spc.AddSource($"NxtLayout.{hint}.g.cs", SourceText.From(partial, Encoding.UTF8));
            }
        });
    }

    /// <summary>
    /// Picks the deepest <c>_layout.razor</c> in a strictly-outer directory of the given layout.
    /// Returns the outer layout's fully-qualified type name, or null if this layout is the root.
    /// </summary>
    private static string? FindOuterLayout(List<LayoutDescriptor> layouts, LayoutDescriptor self, string rootNs)
    {
        if (string.IsNullOrEmpty(rootNs)) return null;
        var selfDir = NormalizeDir(Path.GetDirectoryName(self.FilePath) ?? string.Empty);
        LayoutDescriptor? best = null;
        var bestDepth = -1;
        foreach (var layout in layouts)
        {
            if (ReferenceEquals(layout, self)) continue;
            var dir = NormalizeDir(Path.GetDirectoryName(layout.FilePath) ?? string.Empty);
            // Must be a strict ancestor — same dir doesn't count.
            if (dir.Length >= selfDir.Length) continue;
            if (!selfDir.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
            if (dir.Length > bestDepth) { best = layout; bestDepth = dir.Length; }
        }
        if (best is null) return null;
        var (ns, cls) = DerivePagesNamespaceAndClass(best.FilePath, rootNs);
        return ns is null || cls is null ? null : $"{ns}.{cls}";
    }

    private static string? EmitLayoutPartial(LayoutDescriptor layout, string outerTypeName, string rootNs)
    {
        var (ns, cls) = DerivePagesNamespaceAndClass(layout.FilePath, rootNs);
        if (ns is null || cls is null) return null;
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"[global::Microsoft.AspNetCore.Components.LayoutAttribute(typeof(global::{outerTypeName}))]");
        sb.AppendLine($"public partial class {cls} {{ }}");
        return sb.ToString();
    }

    /// <summary>
    /// Picks the deepest <c>_layout.razor</c> whose directory is an ancestor of the page.
    /// e.g. for <c>Pages/Blog/[slug].razor</c>, prefers <c>Pages/Blog/_layout.razor</c> over
    /// <c>Pages/_layout.razor</c>. Returns the layout's fully-qualified type name, or null if
    /// no enclosing layout is found.
    /// </summary>
    private static string? FindNearestLayout(List<LayoutDescriptor> layouts, string pageFilePath, string rootNs)
    {
        if (string.IsNullOrEmpty(rootNs) || layouts.Count == 0) return null;
        var pageDir = NormalizeDir(Path.GetDirectoryName(pageFilePath) ?? string.Empty);
        LayoutDescriptor? best = null;
        var bestDepth = -1;
        foreach (var layout in layouts)
        {
            var layoutDir = NormalizeDir(Path.GetDirectoryName(layout.FilePath) ?? string.Empty);
            if (!pageDir.StartsWith(layoutDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (layoutDir.Length > bestDepth) { best = layout; bestDepth = layoutDir.Length; }
        }
        if (best is null) return null;
        var (ns, cls) = DerivePagesNamespaceAndClass(best.FilePath, rootNs);
        return ns is null || cls is null ? null : $"{ns}.{cls}";
    }

    private static string NormalizeDir(string p) =>
        p.Replace('\\', '/').TrimEnd('/') + "/";

    private static LayoutDescriptor? DescribeLayout(AdditionalText file, string projectDir)
    {
        var relative = MakeRelative(file.Path, projectDir).Replace('\\', '/');
        if (relative.IndexOf("Pages/", StringComparison.OrdinalIgnoreCase) < 0) return null;
        return new LayoutDescriptor(relative);
    }

    private sealed record LayoutDescriptor(string FilePath);

    private static string SanitizeHintName(string filePath)
    {
        var s = filePath.Replace('\\', '_').Replace('/', '_').Replace('.', '_').Replace('[', '_').Replace(']', '_');
        return s;
    }

    private static bool IsUnder(string path, string folder)
    {
        var norm = path.Replace('\\', '/');
        return norm.Contains("/" + folder + "/", StringComparison.OrdinalIgnoreCase)
            || norm.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExt(string path, string ext) =>
        path.EndsWith(ext, StringComparison.OrdinalIgnoreCase);

    private static PageDescriptor? DescribePage(AdditionalText file, string projectDir, System.Threading.CancellationToken ct)
    {
        var relative = MakeRelative(file.Path, projectDir).Replace('\\', '/');
        var pagesIdx = relative.IndexOf("Pages/", StringComparison.OrdinalIgnoreCase);
        if (pagesIdx < 0) return null;
        var relFromPages = relative.Substring(pagesIdx + "Pages/".Length);
        var ext = Path.GetExtension(relFromPages);
        var withoutExt = relFromPages.Substring(0, relFromPages.Length - ext.Length);

        // Skip layout files; they're consumed via [UseLayout] not as routes.
        if (Path.GetFileNameWithoutExtension(relFromPages).StartsWith("_", StringComparison.Ordinal))
            return null;

        var url = ToUrlPattern(withoutExt);
        var kind = ext.Equals(".razor", StringComparison.OrdinalIgnoreCase) ? "Blazor" : "Razor";

        var text = file.GetText(ct)?.ToString() ?? string.Empty;
        var directives = ParseDirectives(text);

        return new PageDescriptor(
            FilePath: relative,
            UrlPattern: url,
            Kind: kind,
            RenderMode: directives.RenderMode,
            LayoutTypeName: directives.LayoutTypeName,
            RevalidateSeconds: directives.RevalidateSeconds);
    }

    private static string MakeRelative(string full, string projectDir)
    {
        if (string.IsNullOrEmpty(projectDir)) return full;
        var fullNorm = full.Replace('\\', '/');
        var dirNorm = projectDir.Replace('\\', '/').TrimEnd('/');
        return fullNorm.StartsWith(dirNorm + "/", StringComparison.OrdinalIgnoreCase)
            ? fullNorm.Substring(dirNorm.Length + 1)
            : full;
    }

    /// <summary>Converts <c>blog/[slug]</c> → <c>/blog/{slug}</c>, <c>index</c> → <c>/</c>.</summary>
    private static string ToUrlPattern(string pathWithoutExt)
    {
        var parts = pathWithoutExt.Split('/');
        var sb = new StringBuilder();
        foreach (var raw in parts)
        {
            // Route groups (foo) carry no URL segment — used purely for layout scoping.
            if (raw.StartsWith("(") && raw.EndsWith(")")) continue;
            if (string.Equals(raw, "index", StringComparison.OrdinalIgnoreCase) && raw == parts[parts.Length - 1])
                continue;
            sb.Append('/');
            if (raw.StartsWith("[...") && raw.EndsWith("]"))
                sb.Append("{*").Append(raw.Substring(4, raw.Length - 5)).Append("}");
            else if (raw.StartsWith("[") && raw.EndsWith("]"))
                sb.Append("{").Append(raw.Substring(1, raw.Length - 2)).Append("}");
            else
                sb.Append(raw.ToLowerInvariant());
        }
        var result = sb.ToString();
        return result.Length == 0 ? "/" : result;
    }

    private static readonly Regex DirectiveRegex = new(
        @"(?:@\*|<!--)\s*@nxt:\s*([a-zA-Z][\w-]*)(?:=([^*\s>]+))?\s*(?:\*@|-->)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static DirectiveSet ParseDirectives(string source)
    {
        var renderMode = "Server";
        string? layout = null;
        var revalidate = 0;
        foreach (Match m in DirectiveRegex.Matches(source))
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            var value = m.Groups[2].Success ? m.Groups[2].Value : null;
            switch (key)
            {
                case "static": renderMode = "Static"; break;
                case "isr":
                case "revalidate":
                    renderMode = "IncrementalStatic";
                    if (value is not null && int.TryParse(value, out var sec)) revalidate = sec;
                    break;
                case "interactive-server": renderMode = "InteractiveServer"; break;
                case "interactive-wasm": renderMode = "InteractiveWebAssembly"; break;
                case "layout": layout = value; break;
            }
        }
        return new DirectiveSet(renderMode, layout, revalidate);
    }

    private static AttributedClass? ExtractAttributedClass(GeneratorSyntaxContext ctx)
    {
        if (ctx.Node is not ClassDeclarationSyntax cds) return null;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(cds) as INamedTypeSymbol;
        if (symbol is null) return null;

        string? apiTemplate = null;
        int? middlewareOrder = null;
        foreach (var attr in symbol.GetAttributes())
        {
            var name = attr.AttributeClass?.ToDisplayString();
            if (name == "Nxt.ApiRouteAttribute" && attr.ConstructorArguments.Length == 1)
                apiTemplate = attr.ConstructorArguments[0].Value as string;
            else if (name == "Nxt.MiddlewareAttribute")
                middlewareOrder = attr.ConstructorArguments.Length > 0
                    ? attr.ConstructorArguments[0].Value as int? ?? 100
                    : 100;
        }

        var httpMethods = new List<string>();
        foreach (var member in symbol.GetMembers().OfType<IMethodSymbol>())
        {
            var n = member.Name.ToUpperInvariant();
            if (n is "GET" or "POST" or "PUT" or "DELETE" or "PATCH")
                httpMethods.Add(n);
        }

        // Convention-based discovery: if the class lives in an api/ folder, has HTTP-verb methods,
        // and no explicit [ApiRoute], derive its template from the file path.
        if (apiTemplate is null && middlewareOrder is null && httpMethods.Count > 0)
        {
            apiTemplate = TryDeriveApiTemplateFromPath(ctx.Node.SyntaxTree.FilePath);
        }

        if (apiTemplate is null && middlewareOrder is null) return null;

        return new AttributedClass(
            FullTypeName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", ""),
            ApiTemplate: apiTemplate,
            HttpMethods: httpMethods,
            MiddlewareOrder: middlewareOrder);
    }

    /// <summary>
    /// Derives an API URL template (the part after <c>/api/</c>) from a source file path.
    /// Examples:
    ///   <c>.../Api/hello.cs</c>             → <c>hello</c>            → <c>/api/hello</c>
    ///   <c>.../Pages/Blog/api/posts.cs</c>  → <c>blog/posts</c>       → <c>/api/blog/posts</c>
    ///   <c>.../Pages/Blog/api/[id].cs</c>   → <c>blog/{id}</c>        → <c>/api/blog/{id}</c>
    ///   <c>.../Features/Users/api/list.cs</c> → <c>features/users/list</c>
    /// </summary>
    private static string? TryDeriveApiTemplateFromPath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var norm = filePath!.Replace('\\', '/');
        var segments = norm.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        // Find the last "api" segment (case-insensitive) — that's the routing root.
        var apiIdx = -1;
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (string.Equals(segments[i], "api", StringComparison.OrdinalIgnoreCase))
            { apiIdx = i; break; }
        }
        if (apiIdx < 0) return null;

        // Path before "api/": strip everything up through "Pages/" if present, else through the
        // last folder we recognize as project-internal. Without project dir info, take only the
        // segments between "Pages" and "api" — falls back to all parent segments if no Pages.
        var before = segments.Take(apiIdx).ToArray();
        var pagesIdx = -1;
        for (var i = before.Length - 1; i >= 0; i--)
        {
            if (string.Equals(before[i], "Pages", StringComparison.OrdinalIgnoreCase))
            { pagesIdx = i; break; }
        }

        string[] parentSegments;
        if (pagesIdx >= 0)
        {
            parentSegments = before.Skip(pagesIdx + 1).ToArray();
        }
        else
        {
            // Top-level Api/ folder is at the project root — treat parent as empty.
            // Without the project dir we can't be exact, so we use a heuristic: an "api" folder
            // whose parent is a known top-level folder name like "src" or matches the file's
            // immediate-parent project directory yields an empty prefix.
            parentSegments = before.Length > 0 && IsLikelyProjectRoot(before[before.Length - 1])
                ? Array.Empty<string>()
                : Array.Empty<string>();
            // (We deliberately keep prefix empty here. Users wanting deeper prefixes outside
            // Pages/ should opt into [ApiRoute("explicit/path")] for clarity.)
        }

        var after = segments.Skip(apiIdx + 1).ToArray();
        if (after.Length == 0) return null;
        var fileName = Path.GetFileNameWithoutExtension(after[after.Length - 1]);
        var subfolderSegments = after.Take(after.Length - 1).ToArray();

        var all = parentSegments.Concat(subfolderSegments).Append(fileName)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(ToUrlSegment);

        return string.Join("/", all);
    }

    private static bool IsLikelyProjectRoot(string segment) => true;

    private static string ToUrlSegment(string raw)
    {
        if (raw.StartsWith("[...") && raw.EndsWith("]"))
            return "{*" + raw.Substring(4, raw.Length - 5) + "}";
        if (raw.StartsWith("[") && raw.EndsWith("]"))
            return "{" + raw.Substring(1, raw.Length - 2) + "}";
        return raw.ToLowerInvariant();
    }

    /// <summary>
    /// Emits a partial class for one Blazor page that adds <c>[Route]</c> (so Blazor's Router
    /// finds it), an optional <c>[Layout]</c>, and an optional render-mode attribute
    /// (<c>[RenderModeInteractiveServer]</c> etc.) that makes the page actually interactive
    /// when MapRazorComponents&lt;Root&gt; renders it.
    ///
    /// The partial matches the class the Razor SDK generates for the .razor file:
    ///   <c>Pages/Blog/[slug].razor</c> → namespace <c>{RootNS}.Pages.Blog</c>, class <c>_slug_</c>
    /// </summary>
    private static string? EmitBlazorPagePartial(PageDescriptor page, string rootNs)
    {
        if (string.IsNullOrEmpty(rootNs)) return null;
        var (ns, cls) = DerivePagesNamespaceAndClass(page.FilePath, rootNs);
        if (ns is null || cls is null) return null;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"[global::Microsoft.AspNetCore.Components.RouteAttribute(\"{Escape(page.UrlPattern)}\")]");
        if (page.LayoutTypeName is not null)
            sb.AppendLine($"[global::Microsoft.AspNetCore.Components.LayoutAttribute(typeof(global::{page.LayoutTypeName}))]");
        var renderModeAttr = page.RenderMode switch
        {
            "InteractiveServer" => "global::Nxt.InteractiveServerAttribute",
            "InteractiveWebAssembly" => "global::Nxt.InteractiveWebAssemblyAttribute",
            _ => null,
        };
        if (renderModeAttr is not null)
            sb.AppendLine($"[{renderModeAttr}]");
        sb.AppendLine($"public partial class {cls} {{ }}");
        return sb.ToString();
    }

    /// <summary>Mirrors the Razor SDK's file-to-type naming so the partial matches.</summary>
    private static (string? Namespace, string? Class) DerivePagesNamespaceAndClass(string filePath, string rootNs)
    {
        var norm = filePath.Replace('\\', '/');
        var idx = norm.IndexOf("Pages/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (null, null);
        var rel = norm.Substring(idx); // "Pages/..."
        var ext = Path.GetExtension(rel);
        var withoutExt = rel.Substring(0, rel.Length - ext.Length);
        var parts = withoutExt.Split('/');
        if (parts.Length < 2) return (null, null);
        var className = SanitizeId(parts[parts.Length - 1]);
        var nsParts = parts.Take(parts.Length - 1).Select(SanitizeId);
        // Sanitize each dot-segment of rootNs too — the Razor SDK does this when computing
        // a default namespace from the project name, so we must match (e.g. project "my-app"
        // → namespace "my_app", not "my-app").
        var rootNsSanitized = string.Join(".", rootNs.Split('.').Select(SanitizeId));
        return ($"{rootNsSanitized}.{string.Join(".", nsParts)}", className);
    }

    private static string SanitizeId(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        return new string(chars);
    }

    private static string Emit(List<PageDescriptor> pages, List<AttributedClass> attributed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using Nxt;");
        sb.AppendLine("using Nxt.Routing;");
        sb.AppendLine();
        sb.AppendLine("namespace Nxt.Generated;");
        sb.AppendLine();
        sb.AppendLine("internal static class __NxtRegistrations");
        sb.AppendLine("{");
        sb.AppendLine("    [ModuleInitializer]");
        sb.AppendLine("    internal static void Init()");
        sb.AppendLine("    {");

        foreach (var page in pages)
        {
            var layoutArg = page.LayoutTypeName is null ? "null" : $"\"{page.LayoutTypeName}\"";
            sb.AppendLine($"        NxtStartupBridge.RegisterPending(new PendingRoute(" +
                $"\"{Escape(page.UrlPattern)}\", " +
                $"\"{Escape(page.FilePath)}\", " +
                $"PageKind.{page.Kind}, " +
                $"RenderMode.{page.RenderMode}, " +
                $"{layoutArg}, " +
                $"{page.RevalidateSeconds}));");
        }

        foreach (var cls in attributed)
        {
            if (cls.ApiTemplate is not null)
            {
                var methods = cls.HttpMethods.Count > 0 ? cls.HttpMethods : new List<string> { "GET" };
                foreach (var method in methods)
                {
                    sb.AppendLine($"        ApiEndpointTable.Register(new ApiEndpointDescriptor(" +
                        $"\"{Escape(cls.ApiTemplate)}\", " +
                        $"\"{method}\", " +
                        $"typeof(global::{cls.FullTypeName}), " +
                        $"\"{method}\"));");
                }
            }
            if (cls.MiddlewareOrder is int order)
            {
                sb.AppendLine($"        MiddlewareTable.Register(new MiddlewareDescriptor(" +
                    $"typeof(global::{cls.FullTypeName}), {order}));");
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed record PageDescriptor(
        string FilePath, string UrlPattern, string Kind,
        string RenderMode, string? LayoutTypeName, int RevalidateSeconds);

    private sealed record DirectiveSet(string RenderMode, string? LayoutTypeName, int RevalidateSeconds);

    private sealed record AttributedClass(
        string FullTypeName, string? ApiTemplate,
        List<string> HttpMethods, int? MiddlewareOrder);
}
