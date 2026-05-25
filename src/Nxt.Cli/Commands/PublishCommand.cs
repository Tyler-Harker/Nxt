using System.CommandLine;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Nxt.Cli.Commands;

/// <summary>
/// <c>nxt publish</c> — three publish modes selected by flag. At least one must be given.
///
/// <list type="bullet">
///   <item><c>--static &lt;dir&gt;</c> — runs the published app on a random port, crawls every
///     reachable page by following internal &lt;a href&gt; links from <c>/</c>, and writes
///     each response as a static HTML file. Drop the resulting folder on Cloudflare Pages,
///     GitHub Pages, S3+CloudFront, Netlify, etc.</item>
///   <item><c>--image &lt;tag&gt;</c> — generates a Dockerfile (only if missing) and runs
///     <c>docker build</c>. Pair with <c>--push &lt;registry/tag&gt;</c> to also tag + push.</item>
///   <item><c>--bundle &lt;path&gt;</c> — tars the <c>publish/</c> folder for scp/manual
///     deploys. Output extension chooses compression: <c>.tar.gz</c>, <c>.tar.bz2</c>, etc.</item>
/// </list>
/// </summary>
internal static class PublishCommand
{
    public static Command Build()
    {
        var projectOpt = new Option<string?>("--project", "Project file to publish. Defaults to current directory.");
        var configOpt  = new Option<string>("--configuration", () => "Release", "Build configuration.");
        var staticOpt  = new Option<string?>(new[] { "--static", "-s" }, "Export every reachable page to static HTML in the given directory.");
        var imageOpt   = new Option<string?>(new[] { "--image", "-i" }, "Build a Docker image tagged with the given value (e.g. myapp:v1).");
        var pushOpt    = new Option<string?>("--push", "After --image, also tag (if different) and push to this registry/tag.");
        var contextOpt = new Option<string?>("--docker-context",
            "Build context for --image (default: the project's directory). Use the repo root if your csproj has ProjectReferences to siblings outside the project dir.");
        var basePathOpt = new Option<string?>("--base-path",
            "(--static only) URL path prefix to inject into every absolute internal link in the exported HTML. Use for GitHub Pages project sites: --base-path /MyRepo/. Also drops a .nojekyll file so paths starting with _ are served.");
        var bundleOpt  = new Option<string?>(new[] { "--bundle", "-b" }, "Tar the publish folder to the given path (e.g. ./dist/myapp.tar.gz).");

        var cmd = new Command("publish",
            "Publish your Nxt app — static HTML, Docker image, or a deployment tarball. Pick one or more modes via flags.")
        { projectOpt, configOpt, staticOpt, basePathOpt, imageOpt, pushOpt, contextOpt, bundleOpt };

        cmd.SetHandler(async (string? project, string config, string? staticDir, string? basePath, string? image, string? push, string? dockerContext, string? bundle) =>
        {
            if (staticDir is null && image is null && bundle is null)
            {
                Console.Error.WriteLine("Pick at least one mode: --static <dir>, --image <tag>, or --bundle <path>. See `nxt publish --help`.");
                Environment.Exit(2);
            }
            if (push is not null && image is null)
            {
                Console.Error.WriteLine("--push requires --image.");
                Environment.Exit(2);
            }

            var projectPath = ResolveProject(project);
            Console.WriteLine($"→ project: {projectPath}");

            // Every mode wants a fresh `dotnet publish` first. Do it once and share.
            var publishDir = Path.Combine(Path.GetDirectoryName(projectPath)!, "publish");
            if (Directory.Exists(publishDir)) Directory.Delete(publishDir, recursive: true);
            Console.WriteLine($"→ dotnet publish -c {config} -o {publishDir}");
            var pubExit = await ProcessRunner.RunAsync("dotnet",
                new[] { "publish", projectPath, "-c", config, "-o", publishDir, "--nologo", "-v", "quiet" });
            if (pubExit != 0) { Console.Error.WriteLine($"  ✗ dotnet publish failed (exit {pubExit})"); Environment.Exit(pubExit); }

            int exit = 0;
            if (staticDir is not null) exit = await ExportStaticAsync(projectPath, publishDir, staticDir, basePath);
            if (exit == 0 && image is not null) exit = await BuildImageAsync(projectPath, image, push, dockerContext);
            if (exit == 0 && bundle is not null) exit = await BundleAsync(publishDir, bundle);
            Environment.Exit(exit);
        }, projectOpt, configOpt, staticOpt, basePathOpt, imageOpt, pushOpt, contextOpt, bundleOpt);

        return cmd;
    }

