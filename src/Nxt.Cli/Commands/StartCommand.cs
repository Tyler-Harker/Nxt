using System.CommandLine;

namespace Nxt.Cli.Commands;

/// <summary>
/// <c>nxt start</c> — runs the production build. Equivalent to <c>dotnet run -c Release --no-build</c>.
/// </summary>
internal static class StartCommand
{
    public static Command Build()
    {
        var configOpt = new Option<string>("--configuration", () => "Release", "Build configuration.");
        var portOpt = new Option<int?>(new[] { "--port", "-p" }, "Port to bind on localhost (default 5000).");
        var urlsOpt = new Option<string?>("--urls", "Full URL(s) to bind. Overrides --port.");
        var cmd = new Command("start", "Run the published Nxt app in production mode.")
        { configOpt, portOpt, urlsOpt };
        cmd.SetHandler(async (string config, int? port, string? urls) =>
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", DevCommand.ResolveUrls(port, urls));
            var exit = await ProcessRunner.RunAsync("dotnet", new[] { "run", "-c", config });
            Environment.Exit(exit);
        }, configOpt, portOpt, urlsOpt);
        return cmd;
    }
}
