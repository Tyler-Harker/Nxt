using System.CommandLine;

namespace Nxt.Cli.Commands;

/// <summary>
/// <c>nxt dev</c> — runs <c>dotnet watch run</c> on the current project for
/// hot-reload during development. The runtime is the standard ASP.NET pipeline,
/// so file changes to .razor / .cshtml / .cs all hot-reload by default.
/// </summary>
internal static class DevCommand
{
    public static Command Build()
    {
        var projectOpt = new Option<string?>("--project", "Project file to run. Defaults to the current directory.");
        var portOpt = new Option<int?>(new[] { "--port", "-p" }, "Port to bind on localhost (default 5000).");
        var urlsOpt = new Option<string?>("--urls", "Full URL(s) to bind, e.g. \"http://0.0.0.0:5099;https://0.0.0.0:7099\". Overrides --port.");
        var cmd = new Command("dev", "Run the Nxt app with hot reload.") { projectOpt, portOpt, urlsOpt };
        cmd.SetHandler(async (string? project, int? port, string? urls) =>
        {
            var args = new List<string> { "watch", "run", "--non-interactive" };
            if (!string.IsNullOrEmpty(project)) { args.Add("--project"); args.Add(project); }
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", ResolveUrls(port, urls));
            var exit = await ProcessRunner.RunAsync("dotnet", args);
            Environment.Exit(exit);
        }, projectOpt, portOpt, urlsOpt);
        return cmd;
    }

    internal static string? ResolveUrls(int? port, string? urls)
    {
        if (!string.IsNullOrWhiteSpace(urls)) return urls;
        if (port is int p) return $"http://localhost:{p}";
        return null;
    }
}