    // ─── --static ─────────────────────────────────────────────────────────

    private static async Task<int> ExportStaticAsync(string projectPath, string publishDir, string outDir, string? basePath)
    {
        Console.WriteLine();
        Console.WriteLine($"→ static export → {outDir}");
        if (!string.IsNullOrEmpty(basePath))
            Console.WriteLine($"  base path:    {NormalizeBasePath(basePath)}");
        var absOut = Path.GetFullPath(outDir);
        if (Directory.Exists(absOut)) Directory.Delete(absOut, recursive: true);
        Directory.CreateDirectory(absOut);

        // 1. Copy wwwroot so static assets ship alongside the rendered HTML.
        var pubWwwroot = Path.Combine(publishDir, "wwwroot");
        if (Directory.Exists(pubWwwroot))
        {
            CopyDirectory(pubWwwroot, absOut);
            Console.WriteLine($"  ✓ copied wwwroot/ ({DirectorySize(pubWwwroot)})");
        }

        // 2. Boot the published app on a random local port.
        var dll = FindEntryDll(publishDir, projectPath);
        var port = PickFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        Console.WriteLine($"  → booting {Path.GetFileName(dll)} on {baseUrl} for the crawl");

        var psi = new ProcessStartInfo("dotnet", new[] { dll })
        {
            UseShellExecute = false,
            WorkingDirectory = publishDir,
            Environment =
            {
                ["ASPNETCORE_URLS"] = baseUrl,
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
            },
        };
        using var app = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start published app.");
        try
        {
            // Wait for the app to bind.
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!await CanConnectAsync(port, ready.Token))
            {
                if (app.HasExited) { Console.Error.WriteLine($"  ✗ app exited (code {app.ExitCode})"); return 1; }
                await Task.Delay(200, ready.Token);
            }

            // 3. BFS crawl from /.
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue("/");

            var saved = 0;
            var failed = 0;
            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                if (!visited.Add(path)) continue;

                HttpResponseMessage resp;
                try { resp = await http.GetAsync(path); }
                catch (Exception ex) { Console.Error.WriteLine($"  ! {path} — {ex.Message}"); failed++; continue; }

                if ((int)resp.StatusCode >= 400)
                {
                    Console.Error.WriteLine($"  ! {(int)resp.StatusCode} {path}");
                    failed++;
                    continue;
                }
                var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (!ct.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    // Static asset (already copied from wwwroot, presumably); skip.
                    continue;
                }
                var html = await resp.Content.ReadAsStringAsync();

                var save = path == "/"
                    ? Path.Combine(absOut, "index.html")
                    : Path.Combine(absOut, path.Trim('/').Replace('/', Path.DirectorySeparatorChar), "index.html");
                Directory.CreateDirectory(Path.GetDirectoryName(save)!);

                // Extract links from the ORIGINAL HTML so the crawler keeps using "/docs/foo"
                // (which matches our routes), then rewrite for output if a base path is set.
                var nextLinks = ExtractLinks(html).ToList();
                var outputHtml = string.IsNullOrEmpty(basePath) ? html : RewriteBasePath(html, basePath);
                await File.WriteAllTextAsync(save, outputHtml);
                saved++;
                Console.WriteLine($"  ✓ {path}");

                foreach (var href in nextLinks)
                    if (!visited.Contains(href)) queue.Enqueue(href);
            }

            // GitHub Pages skips files starting with _ unless .nojekyll is present.
            // Drop it whenever someone exports with a base path (the typical Pages case),
            // and also whenever we copied a wwwroot/ that contains _framework/.
            if (Directory.Exists(Path.Combine(absOut, "_framework")) || !string.IsNullOrEmpty(basePath))
            {
                await File.WriteAllTextAsync(Path.Combine(absOut, ".nojekyll"), "");
            }

            Console.WriteLine();
            Console.WriteLine($"✓ exported {saved} page(s){(failed > 0 ? $", {failed} failed" : "")} → {absOut}");
            return failed > 0 ? 1 : 0;
        }
        finally
        {
            try { app.Kill(entireProcessTree: true); app.WaitForExit(5000); } catch { }
        }
    }

    /// <summary>
    /// Rewrites absolute internal URLs (those starting with a single <c>/</c>) to be prefixed
    /// with <paramref name="basePath"/>. Skips protocol-relative URLs (<c>//host/...</c>) and
    /// URLs that already start with the prefix. Touches <c>href</c>, <c>src</c>, <c>action</c>,
    /// and <c>formaction</c> attributes only — not arbitrary text.
    /// </summary>
    private static string RewriteBasePath(string html, string basePath)
    {
        var prefix = NormalizeBasePath(basePath).TrimEnd('/');
        if (string.IsNullOrEmpty(prefix)) return html;

        return Regex.Replace(html,
            @"(\s(?:href|src|action|formaction)\s*=\s*)([""'])(/[^""']*)([""'])",
            m =>
            {
                var url = m.Groups[3].Value;
                if (url.StartsWith("//")) return m.Value;                 // protocol-relative
                if (url.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                    return m.Value;                                        // already prefixed
                return m.Groups[1].Value + m.Groups[2].Value + prefix + url + m.Groups[4].Value;
            },
            RegexOptions.IgnoreCase);
    }

    private static string NormalizeBasePath(string basePath) => "/" + basePath.Trim('/') + "/";

    private static IEnumerable<string> ExtractLinks(string html)
    {
        foreach (Match m in Regex.Matches(html, @"<a\s+[^>]*href\s*=\s*[""']([^""']+)[""']",
                                          RegexOptions.IgnoreCase))
        {
            var href = m.Groups[1].Value;
            if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (href.StartsWith("//") || href.StartsWith("#") || href.StartsWith("mailto:") ||
                href.StartsWith("tel:") || href.StartsWith("javascript:")) continue;
            href = href.Split('?')[0].Split('#')[0];
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!href.StartsWith("/")) continue; // skip relative — too ambiguous for a crawler
            yield return href;
        }
    }

    // ─── --image ──────────────────────────────────────────────────────────

    private static async Task<int> BuildImageAsync(string projectPath, string image, string? push, string? contextOverride)
    {
        Console.WriteLine();
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var dockerfile = Path.Combine(projectDir, "Dockerfile");
        if (!File.Exists(dockerfile))
        {
            var assemblyName = ReadAssemblyName(projectPath);
            await File.WriteAllTextAsync(dockerfile, GenerateDockerfile(assemblyName, projectPath, contextOverride));
            Console.WriteLine($"→ generated Dockerfile (entry = {assemblyName}.dll)");
        }
        else
        {
            Console.WriteLine("→ using existing Dockerfile");
        }

        var context = contextOverride is not null
            ? Path.GetFullPath(contextOverride)
            : projectDir;
        var args = contextOverride is not null
            ? new[] { "build", "-t", image, "-f", dockerfile, context }
            : new[] { "build", "-t", image, projectDir };
        Console.WriteLine($"→ docker build -t {image} {context}");
        var buildExit = await ProcessRunner.RunAsync("docker", args);
        if (buildExit != 0) return buildExit;

        if (push is not null)
        {
            if (!string.Equals(push, image, StringComparison.Ordinal))
            {
                Console.WriteLine($"→ docker tag {image} {push}");
                var tag = await ProcessRunner.RunAsync("docker", new[] { "tag", image, push });
                if (tag != 0) return tag;
            }
            Console.WriteLine($"→ docker push {push}");
            return await ProcessRunner.RunAsync("docker", new[] { "push", push });
        }
        return 0;
    }

    private static string GenerateDockerfile(string assemblyName, string projectPath, string? contextOverride)
    {
        // When the build context is the repo root (typical when the csproj has ProjectReferences
        // to siblings), the inner `dotnet publish` needs the path to THIS csproj relative to
        // that context. Otherwise we publish "." which would discover all the projects.
        var publishTarget = ".";
        if (contextOverride is not null)
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(contextOverride), projectPath).Replace('\\', '/');
            publishTarget = rel;
        }
        return $"""
            # syntax=docker/dockerfile:1
            #
            # Generated by `nxt publish --image`. Tweak freely — `nxt publish` only writes this
            # file when one doesn't already exist.
            FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
            WORKDIR /src
            COPY . .
            ARG CONFIG=Release
            RUN dotnet publish {publishTarget} -c $CONFIG -o /app --nologo -v quiet

            FROM mcr.microsoft.com/dotnet/aspnet:10.0
            WORKDIR /app
            COPY --from=build /app ./
            ENV ASPNETCORE_URLS=http://0.0.0.0:8080
            ENV ASPNETCORE_ENVIRONMENT=Production
            EXPOSE 8080
            ENTRYPOINT ["dotnet", "{assemblyName}.dll"]
            """;
    }

    // ─── --bundle ─────────────────────────────────────────────────────────

    private static async Task<int> BundleAsync(string publishDir, string bundlePath)
    {
        Console.WriteLine();
        var abs = Path.GetFullPath(bundlePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        // Use tar's auto-compression by extension (.tar / .tar.gz / .tar.bz2 / .tar.xz).
        var flags = abs.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                    abs.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)    ? "czf" :
                    abs.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) ? "cjf" :
                    abs.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)  ? "cJf" : "cf";

        var parent = Path.GetDirectoryName(publishDir)!;
        var folder = Path.GetFileName(publishDir);
        Console.WriteLine($"→ tar {flags} {abs} (from {publishDir})");
        var exit = await ProcessRunner.RunAsync("tar", new[] { flags, abs, "-C", parent, folder });
        if (exit != 0) return exit;
        var size = new FileInfo(abs).Length;
        Console.WriteLine($"✓ bundle: {abs} ({size / 1024} KB)");
        return 0;
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private static string ResolveProject(string? project)
    {
        if (!string.IsNullOrEmpty(project)) return Path.GetFullPath(project);
        var csprojs = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj");
        if (csprojs.Length == 1) return csprojs[0];
        if (csprojs.Length == 0) throw new InvalidOperationException("No .csproj found in current directory — pass --project.");
        throw new InvalidOperationException($"{csprojs.Length} .csproj files found — pass --project to disambiguate.");
    }

    private static string ReadAssemblyName(string projectPath)
    {
        var xml = File.ReadAllText(projectPath);
        var match = Regex.Match(xml, @"<AssemblyName>([^<]+)</AssemblyName>");
        return match.Success
            ? match.Groups[1].Value.Trim()
            : Path.GetFileNameWithoutExtension(projectPath);
    }

    private static string FindEntryDll(string publishDir, string projectPath)
    {
        var name = ReadAssemblyName(projectPath);
        var candidate = Path.Combine(publishDir, $"{name}.dll");
        if (File.Exists(candidate)) return candidate;
        // Fallback: pick the first .dll with a sibling runtimeconfig.json (the entry assembly).
        var fallback = Directory.GetFiles(publishDir, "*.dll")
            .FirstOrDefault(d => File.Exists(Path.ChangeExtension(d, ".runtimeconfig.json")));
        return fallback ?? throw new InvalidOperationException(
            $"Could not locate entry assembly in {publishDir}.");
    }

    private static int PickFreePort()
    {
        using var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<bool> CanConnectAsync(int port, CancellationToken ct)
    {
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var connect = sock.ConnectAsync("127.0.0.1", port);
            var done = await Task.WhenAny(connect, Task.Delay(500, ct));
            return done == connect && sock.Connected;
        }
        catch { return false; }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string DirectorySize(string dir)
    {
        var bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        return bytes < 1024 * 1024
            ? $"{bytes / 1024} KB"
            : $"{bytes / (1024.0 * 1024):F1} MB";
    }
}
